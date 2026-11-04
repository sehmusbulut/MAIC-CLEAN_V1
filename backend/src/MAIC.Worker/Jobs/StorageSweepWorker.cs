using MAIC.Application.Knowledge;

namespace MAIC.Worker.Jobs;

/// <summary>Periodically quarantines stored upload files that no document row references (see <see cref="KnowledgeStorageSweeper"/>).</summary>
public sealed class StorageSweepWorker(IServiceScopeFactory scopes, ILogger<StorageSweepWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<KnowledgeStorageSweeper>()
                    .SweepAsync(KnowledgeStorageSweeper.DefaultGracePeriod, stoppingToken);
                if (result.Aborted)
                    logger.LogWarning("Knowledge storage sweep aborted: the database has no workspaces or {Scanned} scanned files " +
                        "look mostly unreferenced. Check that this Worker uses the right database for this storage root.", result.Scanned);
                else if (result.Quarantined > 0 || result.Purged > 0)
                    logger.LogInformation("Quarantined {Quarantined} unreferenced knowledge files and purged {Purged}.",
                        result.Quarantined, result.Purged);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Knowledge storage sweep failed.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try { return await timer.WaitForNextTickAsync(stoppingToken); }
        catch (OperationCanceledException) { return false; }
    }
}
