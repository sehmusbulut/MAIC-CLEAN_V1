using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MAIC.Application.Knowledge;
using MAIC.Domain.Identity;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class KnowledgeDocumentEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly string[] DocumentFields =
    [
        "characterCount", "chunkCount", "clientRequestId", "contentSha256", "contentType", "createdAt", "createdByUserId",
        "failureReason", "fileName", "id", "index", "knowledgeSourceId", "latestJob", "origin", "processedAt", "sizeBytes", "sourceUrl",
        "status", "workspaceId"
    ];
    private static readonly string[] JobFields =
        ["attempts", "completedAt", "createdAt", "id", "lastError", "maxAttempts", "nextAttemptAt", "status"];
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private readonly string storageRoot = Directory.CreateTempSubdirectory("maic-http-knowledge-").FullName;

    public void Dispose() => Directory.Delete(storageRoot, recursive: true);

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
        builder.UseSetting("KnowledgeStorage:RootPath", storageRoot);
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureServices(services => services.AddAuthentication("test-only")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test-only", _ => { }));
    });

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    private async Task<Guid> UserAsync()
    {
        await using var db = fixture.CreateContext();
        var user = new User();
        db.Users.Add(user);
        await db.SaveChangesAsync(Cancellation);
        return user.Id;
    }

    private async Task<Guid> WorkspaceAsync(Guid owner, params (Guid UserId, WorkspaceRole Role)[] members)
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("Knowledge document HTTP tests");
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, owner, WorkspaceRole.OWNER));
        db.WorkspaceMembers.AddRange(members.Select(m => new WorkspaceMember(workspace.Id, m.UserId, m.Role)));
        await db.SaveChangesAsync(Cancellation);
        return workspace.Id;
    }

    private async Task<Guid> SourceAsync(Guid workspaceId, KnowledgeSourceKind kind, string? text = null, string? url = null,
        bool enabled = true)
    {
        await using var db = fixture.CreateContext();
        var source = new KnowledgeSource(workspaceId, kind, $"{kind} source", text, url);
        source.SetEnabled(enabled);
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(Cancellation);
        return source.Id;
    }

    private static async Task AuthenticateAsync(HttpClient client, Guid userId)
    {
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        var csrf = await client.GetFromJsonAsync<Csrf>("/auth/csrf", Cancellation);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
    }

    private static string SourcePath(Guid ws, Guid source) => $"/workspaces/{ws}/knowledge-sources/{source}";
    private static string UploadPath(Guid ws, Guid source, string fileName, Guid key) =>
        $"{SourcePath(ws, source)}/documents?fileName={Uri.EscapeDataString(fileName)}&clientRequestId={key}";

    private static async Task<string> AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Cancellation);
            Assert.True(response.StatusCode == expected,
                $"Expected {(int)expected}, got {(int)response.StatusCode} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
            Assert.True(response.Headers.CacheControl?.NoStore == true, $"Missing no-store on {(int)response.StatusCode}");
            return body;
        }
    }

    private static KnowledgeDocumentView Document(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(DocumentFields, document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        // Index status is shown for READY documents only; these hosts run without an embedding provider.
        var index = document.RootElement.GetProperty("index");
        if (document.RootElement.GetProperty("status").GetString() == "READY")
            Assert.Equal("DISABLED", index.GetProperty("status").GetString());
        else
            Assert.Equal(JsonValueKind.Null, index.ValueKind);
        if (document.RootElement.GetProperty("latestJob").ValueKind == JsonValueKind.Object)
            Assert.Equal(JobFields, document.RootElement.GetProperty("latestJob").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        return JsonSerializer.Deserialize<KnowledgeDocumentView>(json, JsonSerializerOptions.Web)!;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, byte[] content, string? contentType)
    {
        var body = new ByteArrayContent(content);
        if (contentType is not null) body.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return client.PostAsync(path, body, Cancellation);
    }

    private async Task ProcessAllAsync(WebApplicationFactory<Program> factory)
    {
        // Only this class's documents exist in its database, so draining every due job is safe here.
        while (true)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<IngestionProcessor>().ProcessNextAsync(Cancellation) is null) return;
        }
    }

    [Fact]
    public async Task Upload_registers_a_pending_document_with_a_queued_job_and_replays_exactly()
    {
        var owner = await UserAsync();
        var agent = await UserAsync();
        var ws = await WorkspaceAsync(owner, (agent, WorkspaceRole.AGENT));
        var source = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, agent);
        var key = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("# Returns\n\nWithin 30 days. Çok güzel.");

        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var created = await UploadAsync(client, UploadPath(ws, source, "iade politikası.md", key), content, "text/markdown; charset=UTF-8");
        var createdBody = await created.Content.ReadAsStringAsync(Cancellation);
        Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
        Assert.True(created.Headers.CacheControl?.NoStore);
        var document = Document(createdBody);
        Assert.Equal($"{SourcePath(ws, source)}/documents/{document.Id}", created.Headers.Location!.OriginalString);
        Assert.Equal((ws, source, "UPLOAD", "PENDING", agent, key), (document.WorkspaceId, document.KnowledgeSourceId, document.Origin,
            document.Status, document.CreatedByUserId, document.ClientRequestId));
        Assert.Equal(("iade politikası.md", "text/markdown", (long)content.Length), (document.FileName, document.ContentType, document.SizeBytes));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), document.ContentSha256);
        Assert.Equal((0, 0, (string?)null, (string?)null), (document.ChunkCount, document.CharacterCount, document.FailureReason, document.SourceUrl));
        Assert.Equal(("QUEUED", 0, IngestionJob.DefaultMaxAttempts, (string?)null), (document.LatestJob!.Status, document.LatestJob.Attempts,
            document.LatestJob.MaxAttempts, document.LatestJob.LastError));
        Assert.InRange(document.CreatedAt, before, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(storageRoot, ws.ToString("N"), document.Id.ToString("N")), Cancellation));

        var detail = Document(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"{SourcePath(ws, source)}/documents/{document.Id}", Cancellation)));
        Assert.Equal(document with { LatestJob = null }, detail with { LatestJob = null });
        Assert.Equal(document.LatestJob, detail.LatestJob);

        // Exact replay returns the same document; any difference under the same key is a conflict.
        var replay = Document(await AssertStatusAsync(HttpStatusCode.OK,
            await UploadAsync(client, UploadPath(ws, source, "iade politikası.md", key), content, "text/markdown")));
        Assert.Equal(document.Id, replay.Id);
        await AssertStatusAsync(HttpStatusCode.Conflict, await UploadAsync(client, UploadPath(ws, source, "other.md", key), content, "text/markdown"));
        await AssertStatusAsync(HttpStatusCode.Conflict, await UploadAsync(client, UploadPath(ws, source, "iade politikası.md", key), [.. content, 32], "text/markdown"));
        await AssertStatusAsync(HttpStatusCode.Conflict, await UploadAsync(client, UploadPath(ws, source, "iade politikası.md", key), content, "text/plain"));

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.KnowledgeDocuments.CountAsync(d => d.KnowledgeSourceId == source, Cancellation));
        Assert.Single(Directory.GetFiles(Path.Combine(storageRoot, ws.ToString("N"))));
    }

    [Fact]
    public async Task Upload_rejects_invalid_names_types_bytes_sizes_and_wrong_or_disabled_sources()
    {
        var owner = await UserAsync();
        var ws = await WorkspaceAsync(owner);
        var file = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var text = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Text");
        var disabled = await SourceAsync(ws, KnowledgeSourceKind.FILE, enabled: false);
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);
        var ok = "hello"u8.ToArray();

        foreach (var (name, content, type) in new (string, byte[], string?)[]
                 {
                     ("", ok, "text/plain"), (" a.txt", ok, "text/plain"), ("a/b.txt", ok, "text/plain"), ("..", ok, "text/plain"),
                     ("a\u0001.txt", ok, "text/plain"), (new string('a', 256), ok, "text/plain"),
                     ("a.txt", ok, null), ("a.html", ok, "text/html"), ("a.png", ok, "image/png"), ("a.txt", ok, "text/plain; charset=latin1"),
                     ("a.txt", ok, "text/plain; broken"), ("a.txt", [], "text/plain"), ("a.txt", [0xC3, 0x28], "text/plain"),
                     ("a.txt", [0x61, 0x00], "text/plain"), ("a.pdf", ok, "application/pdf")
                 })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await UploadAsync(client, UploadPath(ws, file, name, Guid.NewGuid()), content, type));
        await AssertStatusAsync(HttpStatusCode.BadRequest,
            await UploadAsync(client, $"{SourcePath(ws, file)}/documents?fileName=a.txt", ok, "text/plain"));
        await AssertStatusAsync(HttpStatusCode.BadRequest,
            await UploadAsync(client, $"{SourcePath(ws, file)}/documents?fileName=a.txt&clientRequestId={Guid.Empty}", ok, "text/plain"));

        var large = new byte[KnowledgeDocument.MaxUploadBytes + 1];
        Array.Fill(large, (byte)'a');
        await AssertStatusAsync(HttpStatusCode.RequestEntityTooLarge, await UploadAsync(client, UploadPath(ws, file, "big.txt", Guid.NewGuid()), large, "text/plain"));
        // Without Content-Length the reader still stops one byte past the limit.
        using (var request = new HttpRequestMessage(HttpMethod.Post, UploadPath(ws, file, "big.txt", Guid.NewGuid()))
               { Content = new StreamContent(new MemoryStream(large)) })
        {
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            request.Headers.TransferEncodingChunked = true;
            await AssertStatusAsync(HttpStatusCode.RequestEntityTooLarge, await client.SendAsync(request, Cancellation));
        }

        await AssertStatusAsync(HttpStatusCode.BadRequest, await UploadAsync(client, UploadPath(ws, text, "a.txt", Guid.NewGuid()), ok, "text/plain"));
        await AssertStatusAsync(HttpStatusCode.Conflict, await UploadAsync(client, UploadPath(ws, disabled, "a.txt", Guid.NewGuid()), ok, "text/plain"));
        var pdf = Document(await AssertStatusAsync(HttpStatusCode.Created,
            await UploadAsync(client, UploadPath(ws, file, "ok.pdf", Guid.NewGuid()), KnowledgeTestData.Pdf("x"), "Application/PDF")));
        Assert.Equal("application/pdf", pdf.ContentType);

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.KnowledgeDocuments.CountAsync(d => d.WorkspaceId == ws, Cancellation));
        Assert.Single(Directory.GetFiles(Path.Combine(storageRoot, ws.ToString("N"))));
    }

    [Fact]
    public async Task Ingestion_requests_snapshot_text_and_website_sources_and_documents_become_readable_after_processing()
    {
        var owner = await UserAsync();
        var ws = await WorkspaceAsync(owner);
        var text = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Refunds are processed within five business days.");
        var file = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var disabled = await SourceAsync(ws, KnowledgeSourceKind.WEBSITE, url: "https://example.com/", enabled: false);
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);
        var key = Guid.NewGuid();

        using var created = await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = key }, Cancellation);
        var document = Document(await created.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(("TEXT", "PENDING", (string?)null, (string?)null), (document.Origin, document.Status, document.FileName, document.ContentType));
        Assert.Equal(document.Id, Document(await AssertStatusAsync(HttpStatusCode.OK,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = key }, Cancellation))).Id);
        await AssertStatusAsync(HttpStatusCode.Conflict,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.Conflict,
            await client.PostAsJsonAsync($"{SourcePath(ws, file)}/ingestions", new { clientRequestId = key }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.BadRequest,
            await client.PostAsJsonAsync($"{SourcePath(ws, file)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.Conflict,
            await client.PostAsJsonAsync($"{SourcePath(ws, disabled)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation));
        foreach (var body in new object[] { new { }, new { clientRequestId = Guid.Empty }, new { clientRequestId = key, extra = 1 } })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", body, Cancellation));

        await ProcessAllAsync(factory);
        var ready = Document(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"{SourcePath(ws, text)}/documents/{document.Id}", Cancellation)));
        Assert.Equal(("READY", 1, "text/plain", "SUCCEEDED", 1), (ready.Status, ready.ChunkCount, ready.ContentType, ready.LatestJob!.Status, ready.LatestJob.Attempts));
        Assert.NotNull(ready.ProcessedAt);

        using var chunks = JsonDocument.Parse(await AssertStatusAsync(HttpStatusCode.OK,
            await client.GetAsync($"{SourcePath(ws, text)}/documents/{document.Id}/chunks", Cancellation)));
        Assert.Equal(["items", "nextAfterSequence"], chunks.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var chunk = Assert.Single(chunks.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(["content", "createdAt", "documentId", "id", "sequence", "startOffset", "workspaceId"],
            chunk.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("Refunds are processed within five business days.", 1, 0), (chunk.GetProperty("content").GetString(),
            chunk.GetProperty("sequence").GetInt32(), chunk.GetProperty("startOffset").GetInt32()));
        foreach (var query in new[] { "?take=0", "?take=101", "?afterSequence=-1" })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.GetAsync($"{SourcePath(ws, text)}/documents/{document.Id}/chunks{query}", Cancellation));
        using var empty = JsonDocument.Parse(await AssertStatusAsync(HttpStatusCode.OK,
            await client.GetAsync($"{SourcePath(ws, text)}/documents/{document.Id}/chunks?afterSequence=1", Cancellation)));
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("nextAfterSequence").ValueKind);
    }

    [Fact]
    public async Task Lists_page_documents_per_source_and_retry_requeues_only_failed_documents()
    {
        var owner = await UserAsync();
        var ws = await WorkspaceAsync(owner);
        var file = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var other = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add(Document(await AssertStatusAsync(HttpStatusCode.Created,
                await UploadAsync(client, UploadPath(ws, file, $"f{i}.txt", Guid.NewGuid()), Encoding.UTF8.GetBytes($"File {i}"), "text/plain"))).Id);
        await AssertStatusAsync(HttpStatusCode.Created, await UploadAsync(client, UploadPath(ws, other, "o.txt", Guid.NewGuid()), "x"u8.ToArray(), "text/plain"));

        using var first = JsonDocument.Parse(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"{SourcePath(ws, file)}/documents?take=2", Cancellation)));
        Assert.Equal(ids.Take(2), first.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
        var next = first.RootElement.GetProperty("nextAfter").GetGuid();
        using var second = JsonDocument.Parse(await AssertStatusAsync(HttpStatusCode.OK,
            await client.GetAsync($"{SourcePath(ws, file)}/documents?take=2&after={next}", Cancellation)));
        Assert.Equal([ids[2]], second.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("nextAfter").ValueKind);
        foreach (var query in new[] { "?take=0", "?take=101", $"?after={Guid.Empty}" })
            await AssertStatusAsync(HttpStatusCode.BadRequest, await client.GetAsync($"{SourcePath(ws, file)}/documents{query}", Cancellation));

        // Pending and ready documents cannot be retried; a failed one can, exactly once while its new job is active.
        var retry = $"{SourcePath(ws, file)}/documents/{ids[0]}/retry";
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(retry, new { }, Cancellation));
        File.Delete(Path.Combine(storageRoot, ws.ToString("N"), ids[0].ToString("N")));
        await ProcessAllAsync(factory);
        var failed = Document(await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"{SourcePath(ws, file)}/documents/{ids[0]}", Cancellation)));
        Assert.Equal(("FAILED", "STORAGE_MISSING", "FAILED", "STORAGE_MISSING"), (failed.Status, failed.FailureReason, failed.LatestJob!.Status, failed.LatestJob.LastError));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync(retry, new { extra = true }, Cancellation));
        var requeued = Document(await AssertStatusAsync(HttpStatusCode.OK, await client.PostAsJsonAsync(retry, new { }, Cancellation)));
        Assert.Equal(("PENDING", (string?)null, "QUEUED", 0), (requeued.Status, requeued.FailureReason, requeued.LatestJob!.Status, requeued.LatestJob.Attempts));
        Assert.NotEqual(failed.LatestJob.Id, requeued.LatestJob.Id);
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync(retry, new { }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.Conflict, await client.PostAsJsonAsync($"{SourcePath(ws, file)}/documents/{ids[1]}/retry", new { }, Cancellation));
    }

    [Fact]
    public async Task Tenant_isolation_authentication_and_csrf_apply_to_every_route()
    {
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var ws = await WorkspaceAsync(owner);
        var otherWs = await WorkspaceAsync(outsider);
        var file = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var text = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Private");
        var otherSource = await SourceAsync(otherWs, KnowledgeSourceKind.FILE);
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);
        var document = Document(await AssertStatusAsync(HttpStatusCode.Created,
            await UploadAsync(client, UploadPath(ws, file, "secret.txt", Guid.NewGuid()), "secret"u8.ToArray(), "text/plain")));
        var docPath = $"{SourcePath(ws, file)}/documents/{document.Id}";

        // A nonmember cannot see or act on anything, even with valid IDs; wrong source/workspace routes are 404 too.
        await AuthenticateAsync(client, outsider);
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(docPath, Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"{docPath}/chunks", Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"{SourcePath(ws, file)}/documents", Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.PostAsJsonAsync($"{docPath}/retry", new { }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await UploadAsync(client, UploadPath(ws, file, "x.txt", Guid.NewGuid()), "x"u8.ToArray(), "text/plain"));
        // Membership is decided before the name, media type, size or bytes are examined.
        await AssertStatusAsync(HttpStatusCode.NotFound, await UploadAsync(client, UploadPath(ws, file, "a/b", Guid.NewGuid()), [0xC3], "image/png"));
        await AssertStatusAsync(HttpStatusCode.NotFound,
            await UploadAsync(client, UploadPath(ws, file, "big.txt", Guid.NewGuid()), new byte[KnowledgeDocument.MaxUploadBytes + 1], "text/plain"));
        await AssertStatusAsync(HttpStatusCode.NotFound,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"{SourcePath(otherWs, otherSource)}/documents/{document.Id}", Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"{SourcePath(otherWs, file)}/documents", Cancellation));
        await AuthenticateAsync(client, owner);
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"{SourcePath(ws, text)}/documents/{document.Id}", Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync($"{SourcePath(ws, Guid.NewGuid())}/documents", Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await UploadAsync(client, UploadPath(ws, otherSource, "x.txt", Guid.NewGuid()), "x"u8.ToArray(), "text/plain"));

        // CSRF is required for writes and checked before the body is used.
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AssertStatusAsync(HttpStatusCode.BadRequest, await UploadAsync(client, UploadPath(ws, file, "x.txt", Guid.NewGuid()), "x"u8.ToArray(), "text/plain"));
        await AssertStatusAsync(HttpStatusCode.BadRequest,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.PostAsJsonAsync($"{docPath}/retry", new { }, Cancellation));

        using var anonymous = Client(factory);
        await AssertStatusAsync(HttpStatusCode.Unauthorized, await anonymous.GetAsync(docPath, Cancellation));
        await AssertStatusAsync(HttpStatusCode.Unauthorized, await UploadAsync(anonymous, UploadPath(ws, file, "x.txt", Guid.NewGuid()), "x"u8.ToArray(), "text/plain"));

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.KnowledgeDocuments.CountAsync(d => d.WorkspaceId == ws || d.WorkspaceId == otherWs, Cancellation));
    }

    [Fact]
    public async Task Delete_removes_the_document_its_chunks_jobs_and_file()
    {
        var owner = await UserAsync();
        var agent = await UserAsync();
        var outsider = await UserAsync();
        var ws = await WorkspaceAsync(owner, (agent, WorkspaceRole.AGENT));
        await WorkspaceAsync(outsider);
        var file = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var text = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Snapshot text");
        await using var factory = Factory();
        using var client = Client(factory);
        await AuthenticateAsync(client, agent);
        var upload = Document(await AssertStatusAsync(HttpStatusCode.Created,
            await UploadAsync(client, UploadPath(ws, file, "delete.txt", Guid.NewGuid()), "Delete me"u8.ToArray(), "text/plain")));
        var snapshot = Document(await AssertStatusAsync(HttpStatusCode.Created,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation)));
        await ProcessAllAsync(factory);
        var path = $"{SourcePath(ws, file)}/documents/{upload.Id}";
        var stored = Path.Combine(storageRoot, ws.ToString("N"), upload.Id.ToString("N"));
        Assert.True(File.Exists(stored));

        await AuthenticateAsync(client, outsider);
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.DeleteAsync(path, Cancellation));
        await AuthenticateAsync(client, agent);
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.DeleteAsync($"{SourcePath(ws, text)}/documents/{upload.Id}", Cancellation));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AssertStatusAsync(HttpStatusCode.BadRequest, await client.DeleteAsync(path, Cancellation));
        await AuthenticateAsync(client, agent);
        Assert.True(File.Exists(stored));

        await AssertStatusAsync(HttpStatusCode.NoContent, await client.DeleteAsync(path, Cancellation));
        Assert.False(File.Exists(stored));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.GetAsync(path, Cancellation));
        await AssertStatusAsync(HttpStatusCode.NotFound, await client.DeleteAsync(path, Cancellation));
        // Deleting the only snapshot frees the source for a new one.
        await AssertStatusAsync(HttpStatusCode.NoContent, await client.DeleteAsync($"{SourcePath(ws, text)}/documents/{snapshot.Id}", Cancellation));
        await AssertStatusAsync(HttpStatusCode.Created,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation));

        await using var db = fixture.CreateContext();
        Assert.False(await db.KnowledgeDocuments.AnyAsync(d => d.Id == upload.Id || d.Id == snapshot.Id, Cancellation));
        Assert.False(await db.KnowledgeDocumentChunks.AnyAsync(c => c.DocumentId == upload.Id || c.DocumentId == snapshot.Id, Cancellation));
        Assert.False(await db.IngestionJobs.AnyAsync(j => j.DocumentId == upload.Id || j.DocumentId == snapshot.Id, Cancellation));
    }

    [Fact]
    public async Task Workspace_quotas_reject_uploads_and_snapshots_until_documents_are_deleted()
    {
        var owner = await UserAsync();
        var ws = await WorkspaceAsync(owner);
        var file = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        var text = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Quota text");
        await using var factory = Factory(("KnowledgeLimits:MaxStorageBytesPerWorkspace", "10"), ("KnowledgeLimits:MaxDocumentsPerWorkspace", "2"));
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);

        var first = Document(await AssertStatusAsync(HttpStatusCode.Created,
            await UploadAsync(client, UploadPath(ws, file, "a.txt", Guid.NewGuid()), "123456"u8.ToArray(), "text/plain")));
        var bytes = await AssertStatusAsync(HttpStatusCode.Conflict,
            await UploadAsync(client, UploadPath(ws, file, "b.txt", Guid.NewGuid()), "12345"u8.ToArray(), "text/plain"));
        Assert.Contains("knowledge-quota-exceeded", bytes);
        await AssertStatusAsync(HttpStatusCode.Created,
            await UploadAsync(client, UploadPath(ws, file, "c.txt", Guid.NewGuid()), "1234"u8.ToArray(), "text/plain"));
        Assert.Contains("knowledge-quota-exceeded", await AssertStatusAsync(HttpStatusCode.Conflict,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation)));
        // A rejected upload leaves no file behind.
        Assert.Equal(2, Directory.GetFiles(Path.Combine(storageRoot, ws.ToString("N"))).Length);

        // A declared size over the remaining byte quota is refused before the body is read.
        await AssertStatusAsync(HttpStatusCode.NoContent, await client.DeleteAsync($"{SourcePath(ws, file)}/documents/{first.Id}", Cancellation));
        Assert.Contains("knowledge-quota-exceeded", await AssertStatusAsync(HttpStatusCode.Conflict,
            await UploadAsync(client, UploadPath(ws, file, "d.txt", Guid.NewGuid()), "1234567"u8.ToArray(), "text/plain")));
        await AssertStatusAsync(HttpStatusCode.Created,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation));
        // At the document limit, refreshing a source that has a READY snapshot still works: it replaces, not adds.
        await ProcessAllAsync(factory);
        await AssertStatusAsync(HttpStatusCode.Created,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation));
    }

    [Fact]
    public async Task Knowledge_writes_are_rate_limited_per_user()
    {
        var owner = await UserAsync();
        var other = await UserAsync();
        var ws = await WorkspaceAsync(owner, (other, WorkspaceRole.ADMIN));
        var file = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        await using var factory = Factory(("KnowledgeLimits:WritesPerMinutePerUser", "2"));
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);
        for (var i = 0; i < 2; i++)
            await AssertStatusAsync(HttpStatusCode.Created,
                await UploadAsync(client, UploadPath(ws, file, $"{i}.txt", Guid.NewGuid()), "x"u8.ToArray(), "text/plain"));
        using (var limited = await UploadAsync(client, UploadPath(ws, file, "2.txt", Guid.NewGuid()), "x"u8.ToArray(), "text/plain"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.CacheControl?.NoStore);
            Assert.NotNull(limited.Headers.RetryAfter);
        }
        await AssertStatusAsync(HttpStatusCode.TooManyRequests, await client.DeleteAsync($"{SourcePath(ws, file)}/documents/{Guid.NewGuid()}", Cancellation));
        await AssertStatusAsync(HttpStatusCode.TooManyRequests,
            await client.PostAsJsonAsync($"/workspaces/{ws}/knowledge-sources", new { kind = "FILE", name = "More" }, Cancellation));
        // Reads are not limited, and another user has an independent budget.
        await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"{SourcePath(ws, file)}/documents", Cancellation));
        await AuthenticateAsync(client, other);
        await AssertStatusAsync(HttpStatusCode.Created,
            await UploadAsync(client, UploadPath(ws, file, "other.txt", Guid.NewGuid()), "x"u8.ToArray(), "text/plain"));
    }

    [Fact]
    public async Task Reads_and_snapshot_requests_work_without_a_storage_root()
    {
        var owner = await UserAsync();
        var ws = await WorkspaceAsync(owner);
        var text = await SourceAsync(ws, KnowledgeSourceKind.TEXT, "Needs no file storage");
        var file = await SourceAsync(ws, KnowledgeSourceKind.FILE);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
            builder.ConfigureServices(services => services.AddAuthentication("test-only")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test-only", _ => { }));
        });
        using var client = Client(factory);
        await AuthenticateAsync(client, owner);
        var document = Document(await AssertStatusAsync(HttpStatusCode.Created,
            await client.PostAsJsonAsync($"{SourcePath(ws, text)}/ingestions", new { clientRequestId = Guid.NewGuid() }, Cancellation)));
        await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"{SourcePath(ws, text)}/documents", Cancellation));
        await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"{SourcePath(ws, text)}/documents/{document.Id}", Cancellation));
        // Only storing bytes needs the root; that misconfiguration is a server error and stores nothing.
        using var upload = await UploadAsync(client, UploadPath(ws, file, "a.txt", Guid.NewGuid()), "x"u8.ToArray(), "text/plain");
        Assert.Equal(HttpStatusCode.InternalServerError, upload.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.KnowledgeDocuments.AnyAsync(d => d.KnowledgeSourceId == file, Cancellation));
    }

    private sealed record Csrf(string Token);
}
