using MAIC.Application.Workspaces;
using MAIC.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

public sealed class WorkspaceStore(MaicDbContext db) : IWorkspaceStore
{
    public async Task<WorkspaceView> CreateAsync(Workspace workspace, WorkspaceMember owner, CancellationToken cancellationToken)
    {
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(owner);
        await db.SaveChangesAsync(cancellationToken);
        return View(workspace);
    }

    private IQueryable<Workspace> Visible(Guid userId) => db.Workspaces.AsNoTracking()
        .Where(w => db.WorkspaceMembers.Any(m => m.WorkspaceId == w.Id && m.UserId == userId));

    public async Task<IReadOnlyList<WorkspaceView>> ListAsync(Guid userId, int skip, int take, CancellationToken cancellationToken) =>
        await Visible(userId).OrderBy(w => w.Id).Skip(skip).Take(take)
            .Select(w => new WorkspaceView(w.Id, w.Name, w.CreatedAt)).ToListAsync(cancellationToken);

    public Task<WorkspaceView?> GetAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken) =>
        Visible(userId).Where(w => w.Id == workspaceId)
            .Select(w => new WorkspaceView(w.Id, w.Name, w.CreatedAt)).SingleOrDefaultAsync(cancellationToken);

    public async Task<WorkspaceResult> RenameAsync(Guid userId, Guid workspaceId, string name,
        IReadOnlyCollection<WorkspaceRole> permittedRoles, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // All membership/invitation/rename writers lock the workspace first.
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken);
        // Lock the authorizing row until commit. Concurrent role changes/removal either
        // finish before this read (and are observed), or wait until this rename commits.
        var members = await db.WorkspaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "WorkspaceMembers" WHERE "WorkspaceId" = {workspaceId} AND "UserId" = {userId} FOR UPDATE
            """).AsNoTracking().ToListAsync(cancellationToken);
        var member = members.SingleOrDefault();
        if (member is null) return new(WorkspaceStatus.NotFound);
        if (!permittedRoles.Contains(member.Role)) return new(WorkspaceStatus.Forbidden);
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, cancellationToken);
        workspace.Rename(name);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(WorkspaceStatus.Success, View(workspace));
    }

    private static WorkspaceView View(Workspace workspace) => new(workspace.Id, workspace.Name, workspace.CreatedAt);
}
