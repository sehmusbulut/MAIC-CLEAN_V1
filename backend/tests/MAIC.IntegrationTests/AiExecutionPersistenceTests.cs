using MAIC.Application.AI;
using MAIC.Domain.AI;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class AiExecutionPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AiExecutionSettings Settings = new();

    [Fact]
    public async Task Schema_enforces_tenant_provenance_unique_trigger_and_ai_sender_shape_and_agent_deletion_preserves_history()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(fixture);
        var other = await AiExecutionPersistenceSeed.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        var store = new ConversationAiExecutionStore(db);
        var claim = (await store.BeginAsync(seed.Actor.Id, seed.Request, Settings, Ct)).Claim!;
        var result = await store.CommitAsync(claim, new(AiExecutionOutcome.FALLBACK, "Knowledge unavailable", true, [], []), new(), Ct);
        Assert.Equal(AiExecutionStatus.Success, result.Status);
        var message = result.Message!;
        Assert.Null(message.SenderUserId);
        Assert.Equal(claim.ExecutionId, message.AiExecutionId);
        Assert.Equal(claim.ExecutionId, result.Handoff!.AiExecutionId);
        Assert.Equal(2, message.Sequence);
        await RejectAsync($"UPDATE \"Conversations\" SET \"AiAgentId\" = {other.Agent.Id} WHERE \"Id\" = {seed.Conversation.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"Messages\" SET \"SenderUserId\" = {seed.Actor.Id} WHERE \"Id\" = {message.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"Messages\" SET \"SenderKind\" = {"MEMBER"} WHERE \"Id\" = {message.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"AiExecutions\" SET \"TriggerMessageId\" = {other.Message.Id} WHERE \"Id\" = {claim.ExecutionId}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"AiExecutions\" SET \"ReservedCost\" = {-1m} WHERE \"Id\" = {claim.ExecutionId}", PostgresErrorCodes.CheckViolation);
        var duplicate = new AiExecution(seed.Workspace.Id, seed.Conversation.Id, seed.Message.Id, seed.Actor.Id, seed.Agent.Id,
            "{}", 1, 1, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        await using (var duplicateDb = fixture.CreateContext())
        {
            duplicateDb.AiExecutions.Add(duplicate);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => duplicateDb.SaveChangesAsync(Ct));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        }
        var deletion = await new AiAgentStore(db, TimeProvider.System).DeleteAsync(seed.Actor.Id, seed.Workspace.Id, seed.Agent.Id, [WorkspaceRole.OWNER, WorkspaceRole.ADMIN], Ct);
        Assert.Equal(AiAgentStatus.Success, deletion.Status);
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == seed.Conversation.Id, Ct);
        Assert.Null(conversation.AiAgentId);
        Assert.Equal(2, conversation.AiBindingRevision);
        Assert.Equal(seed.Agent.Id, (await db.AiExecutions.AsNoTracking().SingleAsync(e => e.Id == claim.ExecutionId, Ct)).AgentId);
        Assert.Equal(message.Id, (await store.BeginAsync(seed.Actor.Id, seed.Request, Settings, Ct)).Result.Message!.Id);
        Assert.False(db.Database.HasPendingModelChanges());

        async Task RejectAsync(FormattableString sql, string state) => Assert.Equal(state,
            (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql, Ct))).SqlState);
    }

    [Fact]
    public async Task Failed_publication_rolls_back_message_handoff_counters_and_claim_then_reused_context_can_finish()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        var store = new ConversationAiExecutionStore(db);
        var claim = (await store.BeginAsync(seed.Actor.Id, seed.Request, Settings, Ct)).Claim!;
        // Force only the message unique-index rejection, without changing the trigger's sequence/fence.
        await using (var inconsistent = fixture.CreateContext())
        {
            inconsistent.Messages.Add(new Message(seed.Workspace.Id, seed.Conversation.Id, seed.Actor.Id, Guid.NewGuid(), 2, "Conflicting sequence"));
            await inconsistent.SaveChangesAsync(Ct);
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => store.CommitAsync(claim,
            new(AiExecutionOutcome.FALLBACK, "Fallback", true, [], []), new(), Ct));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == seed.Conversation.Id, Ct);
        Assert.Equal(1, conversation.LastMessageSequence);
        Assert.Equal(0, conversation.LastHandoffSequence);
        Assert.False(await db.HandoffRequests.AnyAsync(h => h.ConversationId == seed.Conversation.Id, Ct));
        Assert.Equal(AiExecutionState.IN_PROGRESS, (await db.AiExecutions.AsNoTracking().SingleAsync(e => e.Id == claim.ExecutionId, Ct)).State);
        await store.FinishAsync(claim, AiExecutionFailure.STORE_UNAVAILABLE, new(), Ct);
        Assert.Equal(AiExecutionState.TERMINAL, (await db.AiExecutions.AsNoTracking().SingleAsync(e => e.Id == claim.ExecutionId, Ct)).State);
        Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == seed.Conversation.Id && m.SenderKind == MessageSenderKind.AI, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_usage_reconciliation_fences_claim_and_records_once_without_disclosing_to_revoked_member(bool finish)
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        var store = new ConversationAiExecutionStore(db);
        var claim = (await store.BeginAsync(seed.Actor.Id, seed.Request, Settings, Ct)).Claim!;
        Assert.True((await store.ReserveGenerationAsync(claim, 0.1m, Settings, Ct)).Admitted);
        await store.FinishAsync(claim, AiExecutionFailure.EXPIRED, new(), Ct);
        var before = await db.AiExecutions.AsNoTracking().SingleAsync(e => e.Id == claim.ExecutionId, Ct);
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == seed.Workspace.Id && m.UserId == seed.Actor.Id).ExecuteDeleteAsync(Ct);

        var usage = new AiExecutionUsage(true, 100, 20, 0.1m, 2m);
        AiExecutionClaim[] forgedClaims =
        [
            claim with { WorkspaceId = Guid.NewGuid() },
            claim with { ConversationId = Guid.NewGuid() },
            claim with { ExecutionId = Guid.NewGuid() },
            claim with { LeaseToken = Guid.NewGuid() },
            claim with { ActorId = Guid.NewGuid() },
            claim with { TriggerMessageId = Guid.NewGuid() }
        ];
        var decision = new AiExecutionDecision(AiExecutionOutcome.ANSWERED, "Late answer", true, [], []);
        foreach (var forged in forgedClaims)
            Assert.Equal(new(AiExecutionStatus.NotFound), await ReconcileAsync(forged));
        var fenced = await db.AiExecutions.AsNoTracking().SingleAsync(e => e.Id == claim.ExecutionId, Ct);
        Assert.Null(fenced.InputTokens);
        Assert.Null(fenced.OutputTokens);
        Assert.Null(fenced.ActualCost);

        Assert.Equal(new(AiExecutionStatus.NotFound), await ReconcileAsync(claim));
        // Independent contexts and either completion entry point must not replace the first known usage.
        await using var retryDb = fixture.CreateContext();
        var retryStore = new ConversationAiExecutionStore(retryDb);
        Assert.Equal(new(AiExecutionStatus.NotFound), await retryStore.CommitAsync(claim, decision, new(true, 1, 1, 0.1m, 0m), Ct));
        Assert.Equal(new(AiExecutionStatus.NotFound), await retryStore.FinishAsync(claim, AiExecutionFailure.CANCELLED, new(true, 200, 40, 0.1m, 4m), Ct));
        var ledger = await retryDb.AiExecutions.AsNoTracking().SingleAsync(e => e.Id == claim.ExecutionId, Ct);
        Assert.Equal(100, ledger.InputTokens);
        Assert.Equal(20, ledger.OutputTokens);
        Assert.Equal(2m, ledger.ActualCost);
        Assert.Equal(before.ReservedCost, ledger.ReservedCost);
        Assert.Equal(before.GenerationReservedAt, ledger.GenerationReservedAt);
        Assert.Equal(before.CompletedAt, ledger.CompletedAt);
        Assert.Equal(before.Outcome, ledger.Outcome);
        Assert.Equal(before.Failure, ledger.Failure);
        Assert.Equal(AiExecutionState.TERMINAL, ledger.State);
        Assert.True(ledger.ProviderAttempted);
        Assert.False(await retryDb.Messages.AnyAsync(m => m.ConversationId == seed.Conversation.Id && m.SenderKind == MessageSenderKind.AI, Ct));
        Assert.False(await retryDb.HandoffRequests.AnyAsync(h => h.ConversationId == seed.Conversation.Id, Ct));
        Assert.Equal(new(AiExecutionStatus.NotFound), (await retryStore.BeginAsync(seed.Actor.Id, seed.Request, Settings, Ct)).Result);

        Task<AiExecutionResult> ReconcileAsync(AiExecutionClaim candidate) => finish
            ? store.FinishAsync(candidate, AiExecutionFailure.CANCELLED, usage, Ct)
            : store.CommitAsync(candidate, decision, usage, Ct);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(true, 0)]
    public async Task Generation_reservation_is_shared_and_late_actual_usage_keeps_daily_budget_conservative(bool lateUsage, int actualCost)
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(fixture);
        var second = await AiExecutionPersistenceSeed.CreateAsync(fixture, seed);
        var third = await AiExecutionPersistenceSeed.CreateAsync(fixture, seed);
        await using var firstDb = fixture.CreateContext();
        await using var secondDb = fixture.CreateContext();
        await using var thirdDb = fixture.CreateContext();
        var firstStore = new ConversationAiExecutionStore(firstDb);
        var secondStore = new ConversationAiExecutionStore(secondDb);
        var thirdStore = new ConversationAiExecutionStore(thirdDb);
        var claim1 = (await firstStore.BeginAsync(seed.Actor.Id, seed.Request, Settings, Ct)).Claim!;
        var claim2 = (await secondStore.BeginAsync(seed.Actor.Id, second.Request, Settings, Ct)).Claim!;
        var claim3 = (await thirdStore.BeginAsync(seed.Actor.Id, third.Request, Settings, Ct)).Claim!;
        var limits = new AiExecutionSettings { GenerationsPerMinutePerWorkspace = 1, MaxGenerationsPerMinute = 10 };
        var admitted = await Task.WhenAll(firstStore.ReserveGenerationAsync(claim1, 0.1m, limits, Ct), secondStore.ReserveGenerationAsync(claim2, 0.1m, limits, Ct));
        Assert.Single(admitted, a => a.Admitted);
        Assert.Single(admitted, a => a.Failure == AiExecutionFailure.RATE_LIMITED);
        var (winner, winnerStore) = admitted[0].Admitted ? (claim1, firstStore) : (claim2, secondStore);
        if (lateUsage) await winnerStore.FinishAsync(winner, AiExecutionFailure.EXPIRED, new(), Ct);
        await winnerStore.FinishAsync(winner, AiExecutionFailure.PROVIDER_INVALID_RESPONSE, new(true, 100, 100, 0.1m, actualCost), Ct);
        var rejected = await thirdStore.ReserveGenerationAsync(claim3, 0.1m,
            new AiExecutionSettings { MaxCostPerWorkspacePerDay = actualCost == 0 ? 0.15m : 1m }, Ct);
        Assert.False(rejected.Admitted);
        Assert.Equal(AiExecutionFailure.QUOTA_EXCEEDED, rejected.Failure);
        var row = await thirdDb.AiExecutions.AsNoTracking().SingleAsync(e => e.Id == winner.ExecutionId, Ct);
        Assert.Equal(0.1m, row.ReservedCost); Assert.Equal((decimal)actualCost, row.ActualCost);
    }
}

public sealed class AiExecutionMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Member_history_round_trips_but_ai_history_refuses_downgrade_atomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await AiExecutionPersistenceSeed.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync("20260927111340_KnowledgeQueryUsage", ct);
        try
        {
            Assert.Equal("Question", await db.Database.SqlQuery<string>($"SELECT \"Content\" AS \"Value\" FROM \"Messages\" WHERE \"Id\" = {seed.Message.Id}").SingleAsync(ct));
            Assert.Equal(seed.Actor.Id, await db.Database.SqlQuery<Guid>($"SELECT \"SenderUserId\" AS \"Value\" FROM \"Messages\" WHERE \"Id\" = {seed.Message.Id}").SingleAsync(ct));
        }
        finally { await db.Database.MigrateAsync(ct); }
        await new ConversationAiBindingStore(db).BindAsync(seed.Actor.Id, seed.Workspace.Id, seed.Conversation.Id, seed.Agent.Id, ct);
        var store = new ConversationAiExecutionStore(db);
        var claim = (await store.BeginAsync(seed.Actor.Id, seed.Request, new(), ct)).Claim!;
        var result = await store.CommitAsync(claim, new(AiExecutionOutcome.FALLBACK, "Durable AI", true, [], []), new(), ct);
        db.ChangeTracker.Clear();
        var failure = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20260927111340_KnowledgeQueryUsage", ct));
        Assert.Equal(PostgresErrorCodes.RaiseException, failure.SqlState);
        Assert.Contains("Cannot downgrade", failure.MessageText);
        Assert.Equal("Durable AI", (await db.Messages.SingleAsync(m => m.Id == result.Message!.Id, ct)).Content);
        Assert.Equal(AiExecutionState.TERMINAL, (await db.AiExecutions.SingleAsync(e => e.Id == claim.ExecutionId, ct)).State);
        Assert.True(await db.HandoffRequests.AnyAsync(h => h.AiExecutionId == claim.ExecutionId, ct));
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(ct), m => m.EndsWith("_ConversationAiExecution"));
        Assert.False(db.Database.HasPendingModelChanges());
    }
}

internal sealed record AiExecutionPersistenceSeed(Workspace Workspace, User Actor, AiAgent Agent, Conversation Conversation, Message Message)
{
    public AiExecutionRequest Request => new(Workspace.Id, Conversation.Id, Message.Id);
    public static async Task<AiExecutionPersistenceSeed> CreateAsync(PostgresFixture fixture, AiExecutionPersistenceSeed? tenant = null)
    {
        await using var db = fixture.CreateContext();
        var workspace = tenant?.Workspace ?? new Workspace("AI execution persistence");
        var actor = tenant?.Actor ?? new User();
        var agent = tenant?.Agent ?? new AiAgent(workspace.Id, new("Assistant", true, null, "en", AiModelTier.FAST, AiKnowledgeScope.ALL_SOURCES, [], true, false, 10), DateTimeOffset.UtcNow);
        if (tenant is null) db.AddRange(workspace, actor, agent, new WorkspaceMember(workspace.Id, actor.Id, WorkspaceRole.OWNER));
        var channel = new Channel(workspace.Id, "Web");
        var contact = new Contact(workspace.Id, "Visitor", []);
        var conversation = new Conversation(workspace.Id, channel.Id, contact.Id, actor.Id, Guid.NewGuid());
        conversation.BindAiAgent(agent.Id);
        var message = new Message(workspace.Id, conversation.Id, actor.Id, Guid.NewGuid(), conversation.AdvanceMessageSequence(), "Question");
        db.AddRange(channel, contact, conversation, message);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new(workspace, actor, agent, conversation, message);
    }
}
