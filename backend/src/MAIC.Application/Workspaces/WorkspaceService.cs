using MAIC.Application.Identity;
using MAIC.Domain.Workspaces;

namespace MAIC.Application.Workspaces;

public sealed record WorkspaceView(Guid Id, string Name, DateTimeOffset CreatedAt);
public enum WorkspaceStatus { Success, Unauthenticated, Invalid, NotFound, Forbidden, Conflict }
public sealed record WorkspaceResult(WorkspaceStatus Status, WorkspaceView? Workspace = null);

public interface IWorkspaceStore
{
    Task<WorkspaceView> CreateAsync(Workspace workspace, WorkspaceMember owner, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkspaceView>> ListAsync(Guid userId, int skip, int take, CancellationToken cancellationToken);
    Task<WorkspaceView?> GetAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken);
    // Implementations must keep persisted membership authorization valid through the write.
    Task<WorkspaceResult> RenameAsync(Guid userId, Guid workspaceId, string name,
        IReadOnlyCollection<WorkspaceRole> permittedRoles, CancellationToken cancellationToken);
}

public sealed class WorkspaceService(ICurrentUser currentUser, IWorkspaceStore store)
{
    public async Task<WorkspaceResult> CreateAsync(string? name, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return new(WorkspaceStatus.Unauthenticated);
        if (!Workspace.IsValidName(name)) return new(WorkspaceStatus.Invalid);
        var existing = await store.ListAsync(userId, 0, 1, cancellationToken);
        if (existing.Count > 0) return new(WorkspaceStatus.Conflict);
        var workspace = new Workspace(name!);
        return new(WorkspaceStatus.Success, await store.CreateAsync(workspace,
            new WorkspaceMember(workspace.Id, userId, WorkspaceRole.OWNER), cancellationToken));
    }

    public async Task<(WorkspaceStatus Status, IReadOnlyList<WorkspaceView> Items)> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return (WorkspaceStatus.Unauthenticated, []);
        if (skip < 0 || take is < 1 or > 100) return (WorkspaceStatus.Invalid, []);
        return (WorkspaceStatus.Success, await store.ListAsync(userId, skip, take, cancellationToken));
    }

    public async Task<WorkspaceResult> GetAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return new(WorkspaceStatus.Unauthenticated);
        var workspace = await store.GetAsync(userId, workspaceId, cancellationToken);
        return new(workspace is null ? WorkspaceStatus.NotFound : WorkspaceStatus.Success, workspace);
    }

    public Task<WorkspaceResult> RenameAsync(Guid workspaceId, string? name, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return Task.FromResult(new WorkspaceResult(WorkspaceStatus.Unauthenticated));
        if (!Workspace.IsValidName(name)) return Task.FromResult(new WorkspaceResult(WorkspaceStatus.Invalid));
        return store.RenameAsync(userId, workspaceId, name!.Trim(), [WorkspaceRole.OWNER, WorkspaceRole.ADMIN], cancellationToken);
    }
}
