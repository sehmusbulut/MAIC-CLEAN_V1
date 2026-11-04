using System.Globalization;
using MAIC.Application.Widget;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MAIC.API.Widget;

public static class WidgetSetup
{
    public static IServiceCollection AddWidget(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton((configuration.GetSection("Widget").Get<WidgetLimits>() ?? new WidgetLimits()).Validate());
        services.AddSingleton((configuration.GetSection("WidgetHttp").Get<WidgetHttpOptions>() ?? new WidgetHttpOptions()).Validate());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WidgetRateLimiter>();
        services.AddScoped<IWidgetSessionStore, WidgetSessionStore>();
        services.AddScoped<WidgetService>();
        services.AddCors(cors => cors.AddPolicy(WidgetEndpoints.CorsPolicy, policy => policy.AllowAnyOrigin()
            .WithMethods("GET", "POST", "DELETE").WithHeaders("Authorization", "Content-Type").WithExposedHeaders("Retry-After")));
        return services;
    }

    // Runs after CORS (including error responses), before authentication/binding/persistence.
    public static IApplicationBuilder UseWidgetBoundaries(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/public/channels")) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var limiter = context.RequestServices.GetRequiredService<WidgetRateLimiter>();
        using var lease = limiter.TryEnter();
        if (lease is null) { RateLimited(context, limiter); return; }
        var parts = context.Request.Path.Value!.Split('/');
        var guest = parts.Length >= 5 && parts[4].Equals("guest-session", StringComparison.OrdinalIgnoreCase);
        if (!guest) { await next(context); return; }
        var messages = parts.Length >= 6 && parts[5].Equals("messages", StringComparison.OrdinalIgnoreCase);
        if (context.Request.Query.Keys.Any(key => !messages || !HttpMethods.IsGet(context.Request.Method)
                || (key != "afterSequence" && key != "take")))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var hash = WidgetService.HashCredential(WidgetEndpoints.Credential(context.Request));
        if (hash is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        if (!HttpMethods.IsPost(context.Request.Method)) { await next(context); return; }
        if (!(messages ? limiter.TryMessage(hash) : limiter.TryStart(context.Connection.RemoteIpAddress)))
        {
            RateLimited(context, limiter);
            return;
        }

        var limit = messages ? 32 * 1024 : 1024;
        var bodyFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyFeature is { IsReadOnly: false }) bodyFeature.MaxRequestBodySize = limit;
        if (context.Request.ContentLength > limit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        // Also bound unknown-length/chunked bodies in TestServer and hosts without a size feature.
        var originalBody = context.Request.Body;
        var originalLength = context.Request.ContentLength;
        using var boundedBody = new MemoryStream();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await originalBody.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)boundedBody.Length)), deadline.Token);
                if (count == 0) break;
                boundedBody.Write(buffer, 0, count);
                if (boundedBody.Length > limit)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
            }
            boundedBody.Position = 0;
            context.Request.Body = boundedBody;
            context.Request.ContentLength = boundedBody.Length;
            await next(context);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
        }
        catch (BadHttpRequestException error) when (!context.Response.HasStarted && error.StatusCode is 400 or 413)
        {
            context.Response.StatusCode = error.StatusCode;
        }
        finally
        {
            context.Request.Body = originalBody;
            context.Request.ContentLength = originalLength;
        }
    });

    private static void RateLimited(HttpContext context, WidgetRateLimiter limiter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = limiter.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
    }
}
