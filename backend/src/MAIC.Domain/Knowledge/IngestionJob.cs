namespace MAIC.Domain.Knowledge;

public enum IngestionJobStatus
{
    QUEUED,
    RUNNING,
    SUCCEEDED,
    FAILED,
    CANCELLED
}

/// <summary>
/// One ingestion attempt series for a document. A worker claims a due job with a lease; the lease token must match
/// to finish it, so a worker whose lease expired cannot overwrite a newer claim. Transient failures are retried with
/// exponential backoff until <see cref="MaxAttempts"/> is reached.
/// </summary>
public sealed class IngestionJob
{
    public const int DefaultMaxAttempts = 5;
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private IngestionJob() { }

    public IngestionJob(Guid workspaceId, Guid documentId, DateTimeOffset now, int maxAttempts = DefaultMaxAttempts)
    {
        if (workspaceId == Guid.Empty || documentId == Guid.Empty) throw new ArgumentException("Job bindings are required.");
        if (maxAttempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        WorkspaceId = workspaceId;
        DocumentId = documentId;
        MaxAttempts = maxAttempts;
        CreatedAt = KnowledgeClock.Truncate(now);
        NextAttemptAt = CreatedAt;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid DocumentId { get; private set; }
    public IngestionJobStatus Status { get; private set; } = IngestionJobStatus.QUEUED;
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }
    /// <summary>When QUEUED: earliest next attempt. When RUNNING: lease expiry, after which another worker may reclaim it.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public IngestionFailure? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsActive => Status is IngestionJobStatus.QUEUED or IngestionJobStatus.RUNNING;
    public bool IsDue(DateTimeOffset now) => IsActive && NextAttemptAt <= now;
    public bool CanAttempt => Attempts < MaxAttempts;

    public static TimeSpan Backoff(int failedAttempts)
    {
        if (failedAttempts < 1) throw new ArgumentOutOfRangeException(nameof(failedAttempts));
        var seconds = BaseBackoff.TotalSeconds * Math.Pow(2, Math.Min(failedAttempts - 1, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    /// <summary>Starts an attempt: a due QUEUED job or a RUNNING job whose lease expired.</summary>
    public Guid Claim(DateTimeOffset now, TimeSpan lease)
    {
        if (!IsDue(now)) throw new InvalidOperationException("Job is not due.");
        if (!CanAttempt) throw new InvalidOperationException("Job has no attempts left.");
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        Attempts++;
        Status = IngestionJobStatus.RUNNING;
        LeaseToken = Guid.NewGuid();
        NextAttemptAt = KnowledgeClock.Truncate(now + lease);
        return LeaseToken.Value;
    }

    public bool Holds(Guid leaseToken) => Status == IngestionJobStatus.RUNNING && LeaseToken == leaseToken;

    public void Succeed(Guid leaseToken, DateTimeOffset now)
    {
        RequireLease(leaseToken);
        Complete(IngestionJobStatus.SUCCEEDED, null, now);
    }

    /// <summary>Records a failed attempt. Returns true when a transient failure was requeued with backoff.</summary>
    public bool Fail(Guid leaseToken, IngestionFailure reason, bool transient, DateTimeOffset now)
    {
        RequireLease(leaseToken);
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (transient && CanAttempt)
        {
            Status = IngestionJobStatus.QUEUED;
            LeaseToken = null;
            LastError = reason;
            NextAttemptAt = KnowledgeClock.Truncate(now + Backoff(Attempts));
            return true;
        }
        Complete(IngestionJobStatus.FAILED, reason, now);
        return false;
    }

    /// <summary>
    /// Returns a running attempt to the queue without counting it, for example when the worker shuts down mid-attempt.
    /// </summary>
    public void Release(Guid leaseToken, DateTimeOffset now)
    {
        RequireLease(leaseToken);
        Attempts--;
        Status = IngestionJobStatus.QUEUED;
        LeaseToken = null;
        NextAttemptAt = KnowledgeClock.Truncate(now);
    }

    /// <summary>Stops a job without processing, for example because its source was disabled.</summary>
    public void Cancel(IngestionFailure reason, DateTimeOffset now)
    {
        if (!IsActive) throw new InvalidOperationException("Job is already terminal.");
        Complete(IngestionJobStatus.CANCELLED, reason, now);
    }

    /// <summary>Fails a RUNNING job whose lease expired after its final attempt (the worker stopped mid-attempt).</summary>
    public void Abandon(DateTimeOffset now)
    {
        if (Status != IngestionJobStatus.RUNNING || !IsDue(now) || CanAttempt)
            throw new InvalidOperationException("Only an expired final attempt can be abandoned.");
        Complete(IngestionJobStatus.FAILED, IngestionFailure.LEASE_EXPIRED, now);
    }

    private void RequireLease(Guid leaseToken)
    {
        if (!Holds(leaseToken)) throw new InvalidOperationException("The lease is not held.");
    }

    private void Complete(IngestionJobStatus status, IngestionFailure? reason, DateTimeOffset now)
    {
        Status = status;
        LeaseToken = null;
        LastError = reason;
        CompletedAt = KnowledgeClock.Truncate(now);
    }
}
