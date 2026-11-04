using System.Data.Common;
using MAIC.Application.AI;
using MAIC.Domain.AI;
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

public sealed class AiAgentPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string PreviousMigration = "IngestionFairness";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static AiAgentSettings Settings(AiKnowledgeScope scope = AiKnowledgeScope.ALL_SOURCES, Guid[]? sources = null, string name = "Bot") =>
        new(name, false, "Be helpful.", "en", AiModelTier.FAST, scope, sources ?? [], true, true, 3);

    private async Task<(Guid Owner, Guid Workspace, Guid Source)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var owner = new User();
        var workspace = new Workspace("Agent persistence");
        var source = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "FAQ", "Answers", null);
        db.AddRange(owner, workspace, new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER), source);
        await db.SaveChangesAsync(Cancellation);
        return (owner.Id, workspace.Id, source.Id);
    }

    private async Task<AiAgent> AddAgentAsync(Guid workspace, Guid? source = null)
    {
        await using var db = fixture.CreateContext();
        var agent = new AiAgent(workspace, source is { } s ? Settings(AiKnowledgeScope.SELECTED_SOURCES, [s]) : Settings(), Now);
        db.AiAgents.Add(agent);
        await db.SaveChangesAsync(Cancellation);
        return agent;
    }

    private async Task RejectAsync(string sql, string sqlState, string? constraint = null)
    {
        await using var db = fixture.CreateContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Cancellation));
        Assert.Equal(sqlState, error.SqlState);
        if (constraint is not null) Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task Schema_mirrors_domain_rules_and_tenant_keys()
    {
        var (_, ws, source) = await SeedAsync();
        var (_, otherWs, otherSource) = await SeedAsync();
        var agent = await AddAgentAsync(ws, source);
        string Update(string set) => $"UPDATE \"AiAgents\" SET {set} WHERE \"Id\" = '{agent.Id}'";
        foreach (var (set, constraint) in new[]
                 {
                     ("\"ModelTier\" = 'fast'", "CK_AiAgents_ModelTier"), ("\"KnowledgeScope\" = 'SOME'", "CK_AiAgents_KnowledgeScope"),
                     ("\"MaxAiReplies\" = 0", "CK_AiAgents_MaxAiReplies"), ("\"MaxAiReplies\" = 101", "CK_AiAgents_MaxAiReplies"),
                     ("\"UpdatedAt\" = \"CreatedAt\" - interval '1 second'", "CK_AiAgents_Timestamps"),
                     ("\"Name\" = ' '", "CK_AiAgents_Name"), ("\"Name\" = E'a\\u0001'", "CK_AiAgents_Name"),
                     ("\"Instructions\" = E' \\n'", "CK_AiAgents_Instructions"), ("\"Instructions\" = E'a\\u0007'", "CK_AiAgents_Instructions"),
                     ("\"Language\" = 'EN'", "CK_AiAgents_Language"), ("\"Language\" = 'en_US'", "CK_AiAgents_Language"),
                     ("\"Language\" = E'en\\n'", "CK_AiAgents_Language"),
                     ("\"Name\" = U&'Bot\\202E'", "CK_AiAgents_HiddenCharacters"),
                     ("\"Instructions\" = U&'Hi\\+0E0069'", "CK_AiAgents_HiddenCharacters"),
                     ("\"Instructions\" = U&'a\\200Bb'", "CK_AiAgents_HiddenCharacters")
                 })
            await RejectAsync(Update(set), PostgresErrorCodes.CheckViolation, constraint);
        await RejectAsync(Update("\"Instructions\" = repeat('x', 8001)"), PostgresErrorCodes.StringDataRightTruncation);
        // Supplementary characters count as two UTF-16 units, exactly as in .NET.
        await RejectAsync(Update("\"Instructions\" = repeat(U&'\\+01F642', 4001)"), PostgresErrorCodes.CheckViolation, "CK_AiAgents_Instructions");
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlRawAsync(Update("\"Instructions\" = repeat(U&'\\+01F642', 4000), \"Language\" = 'zh-Hant-TW', \"MaxAiReplies\" = NULL"), Cancellation);

        // A selection can reference only a source of the agent's workspace, and only an agent of that workspace.
        await RejectAsync($"INSERT INTO \"AiAgentKnowledgeSources\" (\"WorkspaceId\", \"AiAgentId\", \"KnowledgeSourceId\") VALUES ('{ws}', '{agent.Id}', '{otherSource}')", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"INSERT INTO \"AiAgentKnowledgeSources\" (\"WorkspaceId\", \"AiAgentId\", \"KnowledgeSourceId\") VALUES ('{otherWs}', '{agent.Id}', '{otherSource}')", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"INSERT INTO \"AiAgentKnowledgeSources\" (\"WorkspaceId\", \"AiAgentId\", \"KnowledgeSourceId\") VALUES ('{ws}', '{agent.Id}', '{source}')", PostgresErrorCodes.UniqueViolation);

        // Deleting a source removes it from selections; deleting the workspace removes agents and selections.
        await using var cleanup = fixture.CreateContext();
        await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"KnowledgeSources\" WHERE \"Id\" = {source}", Cancellation);
        Assert.False(await cleanup.AiAgentKnowledgeSources.AnyAsync(s => s.AiAgentId == agent.Id, Cancellation));
        await AddAgentAsync(otherWs, otherSource);
        await cleanup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Workspaces\" WHERE \"Id\" = {otherWs}", Cancellation);
        Assert.False(await cleanup.AiAgents.AnyAsync(a => a.WorkspaceId == otherWs, Cancellation));
        Assert.False(await cleanup.AiAgentKnowledgeSources.AnyAsync(s => s.WorkspaceId == otherWs, Cancellation));
    }

    [Fact]
    public async Task Store_replaces_selections_serializes_writers_and_keeps_the_context_reusable()
    {
        var (owner, ws, s1) = await SeedAsync();
        Guid s2, s3;
        await using (var setup = fixture.CreateContext())
        {
            var extra = new[] { new KnowledgeSource(ws, KnowledgeSourceKind.TEXT, "B", "b", null), new KnowledgeSource(ws, KnowledgeSourceKind.TEXT, "C", "c", null) };
            setup.KnowledgeSources.AddRange(extra);
            await setup.SaveChangesAsync(Cancellation);
            (s2, s3) = (extra[0].Id, extra[1].Id);
        }
        await using var db = fixture.CreateContext();
        var store = new AiAgentStore(db, TimeProvider.System);
        var created = await store.CreateAsync(owner, ws, Settings(AiKnowledgeScope.SELECTED_SOURCES, [s1, s2]), AiAgentService.Writers, Cancellation);
        Assert.Equal(AiAgentStatus.Success, created.Status);
        var id = created.Agent!.Id;

        var updated = await store.UpdateAsync(owner, ws, id, Settings(AiKnowledgeScope.SELECTED_SOURCES, [s2, s3], "Two"), AiAgentService.Writers, Cancellation);
        Assert.Equal(new[] { s2, s3 }.Order(), updated.Agent!.KnowledgeSourceIds);
        // An unknown source is rejected and leaves the stored selection and the reused context intact.
        Assert.Equal(AiAgentStatus.Invalid, (await store.UpdateAsync(owner, ws, id,
            Settings(AiKnowledgeScope.SELECTED_SOURCES, [s1, Guid.NewGuid()]), AiAgentService.Writers, Cancellation)).Status);
        var reread = await store.GetAsync(owner, ws, id, Cancellation);
        Assert.Equal("Two", reread.Agent!.Name);
        Assert.Equal(new[] { s2, s3 }.Order(), reread.Agent.KnowledgeSourceIds);
        Assert.Equal(AiAgentStatus.Success, (await store.UpdateAsync(owner, ws, id, Settings(name: "All"), AiAgentService.Writers, Cancellation)).Status);

        // Concurrent full replacements serialize on the workspace lock: the final state is exactly one request, never a mix.
        var sets = new[] { new[] { s1 }, new[] { s2, s3 }, new[] { s1, s2, s3 }, new[] { s3 } };
        await Task.WhenAll(sets.Select(async (set, i) =>
        {
            await using var context = fixture.CreateContext();
            var result = await new AiAgentStore(context, TimeProvider.System).UpdateAsync(owner, ws, id,
                Settings(AiKnowledgeScope.SELECTED_SOURCES, set, $"Writer {i}"), AiAgentService.Writers, Cancellation);
            Assert.Equal(AiAgentStatus.Success, result.Status);
        }));
        await using var check = fixture.CreateContext();
        var final = (await new AiAgentStore(check, TimeProvider.System).GetAsync(owner, ws, id, Cancellation)).Agent!;
        var writer = int.Parse(final.Name["Writer ".Length..]);
        Assert.Equal(sets[writer].Order(), final.KnowledgeSourceIds);
    }

    [Fact]
    public async Task A_role_downgrade_committed_before_a_write_is_observed()
    {
        var (_, ws, _) = await SeedAsync();
        var admin = new User();
        await using (var setup = fixture.CreateContext())
        {
            setup.Users.Add(admin);
            setup.WorkspaceMembers.Add(new WorkspaceMember(ws, admin.Id, WorkspaceRole.ADMIN));
            await setup.SaveChangesAsync(Cancellation);
        }
        var agent = await AddAgentAsync(ws);

        // The downgrade holds the membership row; the admin's write waits for it and then sees AGENT.
        await using var downgrade = fixture.CreateContext();
        await using var transaction = await downgrade.Database.BeginTransactionAsync(Cancellation);
        await downgrade.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"WorkspaceMembers\" SET \"Role\" = 'AGENT' WHERE \"WorkspaceId\" = {ws} AND \"UserId\" = {admin.Id}", Cancellation);
        var write = Task.Run(async () =>
        {
            await using var context = fixture.CreateContext();
            return await new AiAgentStore(context, TimeProvider.System).UpdateAsync(admin.Id, ws, agent.Id, Settings(name: "Hijack"),
                AiAgentService.Writers, Cancellation);
        }, Cancellation);
        await Task.Delay(500, Cancellation);
        Assert.False(write.IsCompleted);
        await transaction.CommitAsync(Cancellation);
        Assert.Equal(AiAgentStatus.Forbidden, (await write).Status);
        await using var check = fixture.CreateContext();
        Assert.Equal("Bot", (await check.AiAgents.SingleAsync(a => a.Id == agent.Id, Cancellation)).Name);
    }

    [Fact]
    public async Task Reads_authorize_and_bound_rows_in_one_statement_without_instructions_in_lists()
    {
        var (owner, ws, source) = await SeedAsync();
        var (outsider, _, _) = await SeedAsync();
        for (var i = 0; i < 3; i++) await AddAgentAsync(ws, i == 0 ? source : null);
        var capture = new SqlCapture();
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>()
            .UseNpgsql(fixture.ConnectionString).AddInterceptors(capture).Options);
        var store = new AiAgentStore(db, TimeProvider.System);

        var page = await store.ListAsync(owner, ws, null, 2, Cancellation);
        Assert.Equal((2, true), (page.Items!.Count, page.NextAfter is not null));
        var sql = Assert.Single(capture.Commands);
        Assert.Contains("WorkspaceMembers", sql);
        Assert.DoesNotContain("Instructions", sql);
        capture.Commands.Clear();

        var detail = await store.GetAsync(owner, ws, page.Items[0].Id, Cancellation);
        Assert.Equal(AiAgentStatus.Success, detail.Status);
        Assert.Single(capture.Commands);
        capture.Commands.Clear();
        Assert.Equal(AiAgentStatus.NotFound, (await store.ListAsync(outsider, ws, null, 10, Cancellation)).Status);
        Assert.Equal(AiAgentStatus.NotFound, (await store.GetAsync(outsider, ws, page.Items[0].Id, Cancellation)).Status);
        Assert.Equal(2, capture.Commands.Count);
    }

    [Fact]
    public async Task Migration_down_and_up_round_trip_in_an_isolated_database()
    {
        var database = "maic_agents_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await ExecuteAdminAsync($"CREATE DATABASE \"{database}\"");
        try
        {
            await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(connection).Options);
            await db.Database.MigrateAsync(Cancellation);
            var workspace = new Workspace("Isolated");
            var source = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "FAQ", "x", null);
            db.AddRange(workspace, source, new AiAgent(workspace.Id, Settings(AiKnowledgeScope.SELECTED_SOURCES, [source.Id]), Now));
            await db.SaveChangesAsync(Cancellation);
            Assert.Equal(2, await TableCountAsync(db));
            db.ChangeTracker.Clear();

            var previous = (await db.Database.GetAppliedMigrationsAsync(Cancellation)).Single(m => m.EndsWith("_" + PreviousMigration));
            await db.GetService<IMigrator>().MigrateAsync(previous, Cancellation);
            Assert.Equal(0, await TableCountAsync(db));
            Assert.True(await db.KnowledgeSources.AnyAsync(s => s.Id == source.Id, Cancellation));

            await db.Database.MigrateAsync(Cancellation);
            Assert.Equal(2, await TableCountAsync(db));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Cancellation));
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally
        {
            await using (var pooled = new NpgsqlConnection(connection)) NpgsqlConnection.ClearPool(pooled);
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static Task<int> TableCountAsync(MaicDbContext db) => db.Database.SqlQuery<int>($"""
        SELECT count(*)::int AS "Value" FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('AiAgents', 'AiAgentKnowledgeSources')
        """).SingleAsync(Cancellation);

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
