using MAIC.Application.Identity;

namespace MAIC.API.Identity;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string UserIdClaim = "maic:user_id";
    public const string SuperAdminClaim = "maic:is_super_admin";
    public Guid? UserId => accessor.HttpContext?.User is { Identity.IsAuthenticated: true } principal
        && Guid.TryParse(principal.FindFirst(UserIdClaim)?.Value, out var id) ? id : null;
    public bool IsSuperAdmin => accessor.HttpContext?.User is { Identity.IsAuthenticated: true } principal
        && bool.TryParse(principal.FindFirst(SuperAdminClaim)?.Value, out var isSuperAdmin) && isSuperAdmin;
}
