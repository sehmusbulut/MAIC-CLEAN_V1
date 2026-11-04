using System.Text.RegularExpressions;

namespace MAIC.Application.AI;

/// <summary>
/// Identifies one vector space: vectors are comparable only within the same space. The key is chosen by server
/// configuration (for example "default-v1") and changes whenever the embedding model or its settings change, so vectors
/// of different models are never mixed. It names no provider in contracts; implementations map it to a model.
/// </summary>
public sealed partial record EmbeddingSpace
{
    public const int MaxDimensions = 4096;
    /// <summary>The key of the space used while no embedding provider is configured; never registered or searched.</summary>
    public const string DisabledKey = "disabled";
    public bool IsDisabled => Key == DisabledKey;

    public EmbeddingSpace(string key, int dimensions)
    {
        if (key is null || !KeyPattern().IsMatch(key)) throw new ArgumentException("Embedding space key must match [a-z0-9][a-z0-9._-]{0,63}.", nameof(key));
        if (dimensions is < 1 or > MaxDimensions) throw new ArgumentOutOfRangeException(nameof(dimensions));
        Key = key;
        Dimensions = dimensions;
    }

    public string Key { get; }
    public int Dimensions { get; }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>Some models embed stored passages and search queries differently.</summary>
public enum EmbeddingPurpose
{
    Document,
    Query
}

/// <summary>
/// Provider-neutral embedding port. Implementations live in Infrastructure, keep provider SDK types there, and return
/// exactly one vector of <see cref="EmbeddingSpace.Dimensions"/> per input, in input order. Failures are reported as
/// <see cref="EmbeddingException"/>.
/// </summary>
public interface IEmbeddingGenerator
{
    EmbeddingSpace Space { get; }
    /// <summary>Largest number of inputs per call.</summary>
    int MaxBatchSize { get; }
    /// <summary>Longest single input, in UTF-16 units; callers never send longer text.</summary>
    int MaxInputLength { get; }

    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose,
        CancellationToken cancellationToken);
}

public enum EmbeddingFailure
{
    /// <summary>Rate limit, timeout or provider outage: retry later.</summary>
    UNAVAILABLE,
    /// <summary>The provider rejected the input (for example content policy or length).</summary>
    REJECTED_INPUT,
    /// <summary>
    /// The provider answered with the wrong count, dimensions or non-finite values, or a malformed/oversized body. An
    /// adapter that can tell a consistent dimension mismatch (the configured Dimensions are wrong) reports MISCONFIGURED.
    /// Callers treat this as a provider fault, never as a content failure.
    /// </summary>
    INVALID_RESPONSE,
    /// <summary>Credentials or configuration are wrong; retrying will not help until an operator acts.</summary>
    MISCONFIGURED
}

/// <summary>
/// A classified embedding failure. Messages never contain input text or provider payloads, and adapters must not attach
/// an inner exception that does (wrap only exception types/status codes they have sanitized).
/// </summary>
public sealed class EmbeddingException(EmbeddingFailure failure, Exception? inner = null, TimeSpan? retryAfter = null)
    : Exception($"Embedding failed: {failure}.", inner)
{
    public EmbeddingFailure Failure { get; } = failure;
    public bool Transient => Failure == EmbeddingFailure.UNAVAILABLE;
    /// <summary>Provider-suggested delay for <see cref="EmbeddingFailure.UNAVAILABLE"/>, when known.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
