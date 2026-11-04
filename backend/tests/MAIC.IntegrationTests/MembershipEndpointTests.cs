using System.Net;
using System.Net.Http.Json;
using MAIC.Application.Workspaces;
using MAIC.Domain.Workspaces;
using MAIC.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class MembershipEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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


    [Fact]
    public async Task Role_matrix_tenant_isolation_strict_bodies_and_csrf_are_enforced()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var owner = await UserAsync();
        var admin = await UserAsync();
        var agent = await UserAsync();
        var recipient = await UserAsync();
        var outsider = await UserAsync();
        Guid workspaceId;
        await using (var db = fixture.CreateContext())
        {
            var workspace = new Workspace("Roles"); workspaceId = workspace.Id;
            db.Workspaces.Add(workspace);
            db.WorkspaceMembers.AddRange(new WorkspaceMember(workspaceId, owner, WorkspaceRole.OWNER),
                new WorkspaceMember(workspaceId, admin, WorkspaceRole.ADMIN), new WorkspaceMember(workspaceId, agent, WorkspaceRole.AGENT));
            await db.SaveChangesAsync(Cancellation);
        }
        var path = $"/workspaces/{workspaceId}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path + "/members", Cancellation)).StatusCode);
        await AuthenticateAsync(client, outsider);
        foreach (var root in new[] { path, $"/workspaces/{Guid.NewGuid()}" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "/members", Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "/invitations", Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(root + $"/members/{agent}", Cancellation)).StatusCode);
        }
        await AuthenticateAsync(client, agent);
        Assert.Equal(3, (await client.GetFromJsonAsync<MemberView[]>(path + "/members", Cancellation))!.Length);
        var orderedIds = new[] { owner, admin, agent }.Order().ToArray();
        var second = await client.GetFromJsonAsync<MemberView[]>(path + "/members?skip=1&take=1", Cancellation);
        Assert.Equal(orderedIds[1], Assert.Single(second!).UserId);
        Assert.Empty((await client.GetFromJsonAsync<MemberView[]>(path + "/members?skip=3&take=1", Cancellation))!);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "/invitations", Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/invitations", new { recipientUserId = recipient, role = "AGENT" }, Cancellation)).StatusCode);
        await AuthenticateAsync(client, admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path + $"/members/{agent}/role", new { role = "ADMIN" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(path + $"/members/{owner}", Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/invitations", new { recipientUserId = recipient, role = "OWNER" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(path + "/invitations", new { recipientUserId = recipient, role = "AGENT" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path + $"/members/{agent}", Cancellation)).StatusCode);
        await AuthenticateAsync(client, owner);
        foreach (var role in new string?[] { null, "owner", "0", "UNKNOWN" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path + $"/members/{admin}/role", new { role }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path + $"/members/{admin}/role", new { role = "OWNER", actorId = owner }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/invitations", new { recipientUserId = agent, role = "AGENT", workspaceId }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "/members?take=101", Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path + "/leave", null, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path + $"/members/{owner}/role", new { role = "AGENT" }, Cancellation)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync(path + $"/members/{admin}", Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path + "/leave", null, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path + $"/members/{admin}/role", new { role = "OWNER" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/invitations", new { recipientUserId = agent, role = "AGENT" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync(path + $"/invitations/{Guid.NewGuid()}", Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path + $"/invitations/{Guid.NewGuid()}/accept", null, Cancellation)).StatusCode);
        await AuthenticateAsync(client, owner);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path + $"/members/{admin}/role", new { role = "OWNER" }, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(path + "/leave", null, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/members", Cancellation)).StatusCode);
    }

    [Fact]
    public async Task Invitations_require_recipient_scope_are_single_use_and_never_elevate_existing_member()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var owner = await UserAsync(); var recipient = await UserAsync(); var other = await UserAsync();
        await AuthenticateAsync(client, owner);
        var created = await client.PostAsJsonAsync("/workspaces", new { name = "Invites" }, Cancellation);
        var workspace = (await created.Content.ReadFromJsonAsync<WorkspaceView>(Cancellation))!;
        var path = $"/workspaces/{workspace.Id}";
        async Task<InvitationView> Invite(Guid userId, string role)
        {
            var response = await client.PostAsJsonAsync(path + "/invitations", new { recipientUserId = userId, role }, Cancellation);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<InvitationView>(Cancellation))!;
        }
        var invitation = await Invite(recipient, "ADMIN");
        Assert.Equal(TimeSpan.FromDays(7), invitation.ExpiresAt - invitation.CreatedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/invitations", new { recipientUserId = recipient, role = "OWNER" }, Cancellation)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<InvitationView[]>(path + "/invitations", Cancellation))!);
        await AuthenticateAsync(client, other);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(path + $"/invitations/{invitation.Id}/accept", null, Cancellation)).StatusCode);
        await AuthenticateAsync(client, recipient);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/workspaces/{Guid.NewGuid()}/invitations/{invitation.Id}/accept", null, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(path + $"/invitations/{invitation.Id}/accept", null, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path + $"/invitations/{invitation.Id}/accept", null, Cancellation)).StatusCode);
        await AuthenticateAsync(client, owner);
        var revoked = await Invite(other, "OWNER");
        await AuthenticateAsync(client, recipient);
        Assert.Empty((await client.GetFromJsonAsync<InvitationView[]>(path + "/invitations", Cancellation))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(path + $"/invitations/{revoked.Id}", Cancellation)).StatusCode);
        await AuthenticateAsync(client, owner);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path + $"/invitations/{revoked.Id}", Cancellation)).StatusCode);
        await AuthenticateAsync(client, other);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path + $"/invitations/{revoked.Id}/accept", null, Cancellation)).StatusCode);
        await AuthenticateAsync(client, owner);
        var pending = await Invite(other, "OWNER");
        await using (var db = fixture.CreateContext())
        {
            db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, other, WorkspaceRole.AGENT));
            await db.SaveChangesAsync(Cancellation);
        }
        await AuthenticateAsync(client, other);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path + $"/invitations/{pending.Id}/accept", null, Cancellation)).StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(WorkspaceRole.AGENT, (await verify.WorkspaceMembers.SingleAsync(m => m.WorkspaceId == workspace.Id && m.UserId == other, Cancellation)).Role);
        Assert.Null((await verify.WorkspaceInvitations.SingleAsync(i => i.Id == pending.Id, Cancellation)).AcceptedAt);
    }
    private sealed record Csrf(string Token);
}
