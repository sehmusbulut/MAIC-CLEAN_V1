using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MAIC.Application.Channels;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class ChannelEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    private async Task<Guid> WorkspaceAsync(Guid owner, params (Guid UserId, WorkspaceRole Role)[] members)
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("Channel HTTP tests");
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

    private static string Path(Guid workspaceId) => $"/workspaces/{workspaceId}/channels";
    private static string PublicPath(string publishableToken) => $"/public/channels/{publishableToken}";

    private static async Task<ChannelView> CreateAsync(HttpClient client, Guid workspaceId, string name)
    {
        using var response = await client.PostAsJsonAsync(Path(workspaceId), new { name }, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var channel = (await response.Content.ReadFromJsonAsync<ChannelView>(Cancellation))!;
        Assert.Equal($"{Path(workspaceId)}/{channel.Id}", response.Headers.Location!.OriginalString);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(workspaceId, channel.WorkspaceId);
        Assert.Equal("WEB", channel.Type);
        Assert.True(channel.Enabled);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", channel.PublishableToken);
        Assert.Equal(32, Convert.FromBase64String(channel.PublishableToken.Replace('-', '+').Replace('_', '/') + "=").Length);
        return channel;
    }

    private static async Task<ChannelView> GetAsync(HttpClient client, Guid workspaceId, Guid channelId)
    {
        using var response = await client.GetAsync($"{Path(workspaceId)}/{channelId}", Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync<ChannelView>(Cancellation))!;
    }

    private static async Task AssertPublicNameAsync(HttpResponseMessage response, string expectedName)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        var property = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("name", property.Name);
        Assert.Equal(expectedName, property.Value.GetString());
    }

    [Fact]
    public async Task All_member_roles_manage_channels_but_cross_tenant_ids_nonmembers_and_revocations_receive_404()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var admin = await UserAsync();
        var agent = await UserAsync();
        var outsider = await UserAsync();
        var workspace = await WorkspaceAsync(owner, (admin, WorkspaceRole.ADMIN), (agent, WorkspaceRole.AGENT));
        var otherWorkspace = await WorkspaceAsync(outsider, (owner, WorkspaceRole.AGENT));
        var created = new List<ChannelView>();

        foreach (var actor in new[] { owner, admin, agent })
        {
            await AuthenticateAsync(client, actor);
            var channel = await CreateAsync(client, workspace, "  Website  ");
            created.Add(channel);
            Assert.Equal("Website", channel.Name);
            Assert.Equal('7', channel.Id.ToString()[14]);
            var before = await GetAsync(client, workspace, channel.Id);
            using var updated = await client.PutAsJsonAsync($"{Path(workspace)}/{channel.Id}",
                new { name = "  Renamed  ", enabled = false }, Cancellation);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var actual = (await updated.Content.ReadFromJsonAsync<ChannelView>(Cancellation))!;
            Assert.Equal("Renamed", actual.Name);
            Assert.False(actual.Enabled);
            Assert.Equal(before.Id, actual.Id);
            Assert.Equal(before.WorkspaceId, actual.WorkspaceId);
            Assert.Equal(before.Type, actual.Type);
            Assert.Equal(before.CreatedAt, actual.CreatedAt);
            Assert.Equal(before.PublishableToken, actual.PublishableToken);
            Assert.Equal(actual, await GetAsync(client, workspace, channel.Id));
            Assert.Contains((await client.GetFromJsonAsync<ChannelView[]>(Path(workspace), Cancellation))!, c => c.Id == channel.Id);
        }

        await AuthenticateAsync(client, owner);
        // Even a member of both workspaces cannot access a channel through the other workspace route.
        var crossTenant = $"{Path(otherWorkspace)}/{created[0].Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(crossTenant, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(crossTenant,
            new { name = "Moved", enabled = true }, Cancellation)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ChannelView[]>(Path(otherWorkspace), Cancellation))!);
        created.Add(await CreateAsync(client, otherWorkspace, "Website"));
        Assert.Equal(created.Count, created.Select(c => c.PublishableToken).Distinct().Count());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path(workspace)}/{Guid.NewGuid()}", Cancellation)).StatusCode);

        await AuthenticateAsync(client, outsider);
        foreach (var deniedWorkspace in new[] { workspace, Guid.NewGuid() })
            await AssertManagementNotFoundAsync(client, deniedWorkspace, created[0].Id);

        await AuthenticateAsync(client, admin);
        await using (var db = fixture.CreateContext())
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace && m.UserId == admin).ExecuteDeleteAsync(Cancellation);
        await AssertManagementNotFoundAsync(client, workspace, created[1].Id);
        await AuthenticateAsync(client, owner);
        var retained = await GetAsync(client, workspace, created[1].Id);
        Assert.Equal("Renamed", retained.Name);
        Assert.False(retained.Enabled);
        Assert.Equal(3, (await client.GetFromJsonAsync<ChannelView[]>(Path(workspace), Cancellation))!.Length);
    }

    private static async Task AssertManagementNotFoundAsync(HttpClient client, Guid workspaceId, Guid channelId)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path(workspaceId), Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path(workspaceId)}/{channelId}", Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Path(workspaceId), new { name = "Intruder" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Path(workspaceId)}/{channelId}",
            new { name = "Intruder", enabled = true }, Cancellation)).StatusCode);
    }

    [Fact]
    public async Task Authentication_user_bound_csrf_and_strict_DTOs_prevent_forged_mutations()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var secondMember = await UserAsync();
        var workspace = await WorkspaceAsync(owner, (secondMember, WorkspaceRole.ADMIN));
        var path = Path(workspace);
        var unknownChannel = path + "/" + Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(unknownChannel, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, new { name = "Anonymous" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(unknownChannel, new { enabled = false }, Cancellation)).StatusCode);
        var anonymousCsrf = (await client.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation))!.Token;
        await AuthenticateAsync(client, owner);
        var channel = await CreateAsync(client, workspace, "Original");
        var channelPath = path + "/" + channel.Id;

        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AssertCsrfRejectedAsync(client, path, channelPath);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymousCsrf);
        await AssertCsrfRejectedAsync(client, path, channelPath);
        await AuthenticateAsync(client, owner);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", secondMember.ToString());
        await AssertCsrfRejectedAsync(client, path, channelPath);
        await AuthenticateAsync(client, owner);

        foreach (var field in new[] { "workspaceId", "id", "channelId", "userId", "type", "publishableToken", "createdAt" })
        {
            var body = new Dictionary<string, object> { ["name"] = "Forged", [field] = Guid.NewGuid().ToString() };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, body, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(channelPath, body, Cancellation)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path,
            new { name = "Client chooses initial state", enabled = false }, Cancellation)).StatusCode);
        Assert.Equal(channel with { CreatedAt = (await GetAsync(client, workspace, channel.Id)).CreatedAt },
            await GetAsync(client, workspace, channel.Id));
        Assert.Single((await client.GetFromJsonAsync<ChannelView[]>(path, Cancellation))!);
        await AuthenticateAsync(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, new { name = "Unknown user" }, Cancellation)).StatusCode);
    }

    private static async Task AssertCsrfRejectedAsync(HttpClient client, string path, string channelPath)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { name = "Forged" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(channelPath,
            new { name = "Forged", enabled = false }, Cancellation)).StatusCode);
    }

    [Fact]
    public async Task Invalid_names_empty_updates_and_query_bounds_return_400_without_mutation()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var channel = await CreateAsync(client, workspace, "Original");
        var path = Path(workspace);
        var channelPath = path + "/" + channel.Id;
        foreach (var name in new[] { "", "  ", new string('n', 201), "bad\nname", "bad\tname", "bad\0name", "bad\u007fname" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { name }, Cancellation)).StatusCode);
            // A simultaneous valid enable change must not commit when the name is invalid.
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(channelPath, new { name, enabled = false }, Cancellation)).StatusCode);
        }
        foreach (var malformed in new[]
        {
            "null", "{}", "{\"name\":null}", "{\"name\":17}",
            "{\"name\":\"\\uD800\"}", "{\"name\":\"\\uDC00\"}", "{\"name\":\"bad\\uD800name\"}"
        })
        {
            using var createBody = new StringContent(malformed, Encoding.UTF8, "application/json");
            using var updateBody = new StringContent(malformed, Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, createBody, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(channelPath, updateBody, Cancellation)).StatusCode);
        }
        foreach (var malformed in new[] { "{\"enabled\":null}", "{\"enabled\":\"false\"}", "{\"enabled\":0}" })
        {
            using var body = new StringContent(malformed, Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(channelPath, body, Cancellation)).StatusCode);
        }
        foreach (var query in new[] { "skip=-1", "skip=100001", "skip=2147483647", "skip=2147483648", "skip=abc", "take=0", "take=101", "take=abc" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?" + query, Cancellation)).StatusCode);

        var actual = await GetAsync(client, workspace, channel.Id);
        Assert.Equal("Original", actual.Name);
        Assert.True(actual.Enabled);
        Assert.Equal(channel.PublishableToken, actual.PublishableToken);
        Assert.Single((await client.GetFromJsonAsync<ChannelView[]>(path, Cancellation))!);
    }

    [Fact]
    public async Task Listing_uses_stable_paging_and_accepts_boundary_length_and_well_formed_unicode_names()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var first = await CreateAsync(client, workspace, new string('n', 200));
        var second = await CreateAsync(client, workspace, "Support \ud83d\ude42");
        var third = await CreateAsync(client, workspace, "Third");
        var expected = new[] { first.Id, second.Id, third.Id }.Order().ToArray();
        using var response = await client.GetAsync(Path(workspace), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<ChannelView[]>(Cancellation))!.Select(c => c.Id));
        Assert.Equal(new string('n', 200), (await GetAsync(client, workspace, first.Id)).Name);
        Assert.Equal("Support \ud83d\ude42", (await GetAsync(client, workspace, second.Id)).Name);
        Assert.Equal(expected.Take(2), (await client.GetFromJsonAsync<ChannelView[]>(Path(workspace) + "?skip=0&take=2", Cancellation))!.Select(c => c.Id));
        Assert.Equal(expected[2], Assert.Single((await client.GetFromJsonAsync<ChannelView[]>(Path(workspace) + "?skip=2&take=1", Cancellation))!).Id);
        Assert.Empty((await client.GetFromJsonAsync<ChannelView[]>(Path(workspace) + "?skip=3&take=1", Cancellation))!);
        Assert.Empty((await client.GetFromJsonAsync<ChannelView[]>(Path(workspace) + "?skip=100000&take=100", Cancellation))!);
    }

    [Fact]
    public async Task Anonymous_public_lookup_is_minimal_and_disable_reenable_preserves_the_same_identity()
    {
        await using var factory = Factory();
        using var management = Client(factory);
        using var anonymous = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(management, owner);
        var created = await CreateAsync(management, workspace, "Website");
        var original = await GetAsync(management, workspace, created.Id);
        var path = $"{Path(workspace)}/{original.Id}";
        using (var lookup = await anonymous.GetAsync(PublicPath(original.PublishableToken), Cancellation))
            await AssertPublicNameAsync(lookup, "Website");
        using (var forgedWorkspace = await anonymous.GetAsync(PublicPath(original.PublishableToken)
            + $"?workspaceId={Guid.NewGuid()}&channelId={Guid.NewGuid()}", Cancellation))
            await AssertPublicNameAsync(forgedWorkspace, "Website");

        using (var disabled = await management.PutAsJsonAsync(path, new { enabled = false }, Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            Assert.Equal(original with { Enabled = false }, await disabled.Content.ReadFromJsonAsync<ChannelView>(Cancellation));
        }
        using var hidden = await anonymous.GetAsync(PublicPath(original.PublishableToken), Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.True(hidden.Headers.CacheControl!.NoStore);
        using var hiddenBody = JsonDocument.Parse(await hidden.Content.ReadAsStringAsync(Cancellation));
        foreach (var unknown in new[] { new string('A', 43), new string('A', 42), new string('A', 44), new string('A', 42) + "!", original.Id.ToString(), workspace.ToString() })
        {
            using var response = await anonymous.GetAsync(PublicPath(unknown), Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            using var unknownBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
            // Diagnostic trace IDs differ per request; the public error contract must not.
            Assert.Equal(
                hiddenBody.RootElement.EnumerateObject().Where(p => p.Name != "traceId").OrderBy(p => p.Name).Select(p => (p.Name, p.Value.GetRawText())),
                unknownBody.RootElement.EnumerateObject().Where(p => p.Name != "traceId").OrderBy(p => p.Name).Select(p => (p.Name, p.Value.GetRawText())));
        }

        using (var renamed = await management.PutAsJsonAsync(path, new { name = "Renamed while disabled" }, Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.Equal(original with { Enabled = false, Name = "Renamed while disabled" },
                await renamed.Content.ReadFromJsonAsync<ChannelView>(Cancellation));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(PublicPath(original.PublishableToken), Cancellation)).StatusCode);
        using (var enabled = await management.PutAsJsonAsync(path, new { enabled = true }, Cancellation))
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal(original with { Name = "Renamed while disabled" }, await GetAsync(management, workspace, original.Id));
        using (var lookup = await anonymous.GetAsync(PublicPath(original.PublishableToken), Cancellation))
            await AssertPublicNameAsync(lookup, "Renamed while disabled");
    }

    [Fact]
    public async Task Publishable_tokens_cannot_authenticate_users_or_authorize_management()
    {
        await using var factory = Factory();
        using var management = Client(factory);
        using var tokenOnly = Client(factory);
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(management, owner);
        var channel = await CreateAsync(management, workspace, "Website");
        tokenOnly.DefaultRequestHeaders.Add("Authorization", "Bearer " + channel.PublishableToken);
        tokenOnly.DefaultRequestHeaders.Add("X-Publishable-Token", channel.PublishableToken);
        tokenOnly.DefaultRequestHeaders.Add("X-Test-User", channel.PublishableToken);
        var query = "?publishableToken=" + channel.PublishableToken;
        Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.GetAsync("/auth/me" + query, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.GetAsync(Path(workspace) + query, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.GetAsync($"{Path(workspace)}/{channel.Id}" + query, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.PostAsJsonAsync(Path(workspace) + query, new { name = "Forged" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.PutAsJsonAsync($"{Path(workspace)}/{channel.Id}" + query, new { enabled = false }, Cancellation)).StatusCode);
        await AuthenticateAsync(tokenOnly, outsider);
        await AssertManagementNotFoundAsync(tokenOnly, workspace, channel.Id);
        Assert.True((await GetAsync(management, workspace, channel.Id)).Enabled);
        Assert.Single((await management.GetFromJsonAsync<ChannelView[]>(Path(workspace), Cancellation))!);
    }

    [Fact]
    public async Task Public_CORS_allows_anonymous_GET_including_errors_without_enabling_management_or_credentials()
    {
        await using var factory = Factory();
        using var management = Client(factory);
        using var anonymous = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(management, owner);
        var channel = await CreateAsync(management, workspace, "Website");
        anonymous.DefaultRequestHeaders.Add("Origin", "https://customer.example");
        using (var lookup = await anonymous.GetAsync(PublicPath(channel.PublishableToken), Cancellation))
        {
            await AssertPublicNameAsync(lookup, "Website");
            AssertPublicCors(lookup);
        }
        foreach (var token in new[] { new string('A', 43), "malformed" })
        {
            using var missing = await anonymous.GetAsync(PublicPath(token), Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            AssertPublicCors(missing);
            Assert.True(missing.Headers.CacheControl!.NoStore);
            Assert.Equal("no-referrer", Assert.Single(missing.Headers.GetValues("Referrer-Policy")));
        }
        using (var disabled = await management.PutAsJsonAsync($"{Path(workspace)}/{channel.Id}", new { enabled = false }, Cancellation))
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        anonymous.DefaultRequestHeaders.Remove("Origin");
        anonymous.DefaultRequestHeaders.Add("Origin", "https://another-customer.example");
        using (var disabled = await anonymous.GetAsync(PublicPath(channel.PublishableToken), Cancellation))
        {
            Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
            AssertPublicCors(disabled);
        }

        using (var preflight = new HttpRequestMessage(HttpMethod.Options, PublicPath(channel.PublishableToken)))
        {
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            using var response = await anonymous.SendAsync(preflight, Cancellation);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            AssertPublicCors(response);
            Assert.Equal("GET", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")));
        }

        management.DefaultRequestHeaders.Add("Origin", "https://customer.example");
        using (var listing = await management.GetAsync(Path(workspace), Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
            AssertNoCors(listing);
        }
        using (var updated = await management.PutAsJsonAsync($"{Path(workspace)}/{channel.Id}", new { enabled = true }, Cancellation))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            AssertNoCors(updated);
        }
        using (var unauthenticated = await anonymous.GetAsync(Path(workspace), Cancellation))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            AssertNoCors(unauthenticated);
        }
        using (var preflight = new HttpRequestMessage(HttpMethod.Options, Path(workspace)))
        {
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            using var response = await anonymous.SendAsync(preflight, Cancellation);
            AssertNoCors(response);
        }
    }

    private static void AssertPublicCors(HttpResponseMessage response)
    {
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static void AssertNoCors(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    private sealed record Csrf(string Token);
}
