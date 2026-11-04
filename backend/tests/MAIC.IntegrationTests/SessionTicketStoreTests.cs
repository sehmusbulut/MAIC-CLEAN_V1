using System.Security.Claims;
using MAIC.API.Identity;
using Microsoft.AspNetCore.Authentication;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class SessionTicketStoreTests
{
    [Fact]
    public async Task Capacity_exhaustion_does_not_issue_an_unstored_session_key()
    {
        using var store = new SessionTicketStore();
        var ticket = CreateTicket(DateTimeOffset.UtcNow.AddMinutes(5));
        for (var index = 0; index < 10000; index++)
            await store.StoreAsync(ticket);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(ticket));
    }

    [Fact]
    public async Task Ticket_that_expires_after_insertion_cannot_authenticate()
    {
        using var store = new SessionTicketStore();
        var key = await store.StoreAsync(CreateTicket(DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.NotNull(await store.RetrieveAsync(key));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);
        Assert.Null(await store.RetrieveAsync(key));
    }

    private static AuthenticationTicket CreateTicket(DateTimeOffset expires) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(HttpCurrentUser.UserIdClaim, Guid.NewGuid().ToString())], IdentitySetup.SessionScheme)),
        new AuthenticationProperties { ExpiresUtc = expires }, IdentitySetup.SessionScheme);

    [Fact]
    public async Task Removed_expired_and_unknown_tickets_cannot_be_replayed()
    {
        using var store = new SessionTicketStore();
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(HttpCurrentUser.UserIdClaim, Guid.NewGuid().ToString())], IdentitySetup.SessionScheme)),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1) }, IdentitySetup.SessionScheme);
        var key = await store.StoreAsync(ticket);
        Assert.NotNull(await store.RetrieveAsync(key));
        await store.RemoveAsync(key);
        Assert.Null(await store.RetrieveAsync(key));
        Assert.Null(await store.RetrieveAsync("forged"));
        ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(ticket));
    }
}
