using System.Text.Json.Serialization;
using MAIC.Application.Workspaces;
using Microsoft.AspNetCore.Antiforgery;

namespace MAIC.API.Workspaces;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceRequest(string? Name);

public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsPost(context.HttpContext.Request.Method) || HttpMethods.IsPut(context.HttpContext.Request.Method))
            {
                try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            }
            return await next(context);
        });
        // Locked by the Single-Workspace-Per-User rule: returns 409 Conflict if the user already has a workspace.
        group.MapPost("", async (WorkspaceRequest request, WorkspaceService service, CancellationToken cancellationToken) =>
        {
            var result = await service.CreateAsync(request.Name, cancellationToken);
            return result.Status == WorkspaceStatus.Success
                ? Results.Created($"/workspaces/{result.Workspace!.Id}", result.Workspace) : Map(result);
        });
        group.MapGet("", async (int? skip, int? take, WorkspaceService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ListAsync(skip ?? 0, take ?? 50, cancellationToken);
            return result.Status == WorkspaceStatus.Success ? Results.Ok(result.Items) : Map(new(result.Status));
        });
        group.MapGet("/{id:guid}", async (Guid id, WorkspaceService service, CancellationToken cancellationToken) =>
            Map(await service.GetAsync(id, cancellationToken)));
        group.MapPut("/{id:guid}", async (Guid id, WorkspaceRequest request, WorkspaceService service, CancellationToken cancellationToken) =>
            Map(await service.RenameAsync(id, request.Name, cancellationToken)));
    }

    private static IResult Map(WorkspaceResult result) => result.Status switch
    {
        WorkspaceStatus.Success => Results.Ok(result.Workspace),
        WorkspaceStatus.Unauthenticated => Results.Unauthorized(),
        WorkspaceStatus.Invalid => Results.BadRequest(),
        WorkspaceStatus.NotFound => Results.NotFound(),
        WorkspaceStatus.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        WorkspaceStatus.Conflict => Results.Conflict(new
        {
            code = "SINGLE_WORKSPACE_LIMIT_REACHED",
            error = "Kullanıcı başına yalnızca 1 çalışma alanı oluşturulabilir."
        }),
        _ => throw new InvalidOperationException("Unknown workspace result.")
    };
}
