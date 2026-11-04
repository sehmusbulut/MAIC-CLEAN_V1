using System.Net;
using System.Net.Http.Json;
using System.Text;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class HandoffEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static object Empty => new { };

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
        builder.ConfigureServices(services => services.AddAuthentication("test-only")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test-only", _ => { }));
    });

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    private async Task<Guid> UserAsync()
    {
        await using var db = fixture.CreateContext();
        var user = new User();
        db.Users.Add(user);
        await db.SaveChangesAsync(Cancellation);
        return user.Id;
    }

    private async Task<Seed> SeedAsync(Guid owner, params (Guid UserId, WorkspaceRole Role)[] members)
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("Handoff HTTP tests");
        var channel = new Channel(workspace.Id, "Website");
        var contact = new Contact(workspace.Id, "Customer", []);
        db.Workspaces.Add(workspace);
        db.Channels.Add(channel);
        db.Contacts.Add(contact);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, owner, WorkspaceRole.OWNER));
        db.WorkspaceMembers.AddRange(members.Select(m => new WorkspaceMember(workspace.Id, m.UserId, m.Role)));
        await db.SaveChangesAsync(Cancellation);
        return new(workspace.Id, channel.Id, contact.Id);
    }

    private static async Task AuthenticateAsync(HttpClient client, Guid user)
    {
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", user.ToString());
        var csrf = await client.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
    }

    private static string Path(Guid workspace, Guid conversation) => $"/workspaces/{workspace}/conversations/{conversation}";

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.StatusCode == expected,
                $"Expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancellation)}");
            if (response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/conversations", StringComparison.OrdinalIgnoreCase) == true)
                Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            Assert.True(response.StatusCode == expected,
                $"Expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancellation)}");
            Assert.True(response.Headers.CacheControl?.NoStore);
            return (await response.Content.ReadFromJsonAsync<T>(Cancellation))!;
        }
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path) =>
        await ReadAsync<T>(await client.GetAsync(path, Cancellation));

    private static async Task<ConversationResponse> CreateAsync(HttpClient client, Seed seed) =>
        await ReadAsync<ConversationResponse>(await client.PostAsJsonAsync($"/workspaces/{seed.WorkspaceId}/conversations",
            new { seed.ChannelId, seed.ContactId, clientRequestId = Guid.NewGuid() }, Cancellation), HttpStatusCode.Created);

    private static async Task<ConversationResponse> AssignAsync(HttpClient client, string path, Guid? assignee) =>
        await ReadAsync<ConversationResponse>(await client.PutAsJsonAsync(path + "/assignment", new { assignedUserId = assignee }, Cancellation));

    private static async Task<HandoffResponse> RequestAsync(HttpClient client, string path, Guid? key = null,
        HttpStatusCode status = HttpStatusCode.Created)
    {
        var requestKey = key ?? Guid.NewGuid();
        var handoff = await ReadAsync<HandoffResponse>(await client.PostAsJsonAsync(path + "/handoffs",
            new { clientRequestId = requestKey }, Cancellation), status);
        Assert.Equal(requestKey, handoff.ClientRequestId);
        Assert.Equal('7', handoff.Id.ToString()[14]);
        var persisted = Assert.Single((await GetAsync<Page<HandoffResponse>>(client,
            path + $"/handoffs?afterSequence={handoff.Sequence - 1}&take=1")).Items);
        Assert.InRange((handoff.CreatedAt - persisted.CreatedAt).Ticks, -9, 9);
        Assert.Equal(handoff with { CreatedAt = persisted.CreatedAt }, persisted);
        return persisted;
    }

    private static async Task<HandoffResponse> AcceptAsync(HttpClient client, string path, Guid handoff) =>
        await ReadAsync<HandoffResponse>(await client.PostAsJsonAsync(path + $"/handoffs/{handoff}/accept", Empty, Cancellation));

    private static async Task<ConversationResponse> StatusAsync(HttpClient client, string path, string action) =>
        await ReadAsync<ConversationResponse>(await client.PostAsJsonAsync(path + "/" + action, Empty, Cancellation));

    [Fact]
    public async Task All_member_roles_assign_unassign_and_read_durable_history_without_duplicate_noops()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var admin = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (admin, WorkspaceRole.ADMIN), (agent, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, seed);
        var path = Path(seed.WorkspaceId, conversation.Id);
        Assert.Null(conversation.AssignedUserId);
        Assert.Empty((await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items);
        Assert.Empty((await GetAsync<Page<HandoffResponse>>(client, path + "/handoffs")).Items);
        var count = 0;

        foreach (var actor in new[] { owner, admin, agent })
        {
            await AuthenticateAsync(client, actor);
            Assert.Equal(actor, (await AssignAsync(client, path, actor)).AssignedUserId);
            Assert.Equal(actor, (await AssignAsync(client, path, actor)).AssignedUserId);
            var assigned = (await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items;
            Assert.Equal(++count, assigned.Length);
            var entry = assigned[^1];
            Assert.Equal('7', entry.Id.ToString()[14]);
            Assert.Equal(seed.WorkspaceId, entry.WorkspaceId);
            Assert.Equal(conversation.Id, entry.ConversationId);
            Assert.Null(entry.PreviousAssignedUserId);
            Assert.Equal(actor, entry.AssignedUserId);
            Assert.Equal(actor, entry.ActorUserId);
            Assert.Equal("MANUAL", entry.Reason);
            Assert.Equal(count, entry.Sequence);
            Assert.Null((await AssignAsync(client, path, null)).AssignedUserId);
            Assert.Null((await AssignAsync(client, path, null)).AssignedUserId);
            var cleared = (await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items;
            Assert.Equal(++count, cleared.Length);
            Assert.Equal(actor, cleared[^1].PreviousAssignedUserId);
            Assert.Null(cleared[^1].AssignedUserId);
            Assert.Equal(actor, cleared[^1].ActorUserId);

            var ownConversation = await CreateAsync(client, seed);
            var ownPath = Path(seed.WorkspaceId, ownConversation.Id);
            var ownRequest = await RequestAsync(client, ownPath);
            Assert.Equal(actor, ownRequest.RequesterUserId);
            Assert.Equal(actor, (await AcceptAsync(client, ownPath, ownRequest.Id)).TerminalActorUserId);
            Assert.Equal("RESOLVED", (await StatusAsync(client, ownPath, "resolve")).Status);
            Assert.Equal("OPEN", (await StatusAsync(client, ownPath, "reopen")).Status);
        }

        Assert.Equal("RESOLVED", (await StatusAsync(client, path, "resolve")).Status);
        Assert.Equal(admin, (await AssignAsync(client, path, admin)).AssignedUserId);
        var resolved = await GetAsync<ConversationResponse>(client, path);
        Assert.Equal("RESOLVED", resolved.Status);
        Assert.Equal(admin, resolved.AssignedUserId);
        Assert.Equal(owner, resolved.CreatedByUserId);
        Assert.Equal(conversation.ChannelId, resolved.ChannelId);
        Assert.Equal(conversation.ContactId, resolved.ContactId);
    }

    [Fact]
    public async Task Handoff_replays_follow_current_membership_and_terminal_acceptance_never_restores_assignment()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (agent, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, seed);
        var path = Path(seed.WorkspaceId, conversation.Id);
        var request = await RequestAsync(client, path);
        Assert.Equal("PENDING", request.Status);
        Assert.Equal(owner, request.RequesterUserId);
        Assert.Null(request.TerminalActorUserId);
        Assert.Null(request.TerminalAt);
        Assert.Null(request.TerminalReason);
        Assert.Equal(request, await RequestAsync(client, path, request.ClientRequestId, HttpStatusCode.OK));
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(path + "/handoffs",
            new { clientRequestId = Guid.NewGuid() }, Cancellation));
        await AuthenticateAsync(client, agent);
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(path + "/handoffs",
            new { clientRequestId = request.ClientRequestId }, Cancellation));
        var accepted = await AcceptAsync(client, path, request.Id);
        Assert.Equal("ACCEPTED", accepted.Status);
        Assert.Equal(agent, accepted.TerminalActorUserId);
        Assert.Equal("ACCEPTED", accepted.TerminalReason);
        Assert.NotNull(accepted.TerminalAt);
        Assert.Equal(agent, (await GetAsync<ConversationResponse>(client, path)).AssignedUserId);
        var history = Assert.Single((await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items);
        Assert.Equal(agent, history.AssignedUserId);
        Assert.Equal(agent, history.ActorUserId);
        Assert.Equal("HANDOFF_ACCEPTED", history.Reason);
        var persisted = Assert.Single((await GetAsync<Page<HandoffResponse>>(client, path + "/handoffs")).Items);
        Assert.Equal(persisted, await AcceptAsync(client, path, request.Id));

        await AuthenticateAsync(client, owner);
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(path + $"/handoffs/{request.Id}/accept", Empty, Cancellation));
        Assert.Equal(persisted, await RequestAsync(client, path, request.ClientRequestId, HttpStatusCode.OK));
        await AssignAsync(client, path, owner);
        await AuthenticateAsync(client, agent);
        Assert.Equal(persisted, await AcceptAsync(client, path, request.Id));
        Assert.Equal(owner, (await GetAsync<ConversationResponse>(client, path)).AssignedUserId);
        await AuthenticateAsync(client, owner);
        await AssignAsync(client, path, null);
        await StatusAsync(client, path, "resolve");
        await AuthenticateAsync(client, agent);
        Assert.Equal(persisted, await AcceptAsync(client, path, request.Id));
        var current = await GetAsync<ConversationResponse>(client, path);
        Assert.Equal("RESOLVED", current.Status);
        Assert.Null(current.AssignedUserId);
        Assert.Equal(3, (await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items.Length);

        // Identical request keys from another actor create independent requests once none is pending.
        await StatusAsync(client, path, "reopen");
        var ownRequest = await RequestAsync(client, path, request.ClientRequestId);
        Assert.NotEqual(request.Id, ownRequest.Id);
        Assert.Equal(agent, ownRequest.RequesterUserId);
        Assert.Equal(2, ownRequest.Sequence);
        await AuthenticateAsync(client, owner);
        await AssertStatusAsync(HttpStatusCode.NoContent, await client.DeleteAsync($"/workspaces/{seed.WorkspaceId}/members/{agent}", Cancellation));
        await AuthenticateAsync(client, agent);
        await AssertAllRoutesAsync(client, path, request.Id, ownRequest.ClientRequestId, agent, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolve_and_manual_assignment_cancel_pending_handoffs_and_preserve_message_replays()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (agent, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, seed);
        var path = Path(seed.WorkspaceId, conversation.Id);
        var messageKey = Guid.NewGuid();
        var messageBody = new { clientMessageId = messageKey, content = "Before resolving" };
        var originalMessage = await ReadAsync<MessageResponse>(await client.PostAsJsonAsync(path + "/messages", messageBody, Cancellation), HttpStatusCode.Created);
        var pending = await RequestAsync(client, path);
        await AssignAsync(client, path, null);
        var cancelled = await RequestAsync(client, path, pending.ClientRequestId, HttpStatusCode.OK);
        Assert.Equal("CANCELLED", cancelled.Status);
        Assert.Equal(owner, cancelled.TerminalActorUserId);
        Assert.Equal("MANUAL_ASSIGNMENT", cancelled.TerminalReason);
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(path + $"/handoffs/{pending.Id}/accept", Empty, Cancellation));
        await AssignAsync(client, path, agent);
        var toResolve = await RequestAsync(client, path);
        var resolved = await StatusAsync(client, path, "resolve");
        Assert.Equal("RESOLVED", resolved.Status);
        Assert.Equal(agent, resolved.AssignedUserId);
        Assert.Equal(resolved, await StatusAsync(client, path, "resolve"));
        var terminal = await RequestAsync(client, path, toResolve.ClientRequestId, HttpStatusCode.OK);
        Assert.Equal("CANCELLED", terminal.Status);
        Assert.Equal("RESOLVED", terminal.TerminalReason);
        Assert.Equal(owner, terminal.TerminalActorUserId);
        Assert.NotNull(terminal.TerminalAt);
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(path + $"/handoffs/{toResolve.Id}/accept", Empty, Cancellation));
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(path + "/handoffs", new { clientRequestId = Guid.NewGuid() }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(path + "/messages",
            new { clientMessageId = Guid.NewGuid(), content = "Blocked while resolved" }, Cancellation));
        var messageReplay = await ReadAsync<MessageResponse>(await client.PostAsJsonAsync(path + "/messages", messageBody, Cancellation));
        Assert.Equal(originalMessage.Id, messageReplay.Id);
        Assert.Equal(1, messageReplay.Sequence);
        Assert.Single((await GetAsync<Page<MessageResponse>>(client, path + "/messages")).Items);
        var assignmentCount = (await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items.Length;
        var reopened = await StatusAsync(client, path, "reopen");
        Assert.Equal("OPEN", reopened.Status);
        Assert.Equal(agent, reopened.AssignedUserId);
        Assert.Equal(reopened, await StatusAsync(client, path, "reopen"));
        Assert.Equal(assignmentCount, (await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items.Length);
        Assert.Equal(2, (await GetAsync<Page<HandoffResponse>>(client, path + "/handoffs")).Items.Length);
        var freshMessage = await ReadAsync<MessageResponse>(await client.PostAsJsonAsync(path + "/messages",
            new { clientMessageId = Guid.NewGuid(), content = "Allowed after reopen" }, Cancellation), HttpStatusCode.Created);
        Assert.Equal(2, freshMessage.Sequence);

        // Disabling the channel does not disable workflow management.
        await AssertStatusAsync(HttpStatusCode.OK, await client.PutAsJsonAsync($"/workspaces/{seed.WorkspaceId}/channels/{seed.ChannelId}",
            new { enabled = false }, Cancellation));
        await AssignAsync(client, path, null);
        var disabledRequest = await RequestAsync(client, path);
        await AuthenticateAsync(client, agent);
        Assert.Equal(agent, (await AcceptAsync(client, path, disabledRequest.Id)).TerminalActorUserId);
        await StatusAsync(client, path, "resolve");
        await StatusAsync(client, path, "reopen");
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(path + "/messages",
            new { clientMessageId = Guid.NewGuid(), content = "Channel still disabled" }, Cancellation));
        Assert.Equal(2, (await GetAsync<Page<MessageResponse>>(client, path + "/messages")).Items.Length);
    }

    [Fact]
    public async Task Cross_tenant_targets_conversations_handoffs_and_nonmembers_remain_hidden()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var first = await SeedAsync(owner);
        var other = await SeedAsync(outsider, (owner, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, first);
        var otherConversation = await CreateAsync(client, other);
        var sibling = await CreateAsync(client, first);
        var path = Path(first.WorkspaceId, conversation.Id);
        var request = await RequestAsync(client, path);
        await AssertAllRoutesAsync(client, Path(other.WorkspaceId, conversation.Id), request.Id, request.ClientRequestId, owner, HttpStatusCode.NotFound);
        await AssertAllRoutesAsync(client, Path(first.WorkspaceId, Guid.NewGuid()), request.Id, request.ClientRequestId, owner, HttpStatusCode.NotFound);
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(path + "/assignment", new { assignedUserId = outsider }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(path + "/assignment", new { assignedUserId = Guid.NewGuid() }, Cancellation));
        foreach (var target in new[] { Path(other.WorkspaceId, otherConversation.Id), Path(first.WorkspaceId, sibling.Id) })
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(target + $"/handoffs/{request.Id}/accept", Empty, Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(path + $"/handoffs/{Guid.NewGuid()}/accept", Empty, Cancellation));
        Assert.Empty((await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items);
        Assert.Equal("PENDING", Assert.Single((await GetAsync<Page<HandoffResponse>>(client, path + "/handoffs")).Items).Status);

        await AuthenticateAsync(client, outsider);
        await AssertAllRoutesAsync(client, path, request.Id, request.ClientRequestId, outsider, HttpStatusCode.NotFound);
        await AssertAllRoutesAsync(client, Path(Guid.NewGuid(), conversation.Id), request.Id, request.ClientRequestId, outsider, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Removal_and_leave_clear_all_current_assignments_preserve_history_and_never_restore_after_rejoin()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (agent, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var first = await CreateAsync(client, seed);
        var second = await CreateAsync(client, seed);
        var firstPath = Path(seed.WorkspaceId, first.Id);
        var secondPath = Path(seed.WorkspaceId, second.Id);
        await AssignAsync(client, firstPath, agent);
        await AssignAsync(client, secondPath, agent);
        await AuthenticateAsync(client, agent);
        var pending = await RequestAsync(client, firstPath);
        await AuthenticateAsync(client, owner);
        await AssertStatusAsync(HttpStatusCode.NoContent, await client.DeleteAsync($"/workspaces/{seed.WorkspaceId}/members/{agent}", Cancellation));
        foreach (var path in new[] { firstPath, secondPath })
        {
            Assert.Null((await GetAsync<ConversationResponse>(client, path)).AssignedUserId);
            var removed = (await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items;
            Assert.Equal(2, removed.Length);
            Assert.Equal(agent, removed[^1].PreviousAssignedUserId);
            Assert.Null(removed[^1].AssignedUserId);
            Assert.Equal(owner, removed[^1].ActorUserId);
            Assert.Equal("MEMBER_REMOVED", removed[^1].Reason);
        }
        Assert.Equal(pending, Assert.Single((await GetAsync<Page<HandoffResponse>>(client, firstPath + "/handoffs")).Items));
        await AuthenticateAsync(client, agent);
        await AssertAllRoutesAsync(client, firstPath, pending.Id, pending.ClientRequestId, agent, HttpStatusCode.NotFound);
        await AuthenticateAsync(client, owner);
        Assert.Equal(owner, (await AcceptAsync(client, firstPath, pending.Id)).TerminalActorUserId);
        var invitation = await ReadAsync<InvitationResponse>(await client.PostAsJsonAsync($"/workspaces/{seed.WorkspaceId}/invitations",
            new { recipientUserId = agent, role = "AGENT" }, Cancellation), HttpStatusCode.Created);
        await AuthenticateAsync(client, agent);
        await AssertStatusAsync(HttpStatusCode.NoContent, await client.PostAsync($"/workspaces/{seed.WorkspaceId}/invitations/{invitation.Id}/accept", null, Cancellation));
        Assert.Equal(owner, (await GetAsync<ConversationResponse>(client, firstPath)).AssignedUserId);
        Assert.Null((await GetAsync<ConversationResponse>(client, secondPath)).AssignedUserId);
        Assert.Equal("ACCEPTED", (await RequestAsync(client, firstPath, pending.ClientRequestId, HttpStatusCode.OK)).Status);
        await AssignAsync(client, secondPath, agent);
        await AssertStatusAsync(HttpStatusCode.NoContent, await client.PostAsync($"/workspaces/{seed.WorkspaceId}/leave", null, Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(secondPath + "/assignments", Cancellation));
        await AuthenticateAsync(client, owner);
        Assert.Null((await GetAsync<ConversationResponse>(client, secondPath)).AssignedUserId);
        var leave = (await GetAsync<Page<AssignmentResponse>>(client, secondPath + "/assignments")).Items[^1];
        Assert.Equal(agent, leave.ActorUserId);
        Assert.Equal(agent, leave.PreviousAssignedUserId);
        Assert.Equal("MEMBER_REMOVED", leave.Reason);

        await AssignAsync(client, secondPath, owner);
        var before = (await GetAsync<Page<AssignmentResponse>>(client, secondPath + "/assignments")).Items;
        var ownerPending = await RequestAsync(client, secondPath);
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsync($"/workspaces/{seed.WorkspaceId}/leave", null, Cancellation));
        Assert.Equal(owner, (await GetAsync<ConversationResponse>(client, secondPath)).AssignedUserId);
        Assert.Equal(before, (await GetAsync<Page<AssignmentResponse>>(client, secondPath + "/assignments")).Items);
        Assert.Equal(ownerPending, Assert.Single((await GetAsync<Page<HandoffResponse>>(client, secondPath + "/handoffs")).Items));
    }

    [Fact]
    public async Task Workflow_routes_require_authentication_bound_csrf_and_strict_present_request_bodies()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (agent, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, seed);
        var path = Path(seed.WorkspaceId, conversation.Id);
        var pending = await RequestAsync(client, path);
        using var anonymous = Client(factory);
        await AssertAllRoutesAsync(anonymous, path, pending.Id, pending.ClientRequestId, owner, HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AssertWritesAsync(client, path, pending.Id, pending.ClientRequestId, owner, HttpStatusCode.BadRequest);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "not-a-token");
        await AssertWritesAsync(client, path, pending.Id, pending.ClientRequestId, owner, HttpStatusCode.BadRequest);
        await AuthenticateAsync(client, owner);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", agent.ToString());
        await AssertWritesAsync(client, path, pending.Id, pending.ClientRequestId, owner, HttpStatusCode.BadRequest);
        await AuthenticateAsync(client, owner);

        foreach (var field in new[] { "workspaceId", "conversationId", "actorUserId", "requesterUserId", "senderUserId", "status", "sequence", "terminalActorUserId" })
        {
            var assignment = new Dictionary<string, object?> { ["assignedUserId"] = agent, [field] = Guid.NewGuid() };
            var handoff = new Dictionary<string, object> { ["clientRequestId"] = Guid.NewGuid(), [field] = Guid.NewGuid() };
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(path + "/assignment", assignment, Cancellation));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(path + "/handoffs", handoff, Cancellation));
        }
        foreach (var action in new[] { $"handoffs/{pending.Id}/accept", "resolve", "reopen" })
        {
            foreach (var field in new[] { "assignedUserId", "actorUserId", "workspaceId", "status", "clientRequestId" })
                await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(path + "/" + action,
                    new Dictionary<string, object> { [field] = Guid.NewGuid() }, Cancellation));
            foreach (var malformed in new[] { "null", "[]", "false" })
            {
                using var body = new StringContent(malformed, Encoding.UTF8, "application/json");
                await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsync(path + "/" + action, body, Cancellation));
            }
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsync(path + "/" + action, null, Cancellation));
        }
        foreach (var malformed in new[] { "{}", "null", "[]", "{\"assignedUserId\":17}", "{\"assignedUserId\":\"bad-uuid\"}", $"{{\"assignedUserId\":\"{Guid.Empty}\"}}" })
        {
            using var body = new StringContent(malformed, Encoding.UTF8, "application/json");
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsync(path + "/assignment", body, Cancellation));
        }
        foreach (var body in new object[] { Empty, new { clientRequestId = Guid.Empty }, new { clientRequestId = (string?)null } })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(path + "/handoffs", body, Cancellation));
        var unchanged = await GetAsync<ConversationResponse>(client, path);
        Assert.Null(unchanged.AssignedUserId);
        Assert.Equal("OPEN", unchanged.Status);
        Assert.Empty((await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items);
        Assert.Equal(pending, Assert.Single((await GetAsync<Page<HandoffResponse>>(client, path + "/handoffs")).Items));
        await GetAsync<Page<AssignmentResponse>>(client, path.ToUpperInvariant() + "/ASSIGNMENTS");
        await AuthenticateAsync(client, Guid.NewGuid());
        await AssertAllRoutesAsync(client, path, pending.Id, pending.ClientRequestId, owner, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Assignment_and_handoff_histories_use_exclusive_cursor_pages_with_validated_bounds()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (agent, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, seed);
        var path = Path(seed.WorkspaceId, conversation.Id);
        for (var index = 0; index < 5; index++)
        {
            await RequestAsync(client, path);
            await AssignAsync(client, path, index % 2 == 0 ? agent : owner);
        }
        var assignments = (await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments")).Items;
        var handoffs = (await GetAsync<Page<HandoffResponse>>(client, path + "/handoffs")).Items;
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, assignments.Select(a => a.Sequence));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, handoffs.Select(h => h.Sequence));
        Assert.All(handoffs, h => Assert.Equal("CANCELLED", h.Status));
        await AssertPagesAsync(client, path + "/assignments", assignments);
        await AssertPagesAsync(client, path + "/handoffs", handoffs);
        foreach (var history in new[] { "assignments", "handoffs" })
            foreach (var query in new[] { "take=0", "take=-1", "take=101", "take=abc", "afterSequence=-1", "afterSequence=abc", "afterSequence=9223372036854775808" })
                await AssertStatusAsync(HttpStatusCode.BadRequest, await client.GetAsync(path + "/" + history + "?" + query, Cancellation));
        var sixth = await RequestAsync(client, path);
        await AssignAsync(client, path, owner);
        Assert.Equal(sixth.Id, Assert.Single((await GetAsync<Page<HandoffResponse>>(client, path + "/handoffs?afterSequence=5&take=1")).Items).Id);
        Assert.Equal(6, Assert.Single((await GetAsync<Page<AssignmentResponse>>(client, path + "/assignments?afterSequence=5&take=1")).Items).Sequence);
    }

    private static async Task AssertPagesAsync<T>(HttpClient client, string path, T[] expected)
    {
        var first = await GetAsync<Page<T>>(client, path + "?afterSequence=0&take=2");
        Assert.Equal(expected.Take(2), first.Items);
        Assert.Equal(2, first.NextAfterSequence);
        var second = await GetAsync<Page<T>>(client, path + $"?afterSequence={first.NextAfterSequence}&take=2");
        Assert.Equal(expected.Skip(2).Take(2), second.Items);
        Assert.Equal(4, second.NextAfterSequence);
        var last = await GetAsync<Page<T>>(client, path + $"?afterSequence={second.NextAfterSequence}&take=2");
        Assert.Equal(expected.Skip(4), last.Items);
        Assert.Null(last.NextAfterSequence);
        var exact = await GetAsync<Page<T>>(client, path + "?afterSequence=3&take=2");
        Assert.Equal(expected.Skip(3), exact.Items);
        Assert.Null(exact.NextAfterSequence);
        var empty = await GetAsync<Page<T>>(client, path + "?afterSequence=5&take=100");
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextAfterSequence);
    }

    private static async Task AssertAllRoutesAsync(HttpClient client, string path, Guid handoff, Guid key, Guid assignee, HttpStatusCode expected)
    {
        await AssertStatusAsync(expected, await client.GetAsync(path + "/assignments", Cancellation));
        await AssertStatusAsync(expected, await client.GetAsync(path + "/handoffs", Cancellation));
        await AssertWritesAsync(client, path, handoff, key, assignee, expected);
    }

    private static async Task AssertWritesAsync(HttpClient client, string path, Guid handoff, Guid key, Guid assignee, HttpStatusCode expected)
    {
        await AssertStatusAsync(expected, await client.PutAsJsonAsync(path + "/assignment", new { assignedUserId = assignee }, Cancellation));
        await AssertStatusAsync(expected, await client.PostAsJsonAsync(path + "/handoffs", new { clientRequestId = key }, Cancellation));
        await AssertStatusAsync(expected, await client.PostAsJsonAsync(path + $"/handoffs/{handoff}/accept", Empty, Cancellation));
        await AssertStatusAsync(expected, await client.PostAsJsonAsync(path + "/resolve", Empty, Cancellation));
        await AssertStatusAsync(expected, await client.PostAsJsonAsync(path + "/reopen", Empty, Cancellation));
    }

    private sealed record Csrf(string Token);
    private sealed record Seed(Guid WorkspaceId, Guid ChannelId, Guid ContactId);
    private sealed record ConversationResponse(Guid Id, Guid WorkspaceId, Guid ChannelId, Guid ContactId,
        Guid CreatedByUserId, Guid ClientRequestId, string Status, DateTimeOffset CreatedAt, Guid? AssignedUserId);
    private sealed record AssignmentResponse(Guid Id, Guid WorkspaceId, Guid ConversationId, long Sequence,
        Guid? PreviousAssignedUserId, Guid? AssignedUserId, Guid ActorUserId, string Reason, DateTimeOffset CreatedAt);
    private sealed record HandoffResponse(Guid Id, Guid WorkspaceId, Guid ConversationId, long Sequence,
        Guid RequesterUserId, Guid ClientRequestId, string Status, DateTimeOffset CreatedAt,
        Guid? TerminalActorUserId, DateTimeOffset? TerminalAt, string? TerminalReason);
    private sealed record MessageResponse(Guid Id, Guid WorkspaceId, Guid ConversationId, Guid SenderUserId,
        Guid ClientMessageId, long Sequence, string Content, DateTimeOffset CreatedAt);
    private sealed record InvitationResponse(Guid Id);
    private sealed record Page<T>(T[] Items, long? NextAfterSequence);
}
