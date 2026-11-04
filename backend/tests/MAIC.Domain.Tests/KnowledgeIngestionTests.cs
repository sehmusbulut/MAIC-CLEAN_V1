using MAIC.Domain.Knowledge;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class KnowledgeIngestionTests
{
    private const string Emoji = "\ud83d\ude42";
    private static readonly string Sha = new('a', 64);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static KnowledgeDocument Upload() => KnowledgeDocument.ForUpload(Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.NewGuid(), "guide.pdf", KnowledgeDocument.ApplicationPdf, 10, Sha);

    [Fact]
    public void Upload_document_records_fixed_file_metadata_and_moves_through_its_lifecycle()
    {
        var document = Upload();
        Assert.Equal((KnowledgeDocumentOrigin.UPLOAD, KnowledgeDocumentStatus.PENDING), (document.Origin, document.Status));
        Assert.Equal(("guide.pdf", "application/pdf", 10L, Sha), (document.FileName, document.ContentType, document.SizeBytes, document.ContentSha256));
        Assert.Equal('7', document.Id.ToString()[14]);

        document.MarkFailed(IngestionFailure.INVALID_CONTENT, Now);
        Assert.Equal((KnowledgeDocumentStatus.FAILED, IngestionFailure.INVALID_CONTENT, Now), (document.Status, document.FailureReason, document.ProcessedAt));
        Assert.Throws<InvalidOperationException>(() => document.MarkReady(1, 1, Now));
        document.Requeue();
        Assert.Equal((KnowledgeDocumentStatus.PENDING, (IngestionFailure?)null, (DateTimeOffset?)null), (document.Status, document.FailureReason, document.ProcessedAt));
        Assert.Throws<ArgumentException>(() => document.MarkReady(1, 1, Now, "text/plain", 1, Sha, "https://example.com"));
        document.MarkReady(3, 120, Now.AddTicks(7));
        Assert.Equal((KnowledgeDocumentStatus.READY, 3, 120, Now), (document.Status, document.ChunkCount, document.CharacterCount, document.ProcessedAt));
        Assert.Equal(("application/pdf", Sha), (document.ContentType, document.ContentSha256));
        Assert.Throws<InvalidOperationException>(() => document.MarkSuperseded(Now));
        Assert.Throws<InvalidOperationException>(() => document.Requeue());
    }

    [Fact]
    public void Snapshots_record_processed_content_and_can_be_superseded()
    {
        var ws = Guid.CreateVersion7();
        var text = KnowledgeDocument.ForSnapshot(ws, Guid.CreateVersion7(), KnowledgeSourceKind.TEXT, Guid.CreateVersion7(), Guid.NewGuid());
        Assert.Equal(KnowledgeDocumentOrigin.TEXT, text.Origin);
        Assert.Null(text.ContentType);
        Assert.Throws<ArgumentException>(() => text.MarkReady(1, 1, Now));
        text.MarkReady(1, 5, Now, "text/plain", 5, Sha);
        Assert.Equal(("text/plain", 5L, (string?)null), (text.ContentType, text.SizeBytes, text.SourceUrl));
        text.MarkSuperseded(Now);
        Assert.Equal(KnowledgeDocumentStatus.SUPERSEDED, text.Status);

        var site = KnowledgeDocument.ForSnapshot(ws, Guid.CreateVersion7(), KnowledgeSourceKind.WEBSITE, Guid.CreateVersion7(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => site.MarkReady(1, 1, Now, "text/html", 1, Sha));
        site.MarkReady(1, 1, Now, "text/html", 1, Sha, "https://example.com/final");
        Assert.Equal("https://example.com/final", site.SourceUrl);
        Assert.Throws<ArgumentException>(() => KnowledgeDocument.ForSnapshot(ws, Guid.CreateVersion7(), KnowledgeSourceKind.FILE, Guid.CreateVersion7(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => KnowledgeDocument.ForSnapshot(ws, Guid.CreateVersion7(), KnowledgeSourceKind.TEXT, Guid.CreateVersion7(), Guid.Empty));
    }

    [Theory]
    [InlineData("guide.pdf", true)]
    [InlineData("rapor ğüş " + Emoji + ".md", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(" lead.txt", false)]
    [InlineData("trail.txt ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b.txt", false)]
    [InlineData("a\\b.txt", false)]
    [InlineData("a\u0000b", false)]
    [InlineData("a\u0085b", false)]
    public void File_names_are_display_only_and_strict(string name, bool valid) =>
        Assert.Equal(valid, KnowledgeDocument.IsValidFileName(name));

    [Fact]
    public void Upload_factory_rejects_invalid_metadata()
    {
        var (ws, source, user) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.False(KnowledgeDocument.IsValidFileName(new string('a', 256)));
        // Lone surrogates are asserted here: xUnit theory serialization would replace them with U+FFFD.
        Assert.False(KnowledgeDocument.IsValidFileName("a\ud83d"));
        Assert.False(KnowledgeDocument.IsValidFileName("\ude42a"));
        // Invisible format characters could disguise a name, for example a right-to-left override.
        Assert.False(KnowledgeDocument.IsValidFileName("invoice\u202efdp.exe"));
        Assert.False(KnowledgeDocument.IsValidFileName("a\u200bb.txt"));
        Assert.False(KnowledgeDocument.IsValidFileName("a\ufeffb.txt"));
        Assert.True(KnowledgeDocument.IsValidFileName(new string('a', 255)));
        Assert.ThrowsAny<ArgumentException>(() => KnowledgeDocument.ForUpload(ws, source, user, Guid.NewGuid(), "a.html", "text/html", 1, Sha));
        Assert.ThrowsAny<ArgumentException>(() => KnowledgeDocument.ForUpload(ws, source, user, Guid.NewGuid(), "a.txt", "text/plain", 0, Sha));
        Assert.ThrowsAny<ArgumentException>(() => KnowledgeDocument.ForUpload(ws, source, user, Guid.NewGuid(), "a.txt", "text/plain",
            KnowledgeDocument.MaxUploadBytes + 1, Sha));
        Assert.ThrowsAny<ArgumentException>(() => KnowledgeDocument.ForUpload(ws, source, user, Guid.NewGuid(), "a.txt", "text/plain", 1, Sha.ToUpperInvariant()));
        Assert.ThrowsAny<ArgumentException>(() => KnowledgeDocument.ForUpload(Guid.Empty, source, user, Guid.NewGuid(), "a.txt", "text/plain", 1, Sha));
    }

    [Fact]
    public void Jobs_lease_retry_with_backoff_and_fail_after_the_last_attempt()
    {
        var job = new IngestionJob(Guid.CreateVersion7(), Guid.CreateVersion7(), Now, maxAttempts: 3);
        Assert.Equal((IngestionJobStatus.QUEUED, 0, Now), (job.Status, job.Attempts, job.NextAttemptAt));
        Assert.False(job.IsDue(Now.AddTicks(-10)));
        var lease = job.Claim(Now, TimeSpan.FromMinutes(10));
        Assert.Equal((IngestionJobStatus.RUNNING, 1, Now.AddMinutes(10)), (job.Status, job.Attempts, job.NextAttemptAt));
        Assert.Throws<InvalidOperationException>(() => job.Succeed(Guid.NewGuid(), Now));

        Assert.True(job.Fail(lease, IngestionFailure.FETCH_FAILED, transient: true, Now));
        Assert.Equal((IngestionJobStatus.QUEUED, Now.AddSeconds(30), IngestionFailure.FETCH_FAILED), (job.Status, job.NextAttemptAt, job.LastError));
        Assert.Throws<InvalidOperationException>(() => job.Fail(lease, IngestionFailure.FETCH_FAILED, true, Now));

        lease = job.Claim(Now.AddSeconds(30), TimeSpan.FromMinutes(10));
        Assert.True(job.Fail(lease, IngestionFailure.STORAGE_UNAVAILABLE, true, Now.AddSeconds(30)));
        Assert.Equal(Now.AddSeconds(90), job.NextAttemptAt);

        lease = job.Claim(Now.AddSeconds(90), TimeSpan.FromMinutes(10));
        Assert.False(job.Fail(lease, IngestionFailure.FETCH_FAILED, true, Now.AddMinutes(2)));
        Assert.Equal((IngestionJobStatus.FAILED, 3, Now.AddMinutes(2)), (job.Status, job.Attempts, job.CompletedAt));
        Assert.Null(job.LeaseToken);
        Assert.Throws<InvalidOperationException>(() => job.Claim(Now.AddDays(1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Releasing_an_attempt_requeues_it_without_counting_it()
    {
        var job = new IngestionJob(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        var lease = job.Claim(Now, TimeSpan.FromMinutes(10));
        Assert.Throws<InvalidOperationException>(() => job.Release(Guid.NewGuid(), Now));
        job.Release(lease, Now.AddSeconds(5));
        Assert.Equal((IngestionJobStatus.QUEUED, 0, Now.AddSeconds(5), (Guid?)null), (job.Status, job.Attempts, job.NextAttemptAt, job.LeaseToken));
        Assert.Throws<InvalidOperationException>(() => job.Release(lease, Now));
    }

    [Fact]
    public void Permanent_failures_cancellation_expired_leases_and_backoff_cap()
    {
        var job = new IngestionJob(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        var lease = job.Claim(Now, TimeSpan.FromMinutes(1));
        Assert.False(job.Fail(lease, IngestionFailure.INVALID_CONTENT, transient: false, Now));
        Assert.Equal(IngestionJobStatus.FAILED, job.Status);

        var cancelled = new IngestionJob(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        cancelled.Cancel(IngestionFailure.SOURCE_DISABLED, Now);
        Assert.Equal((IngestionJobStatus.CANCELLED, IngestionFailure.SOURCE_DISABLED), (cancelled.Status, cancelled.LastError));
        Assert.Throws<InvalidOperationException>(() => cancelled.Cancel(IngestionFailure.SOURCE_DISABLED, Now));

        var crashed = new IngestionJob(Guid.CreateVersion7(), Guid.CreateVersion7(), Now, maxAttempts: 2);
        var first = crashed.Claim(Now, TimeSpan.FromMinutes(1));
        Assert.False(crashed.IsDue(Now));
        var second = crashed.Claim(Now.AddMinutes(1), TimeSpan.FromMinutes(1)); // expired lease is reclaimed
        Assert.NotEqual(first, second);
        Assert.False(crashed.Holds(first));
        Assert.Throws<InvalidOperationException>(() => crashed.Abandon(Now.AddSeconds(90)));
        crashed.Abandon(Now.AddMinutes(2));
        Assert.Equal((IngestionJobStatus.FAILED, IngestionFailure.LEASE_EXPIRED), (crashed.Status, crashed.LastError));

        Assert.Equal(TimeSpan.FromSeconds(30), IngestionJob.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(240), IngestionJob.Backoff(4));
        Assert.Equal(IngestionJob.MaxBackoff, IngestionJob.Backoff(50));
    }

    [Fact]
    public void Normalize_unifies_whitespace_and_removes_unsafe_characters()
    {
        Assert.Equal("a b\nc\n\nd", KnowledgeChunker.Normalize("\ufeff  a \t b  \r\n  c\r\r\r\n\n d \u0000 "));
        Assert.Equal("x\ufffdy" + Emoji, KnowledgeChunker.Normalize("x\ud83dy" + Emoji));
        Assert.Equal("", KnowledgeChunker.Normalize(" \n\t\u0001 "));
        Assert.Equal(KnowledgeChunker.MaxTextLength + 1, KnowledgeChunker.Normalize(new string('a', KnowledgeChunker.MaxTextLength + 50)).Length);
    }

    [Fact]
    public void Split_bounds_chunks_prefers_boundaries_overlaps_and_is_deterministic()
    {
        Assert.Empty(KnowledgeChunker.Split(""));
        var single = Assert.Single(KnowledgeChunker.Split("Short text."));
        Assert.Equal(new KnowledgeChunkSpan(1, 0, "Short text."), single);

        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"Paragraph {i}. " + string.Join(' ', Enumerable.Repeat("word", 40))));
        var chunks = KnowledgeChunker.Split(paragraphs);
        Assert.True(chunks.Count > 1);
        Assert.Equal(Enumerable.Range(1, chunks.Count), chunks.Select(c => c.Sequence));
        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.Content.Length, 1, KnowledgeChunker.MaxChunkLength);
            Assert.Equal(chunk.Content, paragraphs.Substring(chunk.StartOffset, chunk.Content.Length));
            Assert.Equal(chunk.Content, chunk.Content.Trim());
        }
        // Paragraph-aware breaks: every chunk but the last ends at a paragraph's end.
        Assert.All(chunks.SkipLast(1), c => Assert.EndsWith("word", c.Content));
        // Consecutive chunks overlap and cover the whole text.
        for (var i = 1; i < chunks.Count; i++)
            Assert.True(chunks[i].StartOffset < chunks[i - 1].StartOffset + chunks[i - 1].Content.Length);
        Assert.Equal(paragraphs.Length, chunks[^1].StartOffset + chunks[^1].Content.Length);
        Assert.Equal(chunks, KnowledgeChunker.Split(paragraphs));
    }

    [Fact]
    public void Split_hard_splits_unbroken_text_without_breaking_surrogate_pairs()
    {
        var text = string.Concat(Enumerable.Repeat(Emoji, 3000));
        var chunks = KnowledgeChunker.Split(text);
        Assert.True(chunks.Count >= 3);
        foreach (var chunk in chunks)
        {
            Assert.False(char.IsLowSurrogate(chunk.Content[0]));
            Assert.False(char.IsHighSurrogate(chunk.Content[^1]));
            Assert.InRange(chunk.Content.Length, 1, KnowledgeChunker.MaxChunkLength);
        }
        Assert.Equal(text.Length, chunks[^1].StartOffset + chunks[^1].Content.Length);
    }

    [Fact]
    public void Chunks_require_bounded_nonblank_content()
    {
        var (ws, doc) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.Equal(1, new KnowledgeDocumentChunk(ws, doc, 1, 0, "x").Sequence);
        Assert.Throws<ArgumentOutOfRangeException>(() => new KnowledgeDocumentChunk(ws, doc, 0, 0, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KnowledgeDocumentChunk(ws, doc, 1, -1, "x"));
        Assert.Throws<ArgumentException>(() => new KnowledgeDocumentChunk(ws, doc, 1, 0, " "));
        Assert.Throws<ArgumentException>(() => new KnowledgeDocumentChunk(ws, doc, 1, 0, new string('x', KnowledgeChunker.MaxChunkLength + 1)));
    }
}
