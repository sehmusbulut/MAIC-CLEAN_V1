using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MAIC.Application.AI;
using MAIC.Domain.Identity;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class AiAgentEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string[] DetailFields =
    [
        "createdAt", "enabled", "handoffOnRequest", "handoffWhenUnanswered", "id", "instructions", "knowledgeScope",
        "knowledgeSourceIds", "language", "maxAiReplies", "modelTier", "name", "updatedAt", "workspaceId"
    ];
    private static readonly string[] SummaryFields = ["createdAt", "enabled", "id", "knowledgeScope", "modelTier", "name", "updatedAt", "workspaceId"];
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
        // Contract tests make many writes per user; rate limiting is covered by KnowledgeDocumentEndpointTests.
        builder.UseSetting("KnowledgeLimits:WritesPerMinutePerUser", "100000");
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

    private async Task<Guid> WorkspaceAsync(Guid owner, params (Guid UserId, WorkspaceRole Role)[] members)
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("AI agent HTTP tests");
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, owner, WorkspaceRole.OWNER));
        db.WorkspaceMembers.AddRange(members.Select(m => new WorkspaceMember(workspace.Id, m.UserId, m.Role)));
        await db.SaveChangesAsync(Cancellation);
        return workspace.Id;
    }

    private async Task<Guid> SourceAsync(Guid workspaceId)
    {
        await using var db = fixture.CreateContext();
        var source = new KnowledgeSource(workspaceId, KnowledgeSourceKind.TEXT, "FAQ", "Answers", null);
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(Cancellation);
        return source.Id;
    }

    private static async Task AuthenticateAsync(HttpClient client, Guid userId)
    {
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        var csrf = await client.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
    }

    private static string Path(Guid ws) => $"/workspaces/{ws}/ai-agents";
    private static string Path(Guid ws, Guid id) => $"{Path(ws)}/{id}";

    private static Dictionary<string, object?> Body(string name = "Support bot", bool enabled = false, string scope = "ALL_SOURCES",
        Guid[]? sources = null, string? instructions = "Answer from the FAQ.", string? language = "tr", string tier = "BALANCED",
        int? maxAiReplies = 5) => new()
    {
        ["name"] = name, ["enabled"] = enabled, ["instructions"] = instructions, ["language"] = language, ["modelTier"] = tier,
        ["knowledgeScope"] = scope, ["knowledgeSourceIds"] = sources ?? [], ["handoffOnRequest"] = true,
        ["handoffWhenUnanswered"] = false, ["maxAiReplies"] = maxAiReplies
    };

    private static async Task<string> AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Cancellation);
            Assert.True(response.StatusCode == expected,
                $"Expected {(int)expected}, got {(int)response.StatusCode} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
            Assert.True(response.Headers.CacheControl?.NoStore == true, $"Missing no-store on {(int)response.StatusCode}");
            return body;
        }
    }

    private static AiAgentView Detail(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(DetailFields, document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        return JsonSerializer.Deserialize<AiAgentView>(json, JsonSerializerOptions.Web)!;
    }

    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, string path, string json) =>
        client.SendAsync(new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, Cancellation);

    [Fact]
    public async Task Owners_and_admins_manage_agents_and_every_member_reads_them()
    {
        var owner = await UserAsync();
        var admin = await UserAsync();
        var agentUser = await UserAsync();
        var ws = await WorkspaceAsync(owner, (admin, WorkspaceRole.ADMIN), (agentUser, WorkspaceRole.AGENT));
        var (s1, s2) = (await SourceAsync(ws), await SourceAsync(ws));
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, admin);

        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var created = await client.PostAsJsonAsync(Path(ws), Body(scope: "SELECTED_SOURCES", sources: [s2, s1]), Cancellation);
        var createdBody = await created.Content.ReadAsStringAsync(Cancellation);
        Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
        var agent = Detail(createdBody);
        Assert.Equal(Path(ws, agent.Id), created.Headers.Location!.OriginalString);
        Assert.Equal((ws, "Support bot", false, "Answer from the FAQ.", "tr", "BALANCED", "SELECTED_SOURCES", true, false, (int?)5),
            (agent.WorkspaceId, agent.Name, agent.Enabled, agent.Instructions, agent.Language, agent.ModelTier, agent.KnowledgeScope,
                agent.HandoffOnRequest, agent.HandoffWhenUnanswered, agent.MaxAiReplies));
        Assert.Equal(new[] { s1, s2 }.Order(), agent.KnowledgeSourceIds);
        Assert.Equal('7', agent.Id.ToString()[14]);
        Assert.InRange(agent.CreatedAt, before, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(agent.CreatedAt, agent.UpdatedAt);

        // Every member (including AGENT) can read; the creation response equals later reads.
        await AuthenticateAsync(client, agentUser);
        var read = Detail(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(Path(ws, agent.Id), Cancellation)));
        Assert.Equal(agent with { KnowledgeSourceIds = [] }, read with { KnowledgeSourceIds = [] });
        Assert.Equal(agent.KnowledgeSourceIds, read.KnowledgeSourceIds);
        // AGENT members cannot change what answers customers.
        await AssertStatusAsync(HttpStatusCode.Forbidden, await client.PostAsJsonAsync(Path(ws), Body(), Cancellation));
        await AssertStatusAsync(HttpStatusCode.Forbidden, await client.PutAsJsonAsync(Path(ws, agent.Id), Body(enabled: true), Cancellation));
        await AssertStatusAsync(HttpStatusCode.Forbidden, await client.DeleteAsync(Path(ws, agent.Id), Cancellation));
        // Role is decided before source lookup, so an AGENT learns nothing about which source IDs exist.
        await AssertStatusAsync(HttpStatusCode.Forbidden,
            await client.PostAsJsonAsync(Path(ws), Body(scope: "SELECTED_SOURCES", sources: [Guid.NewGuid()]), Cancellation));

        await AuthenticateAsync(client, owner);
        var updated = Detail(await AssertStatusAsync(HttpStatusCode.OK, await client.PutAsJsonAsync(Path(ws, agent.Id),
            Body("Renamed", enabled: true, instructions: null, language: null, tier: "QUALITY", maxAiReplies: null), Cancellation)));
        Assert.Equal(("Renamed", true, (string?)null, (string?)null, "QUALITY", "ALL_SOURCES", (int?)null),
            (updated.Name, updated.Enabled, updated.Instructions, updated.Language, updated.ModelTier, updated.KnowledgeScope, updated.MaxAiReplies));
        Assert.Empty(updated.KnowledgeSourceIds);
        Assert.True(updated.UpdatedAt >= updated.CreatedAt);
        Assert.Equal(agent.CreatedAt, updated.CreatedAt);
        Assert.Equal(updated, Detail(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(Path(ws, agent.Id), Cancellation))) with
            { KnowledgeSourceIds = updated.KnowledgeSourceIds });

        await AssertStatusAsync(HttpStatusCode.NoContent, await client.DeleteAsync(Path(ws, agent.Id), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(ws, agent.Id), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.DeleteAsync(Path(ws, agent.Id), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(Path(ws, agent.Id), Body(), Cancellation));
        await using var db = fixture.CreateContext();
        Assert.False(await db.AiAgentKnowledgeSources.AnyAsync(s => s.AiAgentId == agent.Id, Cancellation));
        Assert.Equal(2, await db.KnowledgeSources.CountAsync(s => s.WorkspaceId == ws, Cancellation));
    }

    [Fact]
    public async Task Input_is_validated_strictly_and_selected_sources_must_belong_to_the_workspace()
    {
        var owner = await UserAsync();
        var other = await UserAsync();
        var ws = await WorkspaceAsync(owner);
        var otherWs = await WorkspaceAsync(other);
        var source = await SourceAsync(ws);
        var foreign = await SourceAsync(otherWs);
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);
        var existing = Detail(await AssertStatusAsync(HttpStatusCode.Created, await client.PostAsJsonAsync(Path(ws), Body(), Cancellation)));

        var invalid = new List<Dictionary<string, object?>>
        {
            Body(name: " "), Body(name: new string('x', 201)), Body(instructions: " "), Body(instructions: new string('x', 8001)),
            Body(language: "TR"), Body(language: "english"), Body(tier: "fast"), Body(tier: "GPT4"), Body(scope: "all"),
            Body(maxAiReplies: 0), Body(maxAiReplies: 101), Body(scope: "SELECTED_SOURCES"), Body(sources: [source]),
            Body(scope: "SELECTED_SOURCES", sources: [source, source]), Body(scope: "SELECTED_SOURCES", sources: [foreign]),
            Body(scope: "SELECTED_SOURCES", sources: [Guid.NewGuid()]), Body(scope: "SELECTED_SOURCES", sources: [Guid.Empty])
        };
        var missing = Body();
        missing.Remove("maxAiReplies");
        invalid.Add(missing);
        var extra = Body();
        extra["workspaceId"] = otherWs;
        invalid.Add(extra);
        var nullSources = Body();
        nullSources["knowledgeSourceIds"] = null;
        invalid.Add(nullSources);
        foreach (var body in invalid)
        {
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(ws), body, Cancellation));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(Path(ws, existing.Id), body, Cancellation));
        }
        await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Post, Path(ws), "{not json"));
        await AssertStatusAsync(HttpStatusCode.BadRequest,
            await SendRawAsync(client, HttpMethod.Post, Path(ws), JsonSerializer.Serialize(Body()).Replace("\"BALANCED\"", "1")));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(Guid.Empty), Body(), Cancellation));

        // A failed update applies nothing.
        Assert.Equal(existing, Detail(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(Path(ws, existing.Id), Cancellation))) with
            { KnowledgeSourceIds = existing.KnowledgeSourceIds });
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.AiAgents.CountAsync(a => a.WorkspaceId == ws, Cancellation));
        Assert.False(await db.AiAgentKnowledgeSources.AnyAsync(s => s.WorkspaceId == ws, Cancellation));
    }

    [Fact]
    public async Task Tenant_isolation_authentication_csrf_and_paging()
    {
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var ws = await WorkspaceAsync(owner);
        var otherWs = await WorkspaceAsync(outsider);
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add(Detail(await AssertStatusAsync(HttpStatusCode.Created, await client.PostAsJsonAsync(Path(ws), Body($"Bot {i}"), Cancellation))).Id);
        ids.Sort();

        using (var first = JsonDocument.Parse(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(Path(ws) + "?take=2", Cancellation))))
        {
            Assert.Equal(["items", "nextAfter"], first.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            var items = first.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(ids[..2], items.Select(e => e.GetProperty("id").GetGuid()));
            Assert.All(items, e => Assert.Equal(SummaryFields, e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
            Assert.Equal(ids[1], first.RootElement.GetProperty("nextAfter").GetGuid());
        }
        using (var last = JsonDocument.Parse(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"{Path(ws)}?take=2&after={ids[1]}", Cancellation))))
        {
            Assert.Equal([ids[2]], last.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
            Assert.Equal(JsonValueKind.Null, last.RootElement.GetProperty("nextAfter").ValueKind);
        }
        foreach (var query in new[] { "?take=0", "?take=101", $"?after={Guid.Empty}" })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.GetAsync(Path(ws) + query, Cancellation));

        await AuthenticateAsync(client, outsider);
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(ws), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(ws, ids[0]), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(otherWs, ids[0]), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(Path(ws), Body(), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(Path(ws, ids[0]), Body(), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(Path(otherWs, ids[0]), Body(), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.DeleteAsync(Path(otherWs, ids[0]), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.DeleteAsync(Path(ws, ids[0]), Cancellation));
        // Static validation precedes membership (as for workspace rename), so a nonmember's invalid body is 400, not 404.
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(ws), Body(tier: "fast"), Cancellation));
        using (var empty = JsonDocument.Parse(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(Path(otherWs), Cancellation))))
            Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());

        await AuthenticateAsync(client, owner);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(ws), Body(), Cancellation));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(Path(ws, ids[0]), Body(), Cancellation));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.DeleteAsync(Path(ws, ids[0]), Cancellation));
        using var anonymous = Client(factory);
        await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.GetAsync(Path(ws), Cancellation));
        await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.PostAsJsonAsync(Path(ws), Body(), Cancellation));

        await using var db = fixture.CreateContext();
        Assert.Equal(3, await db.AiAgents.CountAsync(a => a.WorkspaceId == ws, Cancellation));
        Assert.False(await db.AiAgents.AnyAsync(a => a.WorkspaceId == otherWs, Cancellation));
    }

    [Fact]
    public async Task Oversized_bodies_are_refused_and_a_selection_emptied_by_source_deletion_reads_back_as_no_knowledge()
    {
        var owner = await UserAsync();
        var ws = await WorkspaceAsync(owner);
        var source = await SourceAsync(ws);
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);

        var huge = Body(scope: "SELECTED_SOURCES", sources: Enumerable.Range(0, 3000).Select(_ => Guid.NewGuid()).ToArray());
        using (var tooLarge = await client.PostAsJsonAsync(Path(ws), huge, Cancellation))
            Assert.Contains(tooLarge.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge });
        // The server (Kestrel) enforces the 64 KB limit from endpoint metadata before the body is read.
        var writes = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("ai-agents") == true
                && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods is ["POST"] or ["PUT"]).ToArray();
        Assert.Equal(3, writes.Length); // create, update, retrieval preview
        Assert.All(writes, e => Assert.Equal(64 * 1024, e.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()?.MaxRequestBodySize));

        var agent = Detail(await AssertStatusAsync(HttpStatusCode.Created,
            await client.PostAsJsonAsync(Path(ws), Body(scope: "SELECTED_SOURCES", sources: [source]), Cancellation)));
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"KnowledgeSources\" WHERE \"Id\" = {source}", Cancellation);
        var emptied = Detail(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(Path(ws, agent.Id), Cancellation)));
        Assert.Equal(("SELECTED_SOURCES", 0), (emptied.KnowledgeScope, emptied.KnowledgeSourceIds.Count));
        // Saving it back unchanged is invalid: an admin must pick sources again or switch to ALL_SOURCES.
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(Path(ws, agent.Id),
            Body(scope: "SELECTED_SOURCES", sources: []), Cancellation));
        await AssertStatusAsync(HttpStatusCode.OK, await client.PutAsJsonAsync(Path(ws, agent.Id), Body(), Cancellation));
    }

    private sealed record Csrf(string Token);
}
