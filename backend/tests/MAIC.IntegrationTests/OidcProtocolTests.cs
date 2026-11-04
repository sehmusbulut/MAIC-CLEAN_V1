using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MAIC.API.Identity;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class OidcProtocolTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Verified_callback_provisions_internal_identity_and_issues_session_without_provider_claims_or_tokens()
    {
        using var provider = new OidcTestProvider();
        using var factory = CreateFactory(provider);
        using var client = CreateClient(factory);
        var cookies = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentitySetup.SessionScheme);
        var challenge = await BeginLogin(client, provider);
        using var callback = await CompleteLogin(client, challenge["state"].ToString());
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/after-login", callback.Headers.Location?.OriginalString);
        var issuedCookie = Assert.Single(callback.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-maic-session=")).Split(';')[0];
        var cookieTicket = cookies.TicketDataFormat.Unprotect(Uri.UnescapeDataString(issuedCookie[(issuedCookie.IndexOf('=') + 1)..]));
        Assert.NotNull(cookieTicket);
        var sessionKey = Assert.Single(cookieTicket.Principal.Claims).Value;
        var stored = await cookies.SessionStore!.RetrieveAsync(sessionKey, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.True(stored.Principal.Identity!.IsAuthenticated);
        Assert.Empty(stored.Properties.GetTokens());
        var me = await client.GetFromJsonAsync<JsonElement>("/auth/me", TestContext.Current.CancellationToken);
        var userId = me.GetProperty("userId").GetGuid();
        Assert.NotEqual(Guid.Empty, userId);
        var claims = stored.Principal.Claims.ToArray();
        Assert.Equal(3, claims.Length);
        Assert.Equal(sessionKey, Assert.Single(claims, claim => claim.Type == SessionTicketStore.SessionKeyClaim).Value);
        Assert.All(claims.Where(claim => claim.Type != SessionTicketStore.SessionKeyClaim),
            claim => Assert.Equal(userId.ToString(), claim.Value));
        Assert.Contains(claims, claim => claim.Type == ClaimTypes.NameIdentifier);
        Assert.Contains(claims, claim => claim.Type == "maic:user_id");
        Assert.DoesNotContain("opaque-provider-access-token", string.Join(" ", callback.Headers.GetValues("Set-Cookie")));
        var sessionCookie = Assert.Single(callback.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-maic-session="));
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);

        var tokenRequest = QueryHelpers.ParseQuery(Assert.IsType<string>(provider.TokenRequest));
        Assert.Equal("authorization_code", tokenRequest["grant_type"].ToString());
        var verifier = tokenRequest["code_verifier"].ToString();
        Assert.NotEmpty(verifier);
        Assert.Equal(challenge["code_challenge"].ToString(), WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaicDbContext>();
        var external = await db.ExternalIdentities.SingleAsync(x => x.Subject == provider.Subject, TestContext.Current.CancellationToken);
        Assert.Equal(OidcTestProvider.Issuer, external.Issuer);
        Assert.Equal(userId, external.UserId);
        Assert.True(await db.Users.AnyAsync(x => x.Id == userId, TestContext.Current.CancellationToken));

        // A consumed callback cannot mint another session, even in the original browser.
        using var replay = await CompleteLogin(client, challenge["state"].ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.DoesNotContain(replay.Headers.TryGetValues("Set-Cookie", out var replayCookies) ? replayCookies : [],
            x => x.StartsWith("__Host-maic-session="));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("correlation")]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    public async Task Invalid_callback_never_provisions_identity_or_establishes_session(string fault)
    {
        using var provider = new OidcTestProvider { Fault = fault };
        using var factory = CreateFactory(provider);
        using var browser = CreateClient(factory);
        var challenge = await BeginLogin(browser, provider);
        using var otherBrowser = CreateClient(factory);
        var callbackClient = fault == "correlation" ? otherBrowser : browser;
        var state = fault == "state" ? "tampered-state" : challenge["state"].ToString();
        using var callback = await CompleteLogin(callbackClient, state);
        Assert.Equal(HttpStatusCode.Unauthorized, callback.StatusCode);
        using var me = await callbackClient.GetAsync("/auth/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaicDbContext>();
        Assert.False(await db.ExternalIdentities.AnyAsync(x => x.Subject == provider.Subject, TestContext.Current.CancellationToken));
        if (fault is "state" or "correlation") Assert.Null(provider.TokenRequest);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("deleted-user")]
    public async Task Previously_valid_cookie_cannot_authenticate_after_revocation(string reason)
    {
        using var provider = new OidcTestProvider();
        using var factory = CreateFactory(provider);
        using var browser = CreateClient(factory);
        var challenge = await BeginLogin(browser, provider);
        using var callback = await CompleteLogin(browser, challenge["state"].ToString());
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var capturedCookie = Assert.Single(callback.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-maic-session=")).Split(';')[0];
        var me = await browser.GetFromJsonAsync<JsonElement>("/auth/me", TestContext.Current.CancellationToken);
        var userId = me.GetProperty("userId").GetGuid();
        if (reason == "logout")
        {
            var csrf = await browser.GetFromJsonAsync<JsonElement>("/auth/csrf", TestContext.Current.CancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
            using var response = await browser.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode);
        }
        else
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MaicDbContext>();
            Assert.Equal(1, await db.Users.Where(x => x.Id == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        }
        using var replayBrowser = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        replayBrowser.DefaultRequestHeaders.Add("Cookie", capturedCookie);
        using var replay = await replayBrowser.GetAsync("/auth/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }
    [Theory]
    [InlineData("blank-subject")]
    [InlineData("removed-subject")]
    public async Task Incomplete_verified_identity_cannot_sign_in_as_provider_supplied_existing_user(string fault)
    {
        await using var db = postgres.CreateContext();
        var existingUser = new MAIC.Domain.Identity.User();
        db.Users.Add(existingUser);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var provider = new OidcTestProvider { Fault = fault, SpoofedUserId = existingUser.Id };
        using var factory = CreateFactory(provider);
        using var client = CreateClient(factory);
        var challenge = await BeginLogin(client, provider);
        using var callback = await CompleteLogin(client, challenge["state"].ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, callback.StatusCode);
        Assert.DoesNotContain(callback.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            x => x.StartsWith("__Host-maic-session="));
        using var me = await client.GetAsync("/auth/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.False(await db.ExternalIdentities.AnyAsync(x => x.Subject == provider.Subject || x.Subject == " ", TestContext.Current.CancellationToken));
    }
    private WebApplicationFactory<Program> CreateFactory(OidcTestProvider provider) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var setting in new Dictionary<string, string?>
            {
                ["ConnectionStrings:Maic"] = postgres.ConnectionString,
                ["Authentication:Oidc:Authority"] = OidcTestProvider.Issuer,
                ["Authentication:Oidc:ClientId"] = OidcTestProvider.ClientId,
                ["Authentication:Oidc:ClientSecret"] = "local-test-secret"
            }) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureServices(services => services.PostConfigure<OpenIdConnectOptions>("oidc", options =>
            {
                options.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(provider.Configuration);
                options.Backchannel = new HttpClient(provider, disposeHandler: false);
                // Remove sub after token validation to exercise MAIC's incomplete-ticket branch.
                if (provider.Fault == "removed-subject") options.ClaimActions.DeleteClaim("sub");
            }));
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<Dictionary<string, Microsoft.Extensions.Primitives.StringValues>> BeginLogin(HttpClient client, OidcTestProvider provider)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/auth/csrf", TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login?returnUrl=/after-login");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal(OidcTestProvider.Issuer + "/authorize", location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        provider.Nonce = query["nonce"].ToString();
        Assert.NotEmpty(provider.Nonce);
        return query;
    }

    private static Task<HttpResponseMessage> CompleteLogin(HttpClient client, string state) =>
        client.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["state"] = state,
            ["code"] = "local-provider-authorization-code"
        }), TestContext.Current.CancellationToken);
}










