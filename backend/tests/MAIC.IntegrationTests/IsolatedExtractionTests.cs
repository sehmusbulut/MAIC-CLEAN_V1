using System.Diagnostics;
using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using MAIC.Worker.Jobs;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>The Worker parses untrusted content in a child process; these tests run the real child and a scripted stand-in.</summary>
public sealed class IsolatedExtractionTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static IsolatedDocumentTextExtractor Real() => new(new IsolatedExtractionOptions());

    /// <summary>A shell script plays the child so exit codes, output and environment handling can be forced.</summary>
    private static IsolatedDocumentTextExtractor Scripted(string script, int maxOutputBytes = 16 * 1024 * 1024) =>
        new(new IsolatedExtractionOptions { FileName = "/bin/sh", PrefixArguments = ["-c", script, "child"], MaxOutputBytes = maxOutputBytes });

    [Fact]
    public async Task The_real_child_extracts_text_and_pdf_and_classifies_bad_content()
    {
        var extractor = Real();
        await extractor.VerifyAsync(Cancellation);
        Assert.Equal("Merhaba çıktı", await extractor.ExtractAsync("text/plain", null, "Merhaba çıktı"u8.ToArray(), Cancellation));
        Assert.Contains("Isolated page", await extractor.ExtractAsync("application/pdf", null, KnowledgeTestData.Pdf("Isolated page"), Cancellation));
        Assert.Contains("Title", await extractor.ExtractAsync("text/html", "utf-8", "<title>Title</title><p>x</p>"u8.ToArray(), Cancellation));

        foreach (var (type, content, expected) in new (string, byte[], IngestionFailure)[]
                 {
                     ("application/pdf", "%PDF-1.7 broken"u8.ToArray(), IngestionFailure.INVALID_CONTENT),
                     ("text/plain", [0xC3, 0x28], IngestionFailure.INVALID_CONTENT),
                     ("image/png", [1, 2, 3], IngestionFailure.UNSUPPORTED_CONTENT)
                 })
        {
            var error = await Assert.ThrowsAsync<IngestionException>(() => extractor.ExtractAsync(type, null, content, Cancellation));
            Assert.Equal((expected, false), (error.Reason, error.Transient));
        }
    }

    [Fact]
    public async Task The_child_gets_a_heap_limit_and_no_secrets()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Maic", "Host=secret");
        Environment.SetEnvironmentVariable("KnowledgeStorage__RootPath", "/secret");
        try
        {
            var text = await Scripted("""printf '%s|%s|%s|%s' "$DOTNET_GCHeapHardLimit" "${ConnectionStrings__Maic:-none}" "${KnowledgeStorage__RootPath:-none}" "$2" """)
                .ExtractAsync("text/plain", null, "x"u8.ToArray(), Cancellation);
            Assert.Equal("20000000|none|none|text/plain", text);
            // Only allowlisted variables reach the child, whatever the secret's name.
            Environment.SetEnvironmentVariable("PGPASSWORD", "secret");
            Environment.SetEnvironmentVariable("DOTNET_ConnectionStrings__Maic", "secret");
            Assert.Equal("none|none|180", await Scripted("""printf '%s|%s|%s' "${PGPASSWORD:-none}" "${DOTNET_ConnectionStrings__Maic:-none}" "$MAIC_EXTRACTION_DEADLINE_SECONDS" """)
                .ExtractAsync("text/plain", null, "x"u8.ToArray(), Cancellation));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Maic", null);
            Environment.SetEnvironmentVariable("KnowledgeStorage__RootPath", null);
            Environment.SetEnvironmentVariable("PGPASSWORD", null);
            Environment.SetEnvironmentVariable("DOTNET_ConnectionStrings__Maic", null);
        }
    }

    [Theory]
    [InlineData("printf EMPTY_CONTENT >&2; exit 2", IngestionFailure.EMPTY_CONTENT, false)]
    [InlineData("printf NOT_A_CODE >&2; exit 2", IngestionFailure.INVALID_CONTENT, false)]
    [InlineData("printf STORAGE_MISSING >&2; exit 2", IngestionFailure.INVALID_CONTENT, false)] // only content codes are trusted
    [InlineData("printf ' EMPTY_CONTENT' >&2; exit 2", IngestionFailure.INVALID_CONTENT, false)]
    [InlineData("head -c 100000 /dev/zero >&2; exit 2", IngestionFailure.INVALID_CONTENT, false)] // stderr is drained, never blocks
    [InlineData("exit 3", IngestionFailure.CONTENT_TOO_LARGE, false)]
    [InlineData("kill -SEGV $$", IngestionFailure.INVALID_CONTENT, false)]
    [InlineData("kill -ABRT $$", IngestionFailure.INVALID_CONTENT, false)]
    [InlineData("head -c 5000 /dev/zero", IngestionFailure.CONTENT_TOO_LARGE, false)]
    // Killed from outside (for example by a shutdown signal to the process group) or a host error: retried, never a verdict.
    [InlineData("kill -TERM $$", IngestionFailure.INTERNAL_ERROR, true)]
    [InlineData("kill -INT $$", IngestionFailure.INTERNAL_ERROR, true)]
    [InlineData("kill -KILL $$", IngestionFailure.INTERNAL_ERROR, true)]
    [InlineData("exit 1", IngestionFailure.INTERNAL_ERROR, true)]
    public async Task Child_exit_codes_map_to_content_failures_or_retries(string script, IngestionFailure expected, bool transient)
    {
        var error = await Assert.ThrowsAsync<IngestionException>(() =>
            Scripted("cat > /dev/null; " + script, maxOutputBytes: 1024).ExtractAsync("text/plain", null, "x"u8.ToArray(), Cancellation));
        Assert.Equal((expected, transient), (error.Reason, error.Transient));
    }

    [Fact]
    public async Task Cancellation_kills_a_runaway_child()
    {
        var marker = $"29.{Random.Shared.Next(100000, 999999)}"; // a unique sleep duration identifies the child
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Scripted($"exec sleep {marker}").ExtractAsync("text/plain", null, "x"u8.ToArray(), deadline.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        await Task.Delay(200, Cancellation);
        Assert.DoesNotContain(Process.GetProcessesByName("sleep"), p => p.StartTime > DateTime.Now.AddSeconds(-15) && !p.HasExited
            && File.ReadAllText($"/proc/{p.Id}/cmdline").Contains(marker));
    }

    [Fact]
    public async Task A_timed_out_extraction_fails_the_document_permanently()
    {
        // The processor's deadline cancels the extractor, which kills the child instead of leaving it running.
        var extractor = Scripted("sleep 30");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync("application/pdf", null, [1], deadline.Token));
    }
}
