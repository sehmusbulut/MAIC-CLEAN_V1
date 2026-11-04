namespace MAIC.Application.Identity;

public interface IIdentityResolver
{
    Task<Guid> ResolveAsync(string issuer, string subject, CancellationToken cancellationToken);
    Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken);
}

public interface ICurrentUser
{
    Guid? UserId { get; }
    bool IsSuperAdmin => false;
}
