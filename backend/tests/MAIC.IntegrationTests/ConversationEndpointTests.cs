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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class ConversationEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

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
        var workspace = new Workspace("Conversation HTTP tests");
        var channel = new Channel(workspace.Id, "Website");
        var externalUser = Guid.CreateVersion7();
        var contact = new Contact(workspace.Id, "Customer", [new("maic-user", externalUser.ToString())]);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, owner, WorkspaceRole.OWNER));
        db.WorkspaceMembers.AddRange(members.Select(m => new WorkspaceMember(workspace.Id, m.UserId, m.Role)));
        db.Channels.Add(channel);
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(Cancellation);
        return new(workspace.Id, channel.Id, contact.Id, channel.PublishableToken, externalUser, Assert.Single(contact.Identities).Id);
    }

    private static async Task AuthenticateAsync(HttpClient client, Guid userId)
    {
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        var csrf = await client.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
    }

    private static string Path(Guid workspace) => $"/workspaces/{workspace}/conversations";
    private static string Path(Guid workspace, Guid conversation) => $"{Path(workspace)}/{conversation}";
    private static string MessagesPath(Guid workspace, Guid conversation) => $"{Path(workspace, conversation)}/messages";
    private static object CreateBody(Seed seed, Guid requestId) => new { seed.ChannelId, seed.ContactId, clientRequestId = requestId };
    private static object MessageBody(Guid messageId, string content) => new { clientMessageId = messageId, content };

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.StatusCode == expected,
                $"Expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancellation)}");
            if (response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/conversations", StringComparison.Ordinal) == true)
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

    private static Task<ConversationResponse> GetAsync(HttpClient client, Guid workspace, Guid conversation) =>
        ReadGetAsync<ConversationResponse>(client, Path(workspace, conversation));

    private static async Task<T> ReadGetAsync<T>(HttpClient client, string path) =>
        await ReadAsync<T>(await client.GetAsync(path, Cancellation));

    private static async Task<ConversationResponse> CreateAsync(HttpClient client, Seed seed, Guid? requestId = null,
        HttpStatusCode expected = HttpStatusCode.Created)
    {
        var key = requestId ?? Guid.NewGuid();
        var response = await client.PostAsJsonAsync(Path(seed.WorkspaceId), CreateBody(seed, key), Cancellation);
        var location = response.Headers.Location;
        var conversation = await ReadAsync<ConversationResponse>(response, expected);
        Assert.Equal(seed.WorkspaceId, conversation.WorkspaceId);
        Assert.Equal(seed.ChannelId, conversation.ChannelId);
        Assert.Equal(seed.ContactId, conversation.ContactId);
        Assert.Equal(key, conversation.ClientRequestId);
        Assert.Equal("OPEN", conversation.Status);
        Assert.Equal('7', conversation.Id.ToString()[14]);
        if (expected == HttpStatusCode.Created)
            Assert.Equal(Path(seed.WorkspaceId, conversation.Id), location!.OriginalString);
        var persisted = await GetAsync(client, seed.WorkspaceId, conversation.Id);
        Assert.InRange((conversation.CreatedAt - persisted.CreatedAt).Ticks, -9, 9);
        Assert.Equal(conversation with { CreatedAt = persisted.CreatedAt }, persisted);
        return persisted;
    }

    private static async Task<MessageResponse> AppendAsync(HttpClient client, Guid workspace, Guid conversation,
        string content, Guid? messageId = null, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var key = messageId ?? Guid.NewGuid();
        var result = await ReadAsync<MessageResponse>(await client.PostAsJsonAsync(MessagesPath(workspace, conversation),
            MessageBody(key, content), Cancellation), expected);
        Assert.Equal(workspace, result.WorkspaceId);
        Assert.Equal(conversation, result.ConversationId);
        Assert.Equal(key, result.ClientMessageId);
        Assert.Equal(content, result.Content);
        Assert.Equal('7', result.Id.ToString()[14]);
        var persisted = Assert.Single((await ReadGetAsync<MessagePage>(client,
            MessagesPath(workspace, conversation) + $"?afterSequence={result.Sequence - 1}&take=1")).Items);
        Assert.InRange((result.CreatedAt - persisted.CreatedAt).Ticks, -9, 9);
        Assert.Equal(result with { CreatedAt = persisted.CreatedAt }, persisted);
        return persisted;
    }

    [Fact]
    public async Task Every_member_role_creates_reads_and_appends_as_the_persisted_actor()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var admin = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (admin, WorkspaceRole.ADMIN), (agent, WorkspaceRole.AGENT));
        var created = new List<ConversationResponse>();

        foreach (var actor in new[] { owner, admin, agent })
        {
            await AuthenticateAsync(client, actor);
            var conversation = await CreateAsync(client, seed);
            created.Add(conversation);
            Assert.Equal(actor, conversation.CreatedByUserId);
            Assert.Equal(conversation, await GetAsync(client, seed.WorkspaceId, conversation.Id));
            var before = await ReadGetAsync<MessagePage>(client, MessagesPath(seed.WorkspaceId, conversation.Id));
            Assert.Empty(before.Items);
            Assert.Null(before.NextAfterSequence);

            var first = await AppendAsync(client, seed.WorkspaceId, conversation.Id, "  Hello\r\n\tCustomer 🌍  ");
            var second = await AppendAsync(client, seed.WorkspaceId, conversation.Id, "Follow-up");
            Assert.Equal(actor, first.SenderUserId);
            Assert.Equal(actor, second.SenderUserId);
            Assert.Equal(1, first.Sequence);
            Assert.Equal(2, second.Sequence);
            var history = await ReadGetAsync<MessagePage>(client, MessagesPath(seed.WorkspaceId, conversation.Id));
            Assert.Equal(new[] { first, second }, history.Items);
            Assert.Null(history.NextAfterSequence);
        }

        var list = await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId));
        Assert.Equal(created.Select(c => c.Id).Order(), list.Items.Select(c => c.Id));
        Assert.Null(list.NextAfter);
        // A different member can append; the sender comes from that request's authenticated user.
        await AuthenticateAsync(client, owner);
        var shared = await AppendAsync(client, seed.WorkspaceId, created[^1].Id, "Owner joins");
        Assert.Equal(owner, shared.SenderUserId);
        Assert.Equal(3, shared.Sequence);
        Assert.Equal(agent, (await GetAsync(client, seed.WorkspaceId, created[^1].Id)).CreatedByUserId);
    }

    [Fact]
    public async Task Cross_workspace_targets_and_conversation_ids_remain_hidden_even_to_dual_members()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var first = await SeedAsync(owner);
        var second = await SeedAsync(outsider, (owner, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, first);
        await AppendAsync(client, first.WorkspaceId, conversation.Id, "Tenant private history");

        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(second.WorkspaceId, conversation.Id), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(MessagesPath(second.WorkspaceId, conversation.Id), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(MessagesPath(second.WorkspaceId, conversation.Id),
            MessageBody(Guid.NewGuid(), "Moved"), Cancellation));
        foreach (var target in new[] { second with { ContactId = first.ContactId }, second with { ChannelId = first.ChannelId },
                     second with { ContactId = Guid.NewGuid() }, second with { ChannelId = Guid.NewGuid() } })
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(Path(second.WorkspaceId),
                CreateBody(target, Guid.NewGuid()), Cancellation));
        var authorizedEmpty = await ReadGetAsync<ConversationPage>(client, Path(second.WorkspaceId));
        Assert.Empty(authorizedEmpty.Items);
        Assert.Null(authorizedEmpty.NextAfter);
        var absent = Guid.NewGuid();
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(first.WorkspaceId, absent), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(MessagesPath(first.WorkspaceId, absent), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(MessagesPath(first.WorkspaceId, absent),
            MessageBody(Guid.NewGuid(), "Missing"), Cancellation));

        await AuthenticateAsync(client, outsider);
        foreach (var workspace in new[] { first.WorkspaceId, Guid.NewGuid() })
        {
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(workspace), Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(workspace, conversation.Id), Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(MessagesPath(workspace, conversation.Id), Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(Path(workspace),
                CreateBody(first, conversation.ClientRequestId), Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(MessagesPath(workspace, conversation.Id),
                MessageBody(Guid.NewGuid(), "Intruder"), Cancellation));
        }
    }

    [Fact]
    public async Task Exact_retries_preserve_ids_and_sequences_but_changed_targets_or_content_conflict()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (agent, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var requestKey = Guid.NewGuid();
        var original = await CreateAsync(client, seed, requestKey);
        Assert.Equal(original, await CreateAsync(client, seed, requestKey, HttpStatusCode.OK));
        await using (var db = fixture.CreateContext())
        {
            var contact = new Contact(seed.WorkspaceId, "Different customer", []);
            var channel = new Channel(seed.WorkspaceId, "Different website");
            db.Contacts.Add(contact);
            db.Channels.Add(channel);
            await db.SaveChangesAsync(Cancellation);
            foreach (var altered in new[] { seed with { ContactId = contact.Id }, seed with { ChannelId = channel.Id } })
                await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(Path(seed.WorkspaceId),
                    CreateBody(altered, requestKey), Cancellation));
        }

        var messageKey = Guid.NewGuid();
        var first = await AppendAsync(client, seed.WorkspaceId, original.Id, " unchanged\r\n", messageKey);
        Assert.Equal(first, await AppendAsync(client, seed.WorkspaceId, original.Id, first.Content, messageKey, HttpStatusCode.OK));
        foreach (var changed in new[] { " unchanged\n", "unchanged\r\n", "different" })
            await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(MessagesPath(seed.WorkspaceId, original.Id),
                MessageBody(messageKey, changed), Cancellation));
        var next = await AppendAsync(client, seed.WorkspaceId, original.Id, "After conflicts");
        Assert.Equal(2, next.Sequence);
        Assert.Equal(2, (await ReadGetAsync<MessagePage>(client, MessagesPath(seed.WorkspaceId, original.Id))).Items.Length);

        var otherWorkspace = await SeedAsync(owner);
        var sameRequestInOtherWorkspace = await CreateAsync(client, otherWorkspace, requestKey);
        Assert.NotEqual(original.Id, sameRequestInOtherWorkspace.Id);
        await AppendAsync(client, otherWorkspace.WorkspaceId, sameRequestInOtherWorkspace.Id, "Different workspace", messageKey);

        // Request keys are scoped to actor; message keys are additionally scoped to conversation.
        var otherConversation = await CreateAsync(client, seed);
        var independent = await AppendAsync(client, seed.WorkspaceId, otherConversation.Id, "Different conversation", messageKey);
        Assert.Equal(1, independent.Sequence);
        await AuthenticateAsync(client, agent);
        var sameRequestByAgent = await CreateAsync(client, seed, requestKey);
        Assert.NotEqual(original.Id, sameRequestByAgent.Id);
        Assert.Equal(agent, sameRequestByAgent.CreatedByUserId);
        var sameMessageByAgent = await AppendAsync(client, seed.WorkspaceId, original.Id, "Different actor", messageKey);
        Assert.Equal(agent, sameMessageByAgent.SenderUserId);
        Assert.Equal(3, sameMessageByAgent.Sequence);
        Assert.Equal(sameMessageByAgent, await AppendAsync(client, seed.WorkspaceId, original.Id,
            sameMessageByAgent.Content, messageKey, HttpStatusCode.OK));
        Assert.Equal(3, (await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId))).Items.Length);
    }

    [Fact]
    public async Task Disabled_channels_keep_history_and_exact_replays_but_revoked_members_cannot_read_or_replay()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (agent, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, agent);
        var conversation = await CreateAsync(client, seed);
        var message = await AppendAsync(client, seed.WorkspaceId, conversation.Id, "Retained after departure");
        await AuthenticateAsync(client, owner);
        await AssertStatusAsync(HttpStatusCode.OK, await client.PutAsJsonAsync($"/workspaces/{seed.WorkspaceId}/channels/{seed.ChannelId}",
            new { enabled = false }, Cancellation));
        await AuthenticateAsync(client, agent);

        Assert.Equal(conversation, await CreateAsync(client, seed, conversation.ClientRequestId, HttpStatusCode.OK));
        Assert.Equal(message, await AppendAsync(client, seed.WorkspaceId, conversation.Id,
            message.Content, message.ClientMessageId, HttpStatusCode.OK));
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(Path(seed.WorkspaceId),
            CreateBody(seed, Guid.NewGuid()), Cancellation));
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(MessagesPath(seed.WorkspaceId, conversation.Id),
            MessageBody(Guid.NewGuid(), "New write to disabled channel"), Cancellation));
        Assert.Equal(conversation, await GetAsync(client, seed.WorkspaceId, conversation.Id));
        Assert.Equal(message, Assert.Single((await ReadGetAsync<MessagePage>(client,
            MessagesPath(seed.WorkspaceId, conversation.Id))).Items));

        await using (var db = fixture.CreateContext())
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == seed.WorkspaceId && m.UserId == agent).ExecuteDeleteAsync(Cancellation);
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(seed.WorkspaceId), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(seed.WorkspaceId, conversation.Id), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(MessagesPath(seed.WorkspaceId, conversation.Id), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(Path(seed.WorkspaceId),
            CreateBody(seed, conversation.ClientRequestId), Cancellation));
        foreach (var content in new[] { message.Content, "Changed replay must not disclose conflict" })
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(MessagesPath(seed.WorkspaceId, conversation.Id),
                MessageBody(message.ClientMessageId, content), Cancellation));
        await AuthenticateAsync(client, owner);
        var retained = Assert.Single((await ReadGetAsync<MessagePage>(client, MessagesPath(seed.WorkspaceId, conversation.Id))).Items);
        Assert.Equal(message, retained);
        Assert.Equal(agent, retained.SenderUserId);
        Assert.Equal(agent, (await GetAsync(client, seed.WorkspaceId, conversation.Id)).CreatedByUserId);
    }

    [Fact]
    public async Task Authentication_and_user_bound_csrf_cover_all_routes_and_both_writes()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var agent = await UserAsync();
        var seed = await SeedAsync(owner, (agent, WorkspaceRole.AGENT));
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, seed);
        using var anonymous = Client(factory);
        foreach (var path in new[] { Path(seed.WorkspaceId), Path(seed.WorkspaceId, conversation.Id), MessagesPath(seed.WorkspaceId, conversation.Id) })
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.GetAsync(path, Cancellation));
        await AssertWritesAsync(anonymous, seed, conversation.Id, HttpStatusCode.Unauthorized);
        var anonymousCsrf = (await anonymous.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation))!.Token;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AssertWritesAsync(client, seed, conversation.Id, HttpStatusCode.BadRequest);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymousCsrf);
        await AssertWritesAsync(client, seed, conversation.Id, HttpStatusCode.BadRequest);
        await AuthenticateAsync(client, owner);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", agent.ToString());
        await AssertWritesAsync(client, seed, conversation.Id, HttpStatusCode.BadRequest);
        await AuthenticateAsync(client, Guid.NewGuid());
        foreach (var path in new[] { Path(seed.WorkspaceId), Path(seed.WorkspaceId, conversation.Id), MessagesPath(seed.WorkspaceId, conversation.Id) })
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await client.GetAsync(path, Cancellation));
        await AssertWritesAsync(client, seed, conversation.Id, HttpStatusCode.Unauthorized);
        await AuthenticateAsync(client, owner);
        Assert.Single((await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId))).Items);
        Assert.Empty((await ReadGetAsync<MessagePage>(client, MessagesPath(seed.WorkspaceId, conversation.Id))).Items);
    }

    private static async Task AssertWritesAsync(HttpClient client, Seed seed, Guid conversation, HttpStatusCode expected)
    {
        await AssertStatusAsync(expected, await client.PostAsJsonAsync(Path(seed.WorkspaceId), CreateBody(seed, Guid.NewGuid()), Cancellation));
        await AssertStatusAsync(expected, await client.PostAsJsonAsync(MessagesPath(seed.WorkspaceId, conversation),
            MessageBody(Guid.NewGuid(), "Attempted write"), Cancellation));
    }

    [Fact]
    public async Task Strict_DTOs_reject_server_owned_fields_and_messages_have_no_mutation_routes()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var seed = await SeedAsync(owner);
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, seed);
        var original = await AppendAsync(client, seed.WorkspaceId, conversation.Id, "Immutable");
        foreach (var field in new[] { "workspaceId", "id", "conversationId", "senderUserId", "createdByUserId", "userId", "sender", "role", "status", "sequence", "createdAt" })
        {
            var create = new Dictionary<string, object>
            {
                ["channelId"] = seed.ChannelId, ["contactId"] = seed.ContactId, ["clientRequestId"] = Guid.NewGuid(), [field] = "forged"
            };
            var append = new Dictionary<string, object>
            {
                ["clientMessageId"] = Guid.NewGuid(), ["content"] = "forged", [field] = "forged"
            };
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(seed.WorkspaceId), create, Cancellation));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(MessagesPath(seed.WorkspaceId, conversation.Id), append, Cancellation));
        }
        foreach (var field in new[] { "channelId", "contactId", "clientRequestId" })
        {
            var append = new Dictionary<string, object> { ["clientMessageId"] = Guid.NewGuid(), ["content"] = "Moved", [field] = Guid.NewGuid() };
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(MessagesPath(seed.WorkspaceId, conversation.Id), append, Cancellation));
        }
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"{MessagesPath(seed.WorkspaceId, conversation.Id)}/{original.Id}")
            {
                Content = JsonContent.Create(new { content = "Edited", senderUserId = Guid.NewGuid() })
            };
            using var response = await client.SendAsync(request, Cancellation);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }
        Assert.Single((await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId))).Items);
        Assert.Equal(original, Assert.Single((await ReadGetAsync<MessagePage>(client, MessagesPath(seed.WorkspaceId, conversation.Id))).Items));
    }

    [Fact]
    public async Task Invalid_payloads_fail_without_consuming_sequence_and_valid_text_preserves_utf16_boundaries()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var seed = await SeedAsync(owner);
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, seed);
        foreach (var body in new object[]
        {
            new { seed.ChannelId, seed.ContactId }, new { seed.ChannelId, clientRequestId = Guid.NewGuid() },
            new { seed.ContactId, clientRequestId = Guid.NewGuid() }, CreateBody(seed, Guid.Empty),
            CreateBody(seed with { ChannelId = Guid.Empty }, Guid.NewGuid()), CreateBody(seed with { ContactId = Guid.Empty }, Guid.NewGuid())
        })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(seed.WorkspaceId), body, Cancellation));
        foreach (var malformed in new[] { "null", "[]", "{\"channelId\":\"not-a-uuid\"}", "{\"channelId\":17}" })
        {
            using var body = new StringContent(malformed, Encoding.UTF8, "application/json");
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsync(Path(seed.WorkspaceId), body, Cancellation));
        }
        foreach (var content in new[] { "", " \r\n\t ", "bad\0text", "bad\u0001text", "bad\u000Btext", "bad\u000Ctext", "bad\u007Ftext", "bad\u0085text",
                     new string('a', 4001), string.Concat(Enumerable.Repeat("🌍", 2001)) })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(MessagesPath(seed.WorkspaceId, conversation.Id),
                MessageBody(Guid.NewGuid(), content), Cancellation));
        foreach (var body in new object[] { new { content = "Missing key" }, MessageBody(Guid.Empty, "Empty key"),
                     new { clientMessageId = Guid.NewGuid() }, new { clientMessageId = Guid.NewGuid(), content = (string?)null } })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(MessagesPath(seed.WorkspaceId, conversation.Id), body, Cancellation));

        var validKey = Guid.NewGuid();
        foreach (var malformed in new[] { "null", "{}", "[]", "{\"clientMessageId\":\"not-a-uuid\",\"content\":\"text\"}",
                     $"{{\"clientMessageId\":\"{validKey}\",\"content\":17}}",
                     $"{{\"clientMessageId\":\"{validKey}\",\"content\":\"\\uD800\"}}",
                     $"{{\"clientMessageId\":\"{validKey}\",\"content\":\"\\uDC00\"}}" })
        {
            using var body = new StringContent(malformed, Encoding.UTF8, "application/json");
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsync(MessagesPath(seed.WorkspaceId, conversation.Id), body, Cancellation));
        }
        var ascii = await AppendAsync(client, seed.WorkspaceId, conversation.Id, new string('a', 4000), validKey);
        var unicode = await AppendAsync(client, seed.WorkspaceId, conversation.Id, string.Concat(Enumerable.Repeat("🌍", 2000)));
        var whitespace = await AppendAsync(client, seed.WorkspaceId, conversation.Id, " \tPreserved\r\nline two\n  ");
        Assert.Equal(new long[] { 1, 2, 3 }, new[] { ascii.Sequence, unicode.Sequence, whitespace.Sequence });
        Assert.Equal(new[] { ascii, unicode, whitespace }, (await ReadGetAsync<MessagePage>(client, MessagesPath(seed.WorkspaceId, conversation.Id))).Items);
        Assert.Single((await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId))).Items);
    }

    [Fact]
    public async Task Cursors_are_exclusive_deterministic_bounded_and_distinguish_empty_pages_from_missing_resources()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var seed = await SeedAsync(owner);
        await AuthenticateAsync(client, owner);
        var conversations = new List<ConversationResponse>();
        for (var index = 0; index < 5; index++) conversations.Add(await CreateAsync(client, seed));
        var expected = conversations.OrderBy(c => c.Id).ToArray();
        var one = await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId) + "?take=2");
        Assert.Equal(expected.Take(2), one.Items);
        Assert.Equal(one.Items[^1].Id, one.NextAfter);
        var two = await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId) + $"?take=2&after={one.NextAfter}");
        Assert.Equal(expected.Skip(2).Take(2), two.Items);
        Assert.Equal(two.Items[^1].Id, two.NextAfter);
        var three = await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId) + $"?take=2&after={two.NextAfter}");
        Assert.Equal(expected.Skip(4), three.Items);
        Assert.Null(three.NextAfter);
        var empty = await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId) + $"?after={expected[^1].Id}&take=100");
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextAfter);

        var conversation = expected[0];
        var messages = new List<MessageResponse>();
        for (var index = 0; index < 5; index++)
            messages.Add(await AppendAsync(client, seed.WorkspaceId, conversation.Id, "Message " + index));
        var messagePath = MessagesPath(seed.WorkspaceId, conversation.Id);
        var first = await ReadGetAsync<MessagePage>(client, messagePath + "?afterSequence=0&take=2");
        Assert.Equal(messages.Take(2), first.Items);
        Assert.Equal(2, first.NextAfterSequence);
        var second = await ReadGetAsync<MessagePage>(client, messagePath + $"?afterSequence={first.NextAfterSequence}&take=2");
        Assert.Equal(messages.Skip(2).Take(2), second.Items);
        Assert.Equal(4, second.NextAfterSequence);
        var third = await ReadGetAsync<MessagePage>(client, messagePath + $"?afterSequence={second.NextAfterSequence}&take=2");
        Assert.Equal(messages.Skip(4), third.Items);
        Assert.Null(third.NextAfterSequence);
        var exactPage = await ReadGetAsync<MessagePage>(client, messagePath + "?afterSequence=3&take=2");
        Assert.Equal(messages.Skip(3), exactPage.Items);
        Assert.Null(exactPage.NextAfterSequence);
        var emptyMessages = await ReadGetAsync<MessagePage>(client, messagePath + "?afterSequence=5&take=100");
        Assert.Empty(emptyMessages.Items);
        Assert.Null(emptyMessages.NextAfterSequence);
        var resumed = await AppendAsync(client, seed.WorkspaceId, conversation.Id, "Arrives after paging");
        Assert.Equal(resumed, Assert.Single((await ReadGetAsync<MessagePage>(client, messagePath + "?afterSequence=5&take=1")).Items));

        foreach (var query in new[] { "take=0", "take=-1", "take=101", "take=abc", "take=2147483648", "after=not-a-uuid" })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.GetAsync(Path(seed.WorkspaceId) + "?" + query, Cancellation));
        foreach (var query in new[] { "take=0", "take=-1", "take=101", "take=abc", "afterSequence=-1", "afterSequence=abc", "afterSequence=9223372036854775808" })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.GetAsync(messagePath + "?" + query, Cancellation));
    }

    [Fact]
    public async Task Contact_identities_and_channel_publishable_tokens_never_authorize_member_conversations()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var seed = await SeedAsync(owner);
        await AuthenticateAsync(client, owner);
        var conversation = await CreateAsync(client, seed);
        await AppendAsync(client, seed.WorkspaceId, conversation.Id, "Private member history");

        using var anonymous = Client(factory);
        anonymous.DefaultRequestHeaders.Add("Authorization", "Bearer " + seed.PublishableToken);
        anonymous.DefaultRequestHeaders.Add("X-Channel-Token", seed.PublishableToken);
        anonymous.DefaultRequestHeaders.Add("X-Contact-Id", seed.ContactId.ToString());
        await AssertStatusAsync(HttpStatusCode.OK, await anonymous.GetAsync($"/public/channels/{seed.PublishableToken}", Cancellation));
        foreach (var path in new[] { Path(seed.WorkspaceId), Path(seed.WorkspaceId, conversation.Id), MessagesPath(seed.WorkspaceId, conversation.Id) })
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.GetAsync(path, Cancellation));
        await AssertWritesAsync(anonymous, seed, conversation.Id, HttpStatusCode.Unauthorized);
        foreach (var candidate in new[] { seed.ExternalUserId, seed.ContactId, seed.ContactIdentityId, seed.ChannelId })
        {
            await AuthenticateAsync(client, candidate);
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await client.GetAsync(MessagesPath(seed.WorkspaceId, conversation.Id), Cancellation));
            await AssertWritesAsync(client, seed, conversation.Id, HttpStatusCode.Unauthorized);
        }
        await AuthenticateAsync(client, owner);
        Assert.Single((await ReadGetAsync<ConversationPage>(client, Path(seed.WorkspaceId))).Items);
        Assert.Single((await ReadGetAsync<MessagePage>(client, MessagesPath(seed.WorkspaceId, conversation.Id))).Items);
    }

    private sealed record Csrf(string Token);
    private sealed record Seed(Guid WorkspaceId, Guid ChannelId, Guid ContactId, string PublishableToken, Guid ExternalUserId, Guid ContactIdentityId);
    private sealed record ConversationResponse(Guid Id, Guid WorkspaceId, Guid ChannelId, Guid ContactId,
        Guid CreatedByUserId, Guid ClientRequestId, string Status, DateTimeOffset CreatedAt);
    private sealed record MessageResponse(Guid Id, Guid WorkspaceId, Guid ConversationId, Guid SenderUserId,
        Guid ClientMessageId, long Sequence, string Content, DateTimeOffset CreatedAt);
    private sealed record ConversationPage(ConversationResponse[] Items, Guid? NextAfter);
    private sealed record MessagePage(MessageResponse[] Items, long? NextAfterSequence);
}
