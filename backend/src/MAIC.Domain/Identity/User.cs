namespace MAIC.Domain.Identity;

public sealed class User
{
    public User() { }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Email { get; private set; } = string.Empty;
    public string FullName { get; private set; } = string.Empty;
    public string? PasswordHash { get; private set; }
    public bool IsSuperAdmin { get; private set; }
    public Guid? PlatformRoleId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static User CreateWithPassword(string email, string fullName, string passwordHash, bool isSuperAdmin = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        return new User
        {
            Email = email.Trim().ToLowerInvariant(),
            FullName = fullName.Trim(),
            PasswordHash = passwordHash,
            IsSuperAdmin = isSuperAdmin
        };
    }

    public void UpdateProfile(string fullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        FullName = fullName.Trim();
    }

    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void SetSuperAdmin(bool isSuperAdmin, Guid? platformRoleId = null)
    {
        IsSuperAdmin = isSuperAdmin;
        PlatformRoleId = platformRoleId;
    }
}
