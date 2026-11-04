using System.Text.Json.Serialization;
using MAIC.Application.Contacts;
using MAIC.Domain.Contacts;
using Microsoft.AspNetCore.Antiforgery;

namespace MAIC.API.Contacts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContactIdentityRequest(string? Kind, string? Value);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContactRequest(string? DisplayName, IReadOnlyList<ContactIdentityRequest?>? Identities)
{
    public IReadOnlyCollection<ContactIdentityValue>? IdentityValues() => Identities is null || Identities.Any(i => i is null)
        ? null : Identities.Select(i => new ContactIdentityValue(i!.Kind, i.Value)).ToArray();
}

public static class ContactEndpoints
{
    public static void MapContactEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}/contacts").RequireAuthorization();
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
        group.MapGet("", async (Guid workspaceId, string? query, int? skip, int? take, ContactService service, CancellationToken ct) =>
            Map(await service.SearchAsync(workspaceId, query, skip ?? 0, take ?? 50, ct)));
        group.MapGet("/{contactId:guid}", async (Guid workspaceId, Guid contactId, ContactService service, CancellationToken ct) =>
            Map(await service.GetAsync(workspaceId, contactId, ct)));
        group.MapPost("", async (Guid workspaceId, ContactRequest request, ContactService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(workspaceId, request.DisplayName, request.IdentityValues(), ct);
            return result.Status == ContactStatus.Success
                ? Results.Created($"/workspaces/{workspaceId}/contacts/{result.Contact!.Id}", result.Contact) : Map(result);
        });
        group.MapPut("/{contactId:guid}", async (Guid workspaceId, Guid contactId, ContactRequest request, ContactService service, CancellationToken ct) =>
            Map(await service.UpdateAsync(workspaceId, contactId, request.DisplayName, request.IdentityValues(), ct)));
    }

    private static IResult Map(ContactResult result) => result.Status switch
    {
        ContactStatus.Success when result.Items is not null => Results.Ok(result.Items),
        ContactStatus.Success => Results.Ok(result.Contact),
        ContactStatus.Unauthenticated => Results.Unauthorized(),
        ContactStatus.Invalid => Results.BadRequest(),
        ContactStatus.NotFound => Results.NotFound(),
        ContactStatus.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        ContactStatus.Conflict => Results.Conflict(),
        _ => throw new InvalidOperationException("Unknown contact result.")
    };
}
