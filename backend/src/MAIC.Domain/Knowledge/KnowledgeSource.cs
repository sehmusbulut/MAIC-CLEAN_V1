using System.Buffers;
using System.Globalization;
using System.Text;

namespace MAIC.Domain.Knowledge;

public enum KnowledgeSourceKind
{
    FILE,
    TEXT,
    WEBSITE
}

/// <summary>
/// A workspace-owned knowledge source record. Its kind is fixed at creation and decides which payload it carries:
/// FILE has no inline payload, TEXT carries exact text content and WEBSITE carries an unverified http(s) URL.
/// A stored URL is never fetched, resolved or considered safe here; any future fetcher must validate the
/// network destination at fetch time.
/// </summary>
public sealed class KnowledgeSource
{
    public const int MaxNameLength = 200;
    public const int MaxTextContentLength = 64000;
    public const int MaxWebsiteUrlLength = 2048;

    private KnowledgeSource() { }

    public KnowledgeSource(Guid workspaceId, KnowledgeSourceKind kind, string name, string? textContent, string? websiteUrl)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), "Knowledge source kind is not supported.");
        if (!IsValidName(name)) throw new ArgumentException(NameError, nameof(name));
        if (!IsValidPayload(kind, textContent, websiteUrl))
            throw new ArgumentException("Knowledge source payload does not match its kind or is invalid.");

        WorkspaceId = workspaceId;
        Kind = kind;
        Name = name.Trim();
        TextContent = textContent;
        WebsiteUrl = websiteUrl;
    }

    private const string NameError = "Knowledge source name must contain 1–200 characters without control characters.";

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public KnowledgeSourceKind Kind { get; private set; }
    public string Name { get; private set; } = null!;
    public string? TextContent { get; private set; }
    public string? WebsiteUrl { get; private set; }
    public bool Enabled { get; private set; } = true;
    // Microsecond precision matches PostgreSQL, so the creation response equals later reads.
    public DateTimeOffset CreatedAt { get; private set; } = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);

    /// <summary>Parses the exact, case-sensitive wire name of a kind. Numbers and other spellings are rejected.</summary>
    public static bool TryParseKind(string? value, out KnowledgeSourceKind kind)
    {
        (var valid, kind) = value switch
        {
            "FILE" => (true, KnowledgeSourceKind.FILE),
            "TEXT" => (true, KnowledgeSourceKind.TEXT),
            "WEBSITE" => (true, KnowledgeSourceKind.WEBSITE),
            _ => (false, default)
        };
        return valid;
    }

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength) return false;
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsControl(name[i])) return false;
            if (char.IsHighSurrogate(name[i]))
            {
                if (++i >= name.Length || !char.IsLowSurrogate(name[i])) return false;
            }
            else if (char.IsLowSurrogate(name[i])) return false;
        }
        return true;
    }

    /// <summary>Text is kept exactly as supplied; only tab, line feed and carriage return are allowed controls.</summary>
    public static bool IsValidTextContent(string? textContent)
    {
        if (string.IsNullOrWhiteSpace(textContent) || textContent.Length > MaxTextContentLength) return false;
        for (var i = 0; i < textContent.Length; i++)
        {
            var c = textContent[i];
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r')) return false;
            if (char.IsHighSurrogate(c))
            {
                if (++i >= textContent.Length || !char.IsLowSurrogate(textContent[i])) return false;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Accepts an absolute http(s) URL with no credentials or fragment whose host, after IDN mapping, is a DNS name or
    /// IP literal. The URL is stored exactly as supplied. Passing this check says nothing about reachability or network
    /// safety (SSRF).
    /// </summary>
    public static bool IsValidWebsiteUrl(string? websiteUrl)
    {
        if (websiteUrl is null || websiteUrl.Length is 0 or > MaxWebsiteUrlLength) return false;

        var prefix = websiteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https://".Length
            : websiteUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http://".Length : 0;
        if (prefix == 0) return false;

        var text = websiteUrl.AsSpan();
        while (!text.IsEmpty)
        {
            // Lone or reversed surrogates are malformed UTF-16.
            if (Rune.DecodeFromUtf16(text, out var rune, out var consumed) != OperationStatus.Done) return false;
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) || rune.Value is '#' or '\\'
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) return false;
            text = text[consumed..];
        }

        var authority = websiteUrl.AsSpan(prefix);
        var authorityEnd = authority.IndexOfAny('/', '?');
        if (authorityEnd >= 0) authority = authority[..authorityEnd];
        if (authority.IsEmpty || authority.Contains('@')) return false;

        if (!Uri.TryCreate(websiteUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0
            || uri.UserInfo.Length != 0) return false;

        // IDN (UTS #46) mapping can turn look-alikes such as fullwidth '@', '/', '#', '?', '\' or ':' into delimiters, so
        // the mapped host must still be a DNS name or IP literal. IdnHost omits IPv6 brackets; CheckHostName accepts both.
        string idnHost;
        try
        {
            idnHost = uri.IdnHost;
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException)
        {
            return false;
        }
        return Uri.CheckHostName(idnHost) != UriHostNameType.Unknown;
    }

    /// <summary>FILE carries no inline payload; TEXT requires only text; WEBSITE requires only a URL.</summary>
    public static bool IsValidPayload(KnowledgeSourceKind kind, string? textContent, string? websiteUrl) => kind switch
    {
        KnowledgeSourceKind.FILE => textContent is null && websiteUrl is null,
        KnowledgeSourceKind.TEXT => websiteUrl is null && IsValidTextContent(textContent),
        KnowledgeSourceKind.WEBSITE => textContent is null && IsValidWebsiteUrl(websiteUrl),
        _ => false
    };

    /// <summary>
    /// Static validation of a partial update: at least one field, at most one payload field, and every supplied
    /// value valid. Whether the payload field fits this source's kind is checked by <see cref="AcceptsPayload"/>.
    /// </summary>
    public static bool IsValidUpdate(string? name, bool? enabled, string? textContent, string? websiteUrl) =>
        (name is not null || enabled is not null || textContent is not null || websiteUrl is not null)
        && (textContent is null || websiteUrl is null)
        && (name is null || IsValidName(name))
        && (textContent is null || IsValidTextContent(textContent))
        && (websiteUrl is null || IsValidWebsiteUrl(websiteUrl));

    /// <summary>Only TEXT sources take text content and only WEBSITE sources take a URL.</summary>
    public bool AcceptsPayload(string? textContent, string? websiteUrl) =>
        (textContent is null || Kind == KnowledgeSourceKind.TEXT) && (websiteUrl is null || Kind == KnowledgeSourceKind.WEBSITE);

    public void Rename(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException(NameError, nameof(name));
        Name = name.Trim();
    }

    public void SetEnabled(bool enabled) => Enabled = enabled;

    /// <summary>
    /// Applies a validated partial update atomically: every value and the payload/kind fit are checked before any
    /// field changes, so a rejected update leaves the source untouched. Kind, workspace, ID and creation time never change.
    /// </summary>
    public void Update(string? name, bool? enabled, string? textContent, string? websiteUrl)
    {
        if (!IsValidUpdate(name, enabled, textContent, websiteUrl)) throw new ArgumentException("Knowledge source update is invalid.");
        if (!AcceptsPayload(textContent, websiteUrl)) throw new InvalidOperationException("Payload does not match the knowledge source kind.");
        if (name is not null) Name = name.Trim();
        if (enabled is { } state) Enabled = state;
        if (textContent is not null) TextContent = textContent;
        if (websiteUrl is not null) WebsiteUrl = websiteUrl;
    }
}
