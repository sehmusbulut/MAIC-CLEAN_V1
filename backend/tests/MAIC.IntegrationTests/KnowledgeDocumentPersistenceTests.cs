using System.Data.Common;
using MAIC.Application.Knowledge;
using MAIC.Domain.Identity;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Knowledge;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class KnowledgeDocumentPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IDisposable
{
    private const string PreviousMigration = "20260926213849_KnowledgeSources";
    private static readonly string Sha = new('c', 64);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private readonly string storageRoot = Directory.CreateTempSubdirectory("maic-persistence-knowledge-").FullName;

    public void Dispose() => Directory.Delete(storageRoot, recursive: true);

    private async Task<(Guid User, Guid Workspace, Guid File, Guid Text)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var user = new User();
        var workspace = new Workspace("Document persistence");
        var file = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, "Files", null, null);
        var text = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "Text", "Body", null);
        db.AddRange(user, workspace, new WorkspaceMember(workspace.Id, user.Id, WorkspaceRole.OWNER), file, text);
        await db.SaveChangesAsync(Cancellation);
        return (user.Id, workspace.Id, file.Id, text.Id);
    }

    private async Task<(KnowledgeDocument Document, IngestionJob Job)> AddUploadAsync(Guid user, Guid workspace, Guid source)
    {
        await using var db = fixture.CreateContext();
        var document = KnowledgeDocument.ForUpload(workspace, source, user, Guid.NewGuid(), "a.txt", "text/plain", 1, Sha);
        var job = new IngestionJob(workspace, document.Id, DateTimeOffset.UtcNow);
        db.AddRange(document, job);
        await db.SaveChangesAsync(Cancellation);
        return (document, job);
    }

    private async Task SetAsync(string table, Guid id, string assignments)
    {
        await using var db = fixture.CreateContext();
        // Test-controlled literals only; the statement shapes are the point of these constraint tests.
        var sql = $"UPDATE \"{table}\" SET {assignments} WHERE \"Id\" = '{id}'";
        await db.Database.ExecuteSqlRawAsync(sql, Cancellation);
    }

    private static async Task RejectAsync(Func<Task> action, string sqlState, string? constraint = null)
    {
        var thrown = await Assert.ThrowsAnyAsync<Exception>(action);
        var error = Assert.IsType<PostgresException>(thrown is DbUpdateException update ? update.InnerException : thrown);
        Assert.Equal(sqlState, error.SqlState);
        if (constraint is not null) Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task Schema_enforces_document_lifecycle_origin_shape_and_file_names()
    {
        var (user, ws, file, _) = await SeedAsync();
        var (document, _) = await AddUploadAsync(user, ws, file);
        const string lifecycle = "CK_KnowledgeDocuments_Lifecycle";
        foreach (var invalid in new[]
                 {
                     "\"Status\" = 'READY'", "\"Status\" = 'READY', \"ProcessedAt\" = now(), \"CharacterCount\" = 1",
                     "\"Status\" = 'FAILED', \"ProcessedAt\" = now()", "\"FailureReason\" = 'FETCH_FAILED'",
                     "\"Status\" = 'SUPERSEDED', \"ProcessedAt\" = now()", "\"ChunkCount\" = 1", "\"ProcessedAt\" = now()"
                 })
            await RejectAsync(() => SetAsync("KnowledgeDocuments", document.Id, invalid), PostgresErrorCodes.CheckViolation, lifecycle);
        await SetAsync("KnowledgeDocuments", document.Id, "\"Status\" = 'READY', \"ProcessedAt\" = now(), \"ChunkCount\" = 1, \"CharacterCount\" = 1");
        await SetAsync("KnowledgeDocuments", document.Id, "\"Status\" = 'FAILED', \"FailureReason\" = 'INVALID_CONTENT', \"ChunkCount\" = 0, \"CharacterCount\" = 0");

        const string shape = "CK_KnowledgeDocuments_OriginShape";
        foreach (var invalid in new[]
                 {
                     "\"FileName\" = NULL", "\"ContentType\" = 'text/html'", "\"SizeBytes\" = 0", "\"SizeBytes\" = 10485761",
                     "\"ContentSha256\" = NULL", "\"SourceUrl\" = 'https://example.com'", "\"Origin\" = 'TEXT'"
                 })
            await RejectAsync(() => SetAsync("KnowledgeDocuments", document.Id, invalid), PostgresErrorCodes.CheckViolation, shape);
        await RejectAsync(() => SetAsync("KnowledgeDocuments", document.Id, $"\"ContentSha256\" = '{new string('C', 64)}'"),
            PostgresErrorCodes.CheckViolation, "CK_KnowledgeDocuments_Content");
        await RejectAsync(() => SetAsync("KnowledgeDocuments", document.Id, "\"FailureReason\" = 'OTHER'"),
            PostgresErrorCodes.CheckViolation, "CK_KnowledgeDocuments_FailureReason");
        await RejectAsync(() => SetAsync("KnowledgeDocuments", document.Id, "\"ClientRequestId\" = '00000000-0000-0000-0000-000000000000'"),
            PostgresErrorCodes.CheckViolation, "CK_KnowledgeDocuments_RequestKey");

        // The file-name check mirrors KnowledgeDocument.IsValidFileName in both directions.
        foreach (var (name, valid) in new (string, bool)[]
                 {
                     ("guide.pdf", true), ("rapor çıktı \ud83d\ude42.md", true), ("a b.txt", true), (new string('x', 255), true),
                     (new string('x', 254) + "\ud83d\ude42", false), ("a/b", false), ("a\\b", false), (" a", false), ("a ", false),
                     ("a ", false), (".", false), ("..", false), ("a\u0085b", false), ("a\tb", false)
                 })
        {
            Assert.Equal(valid, KnowledgeDocument.IsValidFileName(name));
            await using var db = fixture.CreateContext();
            var update = () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"KnowledgeDocuments\" SET \"FileName\" = {name} WHERE \"Id\" = {document.Id}", Cancellation);
            if (valid) await update();
            else await Assert.ThrowsAsync<PostgresException>(update);
        }
    }

    [Fact]
    public async Task Schema_enforces_jobs_chunks_uniqueness_and_tenant_composite_keys()
    {
        var (user, ws, file, text) = await SeedAsync();
        var (_, otherWs, otherFile, _) = await SeedAsync();
        var (document, job) = await AddUploadAsync(user, ws, file);

        const string jobLifecycle = "CK_IngestionJobs_Lifecycle";
        foreach (var invalid in new[]
                 {
                     "\"Status\" = 'RUNNING'", "\"Status\" = 'RUNNING', \"LeaseToken\" = gen_random_uuid()",
                     "\"Status\" = 'SUCCEEDED', \"CompletedAt\" = now()", "\"Status\" = 'FAILED', \"CompletedAt\" = now(), \"Attempts\" = 1",
                     "\"Status\" = 'CANCELLED', \"CompletedAt\" = now()", "\"LeaseToken\" = gen_random_uuid()", "\"CompletedAt\" = now()"
                 })
            await RejectAsync(() => SetAsync("IngestionJobs", job.Id, invalid), PostgresErrorCodes.CheckViolation, jobLifecycle);
        await RejectAsync(() => SetAsync("IngestionJobs", job.Id, "\"Attempts\" = 6"), PostgresErrorCodes.CheckViolation, "CK_IngestionJobs_Attempts");
        await RejectAsync(() => SetAsync("IngestionJobs", job.Id, "\"MaxAttempts\" = 0"), PostgresErrorCodes.CheckViolation, "CK_IngestionJobs_Attempts");

        await using (var db = fixture.CreateContext())
        {
            db.IngestionJobs.Add(new IngestionJob(ws, document.Id, DateTimeOffset.UtcNow));
            await RejectAsync(() => db.SaveChangesAsync(Cancellation), PostgresErrorCodes.UniqueViolation, "IX_IngestionJobs_ActiveDocument");
        }
        await using (var db = fixture.CreateContext())
        {
            db.KnowledgeDocuments.AddRange(KnowledgeDocument.ForSnapshot(ws, text, KnowledgeSourceKind.TEXT, user, Guid.NewGuid()),
                KnowledgeDocument.ForSnapshot(ws, text, KnowledgeSourceKind.TEXT, user, Guid.NewGuid()));
            await RejectAsync(() => db.SaveChangesAsync(Cancellation), PostgresErrorCodes.UniqueViolation, "IX_KnowledgeDocuments_PendingSnapshot");
        }
        await using (var db = fixture.CreateContext())
        {
            // A document cannot point at another workspace's source, and a chunk cannot point at another workspace's document.
            db.KnowledgeDocuments.Add(KnowledgeDocument.ForUpload(ws, otherFile, user, Guid.NewGuid(), "x.txt", "text/plain", 1, Sha));
            await RejectAsync(() => db.SaveChangesAsync(Cancellation), PostgresErrorCodes.ForeignKeyViolation);
        }
        await using (var db = fixture.CreateContext())
        {
            db.KnowledgeDocumentChunks.Add(new KnowledgeDocumentChunk(otherWs, document.Id, 1, 0, "x"));
            await RejectAsync(() => db.SaveChangesAsync(Cancellation), PostgresErrorCodes.ForeignKeyViolation);
        }
        await using (var db = fixture.CreateContext())
        {
            db.KnowledgeDocumentChunks.AddRange(new KnowledgeDocumentChunk(ws, document.Id, 1, 0, "x"), new KnowledgeDocumentChunk(ws, document.Id, 1, 5, "y"));
            await RejectAsync(() => db.SaveChangesAsync(Cancellation), PostgresErrorCodes.UniqueViolation);
        }
        await using (var db = fixture.CreateContext())
        {
            var chunk = new KnowledgeDocumentChunk(ws, document.Id, 1, 0, "x");
            db.KnowledgeDocumentChunks.Add(chunk);
            await db.SaveChangesAsync(Cancellation);
            await RejectAsync(() => SetAsync("KnowledgeDocumentChunks", chunk.Id, "\"Content\" = ' '"), PostgresErrorCodes.CheckViolation, "CK_KnowledgeDocumentChunks_Content");
            await RejectAsync(() => SetAsync("KnowledgeDocumentChunks", chunk.Id, "\"Sequence\" = 0"), PostgresErrorCodes.CheckViolation, "CK_KnowledgeDocumentChunks_Position");
            await RejectAsync(() => SetAsync("KnowledgeDocumentChunks", chunk.Id, $"\"Content\" = repeat('\ud83d\ude42', 1001)"),
                PostgresErrorCodes.CheckViolation, "CK_KnowledgeDocumentChunks_Content");

            // Deleting the workspace removes sources, documents, jobs and chunks.
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Workspaces\" WHERE \"Id\" = {ws}", Cancellation);
            Assert.False(await db.KnowledgeDocuments.AnyAsync(d => d.WorkspaceId == ws, Cancellation));
            Assert.False(await db.IngestionJobs.AnyAsync(j => j.WorkspaceId == ws, Cancellation));
            Assert.False(await db.KnowledgeDocumentChunks.AnyAsync(c => c.WorkspaceId == ws, Cancellation));
        }
    }

    [Fact]
    public async Task Reads_are_authorized_and_bounded_in_one_statement()
    {
        var (user, ws, file, _) = await SeedAsync();
        var (outsider, _, _, _) = await SeedAsync();
        var documents = new List<Guid>();
        for (var i = 0; i < 3; i++) documents.Add((await AddUploadAsync(user, ws, file)).Document.Id);
        documents.Sort();
        await using (var setup = fixture.CreateContext())
        {
            setup.KnowledgeDocumentChunks.AddRange(Enumerable.Range(1, 3).Select(i => new KnowledgeDocumentChunk(ws, documents[0], i, i * 10, $"Chunk {i}")));
            await setup.SaveChangesAsync(Cancellation);
        }
        var capture = new SqlCapture();
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>()
            .UseNpgsql(fixture.ConnectionString).AddInterceptors(capture).Options);
        var store = new KnowledgeDocumentStore(db, new LocalKnowledgeFileStorage(storageRoot), TimeProvider.System, new KnowledgeLimits());

        var page = await store.ListAsync(user, ws, file, null, 2, Cancellation);
        Assert.Equal(documents[..2], page.Items!.Select(d => d.Id));
        Assert.Equal(documents[1], page.NextAfter);
        Assert.All(page.Items!, d => Assert.Equal("QUEUED", d.LatestJob!.Status));
        var (sql, _) = Assert.Single(capture.Commands);
        Assert.Contains("WorkspaceMembers", sql);
        Assert.Contains("IngestionJobs", sql);
        capture.Commands.Clear();

        var chunks = await store.ListChunksAsync(user, ws, file, documents[0], 1, 1, Cancellation);
        Assert.Equal((2, 20, "Chunk 2", 2), (Assert.Single(chunks.Chunks!).Sequence, chunks.Chunks![0].StartOffset, chunks.Chunks[0].Content,
            chunks.NextAfterSequence!.Value));
        Assert.Single(capture.Commands);
        capture.Commands.Clear();

        foreach (var result in new[]
                 {
                     await store.ListAsync(outsider, ws, file, null, 10, Cancellation),
                     await store.GetAsync(outsider, ws, file, documents[0], Cancellation),
                     await store.ListChunksAsync(outsider, ws, file, documents[0], 0, 10, Cancellation)
                 })
            Assert.Equal(KnowledgeDocumentStatusCode.NotFound, result.Status);
        Assert.Equal(3, capture.Commands.Count);
        var empty = await store.ListAsync(user, ws, file, documents[2], 10, Cancellation);
        Assert.Equal((KnowledgeDocumentStatusCode.Success, 0), (empty.Status, empty.Items!.Count));
    }

    [Fact]
    public async Task Migration_down_and_up_round_trip_in_an_isolated_database()
    {
        var database = "maic_ingestion_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await ExecuteAdminAsync($"CREATE DATABASE \"{database}\"");
        try
        {
            await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(connection).Options);
            await db.Database.MigrateAsync(Cancellation);
            var user = new User();
            var workspace = new Workspace("Isolated");
            var source = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, "Files", null, null);
            var document = KnowledgeDocument.ForUpload(workspace.Id, source.Id, user.Id, Guid.NewGuid(), "a.txt", "text/plain", 1, Sha);
            db.AddRange(user, workspace, source, document, new IngestionJob(workspace.Id, document.Id, DateTimeOffset.UtcNow),
                new KnowledgeDocumentChunk(workspace.Id, document.Id, 1, 0, "a"));
            await db.SaveChangesAsync(Cancellation);
            Assert.Equal(3, await TableCountAsync(db));
            db.ChangeTracker.Clear();

            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration, Cancellation);
            Assert.Equal(0, await TableCountAsync(db));
            Assert.True(await db.KnowledgeSources.AnyAsync(s => s.Id == source.Id, Cancellation));

            await db.Database.MigrateAsync(Cancellation);
            Assert.Equal(3, await TableCountAsync(db));
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
        SELECT count(*)::int AS "Value" FROM pg_tables WHERE schemaname = 'public'
            AND tablename IN ('KnowledgeDocuments', 'KnowledgeDocumentChunks', 'IngestionJobs')
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
        public List<(string Sql, Dictionary<string, object?> Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName, p => (object?)p.Value)));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
