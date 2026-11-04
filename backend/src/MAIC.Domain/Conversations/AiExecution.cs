namespace MAIC.Domain.Conversations;

public enum AiExecutionState { IN_PROGRESS, TERMINAL }

/// <summary>Durable trigger ownership and immutable agent provenance. Historical agents need not survive.</summary>
public sealed class AiExecution
{
    private AiExecution() { }

    public AiExecution(Guid workspaceId, Guid conversationId, Guid triggerMessageId, Guid actorId, Guid agentId,
        string agentSnapshot, long revision, long messageSequence, long assignmentSequence, long handoffSequence,
        DateTimeOffset now, DateTimeOffset expiresAt)
    {
        if (new[] { workspaceId, conversationId, triggerMessageId, actorId, agentId }.Contains(Guid.Empty)
            || string.IsNullOrWhiteSpace(agentSnapshot) || expiresAt <= now || revision < 0 || messageSequence < 1
            || assignmentSequence < 0 || handoffSequence < 0) throw new ArgumentException("Invalid execution claim.");
        WorkspaceId = workspaceId; ConversationId = conversationId; TriggerMessageId = triggerMessageId;
        ActorId = actorId; AgentId = agentId; AgentSnapshot = agentSnapshot; ConversationRevision = revision;
        MessageSequence = messageSequence; AssignmentSequence = assignmentSequence; HandoffSequence = handoffSequence;
        CreatedAt = now; ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid TriggerMessageId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid AgentId { get; private set; }
    public string AgentSnapshot { get; private set; } = null!;
    public long ConversationRevision { get; private set; }
    public long MessageSequence { get; private set; }
    public long AssignmentSequence { get; private set; }
    public long HandoffSequence { get; private set; }
    public Guid LeaseToken { get; private set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public AiExecutionState State { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? Outcome { get; private set; }
    public string? Failure { get; private set; }
    public string? RetrievalOutcome { get; private set; }
    public string? RetrievalReason { get; private set; }
    public string CitationsJson { get; private set; } = "[]";
    public DateTimeOffset? GenerationReservedAt { get; private set; }
    public decimal ReservedCost { get; private set; }
    public decimal? ActualCost { get; private set; }
    public long? InputTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public bool ProviderAttempted { get; private set; }

    public bool IsLive(Guid token, DateTimeOffset now) => State == AiExecutionState.IN_PROGRESS && LeaseToken == token && ExpiresAt > now;

    public void Reserve(decimal cost, DateTimeOffset now)
    {
        if (!IsLive(LeaseToken, now) || GenerationReservedAt is not null || cost < 0) throw new InvalidOperationException("Execution cannot reserve generation.");
        ReservedCost = cost; GenerationReservedAt = now;
        // Once external work is admitted, process death must retain conservative cost accounting.
        ProviderAttempted = true;
    }

    public void Complete(string outcome, string? failure, DateTimeOffset now, bool providerAttempted = false,
        long? inputTokens = null, long? outputTokens = null, decimal? actualCost = null,
        string? retrievalOutcome = null, string? retrievalReason = null, string citationsJson = "[]")
    {
        if (State != AiExecutionState.IN_PROGRESS || string.IsNullOrWhiteSpace(outcome) || inputTokens < 0 || outputTokens < 0 || actualCost < 0)
            throw new InvalidOperationException("Invalid execution completion.");
        State = AiExecutionState.TERMINAL; Outcome = outcome; Failure = failure; CompletedAt = now;
        ProviderAttempted |= providerAttempted; InputTokens = inputTokens; OutputTokens = outputTokens; ActualCost = actualCost;
        RetrievalOutcome = retrievalOutcome; RetrievalReason = retrievalReason; CitationsJson = citationsJson;
    }

    /// <summary>Record validated usage once after a concurrent terminal transition, without changing its result.</summary>
    public bool ReconcileUsage(long inputTokens, long outputTokens, decimal actualCost)
    {
        if (State != AiExecutionState.TERMINAL || GenerationReservedAt is null || !ProviderAttempted
            || inputTokens < 0 || outputTokens < 0 || actualCost < 0)
            throw new InvalidOperationException("Invalid execution usage reconciliation.");
        if (InputTokens is not null || OutputTokens is not null || ActualCost is not null) return false;
        InputTokens = inputTokens; OutputTokens = outputTokens; ActualCost = actualCost;
        return true;
    }
}
