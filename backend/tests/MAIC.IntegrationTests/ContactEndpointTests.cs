using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MAIC.Application.Contacts;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class ContactEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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
        var workspace = new Workspace("Contact HTTP tests");
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

    private static object Details(string displayName, params (string Kind, string Value)[] identities) => new
    {
        displayName, identities = identities.Select(i => new { kind = i.Kind, value = i.Value }).ToArray()
    };

    private static string Path(Guid workspaceId) => $"/workspaces/{workspaceId}/contacts";

    private static async Task<ContactView> CreateAsync(HttpClient client, Guid workspaceId, object details)
    {
        using var response = await client.PostAsJsonAsync(Path(workspaceId), details, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var contact = (await response.Content.ReadFromJsonAsync<ContactView>(Cancellation))!;
        Assert.Equal($"{Path(workspaceId)}/{contact.Id}", response.Headers.Location!.OriginalString);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(workspaceId, contact.WorkspaceId);
        return contact;
    }

    private static async Task<ContactView> GetAsync(HttpClient client, Guid workspaceId, Guid contactId)
    {
        using var response = await client.GetAsync($"{Path(workspaceId)}/{contactId}", Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync<ContactView>(Cancellation))!;
    }

    [Fact]
    public async Task All_member_roles_manage_contacts_but_other_tenants_and_revoked_members_receive_404()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var admin = await UserAsync();
        var agent = await UserAsync();
        var outsider = await UserAsync();
        var workspace = await WorkspaceAsync(owner, (admin, WorkspaceRole.ADMIN), (agent, WorkspaceRole.AGENT));
        var otherWorkspace = await WorkspaceAsync(outsider, (owner, WorkspaceRole.AGENT));
        var created = new List<ContactView>();

        foreach (var actor in new[] { owner, admin, agent })
        {
            await AuthenticateAsync(client, actor);
            var contact = await CreateAsync(client, workspace,
                Details("  Customer  ", (" EMAIL ", actor.ToString()), ("legacy", actor.ToString())));
            created.Add(contact);
            Assert.Equal("Customer", contact.DisplayName);
            Assert.Equal('7', contact.Id.ToString()[14]);
            var retained = Assert.Single(contact.Identities, i => i.Kind == "email");
            var before = await GetAsync(client, workspace, contact.Id);
            using var updated = await client.PutAsJsonAsync($"{Path(workspace)}/{contact.Id}",
                Details("Renamed", ("Email", actor.ToString()), ("external", " opaque " + actor + " ")), Cancellation);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var actual = await GetAsync(client, workspace, contact.Id);
            Assert.Equal("Renamed", actual.DisplayName);
            Assert.Equal(before.CreatedAt, actual.CreatedAt);
            Assert.Equal(retained, Assert.Single(actual.Identities, i => i.Kind == "email"));
            Assert.Equal(" opaque " + actor + " ", Assert.Single(actual.Identities, i => i.Kind == "external").Value);
            Assert.Equal(2, actual.Identities.Count);
            Assert.Contains((await client.GetFromJsonAsync<ContactView[]>(Path(workspace), Cancellation))!, c => c.Id == contact.Id);
        }

        await AuthenticateAsync(client, owner);
        // This actor belongs to both tenants: the contact ID must still be scoped to the route workspace.
        var crossTenant = $"{Path(otherWorkspace)}/{created[0].Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(crossTenant, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(crossTenant, Details("Moved"), Cancellation)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ContactView[]>(Path(otherWorkspace), Cancellation))!);
        await CreateAsync(client, otherWorkspace, Details("Same identity in another tenant", ("email", owner.ToString())));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path(workspace)}/{Guid.NewGuid()}", Cancellation)).StatusCode);

        await AuthenticateAsync(client, outsider);
        foreach (var denied in new[] { workspace, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path(denied), Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path(denied)}/{created[0].Id}", Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Path(denied), Details("Intruder"), Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Path(denied)}/{created[0].Id}", Details("Intruder"), Cancellation)).StatusCode);
        }

        await AuthenticateAsync(client, admin);
        await using (var db = fixture.CreateContext())
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace && m.UserId == admin).ExecuteDeleteAsync(Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path(workspace), Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path(workspace)}/{created[1].Id}", Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Path(workspace), Details("Removed"), Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Path(workspace)}/{created[1].Id}", Details("Removed"), Cancellation)).StatusCode);
        await AuthenticateAsync(client, owner);
        Assert.Equal("Renamed", (await GetAsync(client, workspace, created[1].Id)).DisplayName);
    }

    [Fact]
    public async Task Authentication_user_bound_csrf_and_strict_nested_DTOs_prevent_forged_mutations()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        var path = Path(workspace);
        var unknownContact = path + "/" + Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(unknownContact, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, Details("Anonymous"), Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(unknownContact, Details("Anonymous"), Cancellation)).StatusCode);
        var anonymousCsrf = (await client.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation))!.Token;
        await AuthenticateAsync(client, owner);
        var contact = await CreateAsync(client, workspace, Details("Original", ("external", "original")));
        var contactPath = path + "/" + contact.Id;

        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, Details("No CSRF"), Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(contactPath, Details("No CSRF"), Cancellation)).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymousCsrf);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, Details("Wrong CSRF"), Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(contactPath, Details("Wrong CSRF"), Cancellation)).StatusCode);
        await AuthenticateAsync(client, owner);

        foreach (var field in new[] { "workspaceId", "contactId", "userId" })
        {
            var identity = new Dictionary<string, object> { ["kind"] = "external", ["value"] = "forged" };
            var body = new Dictionary<string, object> { ["displayName"] = "Forged", ["identities"] = new[] { identity }, [field] = Guid.NewGuid() };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, body, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(contactPath, body, Cancellation)).StatusCode);
            body.Remove(field);
            identity[field] = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, body, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(contactPath, body, Cancellation)).StatusCode);
        }

        Assert.Equal("Original", (await GetAsync(client, workspace, contact.Id)).DisplayName);
        Assert.Single((await client.GetFromJsonAsync<ContactView[]>(path, Cancellation))!);
        await AuthenticateAsync(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, Details("Unknown user"), Cancellation)).StatusCode);
    }

    [Fact]
    public async Task Invalid_payloads_and_query_bounds_return_400_without_changing_contacts()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var contact = await CreateAsync(client, workspace, Details("Original"));
        object[] invalidBodies =
        [
            new { identities = Array.Empty<object>() },
            Details("  "), Details(new string('n', 201)), Details("bad\nname"),
            new { displayName = "Missing identities" },
            new { displayName = "Null identities", identities = (object?)null },
            new { displayName = "Null item", identities = new object?[] { null } },
            Details("Duplicate", ("Email", "same"), (" email ", "same")),
            Details("Invalid kind", ("bad kind", "value")),
            Details("Empty value", ("external", "")),
            Details("Long value", ("external", new string('v', 513))),
            Details("NUL value", ("external", "bad\0value")),
            Details("Too many", Enumerable.Range(0, 21).Select(i => ("external", i.ToString())).ToArray())
        ];
        foreach (var body in invalidBodies)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path(workspace), body, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Path(workspace)}/{contact.Id}", body, Cancellation)).StatusCode);
        }
        foreach (var malformed in new[] { "null", "{\"displayName\":17,\"identities\":[]}", "{\"displayName\":\"\\uD800\",\"identities\":[]}" })
        {
            using var body = new StringContent(malformed, Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Path(workspace), body, Cancellation)).StatusCode);
        }
        foreach (var query in new[] { "skip=-1", "skip=100001", "take=0", "take=101", "skip=abc", "query=" + new string('q', 513), "query=bad%00query" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path(workspace) + "?" + query, Cancellation)).StatusCode);

        var actual = await GetAsync(client, workspace, contact.Id);
        Assert.Equal("Original", actual.DisplayName);
        Assert.Empty(actual.Identities);
        Assert.Single((await client.GetFromJsonAsync<ContactView[]>(Path(workspace), Cancellation))!);
    }

    [Fact]
    public async Task Search_matches_literal_case_sensitive_names_and_values_with_stable_paging()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var first = await CreateAsync(client, workspace, Details("Case %_\\ marker", ("external", "first-Marker %_\\ value")));
        var second = await CreateAsync(client, workspace, Details("Other", ("external", "second-Marker %_\\ value")));
        var decoy = await CreateAsync(client, workspace, Details("Case XXY marker", ("external", "third-Marker XXY value")));
        var literalQuery = Path(workspace) + "?query=" + Uri.EscapeDataString("%_\\");
        using var response = await client.GetAsync(literalQuery, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var matches = (await response.Content.ReadFromJsonAsync<ContactView[]>(Cancellation))!;
        var expected = new[] { first.Id, second.Id }.Order().ToArray();
        Assert.Equal(expected, matches.Select(c => c.Id));
        Assert.Equal(expected[1], Assert.Single((await client.GetFromJsonAsync<ContactView[]>(literalQuery + "&skip=1&take=1", Cancellation))!).Id);
        Assert.Empty((await client.GetFromJsonAsync<ContactView[]>(literalQuery + "&skip=2&take=1", Cancellation))!);
        Assert.Empty((await client.GetFromJsonAsync<ContactView[]>(Path(workspace) + "?query=case", Cancellation))!);
        Assert.Equal(new[] { first.Id, decoy.Id }.Order(),
            (await client.GetFromJsonAsync<ContactView[]>(Path(workspace) + "?query=Case", Cancellation))!.Select(c => c.Id));
        Assert.Equal(new[] { first.Id, second.Id, decoy.Id }.Order(),
            (await client.GetFromJsonAsync<ContactView[]>(Path(workspace) + "?query=Marker", Cancellation))!.Select(c => c.Id));
        Assert.Equal(3, (await client.GetFromJsonAsync<ContactView[]>(Path(workspace), Cancellation))!.Length);
        Assert.Empty((await client.GetFromJsonAsync<ContactView[]>(Path(workspace) + "?skip=100000&take=100", Cancellation))!);
    }

    [Fact]
    public async Task Identity_conflicts_return_409_and_roll_back_replacement_name_and_identity_changes()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        var original = await CreateAsync(client, workspace, Details("Original", ("email", "retained"), ("external", "removed-if-committed")));
        original = await GetAsync(client, workspace, original.Id);
        await CreateAsync(client, workspace, Details("Other", ("email", "already-taken")));
        using var conflict = await client.PutAsJsonAsync($"{Path(workspace)}/{original.Id}",
            Details("Must roll back", ("email", "retained"), ("external", "new-if-committed"), ("email", "already-taken")), Cancellation);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var conflictBody = await conflict.Content.ReadAsStringAsync(Cancellation);
        using var problem = JsonDocument.Parse(conflictBody);
        Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain("already-taken", conflictBody);
        Assert.DoesNotContain(original.Id.ToString(), conflictBody);
        var actual = await GetAsync(client, workspace, original.Id);
        Assert.Equal(original.DisplayName, actual.DisplayName);
        Assert.Equal(original.CreatedAt, actual.CreatedAt);
        Assert.Equal(original.Identities.OrderBy(i => i.Id), actual.Identities.OrderBy(i => i.Id));
        await CreateAsync(client, workspace, Details("Proves failed addition rolled back", ("external", "new-if-committed")));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path(workspace),
            Details("Proves failed removal rolled back", ("external", "removed-if-committed")), Cancellation)).StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<ContactView[]>(Path(workspace), Cancellation))!.Length);
    }

    [Fact]
    public async Task Contact_identity_records_neither_create_users_nor_authenticate_as_MAIC_users()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var owner = await UserAsync();
        var workspace = await WorkspaceAsync(owner);
        await AuthenticateAsync(client, owner);
        await using var db = fixture.CreateContext();
        var usersBefore = await db.Users.CountAsync(Cancellation);
        var externalIdentitiesBefore = await db.ExternalIdentities.CountAsync(Cancellation);
        var purportedUser = Guid.CreateVersion7();
        var contact = await CreateAsync(client, workspace, Details("Customer identity", ("maic-user", purportedUser.ToString())));
        Assert.Equal(usersBefore, await db.Users.CountAsync(Cancellation));
        Assert.Equal(externalIdentitiesBefore, await db.ExternalIdentities.CountAsync(Cancellation));
        foreach (var candidate in new[] { purportedUser, contact.Id, Assert.Single(contact.Identities).Id })
        {
            await AuthenticateAsync(client, candidate);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me", Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path(workspace), Cancellation)).StatusCode);
        }
        await AuthenticateAsync(client, owner);
        Assert.Equal(contact.Id, (await GetAsync(client, workspace, contact.Id)).Id);
    }

    private sealed record Csrf(string Token);
}
