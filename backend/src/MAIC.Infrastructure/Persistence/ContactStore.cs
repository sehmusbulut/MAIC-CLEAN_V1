using MAIC.Application.Contacts;
using MAIC.Domain.Contacts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

public sealed class ContactStore(MaicDbContext db) : IContactStore
{
    private const string IdentityUniqueIndex = "IX_ContactIdentities_WorkspaceId_Kind_Value";

    private async Task<bool> AuthorizeWriteAsync(Guid actorId, Guid workspaceId, CancellationToken ct)
    {
        // Match membership/rename writers: workspace first, then the authorizing row.
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var members = await db.WorkspaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "WorkspaceMembers" WHERE "WorkspaceId" = {workspaceId} AND "UserId" = {actorId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        // Every persisted membership role may manage contacts.
        return members.Count == 1;
    }

    public async Task<ContactResult> CreateAsync(Guid actorId, Contact contact, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AuthorizeWriteAsync(actorId, contact.WorkspaceId, cancellationToken)) return new(ContactStatus.NotFound);
        db.Contacts.Add(contact);
        var result = await SaveAsync(contact, cancellationToken);
        if (result.Status == ContactStatus.Success) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ContactResult> UpdateAsync(Guid actorId, Guid workspaceId, Guid contactId, string displayName,
        IReadOnlyCollection<ContactIdentityValue> identities, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AuthorizeWriteAsync(actorId, workspaceId, cancellationToken)) return new(ContactStatus.NotFound);
        var contact = await db.Contacts.Include(c => c.Identities)
            .SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Id == contactId, cancellationToken);
        if (contact is null) return new(ContactStatus.NotFound);
        contact.ReplaceDetails(displayName, identities);
        var result = await SaveAsync(contact, cancellationToken);
        if (result.Status == ContactStatus.Success) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<ContactResult> SaveAsync(Contact contact, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return new(ContactStatus.Success, View(contact));
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IdentityUniqueIndex })
        {
            // The caller disposes the uncommitted transaction, rolling back the whole
            // replacement (including deleted identities and name). Discard failed state.
            foreach (var entry in db.ChangeTracker.Entries().Where(e =>
                e.Entity is Contact c && c.Id == contact.Id || e.Entity is ContactIdentity i
                    && i.ContactId == contact.Id && i.WorkspaceId == contact.WorkspaceId).ToArray())
                entry.State = EntityState.Detached;
            return new(ContactStatus.Conflict);
        }
    }

    public async Task<ContactResult> GetAsync(Guid actorId, Guid workspaceId, Guid contactId, CancellationToken cancellationToken)
    {
        var contact = await db.Contacts.AsNoTracking().AsSingleQuery()
            .Where(c => c.WorkspaceId == workspaceId && c.Id == contactId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == actorId))
            .Select(c => new ContactView(c.Id, c.WorkspaceId, c.DisplayName, c.CreatedAt,
                c.Identities.OrderBy(i => i.Kind).ThenBy(i => i.Value)
                    .Select(i => new ContactIdentityView(i.Id, i.Kind, i.Value)).ToList()))
            .SingleOrDefaultAsync(cancellationToken);
        return contact is null ? new(ContactStatus.NotFound) : new(ContactStatus.Success, contact);
    }

    public async Task<ContactResult> SearchAsync(Guid actorId, Guid workspaceId, string query, int skip, int take,
        CancellationToken cancellationToken)
    {
        // Membership is the root so an authorized empty page is distinguishable from
        // a hidden workspace, without a separate authorization snapshot. Contains is
        // translated as literal substring matching, including %, _ and backslash.
        var result = await db.WorkspaceMembers.AsNoTracking().AsSingleQuery()
            .Where(actor => actor.WorkspaceId == workspaceId && actor.UserId == actorId)
            .Select(actor => new
            {
                Items = db.Contacts.Where(c => c.WorkspaceId == workspaceId && c.WorkspaceId == actor.WorkspaceId
                        && (c.DisplayName.Contains(query) || c.Identities.Any(i => i.Value.Contains(query))))
                    .OrderBy(c => c.Id).Skip(skip).Take(take)
                    .Select(c => new ContactView(c.Id, c.WorkspaceId, c.DisplayName, c.CreatedAt,
                        c.Identities.OrderBy(i => i.Kind).ThenBy(i => i.Value)
                            .Select(i => new ContactIdentityView(i.Id, i.Kind, i.Value)).ToList())).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        return result is null ? new(ContactStatus.NotFound) : new(ContactStatus.Success, Items: result.Items);
    }

    private static ContactView View(Contact contact) => new(contact.Id, contact.WorkspaceId, contact.DisplayName, contact.CreatedAt,
        contact.Identities.OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Value, StringComparer.Ordinal)
            .Select(i => new ContactIdentityView(i.Id, i.Kind, i.Value)).ToArray());
}
