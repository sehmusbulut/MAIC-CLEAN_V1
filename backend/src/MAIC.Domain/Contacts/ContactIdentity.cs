namespace MAIC.Domain.Contacts;

public sealed class ContactIdentity
{
    private ContactIdentity() { }
    internal ContactIdentity(Guid workspaceId, Guid contactId, string kind, string value)
    {
        WorkspaceId = workspaceId;
        ContactId = contactId;
        Kind = kind;
        Value = value;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid ContactId { get; private set; }
    public string Kind { get; private set; } = null!;
    public string Value { get; private set; } = null!;

    internal static string CanonicalKind(string kind) => kind.Trim().ToLowerInvariant();
    internal static bool IsValidKind(string? kind)
    {
        if (kind is null || !ContactText.IsValid(kind)) return false;
        var canonical = CanonicalKind(kind);
        return canonical.Length is >= 1 and <= 64 && canonical[0] is >= 'a' and <= 'z'
            && canonical.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or ':' or '-');
    }
    internal static bool IsValidValue(string? value) => value is { Length: >= 1 and <= 512 } && ContactText.IsValid(value);
}
