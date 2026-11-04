using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MAIC.Application.Knowledge;
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

public sealed class KnowledgeSourceEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Emoji = "\ud83d\ude42"; // one code point, two UTF-16 units
    private const string Url = "https://example.com/docs";
    private static readonly string[] DetailFields =
        ["createdAt", "enabled", "id", "kind", "name", "textContent", "websiteUrl", "workspaceId"];
    private static readonly string[] SummaryFields = ["createdAt", "enabled", "id", "kind", "name", "workspaceId"];
    private static readonly string[] PageFields = ["items", "nextAfter"];
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
        // These tests exercise validation with many writes per user; rate limiting is covered by KnowledgeDocumentEndpointTests.
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
        var workspace = new Workspace("Knowledge HTTP tests");
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, owner, WorkspaceRole.OWNER));
        db.WorkspaceMembers.AddRange(members.Select(m => new WorkspaceMember(workspace.Id, m.UserId, m.Role)));
        await db.SaveChangesAsync(Cancellation);
        return workspace.Id;
    }

    private static async Task AuthenticateAsync(HttpClient client, Guid userId)
    {
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        var csrf = await client.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
    }

    private static string Path(Guid workspaceId) => $"/workspaces/{workspaceId}/knowledge-sources";
    private static string Path(Guid workspaceId, Guid sourceId) => $"{Path(workspaceId)}/{sourceId}";

    /// <summary>Every response under the collection must be uncacheable, whatever its status.</summary>
    private static async Task<string> AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Cancellation);
            Assert.True(response.StatusCode == expected,
                $"Expected {(int)expected}, got {(int)response.StatusCode} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
            Assert.True(response.Headers.CacheControl?.NoStore == true,
                $"Missing Cache-Control: no-store on {(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}");
            return body;
        }
    }

    private static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, string path, string json)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        return await client.SendAsync(request, Cancellation);
    }

    private static KnowledgeSourceView Detail(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(DetailFields, document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        return JsonSerializer.Deserialize<KnowledgeSourceView>(json, JsonSerializerOptions.Web)!;
    }

    private static KnowledgeSourceSummary Summary(KnowledgeSourceView s) => new(s.Id, s.WorkspaceId, s.Kind, s.Name, s.Enabled, s.CreatedAt);

    private static async Task<KnowledgeSourceView> CreateAsync(HttpClient client, Guid workspaceId, string kind, string name,
        string? textContent = null, string? websiteUrl = null)
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var response = await client.PostAsJsonAsync(Path(workspaceId), new { kind, name, textContent, websiteUrl }, Cancellation);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}: {body}");
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        var source = Detail(body);
        Assert.Equal(Path(workspaceId, source.Id), response.Headers.Location!.OriginalString);
        Assert.Equal(workspaceId, source.WorkspaceId);
        Assert.Equal(kind, source.Kind);
        Assert.True(source.Enabled);
        Assert.Equal('7', source.Id.ToString()[14]); // UUIDv7
        Assert.Equal(TimeSpan.Zero, source.CreatedAt.Offset);
        Assert.InRange(source.CreatedAt, before, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(textContent, source.TextContent);
        Assert.Equal(websiteUrl, source.WebsiteUrl);
        // The creation response is exactly what later reads return (including CreatedAt precision).
        Assert.Equal(source, await GetAsync(client, workspaceId, source.Id));
        return source;
    }

    private static async Task<KnowledgeSourceView> GetAsync(HttpClient client, Guid workspaceId, Guid sourceId) =>
        Detail(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(Path(workspaceId, sourceId), Cancellation)));

    private static async Task<KnowledgeSourceView> UpdateAsync(HttpClient client, Guid workspaceId, Guid sourceId, object body)
    {
        var updated = Detail(await AssertStatusAsync(HttpStatusCode.OK,
            await client.PutAsJsonAsync(Path(workspaceId, sourceId), body, Cancellation)));
        Assert.Equal(updated, await GetAsync(client, workspaceId, sourceId));
        return updated;
    }

    private static async Task<(Page Page, string Json)> ListRawAsync(HttpClient client, Guid workspaceId, string query = "")
    {
        var json = await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(Path(workspaceId) + query, Cancellation));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(PageFields, document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("items").ValueKind);
        // List items are metadata only: never textContent or websiteUrl.
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            Assert.Equal(SummaryFields, item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        return (JsonSerializer.Deserialize<Page>(json, JsonSerializerOptions.Web)!, json);
    }

    private static async Task<Page> ListAsync(HttpClient client, Guid workspaceId, string query = "") =>
        (await ListRawAsync(client, workspaceId, query)).Page;

    private static void AssertExact(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), "Text must round-trip without trimming or normalization.");
    }

    [Fact]
    public async Task Every_member_role_manages_every_kind_while_nonmembers_revoked_members_and_wrong_routes_get_404()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var admin = await UserAsync();
        var agent = await UserAsync();
        var outsider = await UserAsync();
        var workspace = await WorkspaceAsync(owner, (admin, WorkspaceRole.ADMIN), (agent, WorkspaceRole.AGENT));
        // The owner of `workspace` is also a member of `otherWorkspace`, so wrong-route checks exercise a dual member.
        var otherWorkspace = await WorkspaceAsync(outsider, (owner, WorkspaceRole.AGENT));
        var created = new List<KnowledgeSourceView>();

        foreach (var actor in new[] { owner, admin, agent })
        {
            await AuthenticateAsync(client, actor);
            var text = await CreateAsync(client, workspace, "TEXT", "  Handbook  ", textContent: "Refunds take 5 days.");
            Assert.Equal("Handbook", text.Name);
            var site = await CreateAsync(client, workspace, "WEBSITE", "Docs", websiteUrl: Url);
            var file = await CreateAsync(client, workspace, "FILE", "Upload");
            foreach (var source in new[] { text, site, file })
            {
                var updated = await UpdateAsync(client, workspace, source.Id, new { name = "  Renamed  ", enabled = false });
                Assert.Equal(source with { Name = "Renamed", Enabled = false }, updated);
                created.Add(updated);
            }
            var listed = await ListAsync(client, workspace, "?take=100");
            Assert.Equal(created.Select(Summary).OrderBy(s => s.Id), listed.Items);
            Assert.Null(listed.NextAfter);
        }

        await AuthenticateAsync(client, owner);
        var foreign = await CreateAsync(client, otherWorkspace, "TEXT", "Other tenant", textContent: "Other body");
        var foreignSite = await CreateAsync(client, otherWorkspace, "WEBSITE", "Other site", websiteUrl: Url);
        // Even a member of both workspaces cannot reach a source through the other workspace's route, and a payload
        // that does not fit the source's kind is still answered 404 (existence and kind are never revealed first).
        foreach (var (route, source) in new[] { (otherWorkspace, created[0]), (otherWorkspace, created[1]), (otherWorkspace, created[2]),
                     (workspace, foreign), (workspace, foreignSite) })
        {
            var wrong = Path(route, source.Id);
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(wrong, Cancellation));
            foreach (var body in new object[]
                     {
                         new { name = "Moved", enabled = true }, new { name = "Moved", textContent = "Moved" },
                         new { name = "Moved", websiteUrl = "https://example.com/moved" }
                     })
                await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(wrong, body, Cancellation));
        }
        Assert.Equal(new[] { foreign, foreignSite }.Select(Summary).OrderBy(s => s.Id), (await ListAsync(client, otherWorkspace)).Items);
        Assert.Equal(foreign, await GetAsync(client, otherWorkspace, foreign.Id));
        Assert.Equal(foreignSite, await GetAsync(client, otherWorkspace, foreignSite.Id));
        foreach (var source in created) Assert.Equal(source, await GetAsync(client, workspace, source.Id));
        foreach (var missing in new[] { Guid.NewGuid(), Guid.CreateVersion7() })
        {
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(workspace, missing), Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(Path(workspace, missing),
                new { name = "Ghost" }, Cancellation));
        }
        // As for Channels, creating under the all-zero workspace ID is invalid input; other routes treat it as unknown.
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(Guid.Empty),
            new { kind = "FILE", name = "Ghost" }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(Guid.Empty), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(Guid.Empty, created[0].Id), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(Path(Guid.Empty, created[0].Id),
            new { name = "Ghost" }, Cancellation));

        await AuthenticateAsync(client, outsider);
        foreach (var deniedWorkspace in new[] { workspace, Guid.NewGuid() })
            foreach (var source in created.Take(3))
                await AssertManagementNotFoundAsync(client, deniedWorkspace, source.Id);

        // A revoked member immediately loses every operation, including on sources they created.
        await AuthenticateAsync(client, admin);
        await using (var db = fixture.CreateContext())
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace && m.UserId == admin).ExecuteDeleteAsync(Cancellation);
        foreach (var source in created.Skip(3).Take(3))
            await AssertManagementNotFoundAsync(client, workspace, source.Id);

        await AuthenticateAsync(client, owner);
        foreach (var source in created) Assert.Equal(source, await GetAsync(client, workspace, source.Id));
        Assert.Equal(created.Select(Summary).OrderBy(s => s.Id), (await ListAsync(client, workspace)).Items);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.KnowledgeSources.AnyAsync(s => s.Name == "Intruder" || s.Name == "Moved" || s.Name == "Ghost", Cancellation));
        Assert.Equal(9, await verify.KnowledgeSources.CountAsync(s => s.WorkspaceId == workspace, Cancellation));
        Assert.Equal(2, await verify.KnowledgeSources.CountAsync(s => s.WorkspaceId == otherWorkspace, Cancellation));
    }

    private static async Task AssertManagementNotFoundAsync(HttpClient client, Guid workspaceId, Guid sourceId)
    {
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(workspaceId), Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(workspaceId) + $"?after={sourceId}&take=1", Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(workspaceId, sourceId), Cancellation));
        foreach (var body in new object[]
                 {
                     new { kind = "FILE", name = "Intruder" }, new { kind = "TEXT", name = "Intruder", textContent = "Intruder" },
                     new { kind = "WEBSITE", name = "Intruder", websiteUrl = Url }
                 })
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(Path(workspaceId), body, Cancellation));
        // Both payload kinds are tried so one of them always mismatches the hidden source's kind: still 404, never 400.
        foreach (var body in new object[]
                 {
                     new { name = "Intruder", enabled = false }, new { name = "Intruder", textContent = "Intruder" },
                     new { name = "Intruder", websiteUrl = Url }
                 })
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync(Path(workspaceId, sourceId), body, Cancellation));
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401_and_missing_or_foreign_csrf_tokens_reject_mutations_without_changes()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        using var anonymous = Client(factory);
        var owner = await UserAsync();
        var secondMember = await UserAsync();
        var workspace = await WorkspaceAsync(owner, (secondMember, WorkspaceRole.ADMIN));
        var anonymousCsrf = (await anonymous.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation))!.Token;
        await AuthenticateAsync(client, owner);
        var source = await CreateAsync(client, workspace, "TEXT", "Original", textContent: "Body");

        foreach (var sourcePath in new[] { Path(workspace, source.Id), Path(workspace, Guid.NewGuid()) })
        {
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.GetAsync(Path(workspace), Cancellation));
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.GetAsync(sourcePath, Cancellation));
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.PostAsJsonAsync(Path(workspace),
                new { kind = "FILE", name = "Anonymous" }, Cancellation));
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.PutAsJsonAsync(sourcePath,
                new { enabled = false }, Cancellation));
        }
        // A valid anonymous CSRF token does not authenticate anything.
        anonymous.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymousCsrf);
        await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.PutAsJsonAsync(Path(workspace, source.Id),
            new { enabled = false }, Cancellation));

        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AssertCsrfRejectedAsync(client, workspace, source.Id);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "not-a-token");
        await AssertCsrfRejectedAsync(client, workspace, source.Id);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymousCsrf);
        await AssertCsrfRejectedAsync(client, workspace, source.Id);
        // The owner's token presented by another (authorized) member is bound to the wrong user.
        await AuthenticateAsync(client, owner);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", secondMember.ToString());
        await AssertCsrfRejectedAsync(client, workspace, source.Id);

        await AuthenticateAsync(client, owner);
        Assert.Equal(source, await GetAsync(client, workspace, source.Id));
        Assert.Equal(new[] { Summary(source) }, (await ListAsync(client, workspace)).Items);
        await using (var db = fixture.CreateContext())
            Assert.Equal(1, await db.KnowledgeSources.CountAsync(s => s.WorkspaceId == workspace, Cancellation));

        // A user id that does not exist never authenticates.
        await AuthenticateAsync(client, Guid.NewGuid());
        await AssertStatusAsync(HttpStatusCode.Unauthorized, await client.GetAsync(Path(workspace), Cancellation));
        await AssertStatusAsync(HttpStatusCode.Unauthorized, await client.PostAsJsonAsync(Path(workspace),
            new { kind = "FILE", name = "Unknown user" }, Cancellation));
    }

    private static async Task AssertCsrfRejectedAsync(HttpClient client, Guid workspaceId, Guid sourceId)
    {
        foreach (var body in new object[]
                 {
                     new { kind = "FILE", name = "Forged" }, new { kind = "TEXT", name = "Forged", textContent = "Forged" },
                     new { kind = "WEBSITE", name = "Forged", websiteUrl = Url }
                 })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(workspaceId), body, Cancellation));
        foreach (var body in new object[] { new { name = "Forged", enabled = false }, new { textContent = "Forged" } })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(Path(workspaceId, sourceId), body, Cancellation));
    }

    [Fact]
    public async Task Strict_DTOs_reject_server_owned_or_unknown_fields_and_kind_must_be_an_exact_name()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var source = await CreateAsync(client, workspace, "TEXT", "Original", textContent: "Body");
        var path = Path(workspace);
        var sourcePath = Path(workspace, source.Id);

        foreach (var field in new[] { "workspaceId", "id", "sourceId", "userId", "actorId", "createdByUserId", "createdAt", "enabled", "type" })
        {
            var body = new Dictionary<string, object> { ["kind"] = "FILE", ["name"] = "Forged", [field] = field == "enabled" ? false : Guid.NewGuid().ToString() };
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(path, body, Cancellation));
        }
        foreach (var field in new[] { "kind", "workspaceId", "id", "sourceId", "userId", "actorId", "createdAt", "type" })
        {
            var body = new Dictionary<string, object> { ["name"] = "Forged", [field] = field == "kind" ? "TEXT" : Guid.NewGuid().ToString() };
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(sourcePath, body, Cancellation));
        }
        // Kind never changes after creation, not even to the same value or together with a matching payload.
        foreach (var json in new[] { """{"kind":"TEXT"}""", """{"kind":"WEBSITE","websiteUrl":"https://example.com/"}""", """{"kind":"FILE"}""" })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Put, sourcePath, json));

        foreach (var kind in new[]
                 {
                     "0", "1", "2", "7", "-1", "1.0", "\"1\"", "\"text\"", "\"Text\"", "\"tExT\"", "\" TEXT\"", "\"TEXT \"", "\"TEXT\\u0000\"",
                     "\"PDF\"", "\"URL\"", "\"\"", "null", "true", "[\"TEXT\"]", "{}"
                 })
            await AssertStatusAsync(HttpStatusCode.BadRequest,
                await SendRawAsync(client, HttpMethod.Post, path, $$"""{"kind":{{kind}},"name":"Kinded","textContent":"Body"}"""));
        await AssertStatusAsync(HttpStatusCode.BadRequest,
            await SendRawAsync(client, HttpMethod.Post, path, """{"name":"No kind","textContent":"Body"}"""));

        foreach (var malformed in new[]
                 {
                     "", "null", "[]", "{", "\"TEXT\"", """{"kind":"FILE","name":"x",}""", """{"kind":"FILE","name":"x"} trailing""",
                     """{"kind":"FILE","name":17}""", """{"kind":"TEXT","name":"x","textContent":17}""",
                     """{"kind":"WEBSITE","name":"x","websiteUrl":true}""", """{"kind":"FILE","name":["x"]}"""
                 })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Post, path, malformed));
        foreach (var malformed in new[]
                 {
                     "", "null", "[]", "{", """{"name":17}""", """{"enabled":"false"}""", """{"enabled":0}""", """{"enabled":"yes"}""",
                     """{"textContent":17}""", """{"websiteUrl":true}""", """{"name":"x"} trailing"""
                 })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Put, sourcePath, malformed));

        // Invalid UTF-8 in the request body is a client error, not a server error.
        var invalidUtf8 = Encoding.UTF8.GetBytes("""{"kind":"FILE","name":"x""").Concat(new byte[] { 0xC3, 0x28 })
            .Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        using (var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(invalidUtf8) })
        {
            request.Content.Headers.ContentType = new("application/json");
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.SendAsync(request, Cancellation));
        }

        Assert.Equal(source, await GetAsync(client, workspace, source.Id));
        await using var db = fixture.CreateContext();
        Assert.Equal(source.Id, Assert.Single(await db.KnowledgeSources.AsNoTracking()
            .Where(s => s.WorkspaceId == workspace).Select(s => s.Id).ToListAsync(Cancellation)));
    }

    [Fact]
    public async Task Create_accepts_only_the_payload_shape_of_each_kind_and_has_no_uniqueness_rules()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var accepted = new List<KnowledgeSourceView>();
        string?[] texts = [null, "Body", "", "   "];
        string?[] urls = [null, Url, "", "not a url"];

        foreach (var kind in new[] { "FILE", "TEXT", "WEBSITE" })
        foreach (var textContent in texts)
        foreach (var websiteUrl in urls)
        {
            var valid = kind switch
            {
                "FILE" => textContent is null && websiteUrl is null,
                "TEXT" => textContent == "Body" && websiteUrl is null,
                _ => textContent is null && websiteUrl == Url
            };
            if (valid) accepted.Add(await CreateAsync(client, workspace, kind, "Same name", textContent, websiteUrl));
            else
                await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(workspace),
                    new { kind, name = "Rejected", textContent, websiteUrl }, Cancellation));
        }
        Assert.Equal(new[] { "FILE", "TEXT", "WEBSITE" }, accepted.Select(s => s.Kind));

        foreach (var kind in new[] { "FILE", "TEXT", "WEBSITE" })
        {
            var textContent = kind == "TEXT" ? "Body" : null;
            var websiteUrl = kind == "WEBSITE" ? Url : null;
            foreach (var name in new[] { null, "", "   " })
                await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(workspace),
                    new { kind, name, textContent, websiteUrl }, Cancellation));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(Path(workspace),
                new { kind, textContent, websiteUrl }, Cancellation));
        }

        // Explicit JSON null equals an omitted payload field.
        foreach (var json in new[] { """{"kind":"FILE","name":"Omitted"}""", """{"kind":"FILE","name":"Nulls","textContent":null,"websiteUrl":null}""" })
        {
            var created = Detail(await AssertStatusAsync(HttpStatusCode.Created, await SendRawAsync(client, HttpMethod.Post, Path(workspace), json)));
            Assert.Equal(("FILE", (string?)null, (string?)null), (created.Kind, created.TextContent, created.WebsiteUrl));
            accepted.Add(created);
        }

        // No uniqueness on name, text or URL: identical requests create distinct sources.
        foreach (var source in accepted.Take(3).ToArray())
            accepted.Add(await CreateAsync(client, workspace, source.Kind, source.Name, source.TextContent, source.WebsiteUrl));
        Assert.Equal(accepted.Count, accepted.Select(s => s.Id).Distinct().Count());
        Assert.Equal(accepted.Select(Summary).OrderBy(s => s.Id), (await ListAsync(client, workspace)).Items);
        await using var db = fixture.CreateContext();
        Assert.Equal(accepted.Count, await db.KnowledgeSources.CountAsync(s => s.WorkspaceId == workspace, Cancellation));
    }

    [Fact]
    public async Task Updates_are_partial_atomic_and_never_move_a_payload_to_another_kind()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var text = await CreateAsync(client, workspace, "TEXT", "Text", textContent: "Body");
        var site = await CreateAsync(client, workspace, "WEBSITE", "Site", websiteUrl: Url);
        var file = await CreateAsync(client, workspace, "FILE", "File");

        foreach (var source in new[] { text, site, file })
        {
            var sourcePath = Path(workspace, source.Id);
            foreach (var empty in new[] { "", "null", "{}", """{"name":null,"enabled":null,"textContent":null,"websiteUrl":null}""" })
                await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Put, sourcePath, empty));
            foreach (var body in new object[]
                     {
                         new { name = "Both", enabled = false, textContent = "Body", websiteUrl = Url },
                         new { textContent = "Body", websiteUrl = Url },
                         // One invalid value rejects the whole request, including otherwise valid fields.
                         new { name = " ", enabled = false }, new { name = new string('n', 201), enabled = false },
                         new { name = "Renamed", textContent = "" }, new { name = "Renamed", textContent = "bad\0text" },
                         new { name = "Renamed", websiteUrl = "ftp://example.com/" }, new { enabled = false, websiteUrl = "https://example.com/#x" }
                     })
                await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(sourcePath, body, Cancellation));
        }

        // A payload that does not fit the kind rejects the whole update: no rename and no enable change is applied.
        foreach (var (source, body) in new (KnowledgeSourceView, object)[]
                 {
                     (site, new { name = "Moved", enabled = false, textContent = "Body" }),
                     (file, new { name = "Moved", enabled = false, textContent = "Body" }),
                     (text, new { name = "Moved", enabled = false, websiteUrl = Url }),
                     (file, new { name = "Moved", enabled = false, websiteUrl = Url }),
                     (site, new { textContent = "Body" }), (text, new { websiteUrl = Url })
                 })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(Path(workspace, source.Id), body, Cancellation));
        foreach (var source in new[] { text, site, file })
            Assert.Equal(source, await GetAsync(client, workspace, source.Id));

        // Each partial update changes only the supplied fields; kind, id, workspace and creation time never change.
        var renamed = await UpdateAsync(client, workspace, text.Id, new { name = "  Renamed text  " });
        Assert.Equal(text with { Name = "Renamed text" }, renamed);
        var rewritten = await UpdateAsync(client, workspace, text.Id, new { textContent = "  New body\r\n" });
        Assert.Equal(renamed with { TextContent = "  New body\r\n" }, rewritten);
        var disabled = await UpdateAsync(client, workspace, text.Id, new { enabled = false });
        Assert.Equal(rewritten with { Enabled = false }, disabled);
        var nullsAreOmitted = await UpdateAsync(client, workspace, text.Id,
            new { name = (string?)null, enabled = (bool?)true, textContent = (string?)null, websiteUrl = (string?)null });
        Assert.Equal(disabled with { Enabled = true }, nullsAreOmitted);

        const string exactUrl = "HTTP://Example.COM:8080/Path/../a?q=1&r=%20x";
        var moved = await UpdateAsync(client, workspace, site.Id, new { websiteUrl = exactUrl });
        Assert.Equal(site with { WebsiteUrl = exactUrl }, moved);
        var all = await UpdateAsync(client, workspace, site.Id, new { name = "Site 2", enabled = false, websiteUrl = Url });
        Assert.Equal(site with { Name = "Site 2", Enabled = false }, all);
        var fileUpdated = await UpdateAsync(client, workspace, file.Id, new { name = "File 2", enabled = false });
        Assert.Equal(file with { Name = "File 2", Enabled = false }, fileUpdated);

        Assert.Equal(new[] { nullsAreOmitted, all, fileUpdated }.Select(Summary).OrderBy(s => s.Id), (await ListAsync(client, workspace)).Items);
    }

    [Fact]
    public async Task Disabled_sources_keep_their_payload_and_remain_readable_listable_and_updatable()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var text = await CreateAsync(client, workspace, "TEXT", "Text", textContent: " Body \n");
        var site = await CreateAsync(client, workspace, "WEBSITE", "Site", websiteUrl: Url);
        var file = await CreateAsync(client, workspace, "FILE", "File");

        foreach (var source in new[] { text, site, file })
        {
            Assert.Equal(source with { Enabled = false }, await UpdateAsync(client, workspace, source.Id, new { enabled = false }));
            // Disabling twice is a no-op, not an error.
            Assert.Equal(source with { Enabled = false }, await UpdateAsync(client, workspace, source.Id, new { enabled = false }));
        }
        var listed = await ListAsync(client, workspace);
        Assert.Equal(3, listed.Items.Length);
        Assert.All(listed.Items, s => Assert.False(s.Enabled));

        // Disabled sources stay updatable, keep their disabled state and still enforce kind rules.
        var textEdited = await UpdateAsync(client, workspace, text.Id, new { name = "Text 2", textContent = "Edited while disabled" });
        Assert.Equal(text with { Name = "Text 2", Enabled = false, TextContent = "Edited while disabled" }, textEdited);
        var siteEdited = await UpdateAsync(client, workspace, site.Id, new { websiteUrl = "https://example.com/edited" });
        Assert.Equal(site with { Enabled = false, WebsiteUrl = "https://example.com/edited" }, siteEdited);
        var fileEdited = await UpdateAsync(client, workspace, file.Id, new { name = "File 2" });
        Assert.Equal(file with { Name = "File 2", Enabled = false }, fileEdited);
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(Path(workspace, file.Id),
            new { enabled = true, textContent = "Not for files" }, Cancellation));
        Assert.Equal(fileEdited, await GetAsync(client, workspace, file.Id));

        foreach (var source in new[] { textEdited, siteEdited, fileEdited })
            Assert.Equal(source with { Enabled = true }, await UpdateAsync(client, workspace, source.Id, new { enabled = true }));
        Assert.All((await ListAsync(client, workspace)).Items, s => Assert.True(s.Enabled));
        Assert.Equal(siteEdited with { Enabled = true }, await GetAsync(client, workspace, site.Id));
    }

    [Fact]
    public async Task Limits_count_UTF16_units_and_text_and_urls_round_trip_exactly()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var path = Path(workspace);

        // Names are trimmed of Unicode whitespace and bounded after trimming.
        var longest = new string('n', KnowledgeSource.MaxNameLength);
        Assert.Equal(longest, (await CreateAsync(client, workspace, "FILE", "\u00a0 " + longest + " \u3000")).Name);
        var emojiName = new string('n', 198) + Emoji;
        var file = await CreateAsync(client, workspace, "FILE", emojiName);
        Assert.Equal(emojiName, file.Name);
        foreach (var name in new[]
                 {
                     new string('n', 201), " " + new string('n', 201) + " ", new string('n', 199) + Emoji, "", " \u00a0\u3000 ",
                     "\tName", "Name\n", "Na\0me", "Na\u001fme", "Na\u007fme", "Na\u0085me", "Na\u009fme"
                 })
        {
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(path, new { kind = "FILE", name }, Cancellation));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(Path(workspace, file.Id),
                new { name, enabled = false }, Cancellation));
        }
        Assert.Equal(file, await GetAsync(client, workspace, file.Id));

        // Text is kept exactly: leading/trailing whitespace, CR/LF/CRLF, tabs, BOM, zero-width and combining sequences.
        var exact = ExactText();
        Assert.Equal(KnowledgeSource.MaxTextContentLength, exact.Length);
        var text = await CreateAsync(client, workspace, "TEXT", "Exact", textContent: exact);
        AssertExact(exact, text.TextContent);
        AssertExact(exact, (await GetAsync(client, workspace, text.Id)).TextContent);
        var surrogateAtLimit = new string('y', KnowledgeSource.MaxTextContentLength - 2) + Emoji;
        var updated = await UpdateAsync(client, workspace, text.Id, new { textContent = surrogateAtLimit });
        AssertExact(surrogateAtLimit, updated.TextContent);
        foreach (var invalid in new[]
                 {
                     new string('y', KnowledgeSource.MaxTextContentLength + 1), new string('y', KnowledgeSource.MaxTextContentLength - 1) + Emoji,
                     "", " ", " \r\n\t ", "\u00a0\u2003\u3000\u2028", "bad\0text", "bad\u0001text", "bad\u0008text", "bad\u000btext",
                     "bad\u000ctext", "bad\u001ftext", "bad\u007ftext", "bad\u0080text", "bad\u0085text", "bad\u009ftext"
                 })
        {
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(path,
                new { kind = "TEXT", name = "Invalid text", textContent = invalid }, Cancellation));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(Path(workspace, text.Id),
                new { name = "Changed", enabled = false, textContent = invalid }, Cancellation));
        }
        Assert.Equal(updated, await GetAsync(client, workspace, text.Id));

        // URLs are stored exactly as supplied (no normalization) and bounded in UTF-16 units.
        var longestUrl = "https://example.com/" + new string('a', KnowledgeSource.MaxWebsiteUrlLength - 20);
        var emojiUrl = "https://example.com/" + new string('a', KnowledgeSource.MaxWebsiteUrlLength - 22) + Emoji;
        Assert.Equal(KnowledgeSource.MaxWebsiteUrlLength, longestUrl.Length);
        Assert.Equal(KnowledgeSource.MaxWebsiteUrlLength, emojiUrl.Length);
        var site = await CreateAsync(client, workspace, "WEBSITE", "Longest", websiteUrl: longestUrl);
        foreach (var url in new[]
                 {
                     emojiUrl, "HTTPS://Example.COM", "http://example.com", "https://example.com:8443/a/../b/?q=1&r=%20x",
                     "https://example.com/users/@me?mail=a@b", "https://b\u00fccher.example/", "https://\u00f6rnek.com.tr/yol",
                     "https://[2001:db8::1]/", "https://127.0.0.1:8080/x"
                 })
            Assert.Equal(url, (await CreateAsync(client, workspace, "WEBSITE", "Exact URL", websiteUrl: url)).WebsiteUrl);
        foreach (var invalid in new[]
                 {
                     longestUrl + "a", "https://example.com/" + new string('a', KnowledgeSource.MaxWebsiteUrlLength - 21) + Emoji,
                     "", " ", "example.com", "//example.com/x", "ftp://example.com/", "javascript:alert(1)", "file:///etc/passwd",
                     "https://", "https:///path", "https://?q=1", "https://user:pass@example.com/", "https://user@example.com/",
                     "https://@example.com/", "https://example.com/#top", "https://example.com#", "https://example.com\\path",
                     "http:\\\\example.com", "https://exa mple.com/", " https://example.com/", "https://example.com/ ",
                     "https://example.com/\t", "https://example.com/\n", "https://example.com/\0", "https://example.com/\u007f",
                     "https://example.com/\u0085", "https://example.com/\u00a0", "https://example.com/\u2003",
                     "https://example.com/\u200b", "https://example.com/\u202e", "https://example.com/\ufeff", "https://example.com/\u00ad",
                     // Fullwidth look-alikes that IDN mapping turns into @ / # ? \ : inside the host.
                     "https://example\uff20evil.com/", "https://user\uff20example.com/", "https://example\uff0fevil.com/",
                     "https://example\uff03evil.com/", "https://example\uff1fevil.com/", "https://example\uff3cevil.com/",
                     "https://example\uff1aevil.com/"
                 })
        {
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(path,
                new { kind = "WEBSITE", name = "Invalid URL", websiteUrl = invalid }, Cancellation));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync(Path(workspace, site.Id),
                new { name = "Changed", enabled = false, websiteUrl = invalid }, Cancellation));
        }
        Assert.Equal(site, await GetAsync(client, workspace, site.Id));

        // Malformed UTF-16 can only arrive through JSON escapes; it is rejected in every string field.
        foreach (var escaped in new[] { @"\ud800", @"\udc00", @"a\ud800b", @"\udc00\ud800", @"\ud83d" })
        {
            foreach (var json in new[]
                     {
                         $$"""{"kind":"FILE","name":"{{escaped}}"}""",
                         $$"""{"kind":"TEXT","name":"Lone","textContent":"{{escaped}}"}""",
                         $$"""{"kind":"WEBSITE","name":"Lone","websiteUrl":"https://example.com/{{escaped}}"}"""
                     })
                await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Post, path, json));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Put, Path(workspace, text.Id),
                $$"""{"name":"Changed","textContent":"{{escaped}}"}"""));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Put, Path(workspace, text.Id),
                $$"""{"name":"{{escaped}}"}"""));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Put, Path(workspace, site.Id),
                $$"""{"websiteUrl":"https://example.com/{{escaped}}"}"""));
        }
        var pair = Detail(await AssertStatusAsync(HttpStatusCode.Created, await SendRawAsync(client, HttpMethod.Post, path,
            """{"kind":"TEXT","name":"Pair \ud83d\ude42","textContent":"\ud83d\ude42 escaped pair\t"}""")));
        Assert.Equal(("Pair " + Emoji, Emoji + " escaped pair\t"), (pair.Name, pair.TextContent));
        Assert.Equal(updated, await GetAsync(client, workspace, text.Id));
        Assert.Equal(site, await GetAsync(client, workspace, site.Id));

        await using var db = fixture.CreateContext();
        Assert.Equal(14, await db.KnowledgeSources.CountAsync(s => s.WorkspaceId == workspace, Cancellation));
    }

    private static string ExactText()
    {
        var head = "\t leading tab and space\r\nCRLF\nLF\rCR e\u0301 \u00e9 \ufeffBOM \u200bZWSP \u2028LS "
            + string.Concat(Enumerable.Repeat(Emoji, 1000)) + " ";
        const string tail = " \r\n\t";
        return head + new string('x', KnowledgeSource.MaxTextContentLength - head.Length - tail.Length) + tail;
    }

    [Fact]
    public async Task Listing_is_metadata_only_keyset_paged_in_stable_id_order_and_bounded()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        var other = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);

        foreach (var query in new[] { "", $"?after={Guid.NewGuid()}", "?take=1" })
        {
            var (emptyPage, emptyJson) = await ListRawAsync(client, workspace, query);
            Assert.Empty(emptyPage.Items);
            Assert.Null(emptyPage.NextAfter);
            using var document = JsonDocument.Parse(emptyJson);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextAfter").ValueKind);
        }

        var sources = new List<KnowledgeSourceView>();
        for (var index = 0; index < 7; index++)
            sources.Add((index % 3) switch
            {
                0 => await CreateAsync(client, workspace, "TEXT", $"Text {index}", textContent: $"secret-marker text {index}"),
                1 => await CreateAsync(client, workspace, "WEBSITE", $"Site {index}", websiteUrl: $"https://example.com/secret-marker/{index}"),
                _ => await CreateAsync(client, workspace, "FILE", $"File {index}")
            });
        var foreign = await CreateAsync(client, other, "TEXT", "Foreign", textContent: "secret-marker foreign");
        var expected = sources.Select(Summary).OrderBy(s => s.Id).ToArray();

        var (all, allJson) = await ListRawAsync(client, workspace);
        Assert.Equal(expected, all.Items);
        Assert.Null(all.NextAfter);
        Assert.DoesNotContain("secret-marker", allJson, StringComparison.Ordinal);
        Assert.DoesNotContain("textContent", allJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("websiteUrl", allJson, StringComparison.OrdinalIgnoreCase);

        var page1 = await ListAsync(client, workspace, "?take=3");
        Assert.Equal(expected.Take(3), page1.Items);
        Assert.Equal(page1.Items[^1].Id, page1.NextAfter);
        // Renaming or disabling rows between pages never moves them: the order is the immutable id.
        await UpdateAsync(client, workspace, page1.Items[0].Id, new { name = "zzz renamed" });
        await UpdateAsync(client, workspace, expected[4].Id, new { enabled = false });
        var page2 = await ListAsync(client, workspace, $"?take=3&after={page1.NextAfter}");
        Assert.Equal(expected.Skip(3).Take(3).Select(s => s.Id), page2.Items.Select(s => s.Id));
        Assert.False(page2.Items.Single(s => s.Id == expected[4].Id).Enabled);
        Assert.Equal(page2.Items[^1].Id, page2.NextAfter);
        var page3 = await ListAsync(client, workspace, $"?after={page2.NextAfter}&take=3");
        Assert.Equal(expected.Skip(6).Select(s => s.Id), page3.Items.Select(s => s.Id));
        Assert.Null(page3.NextAfter);
        var chained = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(s => s.Id).ToArray();
        Assert.Equal(expected.Select(s => s.Id), chained);
        Assert.Equal(chained.Length, chained.Distinct().Count());

        // nextAfter is set only when another row exists.
        Assert.Null((await ListAsync(client, workspace, "?take=7")).NextAfter);
        var six = await ListAsync(client, workspace, "?take=6");
        Assert.Equal(expected[5].Id, six.NextAfter);
        var tail = await ListAsync(client, workspace, $"?take=1&after={six.NextAfter}");
        Assert.Equal(expected[6].Id, Assert.Single(tail.Items).Id);
        Assert.Null(tail.NextAfter);
        var afterLast = await ListAsync(client, workspace, $"?after={expected[^1].Id}&take=100");
        Assert.Empty(afterLast.Items);
        Assert.Null(afterLast.NextAfter);
        // A cursor taken from another workspace is only a position; it never reveals that workspace's rows.
        var afterForeign = await ListAsync(client, workspace, $"?after={foreign.Id}&take=100");
        Assert.Equal(expected.Where(s => s.Id.CompareTo(foreign.Id) > 0).Select(s => s.Id), afterForeign.Items.Select(s => s.Id));
        Assert.Equal(new[] { Summary(foreign) }, (await ListAsync(client, other)).Items);

        foreach (var query in new[]
                 {
                     "take=0", "take=-1", "take=101", "take=abc", "take=1.5", "take=2147483648", "after=not-a-uuid",
                     "after=00000000-0000-0000-0000-000000000000", "after=00000000-0000-0000-0000-000000000000&take=10"
                 })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.GetAsync(Path(workspace) + "?" + query, Cancellation));
        Assert.Single((await ListAsync(client, workspace, "?take=1")).Items);
        Assert.Equal(7, (await ListAsync(client, workspace, "?take=100")).Items.Length);

        // Default page size is 50 and the maximum is 100.
        var bulk = await WorkspaceAsync(owner);
        await using (var db = fixture.CreateContext())
        {
            db.KnowledgeSources.AddRange(Enumerable.Range(0, 101)
                .Select(i => new KnowledgeSource(bulk, KnowledgeSourceKind.FILE, $"Bulk {i}", null, null)));
            await db.SaveChangesAsync(Cancellation);
        }
        await using (var db = fixture.CreateContext())
        {
            var ids = (await db.KnowledgeSources.AsNoTracking().Where(s => s.WorkspaceId == bulk).Select(s => s.Id).ToListAsync(Cancellation))
                .Order().ToArray();
            var first = await ListAsync(client, bulk);
            Assert.Equal(ids.Take(KnowledgeSourceService.DefaultPageSize), first.Items.Select(s => s.Id));
            Assert.Equal(ids[KnowledgeSourceService.DefaultPageSize - 1], first.NextAfter);
            var max = await ListAsync(client, bulk, "?take=100");
            Assert.Equal(ids.Take(KnowledgeSourceService.MaxPageSize), max.Items.Select(s => s.Id));
            Assert.Equal(ids[KnowledgeSourceService.MaxPageSize - 1], max.NextAfter);
            var last = await ListAsync(client, bulk, $"?take=100&after={max.NextAfter}");
            Assert.Equal(ids[^1], Assert.Single(last.Items).Id);
            Assert.Null(last.NextAfter);
        }

        await AuthenticateAsync(client, outsider);
        foreach (var query in new[] { "", "?take=1", $"?after={expected[0].Id}&take=2" })
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(Path(workspace) + query, Cancellation));
    }

    [Fact]
    public async Task Every_collection_response_is_no_store_including_mixed_case_paths_binding_errors_and_unmatched_routes()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        using var anonymous = Client(factory);
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        var mixed = $"/WORKSPACES/{workspace}/Knowledge-Sources";
        var paths = new[] { Path(workspace), mixed, $"/workspaces/{workspace}/KNOWLEDGE-SOURCES" };

        foreach (var path in paths)
        {
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.GetAsync(path, Cancellation));
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.GetAsync($"{path}/{Guid.NewGuid()}", Cancellation));
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.PostAsJsonAsync(path,
                new { kind = "FILE", name = "Anonymous" }, Cancellation));
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.PutAsJsonAsync($"{path}/{Guid.NewGuid()}",
                new { enabled = false }, Cancellation));
            await AssertStatusAsync(HttpStatusCode.Unauthorized, await SendRawAsync(anonymous, HttpMethod.Post, path, "{"));
        }

        await AuthenticateAsync(client, owner);
        var created = await CreateAsync(client, workspace, "TEXT", "Uncacheable", textContent: "Body");
        foreach (var path in paths)
        {
            Assert.Equal(new[] { Summary(created) }, (JsonSerializer.Deserialize<Page>(
                await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(path, Cancellation)), JsonSerializerOptions.Web)!).Items);
            Assert.Equal(created, Detail(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"{path}/{created.Id}", Cancellation))));
            Assert.Equal(created, Detail(await AssertStatusAsync(HttpStatusCode.OK,
                await client.PutAsJsonAsync($"{path}/{created.Id}", new { enabled = true }, Cancellation))));
            // Binding failures never reach the endpoint filter; the early middleware must still mark them no-store.
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Post, path, "{"));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Post, path, """{"kind":1,"name":"x"}"""));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Put, $"{path}/{created.Id}", """{"enabled":"no"}"""));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await SendRawAsync(client, HttpMethod.Put, $"{path}/{created.Id}", """{"kind":"TEXT"}"""));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.GetAsync(path + "?take=abc", Cancellation));
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.GetAsync(path + "?take=0", Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"{path}/{Guid.NewGuid()}", Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"{path}/not-a-guid", Cancellation));
            // There is no DELETE API in this slice.
            await AssertStatusAsync(HttpStatusCode.MethodNotAllowed, await client.DeleteAsync($"{path}/{created.Id}", Cancellation));
        }
        var createdMixed = Detail(await AssertStatusAsync(HttpStatusCode.Created,
            await client.PostAsJsonAsync(mixed, new { kind = "FILE", name = "Mixed case" }, Cancellation)));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync("/workspaces/not-a-guid/knowledge-sources", Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"/workspaces/{Guid.NewGuid()}/knowledge-sources", Cancellation));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(mixed, new { kind = "FILE", name = "No token" }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PutAsJsonAsync($"{mixed}/{created.Id}", new { enabled = false }, Cancellation));

        await AuthenticateAsync(client, outsider);
        foreach (var path in paths)
        {
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(path, Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"{path}/{created.Id}", Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync(path, new { kind = "FILE", name = "Intruder" }, Cancellation));
            await AssertStatusAsync(HttpStatusCode.NotFound, await client.PutAsJsonAsync($"{path}/{created.Id}", new { enabled = false }, Cancellation));
        }

        await AuthenticateAsync(client, owner);
        Assert.Equal(new[] { created, createdMixed }.Select(Summary).OrderBy(s => s.Id), (await ListAsync(client, workspace)).Items);
        Assert.Equal(created, await GetAsync(client, workspace, created.Id));
    }

    private sealed record Csrf(string Token);
    private sealed record Page(KnowledgeSourceSummary[] Items, Guid? NextAfter);
}
