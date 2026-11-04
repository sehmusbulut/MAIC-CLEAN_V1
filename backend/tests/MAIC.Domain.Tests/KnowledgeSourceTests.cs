using MAIC.Domain.Knowledge;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class KnowledgeSourceTests
{
    private const string Emoji = "\ud83d\ude42"; // one supplementary code point, two UTF-16 units
    private const string Url = "https://example.com/docs";

    [Fact]
    public void Lifecycle_keeps_identity_kind_and_payload_while_toggling_and_renaming()
    {
        var workspaceId = Guid.CreateVersion7();
        const string text = "  Exact\r\n\ttext with trailing space  ";
        var source = new KnowledgeSource(workspaceId, KnowledgeSourceKind.TEXT, "  Handbook  ", text, null);
        Assert.Equal('7', source.Id.ToString()[14]);
        Assert.Equal(workspaceId, source.WorkspaceId);
        Assert.Equal(KnowledgeSourceKind.TEXT, source.Kind);
        Assert.Equal("Handbook", source.Name);
        Assert.Same(text, source.TextContent);
        Assert.Null(source.WebsiteUrl);
        Assert.True(source.Enabled);
        var (id, created) = (source.Id, source.CreatedAt);

        source.SetEnabled(false);
        Assert.False(source.Enabled);
        Assert.Equal(text, source.TextContent);
        source.Rename("  Renamed  ");
        source.Update(null, true, null, null);
        Assert.True(source.Enabled);
        source.Update("Other", false, "New text", null);
        Assert.Equal(("Other", false, "New text"), (source.Name, source.Enabled, source.TextContent));
        Assert.Equal((id, workspaceId, created, KnowledgeSourceKind.TEXT), (source.Id, source.WorkspaceId, source.CreatedAt, source.Kind));

        var website = new KnowledgeSource(workspaceId, KnowledgeSourceKind.WEBSITE, "Site", null, "HTTPS://Example.COM/A?b=C");
        Assert.Equal("HTTPS://Example.COM/A?b=C", website.WebsiteUrl);
        website.SetEnabled(false);
        website.Update(null, null, null, Url);
        Assert.Equal(Url, website.WebsiteUrl);
        Assert.False(website.Enabled);
        Assert.Null(website.TextContent);

        var file = new KnowledgeSource(workspaceId, KnowledgeSourceKind.FILE, "Upload", null, null);
        file.SetEnabled(false);
        file.SetEnabled(true);
        Assert.Equal((KnowledgeSourceKind.FILE, (string?)null, (string?)null, true), (file.Kind, file.TextContent, file.WebsiteUrl, file.Enabled));
        Assert.NotEqual(file.Id, website.Id);
    }

    [Fact]
    public void Factory_rejects_missing_workspace_undefined_kinds_and_mixed_or_missing_payloads()
    {
        var ws = Guid.CreateVersion7();
        Assert.Throws<ArgumentException>(() => new KnowledgeSource(Guid.Empty, KnowledgeSourceKind.FILE, "Name", null, null));
        Assert.ThrowsAny<ArgumentException>(() => new KnowledgeSource(ws, (KnowledgeSourceKind)3, "Name", null, null));
        Assert.ThrowsAny<ArgumentException>(() => new KnowledgeSource(ws, (KnowledgeSourceKind)(-1), "Name", null, null));
        Assert.Throws<ArgumentException>(() => new KnowledgeSource(ws, KnowledgeSourceKind.FILE, " ", null, null));
        var invalid = new (KnowledgeSourceKind Kind, string? Text, string? Url)[]
        {
            (KnowledgeSourceKind.FILE, "text", null), (KnowledgeSourceKind.FILE, null, Url), (KnowledgeSourceKind.FILE, "text", Url),
            (KnowledgeSourceKind.TEXT, null, null), (KnowledgeSourceKind.TEXT, null, Url), (KnowledgeSourceKind.TEXT, "text", Url),
            (KnowledgeSourceKind.TEXT, " ", null),
            (KnowledgeSourceKind.WEBSITE, null, null), (KnowledgeSourceKind.WEBSITE, "text", null), (KnowledgeSourceKind.WEBSITE, "text", Url),
            (KnowledgeSourceKind.WEBSITE, null, "ftp://example.com"),
            (KnowledgeSourceKind.FILE, "", null), (KnowledgeSourceKind.FILE, null, "")
        };
        foreach (var (kind, text, url) in invalid)
        {
            Assert.False(KnowledgeSource.IsValidPayload(kind, text, url));
            Assert.Throws<ArgumentException>(() => new KnowledgeSource(ws, kind, "Name", text, url));
        }
        Assert.True(KnowledgeSource.IsValidPayload(KnowledgeSourceKind.FILE, null, null));
        Assert.True(KnowledgeSource.IsValidPayload(KnowledgeSourceKind.TEXT, "text", null));
        Assert.True(KnowledgeSource.IsValidPayload(KnowledgeSourceKind.WEBSITE, null, Url));
        Assert.False(KnowledgeSource.IsValidPayload((KnowledgeSourceKind)7, null, null));
    }

    [Fact]
    public void Kind_names_parse_exactly_and_case_sensitively()
    {
        Assert.True(KnowledgeSource.TryParseKind("FILE", out var file) && file == KnowledgeSourceKind.FILE);
        Assert.True(KnowledgeSource.TryParseKind("TEXT", out var text) && text == KnowledgeSourceKind.TEXT);
        Assert.True(KnowledgeSource.TryParseKind("WEBSITE", out var site) && site == KnowledgeSourceKind.WEBSITE);
        foreach (var invalid in new[] { null, "", "file", "Text", "website", " FILE", "FILE ", "0", "1", "2", "7", "URL", "FILE,TEXT" })
            Assert.False(KnowledgeSource.TryParseKind(invalid, out _), invalid);
    }

    [Fact]
    public void Names_are_trimmed_bounded_and_reject_controls_and_malformed_utf16()
    {
        var source = new KnowledgeSource(Guid.CreateVersion7(), KnowledgeSourceKind.FILE, "Original", null, null);
        foreach (var invalid in new[] { "", " ", "\t", "\u3000", "bad\0name", "bad\nname", "bad\u007Fname", "bad\u0085name",
            "\ud800", "\udc00", "a\udc00\ud800b", "x\ud83d", new string('n', 201), "  " + new string('n', 201) + "  ",
            string.Concat(Enumerable.Repeat(Emoji, 100)) + "n" })
        {
            Assert.False(KnowledgeSource.IsValidName(invalid));
            Assert.Throws<ArgumentException>(() => new KnowledgeSource(source.WorkspaceId, KnowledgeSourceKind.FILE, invalid, null, null));
            Assert.Throws<ArgumentException>(() => source.Rename(invalid));
            Assert.Throws<ArgumentException>(() => source.Update(invalid, false, null, null));
            Assert.Equal("Original", source.Name);
            Assert.True(source.Enabled);
        }
        Assert.False(KnowledgeSource.IsValidName(null));
        Assert.True(KnowledgeSource.IsValidName(new string('n', 200)));
        Assert.True(KnowledgeSource.IsValidName("  " + new string('n', 200) + "  "));
        Assert.True(KnowledgeSource.IsValidName(string.Concat(Enumerable.Repeat(Emoji, 100))));
        Assert.True(KnowledgeSource.IsValidName("Docs \u200B\u202E ok"));
        source.Rename("  " + new string('n', 200) + "  ");
        Assert.Equal(new string('n', 200), source.Name);
    }

    [Fact]
    public void Text_content_is_exact_nonblank_bounded_and_allows_only_tab_newline_and_return()
    {
        foreach (var valid in new[] { "a", "  padded  ", "line\r\nnext\ttab\n", "\u00A0x\u3000", "zero\u200Bwidth", Emoji,
            new string('a', 64000), string.Concat(Enumerable.Repeat(Emoji, 32000)),
            string.Concat(Enumerable.Repeat(Emoji, 31999)) + "ab" })
            Assert.True(KnowledgeSource.IsValidTextContent(valid));

        foreach (var invalid in new[] { null, "", " ", "\t\r\n", "\u00A0\u2028\u3000\u0085", "a\0b", "a\u0001", "a\u0008", "a\u000B",
            "a\u000C", "a\u001F", "a\u007F", "a\u0080", "a\u0085", "a\u009F", "a\ud800", "a\udc00", "\udc00\ud800", "a\ud83d",
            new string('a', 64001), string.Concat(Enumerable.Repeat(Emoji, 32000)) + "a" })
            Assert.False(KnowledgeSource.IsValidTextContent(invalid));
    }

    [Fact]
    public void Website_urls_require_absolute_http_with_host_and_no_credentials_fragment_or_invisible_text()
    {
        var prefix = "https://example.com/";
        foreach (var valid in new[] { "https://example.com", "http://example.com", "HTTPS://Example.COM/Path?q=1",
            "HtTp://example.com:8080/a/b?c=d&e=f", "https://example.com/@user", "https://example.com/?to=a@b.c",
            "https://example.com?x=1", "https://b\u00FCcher.de/", "https://\u00F6rnek.com.tr/yol", "https://[::1]:8443/x",
            "https://[2001:db8::1]/", "http://127.0.0.1/", "http://localhost:5080/", "https://example.com:8443/a/../b/./c?q=1",
            "https://example.com/" + Emoji, prefix + new string('a', 2048 - prefix.Length),
            prefix + string.Concat(Enumerable.Repeat(Emoji, (2048 - prefix.Length) / 2)) })
            Assert.True(KnowledgeSource.IsValidWebsiteUrl(valid), valid);

        // .NET's parser accepts (and silently rewrites) these; the raw prefix/character/authority rules still reject them.
        foreach (var lenient in new[] { "http:\\\\example.com", " https://example.com ", "https://example.com/a b",
            "https://user:pass@example.com/", "https://example.com/#frag" })
        {
            Assert.True(Uri.TryCreate(lenient, UriKind.Absolute, out _), lenient);
            Assert.False(KnowledgeSource.IsValidWebsiteUrl(lenient), lenient);
        }
        // Non-ASCII hosts whose IDN mapping yields a delimiter (fullwidth @ / # ? \ : and parenthesized digits) parse
        // as hosts, but the mapped host is not a valid DNS name.
        foreach (var lookalike in new[] { "https://example\uFF20evil.com/", "https://example\uFF0Fevil.com/",
            "https://example\uFF03evil.com/", "https://example\uFF1Fevil.com/", "https://example\uFF3Cevil.com/",
            "https://example\uFF1Aevil.com/", "https://user\uFF20example.com/", "https://ex\u2474.com/" })
        {
            Assert.True(Uri.TryCreate(lookalike, UriKind.Absolute, out _), lookalike);
            Assert.False(KnowledgeSource.IsValidWebsiteUrl(lookalike), lookalike);
        }
        foreach (var invalid in new[] { null, "", "example.com", "//example.com", "ftp://example.com", "file:///etc/passwd",
            "javascript:alert(1)", "mailto:a@b.c", "ws://example.com", "http:/x", "http:x", "http:", "http://", "https://",
            "http:///path", "http://?q=1", "http://:80/", "http://user:pass@example.com/", "http://user@example.com",
            "http://@example.com", "https://a@b@example.com", "https://example.com#frag", "https://example.com/#",
            "https://example.com/a#b", "https://example.com\\path", "http:\\\\example.com", "https:/\\example.com",
            " https://example.com", "https://example.com ", "https://exa mple.com", "https://example.com/a b",
            "https://example.com/\t", "https://example.com/\n", "https://example.com/\u00A0", "https://example.com/\u3000",
            "https://example.com/\u2028", "https://example.com/\u0085", "https://example.com/\u007F", "https://example.com/\0",
            "https://example.com/\u001B", "https://example.com/\u202E", "https://example.com/\u2066", "https://example.com/\u200B",
            "https://example.com/\u200E", "https://example.com/\uFEFF", "https://example.com/\u00AD", "https://example.com/\U000E0001",
            "https://example.com/\ud800", "https://example.com/\udc00", "https://example.com/\udc00\ud800",
            prefix + new string('a', 2049 - prefix.Length),
            prefix + string.Concat(Enumerable.Repeat(Emoji, (2048 - prefix.Length) / 2)) + "a" })
            Assert.False(KnowledgeSource.IsValidWebsiteUrl(invalid), invalid);
    }

    [Fact]
    public void Updates_are_validated_and_rejected_whole_when_payload_does_not_fit_the_kind()
    {
        var ws = Guid.CreateVersion7();
        var file = new KnowledgeSource(ws, KnowledgeSourceKind.FILE, "File", null, null);
        var text = new KnowledgeSource(ws, KnowledgeSourceKind.TEXT, "Text", "Body", null);
        var site = new KnowledgeSource(ws, KnowledgeSourceKind.WEBSITE, "Site", null, Url);
        var snapshot = new[] { Snapshot(file), Snapshot(text), Snapshot(site) };

        Assert.Throws<InvalidOperationException>(() => file.Update("Renamed", false, "Body", null));
        Assert.Throws<InvalidOperationException>(() => file.Update("Renamed", false, null, Url));
        Assert.Throws<InvalidOperationException>(() => text.Update("Renamed", false, null, Url));
        Assert.Throws<InvalidOperationException>(() => site.Update("Renamed", false, "Body", null));
        Assert.Throws<ArgumentException>(() => text.Update("Renamed", false, "Body", Url));
        Assert.Throws<ArgumentException>(() => text.Update(null, null, null, null));
        Assert.Throws<ArgumentException>(() => text.Update("Renamed", false, " ", null));
        Assert.Throws<ArgumentException>(() => site.Update("Renamed", false, null, "https://user@example.com"));
        Assert.Equal(snapshot, new[] { Snapshot(file), Snapshot(text), Snapshot(site) });

        Assert.True(file.AcceptsPayload(null, null));
        Assert.False(file.AcceptsPayload("Body", null));
        Assert.False(file.AcceptsPayload(null, Url));
        Assert.True(text.AcceptsPayload("Body", null));
        Assert.False(text.AcceptsPayload(null, Url));
        Assert.True(site.AcceptsPayload(null, Url));
        Assert.False(site.AcceptsPayload("Body", null));

        Assert.False(KnowledgeSource.IsValidUpdate(null, null, null, null));
        Assert.False(KnowledgeSource.IsValidUpdate(null, null, "Body", Url));
        Assert.False(KnowledgeSource.IsValidUpdate(" ", true, null, null));
        Assert.False(KnowledgeSource.IsValidUpdate(null, true, "a\0", null));
        Assert.False(KnowledgeSource.IsValidUpdate(null, true, null, "https://example.com#x"));
        Assert.True(KnowledgeSource.IsValidUpdate(null, false, null, null));
        Assert.True(KnowledgeSource.IsValidUpdate("Name", null, null, null));
        Assert.True(KnowledgeSource.IsValidUpdate(null, null, "Body", null));
        Assert.True(KnowledgeSource.IsValidUpdate(null, null, null, Url));

        site.Update(null, false, null, null);
        Assert.Equal((false, Url), (site.Enabled, site.WebsiteUrl));
        site.Update(null, true, null, null);
        Assert.Equal((true, Url), (site.Enabled, site.WebsiteUrl));
    }

    private static (string, bool, string?, string?, KnowledgeSourceKind) Snapshot(KnowledgeSource s) =>
        (s.Name, s.Enabled, s.TextContent, s.WebsiteUrl, s.Kind);
}
