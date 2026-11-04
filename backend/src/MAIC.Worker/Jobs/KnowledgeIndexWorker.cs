using MAIC.Application.AI;

namespace MAIC.Worker.Jobs;

/// <summary>
/// Indexes READY documents into the configured embedding space, one document at a time in a fresh DI scope. It discovers
/// new work every 30 seconds (also while idle, never more often). Registered only when an embedding provider is configured; several
/// Worker instances may run it (claims skip rows others hold, leases recover crashed attempts).
/// </summary>
public sealed class KnowledgeIndexWorker(IServiceScopeFactory scopes, ILogger<KnowledgeIndexWorker> logger) : BackgroundService
{
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastDiscovery = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<KnowledgeIndexProcessor>();
                if (DateTimeOffset.UtcNow - lastDiscovery >= DiscoveryInterval)
                {
                    var found = await processor.DiscoverAsync(stoppingToken);
                    lastDiscovery = DateTimeOffset.UtcNow;
                    if (found > 0) logger.LogInformation("Knowledge indexing discovered {Count} documents.", found);
                }
                var outcome = await processor.ProcessNextAsync(stoppingToken);
                if (outcome is not null) Log(outcome);
                delay = outcome is null ? IdleDelay : TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Npgsql.PostgresException error) when (error.SqlState == Npgsql.PostgresErrorCodes.InsufficientPrivilege)
            {
                // A missing grant (for example after an upgrade added a table) stops all indexing: say so plainly.
                logger.LogCritical("Knowledge indexing is blocked: the database role lacks a privilege ({Table}). Apply the grants in " +
                    "EMBEDDINGS.md (\"Privileges and roles\").", error.TableName);
                delay = ErrorDelay;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Knowledge indexing polling failed.");
                delay = ErrorDelay;
            }
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // Only IDs, counts and failure codes are logged; never chunk text, queries, vectors or provider responses.
    private void Log(KnowledgeIndexOutcome outcome)
    {
        if (outcome.Error is not null)
            logger.LogError(outcome.Error, "Knowledge indexing of document {DocumentId} attempt {Attempt}: {Outcome} {Failure}",
                outcome.DocumentId, outcome.Attempt, outcome.Kind, outcome.Failure);
        else
            logger.LogInformation(
                "Knowledge indexing of document {DocumentId} attempt {Attempt}: {Outcome} {Failure} indexed {Indexed} rejected {Rejected}",
                outcome.DocumentId, outcome.Attempt, outcome.Kind, outcome.Failure, outcome.IndexedChunks, outcome.RejectedChunks);
    }
}
