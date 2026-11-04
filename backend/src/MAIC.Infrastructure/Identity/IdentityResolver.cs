using MAIC.Application.Identity;
using MAIC.Domain.Identity;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAIC.Infrastructure.Identity;

public sealed class IdentityResolver(MaicDbContext db) : IIdentityResolver
{
    public async Task<Guid> ResolveAsync(string issuer, string subject, CancellationToken cancellationToken)
    {
        var existing = await FindAsync(issuer, subject, cancellationToken);
        if (existing.HasValue) return existing.Value;

        var user = new User();
        var identity = new ExternalIdentity(user.Id, issuer, subject);
        db.Users.Add(user);
        db.ExternalIdentities.Add(identity);
        try
        {
            // SaveChanges is atomic: a competing identity insertion also rolls back the new user.
            await db.SaveChangesAsync(cancellationToken);
            return user.Id;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_ExternalIdentities_Issuer_Subject" })
        {
            db.Entry(identity).State = EntityState.Detached;
            db.Entry(user).State = EntityState.Detached;
            return await FindAsync(issuer, subject, cancellationToken)
                ?? throw new InvalidOperationException("Concurrent identity disappeared; retry authentication.");
        }
    }

    public Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(x => x.Id == userId, cancellationToken);

    private Task<Guid?> FindAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        db.ExternalIdentities.Where(x => x.Issuer == issuer && x.Subject == subject)
            .Select(x => (Guid?)x.UserId).SingleOrDefaultAsync(cancellationToken);
}
