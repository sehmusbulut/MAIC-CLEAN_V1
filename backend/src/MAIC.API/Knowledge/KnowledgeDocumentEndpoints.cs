using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MAIC.API.Identity;
using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using Microsoft.AspNetCore.Antiforgery;

namespace MAIC.API.Knowledge;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IngestionRequest(Guid ClientRequestId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record KnowledgeDocumentActionRequest;

public static class KnowledgeDocumentEndpoints
{
    public const string WritePolicy = "knowledge-writes";

    /// <summary>
    /// Per-user fixed window for knowledge writes, partitioned by the authenticated MAIC user (never by client input).
    /// Rejections return 429 and nothing is read or written.
    /// </summary>
    public static IServiceCollection AddKnowledgeRateLimits(this IServiceCollection services, KnowledgeLimits limits) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
            // Sliding window: no double burst across a window boundary. The limiter is per API instance.
            options.AddPolicy(WritePolicy, context => RateLimitPartition.GetSlidingWindowLimiter(
                context.User.FindFirst(HttpCurrentUser.UserIdClaim)?.Value ?? "anonymous",
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = limits.WritesPerMinutePerUser, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0
                }));
        });

    public static void MapKnowledgeDocumentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}/knowledge-sources/{sourceId:guid}").RequireAuthorization();
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

        // The body is the raw file; its Content-Type header declares the media type. Authentication, CSRF, membership,
        // source kind and metadata are checked before any byte is read, and reading stops one byte past the size limit.
        group.MapPost("/documents", async (Guid workspaceId, Guid sourceId, string? fileName, Guid? clientRequestId,
            HttpRequest request, KnowledgeDocumentService service, CancellationToken ct) =>
        {
            var prepared = await service.PrepareUploadAsync(workspaceId, sourceId, clientRequestId ?? Guid.Empty, fileName,
                request.ContentType, ct, request.ContentLength);
            if (prepared.Status != KnowledgeDocumentStatusCode.Success) return Map(prepared);
            if (request.ContentLength > KnowledgeDocument.MaxUploadBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var content = await ReadBoundedAsync(request.Body, KnowledgeDocument.MaxUploadBytes, ct);
            if (content is null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var result = await service.UploadAsync(workspaceId, sourceId, clientRequestId ?? Guid.Empty, fileName,
                request.ContentType, content, ct);
            return Created(workspaceId, sourceId, result);
        }).RequireRateLimiting(WritePolicy);
        group.MapPost("/ingestions", async (Guid workspaceId, Guid sourceId, IngestionRequest request,
            KnowledgeDocumentService service, CancellationToken ct) =>
            Created(workspaceId, sourceId, await service.RequestIngestionAsync(workspaceId, sourceId, request.ClientRequestId, ct)))
            .RequireRateLimiting(WritePolicy);
        group.MapGet("/documents", async (Guid workspaceId, Guid sourceId, Guid? after, int? take, KnowledgeDocumentService service,
            CancellationToken ct) =>
            Map(await service.ListAsync(workspaceId, sourceId, after, take ?? KnowledgeDocumentService.DefaultPageSize, ct)));
        group.MapGet("/documents/{documentId:guid}", async (Guid workspaceId, Guid sourceId, Guid documentId,
            KnowledgeDocumentService service, CancellationToken ct) =>
            Map(await service.GetAsync(workspaceId, sourceId, documentId, ct)));
        group.MapPost("/documents/{documentId:guid}/retry", async (Guid workspaceId, Guid sourceId, Guid documentId,
            KnowledgeDocumentActionRequest request, KnowledgeDocumentService service, CancellationToken ct) =>
            Map(await service.RetryAsync(workspaceId, sourceId, documentId, ct))).RequireRateLimiting(WritePolicy);
        group.MapDelete("/documents/{documentId:guid}", async (Guid workspaceId, Guid sourceId, Guid documentId,
            KnowledgeDocumentService service, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(workspaceId, sourceId, documentId, ct);
            return result.Status == KnowledgeDocumentStatusCode.Success ? Results.NoContent() : Map(result);
        }).RequireRateLimiting(WritePolicy);
        group.MapGet("/documents/{documentId:guid}/chunks", async (Guid workspaceId, Guid sourceId, Guid documentId,
            int? afterSequence, int? take, KnowledgeDocumentService service, CancellationToken ct) =>
            Map(await service.ListChunksAsync(workspaceId, sourceId, documentId, afterSequence ?? 0,
                take ?? KnowledgeDocumentService.DefaultPageSize, ct)));
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream body, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static IResult Created(Guid workspaceId, Guid sourceId, KnowledgeDocumentResult result) =>
        result.Status == KnowledgeDocumentStatusCode.Success && !result.Replayed
            ? Results.Created($"/workspaces/{workspaceId}/knowledge-sources/{sourceId}/documents/{result.Document!.Id}", result.Document)
            : Map(result);

    private static IResult Map(KnowledgeDocumentResult result) => result.Status switch
    {
        KnowledgeDocumentStatusCode.Success when result.Items is not null => Results.Ok(new { result.Items, result.NextAfter }),
        KnowledgeDocumentStatusCode.Success when result.Chunks is not null => Results.Ok(new { Items = result.Chunks, result.NextAfterSequence }),
        KnowledgeDocumentStatusCode.Success => Results.Ok(result.Document),
        KnowledgeDocumentStatusCode.Unauthenticated => Results.Unauthorized(),
        KnowledgeDocumentStatusCode.Invalid => Results.BadRequest(),
        KnowledgeDocumentStatusCode.NotFound => Results.NotFound(),
        KnowledgeDocumentStatusCode.Conflict => Results.Conflict(),
        KnowledgeDocumentStatusCode.TooLarge => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
        // 409 like other state conflicts; the problem type lets clients tell a full workspace apart.
        KnowledgeDocumentStatusCode.QuotaExceeded => Results.Problem(statusCode: StatusCodes.Status409Conflict,
            type: "https://maic.dev/problems/knowledge-quota-exceeded", title: "Knowledge quota exceeded"),
        _ => throw new InvalidOperationException("Unknown knowledge document result.")
    };
}
