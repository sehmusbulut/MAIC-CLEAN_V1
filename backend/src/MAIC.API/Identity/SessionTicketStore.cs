using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Security.Claims;

namespace MAIC.API.Identity;

// Single-instance baseline: restart revokes all sessions. Never store provider tokens.
public sealed class SessionTicketStore : ITicketStore, IDisposable
{
    public const string SessionKeyClaim = "maic:session-key";
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 10000 });

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        if (!cache.TryGetValue("session:" + key, out _))
            throw new InvalidOperationException("Session capacity reached. Retry authentication later.");
        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        // Never mutate the caller's principal or trust a provider-supplied session key.
        var stored = TicketSerializer.Default.Deserialize(TicketSerializer.Default.Serialize(ticket))!;
        foreach (var identity in stored.Principal.Identities)
            foreach (var claim in identity.FindAll(SessionKeyClaim).ToArray())
                identity.RemoveClaim(claim);
        var authenticated = stored.Principal.Identities.FirstOrDefault(identity => identity.IsAuthenticated)
            ?? throw new InvalidOperationException("A session requires an authenticated principal.");
        authenticated.AddClaim(new Claim(SessionKeyClaim, key));
        stored.Properties.ExpiresUtc ??= DateTimeOffset.UtcNow.AddHours(8);
        cache.Set("session:" + key, TicketSerializer.Default.Serialize(stored), new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = stored.Properties.ExpiresUtc,
            Size = 1
        });
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => Task.FromResult(
        cache.TryGetValue<byte[]>("session:" + key, out var bytes) && bytes is not null
            ? TicketSerializer.Default.Deserialize(bytes) : null);

    public Task RemoveAsync(string key)
    {
        cache.Remove("session:" + key);
        return Task.CompletedTask;
    }

    public void Dispose() => cache.Dispose();
}
