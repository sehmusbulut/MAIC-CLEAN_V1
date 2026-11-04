namespace MAIC.Domain.Identity;

public sealed class ExternalIdentity
{
    private ExternalIdentity() { }

    public ExternalIdentity(Guid userId, string issuer, string subject)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (issuer.Length > 512 || subject.Length > 255) throw new ArgumentException("Identity exceeds supported length.");
        UserId = userId;
        Issuer = issuer;
        Subject = subject;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid UserId { get; private set; }
    public string Issuer { get; private set; } = null!;
    public string Subject { get; private set; } = null!;
}
