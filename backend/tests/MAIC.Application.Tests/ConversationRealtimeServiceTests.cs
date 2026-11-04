using MAIC.Application.Realtime;
using Xunit;

namespace MAIC.Application.Tests;

public sealed class ConversationRealtimeServiceTests
{
    [Theory]
    [InlineData("actor")]
    [InlineData("workspace")]
    [InlineData("conversation")]
    [InlineData("empty-candidate")]
    [InlineData("oversized-audience")]
    public async Task Invalid_or_unbounded_reads_do_not_reach_persistence(string invalid)
    {
        var store = new RecordingStore();
        var candidates = invalid == "oversized-audience"
            ? Enumerable.Range(0, ConversationRealtimeService.MaxCandidateUsers + 1).Select(_ => Guid.NewGuid()).ToArray()
            : invalid == "empty-candidate" ? [Guid.Empty] : Array.Empty<Guid>();
        var result = await new ConversationRealtimeService(store).ReadAsync(
            invalid == "actor" ? Guid.Empty : Guid.NewGuid(),
            invalid == "workspace" ? Guid.Empty : Guid.NewGuid(),
            invalid == "conversation" ? Guid.Empty : Guid.NewGuid(), candidates, TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public async Task Bounded_reads_preserve_actor_tenant_conversation_and_cancellation_for_authorization(int count)
    {
        var actor = Guid.NewGuid(); var workspace = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var candidates = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingStore();
        // A missing persisted membership remains a denial even with structurally valid inputs.
        var result = await new ConversationRealtimeService(store).ReadAsync(actor, workspace, conversation, candidates, cancellation.Token);
        Assert.Null(result);
        Assert.Equal(1, store.Calls);
        Assert.Equal((actor, workspace, conversation, cancellation.Token), store.Request);
        Assert.Same(candidates, store.Candidates);
    }

    private sealed class RecordingStore : IConversationRealtimeStore
    {
        public int Calls { get; private set; }
        public (Guid, Guid, Guid, CancellationToken) Request { get; private set; }
        public IReadOnlyCollection<Guid>? Candidates { get; private set; }
        public Task<RealtimeReadResult?> ReadAsync(Guid actorId, Guid workspaceId, Guid conversationId,
            IReadOnlyCollection<Guid> candidateUserIds, CancellationToken ct)
        {
            Calls++;
            Request = (actorId, workspaceId, conversationId, ct);
            Candidates = candidateUserIds;
            return Task.FromResult<RealtimeReadResult?>(null);
        }
    }
}
