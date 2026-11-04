using System.Text.Json;
using MAIC.Application.AI;
using MAIC.Application.Conversations;
using MAIC.Domain.AI;
using MAIC.Domain.Channels;
using MAIC.Domain.Conversations;
using MAIC.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

/// <summary>Short transactions own claims/admission and publication; external providers never run under these locks.</summary>
public sealed class ConversationAiExecutionStore(MaicDbContext db) : IConversationAiExecutionStore
{
    public async Task<AiExecutionBeginResult> BeginAsync(Guid actorId, AiExecutionRequest request, AiExecutionSettings settings, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await AuthorizeAsync(actorId, request.WorkspaceId, ct)) return new(new(AiExecutionStatus.NotFound));
        var conversation = await LockConversationAsync(request.WorkspaceId, request.ConversationId, ct);
        if (conversation is null) return new(new(AiExecutionStatus.NotFound));
        var existing = await db.AiExecutions.SingleOrDefaultAsync(e => e.WorkspaceId == request.WorkspaceId
            && e.ConversationId == request.ConversationId && e.TriggerMessageId == request.TriggerMessageId, ct);
        var now = await DatabaseNowAsync(ct);
        if (existing is not null)
        {
            await db.Entry(existing).ReloadAsync(ct);
            if (existing.State == AiExecutionState.IN_PROGRESS && existing.ExpiresAt <= now)
            {
                existing.Complete(nameof(AiExecutionOutcome.UNAVAILABLE), nameof(AiExecutionFailure.EXPIRED), now);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            return new(await ResultAsync(existing, true, ct));
        }
        if (!await EligibleAsync(conversation, ct)) return new(new(AiExecutionStatus.Conflict));
        if (conversation.AiAgentId is not { } agentId) return new(new(AiExecutionStatus.Conflict, Failure: AiExecutionFailure.NO_BINDING));
        var agent = await LoadSettingsAsync(request.WorkspaceId, agentId, ct);
        if (agent is null) return new(new(AiExecutionStatus.Conflict, Failure: AiExecutionFailure.NO_BINDING));
        if (!agent.Enabled) return new(new(AiExecutionStatus.Conflict, Failure: AiExecutionFailure.AGENT_DISABLED));
        var trigger = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.WorkspaceId == request.WorkspaceId
            && m.ConversationId == request.ConversationId && m.Id == request.TriggerMessageId, ct);
        if (trigger is null) return new(new(AiExecutionStatus.NotFound));
        if (trigger.SenderKind is not (MessageSenderKind.MEMBER or MessageSenderKind.GUEST) || trigger.Sequence != conversation.LastMessageSequence)
            return new(new(AiExecutionStatus.Conflict));
        // Database-wide admission lock serializes short counters across API hosts. Always acquired after tenant locks.
        await AdmissionLockAsync(ct);
        now = await DatabaseNowAsync(ct);
        var minute = now.AddMinutes(-1);
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        if (await db.AiExecutions.CountAsync(e => e.CreatedAt > minute, ct) >= settings.MaxExecutionsPerMinute
            || await db.AiExecutions.CountAsync(e => e.WorkspaceId == request.WorkspaceId && e.CreatedAt > minute, ct) >= settings.ExecutionsPerMinutePerWorkspace)
            return new(new(AiExecutionStatus.Unavailable, Failure: AiExecutionFailure.RATE_LIMITED));
        if (await db.AiExecutions.CountAsync(e => e.WorkspaceId == request.WorkspaceId && e.CreatedAt >= day, ct) >= settings.MaxExecutionsPerWorkspacePerDay)
            return new(new(AiExecutionStatus.Unavailable, Failure: AiExecutionFailure.QUOTA_EXCEEDED));
        var history = await db.Messages.AsNoTracking().Where(m => m.WorkspaceId == request.WorkspaceId && m.ConversationId == request.ConversationId)
            .OrderByDescending(m => m.Sequence).Take(settings.MaxHistoryMessages)
            .Select(m => new AiExecutionHistoryMessage(m.Id, m.Sequence, m.SenderKind.ToString(), m.Content)).ToListAsync(ct);
        var bounded = new List<AiExecutionHistoryMessage>();
        var characters = 0;
        foreach (var item in history)
        {
            if (characters + item.Content.Length > settings.MaxHistoryCharacters) break;
            bounded.Add(item); characters += item.Content.Length;
        }
        bounded.Reverse();
        var count = await db.Messages.CountAsync(m => m.WorkspaceId == request.WorkspaceId
            && m.ConversationId == request.ConversationId && m.SenderKind == MessageSenderKind.AI, ct);
        var execution = new AiExecution(request.WorkspaceId, request.ConversationId, request.TriggerMessageId, actorId,
            agentId, Serialize(agent), conversation.AiBindingRevision, conversation.LastMessageSequence,
            conversation.LastAssignmentSequence, conversation.LastHandoffSequence, now, now.AddSeconds(settings.ExecutionTimeoutSeconds));
        db.AiExecutions.Add(execution);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var snapshot = new AiExecutionSnapshot(agentId, agent, conversation.AiBindingRevision, conversation.LastMessageSequence,
            conversation.LastAssignmentSequence, conversation.LastHandoffSequence, count, bounded);
        return new(new(AiExecutionStatus.Success, execution.Id), new(execution.Id, execution.LeaseToken, actorId,
            request.WorkspaceId, request.ConversationId, request.TriggerMessageId, execution.ExpiresAt, snapshot));
    }

    public async Task<AiGenerationReservation> ReserveGenerationAsync(AiExecutionClaim claim, decimal maximumCost, AiExecutionSettings settings, CancellationToken ct)
    {
        if (maximumCost < 0) return new(false, Failure: AiExecutionFailure.QUOTA_EXCEEDED);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await AuthorizeAsync(claim.ActorId, claim.WorkspaceId, ct)) return new(false, Failure: AiExecutionFailure.STALE_CONTEXT);
        var conversation = await LockConversationAsync(claim.WorkspaceId, claim.ConversationId, ct);
        var execution = await LockExecutionAsync(claim, ct);
        var now = await DatabaseNowAsync(ct);
        if (execution is null || !execution.IsLive(claim.LeaseToken, now) || execution.GenerationReservedAt is not null
            || !await CurrentAsync(conversation, execution, claim, ct)) return new(false, Failure: AiExecutionFailure.STALE_CONTEXT);
        await AdmissionLockAsync(ct);
        now = await DatabaseNowAsync(ct);
        if (!execution.IsLive(claim.LeaseToken, now)) return new(false, Failure: AiExecutionFailure.EXPIRED);
        var minute = now.AddMinutes(-1);
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        if (await db.AiExecutions.CountAsync(e => e.GenerationReservedAt > minute, ct) >= settings.MaxGenerationsPerMinute
            || await db.AiExecutions.CountAsync(e => e.WorkspaceId == claim.WorkspaceId && e.GenerationReservedAt > minute, ct) >= settings.GenerationsPerMinutePerWorkspace)
            return new(false, Failure: AiExecutionFailure.RATE_LIMITED);
        var charged = await db.AiExecutions.Where(e => e.WorkspaceId == claim.WorkspaceId && e.GenerationReservedAt >= day)
            .SumAsync(e => e.ActualCost != null && e.ActualCost > e.ReservedCost ? e.ActualCost.Value : e.ReservedCost, ct);
        if (charged + maximumCost > settings.MaxCostPerWorkspacePerDay) return new(false, Failure: AiExecutionFailure.QUOTA_EXCEEDED);
        execution.Reserve(maximumCost, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(true, maximumCost);
    }

    public async Task<AiExecutionResult> CommitAsync(AiExecutionClaim claim, AiExecutionDecision decision, AiExecutionUsage usage, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var authorized = await AuthorizeAsync(claim.ActorId, claim.WorkspaceId, ct);
        var conversation = await LockConversationAsync(claim.WorkspaceId, claim.ConversationId, ct);
        var execution = await LockExecutionAsync(claim, ct);
        if (execution is null) return new(AiExecutionStatus.NotFound);
        if (execution.State == AiExecutionState.TERMINAL)
        {
            if (ReconcileUsage(execution, usage))
            {
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            return authorized ? await ResultAsync(execution, true, ct) : new(AiExecutionStatus.NotFound);
        }
        var current = authorized && await CurrentAsync(conversation, execution, claim, ct)
            && await PassagesCurrentAsync(claim, decision.Passages, ct);
        // A passage/source lock may have waited past the lease. Check database time only after all rechecks/locks.
        var now = await DatabaseNowAsync(ct);
        if (!current || !execution.IsLive(claim.LeaseToken, now))
        {
            Complete(execution, AiExecutionOutcome.SUPERSEDED, AiExecutionFailure.STALE_CONTEXT, usage, now);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return authorized ? await ResultAsync(execution, false, ct) : new(AiExecutionStatus.NotFound);
        }
        if (decision.Answer is not null)
        {
            if (!Message.IsValidContent(decision.Answer)) throw new InvalidOperationException("Invalid answer at persistence boundary.");
            db.Messages.Add(Message.CreateAi(claim.WorkspaceId, claim.ConversationId, execution.Id,
                conversation!.AdvanceMessageSequence(), decision.Answer));
        }
        if (decision.Handoff) db.HandoffRequests.Add(conversation!.RequestHandoff(claim.ActorId, execution.Id, execution.Id));
        execution.Complete(decision.Outcome.ToString(), decision.Failure?.ToString(), now, usage.ProviderAttempted,
            usage.InputTokens, usage.OutputTokens, usage.ActualCost, decision.RetrievalOutcome?.ToString(), decision.RetrievalReason?.ToString(),
            JsonSerializer.Serialize(decision.Citations));
        try
        {
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        catch
        {
            // The scoped context may be reused by cleanup; rejected publication must never be saved later.
            db.ChangeTracker.Clear();
            throw;
        }
        return await ResultAsync(execution, false, ct);
    }

    public async Task<AiExecutionResult> FinishAsync(AiExecutionClaim claim, AiExecutionFailure failure, AiExecutionUsage usage, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var authorized = await AuthorizeAsync(claim.ActorId, claim.WorkspaceId, ct);
        await LockConversationAsync(claim.WorkspaceId, claim.ConversationId, ct);
        var execution = await LockExecutionAsync(claim, ct);
        if (execution is null) return new(AiExecutionStatus.NotFound);
        if (execution.State == AiExecutionState.IN_PROGRESS)
        {
            Complete(execution, failure == AiExecutionFailure.CANCELLED ? AiExecutionOutcome.CANCELLED : AiExecutionOutcome.UNAVAILABLE,
                failure, usage, await DatabaseNowAsync(ct));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        else if (ReconcileUsage(execution, usage))
        {
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        return authorized ? await ResultAsync(execution, false, ct) : new(AiExecutionStatus.NotFound);
    }

    private static bool ReconcileUsage(AiExecution execution, AiExecutionUsage usage) =>
        usage.ProviderAttempted && usage.InputTokens is { } input && usage.OutputTokens is { } output
        && usage.ActualCost is { } cost && execution.ReconcileUsage(input, output, cost);

    private static void Complete(AiExecution execution, AiExecutionOutcome outcome, AiExecutionFailure failure, AiExecutionUsage usage, DateTimeOffset now) =>
        execution.Complete(outcome.ToString(), failure.ToString(), now, usage.ProviderAttempted, usage.InputTokens, usage.OutputTokens, usage.ActualCost);

    private async Task<bool> AuthorizeAsync(Guid actorId, Guid workspaceId, CancellationToken ct)
    {
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        return (await db.WorkspaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "WorkspaceMembers" WHERE "WorkspaceId" = {workspaceId} AND "UserId" = {actorId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct)).Count == 1;
    }

    private async Task<Conversation?> LockConversationAsync(Guid workspaceId, Guid conversationId, CancellationToken ct)
    {
        var c = (await db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM "Conversations" WHERE "WorkspaceId" = {workspaceId} AND "Id" = {conversationId} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();
        if (c is not null) await db.Entry(c).ReloadAsync(ct);
        return c;
    }

    private async Task<AiExecution?> LockExecutionAsync(AiExecutionClaim claim, CancellationToken ct)
    {
        var e = (await db.AiExecutions.FromSqlInterpolated($"""
            SELECT * FROM "AiExecutions" WHERE "WorkspaceId" = {claim.WorkspaceId} AND "ConversationId" = {claim.ConversationId}
                AND "Id" = {claim.ExecutionId} AND "LeaseToken" = {claim.LeaseToken} AND "ActorId" = {claim.ActorId}
                AND "TriggerMessageId" = {claim.TriggerMessageId} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();
        if (e is not null) await db.Entry(e).ReloadAsync(ct);
        return e;
    }

    private Task AdmissionLockAsync(CancellationToken ct) => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(714203004)", ct);

    // clock_timestamp() is wall time at this statement, unlike transaction-start now(); all hosts share this authority.
    private Task<DateTimeOffset> DatabaseNowAsync(CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private async Task<bool> EligibleAsync(Conversation c, CancellationToken ct) => c.Status == ConversationState.OPEN
        && c.AssignedUserId is null && await db.Channels.AnyAsync(ch => ch.WorkspaceId == c.WorkspaceId && ch.Id == c.ChannelId && ch.Type == ChannelType.WEB && ch.Enabled, ct)
        && !await db.HandoffRequests.AnyAsync(h => h.WorkspaceId == c.WorkspaceId && h.ConversationId == c.Id && h.Status == HandoffState.PENDING, ct);

    private async Task<bool> CurrentAsync(Conversation? c, AiExecution e, AiExecutionClaim claim, CancellationToken ct)
    {
        if (c is null || c.AiAgentId != e.AgentId || c.AiBindingRevision != e.ConversationRevision
            || c.LastMessageSequence != e.MessageSequence || c.LastAssignmentSequence != e.AssignmentSequence
            || c.LastHandoffSequence != e.HandoffSequence || !await EligibleAsync(c, ct)) return false;
        var settings = await LoadSettingsAsync(e.WorkspaceId, e.AgentId, ct);
        return settings is { Enabled: true } && Serialize(settings) == e.AgentSnapshot && Serialize(claim.Snapshot.Agent) == e.AgentSnapshot
            && await db.Messages.AnyAsync(m => m.WorkspaceId == e.WorkspaceId && m.ConversationId == e.ConversationId
             && m.Id == e.TriggerMessageId && m.Sequence == e.MessageSequence && (m.SenderKind == MessageSenderKind.MEMBER || m.SenderKind == MessageSenderKind.GUEST), ct);
    }

    private async Task<AiAgentSettings?> LoadSettingsAsync(Guid workspaceId, Guid agentId, CancellationToken ct)
    {
        var a = await db.AiAgents.AsNoTracking().Include(a => a.KnowledgeSources)
            .SingleOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.Id == agentId, ct);
        return a is null ? null : new(a.Name, a.Enabled, a.Instructions, a.Language, a.ModelTier, a.KnowledgeScope,
            a.KnowledgeSources.Select(s => s.KnowledgeSourceId).Order().ToArray(), a.HandoffOnRequest, a.HandoffWhenUnanswered, a.MaxAiReplies);
    }

    private static string Serialize(AiAgentSettings settings) => JsonSerializer.Serialize(settings);

    private async Task<bool> PassagesCurrentAsync(AiExecutionClaim claim, IReadOnlyList<RetrievedPassage> passages, CancellationToken ct)
    {
        if (passages.Count == 0) return true;
        if (passages.Count > 20 || passages.Select(p => p.ChunkId).Distinct().Count() != passages.Count) return false;
        var sourceIds = passages.Select(p => p.Citation.KnowledgeSourceId).Distinct().Order().ToArray();
        var documentIds = passages.Select(p => p.Citation.DocumentId).Distinct().Order().ToArray();
        var chunkIds = passages.Select(p => p.ChunkId).Order().ToArray();
        // Ingestion uses source -> document -> chunks. Lock sources first to avoid deadlocks with snapshot replacement.
        var sources = await db.KnowledgeSources.FromSqlInterpolated($"""
            SELECT * FROM "KnowledgeSources" WHERE "WorkspaceId" = {claim.WorkspaceId} AND "Id" = ANY({sourceIds}) ORDER BY "Id" FOR SHARE
            """).AsNoTracking().ToListAsync(ct);
        var documents = await db.KnowledgeDocuments.FromSqlInterpolated($"""
            SELECT * FROM "KnowledgeDocuments" WHERE "WorkspaceId" = {claim.WorkspaceId} AND "Id" = ANY({documentIds}) ORDER BY "Id" FOR SHARE
            """).AsNoTracking().ToListAsync(ct);
        var chunks = await db.KnowledgeDocumentChunks.FromSqlInterpolated($"""
            SELECT * FROM "KnowledgeDocumentChunks" WHERE "WorkspaceId" = {claim.WorkspaceId} AND "Id" = ANY({chunkIds}) ORDER BY "Id" FOR SHARE
            """).AsNoTracking().ToListAsync(ct);
        foreach (var p in passages)
        {
            var source = sources.SingleOrDefault(s => s.Id == p.Citation.KnowledgeSourceId);
            var document = documents.SingleOrDefault(d => d.Id == p.Citation.DocumentId);
            var chunk = chunks.SingleOrDefault(c => c.Id == p.ChunkId);
            if (source is not { Enabled: true } || document is not { Status: KnowledgeDocumentStatus.READY }
                || chunk is null || document.KnowledgeSourceId != source.Id || chunk.DocumentId != document.Id
                || chunk.Content != p.Text || chunk.Sequence != p.Citation.ChunkSequence || source.Name != p.Citation.SourceName
                || document.FileName != p.Citation.FileName || document.SourceUrl != p.Citation.SourceUrl
                || claim.Snapshot.Agent.KnowledgeScope == AiKnowledgeScope.SELECTED_SOURCES && !claim.Snapshot.Agent.KnowledgeSourceIds.Contains(source.Id)) return false;
        }
        return true;
    }

    private async Task<AiExecutionResult> ResultAsync(AiExecution e, bool replayed, CancellationToken ct)
    {
        if (e.State == AiExecutionState.IN_PROGRESS) return new(AiExecutionStatus.Conflict, e.Id, Failure: AiExecutionFailure.BUSY);
        var message = await db.Messages.AsNoTracking().Where(m => m.WorkspaceId == e.WorkspaceId && m.ConversationId == e.ConversationId && m.AiExecutionId == e.Id)
            .Select(m => new MessageView(m.Id, m.WorkspaceId, m.ConversationId, m.SenderUserId, m.ClientMessageId, m.Sequence, m.Content, m.CreatedAt, m.SenderKind.ToString(), m.AiExecutionId)).SingleOrDefaultAsync(ct);
        var handoff = await db.HandoffRequests.AsNoTracking().Where(h => h.WorkspaceId == e.WorkspaceId && h.ConversationId == e.ConversationId && h.AiExecutionId == e.Id)
            .Select(h => new HandoffView(h.Id, h.WorkspaceId, h.ConversationId, h.Sequence, h.RequesterUserId, h.ClientRequestId,
                h.Status.ToString(), h.CreatedAt, h.TerminalActorUserId, h.TerminalAt, h.TerminalReason == null ? null : h.TerminalReason.ToString(), h.AiExecutionId)).SingleOrDefaultAsync(ct);
        var outcome = Enum.Parse<AiExecutionOutcome>(e.Outcome!);
        var status = outcome == AiExecutionOutcome.SUPERSEDED ? AiExecutionStatus.Conflict
            : outcome is AiExecutionOutcome.UNAVAILABLE or AiExecutionOutcome.CANCELLED ? AiExecutionStatus.Unavailable : AiExecutionStatus.Success;
        return new(status, e.Id, outcome, message, handoff, e.Failure is null ? null : Enum.Parse<AiExecutionFailure>(e.Failure), replayed);
    }
}
