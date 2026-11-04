using System.Runtime.CompilerServices;
using MAIC.API.Identity;
using MAIC.Application.Realtime;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MAIC.API.Realtime;

[Authorize(AuthenticationSchemes = IdentitySetup.SessionScheme)]
public sealed class ConversationRealtimeHub(RealtimeRegistry registry, RealtimeSessionGuard sessions,
    IServiceScopeFactory scopes, IAntiforgery antiforgery, TimeProvider time) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var session = await sessions.ReadAsync(Context.User);
        var connectionContext = Context;
        if (session is null || !registry.TryRegister(Context.ConnectionId, session, connectionContext.Abort))
        {
            Context.Abort();
            throw Denied();
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async IAsyncEnumerable<ConversationRealtimeFrame> Watch(Guid workspaceId, Guid conversationId,
        string csrfToken, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Context.ConnectionAborted);
        var ct = lifetime.Token;
        try
        {
            var connection = registry.Get(Context.ConnectionId) ?? throw Denied();
            var session = await RequireSessionAsync(connection);
            await ValidateCsrfAsync(session, csrfToken, ct);
            if (!registry.TryBeginWatch(Context.ConnectionId)) throw Denied();

            ConversationRealtimeSnapshot? previous = null;
            RealtimePresence[] previousPresence = [];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var candidates = registry.Candidates(workspaceId, conversationId)
                    .Append(connection).DistinctBy(candidate => candidate.ConnectionId).ToArray();
                var liveCandidates = await LiveCandidatesAsync(candidates);
                await RequireSessionAsync(connection);
                var result = await ReadAsync(connection.UserId, workspaceId, conversationId,
                    liveCandidates.Select(candidate => candidate.UserId).Distinct().ToArray(), ct);
                registry.PruneUnauthorized(liveCandidates, result.AuthorizedUserIds);

                // The first persisted authorization precedes creating any transient contribution.
                if (previous is null && !registry.TrySubscribe(Context.ConnectionId, workspaceId, conversationId))
                    throw Denied();

                // A database read may take time: expired/revoked tickets cannot survive into the frame.
                liveCandidates = await LiveCandidatesAsync(liveCandidates);
                await RequireSessionAsync(connection);
                if (registry.Get(Context.ConnectionId) is not { WorkspaceId: not null }) throw Denied();
                var presence = registry.Presence(workspaceId, conversationId,
                    liveCandidates.Select(candidate => candidate.ConnectionId).ToArray(), result.AuthorizedUserIds);
                var changes = Changes(previous, result.Snapshot, previousPresence, presence);
                if (previous is null || changes.Count > 0)
                {
                    var snapshot = result.Snapshot;
                    yield return new ConversationRealtimeFrame(workspaceId, conversationId, previous is null,
                        snapshot.LastMessageSequence, snapshot.LastAssignmentSequence, snapshot.LastHandoffSequence,
                        snapshot.Revision, changes, presence);
                    // The stream consumer resumes this iterator only after processing the yielded frame.
                    // Disconnect or uncertain delivery ends the watch; the next one always resynchronizes.
                    previous = snapshot;
                    previousPresence = presence;
                }
                await Task.Delay(RealtimeOptions.PollInterval, time, ct);
            }
        }
        finally
        {
            registry.Drop(Context.ConnectionId);
            Context.Abort();
        }
    }

    public Task Heartbeat() => UpdatePresenceAsync(null);

    public Task SetTyping(bool isTyping) => UpdatePresenceAsync(isTyping);

    private async Task UpdatePresenceAsync(bool? isTyping)
    {
        var connection = registry.Get(Context.ConnectionId);
        if (connection is not { WorkspaceId: { } workspaceId, ConversationId: { } conversationId })
        {
            registry.Drop(Context.ConnectionId);
            Context.Abort();
            throw Denied();
        }
        await RequireSessionAsync(connection);
        // Shared across all this user's connections and checked before any database work.
        if (!registry.TryConsumeMutation(Context.ConnectionId))
            throw new HubException("Realtime update rate exceeded.");
        try
        {
            await ReadAsync(connection.UserId, workspaceId, conversationId, [], Context.ConnectionAborted);
            await RequireSessionAsync(connection);
            if (!registry.Mutate(Context.ConnectionId, isTyping)) throw Denied();
        }
        catch
        {
            registry.Drop(Context.ConnectionId);
            Context.Abort();
            throw;
        }
    }

    private async Task<RealtimeSession> RequireSessionAsync(RealtimeConnection connection)
    {
        var session = await sessions.ReadAsync(connection.SessionKey, connection.UserId);
        if (session is not null) return session;
        registry.Drop(Context.ConnectionId);
        Context.Abort();
        throw Denied();
    }

    private async Task<RealtimeConnection[]> LiveCandidatesAsync(IEnumerable<RealtimeConnection> candidates)
    {
        var live = new List<RealtimeConnection>();
        foreach (var candidate in candidates)
        {
            if (await sessions.ReadAsync(candidate.SessionKey, candidate.UserId) is not null) live.Add(candidate);
            else registry.Drop(candidate.ConnectionId);
        }
        return live.ToArray();
    }

    private async Task<RealtimeReadResult> ReadAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        IReadOnlyCollection<Guid> candidates, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(RealtimeOptions.QueryDeadline, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        // Hub streams and invocations can overlap; each read owns a separate scoped DbContext.
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ConversationRealtimeService>()
            .ReadAsync(actorId, workspaceId, conversationId, candidates, bounded.Token) ?? throw Denied();
    }

    private async Task ValidateCsrfAsync(RealtimeSession session, string csrfToken, CancellationToken ct)
    {
        var original = Context.GetHttpContext();
        if (original is null || string.IsNullOrWhiteSpace(csrfToken) || csrfToken.Length > 4096) throw Denied();
        // Antiforgery caches validation on HttpContext. Isolate each Watch from the upgrade request
        // and bind the submitted token to the current live principal and the original CSRF cookie.
        await using var scope = scopes.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = session.Principal,
            RequestAborted = ct
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = original.Request.Scheme;
        context.Request.Host = original.Request.Host;
        context.Request.Path = original.Request.Path;
        context.Request.Headers.Cookie = original.Request.Headers.Cookie;
        context.Request.Headers["X-CSRF-TOKEN"] = csrfToken;
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { throw Denied(); }
    }

    private static List<string> Changes(ConversationRealtimeSnapshot? previous, ConversationRealtimeSnapshot current,
        RealtimePresence[] previousPresence, RealtimePresence[] presence)
    {
        if (previous is null) return ["conversation", "messages", "assignments", "handoffs", "presence"];
        var changes = new List<string>(5);
        if (previous.Revision != current.Revision || previous.Status != current.Status || previous.AiAgentId != current.AiAgentId)
            changes.Add("conversation");
        if (previous.LastMessageSequence != current.LastMessageSequence) changes.Add("messages");
        if (previous.LastAssignmentSequence != current.LastAssignmentSequence || previous.AssignedUserId != current.AssignedUserId)
            changes.Add("assignments");
        if (previous.LastHandoffSequence != current.LastHandoffSequence || previous.LatestHandoff != current.LatestHandoff)
            changes.Add("handoffs");
        if (!previousPresence.SequenceEqual(presence)) changes.Add("presence");
        return changes;
    }

    private static HubException Denied() => new("Realtime subscription is unavailable.");
}
