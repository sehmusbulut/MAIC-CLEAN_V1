using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MAIC.Application.AI;
using MAIC.Domain.AI;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class AiExecutionEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_owner_and_admin_bind_but_every_current_member_can_execute_the_persisted_agent()
    {
        var harness = new AiExecutionHarness(fixture);
        var seed = await harness.SeedAsync();
        await using var factory = harness.Factory(seed);
        using var owner = await AiExecutionHarness.ClientAsync(factory, seed.Owner);
        using var admin = await AiExecutionHarness.ClientAsync(factory, seed.Admin);
        using var member = await AiExecutionHarness.ClientAsync(factory, seed.Member);
        foreach (var manager in new[] { owner, admin })
        {
            await AiExecutionHarness.StatusAsync(HttpStatusCode.OK, await manager.PutAsJsonAsync(seed.Path + "/ai-agent", new { agentId = (Guid?)null }, Cancellation));
            await AiExecutionHarness.StatusAsync(HttpStatusCode.Conflict, await AiExecutionHarness.ExecuteAsync(member, seed));
            await AiExecutionHarness.StatusAsync(HttpStatusCode.OK, await manager.PutAsJsonAsync(seed.Path + "/ai-agent", new { agentId = seed.Agent }, Cancellation));
        }
        await AiExecutionHarness.StatusAsync(HttpStatusCode.Forbidden, await member.PutAsJsonAsync(seed.Path + "/ai-agent", new { agentId = (Guid?)null }, Cancellation));
        foreach (var actor in new[] { owner, admin, member })
        {
            var current = await harness.ConversationAsync(seed);
            var response = await AiExecutionHarness.ReadAsync(await AiExecutionHarness.ExecuteAsync(actor, current));
            Assert.Equal("AI", response.GetProperty("message").GetProperty("senderKind").GetString());
            Assert.Equal(JsonValueKind.Null, response.GetProperty("message").GetProperty("senderUserId").ValueKind);
        }
        Assert.Equal(3, harness.Generator.Calls);
    }

    [Fact]
    public async Task Strict_bodies_and_csrf_reject_caller_policy_identity_and_provider_without_spending_generation()
    {
        var harness = new AiExecutionHarness(fixture);
        var seed = await harness.SeedAsync();
        await using var factory = harness.Factory(seed);
        using var client = await AiExecutionHarness.ClientAsync(factory, seed.Owner);
        foreach (var field in new[] { "agentId", "provider", "model", "modelTier", "workspaceId", "conversationId", "senderUserId", "senderKind", "content", "maxAiReplies", "handoffWhenUnanswered" })
        {
            var body = new Dictionary<string, object> { ["messageId"] = seed.Message, [field] = "forged" };
            await AiExecutionHarness.StatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(seed.ExecutionPath, body, Cancellation));
        }
        foreach (var invalid in new[] { "{}", "null", "[]", "{\"messageId\":null}", "{\"messageId\":\"not-a-guid\"}", "{\"messageId\":\"00000000-0000-0000-0000-000000000000\"}" })
        {
            using var body = new StringContent(invalid, Encoding.UTF8, "application/json");
            await AiExecutionHarness.StatusAsync(HttpStatusCode.BadRequest, await client.PostAsync(seed.ExecutionPath, body, Cancellation));
        }
        await AiExecutionHarness.StatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(seed.Path + "/ai-agent", new { }, Cancellation));
        await AiExecutionHarness.StatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(seed.Path + "/ai-agent", new { agentId = seed.Agent, provider = "forged" }, Cancellation));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AiExecutionHarness.StatusAsync(HttpStatusCode.BadRequest, await AiExecutionHarness.ExecuteAsync(client, seed));
        await AiExecutionHarness.StatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(seed.Path + "/ai-agent", new { agentId = seed.Agent }, Cancellation));
        using var anonymous = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await AiExecutionHarness.StatusAsync(HttpStatusCode.Unauthorized, await AiExecutionHarness.ExecuteAsync(anonymous, seed));
        await AiExecutionHarness.StatusAsync(HttpStatusCode.Unauthorized, await anonymous.PutAsJsonAsync(seed.Path + "/ai-agent", new { agentId = seed.Agent }, Cancellation));
        Assert.Equal(0, harness.Generator.Calls);
        await using var db = fixture.CreateContext();
        Assert.Single(await db.Messages.Where(m => m.ConversationId == seed.Conversation).ToListAsync(Cancellation));
    }

    [Fact]
    public async Task Dual_workspace_members_cannot_mix_conversation_agent_or_trigger_ids()
    {
        var harness = new AiExecutionHarness(fixture);
        var first = await harness.SeedAsync();
        var other = await harness.SeedAsync();
        await using (var db = fixture.CreateContext())
        {
            db.WorkspaceMembers.Add(new WorkspaceMember(other.Workspace, first.Owner, WorkspaceRole.OWNER));
            await db.SaveChangesAsync(Cancellation);
        }
        await using var factory = harness.Factory(first);
        using var client = await AiExecutionHarness.ClientAsync(factory, first.Owner);
        await AiExecutionHarness.StatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(first.Path + "/ai-agent", new { agentId = other.Agent }, Cancellation));
        await AiExecutionHarness.StatusAsync(HttpStatusCode.NotFound, await AiExecutionHarness.ExecuteAsync(client, first with { Workspace = other.Workspace }));
        await AiExecutionHarness.StatusAsync(HttpStatusCode.NotFound, await AiExecutionHarness.ExecuteAsync(client, first with { Message = other.Message }));
        var sameTenant = await harness.ConversationAsync(first);
        await AiExecutionHarness.StatusAsync(HttpStatusCode.NotFound, await AiExecutionHarness.ExecuteAsync(client, first with { Message = sameTenant.Message }));
        using var outsider = await AiExecutionHarness.ClientAsync(factory, other.Owner);
        await AiExecutionHarness.StatusAsync(HttpStatusCode.NotFound, await AiExecutionHarness.ExecuteAsync(outsider, first));
        await AiExecutionHarness.StatusAsync(HttpStatusCode.NotFound, await outsider.PutAsJsonAsync(first.Path + "/ai-agent", new { agentId = first.Agent }, Cancellation));
        Assert.Equal(0, harness.Generator.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_valid_json_is_rejected_for_both_fixed_length_and_chunked_requests(bool chunked)
    {
        var harness = new AiExecutionHarness(fixture);
        var seed = await harness.SeedAsync();
        await using var factory = harness.Factory(seed);
        using var client = await AiExecutionHarness.ClientAsync(factory, seed.Owner);
        foreach (var (method, path, json) in new[]
        {
            (HttpMethod.Post, seed.ExecutionPath, JsonSerializer.Serialize(new { messageId = seed.Message })),
            (HttpMethod.Put, seed.Path + "/ai-agent", JsonSerializer.Serialize(new { agentId = (Guid?)null }))
        })
        {
            // The JSON itself is valid: the bound must cover all bytes, including trailing whitespace.
            var payload = json + new string(' ', 4097);
            using var request = new HttpRequestMessage(method, path)
            {
                Content = chunked ? new StreamOnlyJsonContent(payload) : new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (chunked) request.Headers.TransferEncodingChunked = true;
            await AiExecutionHarness.StatusAsync(HttpStatusCode.RequestEntityTooLarge, await client.SendAsync(request, Cancellation));
        }
        Assert.Equal(0, harness.Generator.Calls);
        await using var db = fixture.CreateContext();
        Assert.Equal(seed.Agent, (await db.Conversations.SingleAsync(c => c.Id == seed.Conversation, Cancellation)).AiAgentId);
        Assert.Single(await db.Messages.Where(m => m.ConversationId == seed.Conversation).ToListAsync(Cancellation));
    }

    [Fact]
    public async Task Terminal_replay_is_shared_across_members_and_revocation_still_hides_the_record()
    {
        var harness = new AiExecutionHarness(fixture);
        var seed = await harness.SeedAsync();
        await using var factory = harness.Factory(seed);
        using var member = await AiExecutionHarness.ClientAsync(factory, seed.Member);
        using var owner = await AiExecutionHarness.ClientAsync(factory, seed.Owner);
        var original = await AiExecutionHarness.ReadAsync(await AiExecutionHarness.ExecuteAsync(member, seed));
        var replay = await AiExecutionHarness.ReadAsync(await AiExecutionHarness.ExecuteAsync(owner, seed));
        Assert.Equal(original.GetProperty("executionId").GetGuid(), replay.GetProperty("executionId").GetGuid());
        Assert.Equal(original.GetProperty("message").GetProperty("id").GetGuid(), replay.GetProperty("message").GetProperty("id").GetGuid());
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        await using var db = fixture.CreateContext();
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == seed.Workspace && m.UserId == seed.Member).ExecuteDeleteAsync(Cancellation);
        await AiExecutionHarness.StatusAsync(HttpStatusCode.NotFound, await AiExecutionHarness.ExecuteAsync(member, seed));
        Assert.Equal(1, harness.Generator.Calls);
        var persisted = Assert.Single(await db.Messages.Where(m => m.ConversationId == seed.Conversation && m.SenderKind == MessageSenderKind.AI).ToListAsync(Cancellation));
        Assert.Null(persisted.SenderUserId);
        Assert.Equal(original.GetProperty("executionId").GetGuid(), persisted.AiExecutionId);
        // An AI answer is never accepted as another customer trigger.
        await AiExecutionHarness.StatusAsync(HttpStatusCode.Conflict, await AiExecutionHarness.ExecuteAsync(owner, seed with { Message = persisted.Id }));
    }

    [Fact]
    public async Task Final_allowed_reply_and_handoff_commit_together_and_replay_never_adds_another_reply()
    {
        var harness = new AiExecutionHarness(fixture);
        var seed = await harness.SeedAsync(maxReplies: 1);
        await using var factory = harness.Factory(seed);
        using var client = await AiExecutionHarness.ClientAsync(factory, seed.Owner);
        var result = await AiExecutionHarness.ReadAsync(await AiExecutionHarness.ExecuteAsync(client, seed));
        Assert.NotEqual(JsonValueKind.Null, result.GetProperty("message").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, result.GetProperty("handoff").ValueKind);
        var replay = await AiExecutionHarness.ReadAsync(await AiExecutionHarness.ExecuteAsync(client, seed));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, harness.Generator.Calls);
        await using var db = fixture.CreateContext();
        Assert.Single(await db.Messages.Where(m => m.ConversationId == seed.Conversation && m.SenderKind == MessageSenderKind.AI).ToListAsync(Cancellation));
        Assert.Single(await db.HandoffRequests.Where(h => h.ConversationId == seed.Conversation).ToListAsync(Cancellation));
    }

    private sealed class StreamOnlyJsonContent : HttpContent
    {
        private readonly byte[] bytes;

        public StreamOnlyJsonContent(string json)
        {
            bytes = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }

    }
}

// Shared only by the two independent HTTP/concurrency test classes. All persistence/retrieval remains real PostgreSQL.
internal sealed class AiExecutionHarness(PostgresFixture fixture, bool hold = false, bool ignoreCancellation = false)
{
    internal static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    public ControlledTextGenerator Generator { get; } = new() { Hold = hold, IgnoreCancellation = ignoreCancellation };

    public WebApplicationFactory<Program> Factory(AiExecutionSeed seed, AiExecutionSettings? settings = null, TimeProvider? clock = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
            foreach (var tier in new[] { "Fast", "Balanced", "Quality" })
            {
                builder.UseSetting($"AiExecution:{tier}:InputPerMillionTokens", "1");
                builder.UseSetting($"AiExecution:{tier}:OutputPerMillionTokens", "3");
            }
            builder.ConfigureServices(services =>
            {
                services.AddAuthentication("test-only").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test-only", _ => { });
                services.AddSingleton<IEmbeddingGenerator>(new HashingEmbeddings(seed.Space));
                services.AddSingleton<ITextGenerator>(Generator);
                if (settings is not null) services.AddSingleton(settings);
                if (clock is not null) services.AddSingleton(clock);
            });
        });

    public async Task<AiExecutionSeed> SeedAsync(int? maxReplies = null)
    {
        var owner = await PgVectorSeed.UserAsync(fixture);
        var admin = await PgVectorSeed.UserAsync(fixture);
        var member = await PgVectorSeed.UserAsync(fixture);
        var key = new KnowledgeVectorKey(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        await PgVectorSeed.KeysAsync(fixture, owner, [key]);
        var space = new EmbeddingSpace($"ai-{Guid.NewGuid():N}"[..20], 256);
        await PgVectorSeed.RegisterAsync(fixture, space);
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeDocumentChunks" SET "Content" = 'Refunds are paid within five days.' WHERE "Id" = {0}""", [key.ChunkId], Cancellation);
        db.WorkspaceMembers.AddRange(new WorkspaceMember(key.WorkspaceId, owner, WorkspaceRole.OWNER),
            new WorkspaceMember(key.WorkspaceId, admin, WorkspaceRole.ADMIN), new WorkspaceMember(key.WorkspaceId, member, WorkspaceRole.AGENT));
        var channel = new Channel(key.WorkspaceId, "AI execution tests");
        var contact = new Contact(key.WorkspaceId, "Support customer", []);
        var settings = new AiAgentSettings("Support", true, "Answer from knowledge.", "en", AiModelTier.BALANCED,
            AiKnowledgeScope.ALL_SOURCES, [], true, false, maxReplies);
        var agent = new AiAgent(key.WorkspaceId, settings, DateTimeOffset.UtcNow);
        db.Channels.Add(channel);
        db.Contacts.Add(contact);
        db.AiAgents.Add(agent);
        await db.SaveChangesAsync(Cancellation);
        await new KnowledgeIndexStore(db).DiscoverAsync(space, DateTimeOffset.UtcNow, 100_000, Cancellation);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "KnowledgeIndexStates" WHERE "SpaceKey" = {0} AND "WorkspaceId" <> {1}""", [space.Key, key.WorkspaceId], Cancellation);
        var processor = new KnowledgeIndexProcessor(new KnowledgeIndexStore(db),
            new KnowledgeVectorIndex(new HashingEmbeddings(space), new PgVectorKnowledgeStore(db)), TimeProvider.System);
        while (await processor.ProcessNextAsync(Cancellation) is not null) { }
        return await ConversationAsync(new(key.WorkspaceId, owner, admin, member, channel.Id, contact.Id, agent.Id,
            Guid.Empty, Guid.Empty, key.KnowledgeSourceId, key.DocumentId, key.ChunkId, space));
    }

    public async Task<AiExecutionSeed> ConversationAsync(AiExecutionSeed seed)
    {
        await using var db = fixture.CreateContext();
        var conversation = new Conversation(seed.Workspace, seed.Channel, seed.Contact, seed.Member, Guid.NewGuid());
        conversation.BindAiAgent(seed.Agent);
        var message = new Message(seed.Workspace, conversation.Id, seed.Member, Guid.NewGuid(),
            conversation.AdvanceMessageSequence(), "When are refunds paid?");
        db.Conversations.Add(conversation);
        db.Messages.Add(message);
        await db.SaveChangesAsync(Cancellation);
        return seed with { Conversation = conversation.Id, Message = message.Id };
    }

    public static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> factory, Guid user)
    {
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", user.ToString());
        var csrf = await client.GetFromJsonAsync<JsonElement>("/auth/csrf", Cancellation);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return client;
    }

    public static Task<HttpResponseMessage> ExecuteAsync(HttpClient client, AiExecutionSeed seed, CancellationToken? cancellation = null) =>
        client.PostAsJsonAsync(seed.ExecutionPath, new { messageId = seed.Message }, cancellation ?? Cancellation);

    public static async Task StatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancellation)}");
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancellation)}");
            Assert.True(response.Headers.CacheControl?.NoStore);
            return await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        }
    }
}

internal sealed record AiExecutionSeed(Guid Workspace, Guid Owner, Guid Admin, Guid Member, Guid Channel, Guid Contact,
    Guid Agent, Guid Conversation, Guid Message, Guid Source, Guid Document, Guid Chunk, EmbeddingSpace Space)
{
    public string Path => $"/workspaces/{Workspace}/conversations/{Conversation}";
    public string ExecutionPath => Path + "/ai-executions";
}

internal sealed class ControlledTextGenerator : ITextGenerator
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int calls;
    public bool Hold { get; init; }
    public bool IgnoreCancellation { get; init; }
    public int Calls => Volatile.Read(ref calls);
    public Task Entered => entered.Task;
    public void Release() => release.TrySetResult();

    public async Task<TextGenerationResult> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        entered.TrySetResult();
        if (Hold) await release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
        return new("{\"answer\":\"Refunds are paid within five days.\",\"citations\":[1]}", new(40, 12));
    }
}
