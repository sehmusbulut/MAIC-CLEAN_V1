using MAIC.Domain.AI;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class AiAgentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static AiAgentSettings Settings(AiKnowledgeScope scope = AiKnowledgeScope.ALL_SOURCES, IReadOnlyList<Guid>? sources = null,
        string name = "Support bot", string? instructions = "Be concise.", string? language = "tr", int? maxAiReplies = 5) =>
        new(name, false, instructions, language, AiModelTier.BALANCED, scope, sources ?? [], true, true, maxAiReplies);

    [Fact]
    public void New_agents_start_from_the_given_settings_and_updates_replace_everything()
    {
        var ws = Guid.CreateVersion7();
        var (a, b, c) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var agent = new AiAgent(ws, Settings(AiKnowledgeScope.SELECTED_SOURCES, [b, a], name: "  Bot  "), Now);
        Assert.Equal(('7', ws, "Bot", false, Now, Now), (agent.Id.ToString()[14], agent.WorkspaceId, agent.Name, agent.Enabled, agent.CreatedAt, agent.UpdatedAt));
        Assert.Equal(new[] { a, b }.Order(), agent.KnowledgeSources.Select(s => s.KnowledgeSourceId).Order());
        Assert.All(agent.KnowledgeSources, s => Assert.Equal((ws, agent.Id), (s.WorkspaceId, s.AiAgentId)));

        agent.Apply(Settings(AiKnowledgeScope.SELECTED_SOURCES, [b, c], instructions: null, language: null, maxAiReplies: null) with
            { Enabled = true, ModelTier = AiModelTier.QUALITY, HandoffOnRequest = false }, Now.AddMinutes(1));
        Assert.Equal((true, (string?)null, (string?)null, AiModelTier.QUALITY, false, (int?)null, Now.AddMinutes(1)),
            (agent.Enabled, agent.Instructions, agent.Language, agent.ModelTier, agent.HandoffOnRequest, agent.MaxAiReplies, agent.UpdatedAt));
        Assert.Equal(new[] { b, c }.Order(), agent.KnowledgeSources.Select(s => s.KnowledgeSourceId).Order());

        agent.Apply(Settings(), Now.AddMinutes(-5)); // clock skew never makes UpdatedAt earlier than CreatedAt
        Assert.Empty(agent.KnowledgeSources);
        Assert.Equal(Now, agent.UpdatedAt);
    }

    [Fact]
    public void Invalid_settings_are_rejected_and_leave_the_agent_unchanged()
    {
        var agent = new AiAgent(Guid.CreateVersion7(), Settings(), Now);
        var source = Guid.CreateVersion7();
        foreach (var invalid in new[]
                 {
                     Settings(name: " "), Settings(name: new string('x', 201)), Settings(instructions: " \n"),
                     Settings(instructions: new string('x', 8001)), Settings(instructions: "a\u0000b"), Settings(language: "TR"),
                     Settings(language: "t"), Settings(language: "en_US"), Settings(language: "en\n"), Settings(language: "en-US\n"), Settings(language: "en-" + new string('a', 40)),
                     Settings(maxAiReplies: 0), Settings(maxAiReplies: 101),
                     Settings(AiKnowledgeScope.ALL_SOURCES, [source]), Settings(AiKnowledgeScope.SELECTED_SOURCES, []),
                     Settings(AiKnowledgeScope.SELECTED_SOURCES, [source, source]), Settings(AiKnowledgeScope.SELECTED_SOURCES, [Guid.Empty]),
                     Settings(AiKnowledgeScope.SELECTED_SOURCES, Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray()),
                     Settings() with { ModelTier = (AiModelTier)9 }, Settings() with { KnowledgeScope = (AiKnowledgeScope)9 },
                     // Hidden text: bidi controls, invisible separators and tag characters (ASCII smuggling).
                     Settings(name: "Bot\u202E"), Settings(name: "\u200B"), Settings(name: "a\u2066b"), Settings(name: "a\uFEFF"),
                     Settings(instructions: "Visible\uDB40\uDC69\uDB40\uDC67"), Settings(instructions: "a\u2060b"), Settings(instructions: "a\u202Ab")
                 })
        {
            Assert.False(AiAgent.IsValid(invalid));
            Assert.Throws<ArgumentException>(() => agent.Apply(invalid, Now.AddMinutes(1)));
        }
        Assert.Equal(("Support bot", "Be concise.", Now), (agent.Name, agent.Instructions, agent.UpdatedAt));
        Assert.Throws<ArgumentException>(() => new AiAgent(Guid.Empty, Settings(), Now));
        Assert.True(AiAgent.IsValid(Settings(language: "zh-Hant-TW", maxAiReplies: 100, instructions: new string('x', 8000))));
        // ZWJ/ZWNJ remain valid (emoji sequences, Persian and Indic scripts).
        Assert.True(AiAgent.IsValid(Settings(name: "Destek \ud83d\udc68\u200d\ud83d\udcbb", instructions: "\u0645\u06cc\u200c\u062e\u0648\u0627\u0647\u0645")));
        Assert.True(AiAgent.IsValid(Settings(AiKnowledgeScope.SELECTED_SOURCES,
            Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray())));
    }

    [Theory]
    [InlineData("FAST", true)]
    [InlineData("QUALITY", true)]
    [InlineData("fast", false)]
    [InlineData("1", false)]
    [InlineData(null, false)]
    public void Tier_names_are_exact(string? value, bool valid) => Assert.Equal(valid, AiAgent.TryParseModelTier(value, out _));

    [Theory]
    [InlineData("ALL_SOURCES", true)]
    [InlineData("SELECTED_SOURCES", true)]
    [InlineData("all_sources", false)]
    [InlineData("0", false)]
    public void Scope_names_are_exact(string value, bool valid) => Assert.Equal(valid, AiAgent.TryParseKnowledgeScope(value, out _));
}
