namespace MAIC.Domain.Workspaces;

public enum WorkspaceRole { OWNER, ADMIN, AGENT }

public sealed class WorkspaceMember
{
    private WorkspaceMember() { }
    public WorkspaceMember(Guid workspaceId, Guid userId, WorkspaceRole role)
    {
        if (workspaceId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Membership requires workspace and user IDs.");
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        WorkspaceId = workspaceId;
        UserId = userId;
        Role = role;
    }
    public void ChangeRole(WorkspaceRole role)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        Role = role;
    }
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid UserId { get; private set; }
    public WorkspaceRole Role { get; private set; }
}
