using System.Diagnostics;
using System.Globalization;
using System.Text;
using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;

namespace MAIC.Worker.Jobs;

public sealed class IsolatedExtractionOptions
{
    public const string Command = "extract-text";

    private static readonly (string FileName, string[] Prefix) Default = DefaultCommand();

    /// <summary>Executable that hosts the child: this Worker's own apphost, or the dotnet host plus the Worker assembly.</summary>
    public string FileName { get; init; } = Default.FileName;
    /// <summary>Arguments placed before the extraction arguments.</summary>
    public IReadOnlyList<string> PrefixArguments { get; init; } = Default.Prefix;
    /// <summary>Managed heap limit of the child (DOTNET_GCHeapHardLimit); larger allocations fail inside the child only.</summary>
    public long MemoryLimitBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxOutputBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>
    /// The child's own lifetime limit, enforced inside the child, so it also ends if the Worker dies without killing it.
    /// Longer than the ingestion deadline, which normally kills the child first.
    /// </summary>
    public TimeSpan ChildDeadline { get; init; } = TimeSpan.FromMinutes(3);

    private static (string, string[]) DefaultCommand()
    {
        var worker = typeof(IsolatedDocumentTextExtractor).Assembly.Location;
        var current = Environment.ProcessPath;
        // Single-file publish has no assembly path; the executable itself is the Worker.
        if (string.IsNullOrEmpty(worker) && current is not null) return (current, []);
        // Started as ./MAIC.Worker (apphost): re-run the same executable.
        if (current is not null && Path.GetFileNameWithoutExtension(current) == Path.GetFileNameWithoutExtension(worker)) return (current, []);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(host))
            host = current is not null && Path.GetFileNameWithoutExtension(current) == "dotnet" ? current : "dotnet";
        return (host, [worker]);
    }
}

/// <summary>
/// Runs PDF/HTML/text extraction in a short-lived child process of the Worker with a hard managed-heap limit. A
/// hostile file can at worst exhaust or crash that child: cancellation (the ingestion deadline or shutdown) kills it, a
/// watchdog inside the child ends it if the Worker dies, oversized output is cut off, and the child starts from an
/// empty environment (an allowlist of runtime variables), so no secret is passed to it. The child still runs as the
/// Worker's OS user; operating-system sandboxing (separate user, no network) is a deployment concern. Protocol:
/// content on stdin, UTF-8 text on stdout, exit 0; exit 2 with a content failure code on stderr; exit 3 when memory ran
/// out. A crash (SIGABRT/SIGSEGV) is invalid content; being killed from outside or a host error is retried.
/// </summary>
public sealed class IsolatedDocumentTextExtractor(IsolatedExtractionOptions options) : IDocumentTextExtractor
{
    public const int ExitFailure = 2;
    public const int ExitOutOfMemory = 3;
    public const string DeadlineVariable = "MAIC_EXTRACTION_DEADLINE_SECONDS";

    // The child starts from an empty environment plus these runtime/locale variables, so no secret (connection strings,
    // PGPASSWORD, cloud credentials, DOTNET_-prefixed configuration...) reaches it, whatever its name.
    private static readonly string[] InheritedVariables =
        ["PATH", "HOME", "TMPDIR", "TMP", "TEMP", "LANG", "LC_ALL", "SystemRoot", "DOTNET_ROOT", "DOTNET_ROLL_FORWARD"];

    // Only content failures are meaningful from a parser; anything else is treated as invalid content.
    private static readonly HashSet<string> ChildFailureCodes =
    [
        nameof(IngestionFailure.INVALID_CONTENT), nameof(IngestionFailure.EMPTY_CONTENT),
        nameof(IngestionFailure.CONTENT_TOO_LARGE), nameof(IngestionFailure.UNSUPPORTED_CONTENT)
    ];

    public async Task<string> ExtractAsync(string contentType, string? charset, byte[] content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(options.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in options.PrefixArguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(IsolatedExtractionOptions.Command);
        start.ArgumentList.Add(contentType);
        start.ArgumentList.Add(charset ?? "-");
        start.Environment.Clear();
        foreach (var name in InheritedVariables)
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment[DeadlineVariable] = ((int)options.ChildDeadline.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        start.Environment["DOTNET_GCHeapHardLimit"] = options.MemoryLimitBytes.ToString("X", CultureInfo.InvariantCulture);
        start.Environment["DOTNET_gcServer"] = "0";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The extraction process did not start.");
        try
        {
            var write = WriteInputAsync(process, content, cancellationToken);
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, options.MaxOutputBytes, cancellationToken);
            // stderr is drained to the end (keeping only a prefix) so a chatty child can never block on a full pipe.
            var error = ReadBoundedAsync(process.StandardError.BaseStream, 256, cancellationToken, drainOverflow: true);
            var (text, overflow) = await output;
            if (overflow)
            {
                _ = write.ContinueWith(t => t.Exception, TaskScheduler.Default);
                throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
            }
            await write;
            var (errorBytes, errorOverflow) = await error;
            await process.WaitForExitAsync(cancellationToken);
            // A shutdown can kill the child (SIGTERM/SIGINT to the process group) before the token reaches us; never
            // turn that into a content verdict.
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode switch
            {
                0 => Encoding.UTF8.GetString(text),
                ExitFailure => throw new IngestionException(ParseFailure(errorBytes, errorOverflow), false),
                ExitOutOfMemory => throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false),
                // SIGABRT (runtime fail-fast: stack overflow or heap hard limit) and SIGSEGV are caused by the content.
                128 + 6 or 128 + 11 => throw new IngestionException(IngestionFailure.INVALID_CONTENT, false),
                // Killed from outside (SIGINT/SIGKILL/SIGTERM) or a host/runtime error: not the content's fault; retry.
                _ => throw new IngestionException(IngestionFailure.INTERNAL_ERROR, true)
            };
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static async Task WriteInputAsync(Process process, byte[] content, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(content, cancellationToken);
            process.StandardInput.Close();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The child exited early (for example after rejecting the input); its exit code decides the outcome.
        }
    }

    private static async Task<(byte[] Bytes, bool Overflow)> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken,
        bool drainOverflow = false)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var overflow = false;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (!overflow && buffer.Length + read > limit)
            {
                overflow = true;
                if (!drainOverflow) break;
            }
            if (!overflow) buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), overflow);
    }

    private static IngestionFailure ParseFailure(byte[] error, bool overflow)
    {
        var code = Encoding.UTF8.GetString(error);
        return !overflow && ChildFailureCodes.Contains(code) ? Enum.Parse<IngestionFailure>(code) : IngestionFailure.INVALID_CONTENT;
    }

    /// <summary>Fails Worker startup if the child cannot run, so a broken deployment never marks content as invalid.</summary>
    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var text = await ExtractAsync(KnowledgeDocument.TextPlain, null, "ok"u8.ToArray(), timeout.Token);
        if (text != "ok") throw new InvalidOperationException("The isolated extraction process returned unexpected output.");
    }
}

/// <summary>The child side: <c>MAIC.Worker extract-text {contentType} {charset|-}</c>.</summary>
public static class ExtractionChild
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3) return 64;
        // Watchdog: end even if the Worker died and can no longer kill this process.
        if (int.TryParse(Environment.GetEnvironmentVariable(IsolatedDocumentTextExtractor.DeadlineVariable), out var seconds) && seconds > 0)
            _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => Process.GetCurrentProcess().Kill(), TaskScheduler.Default);
        await using var stderr = new StreamWriter(Console.OpenStandardError());
        try
        {
            using var input = new MemoryStream();
            await using (var stdin = Console.OpenStandardInput())
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await stdin.ReadAsync(chunk)) > 0)
                {
                    if (input.Length + read > KnowledgeDocument.MaxUploadBytes)
                    {
                        await stderr.WriteAsync(nameof(IngestionFailure.CONTENT_TOO_LARGE));
                        return IsolatedDocumentTextExtractor.ExitFailure;
                    }
                    input.Write(chunk, 0, read);
                }
            }
            var text = new Infrastructure.Knowledge.DocumentTextExtractor().Extract(args[1], args[2] == "-" ? null : args[2], input.ToArray());
            await using var stdout = Console.OpenStandardOutput();
            await stdout.WriteAsync(Encoding.UTF8.GetBytes(text));
            return 0;
        }
        catch (IngestionException error)
        {
            await stderr.WriteAsync(error.Reason.ToString());
            return IsolatedDocumentTextExtractor.ExitFailure;
        }
        catch (OutOfMemoryException)
        {
            return IsolatedDocumentTextExtractor.ExitOutOfMemory;
        }
        catch (Exception)
        {
            await stderr.WriteAsync(nameof(IngestionFailure.INVALID_CONTENT));
            return IsolatedDocumentTextExtractor.ExitFailure;
        }
    }
}
