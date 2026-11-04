using System.Security.Claims;
using MAIC.API.Identity;

namespace MAIC.API.Realtime;

public sealed record RealtimeSession(Guid UserId, string Key, ClaimsPrincipal Principal);

public sealed class RealtimeSessionGuard(SessionTicketStore sessions, TimeProvider time)
{
    public async Task<RealtimeSession?> ReadAsync(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var keys = principal.FindAll(SessionTicketStore.SessionKeyClaim).ToArray();
        var actors = principal.FindAll(HttpCurrentUser.UserIdClaim).ToArray();
        if (keys.Length != 1 || actors.Length != 1 || keys[0].Value.Length != 64
            || !Guid.TryParse(actors[0].Value, out var actor) || actor == Guid.Empty) return null;
        return await ReadAsync(keys[0].Value, actor);
    }

    public async Task<RealtimeSession?> ReadAsync(string key, Guid expectedActor)
    {
        var ticket = await sessions.RetrieveAsync(key);
        if (ticket?.AuthenticationScheme != IdentitySetup.SessionScheme
            || ticket.Principal.Identity?.IsAuthenticated != true
            || ticket.Properties.ExpiresUtc is not { } expires || expires <= time.GetUtcNow()) return null;
        var actors = ticket.Principal.FindAll(HttpCurrentUser.UserIdClaim).ToArray();
        var keys = ticket.Principal.FindAll(SessionTicketStore.SessionKeyClaim).ToArray();
        return actors.Length == 1 && Guid.TryParse(actors[0].Value, out var actor) && actor == expectedActor
            && keys.Length == 1 && keys[0].Value == key
            ? new RealtimeSession(actor, key, ticket.Principal) : null;
    }
}
