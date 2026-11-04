using MAIC.Domain.Conversations;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class AiExecutionTests
{
    [Fact]
    public void Binding_and_lifecycle_round_trips_advance_fence_but_noops_do_not()
    {
        var conversation = new Conversation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.True(conversation.BindAiAgent(a));
        Assert.False(conversation.BindAiAgent(a));
        conversation.BindAiAgent(b); conversation.BindAiAgent(a);
        Assert.Equal(3, conversation.AiBindingRevision);
        Assert.True(conversation.Resolve()); Assert.False(conversation.Resolve());
        Assert.True(conversation.Reopen()); Assert.False(conversation.Reopen());
        Assert.Equal(5, conversation.AiBindingRevision);
        Assert.True(conversation.BindAiAgent(null));
        Assert.Null(conversation.AiAgentId);
        Assert.Throws<ArgumentException>(() => conversation.BindAiAgent(Guid.Empty));
    }

    [Fact]
    public void Ai_message_has_durable_execution_provenance_and_no_member_identity()
    {
        var execution = Guid.NewGuid();
        var message = Message.CreateAi(Guid.NewGuid(), Guid.NewGuid(), execution, 2, "Answer");
        Assert.Equal(MessageSenderKind.AI, message.SenderKind);
        Assert.Null(message.SenderUserId);
        Assert.Equal(execution, message.AiExecutionId);
        Assert.Equal(execution, message.ClientMessageId);
        var member = new Message(message.WorkspaceId, message.ConversationId, Guid.NewGuid(), Guid.NewGuid(), 1, "Question");
        Assert.Equal(MessageSenderKind.MEMBER, member.SenderKind);
        Assert.Null(member.AiExecutionId);
        Assert.Throws<ArgumentException>(() => Message.CreateAi(message.WorkspaceId, message.ConversationId, Guid.Empty, 3, "Answer"));
        Assert.Throws<ArgumentException>(() => Message.CreateAi(message.WorkspaceId, message.ConversationId, execution, 3, " "));
    }

    [Fact]
    public void Claim_fences_lease_and_preserves_unknown_cost_without_re_admission()
    {
        var now = DateTimeOffset.UtcNow;
        var execution = new AiExecution(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{}", 1, 1, 0, 0, now, now.AddSeconds(45));
        Assert.True(execution.IsLive(execution.LeaseToken, now));
        Assert.False(execution.IsLive(Guid.NewGuid(), now));
        execution.Reserve(0.1m, now);
        Assert.True(execution.ProviderAttempted);
        Assert.Throws<InvalidOperationException>(() => execution.Reserve(0.1m, now));
        Assert.False(execution.IsLive(execution.LeaseToken, now.AddSeconds(45)));
        execution.Complete("UNAVAILABLE", "EXPIRED", now.AddSeconds(45));
        Assert.Equal(0.1m, execution.ReservedCost);
        Assert.Null(execution.ActualCost);
        Assert.False(execution.IsLive(execution.LeaseToken, now));
        Assert.Throws<InvalidOperationException>(() => execution.Complete("ANSWERED", null, now.AddSeconds(46)));
    }

    [Fact]
    public void Usage_reconciliation_records_once_without_changing_terminal_result_or_reservation()
    {
        var now = DateTimeOffset.UtcNow;
        var execution = NewExecution(now);
        execution.Reserve(0.1m, now);
        execution.Complete("UNAVAILABLE", "EXPIRED", now.AddSeconds(45), retrievalOutcome: "FOUND",
            retrievalReason: "MATCH", citationsJson: "[1]");

        Assert.True(execution.ReconcileUsage(100, 20, 2m));
        Assert.False(execution.ReconcileUsage(1, 1, 0m));
        Assert.False(execution.ReconcileUsage(200, 40, 4m));
        Assert.Equal(100, execution.InputTokens);
        Assert.Equal(20, execution.OutputTokens);
        Assert.Equal(2m, execution.ActualCost);
        Assert.Equal(0.1m, execution.ReservedCost);
        Assert.Equal(now, execution.GenerationReservedAt);
        Assert.True(execution.ProviderAttempted);
        Assert.Equal(AiExecutionState.TERMINAL, execution.State);
        Assert.Equal("UNAVAILABLE", execution.Outcome);
        Assert.Equal("EXPIRED", execution.Failure);
        Assert.Equal(now.AddSeconds(45), execution.CompletedAt);
        Assert.Equal("FOUND", execution.RetrievalOutcome);
        Assert.Equal("MATCH", execution.RetrievalReason);
        Assert.Equal("[1]", execution.CitationsJson);
        Assert.False(execution.IsLive(execution.LeaseToken, now));
    }

    [Fact]
    public void Usage_reconciliation_requires_terminal_execution_with_admitted_generation()
    {
        var now = DateTimeOffset.UtcNow;
        var execution = NewExecution(now);
        Assert.Throws<InvalidOperationException>(() => execution.ReconcileUsage(1, 1, 1m));
        execution.Reserve(0m, now);
        Assert.Throws<InvalidOperationException>(() => execution.ReconcileUsage(1, 1, 1m));
        execution.Complete("UNAVAILABLE", "EXPIRED", now.AddSeconds(45));
        Assert.True(execution.ReconcileUsage(0, 0, 0m));

        var unreserved = NewExecution(now);
        unreserved.Complete("UNAVAILABLE", "EXPIRED", now.AddSeconds(45));
        Assert.Throws<InvalidOperationException>(() => unreserved.ReconcileUsage(1, 1, 1m));
    }

    [Theory]
    [InlineData(-1, 1, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, -1)]
    public void Usage_reconciliation_rejects_negative_values_atomically(long input, long output, int cost)
    {
        var now = DateTimeOffset.UtcNow;
        var execution = NewExecution(now);
        execution.Reserve(0.1m, now);
        execution.Complete("UNAVAILABLE", "EXPIRED", now.AddSeconds(45));
        Assert.Throws<InvalidOperationException>(() => execution.ReconcileUsage(input, output, cost));
        Assert.Null(execution.InputTokens);
        Assert.Null(execution.OutputTokens);
        Assert.Null(execution.ActualCost);
    }

    [Theory]
    [InlineData(1L, null, null)]
    [InlineData(null, 1L, null)]
    [InlineData(null, null, 1)]
    [InlineData(1L, 1L, 1)]
    public void Usage_reconciliation_never_replaces_previously_recorded_usage(long? input, long? output, int? cost)
    {
        var now = DateTimeOffset.UtcNow;
        var execution = NewExecution(now);
        execution.Reserve(0.1m, now);
        execution.Complete("UNAVAILABLE", "PROVIDER_INVALID_RESPONSE", now, true, input, output, cost);
        Assert.False(execution.ReconcileUsage(100, 100, 2m));
        Assert.Equal(input, execution.InputTokens);
        Assert.Equal(output, execution.OutputTokens);
        Assert.Equal((decimal?)cost, execution.ActualCost);
    }

    private static AiExecution NewExecution(DateTimeOffset now) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), "{}", 1, 1, 0, 0, now, now.AddSeconds(45));
}
