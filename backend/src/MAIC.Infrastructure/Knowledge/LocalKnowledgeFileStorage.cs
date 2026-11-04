using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;

namespace MAIC.Infrastructure.Knowledge;

/// <summary>
/// Stores upload bytes on a local or mounted volume shared by the API and Worker, at
/// {root}/{workspaceId:N}/{documentId:N}. Paths are built only from server-generated IDs, never from file names.
/// Writes go to a temporary file first and are moved into place, so readers never see partial content.
/// </summary>
public sealed class LocalKnowledgeFileStorage : IKnowledgeFileStorage
{
    private readonly Lazy<string> root;

    public LocalKnowledgeFileStorage(string rootPath) : this(() => rootPath) { }

    /// <summary>The root is resolved on first use, so hosts without it configured fail only when bytes are touched.</summary>
    public LocalKnowledgeFileStorage(Func<string?> rootPath) => root = new(() =>
    {
        var configured = rootPath();
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("KnowledgeStorage:RootPath is required for knowledge document storage.");
        return Path.GetFullPath(configured);
    });

    private string PathFor(Guid workspaceId, Guid documentId)
    {
        if (workspaceId == Guid.Empty || documentId == Guid.Empty) throw new ArgumentException("Storage key IDs are required.");
        return Path.Combine(root.Value, workspaceId.ToString("N"), documentId.ToString("N"));
    }

    public async Task SaveAsync(Guid workspaceId, Guid documentId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var path = PathFor(workspaceId, documentId);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            // The sweeper may remove an empty, old workspace directory concurrently; recreate it once if that happens.
            for (var attempt = 0; ; attempt++)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                try
                {
                    await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                        FileOptions.Asynchronous);
                    await stream.WriteAsync(content, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    break;
                }
                catch (DirectoryNotFoundException) when (attempt == 0) { }
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async Task<byte[]> ReadAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken)
    {
        var path = PathFor(workspaceId, documentId);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (stream.Length > KnowledgeDocument.MaxUploadBytes) throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
            var buffer = new byte[stream.Length];
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return buffer;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new IngestionException(IngestionFailure.STORAGE_MISSING, false, error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IngestionException(IngestionFailure.STORAGE_UNAVAILABLE, true, error);
        }
    }

    public async IAsyncEnumerable<StoredKnowledgeFile> ListAsync(DateTimeOffset olderThan,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root.Value)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root.Value))
        {
            if (!TryParseKey(Path.GetFileName(directory), out var workspaceId)) continue;
            string[] files;
            try { files = Directory.EnumerateFiles(directory).ToArray(); }
            catch (DirectoryNotFoundException) { continue; }
            if (files.Length == 0 && Directory.GetLastWriteTimeUtc(directory) < olderThan.UtcDateTime)
            {
                // Left behind by deleted documents or a cascade-deleted workspace.
                try { Directory.Delete(directory); }
                catch (IOException) { }
                continue;
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTime written;
                try { written = File.GetLastWriteTimeUtc(file); }
                catch (IOException) { continue; }
                if (written >= olderThan.UtcDateTime) continue;
                var name = Path.GetFileName(file);
                if (TryParseKey(name, out var documentId)) yield return new(workspaceId, documentId);
                else if (IsTemporaryName(name))
                {
                    // A temporary file this old belongs to a write that crashed before it was moved into place.
                    try { File.Delete(file); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            await Task.Yield();
        }
    }

    // Not a GUID, so ListAsync never enumerates it as a workspace directory.
    private const string QuarantineDirectory = "_quarantine";

    public Task QuarantineAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken)
    {
        var source = PathFor(workspaceId, documentId);
        var target = Path.Combine(root.Value, QuarantineDirectory, workspaceId.ToString("N"), documentId.ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target, overwrite: true);
            // The quarantine time drives purging.
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
        catch (FileNotFoundException)
        {
            // Already removed.
        }
        return Task.CompletedTask;
    }

    public Task<int> PurgeQuarantineAsync(DateTimeOffset quarantinedBefore, CancellationToken cancellationToken)
    {
        var quarantine = Path.Combine(root.Value, QuarantineDirectory);
        if (!Directory.Exists(quarantine)) return Task.FromResult(0);
        var purged = 0;
        foreach (var file in Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= quarantinedBefore.UtcDateTime) continue;
                File.Delete(file);
                purged++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return Task.FromResult(purged);
    }

    private static bool TryParseKey(string name, out Guid id) =>
        Guid.TryParseExact(name, "N", out id) && id != Guid.Empty && name == id.ToString("N");

    private static bool IsTemporaryName(string name) =>
        name.Length == 32 + 1 + 32 + 4 && name.EndsWith(".tmp", StringComparison.Ordinal) && name[32] == '.'
        && TryParseKey(name[..32], out _) && Guid.TryParseExact(name[33..65], "N", out _);

    public Task DeleteAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken)
    {
        try
        {
            File.Delete(PathFor(workspaceId, documentId));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort: an orphaned file is unreachable because no document row references it.
        }
        return Task.CompletedTask;
    }
}
