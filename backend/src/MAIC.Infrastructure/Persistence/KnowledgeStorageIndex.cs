using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

public sealed class KnowledgeStorageIndex(MaicDbContext db) : IKnowledgeStorageIndex
{
    public async Task<IReadOnlySet<Guid>> ExistingUploadsAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds,
        CancellationToken cancellationToken) =>
        (await db.KnowledgeDocuments.AsNoTracking()
            .Where(d => d.WorkspaceId == workspaceId && d.Origin == KnowledgeDocumentOrigin.UPLOAD && documentIds.Contains(d.Id))
            .Select(d => d.Id).ToListAsync(cancellationToken)).ToHashSet();

    public Task<bool> HasWorkspacesAsync(CancellationToken cancellationToken) => db.Workspaces.AnyAsync(cancellationToken);
}
