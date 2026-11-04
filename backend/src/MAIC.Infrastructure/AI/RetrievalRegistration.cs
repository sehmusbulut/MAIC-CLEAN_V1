using MAIC.Application.AI;
using MAIC.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MAIC.Infrastructure.AI;

public static class RetrievalRegistration
{
    /// <summary>
    /// Registers retrieval (after <see cref="EmbeddingRegistration.AddMaicEmbeddings"/>). Settings are validated against the
    /// embedding timeout, so a host with an interactive deadline longer than the provider timeout does not start. With a
    /// provider configured, the query path is warmed up at startup so no visitor waits for the provider check.
    /// </summary>
    public static IServiceCollection AddMaicRetrieval(this IServiceCollection services, RetrievalSettings settings, EmbeddingSettings embeddings)
    {
        settings.Validate(embeddings.TimeoutSeconds);
        services.AddSingleton(settings);
        services.AddSingleton<KnowledgeQueryGate>();
        services.AddScoped<IKnowledgeRetrievalStore, KnowledgeRetrievalStore>();
        services.AddScoped<KnowledgeRetriever>();
        services.AddScoped<RetrievalPreviewService>();
        if (embeddings.Enabled) services.AddHostedService<EmbeddingWarmup>();
        return services;
    }
}

/// <summary>
/// Embeds one fixed query in the background after startup, so the adapter's configuration probe runs before the first
/// visitor asks; retried with backoff (1 minute doubling to 30) until it succeeds. Each attempt is charged to the query
/// provider share with its real request count (the probe's query and batch, and the warm-up query). Never blocks or fails
/// startup; failures are logged without details.
/// </summary>
public sealed class EmbeddingWarmup(IServiceScopeFactory scopes, KnowledgeQueryGate gate, ILogger<EmbeddingWarmup> logger)
    : IHostedService
{
    private readonly CancellationTokenSource stopping = new();
    private Task running = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        running = Task.Run(() => WarmAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromMinutes(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                await using var scope = scopes.CreateAsyncScope();
                gate.ChargeProvider(3);
                await scope.ServiceProvider.GetRequiredService<KnowledgeVectorIndex>().WarmUpAsync(deadline.Token);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("Embedding warm-up did not complete ({Failure}); retrying in {Minutes} minutes.",
                    (error as EmbeddingException)?.Failure.ToString() ?? error.GetType().Name, wait.TotalMinutes);
            }
            try { await Task.Delay(wait, cancellationToken); }
            catch (OperationCanceledException) { return; }
            wait = TimeSpan.FromMinutes(Math.Min(30, wait.TotalMinutes * 2));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        await running.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        stopping.Dispose();
    }
}
