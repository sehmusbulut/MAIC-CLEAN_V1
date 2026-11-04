using System.Text.Json;

namespace MAIC.Domain.Contacts;

public sealed record ContactIdentityValue(string? Kind, string? Value);

public sealed class Contact
{
    private readonly List<ContactIdentity> _identities = [];
    private Contact() { }

    public Contact(Guid workspaceId, string displayName, IReadOnlyCollection<ContactIdentityValue> identities)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        WorkspaceId = workspaceId;
        ReplaceDetails(displayName, identities);
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public string DisplayName { get; private set; } = null!;
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Notes { get; private set; }
    public string TagsJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public IReadOnlyCollection<ContactIdentity> Identities => _identities.AsReadOnly();

    public static bool IsValidDetails(string? displayName, IReadOnlyCollection<ContactIdentityValue>? identities)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200
            || !ContactText.IsValid(displayName) || identities is null || identities.Count > 20) return false;
        var seen = new HashSet<(string, string)>();
        foreach (var identity in identities)
        {
            if (identity is null || !ContactIdentity.IsValidKind(identity.Kind) || !ContactIdentity.IsValidValue(identity.Value)
                || !seen.Add((ContactIdentity.CanonicalKind(identity.Kind!), identity.Value!))) return false;
        }
        return true;
    }

    public void ReplaceDetails(string displayName, IReadOnlyCollection<ContactIdentityValue> identities)
    {
        if (!IsValidDetails(displayName, identities)) throw new ArgumentException("Invalid contact details.");
        var replacement = identities.Select(i => (Kind: ContactIdentity.CanonicalKind(i.Kind!), Value: i.Value!)).ToHashSet();
        _identities.RemoveAll(i => !replacement.Contains((i.Kind, i.Value)));
        foreach (var item in replacement)
            if (!_identities.Any(i => i.Kind == item.Kind && i.Value == item.Value))
                _identities.Add(new ContactIdentity(WorkspaceId, Id, item.Kind, item.Value));
        DisplayName = displayName.Trim();
    }

    public void UpdateLeadCard(
        string? displayName,
        string? email,
        string? phone,
        string? notes,
        IReadOnlyCollection<string>? tags)
    {
        if (!string.IsNullOrWhiteSpace(displayName) && displayName.Trim().Length <= 200 && ContactText.IsValid(displayName.Trim()))
        {
            DisplayName = displayName.Trim();
        }

        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (Email is { Length: > 320 }) Email = Email[..320];

        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if (Phone is { Length: > 64 }) Phone = Phone[..64];

        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (Notes is { Length: > 4000 }) Notes = Notes[..4000];

        if (tags is not null)
        {
            var cleanTags = tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Where(t => t.Length <= 64)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(25)
                .ToArray();
            TagsJson = JsonSerializer.Serialize(cleanTags);
        }
    }

    public string[] GetTags()
    {
        if (string.IsNullOrWhiteSpace(TagsJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(TagsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

// PostgreSQL text cannot store NUL or ill-formed UTF-16; other control characters
// are rejected consistently for names, identity values and search terms as well.
public static class ContactText
{
    public static bool IsValid(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i])) return false;
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}
