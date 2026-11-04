using MAIC.Application.Knowledge;

namespace MAIC.Worker.Jobs;

/// <summary>
/// Polls for due ingestion jobs and processes them one at a time, each in a fresh DI scope. Several worker instances
/// may run concurrently: claims skip rows other workers hold, and leases let a crashed attempt be reclaimed.
/// </summary>
public sealed class IngestionWorker(IServiceScopeFactory scopes, ILogger<IngestionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var outcome = await scope.ServiceProvider.GetRequiredService<IngestionProcessor>().ProcessNextAsync(stoppingToken);
                if (outcome is not null) Log(outcome);
                delay = outcome is null ? IdleDelay : TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                // Database or infrastructure failure outside an attempt; claimed work is recovered through its lease.
                logger.LogError(error, "Ingestion polling failed.");
                delay = ErrorDelay;
            }
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // Only IDs, attempt numbers and failure codes are logged; never document content, URLs or file names.
    private void Log(IngestionOutcome outcome)
    {
        if (outcome.Error is not null && outcome.Failure == Domain.Knowledge.IngestionFailure.INTERNAL_ERROR)
            logger.LogError(outcome.Error, "Ingestion job {JobId} for document {DocumentId} attempt {Attempt}: {Outcome} {Failure}",
                outcome.JobId, outcome.DocumentId, outcome.Attempt, outcome.Kind, outcome.Failure);
        else
            logger.LogInformation("Ingestion job {JobId} for document {DocumentId} attempt {Attempt}: {Outcome} {Failure}",
                outcome.JobId, outcome.DocumentId, outcome.Attempt, outcome.Kind, outcome.Failure);
    }
}
