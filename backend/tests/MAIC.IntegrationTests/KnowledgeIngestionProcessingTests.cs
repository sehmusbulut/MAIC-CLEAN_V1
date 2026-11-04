using System.Text;
using MAIC.Application.Knowledge;
using MAIC.Domain.Identity;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Knowledge;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>Worker-side ingestion lifecycle against real PostgreSQL: claims, leases, retries, supersession and failures.</summary>
public sealed class KnowledgeIngestionProcessingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IDisposable
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private readonly string storageRoot = Directory.CreateTempSubdirectory("maic-knowledge-").FullName;
    private readonly TestClock clock = new(DateTimeOffset.UtcNow);
    // Fair-scheduling caps are covered separately; these lifecycle tests claim many jobs of one workspace.
    private static readonly KnowledgeLimits Limits = new() { MaxRunningJobsPerWorkspace = 100 };

    public void Dispose() => Directory.Delete(storageRoot, recursive: true);

    private LocalKnowledgeFileStorage Storage() => new(storageRoot);

    private KnowledgeDocumentStore DocumentStore(MaicDbContext db) => new(db, Storage(), clock, Limits);

    private IngestionProcessor Processor(MaicDbContext db, IWebPageFetcher? fetcher = null) =>
        new(new IngestionJobStore(db, Limits), Storage(), fetcher ?? new SafeWebPageFetcher(), new DocumentTextExtractor(), clock);

    private async Task<(Guid UserId, Guid WorkspaceId)> WorkspaceAsync()
    {
        await using var db = fixture.CreateContext();
        var user = new User();
        var workspace = new Workspace("Ingestion tests");
        db.Users.Add(user);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, user.Id, WorkspaceRole.OWNER));
        await db.SaveChangesAsync(Cancellation);
        return (user.Id, workspace.Id);
    }

    private async Task<Guid> SourceAsync(Guid workspaceId, KnowledgeSourceKind kind, string? text = null, string? url = null)
    {
        await using var db = fixture.CreateContext();
        var source = new KnowledgeSource(workspaceId, kind, $"{kind} source", text, url);
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(Cancellation);
        return source.Id;
    }

    /// <summary>Claims are global. Cancel every other active job so a test only processes its own work.</summary>
    private async Task IsolateAsync(params Guid[] documentIds)
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IngestionJobs" SET "Status" = 'CANCELLED', "LeaseToken" = NULL, "LastError" = 'SOURCE_DISABLED',
                "CompletedAt" = now()
            WHERE "Status" IN ('QUEUED', 'RUNNING') AND NOT ("DocumentId" = ANY({documentIds}))
            """, Cancellation);
    }

    private async Task<(KnowledgeDocument Document, IngestionJob[] Jobs, KnowledgeDocumentChunk[] Chunks)> LoadAsync(Guid documentId)
    {
        await using var db = fixture.CreateContext();
        var document = await db.KnowledgeDocuments.AsNoTracking().SingleAsync(d => d.Id == documentId, Cancellation);
        var jobs = await db.IngestionJobs.AsNoTracking().Where(j => j.DocumentId == documentId).OrderBy(j => j.Id).ToArrayAsync(Cancellation);
        var chunks = await db.KnowledgeDocumentChunks.AsNoTracking().Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.Sequence).ToArrayAsync(Cancellation);
        return (document, jobs, chunks);
    }

    private async Task<KnowledgeDocumentView> RequestAsync(Guid userId, Guid workspaceId, Guid sourceId)
    {
        await using var db = fixture.CreateContext();
        var result = await DocumentStore(db).RequestIngestionAsync(userId, workspaceId, sourceId, Guid.NewGuid(), Cancellation);
        Assert.Equal(KnowledgeDocumentStatusCode.Success, result.Status);
        return result.Document!;
    }

    private async Task<KnowledgeDocumentView> UploadAsync(Guid userId, Guid workspaceId, Guid sourceId, string fileName,
        string contentType, byte[] content)
    {
        await using var db = fixture.CreateContext();
        var upload = new KnowledgeUpload(fileName, contentType, content,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)));
        var result = await DocumentStore(db).UploadAsync(userId, workspaceId, sourceId, Guid.NewGuid(), upload, Cancellation);
        Assert.Equal(KnowledgeDocumentStatusCode.Success, result.Status);
        return result.Document!;
    }

    private async Task<IngestionOutcome?> ProcessAsync(IWebPageFetcher? fetcher = null)
    {
        await using var db = fixture.CreateContext();
        return await Processor(db, fetcher).ProcessNextAsync(Cancellation);
    }

    [Fact]
    public async Task Text_snapshots_become_ready_and_a_newer_ready_snapshot_supersedes_the_older_one()
    {
        var (user, ws) = await WorkspaceAsync();
        var text = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Policy {i}: " + string.Join(' ', Enumerable.Repeat("refund", 30))));
        var source = await SourceAsync(ws, KnowledgeSourceKind.TEXT, text);
        var first = await RequestAsync(user, ws, source);
        Assert.Equal(("TEXT", "PENDING", "QUEUED", 0), (first.Origin, first.Status, first.LatestJob!.Status, first.LatestJob.Attempts));
        await using (var db = fixture.CreateContext())
            Assert.Equal(KnowledgeDocumentStatusCode.Conflict,
                (await DocumentStore(db).RequestIngestionAsync(user, ws, source, Guid.NewGuid(), Cancellation)).Status);
        await IsolateAsync(first.Id);

        var outcome = await ProcessAsync();
        Assert.Equal((first.Id, IngestionOutcomeKind.Ready, 1), (outcome!.DocumentId, outcome.Kind, outcome.Attempt));
        var (document, jobs, chunks) = await LoadAsync(first.Id);
        var expected = KnowledgeChunker.Split(KnowledgeChunker.Normalize(text));
        Assert.Equal((KnowledgeDocumentStatus.READY, expected.Count, "text/plain"), (document.Status, document.ChunkCount, document.ContentType));
        Assert.Equal(Encoding.UTF8.GetByteCount(text), document.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))), document.ContentSha256);
        Assert.Equal(expected.Select(c => (c.Sequence, c.StartOffset, c.Content)), chunks.Select(c => (c.Sequence, c.StartOffset, c.Content)));
        Assert.All(chunks, c => Assert.Equal(ws, c.WorkspaceId));
        Assert.Equal((IngestionJobStatus.SUCCEEDED, 1), (Assert.Single(jobs).Status, jobs[0].Attempts));
        Assert.Null(await ProcessAsync());

        // The worker reads the source's current text when it processes the next snapshot.
        await using (var db = fixture.CreateContext())
        {
            var entity = await db.KnowledgeSources.SingleAsync(s => s.Id == source, Cancellation);
            entity.Update(null, null, "Updated handbook text.", null);
            await db.SaveChangesAsync(Cancellation);
        }
        var second = await RequestAsync(user, ws, source);
        Assert.Equal(IngestionOutcomeKind.Ready, (await ProcessAsync())!.Kind);
        var old = await LoadAsync(first.Id);
        Assert.Equal(KnowledgeDocumentStatus.SUPERSEDED, old.Document.Status);
        Assert.Empty(old.Chunks);
        var current = await LoadAsync(second.Id);
        Assert.Equal("Updated handbook text.", Assert.Single(current.Chunks).Content);
    }

    [Fact]
    public async Task Uploaded_pdf_markdown_and_text_files_are_extracted_and_chunked()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var pdf = await UploadAsync(user, ws, source, "guide.pdf", "application/pdf", KnowledgeTestData.Pdf("Shipping takes three days"));
        var md = await UploadAsync(user, ws, source, "faq.md", "text/markdown", "# FAQ\r\n\r\nReturns are free."u8.ToArray());
        Assert.True(File.Exists(Path.Combine(storageRoot, ws.ToString("N"), pdf.Id.ToString("N"))));
        await IsolateAsync(pdf.Id, md.Id);

        Assert.Equal(IngestionOutcomeKind.Ready, (await ProcessAsync())!.Kind);
        Assert.Equal(IngestionOutcomeKind.Ready, (await ProcessAsync())!.Kind);
        var pdfState = await LoadAsync(pdf.Id);
        Assert.Contains("Shipping takes three days", Assert.Single(pdfState.Chunks).Content);
        Assert.Equal(("guide.pdf", "application/pdf", KnowledgeDocumentStatus.READY),
            (pdfState.Document.FileName, pdfState.Document.ContentType, pdfState.Document.Status));
        Assert.Equal("# FAQ\n\nReturns are free.", Assert.Single((await LoadAsync(md.Id)).Chunks).Content);
    }

    [Fact]
    public async Task Invalid_empty_and_missing_uploads_fail_permanently_and_can_be_retried()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var corrupt = await UploadAsync(user, ws, source, "bad.pdf", "application/pdf", "%PDF-1.7 not really"u8.ToArray());
        var blank = await UploadAsync(user, ws, source, "blank.txt", "text/plain", " \n\t "u8.ToArray());
        var missing = await UploadAsync(user, ws, source, "gone.txt", "text/plain", "content"u8.ToArray());
        File.Delete(Path.Combine(storageRoot, ws.ToString("N"), missing.Id.ToString("N")));
        await IsolateAsync(corrupt.Id, blank.Id, missing.Id);

        var outcomes = new List<IngestionOutcome>();
        while (await ProcessAsync() is { } outcome) outcomes.Add(outcome);
        Assert.Equal([(corrupt.Id, IngestionFailure.INVALID_CONTENT), (blank.Id, IngestionFailure.EMPTY_CONTENT),
            (missing.Id, IngestionFailure.STORAGE_MISSING)], outcomes.Select(o => (o.DocumentId, o.Failure!.Value)));
        Assert.All(outcomes, o => Assert.Equal((IngestionOutcomeKind.Failed, 1), (o.Kind, o.Attempt)));
        var failed = await LoadAsync(corrupt.Id);
        Assert.Equal((KnowledgeDocumentStatus.FAILED, IngestionFailure.INVALID_CONTENT), (failed.Document.Status, failed.Document.FailureReason));
        Assert.Equal((IngestionJobStatus.FAILED, IngestionFailure.INVALID_CONTENT), (failed.Jobs[0].Status, failed.Jobs[0].LastError));

        // Retrying creates a new job; the missing file fails again without touching other documents.
        await using (var db = fixture.CreateContext())
        {
            var retried = await DocumentStore(db).RetryAsync(user, ws, source, missing.Id, Cancellation);
            Assert.Equal(("PENDING", "QUEUED", (string?)null), (retried.Document!.Status, retried.Document.LatestJob!.Status, retried.Document.FailureReason));
            Assert.Equal(KnowledgeDocumentStatusCode.Conflict, (await DocumentStore(db).RetryAsync(user, ws, source, missing.Id, Cancellation)).Status);
        }
        Assert.Equal(IngestionFailure.STORAGE_MISSING, (await ProcessAsync())!.Failure);
        Assert.Equal(2, (await LoadAsync(missing.Id)).Jobs.Length);
    }

    [Fact]
    public async Task Disabled_sources_are_skipped_and_become_retryable_after_enabling()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Disabled content");
        var document = await RequestAsync(user, ws, source);
        await IsolateAsync(document.Id);
        await SetEnabledAsync(source, false);

        var outcome = await ProcessAsync();
        Assert.Equal((IngestionOutcomeKind.Cancelled, IngestionFailure.SOURCE_DISABLED), (outcome!.Kind, outcome.Failure));
        var state = await LoadAsync(document.Id);
        Assert.Equal((KnowledgeDocumentStatus.FAILED, IngestionFailure.SOURCE_DISABLED), (state.Document.Status, state.Document.FailureReason));
        Assert.Equal(IngestionJobStatus.CANCELLED, Assert.Single(state.Jobs).Status);
        Assert.Empty(state.Chunks);

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(KnowledgeDocumentStatusCode.Conflict, (await DocumentStore(db).RetryAsync(user, ws, source, document.Id, Cancellation)).Status);
            Assert.Equal(KnowledgeDocumentStatusCode.Conflict,
                (await DocumentStore(db).RequestIngestionAsync(user, ws, source, Guid.NewGuid(), Cancellation)).Status);
        }
        await SetEnabledAsync(source, true);
        await using (var db = fixture.CreateContext())
            Assert.Equal(KnowledgeDocumentStatusCode.Success, (await DocumentStore(db).RetryAsync(user, ws, source, document.Id, Cancellation)).Status);
        Assert.Equal(IngestionOutcomeKind.Ready, (await ProcessAsync())!.Kind);
        Assert.Equal(KnowledgeDocumentStatus.READY, (await LoadAsync(document.Id)).Document.Status);
    }

    [Fact]
    public async Task Disabling_during_an_attempt_cancels_instead_of_publishing_chunks()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Racing content");
        var document = await RequestAsync(user, ws, source);
        await IsolateAsync(document.Id);
        await using var db = fixture.CreateContext();
        var store = new IngestionJobStore(db, Limits);
        var work = (await store.ClaimNextAsync(clock.Now, TimeSpan.FromMinutes(5), Cancellation))!;
        Assert.True(work.SourceEnabled);
        await SetEnabledAsync(source, false);

        var content = new IngestionContent("text/plain", 1, new string('a', 64), null, 14, [new(1, 0, "Racing content")]);
        Assert.Equal(IngestionOutcomeKind.Cancelled, await store.CompleteAsync(work, content, clock.Now, Cancellation));
        var state = await LoadAsync(document.Id);
        Assert.Equal((KnowledgeDocumentStatus.FAILED, IngestionFailure.SOURCE_DISABLED, IngestionJobStatus.CANCELLED),
            (state.Document.Status, state.Document.FailureReason, state.Jobs[0].Status));
        Assert.Empty(state.Chunks);
    }

    private async Task SetEnabledAsync(Guid sourceId, bool enabled)
    {
        await using var db = fixture.CreateContext();
        (await db.KnowledgeSources.SingleAsync(s => s.Id == sourceId, Cancellation)).SetEnabled(enabled);
        await db.SaveChangesAsync(Cancellation);
    }

    [Fact]
    public async Task Websites_are_fetched_safely_and_transient_failures_back_off_until_attempts_are_exhausted()
    {
        var (user, ws) = await WorkspaceAsync();
        var failures = 0;
        await using var server = await TestWebServer.StartAsync(app =>
        {
            app.MapGet("/start", () => Results.Redirect("/article"));
            app.MapGet("/article", () => Results.Text("<html><body><h1>Guide</h1><script>tracking()</script><p>Open 9-5.</p></body></html>",
                "text/html; charset=utf-8"));
            app.MapGet("/down", () => { Interlocked.Increment(ref failures); return Results.StatusCode(503); });
        });
        using var fetcher = new SafeWebPageFetcher(KnowledgeTestData.LoopbackOptions());

        var site = await SourceAsync(ws, KnowledgeSourceKind.WEBSITE, url: server.BaseUrl + "/start");
        var document = await RequestAsync(user, ws, site);
        await IsolateAsync(document.Id);
        Assert.Equal(IngestionOutcomeKind.Ready, (await ProcessAsync(fetcher))!.Kind);
        var state = await LoadAsync(document.Id);
        Assert.Equal((server.BaseUrl + "/article", "text/html"), (state.Document.SourceUrl, state.Document.ContentType));
        Assert.Equal("Guide\n\nOpen 9-5.", Assert.Single(state.Chunks).Content);

        // The production fetcher refuses the same loopback URL outright.
        var blockedSource = await SourceAsync(ws, KnowledgeSourceKind.WEBSITE, url: server.BaseUrl + "/article");
        var blocked = await RequestAsync(user, ws, blockedSource);
        await IsolateAsync(blocked.Id);
        var blockedOutcome = await ProcessAsync();
        Assert.Equal((IngestionOutcomeKind.Failed, IngestionFailure.FETCH_BLOCKED, 1), (blockedOutcome!.Kind, blockedOutcome.Failure, blockedOutcome.Attempt));

        var down = await SourceAsync(ws, KnowledgeSourceKind.WEBSITE, url: server.BaseUrl + "/down");
        var retried = await RequestAsync(user, ws, down);
        await IsolateAsync(retried.Id);
        var backoff = TimeSpan.Zero;
        for (var attempt = 1; attempt <= IngestionJob.DefaultMaxAttempts; attempt++)
        {
            clock.Now += backoff;
            var outcome = await ProcessAsync(fetcher);
            Assert.Equal((attempt, IngestionFailure.FETCH_FAILED), (outcome!.Attempt, outcome.Failure));
            var job = (await LoadAsync(retried.Id)).Jobs.Single();
            if (attempt < IngestionJob.DefaultMaxAttempts)
            {
                backoff = IngestionJob.Backoff(attempt);
                Assert.Equal((IngestionOutcomeKind.Retrying, IngestionJobStatus.QUEUED), (outcome.Kind, job.Status));
                Assert.Equal(KnowledgeClock.Truncate(clock.Now + backoff), job.NextAttemptAt);
                Assert.Equal(KnowledgeDocumentStatus.PENDING, (await LoadAsync(retried.Id)).Document.Status);
                clock.Now += backoff - TimeSpan.FromSeconds(1);
                Assert.Null(await ProcessAsync(fetcher)); // not due yet
                backoff = TimeSpan.FromSeconds(1);
            }
            else Assert.Equal((IngestionOutcomeKind.Failed, IngestionJobStatus.FAILED), (outcome.Kind, job.Status));
        }
        Assert.Equal(IngestionJob.DefaultMaxAttempts, failures);
        var final = await LoadAsync(retried.Id);
        Assert.Equal((KnowledgeDocumentStatus.FAILED, IngestionFailure.FETCH_FAILED), (final.Document.Status, final.Document.FailureReason));
    }

    [Fact]
    public async Task Expired_leases_are_reclaimed_stale_workers_cannot_finish_and_exhausted_attempts_fail()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Lease content");
        var document = await RequestAsync(user, ws, source);
        await IsolateAsync(document.Id);
        var lease = TimeSpan.FromMinutes(1);
        var content = new IngestionContent("text/plain", 13, new string('b', 64), null, 13, [new(1, 0, "Lease content")]);

        await using var db = fixture.CreateContext();
        var store = new IngestionJobStore(db, Limits);
        var first = (await store.ClaimNextAsync(clock.Now, lease, Cancellation))!;
        Assert.Null(await store.ClaimNextAsync(clock.Now.AddSeconds(59), lease, Cancellation));
        var second = (await store.ClaimNextAsync(clock.Now.AddMinutes(1), lease, Cancellation))!;
        Assert.Equal((first.JobId, 2), (second.JobId, second.Attempt));
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);

        Assert.Equal(IngestionOutcomeKind.LeaseLost, await store.CompleteAsync(first, content, clock.Now, Cancellation));
        Assert.False(await store.ReleaseAsync(first, clock.Now, Cancellation));
        Assert.Equal(IngestionOutcomeKind.LeaseLost, await store.FailAsync(first, IngestionFailure.FETCH_FAILED, true, clock.Now, Cancellation));
        var untouched = await LoadAsync(document.Id);
        Assert.Equal((KnowledgeDocumentStatus.PENDING, IngestionJobStatus.RUNNING, 2), (untouched.Document.Status, untouched.Jobs[0].Status, untouched.Jobs[0].Attempts));

        // Keep crashing until the final attempt's lease expires; the next claim fails the job instead of running it again.
        var at = clock.Now.AddMinutes(1);
        for (var attempt = 3; attempt <= IngestionJob.DefaultMaxAttempts; attempt++)
        {
            at = at.AddMinutes(1);
            Assert.Equal(attempt, (await store.ClaimNextAsync(at, lease, Cancellation))!.Attempt);
        }
        Assert.Null(await store.ClaimNextAsync(at.AddMinutes(1), lease, Cancellation));
        var abandoned = await LoadAsync(document.Id);
        Assert.Equal((KnowledgeDocumentStatus.FAILED, IngestionFailure.LEASE_EXPIRED), (abandoned.Document.Status, abandoned.Document.FailureReason));
        Assert.Equal((IngestionJobStatus.FAILED, IngestionFailure.LEASE_EXPIRED), (abandoned.Jobs[0].Status, abandoned.Jobs[0].LastError));
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_job()
    {
        var (user, ws) = await WorkspaceAsync();
        var documents = new List<Guid>();
        for (var i = 0; i < 6; i++) documents.Add((await RequestAsync(user, ws, await SourceAsync(ws, KnowledgeSourceKind.TEXT, $"Text {i}"))).Id);
        await IsolateAsync([.. documents]);

        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            return await new IngestionJobStore(db, Limits).ClaimNextAsync(clock.Now, TimeSpan.FromMinutes(5), Cancellation);
        }));
        Assert.All(claims, Assert.NotNull);
        Assert.Equal(documents.Order(), claims.Select(c => c!.DocumentId).Order());
        await using var check = fixture.CreateContext();
        Assert.Null(await new IngestionJobStore(check, Limits).ClaimNextAsync(clock.Now, TimeSpan.FromMinutes(5), Cancellation));
    }

    [Fact]
    public async Task Concurrent_snapshot_requests_enqueue_exactly_one_pending_document()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Concurrent");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            return (await DocumentStore(db).RequestIngestionAsync(user, ws, source, Guid.NewGuid(), Cancellation)).Status;
        }));
        Assert.Equal(1, results.Count(s => s == KnowledgeDocumentStatusCode.Success));
        Assert.Equal(7, results.Count(s => s == KnowledgeDocumentStatusCode.Conflict));
        await using var check = fixture.CreateContext();
        Assert.Equal(1, await check.KnowledgeDocuments.CountAsync(d => d.KnowledgeSourceId == source, Cancellation));
    }

    [Fact]
    public async Task An_older_snapshot_finishing_after_a_newer_ready_one_is_superseded_without_chunks()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Old text");
        var older = await RequestAsync(user, ws, source);
        await IsolateAsync(older.Id);
        await SetEnabledAsync(source, false);
        Assert.Equal(IngestionOutcomeKind.Cancelled, (await ProcessAsync())!.Kind);
        await SetEnabledAsync(source, true);

        var newer = await RequestAsync(user, ws, source);
        var newerOutcome = await ProcessAsync();
        Assert.Equal((newer.Id, IngestionOutcomeKind.Ready), (newerOutcome!.DocumentId, newerOutcome.Kind));
        await using (var db = fixture.CreateContext())
            Assert.Equal(KnowledgeDocumentStatusCode.Success, (await DocumentStore(db).RetryAsync(user, ws, source, older.Id, Cancellation)).Status);
        Assert.Equal(IngestionOutcomeKind.Ready, (await ProcessAsync())!.Kind);

        var stale = await LoadAsync(older.Id);
        Assert.Equal(KnowledgeDocumentStatus.SUPERSEDED, stale.Document.Status);
        Assert.Empty(stale.Chunks);
        Assert.Equal(KnowledgeDocumentStatus.READY, (await LoadAsync(newer.Id)).Document.Status);
        Assert.Single((await LoadAsync(newer.Id)).Chunks);
    }

    [Fact]
    public async Task Shutdown_releases_the_attempt_and_persistence_errors_are_retried_as_internal_errors()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.WEBSITE, url: "https://example.com/page");
        var document = await RequestAsync(user, ws, source);
        await IsolateAsync(document.Id);

        using var shutdown = new CancellationTokenSource();
        await using (var db = fixture.CreateContext())
        {
            var processor = Processor(db, new CallbackFetcher(_ => { shutdown.Cancel(); shutdown.Token.ThrowIfCancellationRequested(); }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessNextAsync(shutdown.Token));
        }
        var released = (await LoadAsync(document.Id)).Jobs.Single();
        Assert.Equal((IngestionJobStatus.QUEUED, 0, (Guid?)null), (released.Status, released.Attempts, released.LeaseToken));

        await using (var db = fixture.CreateContext())
        {
            var fetcher = new CallbackFetcher(_ => { });
            var failing = new IngestionProcessor(new ThrowingCompleteStore(new IngestionJobStore(db, Limits)), Storage(), fetcher,
                new DocumentTextExtractor(), clock);
            var outcome = await failing.ProcessNextAsync(Cancellation);
            Assert.Equal((IngestionOutcomeKind.Retrying, IngestionFailure.INTERNAL_ERROR, 1), (outcome!.Kind, outcome.Failure, outcome.Attempt));
        }
        var retried = await LoadAsync(document.Id);
        Assert.Equal((IngestionJobStatus.QUEUED, 1, IngestionFailure.INTERNAL_ERROR), (retried.Jobs[0].Status, retried.Jobs[0].Attempts, retried.Jobs[0].LastError));
        Assert.Equal(KnowledgeDocumentStatus.PENDING, retried.Document.Status);
    }

    [Fact]
    public async Task Slow_extraction_times_out_as_a_permanent_failure_and_manual_retries_are_capped()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var document = await UploadAsync(user, ws, source, "slow.txt", "text/plain", "slow"u8.ToArray());
        await IsolateAsync(document.Id);

        for (var run = 0; run <= KnowledgeDocument.MaxManualRetries; run++)
        {
            await using (var db = fixture.CreateContext())
            {
                var processor = new IngestionProcessor(new IngestionJobStore(db, Limits), Storage(), new SafeWebPageFetcher(), new SlowExtractor(), clock)
                    { ExtractionTimeout = TimeSpan.FromMilliseconds(100) };
                var outcome = await processor.ProcessNextAsync(Cancellation);
                Assert.Equal((IngestionOutcomeKind.Failed, IngestionFailure.CONTENT_TOO_LARGE, 1), (outcome!.Kind, outcome.Failure, outcome.Attempt));
            }
            await using (var db = fixture.CreateContext())
            {
                var retry = await DocumentStore(db).RetryAsync(user, ws, source, document.Id, Cancellation);
                Assert.Equal(run < KnowledgeDocument.MaxManualRetries ? KnowledgeDocumentStatusCode.Success : KnowledgeDocumentStatusCode.Conflict,
                    retry.Status);
            }
        }
        Assert.Equal(KnowledgeDocument.MaxManualRetries + 1, (await LoadAsync(document.Id)).Jobs.Length);
        // Every timed-out extraction saw cancellation, so an isolated parser would have been killed.
        Assert.Equal(KnowledgeDocument.MaxManualRetries + 1, SlowExtractor.Cancelled);
    }

    [Fact]
    public async Task Concurrent_retries_of_one_failed_document_queue_exactly_one_job()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var document = await UploadAsync(user, ws, source, "gone.txt", "text/plain", "x"u8.ToArray());
        File.Delete(Path.Combine(storageRoot, ws.ToString("N"), document.Id.ToString("N")));
        await IsolateAsync(document.Id);
        Assert.Equal(IngestionFailure.STORAGE_MISSING, (await ProcessAsync())!.Failure);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            return (await DocumentStore(db).RetryAsync(user, ws, source, document.Id, Cancellation)).Status;
        }));
        Assert.Equal(1, results.Count(r => r == KnowledgeDocumentStatusCode.Success));
        Assert.Equal(5, results.Count(r => r == KnowledgeDocumentStatusCode.Conflict));
        Assert.Equal(2, (await LoadAsync(document.Id)).Jobs.Length);
    }

    [Fact]
    public async Task Claims_are_fair_across_workspaces_and_capped_per_workspace()
    {
        var (userA, busy) = await WorkspaceAsync();
        var (userB, quiet) = await WorkspaceAsync();
        var busyDocs = new List<Guid>();
        for (var i = 0; i < 3; i++) busyDocs.Add((await RequestAsync(userA, busy, await SourceAsync(busy, KnowledgeSourceKind.TEXT, $"A{i}"))).Id);
        var quietDoc = (await RequestAsync(userB, quiet, await SourceAsync(quiet, KnowledgeSourceKind.TEXT, "B"))).Id;
        await IsolateAsync([.. busyDocs, quietDoc]);

        await using var db = fixture.CreateContext();
        var store = new IngestionJobStore(db, new KnowledgeLimits { MaxRunningJobsPerWorkspace = 2 });
        async Task<Guid?> ClaimAsync() => (await store.ClaimNextAsync(clock.Now, TimeSpan.FromMinutes(5), Cancellation))?.DocumentId;
        // The quiet workspace's newer job runs second, ahead of the busy workspace's backlog, and the cap then stops the backlog.
        Assert.Equal(busyDocs[0], await ClaimAsync());
        Assert.Equal(quietDoc, await ClaimAsync());
        Assert.Equal(busyDocs[1], await ClaimAsync());
        Assert.Null(await ClaimAsync());
        // Expired leases no longer count as live, so the backlog continues.
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.NotNull(await ClaimAsync());
    }

    [Fact]
    public async Task Deleting_a_document_removes_rows_and_file_and_a_running_worker_loses_its_lease()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var document = await UploadAsync(user, ws, source, "doomed.txt", "text/plain", "Doomed content"u8.ToArray());
        var path = Path.Combine(storageRoot, ws.ToString("N"), document.Id.ToString("N"));
        await IsolateAsync(document.Id);
        await using var db = fixture.CreateContext();
        var jobs = new IngestionJobStore(db, Limits);
        var work = (await jobs.ClaimNextAsync(clock.Now, TimeSpan.FromMinutes(5), Cancellation))!;

        await using (var api = fixture.CreateContext())
        {
            Assert.Equal(KnowledgeDocumentStatusCode.Success, (await DocumentStore(api).DeleteAsync(user, ws, source, document.Id, Cancellation)).Status);
            Assert.Equal(KnowledgeDocumentStatusCode.NotFound, (await DocumentStore(api).DeleteAsync(user, ws, source, document.Id, Cancellation)).Status);
        }
        Assert.False(File.Exists(path));
        var content = new IngestionContent("text/plain", 14, new string('d', 64), null, 14, [new(1, 0, "Doomed content")]);
        Assert.Equal(IngestionOutcomeKind.LeaseLost, await jobs.CompleteAsync(work, content, clock.Now, Cancellation));
        await using var check = fixture.CreateContext();
        Assert.False(await check.KnowledgeDocuments.AnyAsync(d => d.Id == document.Id, Cancellation));
        Assert.False(await check.IngestionJobs.AnyAsync(j => j.DocumentId == document.Id, Cancellation));
        Assert.False(await check.KnowledgeDocumentChunks.AnyAsync(c => c.DocumentId == document.Id, Cancellation));
    }

    [Fact]
    public async Task The_sweeper_removes_only_old_unreferenced_files()
    {
        var (user, ws) = await WorkspaceAsync();
        var source = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var kept = await UploadAsync(user, ws, source, "kept.txt", "text/plain", "kept"u8.ToArray());
        var directory = Path.Combine(storageRoot, ws.ToString("N"));
        var old = DateTime.UtcNow.AddHours(-2);
        string Write(string name, DateTime written)
        {
            var file = Path.Combine(directory, name);
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, written);
            return file;
        }
        var orphan = Write(Guid.CreateVersion7().ToString("N"), old);
        var recentOrphan = Write(Guid.CreateVersion7().ToString("N"), DateTime.UtcNow);
        var staleTemp = Write($"{Guid.CreateVersion7():N}.{Guid.NewGuid():N}.tmp", old);
        var foreign = Write("README.txt", old);
        File.SetLastWriteTimeUtc(Path.Combine(directory, kept.Id.ToString("N")), old);
        // Workspace deletion cascades rows but not files; the sweeper cleans those too.
        var (otherUser, otherWs) = await WorkspaceAsync();
        var otherSource = await SourceAsync(otherWs, KnowledgeSourceKind.FILE);
        var cascaded = await UploadAsync(otherUser, otherWs, otherSource, "gone.txt", "text/plain", "gone"u8.ToArray());
        var cascadedPath = Path.Combine(storageRoot, otherWs.ToString("N"), cascaded.Id.ToString("N"));
        File.SetLastWriteTimeUtc(cascadedPath, old);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Workspaces\" WHERE \"Id\" = {otherWs}", Cancellation);

        // A Worker pointed at an empty database refuses to sweep at all.
        var blind = new KnowledgeStorageSweeper(Storage(), new StubIndex(hasWorkspaces: false), TimeProvider.System);
        Assert.True((await blind.SweepAsync(KnowledgeStorageSweeper.DefaultGracePeriod, Cancellation)).Aborted);
        Assert.True(File.Exists(orphan));

        await using var sweepDb = fixture.CreateContext();
        var sweeper = new KnowledgeStorageSweeper(Storage(), new KnowledgeStorageIndex(sweepDb), TimeProvider.System);
        var result = await sweeper.SweepAsync(KnowledgeStorageSweeper.DefaultGracePeriod, Cancellation);
        Assert.Equal((2, false), (result.Quarantined, result.Aborted));
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(cascadedPath));
        Assert.False(File.Exists(staleTemp));
        Assert.True(File.Exists(recentOrphan));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(Path.Combine(directory, kept.Id.ToString("N"))));
        // Orphans stay recoverable in quarantine until the retention period passes.
        var quarantined = Path.Combine(storageRoot, "_quarantine", ws.ToString("N"), Path.GetFileName(orphan));
        Assert.True(File.Exists(quarantined));
        Assert.Equal(0, (await sweeper.SweepAsync(KnowledgeStorageSweeper.DefaultGracePeriod, Cancellation)).Quarantined);
        // The cascaded workspace's now-empty directory is removed once it is old.
        Directory.SetLastWriteTimeUtc(Path.Combine(storageRoot, otherWs.ToString("N")), old);
        await sweeper.SweepAsync(KnowledgeStorageSweeper.DefaultGracePeriod, Cancellation);
        Assert.False(Directory.Exists(Path.Combine(storageRoot, otherWs.ToString("N"))));
        File.SetLastWriteTimeUtc(quarantined, DateTime.UtcNow - KnowledgeStorageSweeper.QuarantineRetention - TimeSpan.FromHours(1));
        Assert.True((await sweeper.SweepAsync(KnowledgeStorageSweeper.DefaultGracePeriod, Cancellation)).Purged >= 1);
        Assert.False(File.Exists(quarantined));
    }

    [Fact]
    public async Task The_sweeper_aborts_when_most_files_look_unreferenced()
    {
        var workspace = Guid.CreateVersion7();
        var directory = Directory.CreateDirectory(Path.Combine(storageRoot, workspace.ToString("N"))).FullName;
        for (var i = 0; i <= KnowledgeStorageSweeper.SafetyFloor; i++)
        {
            var file = Path.Combine(directory, Guid.CreateVersion7().ToString("N"));
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));
        }
        var sweeper = new KnowledgeStorageSweeper(Storage(), new StubIndex(hasWorkspaces: true), TimeProvider.System);
        var result = await sweeper.SweepAsync(KnowledgeStorageSweeper.DefaultGracePeriod, Cancellation);
        Assert.Equal((KnowledgeStorageSweeper.SafetyFloor + 1, 0, true), (result.Scanned, result.Quarantined, result.Aborted));
        Assert.Equal(KnowledgeStorageSweeper.SafetyFloor + 1, Directory.GetFiles(directory).Length);
    }

    private sealed class CallbackFetcher(Action<string> onFetch) : IWebPageFetcher
    {
        public Task<FetchedWebPage> FetchAsync(string url, CancellationToken cancellationToken)
        {
            onFetch(url);
            return Task.FromResult(new FetchedWebPage(url, "text/plain", null, "Fetched text"u8.ToArray()));
        }
    }

    private sealed class SlowExtractor : IDocumentTextExtractor
    {
        public static int Cancelled;

        public async Task<string> ExtractAsync(string contentType, string? charset, byte[] content, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }
            return "late";
        }
    }

    private sealed class StubIndex(bool hasWorkspaces) : IKnowledgeStorageIndex
    {
        public Task<IReadOnlySet<Guid>> ExistingUploadsAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<bool> HasWorkspacesAsync(CancellationToken ct) => Task.FromResult(hasWorkspaces);
    }

    private sealed class ThrowingCompleteStore(IIngestionJobStore inner) : IIngestionJobStore
    {
        public Task<IngestionWork?> ClaimNextAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct) => inner.ClaimNextAsync(now, lease, ct);
        public Task<IngestionOutcomeKind> CompleteAsync(IngestionWork work, IngestionContent content, DateTimeOffset now, CancellationToken ct) =>
            throw new InvalidOperationException("Simulated persistence failure.");
        public Task<IngestionOutcomeKind> FailAsync(IngestionWork work, IngestionFailure reason, bool transient, DateTimeOffset now,
            CancellationToken ct) => inner.FailAsync(work, reason, transient, now, ct);
        public Task<bool> ReleaseAsync(IngestionWork work, DateTimeOffset now, CancellationToken ct) => inner.ReleaseAsync(work, now, ct);
    }
}
