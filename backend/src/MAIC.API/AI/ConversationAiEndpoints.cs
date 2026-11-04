using System.Text.Json;
using System.Text.Json.Serialization;
using MAIC.Application.AI;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace MAIC.API.AI;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConversationAiBindingRequest { public required Guid? AgentId { get; init; } }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConversationAiExecutionRequest { public required Guid MessageId { get; init; } }

public static class ConversationAiEndpoints
{
    public const int MaxRequestBytes = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapConversationAiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}/conversations/{conversationId:guid}").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            return await next(context);
        });
        group.MapPut("/ai-agent", async (Guid workspaceId, Guid conversationId, HttpRequest request, [FromServices] ConversationAiBindingService service, CancellationToken ct) =>
        {
            var (body, error) = await ReadBodyAsync<ConversationAiBindingRequest>(request, ct);
            if (error is not null) return error;
            var result = await service.BindAsync(workspaceId, conversationId, body!.AgentId, ct);
            return result.Status switch
            {
                AiBindingStatus.Success => Results.Ok(new { result.AgentId }),
                AiBindingStatus.Unauthenticated => Results.Unauthorized(),
                AiBindingStatus.Forbidden => Results.Forbid(),
                AiBindingStatus.NotFound => Results.NotFound(),
                _ => Results.BadRequest()
            };
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
        group.MapPost("/ai-executions", async (Guid workspaceId, Guid conversationId, HttpRequest request, ConversationAiExecutionService service, CancellationToken ct) =>
        {
            var (body, error) = await ReadBodyAsync<ConversationAiExecutionRequest>(request, ct);
            if (error is not null) return error;
            var result = await service.ExecuteAsync(workspaceId, conversationId, body!.MessageId, ct);
            var status = result.Status switch
            {
                AiExecutionStatus.Success => 200, AiExecutionStatus.Unauthenticated => 401,
                AiExecutionStatus.Invalid => 400, AiExecutionStatus.NotFound => 404, AiExecutionStatus.Forbidden => 403,
                AiExecutionStatus.Conflict => 409, _ => 503
            };
            return Results.Json(new { result.ExecutionId, Outcome = result.Outcome?.ToString(), result.Message, result.Handoff,
                Failure = result.Failure?.ToString(), result.Replayed }, statusCode: status);
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
    }

    private static async Task<(T? Body, IResult? Error)> ReadBodyAsync<T>(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType()) return (default, Results.StatusCode(415));
        if (request.ContentLength > MaxRequestBytes) return (default, Results.StatusCode(413));
        var bytes = new byte[MaxRequestBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await request.Body.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        if (count > MaxRequestBytes) return (default, Results.StatusCode(413));
        try
        {
            var body = JsonSerializer.Deserialize<T>(bytes.AsSpan(0, count), Json);
            return body is null ? (default, Results.BadRequest()) : (body, null);
        }
        catch (JsonException) { return (default, Results.BadRequest()); }
    }
}
