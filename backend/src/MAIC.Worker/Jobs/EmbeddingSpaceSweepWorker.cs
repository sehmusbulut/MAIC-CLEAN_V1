using MAIC.Infrastructure.AI;
using MAIC.Infrastructure.Persistence;

namespace MAIC.Worker.Jobs;

/// <summary>
/// Deletes embedding spaces (with their vectors and index states) that no API or Worker has used for
/// <see cref="EmbeddingSettings.RetiredSpaceRetentionDays"/> days, never the configured one. Registered only when a provider
/// is configured and retention is enabled, so turning the provider off never deletes vectors.
/// </summary>
public sealed class EmbeddingSpaceSweepWorker(IServiceScopeFactory scopes, EmbeddingSettings settings,
    ILogger<EmbeddingSpaceSweepWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<EmbeddingSpaceRegistry>().DeleteRetiredAsync(settings.Space(),
                    settings.RetiredSpaceRetentionDays, settings.ManageSchema, stoppingToken);
                foreach (var space in deleted)
                    logger.LogWarning("Deleted retired embedding space {SpaceKey} (unused for {Days} days) with {Vectors} vectors.",
                        space.Key, settings.RetiredSpaceRetentionDays, space.VectorsDeleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Retired embedding space cleanup failed.");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
