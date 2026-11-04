using MAIC.Application.Realtime;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MAIC.API.Realtime;

public static class RealtimeSetup
{
    private const string OriginPolicy = "conversation-realtime";
    public const string Path = "/realtime/conversations";

    public static IServiceCollection AddConversationRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        var options = (configuration.GetSection("Realtime").Get<RealtimeOptions>() ?? new RealtimeOptions()).Validate();
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RealtimeSessionGuard>();
        services.AddSingleton<RealtimeRegistry>();
        services.AddSingleton<RealtimeAdmission>();
        services.AddScoped<IConversationRealtimeStore, ConversationRealtimeStore>();
        services.AddScoped<ConversationRealtimeService>();
        services.AddSignalR(hub =>
        {
            hub.EnableDetailedErrors = false;
            hub.SupportedProtocols = ["json"];
            hub.MaximumReceiveMessageSize = 8 * 1024;
            hub.StreamBufferCapacity = 32;
            hub.MaximumParallelInvocationsPerClient = 1;
            hub.HandshakeTimeout = RealtimeOptions.WatchDeadline;
            hub.KeepAliveInterval = TimeSpan.FromSeconds(15);
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
        });
        services.AddCors(cors => cors.AddPolicy(OriginPolicy, policy =>
        {
            if (options.AllowedOrigins.Length > 0) policy.WithOrigins(options.AllowedOrigins);
            policy.WithMethods("GET", "CONNECT").WithHeaders("Content-Type", "X-Requested-With", "X-SignalR-User-Agent")
                .AllowCredentials();
        }));
        return services;
    }

    public static IApplicationBuilder UseConversationRealtimeOrigin(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(Path))
        {
            context.Response.Headers.CacheControl = "no-store";
            var origins = context.Request.Headers.Origin;
            // CORS does not protect direct WebSocket upgrades. Apply this before every hub route,
            // including negotiation, and never accept credentials in a URL.
            if (origins.Count != 1 || !context.RequestServices.GetRequiredService<RealtimeOptions>().AllowsOrigin(origins[0])
                || context.Request.Query.ContainsKey("access_token"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            // Direct WebSockets only. Negotiation allocates transport state before the hub can
            // authorize a Watch; disabling it avoids unclaimed negotiation reservations.
            if (!context.Request.Path.Equals(new PathString(Path))
                || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsConnect(context.Request.Method))
                || context.Request.Query.ContainsKey("id"))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
        }
        await next(context);
    });

    // Must run after authentication but before the SignalR dispatcher creates transport state.
    public static IApplicationBuilder UseConversationRealtimeAdmission(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments(Path))
        {
            await next(context);
            return;
        }
        var session = await context.RequestServices.GetRequiredService<RealtimeSessionGuard>().ReadAsync(context.User);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        using var lease = context.RequestServices.GetRequiredService<RealtimeAdmission>().TryAcquire(session.UserId);
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        // A failed upgrade, handshake timeout, cancellation or disconnect releases admission.
        // Holding the lease through the request also counts active connections against the same cap.
        await next(context);
    });

    public static void MapConversationRealtime(this WebApplication app) =>
        app.MapHub<ConversationRealtimeHub>(Path, options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.ApplicationMaxBufferSize = 64 * 1024;
            options.TransportMaxBufferSize = 64 * 1024;
            options.TransportSendTimeout = TimeSpan.FromSeconds(5);
            options.WebSockets.CloseTimeout = TimeSpan.FromSeconds(5);
            options.CloseOnAuthenticationExpiration = true;
            options.AllowStatefulReconnects = false;
        }).RequireCors(OriginPolicy);
}
