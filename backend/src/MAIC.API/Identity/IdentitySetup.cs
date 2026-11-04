using MAIC.Application.Identity;
using MAIC.Infrastructure.Identity;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Identity;

public static class IdentitySetup
{
    public const string SessionScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static IServiceCollection AddMaicIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Maic");
        services.AddDbContext<MaicDbContext>(options =>
        {
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("ConnectionStrings:Maic is required for identity persistence.");
            options.UseNpgsql(connection);
        });
        services.AddScoped<IIdentityResolver, IdentityResolver>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddMemoryCache();
        services.AddSingleton<SessionTicketStore>();
        var isDevelopment = string.Equals(configuration["ASPNETCORE_ENVIRONMENT"], Environments.Development, StringComparison.OrdinalIgnoreCase);
        services.AddOptions<CookieAuthenticationOptions>(SessionScheme)
            .Configure<SessionTicketStore>((options, store) => options.SessionStore = store);
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = isDevelopment ? "maic-csrf" : "__Host-maic-csrf";
            options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddAuthorization();
        services.AddAuthentication(SessionScheme).AddCookie(SessionScheme, options =>
        {
            options.Cookie.Name = isDevelopment ? "maic-session" : "__Host-maic-session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = async context =>
            {
                if (!Guid.TryParse(context.Principal?.FindFirst(HttpCurrentUser.UserIdClaim)?.Value, out var id)
                    || !await context.HttpContext.RequestServices.GetRequiredService<IIdentityResolver>()
                        .UserExistsAsync(id, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(SessionScheme);
                }
            };
        });

        return services;
    }
}
