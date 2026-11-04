namespace MAIC.Domain.Workspaces;

public static class MembershipPolicy
{
    public static bool CanInvite(WorkspaceRole actor, WorkspaceRole role) =>
        Enum.IsDefined(role) && (actor == WorkspaceRole.OWNER || actor == WorkspaceRole.ADMIN && role == WorkspaceRole.AGENT);
    public static bool CanChangeRole(WorkspaceRole actor) => actor == WorkspaceRole.OWNER;
    public static bool CanRemove(WorkspaceRole actor, WorkspaceRole target, bool self) =>
        self || actor == WorkspaceRole.OWNER || actor == WorkspaceRole.ADMIN && target == WorkspaceRole.AGENT;
    public static bool WouldLoseFinalOwner(WorkspaceRole target, WorkspaceRole? replacement, int owners) =>
        target == WorkspaceRole.OWNER && replacement != WorkspaceRole.OWNER && owners <= 1;
}
