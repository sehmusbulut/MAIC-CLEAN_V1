using MAIC.Domain.Identity;
using MAIC.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class IdentityPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_matches_model_and_enforces_identity_unique_key_and_user_foreign_key()
    {
        await using var db = fixture.CreateContext();
        Assert.Contains("20260919132340_InitialIdentity", await db.Database.GetAppliedMigrationsAsync(Cancellation));
        Assert.False(db.Database.HasPendingModelChanges());
        var issuer = "https://issuer/" + Guid.NewGuid();
        var id = await new IdentityResolver(db).ResolveAsync(issuer, "subject", Cancellation);
        db.ExternalIdentities.Add(new ExternalIdentity(id, issuer, "subject"));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancellation));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        db.ChangeTracker.Clear();
        db.ExternalIdentities.Add(new ExternalIdentity(Guid.CreateVersion7(), issuer, "orphan"));
        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancellation));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(orphan.InnerException).SqlState);
    }

    [Fact]
    public async Task Exact_issuer_subject_pair_is_stable_and_case_sensitive()
    {
        var issuer = "https://issuer/" + Guid.NewGuid();
        await using var db = fixture.CreateContext();
        var resolver = new IdentityResolver(db);
        var first = await resolver.ResolveAsync(issuer, "Subject", Cancellation);
        Assert.Equal(first, await resolver.ResolveAsync(issuer, "Subject", Cancellation));
        Assert.NotEqual(first, await resolver.ResolveAsync(issuer, "subject", Cancellation));
        Assert.NotEqual(first, await resolver.ResolveAsync(issuer + "/", "Subject", Cancellation));
        Assert.Equal('7', first.ToString()[14]);
        Assert.All(await db.ExternalIdentities.Where(x => x.Issuer == issuer).ToListAsync(Cancellation),
            identity => Assert.Equal('7', identity.Id.ToString()[14]));
    }

    [Fact]
    public async Task Concurrent_first_logins_create_one_identity_and_no_orphan_users()
    {
        await using var before = fixture.CreateContext();
        var usersBefore = await before.Users.CountAsync(Cancellation);
        var issuer = "https://concurrent/" + Guid.NewGuid();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 16).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            await start.Task;
            return await new IdentityResolver(db).ResolveAsync(issuer, "same-subject", Cancellation);
        }).ToArray();
        start.SetResult();
        var ids = await Task.WhenAll(tasks);
        Assert.Single(ids.Distinct());
        await using var after = fixture.CreateContext();
        Assert.Equal(1, await after.ExternalIdentities.CountAsync(x => x.Issuer == issuer, Cancellation));
        Assert.Equal(usersBefore + 1, await after.Users.CountAsync(Cancellation));
    }
}
