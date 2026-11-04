namespace MAIC.Domain.Workspaces;

public sealed class WorkspaceInvitation
{
    private WorkspaceInvitation() { }
    public WorkspaceInvitation(Guid workspaceId, Guid recipientUserId, Guid inviterUserId, WorkspaceRole role, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || recipientUserId == Guid.Empty || inviterUserId == Guid.Empty)
            throw new ArgumentException("Invitation requires workspace, recipient and inviter IDs.");
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        WorkspaceId = workspaceId;
        RecipientUserId = recipientUserId;
        InviterUserId = inviterUserId;
        Role = role;
        CreatedAt = now;
        ExpiresAt = now.AddDays(7);
    }
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public Guid InviterUserId { get; private set; }
    public WorkspaceRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public bool IsPending(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && now < ExpiresAt;
    public void Accept(DateTimeOffset now)
    {
        if (!IsPending(now)) throw new InvalidOperationException("Invitation is no longer pending.");
        AcceptedAt = now;
    }
    public void Revoke(DateTimeOffset now)
    {
        if (!IsPending(now)) throw new InvalidOperationException("Invitation is no longer pending.");
        RevokedAt = now;
    }
}
