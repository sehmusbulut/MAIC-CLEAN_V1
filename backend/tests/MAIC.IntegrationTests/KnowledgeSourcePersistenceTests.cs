using System.Data.Common;
using System.Text.RegularExpressions;
using MAIC.Application.Knowledge;
using MAIC.Domain.Identity;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class KnowledgeSourcePersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string PreviousMigration = "20260926145008_ConversationHandoff";
    private const string Emoji = "\ud83d\ude42"; // one code point in PostgreSQL, two UTF-16 units in .NET
    private const string Url = "https://example.com/docs";
    private const string KindCheck = "CK_KnowledgeSources_Kind";
    private const string NameCheck = "CK_KnowledgeSources_Name";
    private const string PayloadCheck = "CK_KnowledgeSources_Payload";
    private const string TextCheck = "CK_KnowledgeSources_TextContent";
    private const string UrlCheck = "CK_KnowledgeSources_WebsiteUrl";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Schema_enforces_kind_name_and_null_safe_payload_shape_for_every_null_combination()
    {
        var (workspace, _) = await SeedAsync();
        await using var db = fixture.CreateContext();
        foreach (var kind in new[] { "FILE", "TEXT", "WEBSITE" })
        foreach (var body in new[] { null, "Body" })
        foreach (var link in new[] { null, Url })
        {
            var valid = (kind, body, link) is ("FILE", null, null) or ("TEXT", not null, null) or ("WEBSITE", null, not null);
            if (valid) await InsertAsync(db, workspace.Id, kind, "Name", body, link);
            else await RejectAsync(() => InsertAsync(db, workspace.Id, kind, "Name", body, link), PostgresErrorCodes.CheckViolation, PayloadCheck);
        }
        foreach (var kind in new[] { "SMS", "file", "Text", "", " FILE", "FILE " })
            await RejectAsync(() => InsertAsync(db, workspace.Id, kind, "Name", null, null), PostgresErrorCodes.CheckViolation, KindCheck);
        await RejectAsync(() => InsertAsync(db, workspace.Id, null, "Name", null, null), PostgresErrorCodes.NotNullViolation);
        await RejectAsync(() => InsertAsync(db, workspace.Id, null, "Name", "Body", Url), PostgresErrorCodes.NotNullViolation);
        await RejectAsync(() => InsertAsync(db, workspace.Id, "FILE", null, null, null), PostgresErrorCodes.NotNullViolation);

        var file = await InsertAsync(db, workspace.Id, "FILE", new string('n', 200), null, null);
        // The backstop matches the domain for stored (trimmed) names in both directions: it never rejects a name the domain
        // accepts (200 UTF-16 units ending in a surrogate pair, interior Unicode spaces) and rejects C1 controls, Unicode
        // whitespace-only names and names over 200 UTF-16 units that still fit varchar(200) code points.
        foreach (var name in new[] { new string('n', 198) + Emoji, string.Concat(Enumerable.Repeat(Emoji, 100)), "x",
            "Docs\u3000\u00A0Hub", "Docs \u200B\u202E ok", "\u00DCber \u2003 Wissen" })
        {
            Assert.True(KnowledgeSource.IsValidName(name) && name == name.Trim(), name);
            await SetAsync(db, file, "Name", name);
            Assert.Equal(name, await ReadAsync(db, file, "Name"));
        }
        foreach (var name in new[] { "", " ", "   ", "\u3000", "\u00A0", "\u00A0\u2028\u3000\u205F", "bad\nname", "bad\tname",
            "bad\u0001name", "bad\u007Fname", "bad\u0080name", "bad\u0085name", "bad\u009Fname", new string('n', 199) + Emoji,
            string.Concat(Enumerable.Repeat(Emoji, 100)) + "n" })
        {
            Assert.False(KnowledgeSource.IsValidName(name), name);
            await RejectAsync(() => SetAsync(db, file, "Name", name), PostgresErrorCodes.CheckViolation, NameCheck);
        }
        await RejectAsync(() => SetAsync(db, file, "Name", new string('n', 201)), PostgresErrorCodes.StringDataRightTruncation);

        // Existing rows cannot be moved into a mismatched shape: kind changes and payload removal/addition all fail.
        var text = await InsertAsync(db, workspace.Id, "TEXT", "Text", "Body", null);
        var site = await InsertAsync(db, workspace.Id, "WEBSITE", "Site", null, Url);
        await RejectAsync(() => SetAsync(db, text, "TextContent", null), PostgresErrorCodes.CheckViolation, PayloadCheck);
        await RejectAsync(() => SetAsync(db, text, "WebsiteUrl", Url), PostgresErrorCodes.CheckViolation, PayloadCheck);
        await RejectAsync(() => SetAsync(db, site, "WebsiteUrl", null), PostgresErrorCodes.CheckViolation, PayloadCheck);
        await RejectAsync(() => SetAsync(db, site, "TextContent", "Body"), PostgresErrorCodes.CheckViolation, PayloadCheck);
        await RejectAsync(() => SetAsync(db, file, "TextContent", "Body"), PostgresErrorCodes.CheckViolation, PayloadCheck);
        foreach (var (id, kind) in new[] { (text, "FILE"), (text, "WEBSITE"), (site, "TEXT"), (site, "FILE"), (file, "TEXT"), (file, "WEBSITE") })
            await RejectAsync(() => SetAsync(db, id, "Kind", kind), PostgresErrorCodes.CheckViolation, PayloadCheck);
        await RejectAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"KnowledgeSources\" SET \"TextContent\" = NULL, \"WebsiteUrl\" = NULL WHERE \"Id\" = {text}", Cancellation),
            PostgresErrorCodes.CheckViolation, PayloadCheck);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Cancellation), m => m.EndsWith("_KnowledgeSources"));
    }

    [Fact]
    public async Task Schema_bounds_text_in_utf16_units_and_rejects_disallowed_controls_and_blank_text()
    {
        var (workspace, _) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var id = await InsertAsync(db, workspace.Id, "TEXT", "Text", "Body", null);
        foreach (var valid in new[] { "  padded\r\n\ttext  ", "\u00A0x\u3000", "zero\u200Bwidth", new string('a', 64000),
            string.Concat(Enumerable.Repeat(Emoji, 32000)), string.Concat(Enumerable.Repeat(Emoji, 31999)) + "ab" })
        {
            await SetAsync(db, id, "TextContent", valid);
            Assert.Equal(valid, await ReadAsync(db, id, "TextContent"));
        }
        foreach (var invalid in new[] { string.Concat(Enumerable.Repeat(Emoji, 32000)) + "a",
            string.Concat(Enumerable.Repeat(Emoji, 31999)) + "abc", "", " ", "\t\r\n ", "\u00A0\u2028\u3000\u205F",
            "a\u0001", "a\u0008", "a\u000B", "a\u000C", "a\u000E", "a\u001F", "a\u007F", "a\u0080", "a\u0085", "a\u009F" })
            await RejectAsync(() => SetAsync(db, id, "TextContent", invalid), PostgresErrorCodes.CheckViolation, TextCheck);
        await RejectAsync(() => SetAsync(db, id, "TextContent", new string('a', 64001)), PostgresErrorCodes.StringDataRightTruncation);
        await RejectAsync(() => SetAsync(db, id, "TextContent", "a\0b"), PostgresErrorCodes.CharacterNotInRepertoire);
    }

    [Fact]
    public async Task Schema_accepts_only_bounded_http_urls_with_authority_and_no_credentials_fragment_backslash_or_spaces()
    {
        var (workspace, _) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var id = await InsertAsync(db, workspace.Id, "WEBSITE", "Site", null, Url);
        var prefix = "https://example.com/";
        foreach (var valid in new[] { "https://example.com", "HTTP://EXAMPLE.COM", "hTtPs://example.com:8443/a?b=c",
            "https://example.com/@user", "https://example.com?to=a@b.c", "https://[::1]:8443/x",
            prefix + new string('a', 2048 - prefix.Length), prefix + string.Concat(Enumerable.Repeat(Emoji, (2048 - prefix.Length) / 2)) })
        {
            await SetAsync(db, id, "WebsiteUrl", valid);
            Assert.Equal(valid, await ReadAsync(db, id, "WebsiteUrl"));
        }
        foreach (var invalid in new[] { "", "example.com", "//example.com", "ftp://example.com", "javascript:alert(1)", "http:/x",
            "http:x", "http://", "https://", "http:///path", "http://?q", "http://user:pass@example.com/", "http://user@example.com",
            "http://@example.com", "https://a@b/", "https://example.com#x", "https://example.com/#", "https://example.com/a#b",
            "https://example.com/\\x", "http:\\\\example.com", "https://exa mple.com", "https://example.com/a b", " https://example.com",
            "https://example.com ", "https://example.com/\t", "https://example.com/\n", "https://example.com/\u00A0",
            "https://example.com/\u3000", "https://example.com/\u2028", "https://example.com/\u0085", "https://example.com/\u007F",
            "https://example.com/\u0001", prefix + string.Concat(Enumerable.Repeat(Emoji, (2048 - prefix.Length) / 2)) + "a" })
            await RejectAsync(() => SetAsync(db, id, "WebsiteUrl", invalid), PostgresErrorCodes.CheckViolation, UrlCheck);
        await RejectAsync(() => SetAsync(db, id, "WebsiteUrl", prefix + new string('a', 2049 - prefix.Length)),
            PostgresErrorCodes.StringDataRightTruncation);
    }

    [Fact]
    public async Task Tenant_foreign_key_cascade_and_alternate_key_support_tenant_safe_references()
    {
        var (workspace, actor) = await SeedAsync();
        var (other, _) = await SeedAsync();
        await using var db = fixture.CreateContext();
        await RejectAsync(() => InsertAsync(db, Guid.CreateVersion7(), "FILE", "Orphan", null, null),
            PostgresErrorCodes.ForeignKeyViolation, "FK_KnowledgeSources_Workspaces_WorkspaceId");
        var id = await InsertAsync(db, workspace.Id, "FILE", "File", null, null);
        await RejectAsync(() => SetAsync(db, id, "WorkspaceId", Guid.CreateVersion7()), PostgresErrorCodes.ForeignKeyViolation);
        var store = new KnowledgeSourceStore(db);
        Assert.Equal(KnowledgeSourceStatus.NotFound, (await store.CreateAsync(actor.Id,
            new KnowledgeSource(Guid.CreateVersion7(), KnowledgeSourceKind.FILE, "Unknown", null, null), Cancellation)).Status);

        Assert.Equal(1, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_constraint
            WHERE conname = 'AK_KnowledgeSources_WorkspaceId_Id' AND contype = 'u'
            """).SingleAsync(Cancellation));
        await using (var transaction = await db.Database.BeginTransactionAsync(Cancellation))
        {
            // A future dependent (for example a document) can bind tenant and source through the alternate key; DDL rolls back.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "KnowledgeProbe" ("WorkspaceId" uuid NOT NULL, "SourceId" uuid NOT NULL,
                    FOREIGN KEY ("WorkspaceId", "SourceId") REFERENCES "KnowledgeSources" ("WorkspaceId", "Id"))
                """, Cancellation);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"KnowledgeProbe\" VALUES ({workspace.Id}, {id})", Cancellation);
            await RejectAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"KnowledgeProbe\" VALUES ({other.Id}, {id})", Cancellation), PostgresErrorCodes.ForeignKeyViolation);
            await transaction.RollbackAsync(Cancellation);
        }
        Assert.Equal(0, await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM pg_tables WHERE tablename = 'KnowledgeProbe'").SingleAsync(Cancellation));

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Workspaces\" WHERE \"Id\" = {workspace.Id}", Cancellation);
        Assert.False(await db.KnowledgeSources.AnyAsync(s => s.WorkspaceId == workspace.Id, Cancellation));
    }

    [Fact]
    public async Task Migration_down_and_up_round_trip_in_an_isolated_database()
    {
        var database = "maic_knowledge_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await ExecuteAdminAsync($"CREATE DATABASE \"{database}\"");
        try
        {
            await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(connection).Options);
            await db.Database.MigrateAsync(Cancellation);
            var actor = new User();
            var workspace = new Workspace("Isolated");
            db.AddRange(actor, workspace, new WorkspaceMember(workspace.Id, actor.Id, WorkspaceRole.OWNER),
                new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "Doomed", "Body", null));
            await db.SaveChangesAsync(Cancellation);
            Assert.Equal(1, await TableCountAsync(db));
            db.ChangeTracker.Clear();

            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration, Cancellation);
            Assert.Equal(0, await TableCountAsync(db));
            Assert.DoesNotContain(await db.Database.GetAppliedMigrationsAsync(Cancellation), m => m.EndsWith("_KnowledgeSources"));
            Assert.True(await db.Workspaces.AnyAsync(w => w.Id == workspace.Id, Cancellation));

            await db.Database.MigrateAsync(Cancellation);
            Assert.Equal(1, await TableCountAsync(db));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Cancellation));
            Assert.False(await db.KnowledgeSources.AnyAsync(Cancellation));
            Assert.False(db.Database.HasPendingModelChanges());
            var created = await new KnowledgeSourceStore(db).CreateAsync(actor.Id,
                new KnowledgeSource(workspace.Id, KnowledgeSourceKind.WEBSITE, "Again", null, Url), Cancellation);
            Assert.Equal(KnowledgeSourceStatus.Success, created.Status);
            await RejectAsync(() => SetAsync(db, created.Source!.Id, "WebsiteUrl", "https://user@example.com"),
                PostgresErrorCodes.CheckViolation, UrlCheck);
        }
        finally
        {
            await using (var pooled = new NpgsqlConnection(connection)) NpgsqlConnection.ClearPool(pooled);
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
        await using var shared = fixture.CreateContext();
        Assert.Contains(await shared.Database.GetAppliedMigrationsAsync(Cancellation), m => m.EndsWith("_KnowledgeSources"));
    }

    [Fact]
    public async Task Store_creates_reads_and_toggles_every_kind_for_every_member_role()
    {
        var (workspace, owner) = await SeedAsync();
        var admin = await AddMemberAsync(workspace.Id, WorkspaceRole.ADMIN);
        var agent = await AddMemberAsync(workspace.Id, WorkspaceRole.AGENT);
        await using var db = fixture.CreateContext();
        var store = new KnowledgeSourceStore(db);
        foreach (var actor in new[] { owner.Id, admin, agent })
        {
            var text = await store.CreateAsync(actor, new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "  Notes  ",
                "  exact\r\ntext  ", null), Cancellation);
            Assert.Equal(KnowledgeSourceStatus.Success, text.Status);
            Assert.Equal(("TEXT", "Notes", true, "  exact\r\ntext  ", (string?)null),
                (text.Source!.Kind, text.Source.Name, text.Source.Enabled, text.Source.TextContent, text.Source.WebsiteUrl));
            var site = (await store.CreateAsync(actor, new KnowledgeSource(workspace.Id, KnowledgeSourceKind.WEBSITE, "Site",
                null, "HTTPS://Example.COM/Docs?x=1"), Cancellation)).Source!;
            var file = (await store.CreateAsync(actor, new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, "File",
                null, null), Cancellation)).Source!;

            var disabled = await store.UpdateAsync(actor, workspace.Id, site.Id, null, false, null, null, Cancellation);
            Assert.Equal((false, "HTTPS://Example.COM/Docs?x=1"), (disabled.Source!.Enabled, disabled.Source.WebsiteUrl));
            var edited = await store.UpdateAsync(actor, workspace.Id, site.Id, "Renamed site", null, null, Url, Cancellation);
            Assert.Equal(("Renamed site", false, Url), (edited.Source!.Name, edited.Source.Enabled, edited.Source.WebsiteUrl));
            Assert.Equal(KnowledgeSourceStatus.Success, (await store.UpdateAsync(actor, workspace.Id, file.Id, null, false, null, null, Cancellation)).Status);

            var read = await store.GetAsync(actor, workspace.Id, site.Id, Cancellation);
            Assert.Equal(new KnowledgeSourceView(site.Id, workspace.Id, "WEBSITE", "Renamed site", false, site.CreatedAt, null, Url), read.Source);
            var readFile = (await store.GetAsync(actor, workspace.Id, file.Id, Cancellation)).Source!;
            Assert.Equal(("FILE", false, (string?)null, (string?)null), (readFile.Kind, readFile.Enabled, readFile.TextContent, readFile.WebsiteUrl));
            var readText = (await store.GetAsync(actor, workspace.Id, text.Source.Id, Cancellation)).Source!;
            Assert.Equal("  exact\r\ntext  ", readText.TextContent);
        }
    }

    [Fact]
    public async Task Listing_is_metadata_only_bounded_keyset_paged_and_authorized_in_one_statement()
    {
        var (workspace, actor) = await SeedAsync();
        var (other, outsider) = await SeedAsync();
        var sources = new[]
        {
            new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "Text", "SECRET-TEXT-PAYLOAD", null),
            new KnowledgeSource(workspace.Id, KnowledgeSourceKind.WEBSITE, "Site", null, "https://secret.example/payload"),
            new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, "File", null, null),
            new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "Text 2", "More", null),
            new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, "File 2", null, null)
        }.OrderBy(s => s.Id).ToArray();
        sources[2].SetEnabled(false);
        await using (var setup = fixture.CreateContext())
        {
            setup.KnowledgeSources.AddRange(sources);
            setup.KnowledgeSources.Add(new KnowledgeSource(other.Id, KnowledgeSourceKind.TEXT, "Hidden", "Hidden", null));
            await setup.SaveChangesAsync(Cancellation);
        }
        var capture = new SqlCapture();
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>()
            .UseNpgsql(fixture.ConnectionString).AddInterceptors(capture).Options);
        var store = new KnowledgeSourceStore(db);

        var first = await store.ListAsync(actor.Id, workspace.Id, null, 2, Cancellation);
        var (sql, parameters) = Assert.Single(capture.Commands);
        // SingleOrDefault adds its own outer LIMIT on the membership row, so assert the bound on the KnowledgeSources rows
        // themselves: the correlated page is numbered per workspace in id order and cut at a parameter equal to take + 1.
        var pageBound = Regex.Match(sql, """
            ROW_NUMBER\(\) OVER\(PARTITION BY (\w+)\."WorkspaceId" ORDER BY \1\."Id"\) AS (\w+)\s+FROM "KnowledgeSources" AS \1\b.*?\.\2 <= (@\w+)
            """, RegexOptions.Singleline);
        Assert.True(pageBound.Success, sql);
        Assert.Equal(3, Assert.IsType<int>(Assert.Contains(pageBound.Groups[3].Value, parameters)));
        Assert.DoesNotContain("TextContent", sql);
        Assert.DoesNotContain("WebsiteUrl", sql);
        Assert.Contains("WorkspaceMembers", sql);
        Assert.Equal(KnowledgeSourceStatus.Success, first.Status);
        Assert.Equal(sources[..2].Select(s => s.Id), first.Items!.Select(i => i.Id));
        Assert.Equal(sources[1].Id, first.NextAfter);
        Assert.Equal(new KnowledgeSourceSummary(sources[0].Id, workspace.Id, sources[0].Kind.ToString(), sources[0].Name,
            true, sources[0].CreatedAt), first.Items![0]);

        var second = await store.ListAsync(actor.Id, workspace.Id, first.NextAfter, 2, Cancellation);
        Assert.Equal(sources[2..4].Select(s => s.Id), second.Items!.Select(i => i.Id));
        Assert.False(second.Items![0].Enabled);
        Assert.Equal(sources[3].Id, second.NextAfter);
        var last = await store.ListAsync(actor.Id, workspace.Id, second.NextAfter, 2, Cancellation);
        Assert.Equal(sources[4].Id, Assert.Single(last.Items!).Id);
        Assert.Null(last.NextAfter);
        var exact = await store.ListAsync(actor.Id, workspace.Id, null, 5, Cancellation);
        Assert.Equal(5, exact.Items!.Count);
        Assert.Null(exact.NextAfter);
        var fourOfFive = await store.ListAsync(actor.Id, workspace.Id, null, 4, Cancellation);
        Assert.Equal(sources[3].Id, fourOfFive.NextAfter);
        Assert.All(capture.Commands, command => Assert.DoesNotContain("TextContent", command.Sql));

        capture.Commands.Clear();
        var empty = await store.ListAsync(actor.Id, workspace.Id, sources[4].Id, 100, Cancellation);
        Assert.Equal(KnowledgeSourceStatus.Success, empty.Status);
        Assert.Empty(empty.Items!);
        Assert.Null(empty.NextAfter);
        Assert.Single(capture.Commands);
        capture.Commands.Clear();
        Assert.Equal(KnowledgeSourceStatus.NotFound, (await store.ListAsync(outsider.Id, workspace.Id, null, 100, Cancellation)).Status);
        Assert.Single(capture.Commands);
        capture.Commands.Clear();
        // UUIDv7 ids minted in the same millisecond sort randomly, so pick the TEXT source by payload, not by sorted index.
        var secret = sources.Single(s => s.TextContent == "SECRET-TEXT-PAYLOAD");
        var detail = await store.GetAsync(actor.Id, workspace.Id, secret.Id, Cancellation);
        Assert.Equal("SECRET-TEXT-PAYLOAD", detail.Source!.TextContent);
        Assert.Single(capture.Commands);
        capture.Commands.Clear();
        Assert.Equal(KnowledgeSourceStatus.NotFound, (await store.GetAsync(outsider.Id, workspace.Id, secret.Id, Cancellation)).Status);
        Assert.Single(capture.Commands);
        capture.Commands.Clear();

        foreach (var (after, take) in new (Guid?, int)[] { (null, 0), (null, 101), (Guid.Empty, 10) })
            Assert.Equal(KnowledgeSourceStatus.Invalid, (await store.ListAsync(actor.Id, workspace.Id, after, take, Cancellation)).Status);
        Assert.Empty(capture.Commands);
    }

    [Fact]
    public async Task Wrong_route_workspace_and_nonmembers_get_not_found_before_any_kind_check()
    {
        var (home, actor) = await SeedAsync();
        var (second, _) = await SeedAsync();
        var (_, outsider) = await SeedAsync();
        await AddMemberAsync(second.Id, WorkspaceRole.AGENT, actor.Id);
        await using var db = fixture.CreateContext();
        var store = new KnowledgeSourceStore(db);
        var source = (await store.CreateAsync(actor.Id, new KnowledgeSource(home.Id, KnowledgeSourceKind.TEXT, "Home", "Body", null),
            Cancellation)).Source!;

        Assert.Equal(KnowledgeSourceStatus.NotFound, (await store.GetAsync(actor.Id, second.Id, source.Id, Cancellation)).Status);
        Assert.Equal(KnowledgeSourceStatus.NotFound,
            (await store.UpdateAsync(actor.Id, second.Id, source.Id, "Hijacked", false, "Changed", null, Cancellation)).Status);
        Assert.Equal(KnowledgeSourceStatus.NotFound,
            (await store.UpdateAsync(actor.Id, second.Id, source.Id, null, null, null, Url, Cancellation)).Status);
        Assert.Empty((await store.ListAsync(actor.Id, second.Id, null, 100, Cancellation)).Items!);
        Assert.Equal(KnowledgeSourceStatus.NotFound, (await store.GetAsync(outsider.Id, home.Id, source.Id, Cancellation)).Status);
        Assert.Equal(KnowledgeSourceStatus.NotFound,
            (await store.UpdateAsync(outsider.Id, home.Id, source.Id, null, null, null, Url, Cancellation)).Status);
        Assert.Equal(KnowledgeSourceStatus.NotFound,
            (await store.UpdateAsync(outsider.Id, home.Id, source.Id, "Hijacked", null, null, null, Cancellation)).Status);
        Assert.Equal(KnowledgeSourceStatus.NotFound,
            (await store.UpdateAsync(actor.Id, home.Id, Guid.CreateVersion7(), "Missing", null, null, null, Cancellation)).Status);
        Assert.Equal(KnowledgeSourceStatus.NotFound, (await store.CreateAsync(outsider.Id,
            new KnowledgeSource(home.Id, KnowledgeSourceKind.FILE, "Intruder", null, null), Cancellation)).Status);

        await using var verify = fixture.CreateContext();
        var stored = await verify.KnowledgeSources.AsNoTracking().SingleAsync(s => s.Id == source.Id, Cancellation);
        Assert.Equal((home.Id, "Home", true, "Body"), (stored.WorkspaceId, stored.Name, stored.Enabled, stored.TextContent));
        Assert.Equal(1, await verify.KnowledgeSources.CountAsync(s => s.WorkspaceId == home.Id, Cancellation));
        Assert.Equal(KnowledgeSourceStatus.Success, (await store.GetAsync(actor.Id, home.Id, source.Id, Cancellation)).Status);
    }

    [Fact]
    public async Task Rejected_updates_apply_nothing_and_leave_the_reused_context_usable_and_fresh()
    {
        var (workspace, actor) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new KnowledgeSourceStore(db);
        var site = (await store.CreateAsync(actor.Id, new KnowledgeSource(workspace.Id, KnowledgeSourceKind.WEBSITE, "Site", null, Url), Cancellation)).Source!;
        var text = (await store.CreateAsync(actor.Id, new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "Text", "Body", null), Cancellation)).Source!;
        var file = (await store.CreateAsync(actor.Id, new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, "File", null, null), Cancellation)).Source!;

        foreach (var (id, body, url) in new (Guid, string?, string?)[] { (site.Id, "Body", null), (text.Id, null, Url), (file.Id, "Body", null), (file.Id, null, Url) })
            Assert.Equal(KnowledgeSourceStatus.Invalid,
                (await store.UpdateAsync(actor.Id, workspace.Id, id, "Renamed", false, body, url, Cancellation)).Status);
        foreach (var (name, enabled, body, url) in new (string?, bool?, string?, string?)[]
            { (null, null, null, null), ("Renamed", false, "Body", Url), (" ", false, null, null), (null, false, "a\0", null), (null, false, null, "https://example.com#x") })
            Assert.Equal(KnowledgeSourceStatus.Invalid,
                (await store.UpdateAsync(actor.Id, workspace.Id, text.Id, name, enabled, body, url, Cancellation)).Status);
        Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        await AssertStoredAsync(site.Id, "Site", true, null, Url);
        await AssertStoredAsync(text.Id, "Text", true, "Body", null);
        await AssertStoredAsync(file.Id, "File", true, null, null);

        var updated = await store.UpdateAsync(actor.Id, workspace.Id, text.Id, "  Renamed  ", false, "New body", null, Cancellation);
        Assert.Equal(("Renamed", false, "New body"), (updated.Source!.Name, updated.Source.Enabled, updated.Source.TextContent));
        await AssertStoredAsync(text.Id, "Renamed", false, "New body", null);

        // Another writer commits; the reused context must not answer from its stale tracked copy.
        await using (var elsewhere = fixture.CreateContext())
        {
            var copy = await elsewhere.KnowledgeSources.SingleAsync(s => s.Id == site.Id, Cancellation);
            copy.Rename("Changed elsewhere");
            await elsewhere.SaveChangesAsync(Cancellation);
        }
        var toggled = await store.UpdateAsync(actor.Id, workspace.Id, site.Id, null, false, null, null, Cancellation);
        Assert.Equal(("Changed elsewhere", false, Url), (toggled.Source!.Name, toggled.Source.Enabled, toggled.Source.WebsiteUrl));
        await AssertStoredAsync(site.Id, "Changed elsewhere", false, null, Url);
    }

    [Fact]
    public async Task Database_rejections_roll_back_and_restore_tracked_state_for_reuse()
    {
        const string rejected = "Rejected by database";
        var (workspace, actor) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new KnowledgeSourceStore(db);
        var text = (await store.CreateAsync(actor.Id, new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "Text", "Body", null), Cancellation)).Source!;
        // Simulate a database backstop that is stricter than the domain rules.
        await db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE \"KnowledgeSources\" ADD CONSTRAINT \"CK_Test_Reject\" CHECK (\"Name\" <> '{rejected}')", Cancellation);
        try
        {
            var update = await store.UpdateAsync(actor.Id, workspace.Id, text.Id, rejected, false, "Changed", null, Cancellation);
            Assert.Equal(KnowledgeSourceStatus.Invalid, update.Status);
            var tracked = Assert.Single(db.ChangeTracker.Entries<KnowledgeSource>());
            Assert.Equal(EntityState.Unchanged, tracked.State);
            Assert.Equal(("Text", true, "Body"), (tracked.Entity.Name, tracked.Entity.Enabled, tracked.Entity.TextContent));
            await AssertStoredAsync(text.Id, "Text", true, "Body", null);

            var candidate = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, rejected, null, null);
            Assert.Equal(KnowledgeSourceStatus.Invalid, (await store.CreateAsync(actor.Id, candidate, Cancellation)).Status);
            Assert.Equal(EntityState.Detached, db.Entry(candidate).State);
            Assert.False(await db.KnowledgeSources.AnyAsync(s => s.Id == candidate.Id, Cancellation));

            Assert.Equal(KnowledgeSourceStatus.Success,
                (await store.UpdateAsync(actor.Id, workspace.Id, text.Id, null, false, null, null, Cancellation)).Status);
            Assert.Equal(KnowledgeSourceStatus.Success, (await store.CreateAsync(actor.Id,
                new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, "Accepted", null, null), Cancellation)).Status);
            await AssertStoredAsync(text.Id, "Text", false, "Body", null);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"KnowledgeSources\" DROP CONSTRAINT \"CK_Test_Reject\"", CancellationToken.None);
        }
    }

    private async Task AssertStoredAsync(Guid id, string name, bool enabled, string? text, string? url)
    {
        await using var db = fixture.CreateContext();
        var stored = await db.KnowledgeSources.AsNoTracking().SingleAsync(s => s.Id == id, Cancellation);
        Assert.Equal((name, enabled, text, url), (stored.Name, stored.Enabled, stored.TextContent, stored.WebsiteUrl));
    }

    private static async Task<Guid> InsertAsync(MaicDbContext db, Guid workspaceId, string? kind, string? name, string? text, string? url)
    {
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "KnowledgeSources" ("Id", "WorkspaceId", "Kind", "Name", "TextContent", "WebsiteUrl", "Enabled", "CreatedAt")
            VALUES (@id, @workspace, @kind, @name, @text, @url, TRUE, now())
            """, [Uuid("id", id), Uuid("workspace", workspaceId), Text("kind", kind), Text("name", name), Text("text", text), Text("url", url)],
            Cancellation);
        return id;
    }

    // Column names are fixed test literals; values always travel as typed parameters.
    private static Task SetAsync(MaicDbContext db, Guid id, string column, object? value)
    {
        var sql = "UPDATE \"KnowledgeSources\" SET \"" + column + "\" = @value WHERE \"Id\" = @id";
        return db.Database.ExecuteSqlRawAsync(sql,
            [value is Guid guid ? Uuid("value", guid) : Text("value", (string?)value), Uuid("id", id)], Cancellation);
    }

    private static Task<string> ReadAsync(MaicDbContext db, Guid id, string column)
    {
        var sql = "SELECT \"" + column + "\" AS \"Value\" FROM \"KnowledgeSources\" WHERE \"Id\" = @id";
        return db.Database.SqlQueryRaw<string>(sql, Uuid("id", id)).SingleAsync(Cancellation);
    }

    private static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = value };
    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static async Task RejectAsync(Func<Task> action, string sqlState, string? constraint = null)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(sqlState, error.SqlState);
        if (constraint is not null) Assert.Equal(constraint, error.ConstraintName);
    }

    private static Task<int> TableCountAsync(MaicDbContext db) => db.Database.SqlQuery<int>(
        $"SELECT count(*)::int AS \"Value\" FROM pg_tables WHERE schemaname = 'public' AND tablename = 'KnowledgeSources'")
        .SingleAsync(Cancellation);

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<(Workspace Workspace, User Actor)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var actor = new User();
        var workspace = new Workspace("Knowledge persistence");
        db.Users.Add(actor);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, actor.Id, WorkspaceRole.OWNER));
        await db.SaveChangesAsync(Cancellation);
        return (workspace, actor);
    }

    private async Task<Guid> AddMemberAsync(Guid workspaceId, WorkspaceRole role, Guid? existingUserId = null)
    {
        await using var db = fixture.CreateContext();
        var userId = existingUserId ?? Guid.Empty;
        if (existingUserId is null)
        {
            var user = new User();
            db.Users.Add(user);
            userId = user.Id;
        }
        db.WorkspaceMembers.Add(new WorkspaceMember(workspaceId, userId, role));
        await db.SaveChangesAsync(Cancellation);
        return userId;
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<(string Sql, Dictionary<string, object?> Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // Parameter names are stored with the "@" prefix used in the command text.
            Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>()
                .ToDictionary(p => p.ParameterName.StartsWith('@') ? p.ParameterName : "@" + p.ParameterName, p => (object?)p.Value)));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
