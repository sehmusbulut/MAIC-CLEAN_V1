using MAIC.Application.AI;

namespace MAIC.Infrastructure.AI;

/// <summary>
/// Used while no provider is configured. Callers can check <see cref="EmbeddingSettings.Enabled"/> to skip indexing;
/// anything that still calls it gets a MISCONFIGURED failure instead of silently empty vectors.
/// </summary>
public sealed class DisabledEmbeddingGenerator : IEmbeddingGenerator
{
    public EmbeddingSpace Space { get; } = new(EmbeddingSettings.ReservedSpaceKey, 1);
    public int MaxBatchSize => 1;
    public int MaxInputLength => 1;

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose,
        CancellationToken cancellationToken) => throw new EmbeddingException(EmbeddingFailure.MISCONFIGURED);
}
