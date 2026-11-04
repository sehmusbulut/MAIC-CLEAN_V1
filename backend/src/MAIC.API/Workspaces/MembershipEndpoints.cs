using System.Text.Json.Serialization;
using MAIC.Application.Workspaces;
using Microsoft.AspNetCore.Antiforgery;

namespace MAIC.API.Workspaces;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemberRoleRequest(string? Role);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvitationRequest(Guid RecipientUserId, string? Role);

public static class MembershipEndpoints
{
    public static void MapMembershipEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}").RequireAuthorization();
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
        group.MapGet("/members", async (Guid workspaceId, int? skip, int? take, MembershipService service, CancellationToken ct) =>
            Map(await service.ListMembersAsync(workspaceId, skip ?? 0, take ?? 50, ct)));
        group.MapPut("/members/{userId:guid}/role", async (Guid workspaceId, Guid userId, MemberRoleRequest request, MembershipService service, CancellationToken ct) =>
            Map(await service.ChangeRoleAsync(workspaceId, userId, request.Role, ct)));
        group.MapDelete("/members/{userId:guid}", async (Guid workspaceId, Guid userId, MembershipService service, CancellationToken ct) =>
            Map(await service.RemoveAsync(workspaceId, userId, ct)));
        group.MapPost("/leave", async (Guid workspaceId, MembershipService service, CancellationToken ct) =>
            Map(await service.LeaveAsync(workspaceId, ct)));
        group.MapGet("/invitations", async (Guid workspaceId, int? skip, int? take, MembershipService service, CancellationToken ct) =>
            Map(await service.ListInvitationsAsync(workspaceId, skip ?? 0, take ?? 50, ct)));
        group.MapPost("/invitations", async (Guid workspaceId, InvitationRequest request, MembershipService service, CancellationToken ct) =>
        {
            var result = await service.CreateInvitationAsync(workspaceId, request.RecipientUserId, request.Role, ct);
            return result.Status == MembershipStatus.Success
                ? Results.Created($"/workspaces/{workspaceId}/invitations/{result.Invitation!.Id}", result.Invitation) : Map(result);
        });
        group.MapDelete("/invitations/{invitationId:guid}", async (Guid workspaceId, Guid invitationId, MembershipService service, CancellationToken ct) =>
            Map(await service.RevokeInvitationAsync(workspaceId, invitationId, ct)));
        group.MapPost("/invitations/{invitationId:guid}/accept", async (Guid workspaceId, Guid invitationId, MembershipService service, CancellationToken ct) =>
            Map(await service.AcceptInvitationAsync(workspaceId, invitationId, ct)));
    }
    private static IResult Map(MembershipResult result) => result.Status switch
    {
        MembershipStatus.Success when result.Members is not null => Results.Ok(result.Members),
        MembershipStatus.Success when result.Invitations is not null => Results.Ok(result.Invitations),
        MembershipStatus.Success => Results.NoContent(),
        MembershipStatus.Unauthenticated => Results.Unauthorized(),
        MembershipStatus.Invalid => Results.BadRequest(),
        MembershipStatus.NotFound => Results.NotFound(),
        MembershipStatus.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        MembershipStatus.Conflict => Results.Conflict(),
        _ => throw new InvalidOperationException("Unknown membership result.")
    };
}
