using MAIC.Application.Conversations;
using MAIC.Domain.Conversations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

public sealed partial class ConversationStore
{
    public Task<ConversationResult> AssignAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        Guid? assignedUserId, CancellationToken cancellationToken)
    {
        if (assignedUserId == Guid.Empty) return Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid));
        return WriteWorkflowAsync(actorId, workspaceId, conversationId, assignedUserId, async conversation =>
        {
            await CancelPendingAsync(conversation, actorId, HandoffTerminalReason.MANUAL_ASSIGNMENT, cancellationToken);
            AddAssignment(conversation.Assign(assignedUserId, actorId, AssignmentReason.MANUAL));
            return new(ConversationResultStatus.Success, Conversation: View(conversation));
        }, cancellationToken);
    }

    public Task<ConversationResult> RequestHandoffAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        Guid clientRequestId, CancellationToken cancellationToken)
    {
        if (clientRequestId == Guid.Empty) return Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid));
        return WriteWorkflowAsync(actorId, workspaceId, conversationId, null, async conversation =>
        {
            // Current membership and conversation scope have already been checked, including for terminal replays.
            var existing = await db.HandoffRequests.AsNoTracking().SingleOrDefaultAsync(h => h.WorkspaceId == workspaceId
                && h.ConversationId == conversationId && h.RequesterUserId == actorId && h.ClientRequestId == clientRequestId,
                cancellationToken);
            if (existing is not null) return new(ConversationResultStatus.Success, Handoff: View(existing), Replayed: true);
            if (conversation.Status != ConversationState.OPEN || await db.HandoffRequests.AnyAsync(h => h.WorkspaceId == workspaceId
                && h.ConversationId == conversationId && h.Status == HandoffState.PENDING, cancellationToken))
                return new(ConversationResultStatus.Conflict);
            var handoff = conversation.RequestHandoff(actorId, clientRequestId);
            db.HandoffRequests.Add(handoff);
            return new(ConversationResultStatus.Success, Handoff: View(handoff));
        }, cancellationToken);
    }

    public Task<ConversationResult> AcceptHandoffAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        Guid handoffId, CancellationToken cancellationToken) =>
        WriteWorkflowAsync(actorId, workspaceId, conversationId, null, async conversation =>
        {
            var handoff = await db.HandoffRequests.SingleOrDefaultAsync(h => h.WorkspaceId == workspaceId
                && h.ConversationId == conversationId && h.Id == handoffId, cancellationToken);
            if (handoff is null) return new(ConversationResultStatus.NotFound);
            await db.Entry(handoff).ReloadAsync(cancellationToken);
            if (handoff.Status == HandoffState.ACCEPTED && handoff.TerminalActorUserId == actorId)
                return new(ConversationResultStatus.Success, Handoff: View(handoff), Replayed: true);
            if (handoff.Status != HandoffState.PENDING || conversation.Status != ConversationState.OPEN)
                return new(ConversationResultStatus.Conflict);
            handoff.Complete(actorId, HandoffTerminalReason.ACCEPTED);
            AddAssignment(conversation.Assign(actorId, actorId, AssignmentReason.HANDOFF_ACCEPTED));
            conversation.SetMode(Conversation.ModeHumanActive);
            conversation.ClearUnread();
            return new(ConversationResultStatus.Success, Handoff: View(handoff));
        }, cancellationToken);

    public Task<ConversationResult> ResolveAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken) =>
        WriteWorkflowAsync(actorId, workspaceId, conversationId, null, async conversation =>
        {
            conversation.Resolve();
            await CancelPendingAsync(conversation, actorId, HandoffTerminalReason.RESOLVED, cancellationToken);
            return new(ConversationResultStatus.Success, Conversation: View(conversation));
        }, cancellationToken);

    public Task<ConversationResult> ReopenAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken) =>
        WriteWorkflowAsync(actorId, workspaceId, conversationId, null, conversation =>
        {
            conversation.Reopen();
            return Task.FromResult(new ConversationResult(ConversationResultStatus.Success, Conversation: View(conversation)));
        }, cancellationToken);

    public Task<ConversationResult> ArchiveAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken) =>
        WriteWorkflowAsync(actorId, workspaceId, conversationId, null, async conversation =>
        {
            conversation.Archive();
            await CancelPendingAsync(conversation, actorId, HandoffTerminalReason.RESOLVED, cancellationToken);
            return new(ConversationResultStatus.Success, Conversation: View(conversation));
        }, cancellationToken);

    public Task<ConversationResult> UnarchiveAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken) =>
        WriteWorkflowAsync(actorId, workspaceId, conversationId, null, conversation =>
        {
            conversation.Unarchive();
            return Task.FromResult(new ConversationResult(ConversationResultStatus.Success, Conversation: View(conversation)));
        }, cancellationToken);

    public Task<ConversationResult> TakeoverAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken) =>
        WriteWorkflowAsync(actorId, workspaceId, conversationId, actorId, async conversation =>
        {
            if (conversation.Status == ConversationState.ARCHIVED)
            {
                conversation.Unarchive();
            }
            else if (conversation.Status == ConversationState.RESOLVED)
            {
                conversation.Reopen();
            }

            await CompletePendingAsAcceptedAsync(conversation, actorId, cancellationToken);
            AddAssignment(conversation.Takeover(actorId));
            return new(ConversationResultStatus.Success, Conversation: View(conversation));
        }, cancellationToken);

    public Task<ConversationResult> HandbackToAiAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken) =>
        WriteWorkflowAsync(actorId, workspaceId, conversationId, null, async conversation =>
        {
            await CancelPendingAsync(conversation, actorId, HandoffTerminalReason.MANUAL_ASSIGNMENT, cancellationToken);
            AddAssignment(conversation.HandbackToAi(actorId));
            return new(ConversationResultStatus.Success, Conversation: View(conversation));
        }, cancellationToken);

    private async Task<ConversationResult> WriteWorkflowAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        Guid? targetId, Func<Conversation, Task<ConversationResult>> action, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await AuthorizeWriteAsync(actorId, workspaceId, ct, targetId)) return new(ConversationResultStatus.NotFound);
        var conversation = (await db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM "Conversations" WHERE "WorkspaceId" = {workspaceId} AND "Id" = {conversationId} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();
        if (conversation is null) return new(ConversationResultStatus.NotFound);
        await db.Entry(conversation).ReloadAsync(ct);
        try
        {
            var result = await action(conversation);
            if (result.Status != ConversationResultStatus.Success) return result;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            DiscardWorkflowChanges(workspaceId, conversationId);
            return new(ConversationResultStatus.Conflict);
        }
        catch
        {
            DiscardWorkflowChanges(workspaceId, conversationId);
            throw;
        }
    }

    private void DiscardWorkflowChanges(Guid workspaceId, Guid conversationId)
    {
        // A rollback must not leave a future SaveChanges able to persist the rejected transition.
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity switch
        {
            Conversation c => c.WorkspaceId == workspaceId && c.Id == conversationId,
            ConversationAssignment a => a.WorkspaceId == workspaceId && a.ConversationId == conversationId,
            HandoffRequest h => h.WorkspaceId == workspaceId && h.ConversationId == conversationId,
            _ => false
        }).ToArray()) entry.State = EntityState.Detached;
    }

    private void AddAssignment(ConversationAssignment? assignment)
    {
        if (assignment is not null) db.ConversationAssignments.Add(assignment);
    }

    private async Task CancelPendingAsync(Conversation conversation, Guid actorId, HandoffTerminalReason reason, CancellationToken ct)
    {
        var pending = await db.HandoffRequests.SingleOrDefaultAsync(h => h.WorkspaceId == conversation.WorkspaceId
            && h.ConversationId == conversation.Id && h.Status == HandoffState.PENDING, ct);
        if (pending is null) return;
        await db.Entry(pending).ReloadAsync(ct);
        pending.Complete(actorId, reason);
    }

    private async Task CompletePendingAsAcceptedAsync(Conversation conversation, Guid actorId, CancellationToken ct)
    {
        var pending = await db.HandoffRequests.SingleOrDefaultAsync(h => h.WorkspaceId == conversation.WorkspaceId
            && h.ConversationId == conversation.Id && h.Status == HandoffState.PENDING, ct);
        if (pending is null) return;
        await db.Entry(pending).ReloadAsync(ct);
        pending.Complete(actorId, HandoffTerminalReason.ACCEPTED);
    }

    public async Task<ConversationResult> ListAssignmentsAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        long afterSequence, int take, CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100 || afterSequence < 0) return new(ConversationResultStatus.Invalid);
        var result = await db.Conversations.AsNoTracking().AsSingleQuery()
            .Where(c => c.WorkspaceId == workspaceId && c.Id == conversationId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == actorId))
            .Select(c => new
            {
                Items = db.ConversationAssignments.Where(a => a.WorkspaceId == workspaceId && a.WorkspaceId == c.WorkspaceId
                    && a.ConversationId == c.Id && a.Sequence > afterSequence).OrderBy(a => a.Sequence).Take(take + 1)
                    .Select(a => new AssignmentView(a.Id, a.WorkspaceId, a.ConversationId, a.Sequence,
                        a.PreviousAssignedUserId, a.AssignedUserId, a.ActorUserId, a.Reason.ToString(), a.CreatedAt)).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        if (result is null) return new(ConversationResultStatus.NotFound);
        return new(ConversationResultStatus.Success, Assignments: result.Items.Take(take).ToArray(),
            NextAfterSequence: result.Items.Count > take ? result.Items[take - 1].Sequence : null);
    }

    public async Task<ConversationResult> ListHandoffsAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        long afterSequence, int take, CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100 || afterSequence < 0) return new(ConversationResultStatus.Invalid);
        var result = await db.Conversations.AsNoTracking().AsSingleQuery()
            .Where(c => c.WorkspaceId == workspaceId && c.Id == conversationId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == actorId))
            .Select(c => new
            {
                Items = db.HandoffRequests.Where(h => h.WorkspaceId == workspaceId && h.WorkspaceId == c.WorkspaceId
                    && h.ConversationId == c.Id && h.Sequence > afterSequence).OrderBy(h => h.Sequence).Take(take + 1)
                    .Select(h => new HandoffView(h.Id, h.WorkspaceId, h.ConversationId, h.Sequence, h.RequesterUserId,
                        h.ClientRequestId, h.Status.ToString(), h.CreatedAt, h.TerminalActorUserId, h.TerminalAt,
                        h.TerminalReason == null ? null : h.TerminalReason.ToString(), h.AiExecutionId)).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        if (result is null) return new(ConversationResultStatus.NotFound);
        return new(ConversationResultStatus.Success, Handoffs: result.Items.Take(take).ToArray(),
            NextAfterSequence: result.Items.Count > take ? result.Items[take - 1].Sequence : null);
    }

    private static HandoffView View(HandoffRequest h) => new(h.Id, h.WorkspaceId, h.ConversationId, h.Sequence,
        h.RequesterUserId, h.ClientRequestId, h.Status.ToString(), h.CreatedAt, h.TerminalActorUserId,
        h.TerminalAt, h.TerminalReason?.ToString(), h.AiExecutionId);
}
