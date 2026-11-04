using MAIC.Application.Contacts;
using MAIC.Domain.Contacts;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class ContactPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Schema_enforces_exact_identity_uniqueness_and_same_workspace_contact_foreign_key()
    {
        var (workspace, actor) = await SeedAsync();
        var (otherWorkspace, otherActor) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ContactStore(db);
        var contact = new Contact(workspace.Id, "Original", [new("email", "Case@Value")]);
        Assert.Equal(ContactStatus.Success, (await store.CreateAsync(actor.Id, contact, Cancellation)).Status);
        // Exact values remain different, including case and surrounding spaces.
        Assert.Equal(ContactStatus.Success, (await store.CreateAsync(actor.Id,
            new Contact(workspace.Id, "Variants", [new("email", "case@Value"), new("email", " Case@Value "), new("crm", "Case@Value")]), Cancellation)).Status);
        Assert.Equal(ContactStatus.Success, (await store.CreateAsync(otherActor.Id,
            new Contact(otherWorkspace.Id, "Other tenant", [new("email", "Case@Value")]), Cancellation)).Status);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ContactIdentities" ("Id", "WorkspaceId", "ContactId", "Kind", "Value")
            VALUES ({Guid.CreateVersion7()}, {workspace.Id}, {contact.Id}, 'email', 'Case@Value')
            """, Cancellation));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("IX_ContactIdentities_WorkspaceId_Kind_Value", duplicate.ConstraintName);
        var crossTenant = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ContactIdentities" ("Id", "WorkspaceId", "ContactId", "Kind", "Value")
            VALUES ({Guid.CreateVersion7()}, {otherWorkspace.Id}, {contact.Id}, 'crm', 'unlinked')
            """, Cancellation));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossTenant.SqlState);
        Assert.Equal("FK_ContactIdentities_Contacts_WorkspaceId_ContactId", crossTenant.ConstraintName);
        var unknownWorkspace = new Contact(Guid.NewGuid(), "Orphan", []);
        db.Contacts.Add(unknownWorkspace);
        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancellation));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(orphan.InnerException).SqlState);
        db.ChangeTracker.Clear();

        var identityId = Assert.Single(contact.Identities).Id;
        foreach (var invalidKind in new[] { "EMAIL", "", "x y" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"ContactIdentities\" SET \"Kind\" = {invalidKind} WHERE \"Id\" = {identityId}", Cancellation));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        foreach (var invalidValue in new[] { "", "bad\nvalue" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"ContactIdentities\" SET \"Value\" = {invalidValue} WHERE \"Id\" = {identityId}", Cancellation));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        var invalidName = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Contacts\" SET \"DisplayName\" = '   ' WHERE \"Id\" = {contact.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidName.SqlState);
        var longValue = new string('v', 513);
        var length = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ContactIdentities\" SET \"Value\" = {longValue} WHERE \"Id\" = {identityId}", Cancellation));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, length.SqlState);
        var nullValue = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ContactIdentities\" SET \"Value\" = NULL WHERE \"Id\" = {identityId}", Cancellation));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, nullValue.SqlState);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Cancellation), m => m.EndsWith("_Contacts"));
    }

    [Fact]
    public async Task Identity_conflicts_roll_back_create_and_entire_replacement_with_original_identity_ids()
    {
        var (workspace, actor) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ContactStore(db);
        var first = new Contact(workspace.Id, "Before", [new("crm", "keep"), new("email", "old")]);
        var second = new Contact(workspace.Id, "Collision", [new("email", "taken")]);
        Assert.Equal(ContactStatus.Success, (await store.CreateAsync(actor.Id, first, Cancellation)).Status);
        Assert.Equal(ContactStatus.Success, (await store.CreateAsync(actor.Id, second, Cancellation)).Status);
        var originalIds = first.Identities.OrderBy(i => i.Id).Select(i => i.Id).ToArray();
        var keepId = first.Identities.Single(i => i.Kind == "crm").Id;
        var failed = new Contact(workspace.Id, "Failed", [new("email", "taken"), new("crm", "new")]);
        Assert.Equal(ContactStatus.Conflict, (await store.CreateAsync(actor.Id, failed, Cancellation)).Status);
        Assert.False(await db.Contacts.AnyAsync(c => c.Id == failed.Id, Cancellation));
        Assert.False(await db.ContactIdentities.AnyAsync(i => i.ContactId == failed.Id, Cancellation));
        Assert.Equal(ContactStatus.Conflict, (await store.UpdateAsync(actor.Id, workspace.Id, first.Id,
            "Must roll back", [new("email", "taken"), new("crm", "new")], Cancellation)).Status);
        var restored = await db.Contacts.AsNoTracking().Include(c => c.Identities).SingleAsync(c => c.Id == first.Id, Cancellation);
        Assert.Equal("Before", restored.DisplayName);
        Assert.Equal(originalIds, restored.Identities.OrderBy(i => i.Id).Select(i => i.Id));
        Assert.Equal(new[] { "keep", "old" }, restored.Identities.Select(i => i.Value).Order());
        Assert.Equal(2, await db.Contacts.CountAsync(c => c.WorkspaceId == workspace.Id, Cancellation));
        // Reuse the context after conflict to prove failed tracked changes cannot leak later.
        var updated = await store.UpdateAsync(actor.Id, workspace.Id, first.Id, "After",
            [new("CRM", "keep"), new("email", "new")], Cancellation);
        Assert.Equal(ContactStatus.Success, updated.Status);
        Assert.Equal(keepId, updated.Contact!.Identities.Single(i => i.Kind == "crm").Id);
        Assert.DoesNotContain(updated.Contact.Identities, i => i.Value == "old");
        Assert.Equal(3, await db.ContactIdentities.CountAsync(i => i.WorkspaceId == workspace.Id, Cancellation));
    }

    [Fact]
    public async Task Search_bounds_contacts_in_one_SQL_snapshot_and_preserves_literal_matching_and_all_page_identities()
    {
        var (workspace, actor) = await SeedAsync();
        var (otherWorkspace, otherActor) = await SeedAsync();
        Contact[] contacts = Enumerable.Range(0, 3).Select(i => new Contact(workspace.Id, "Name " + i,
            [new("crm", "literal%_\\" + i), new("email", "email" + i)])).OrderBy(c => c.Id).ToArray();
        await using (var setup = fixture.CreateContext())
        {
            var store = new ContactStore(setup);
            foreach (var contact in contacts) Assert.Equal(ContactStatus.Success, (await store.CreateAsync(actor.Id, contact, Cancellation)).Status);
            Assert.Equal(ContactStatus.Success, (await store.CreateAsync(otherActor.Id,
                new Contact(otherWorkspace.Id, "Name other", [new("crm", "literal%_\\")]), Cancellation)).Status);
        }
        var commands = new List<string>();
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(fixture.ConnectionString)
            .LogTo(commands.Add, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information).Options);
        var read = new ContactStore(db);
        var page = await read.SearchAsync(actor.Id, workspace.Id, "%_\\", 1, 1, Cancellation);
        Assert.Equal(ContactStatus.Success, page.Status);
        var item = Assert.Single(page.Items!);
        Assert.Equal(contacts[1].Id, item.Id);
        Assert.Equal(2, item.Identities.Count);
        var sql = Assert.Single(commands);
        Assert.True(sql.Contains("LIMIT") && sql.Contains("OFFSET")
            || sql.Contains("ROW_NUMBER() OVER") && sql.Contains(".row <="), "Pagination must bound contacts within SQL.");
        foreach (var (query, skip) in new[] { ("LITERAL", 0), ("%_\\", 100) })
        {
            commands.Clear();
            var empty = await read.SearchAsync(actor.Id, workspace.Id, query, skip, 1, Cancellation);
            Assert.Equal(ContactStatus.Success, empty.Status);
            Assert.Empty(empty.Items!);
            Assert.Single(commands);
        }
        commands.Clear();
        Assert.Equal(ContactStatus.NotFound, (await read.SearchAsync(otherActor.Id, workspace.Id, "", 100, 1, Cancellation)).Status);
        Assert.Single(commands);
        commands.Clear();
        var get = await read.GetAsync(actor.Id, workspace.Id, contacts[0].Id, Cancellation);
        Assert.Equal(ContactStatus.Success, get.Status);
        Assert.Equal(2, get.Contact!.Identities.Count);
        Assert.Single(commands);
        commands.Clear();
        Assert.Equal(ContactStatus.NotFound, (await read.GetAsync(otherActor.Id, workspace.Id, contacts[0].Id, Cancellation)).Status);
        Assert.Single(commands);
    }

    private async Task<(Workspace Workspace, User Actor)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var user = new User();
        var workspace = new Workspace("Contact tests");
        db.Users.Add(user);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, user.Id, WorkspaceRole.OWNER));
        await db.SaveChangesAsync(Cancellation);
        return (workspace, user);
    }
}
