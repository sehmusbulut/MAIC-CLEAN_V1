using System.Text.Json.Serialization;
using MAIC.Application.Channels;
using Microsoft.AspNetCore.Antiforgery;

namespace MAIC.API.Channels;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChannelCreateRequest(string? Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChannelUpdateRequest(string? Name, bool? Enabled);

public static class ChannelEndpoints
{
    public const string PublicBootstrapCorsPolicy = "PublicChannelBootstrap";

    public static void MapChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}/channels").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsGet(context.HttpContext.Request.Method) && !HttpMethods.IsHead(context.HttpContext.Request.Method))
            {
                try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            }
            return await next(context);
        });
        group.MapGet("", async (Guid workspaceId, int? skip, int? take, ChannelService service, CancellationToken ct) =>
            Map(await service.ListAsync(workspaceId, skip ?? 0, take ?? 50, ct)));
        group.MapGet("/{channelId:guid}", async (Guid workspaceId, Guid channelId, ChannelService service, CancellationToken ct) =>
            Map(await service.GetAsync(workspaceId, channelId, ct)));
        group.MapPost("", async (Guid workspaceId, ChannelCreateRequest request, ChannelService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(workspaceId, request.Name, ct);
            return result.Status == ChannelStatus.Success
                ? Results.Created($"/workspaces/{workspaceId}/channels/{result.Channel!.Id}", result.Channel) : Map(result);
        });
        group.MapPut("/{channelId:guid}", async (Guid workspaceId, Guid channelId, ChannelUpdateRequest request,
            ChannelService service, CancellationToken ct) => Map(await service.UpdateAsync(workspaceId, channelId, request.Name, request.Enabled, ct)));

        var publicGroup = app.MapGroup("/public/channels");
        publicGroup.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
            return await next(context);
        });
        publicGroup.MapGet("/{publishableToken}", async (string publishableToken, ChannelService service, CancellationToken ct) =>
        {
            var result = await service.LookupPublicAsync(publishableToken, ct);
            return result.Status == ChannelStatus.Success ? Results.Ok(result.Public) : Results.NotFound();
        }).RequireCors(PublicBootstrapCorsPolicy).AllowAnonymous();
    }

    private static IResult Map(ChannelResult result) => result.Status switch
    {
        ChannelStatus.Success when result.Items is not null => Results.Ok(result.Items),
        ChannelStatus.Success => Results.Ok(result.Channel),
        ChannelStatus.Unauthenticated => Results.Unauthorized(),
        ChannelStatus.Invalid => Results.BadRequest(),
        ChannelStatus.NotFound => Results.NotFound(),
        ChannelStatus.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        ChannelStatus.Conflict => Results.Conflict(),
        _ => throw new InvalidOperationException("Unknown channel result.")
    };
}
