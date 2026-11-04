using System.Text.Json.Serialization;
using MAIC.Application.Knowledge;
using Microsoft.AspNetCore.Antiforgery;

namespace MAIC.API.Knowledge;

/// <summary>Kind is a string on purpose: only the exact names FILE, TEXT and WEBSITE are accepted, never numbers.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KnowledgeSourceCreateRequest(string? Kind, string? Name, string? TextContent, string? WebsiteUrl);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KnowledgeSourceUpdateRequest(string? Name, bool? Enabled, string? TextContent, string? WebsiteUrl);

public static class KnowledgeSourceEndpoints
{
    public static void MapKnowledgeSourceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}/knowledge-sources").RequireAuthorization();
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
        group.MapPost("", async (Guid workspaceId, KnowledgeSourceCreateRequest request, KnowledgeSourceService service,
            CancellationToken ct) =>
        {
            var result = await service.CreateAsync(workspaceId, request.Kind, request.Name, request.TextContent, request.WebsiteUrl, ct);
            return result.Status == KnowledgeSourceStatus.Success
                ? Results.Created($"/workspaces/{workspaceId}/knowledge-sources/{result.Source!.Id}", result.Source) : Map(result);
        }).RequireRateLimiting(KnowledgeDocumentEndpoints.WritePolicy);
        group.MapGet("", async (Guid workspaceId, Guid? after, int? take, KnowledgeSourceService service, CancellationToken ct) =>
            Map(await service.ListAsync(workspaceId, after, take ?? KnowledgeSourceService.DefaultPageSize, ct)));
        group.MapGet("/{sourceId:guid}", async (Guid workspaceId, Guid sourceId, KnowledgeSourceService service, CancellationToken ct) =>
            Map(await service.GetAsync(workspaceId, sourceId, ct)));
        group.MapPut("/{sourceId:guid}", async (Guid workspaceId, Guid sourceId, KnowledgeSourceUpdateRequest request,
            KnowledgeSourceService service, CancellationToken ct) =>
            Map(await service.UpdateAsync(workspaceId, sourceId, request.Name, request.Enabled, request.TextContent,
                request.WebsiteUrl, ct))).RequireRateLimiting(KnowledgeDocumentEndpoints.WritePolicy);
    }

    private static IResult Map(KnowledgeSourceResult result) => result.Status switch
    {
        KnowledgeSourceStatus.Success when result.Items is not null => Results.Ok(new { result.Items, result.NextAfter }),
        KnowledgeSourceStatus.Success => Results.Ok(result.Source),
        KnowledgeSourceStatus.Unauthenticated => Results.Unauthorized(),
        KnowledgeSourceStatus.Invalid => Results.BadRequest(),
        KnowledgeSourceStatus.NotFound => Results.NotFound(),
        _ => throw new InvalidOperationException("Unknown knowledge source result.")
    };
}
