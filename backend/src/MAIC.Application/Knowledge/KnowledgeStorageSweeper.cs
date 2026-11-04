namespace MAIC.Application.Knowledge;

/// <summary>Answers which upload documents still exist, so storage can drop files nothing references.</summary>
public interface IKnowledgeStorageIndex
{
    Task<IReadOnlySet<Guid>> ExistingUploadsAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds,
        CancellationToken cancellationToken);
    /// <summary>False when the database has no workspaces at all, which signals a wrong or empty database.</summary>
    Task<bool> HasWorkspacesAsync(CancellationToken cancellationToken);
}

public sealed record StorageSweepResult(int Scanned, int Quarantined, int Purged, bool Aborted);

/// <summary>
/// Cleans stored files whose document row no longer exists: deleted documents, cascade-deleted workspaces or sources,
/// and uploads whose transaction never committed. Only files older than the grace period are considered, so an upload
/// whose row is still being committed is never touched. Orphans are moved to quarantine, not deleted, and purged only
/// after <see cref="QuarantineRetention"/>, so a Worker pointed at the wrong database cannot destroy data: the sweep
/// also aborts when the database has no workspaces or when an implausibly large share of files looks orphaned.
/// </summary>
public sealed class KnowledgeStorageSweeper(IKnowledgeFileStorage storage, IKnowledgeStorageIndex index, TimeProvider clock)
{
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromHours(1);
    public static readonly TimeSpan QuarantineRetention = TimeSpan.FromDays(7);
    /// <summary>Orphans above this share of scanned files (and above <see cref="SafetyFloor"/>) abort the sweep.</summary>
    public const double MaxOrphanShare = 0.5;
    public const int SafetyFloor = 100;
    private const int BatchSize = 500;

    public async Task<StorageSweepResult> SweepAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        if (gracePeriod < TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        var now = clock.GetUtcNow();
        var purged = await storage.PurgeQuarantineAsync(now - QuarantineRetention, cancellationToken);
        if (!await index.HasWorkspacesAsync(cancellationToken)) return new(0, 0, purged, Aborted: true);

        var scanned = 0;
        var orphans = new List<StoredKnowledgeFile>();
        var batch = new List<StoredKnowledgeFile>(BatchSize);
        await foreach (var file in storage.ListAsync(now - gracePeriod, cancellationToken))
        {
            scanned++;
            batch.Add(file);
            if (batch.Count == BatchSize) await CollectAsync(batch, orphans, cancellationToken);
        }
        await CollectAsync(batch, orphans, cancellationToken);
        if (orphans.Count > SafetyFloor && orphans.Count > scanned * MaxOrphanShare) return new(scanned, 0, purged, Aborted: true);

        foreach (var orphan in orphans) await storage.QuarantineAsync(orphan.WorkspaceId, orphan.DocumentId, cancellationToken);
        return new(scanned, orphans.Count, purged, Aborted: false);
    }

    private async Task CollectAsync(List<StoredKnowledgeFile> batch, List<StoredKnowledgeFile> orphans, CancellationToken cancellationToken)
    {
        foreach (var workspace in batch.GroupBy(f => f.WorkspaceId))
        {
            var ids = workspace.Select(f => f.DocumentId).ToArray();
            var existing = await index.ExistingUploadsAsync(workspace.Key, ids, cancellationToken);
            orphans.AddRange(ids.Where(id => !existing.Contains(id)).Select(id => new StoredKnowledgeFile(workspace.Key, id)));
        }
        batch.Clear();
    }
}
