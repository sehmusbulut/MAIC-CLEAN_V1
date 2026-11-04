using System.Net;
using System.Net.Http.Json;
using MAIC.Application.Workspaces;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class WorkspaceEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
        builder.ConfigureServices(services => services.AddAuthentication("test-only")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test-only", _ => { }));
    });

    private async Task<Guid> UserAsync()
    {
        await using var db = fixture.CreateContext();
        var user = new User();
        db.Users.Add(user);
        await db.SaveChangesAsync(Cancellation);
        return user.Id;
    }

    private static async Task AuthenticateAsync(HttpClient client, Guid userId)
    {
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        var csrf = await client.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
    }

    private static async Task<WorkspaceView> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/workspaces", new { name }, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var workspace = (await response.Content.ReadFromJsonAsync<WorkspaceView>(Cancellation))!;
        Assert.Equal($"/workspaces/{workspace.Id}", response.Headers.Location!.OriginalString);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return workspace;
    }

    [Fact]
    public async Task Auth_csrf_validation_and_strict_DTOs_prevent_forged_ownership()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var path = "/workspaces/" + Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/workspaces", Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/workspaces", new { name = "Test" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(path, new { name = "Test" }, Cancellation)).StatusCode);
        var anonymousToken = (await client.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation))!.Token;
        var userId = await UserAsync();
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/workspaces", new { name = "Test" }, Cancellation)).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymousToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/workspaces", new { name = "Test" }, Cancellation)).StatusCode);
        await AuthenticateAsync(client, userId);
        foreach (var field in new[] { "userId", "ownerId", "role", "workspaceId" })
        {
            var forged = new Dictionary<string, object> { ["name"] = "Forged", [field] = field == "role" ? "OWNER" : Guid.NewGuid() };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/workspaces", forged, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, forged, Cancellation)).StatusCode);
        }
        foreach (var name in new string?[] { null, "", "  ", "bad\nname", new('x', 201) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/workspaces", new { name }, Cancellation)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<WorkspaceView[]>("/workspaces", Cancellation))!);
        var workspace = await CreateAsync(client, "  Tenant A  ");
        Assert.Equal("Tenant A", workspace.Name);
        Assert.Equal('7', workspace.Id.ToString()[14]);
        await using var db = fixture.CreateContext();
        var owner = await db.WorkspaceMembers.SingleAsync(m => m.WorkspaceId == workspace.Id, Cancellation);
        Assert.Equal(userId, owner.UserId);
        Assert.Equal(WorkspaceRole.OWNER, owner.Role);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/workspaces/{workspace.Id}", new { name = "No CSRF" }, Cancellation)).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymousToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/workspaces/{workspace.Id}", new { name = "Wrong CSRF" }, Cancellation)).StatusCode);
        await AuthenticateAsync(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/workspaces", Cancellation)).StatusCode);
    }

    [Fact]
    public async Task Persisted_membership_controls_list_get_update_and_changes_apply_to_next_request()
    {
        await using var factory = Factory();
        using var owner = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var outsider = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var ownerId = await UserAsync();
        var otherId = await UserAsync();
        await AuthenticateAsync(owner, ownerId);
        await AuthenticateAsync(outsider, otherId);
        var a = await CreateAsync(owner, "Tenant A");
        var a2 = await CreateAsync(owner, "Tenant A2");
        var b = await CreateAsync(outsider, "Tenant B");
        var path = $"/workspaces/{a.Id}";
        var unknown = $"/workspaces/{Guid.NewGuid()}";
        Assert.Equal(new[] { a.Id, a2.Id }.Order(), (await owner.GetFromJsonAsync<WorkspaceView[]>("/workspaces", Cancellation))!.Select(w => w.Id));
        Assert.Equal(b.Id, Assert.Single((await outsider.GetFromJsonAsync<WorkspaceView[]>("/workspaces", Cancellation))!).Id);
        Assert.Single((await owner.GetFromJsonAsync<WorkspaceView[]>("/workspaces?skip=1&take=1", Cancellation))!);
        foreach (var query in new[] { "skip=-1", "take=0", "take=101" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/workspaces?" + query, Cancellation)).StatusCode);
        foreach (var denied in new[] { path, unknown })
        {
            var get = await outsider.GetAsync(denied, Cancellation);
            var put = await outsider.PutAsJsonAsync(denied, new { name = "Stolen" }, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path, new { name = "Owner edit" }, Cancellation)).StatusCode);
        await using var db = fixture.CreateContext();
        db.WorkspaceMembers.Add(new WorkspaceMember(a.Id, otherId, WorkspaceRole.ADMIN));
        await db.SaveChangesAsync(Cancellation);
        Assert.Equal(HttpStatusCode.OK, (await outsider.PutAsJsonAsync(path, new { name = "Admin edit" }, Cancellation)).StatusCode);
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == a.Id && m.UserId == otherId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, WorkspaceRole.AGENT), Cancellation);
        Assert.Equal(HttpStatusCode.OK, (await outsider.GetAsync(path, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PutAsJsonAsync(path, new { name = "Agent edit" }, Cancellation)).StatusCode);
        Assert.Equal("Admin edit", (await owner.GetFromJsonAsync<WorkspaceView>(path, Cancellation))!.Name);
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == a.Id && m.UserId == otherId).ExecuteDeleteAsync(Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(path, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync(path, new { name = "Removed" }, Cancellation)).StatusCode);
        Assert.Equal(b.Id, Assert.Single((await outsider.GetFromJsonAsync<WorkspaceView[]>("/workspaces", Cancellation))!).Id);
    }

    private sealed record Csrf(string Token);
}
