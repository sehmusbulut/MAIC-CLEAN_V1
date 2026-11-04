using System.Text.RegularExpressions;
using MAIC.Domain.Knowledge;

namespace MAIC.Domain.AI;

/// <summary>Provider-neutral capability tier; RAG-004 maps it to a concrete model behind an Application abstraction.</summary>
public enum AiModelTier
{
    FAST,
    BALANCED,
    QUALITY
}

/// <summary>ALL_SOURCES uses every enabled source of the workspace; SELECTED_SOURCES only the listed ones (if enabled).</summary>
public enum AiKnowledgeScope
{
    ALL_SOURCES,
    SELECTED_SOURCES
}

/// <summary>
/// A workspace-owned AI agent configuration: identity (name), behavior (instructions, language, model tier), the
/// knowledge it may answer from and when it hands the conversation to a human. It names no provider or model. Enabled
/// is always an explicit choice of an OWNER/ADMIN; there is no implicit default.
/// </summary>
public sealed partial class AiAgent
{
    public const int MaxNameLength = 200;
    public const int MaxInstructionsLength = 8000;
    public const int MaxLanguageLength = 35;
    public const int MaxSelectedSources = 100;
    public const int MaxAiRepliesLimit = 100;

    private readonly List<AiAgentKnowledgeSource> knowledgeSources = [];

    private AiAgent() { }

    public AiAgent(Guid workspaceId, AiAgentSettings settings, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        WorkspaceId = workspaceId;
        CreatedAt = KnowledgeClock.Truncate(now);
        Apply(settings, now);
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = null!;
    public bool Enabled { get; private set; }
    public string? Instructions { get; private set; }
    public string? Language { get; private set; }
    public AiModelTier ModelTier { get; private set; }
    public AiKnowledgeScope KnowledgeScope { get; private set; }
    public bool HandoffOnRequest { get; private set; }
    public bool HandoffWhenUnanswered { get; private set; }
    public int? MaxAiReplies { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyCollection<AiAgentKnowledgeSource> KnowledgeSources => knowledgeSources;

    /// <summary>Replaces the whole configuration after validating it; a rejected update changes nothing.</summary>
    public void Apply(AiAgentSettings settings, DateTimeOffset now)
    {
        if (!IsValid(settings)) throw new ArgumentException("AI agent settings are invalid.", nameof(settings));
        Name = settings.Name.Trim();
        Enabled = settings.Enabled;
        Instructions = settings.Instructions;
        Language = settings.Language;
        ModelTier = settings.ModelTier;
        KnowledgeScope = settings.KnowledgeScope;
        HandoffOnRequest = settings.HandoffOnRequest;
        HandoffWhenUnanswered = settings.HandoffWhenUnanswered;
        MaxAiReplies = settings.MaxAiReplies;
        var wanted = settings.KnowledgeSourceIds.ToHashSet();
        knowledgeSources.RemoveAll(s => !wanted.Contains(s.KnowledgeSourceId));
        foreach (var id in wanted.Except(knowledgeSources.Select(s => s.KnowledgeSourceId)).Order())
            knowledgeSources.Add(new AiAgentKnowledgeSource(WorkspaceId, Id, id));
        UpdatedAt = KnowledgeClock.Truncate(now);
        if (UpdatedAt < CreatedAt) UpdatedAt = CreatedAt;
    }

    public static bool IsValid(AiAgentSettings? settings) => settings is not null
        // Bound the list before any per-item work.
        && settings.KnowledgeSourceIds is { Count: <= MaxSelectedSources }
        && KnowledgeSource.IsValidName(settings.Name) && !ContainsHiddenCharacters(settings.Name)
        && Enum.IsDefined(settings.ModelTier)
        && Enum.IsDefined(settings.KnowledgeScope)
        && (settings.Instructions is null || IsValidInstructions(settings.Instructions))
        && (settings.Language is null || IsValidLanguage(settings.Language))
        && (settings.MaxAiReplies is null or (>= 1 and <= MaxAiRepliesLimit))
        && settings.KnowledgeSourceIds.All(id => id != Guid.Empty)
        && settings.KnowledgeSourceIds.Distinct().Count() == settings.KnowledgeSourceIds.Count
        && (settings.KnowledgeScope == AiKnowledgeScope.ALL_SOURCES
            ? settings.KnowledgeSourceIds.Count == 0
            : settings.KnowledgeSourceIds.Count is >= 1 and <= MaxSelectedSources);

    /// <summary>Same text rules as TEXT knowledge (exact, nonblank, tab/LF/CR only), bounded to 8000 UTF-16 units.</summary>
    public static bool IsValidInstructions(string instructions) =>
        instructions.Length <= MaxInstructionsLength && KnowledgeSource.IsValidTextContent(instructions)
        && !ContainsHiddenCharacters(instructions);

    /// <summary>
    /// Characters that make text render differently from what a model reads: bidi embeddings/overrides/isolates
    /// (U+202A–U+202E, U+2066–U+2069), invisible separators (U+200B, U+2060, U+FEFF) and tag characters
    /// (U+E0000–U+E007F, used to smuggle hidden prompt text). ZWJ/ZWNJ stay allowed for emoji and scripts that need them.
    /// Mirrored by CK_AiAgents_HiddenCharacters.
    /// </summary>
    public static bool ContainsHiddenCharacters(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200B' or '\u2060' or '\uFEFF') return true;
            // Tag characters are U+E0000–U+E007F: high surrogate U+DB40 followed by U+DC00–U+DC7F.
            if (c == '\uDB40' && i + 1 < text.Length && text[i + 1] is >= '\uDC00' and <= '\uDC7F') return true;
        }
        return false;
    }

    /// <summary>A BCP 47 style tag such as "tr", "en-US" or "zh-Hant-TW".</summary>
    public static bool IsValidLanguage(string language) => language.Length <= MaxLanguageLength && LanguageTag().IsMatch(language);

    public static bool TryParseModelTier(string? value, out AiModelTier tier)
    {
        (var valid, tier) = value switch
        {
            "FAST" => (true, AiModelTier.FAST),
            "BALANCED" => (true, AiModelTier.BALANCED),
            "QUALITY" => (true, AiModelTier.QUALITY),
            _ => (false, default)
        };
        return valid;
    }

    public static bool TryParseKnowledgeScope(string? value, out AiKnowledgeScope scope)
    {
        (var valid, scope) = value switch
        {
            "ALL_SOURCES" => (true, AiKnowledgeScope.ALL_SOURCES),
            "SELECTED_SOURCES" => (true, AiKnowledgeScope.SELECTED_SOURCES),
            _ => (false, default)
        };
        return valid;
    }

    // \z, not $: in .NET "$" also matches before a final newline, unlike PostgreSQL's CK_AiAgents_Language.
    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{1,8})*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTag();
}

/// <summary>The complete, replaceable configuration of an agent.</summary>
public sealed record AiAgentSettings(string Name, bool Enabled, string? Instructions, string? Language, AiModelTier ModelTier,
    AiKnowledgeScope KnowledgeScope, IReadOnlyList<Guid> KnowledgeSourceIds, bool HandoffOnRequest, bool HandoffWhenUnanswered,
    int? MaxAiReplies);

/// <summary>A knowledge source selected for an agent; both must belong to the same workspace.</summary>
public sealed class AiAgentKnowledgeSource
{
    private AiAgentKnowledgeSource() { }

    public AiAgentKnowledgeSource(Guid workspaceId, Guid aiAgentId, Guid knowledgeSourceId)
    {
        if (workspaceId == Guid.Empty || aiAgentId == Guid.Empty || knowledgeSourceId == Guid.Empty)
            throw new ArgumentException("Agent knowledge bindings are required.");
        WorkspaceId = workspaceId;
        AiAgentId = aiAgentId;
        KnowledgeSourceId = knowledgeSourceId;
    }

    public Guid WorkspaceId { get; private set; }
    public Guid AiAgentId { get; private set; }
    public Guid KnowledgeSourceId { get; private set; }
}
