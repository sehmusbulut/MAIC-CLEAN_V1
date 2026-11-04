using MAIC.Application.Identity;
using MAIC.Domain.Contacts;

namespace MAIC.Application.Contacts;

public enum ContactStatus { Success, Unauthenticated, Invalid, NotFound, Forbidden, Conflict }
public sealed record ContactIdentityView(Guid Id, string Kind, string Value);
public sealed record ContactView(Guid Id, Guid WorkspaceId, string DisplayName, DateTimeOffset CreatedAt,
    IReadOnlyList<ContactIdentityView> Identities);
public sealed record ContactResult(ContactStatus Status, ContactView? Contact = null, IReadOnlyList<ContactView>? Items = null);

// Reads use persisted membership and tenant scope in the same statement. Mutations
// lock workspace then actor membership, retaining authorization through atomic commit.
public interface IContactStore
{
    Task<ContactResult> CreateAsync(Guid actorId, Contact contact, CancellationToken cancellationToken);
    Task<ContactResult> GetAsync(Guid actorId, Guid workspaceId, Guid contactId, CancellationToken cancellationToken);
    Task<ContactResult> SearchAsync(Guid actorId, Guid workspaceId, string query, int skip, int take, CancellationToken cancellationToken);
    Task<ContactResult> UpdateAsync(Guid actorId, Guid workspaceId, Guid contactId, string displayName,
        IReadOnlyCollection<ContactIdentityValue> identities, CancellationToken cancellationToken);
}

public sealed class ContactService(ICurrentUser currentUser, IContactStore store)
{
    private Task<ContactResult> Run(Func<Guid, Task<ContactResult>> action) => currentUser.UserId is { } actor
        ? action(actor) : Task.FromResult(new ContactResult(ContactStatus.Unauthenticated));
    private static Task<ContactResult> Invalid() => Task.FromResult(new ContactResult(ContactStatus.Invalid));

    public Task<ContactResult> CreateAsync(Guid workspaceId, string? displayName,
        IReadOnlyCollection<ContactIdentityValue>? identities, CancellationToken ct) => Run(actor =>
        workspaceId == Guid.Empty || !Contact.IsValidDetails(displayName, identities) ? Invalid()
            : store.CreateAsync(actor, new Contact(workspaceId, displayName!, identities!), ct));

    public Task<ContactResult> GetAsync(Guid workspaceId, Guid contactId, CancellationToken ct) =>
        Run(actor => store.GetAsync(actor, workspaceId, contactId, ct));

    public Task<ContactResult> SearchAsync(Guid workspaceId, string? query, int skip, int take, CancellationToken ct) => Run(actor =>
        skip is < 0 or > 100000 || take is < 1 or > 100 || query is { Length: > 512 }
        || (query is not null && !ContactText.IsValid(query)) ? Invalid()
            : store.SearchAsync(actor, workspaceId, query ?? "", skip, take, ct));

    public Task<ContactResult> UpdateAsync(Guid workspaceId, Guid contactId, string? displayName,
        IReadOnlyCollection<ContactIdentityValue>? identities, CancellationToken ct) => Run(actor =>
        !Contact.IsValidDetails(displayName, identities) ? Invalid()
            : store.UpdateAsync(actor, workspaceId, contactId, displayName!, identities!, ct));
}
