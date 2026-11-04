using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MAIC.API.Identity;
using MAIC.Application.Identity;
using MAIC.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class IdentityEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Anonymous_requests_cannot_authenticate_by_headers_or_access_current_user()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/login", null, TestContext.Current.CancellationToken)).StatusCode);
        var csrf = await client.GetFromJsonAsync<Csrf>("/auth/csrf", TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/auth/login", null, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/login?returnUrl=https%3A%2F%2Fevil.example", null, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Test_only_authentication_requires_persisted_user_and_logout_requires_user_bound_csrf()
    {
        await using var db = fixture.CreateContext();
        var id = await new IdentityResolver(db).ResolveAsync("https://test-only/" + Guid.NewGuid(), "subject", TestContext.Current.CancellationToken);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
            builder.ConfigureServices(services => services.AddAuthentication("test-only")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test-only", _ => { }));
        });
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var anonymousCsrf = await client.GetFromJsonAsync<Csrf>("/auth/csrf", TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Add("X-Test-User", id.ToString());
        var me = await client.GetFromJsonAsync<CurrentUser>("/auth/me", TestContext.Current.CancellationToken);
        Assert.Equal(id, me!.UserId);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken)).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymousCsrf!.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken)).StatusCode);
        var csrf = await client.GetFromJsonAsync<Csrf>("/auth/csrf", TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/auth/logout", null, TestContext.Current.CancellationToken)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    private sealed record Csrf(string Token);
    private sealed record CurrentUser(Guid UserId);
}

// This handler exists exclusively in the test assembly; production never reads these headers.
internal sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IIdentityResolver resolver)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Headers["X-Test-User"], out var id)
            || !await resolver.UserExistsAsync(id, Context.RequestAborted)) return AuthenticateResult.NoResult();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(HttpCurrentUser.UserIdClaim, id.ToString()), new Claim(ClaimTypes.NameIdentifier, id.ToString())], Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
