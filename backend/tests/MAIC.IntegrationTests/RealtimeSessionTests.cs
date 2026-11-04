using System.Security.Claims;
using MAIC.API.Identity;
using MAIC.API.Realtime;
using Microsoft.AspNetCore.Authentication;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class RealtimeSessionTests
{
    [Fact]
    public async Task Store_and_renew_replace_forged_session_claims_across_identities_without_mutating_caller()
    {
        using var store = new SessionTicketStore();
        var actor = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new[]
        {
            new ClaimsIdentity([
                new Claim(HttpCurrentUser.UserIdClaim, actor.ToString()),
                new Claim(SessionTicketStore.SessionKeyClaim, "forged-primary"),
                new Claim(SessionTicketStore.SessionKeyClaim, "forged-primary-duplicate")], IdentitySetup.SessionScheme),
            new ClaimsIdentity([new Claim(SessionTicketStore.SessionKeyClaim, "forged-secondary")], "secondary"),
            new ClaimsIdentity([new Claim(SessionTicketStore.SessionKeyClaim, "forged-anonymous")])
        });
        var ticket = Ticket(principal);
        var originalClaims = principal.Claims.Select(claim => (claim.Type, claim.Value)).ToArray();

        var key = await store.StoreAsync(ticket);
        Assert.Equal(64, key.Length);
        await AssertCanonicalTicketAsync();
        await store.RenewAsync(key, ticket);
        await AssertCanonicalTicketAsync();

        async Task AssertCanonicalTicketAsync()
        {
            var stored = Assert.IsType<AuthenticationTicket>(await store.RetrieveAsync(key));
            Assert.NotSame(principal, stored.Principal);
            Assert.Equal(key, Assert.Single(stored.Principal.FindAll(SessionTicketStore.SessionKeyClaim)).Value);
            Assert.Equal(actor.ToString(), Assert.Single(stored.Principal.FindAll(HttpCurrentUser.UserIdClaim)).Value);
            Assert.Equal(originalClaims, principal.Claims.Select(claim => (claim.Type, claim.Value)).ToArray());
            Assert.DoesNotContain(principal.Claims, claim => claim.Value == key);
            var session = Assert.IsType<RealtimeSession>(await new RealtimeSessionGuard(store, TimeProvider.System).ReadAsync(stored.Principal));
            Assert.Equal(actor, session.UserId);
            Assert.Equal(key, session.Key);
        }
    }

    [Fact]
    public async Task Revalidation_rejects_removed_unknown_and_exactly_expired_sessions()
    {
        // AuthenticationProperties serializes expiry at whole-second precision.
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var time = new ManualRealtimeTimeProvider(start);
        using var store = new SessionTicketStore();
        var guard = new RealtimeSessionGuard(store, time);
        var actor = Guid.NewGuid();
        var key = await store.StoreAsync(Ticket(Principal(actor), start.AddMinutes(1)));
        var principal = (await store.RetrieveAsync(key))!.Principal;
        Assert.NotNull(await guard.ReadAsync(principal));
        Assert.NotNull(await guard.ReadAsync(key, actor));
        Assert.Null(await guard.ReadAsync(new string('0', 64), actor));

        time.Advance(TimeSpan.FromMilliseconds(59999));
        Assert.NotNull(await guard.ReadAsync(key, actor));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.NotNull(await store.RetrieveAsync(key));
        Assert.Null(await guard.ReadAsync(principal));
        Assert.Null(await guard.ReadAsync(key, actor));

        var liveKey = await store.StoreAsync(Ticket(Principal(actor), start.AddHours(1)));
        var livePrincipal = (await store.RetrieveAsync(liveKey))!.Principal;
        Assert.NotNull(await guard.ReadAsync(livePrincipal));
        await store.RemoveAsync(liveKey);
        Assert.Null(await guard.ReadAsync(livePrincipal));
        Assert.Null(await guard.ReadAsync(liveKey, actor));
    }

    [Fact]
    public async Task Revalidation_rejects_actor_mismatch_and_old_actor_after_ticket_rebinding()
    {
        using var store = new SessionTicketStore();
        var guard = new RealtimeSessionGuard(store, TimeProvider.System);
        var originalActor = Guid.NewGuid();
        var replacementActor = Guid.NewGuid();
        var key = await store.StoreAsync(Ticket(Principal(originalActor)));
        var originalPrincipal = (await store.RetrieveAsync(key))!.Principal;
        Assert.Null(await guard.ReadAsync(key, replacementActor));
        Assert.Null(await guard.ReadAsync(Principal(replacementActor, key)));

        await store.RenewAsync(key, Ticket(Principal(replacementActor)));
        Assert.Null(await guard.ReadAsync(originalPrincipal));
        Assert.Null(await guard.ReadAsync(key, originalActor));
        Assert.Equal(replacementActor, (await guard.ReadAsync(key, replacementActor))!.UserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public async Task Missing_or_malformed_session_key_is_denied(string? key)
    {
        using var store = new SessionTicketStore();
        var guard = new RealtimeSessionGuard(store, TimeProvider.System);
        Assert.Null(await guard.ReadAsync(Principal(Guid.NewGuid(), key)));
    }

    [Fact]
    public async Task Unauthenticated_and_duplicate_session_claims_are_denied_even_when_the_key_is_valid()
    {
        using var store = new SessionTicketStore();
        var guard = new RealtimeSessionGuard(store, TimeProvider.System);
        var actor = Guid.NewGuid();
        var key = await store.StoreAsync(Ticket(Principal(actor)));
        Assert.Null(await guard.ReadAsync((ClaimsPrincipal?)null));
        Assert.Null(await guard.ReadAsync(new ClaimsPrincipal()));
        Assert.Null(await guard.ReadAsync(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(HttpCurrentUser.UserIdClaim, actor.ToString()), new Claim(SessionTicketStore.SessionKeyClaim, key)]))));

        var duplicate = Principal(actor, key);
        ((ClaimsIdentity)duplicate.Identity!).AddClaim(new Claim(SessionTicketStore.SessionKeyClaim, key));
        Assert.Null(await guard.ReadAsync(duplicate));
        var secondaryDuplicate = Principal(actor, key);
        secondaryDuplicate.AddIdentity(new ClaimsIdentity([new Claim(SessionTicketStore.SessionKeyClaim, key)]));
        Assert.Null(await guard.ReadAsync(secondaryDuplicate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Missing_or_invalid_actor_claim_is_denied(string? actor)
    {
        using var store = new SessionTicketStore();
        var guard = new RealtimeSessionGuard(store, TimeProvider.System);
        var key = await store.StoreAsync(Ticket(Principal(Guid.NewGuid())));
        var identity = new ClaimsIdentity([new Claim(SessionTicketStore.SessionKeyClaim, key)], IdentitySetup.SessionScheme);
        if (actor is not null) identity.AddClaim(new Claim(HttpCurrentUser.UserIdClaim, actor));
        Assert.Null(await guard.ReadAsync(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public async Task Duplicate_actor_claims_in_request_or_retrieved_ticket_are_denied()
    {
        using var store = new SessionTicketStore();
        var guard = new RealtimeSessionGuard(store, TimeProvider.System);
        var actor = Guid.NewGuid();
        var key = await store.StoreAsync(Ticket(Principal(actor)));
        var duplicateActor = Principal(actor, key);
        duplicateActor.AddIdentity(new ClaimsIdentity([new Claim(HttpCurrentUser.UserIdClaim, actor.ToString())]));
        Assert.Null(await guard.ReadAsync(duplicateActor));

        await store.RenewAsync(key, Ticket(duplicateActor));
        Assert.Null(await guard.ReadAsync(key, actor));
        Assert.Null(await guard.ReadAsync(Principal(actor, key)));
    }

    [Fact]
    public async Task Ticket_under_another_authentication_scheme_is_denied()
    {
        using var store = new SessionTicketStore();
        var actor = Guid.NewGuid();
        var ticket = new AuthenticationTicket(Principal(actor),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, "another-scheme");
        var key = await store.StoreAsync(ticket);
        Assert.Null(await new RealtimeSessionGuard(store, TimeProvider.System).ReadAsync(key, actor));
    }

    private static ClaimsPrincipal Principal(Guid actor, string? key = null)
    {
        var identity = new ClaimsIdentity([new Claim(HttpCurrentUser.UserIdClaim, actor.ToString())], IdentitySetup.SessionScheme);
        if (key is not null) identity.AddClaim(new Claim(SessionTicketStore.SessionKeyClaim, key));
        return new ClaimsPrincipal(identity);
    }

    private static AuthenticationTicket Ticket(ClaimsPrincipal principal, DateTimeOffset? expires = null) =>
        new(principal, new AuthenticationProperties { ExpiresUtc = expires ?? DateTimeOffset.UtcNow.AddHours(1) }, IdentitySetup.SessionScheme);
}

public sealed class RealtimeOptionsTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(256, 4, 64)]
    public void Connection_limit_boundaries_are_valid(int global, int user, int workspace)
    {
        var options = new RealtimeOptions { GlobalConnectionLimit = global, UserConnectionLimit = user, WorkspaceConnectionLimit = workspace };
        Assert.Same(options, options.Validate());
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(257, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 5, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 65)]
    public void Connection_limits_cannot_disable_or_raise_hard_caps(int global, int user, int workspace)
    {
        var options = new RealtimeOptions { GlobalConnectionLimit = global, UserConnectionLimit = user, WorkspaceConnectionLimit = workspace };
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("*")]
    [InlineData("https://admin.example/")]
    [InlineData("https://admin.example/path")]
    [InlineData("https://admin.example?query=1")]
    [InlineData("https://admin.example#fragment")]
    [InlineData("https://user:password@admin.example")]
    [InlineData("https://ADMIN.example")]
    [InlineData("HTTPS://admin.example")]
    [InlineData("https://admin.example:443")]
    [InlineData("ftp://admin.example")]
    [InlineData(" https://admin.example")]
    [InlineData("https://admin.example ")]
    public void Noncanonical_origins_are_rejected_in_configuration_and_requests(string origin)
    {
        var options = new RealtimeOptions { AllowedOrigins = [origin] };
        Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.False(options.AllowsOrigin(origin));
    }

    [Fact]
    public void Origin_matching_is_exact_and_empty_configuration_denies_all_origins()
    {
        var closed = new RealtimeOptions().Validate();
        Assert.False(closed.AllowsOrigin(null));
        Assert.False(closed.AllowsOrigin("https://admin.example"));
        var options = new RealtimeOptions { AllowedOrigins = ["https://admin.example", "http://localhost:5173"] }.Validate();
        Assert.True(options.AllowsOrigin("https://admin.example"));
        Assert.True(options.AllowsOrigin("http://localhost:5173"));
        Assert.False(options.AllowsOrigin(null));
        Assert.False(options.AllowsOrigin("https://admin.example.evil.test"));
        Assert.False(options.AllowsOrigin("https://sub.admin.example"));
        Assert.False(options.AllowsOrigin("http://admin.example"));
        Assert.False(options.AllowsOrigin("https://admin.example:8443"));
        Assert.False(options.AllowsOrigin("http://localhost:5174"));
    }
}
