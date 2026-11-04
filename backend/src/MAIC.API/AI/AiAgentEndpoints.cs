using System.Text.Json.Serialization;
using MAIC.API.Knowledge;
using MAIC.Application.AI;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace MAIC.API.AI;

/// <summary>
/// Full replacement body for create and update: every property is required (JSON null is allowed only for the nullable
/// ones), so a client can never change configuration by omission. Tier and scope are exact, case-sensitive names.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiAgentRequest
{
    public required string? Name { get; init; }
    public required bool Enabled { get; init; }
    public required string? Instructions { get; init; }
    public required string? Language { get; init; }
    public required string? ModelTier { get; init; }
    public required string? KnowledgeScope { get; init; }
    public required Guid[]? KnowledgeSourceIds { get; init; }
    public required bool HandoffOnRequest { get; init; }
    public required bool HandoffWhenUnanswered { get; init; }
    public required int? MaxAiReplies { get; init; }

    public AiAgentInput ToInput() => new(Name, Enabled, Instructions, Language, ModelTier, KnowledgeScope, KnowledgeSourceIds,
        HandoffOnRequest, HandoffWhenUnanswered, MaxAiReplies);
}

/// <summary>A question to try against an agent's knowledge.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetrievalPreviewRequest
{
    public required string? Query { get; init; }
}

public static class AiAgentEndpoints
{
    /// <summary>Largest valid body (8000-unit instructions, 100 IDs) is far below this; bigger bodies are refused unread.</summary>
    public const long MaxRequestBytes = 64 * 1024;

    public static void MapAiAgentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}/ai-agents").RequireAuthorization();
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
        // Locked by the Single-Agent-Per-Workspace rule: returns 409 Conflict if the workspace already has an AI agent.
        group.MapPost("", async (Guid workspaceId, AiAgentRequest request, AiAgentService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(workspaceId, request.ToInput(), ct);
            return result.Status == AiAgentStatus.Success
                ? Results.Created($"/workspaces/{workspaceId}/ai-agents/{result.Agent!.Id}", result.Agent) : Map(result);
        }).RequireRateLimiting(KnowledgeDocumentEndpoints.WritePolicy).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
        group.MapGet("", async (Guid workspaceId, Guid? after, int? take, AiAgentService service, CancellationToken ct) =>
            Map(await service.ListAsync(workspaceId, after, take ?? AiAgentService.DefaultPageSize, ct)));
        group.MapGet("/{agentId:guid}", async (Guid workspaceId, Guid agentId, AiAgentService service, CancellationToken ct) =>
            Map(await service.GetAsync(workspaceId, agentId, ct)));
        group.MapPut("/{agentId:guid}", async (Guid workspaceId, Guid agentId, AiAgentRequest request, AiAgentService service,
            CancellationToken ct) => Map(await service.UpdateAsync(workspaceId, agentId, request.ToInput(), ct)))
            .RequireRateLimiting(KnowledgeDocumentEndpoints.WritePolicy).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
        // Every member can preview what an agent would retrieve (they can already read the chunks); limited like writes
        // and by the retrieval gate, since each preview embeds a query.
        group.MapPost("/{agentId:guid}/retrieval-preview", async (Guid workspaceId, Guid agentId, RetrievalPreviewRequest request,
            RetrievalPreviewService service, CancellationToken ct) =>
        {
            var (status, result) = await service.PreviewAsync(workspaceId, agentId, request.Query, ct);
            if (status != AiAgentStatus.Success) return Map(new AiAgentResult(status));
            return Results.Ok(new
            {
                Outcome = result!.Outcome.ToString(),
                Reason = result.Reason?.ToString(),
                result.Coverage,
                Passages = result.Passages.Select(p => new { p.ChunkId, p.Text, p.Score, p.Citation })
            });
        }).RequireRateLimiting(KnowledgeDocumentEndpoints.WritePolicy).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
        group.MapDelete("/{agentId:guid}", async (Guid workspaceId, Guid agentId, AiAgentService service, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(workspaceId, agentId, ct);
            return result.Status == AiAgentStatus.Success ? Results.NoContent() : Map(result);
        }).RequireRateLimiting(KnowledgeDocumentEndpoints.WritePolicy);
    }

    private static IResult Map(AiAgentResult result) => result.Status switch
    {
        AiAgentStatus.Success when result.Items is not null => Results.Ok(new { result.Items, result.NextAfter }),
        AiAgentStatus.Success => Results.Ok(result.Agent),
        AiAgentStatus.Unauthenticated => Results.Unauthorized(),
        AiAgentStatus.Invalid => Results.BadRequest(),
        AiAgentStatus.NotFound => Results.NotFound(),
        AiAgentStatus.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        AiAgentStatus.Conflict => Results.Conflict(new
        {
            code = "SINGLE_AGENT_LIMIT_REACHED",
            error = "Çalışma alanı başına yalnızca 1 AI asistanı oluşturulabilir."
        }),
        _ => throw new InvalidOperationException("Unknown AI agent result.")
    };
}
