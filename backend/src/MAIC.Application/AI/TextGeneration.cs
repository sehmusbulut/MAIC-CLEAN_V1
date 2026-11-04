using System.Text;
using MAIC.Domain.AI;

namespace MAIC.Application.AI;

public enum TextGenerationRole { System, User, Assistant }

/// <summary>Prompt text is private tenant data and is never included in diagnostics.</summary>
public sealed record TextGenerationMessage(TextGenerationRole Role, string Content)
{
    private bool PrintMembers(StringBuilder builder) { builder.Append($"Role = {Role}, Length = {Content?.Length ?? 0}"); return true; }
}

/// <summary>The tier is resolved from a persisted agent; only the Infrastructure adapter maps it to a configured model.</summary>
public sealed record TextGenerationRequest(AiModelTier Tier, IReadOnlyList<TextGenerationMessage> Messages, int MaxOutputTokens)
{
    private bool PrintMembers(StringBuilder builder) { builder.Append($"Tier = {Tier}, MessageCount = {Messages?.Count ?? 0}, MaxOutputTokens = {MaxOutputTokens}"); return true; }
}

public sealed record TextGenerationUsage(long InputTokens, long OutputTokens);

public sealed record TextGenerationResult(string Text, TextGenerationUsage Usage)
{
    private bool PrintMembers(StringBuilder builder) { builder.Append($"Length = {Text?.Length ?? 0}, Usage = {Usage}"); return true; }
}

/// <summary>Provider-neutral, single-attempt text generation. Implementations belong in Infrastructure.</summary>
public interface ITextGenerator
{
    /// <summary>False for the explicitly disabled provider, so execution does not spend query embeddings.</summary>
    bool Enabled => true;
    Task<TextGenerationResult> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken);
}

public enum TextGenerationFailure { UNAVAILABLE, REJECTED_INPUT, INVALID_RESPONSE, MISCONFIGURED }

/// <summary>Contains no request, response payload, credentials or unsafe inner exception.</summary>
public sealed class TextGenerationException(TextGenerationFailure failure, TimeSpan? retryAfter = null)
    : Exception($"Text generation failed: {failure}.")
{
    public TextGenerationFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
