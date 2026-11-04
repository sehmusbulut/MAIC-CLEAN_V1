using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using MAIC.API.Realtime;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed partial class RealtimeEndpointTests
{
    [Fact]
    public async Task Assignment_terminal_handoff_and_coalesced_lifecycle_emit_their_invalidation_categories()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        var time = new ControlledPollTimeProvider();
        using var factory = Factory(time);
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var socket = await Wire.ConnectAsync(factory, browser);
        await socket.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        var initial = await socket.FrameAsync();
        var path = $"/workspaces/{seed.Workspace.Id}/conversations/{seed.Conversation.Id}";

        using (var assignment = await browser.Client.PutAsJsonAsync(path + "/assignment",
                   new { assignedUserId = seed.Actor.Id }, Ct))
            Assert.Equal(HttpStatusCode.OK, assignment.StatusCode);
        await time.NextPollAsync();
        var assigned = await socket.FrameAsync();
        Assert.Contains("assignments", Changes(assigned));
        Assert.Equal(initial.GetProperty("lastAssignmentSequence").GetInt64() + 1,
            assigned.GetProperty("lastAssignmentSequence").GetInt64());

        Guid handoffId;
        using (var request = await browser.Client.PostAsJsonAsync(path + "/handoffs", new { clientRequestId = Guid.NewGuid() }, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, request.StatusCode);
            handoffId = (await request.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }
        await time.NextPollAsync();
        var pending = await socket.FrameAsync();
        Assert.Contains("handoffs", Changes(pending));
        Assert.Equal(assigned.GetProperty("lastHandoffSequence").GetInt64() + 1,
            pending.GetProperty("lastHandoffSequence").GetInt64());

        // Accepting as the current assignee changes only the handoff's terminal fingerprint.
        using (var accept = await browser.Client.PostAsJsonAsync(path + $"/handoffs/{handoffId}/accept", new { }, Ct))
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        await time.NextPollAsync();
        var terminal = await socket.FrameAsync();
        Assert.Contains("handoffs", Changes(terminal));
        foreach (var counter in new[] { "lastMessageSequence", "lastAssignmentSequence", "lastHandoffSequence", "revision" })
            Assert.Equal(pending.GetProperty(counter).GetInt64(), terminal.GetProperty(counter).GetInt64());

        // The poll is explicitly held until both commits: final status matches, revision does not.
        using (var resolve = await browser.Client.PostAsJsonAsync(path + "/resolve", new { }, Ct))
            Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        using (var reopen = await browser.Client.PostAsJsonAsync(path + "/reopen", new { }, Ct))
            Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
        await time.NextPollAsync();
        var reopened = await socket.FrameAsync();
        Assert.Contains("conversation", Changes(reopened));
        Assert.Equal(terminal.GetProperty("revision").GetInt64() + 2, reopened.GetProperty("revision").GetInt64());
        foreach (var frame in new[] { assigned, pending, terminal, reopened })
        {
            Assert.False(frame.GetProperty("resyncRequired").GetBoolean());
            AssertMetadata(frame, seed);
        }
    }

    // Gate only the between-observation delay; real query/watch/transport timeouts remain active.
    private sealed class ControlledPollTimeProvider : TimeProvider
    {
        private readonly Channel<PollTimer> polls = Channel.CreateUnbounded<PollTimer>(new() { SingleReader = true });

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != RealtimeOptions.PollInterval || period != Timeout.InfiniteTimeSpan)
                return System.CreateTimer(callback, state, dueTime, period);
            var timer = new PollTimer(callback, state);
            Assert.True(polls.Writer.TryWrite(timer));
            return timer;
        }

        public async Task NextPollAsync()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            (await polls.Reader.ReadAsync(deadline.Token)).Fire();
        }

        private sealed class PollTimer(TimerCallback callback, object? state) : ITimer
        {
            private int finished;
            public void Fire()
            {
                if (Interlocked.Exchange(ref finished, 1) == 0) callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref finished, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
