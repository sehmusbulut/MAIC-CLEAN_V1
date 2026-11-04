using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MAIC.Application.AI;
using MAIC.Domain.AI;
using MAIC.Domain.Identity;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>
/// Deterministic bag-of-words embedder for tests and the retrieval eval: folded (Turkish-aware) word stems hashed into
/// 256 dimensions. Not a model; it makes ranking reproducible so eval fixtures can guard the pipeline.
/// </summary>
internal sealed class HashingEmbeddings(EmbeddingSpace space) : IEmbeddingGenerator
{
    public EmbeddingSpace Space { get; } = space;
    public int MaxBatchSize => 16;
    public int MaxInputLength => KnowledgeChunker.MaxChunkLength;
    public int Calls;

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Calls);
        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(inputs.Select(text => (ReadOnlyMemory<float>)Embed(text, Space.Dimensions)).ToArray());
    }

    public static float[] Embed(string text, int dimensions)
    {
        var vector = new float[dimensions];
        vector[^1] = 0.05f; // never a zero vector
        var folded = text.ToLowerInvariant().Replace('ı', 'i').Replace('ş', 's').Replace('ğ', 'g').Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c');
        foreach (var word in folded.Split(c => !char.IsLetterOrDigit(c)))
        {
            if (word.Length < 3) continue;
            var stem = word.Length > 5 ? word[..5] : word;
            var hash = 17;
            foreach (var c in stem) hash = unchecked(hash * 31 + c);
            vector[(int)((uint)hash % (uint)(dimensions - 1))] += 1;
        }
        return vector;
    }
}

internal static class StringSplitExtensions
{
    public static string[] Split(this string text, Func<char, bool> separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (separator(c)) { if (current.Length > 0) parts.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts.ToArray();
    }
}

/// <summary>A small bilingual support FAQ with questions phrased differently from the answers (the retrieval eval set).</summary>
internal static class RetrievalEvalFixtures
{
    public static readonly (string Key, string Text)[] Documents =
    [
        ("refund", "İade politikası: Ürünü teslim aldıktan sonra 14 gün içinde iade edebilirsiniz. İade onaylanınca ücret 5 iş günü içinde kartınıza geri ödenir."),
        ("shipping", "Kargo ve teslimat: Siparişler 1-3 iş günü içinde kargoya verilir. Teslimat süresi şehre göre 2-4 gündür, kargo takip numarası e-posta ile gönderilir."),
        ("password", "Şifre sıfırlama: Giriş ekranında 'Şifremi unuttum' bağlantısına tıklayın, e-posta adresinizi girin ve gelen bağlantıyla yeni şifre belirleyin."),
        ("invoice", "Fatura: Kurumsal fatura için vergi numaranızı hesap ayarlarındaki fatura bilgileri bölümüne ekleyin; faturalar her ay e-posta ile gönderilir."),
        ("warranty", "Garanti: Tüm elektronik ürünler 2 yıl garantilidir. Arıza durumunda garanti belgesiyle yetkili servise başvurun."),
        ("hours", "Çalışma saatleri: Müşteri destek ekibimiz hafta içi 09:00-18:00 arasında, cumartesi 10:00-14:00 arasında hizmet verir."),
        ("cancel", "Order cancellation: You can cancel an order free of charge until it ships. Open My Orders, choose the order and press Cancel."),
        ("payment", "Payment methods: We accept credit cards, debit cards and bank transfer. Installments are available for credit cards over 1000 TL."),
        ("privacy", "Privacy: We never sell personal data. You can request deletion of your account data from the privacy settings page."),
        ("exchange", "Exchange: Wrong size? Request an exchange within 30 days; the replacement is shipped after we receive the original item.")
    ];

    public static readonly (string Question, string Expected)[] Questions =
    [
        ("Ürünü iade edersem param ne zaman geri ödenir?", "refund"),
        ("Siparişim ne zaman kargoya verilir, teslimat kaç gün sürer?", "shipping"),
        ("Şifremi unuttum, nasıl yeni şifre belirlerim?", "password"),
        ("Kurumsal fatura için vergi numarası nereye eklenir?", "invoice"),
        ("Elektronik ürünlerin garanti süresi nedir?", "warranty"),
        ("Destek ekibi cumartesi çalışıyor mu, saatler nedir?", "hours"),
        ("How can I cancel my order before it ships?", "cancel"),
        ("Do you accept bank transfer or installments on credit cards?", "payment"),
        ("How do I request deletion of my account data?", "privacy"),
        ("The size is wrong, can I get an exchange?", "exchange")
    ];
}

/// <summary>Retrieval against real PostgreSQL/pgvector: authoritative reload, scope, coverage, quotas, the preview API and the eval set.</summary>
public sealed class KnowledgeRetrievalIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private sealed record Knowledge(Guid Owner, Guid Workspace, Dictionary<string, (Guid Source, Guid Document, Guid Chunk)> Items);

    /// <summary>A workspace with one TEXT source and READY document (one chunk) per fixture text.</summary>
    private async Task<Knowledge> KnowledgeAsync(IEnumerable<(string Key, string Text)> texts)
    {
        var owner = await PgVectorSeed.UserAsync(fixture);
        var workspace = Guid.CreateVersion7();
        var items = new Dictionary<string, (Guid, Guid, Guid)>();
        foreach (var (key, text) in texts)
        {
            var ids = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            await PgVectorSeed.KeysAsync(fixture, owner, [new KnowledgeVectorKey(workspace, ids.Item1, ids.Item2, ids.Item3, 1)]);
            await using var db = fixture.CreateContext();
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeDocumentChunks" SET "Content" = {0} WHERE "Id" = {1}""", [text, ids.Item3], Cancellation);
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Name" = {0} WHERE "Id" = {1}""", [$"FAQ {key}", ids.Item1], Cancellation);
            items[key] = ids;
        }
        await using (var db = fixture.CreateContext())
            if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspace, Cancellation))
            {
                db.WorkspaceMembers.Add(new WorkspaceMember(workspace, owner, WorkspaceRole.OWNER));
                await db.SaveChangesAsync(Cancellation);
            }
        return new Knowledge(owner, workspace, items);
    }

    private async Task<Guid> AgentAsync(Guid workspace, bool enabled = true, AiKnowledgeScope scope = AiKnowledgeScope.ALL_SOURCES, params Guid[] sources)
    {
        await using var db = fixture.CreateContext();
        var agent = new AiAgent(workspace, new AiAgentSettings("Support", enabled, null, "tr", AiModelTier.BALANCED, scope, sources, true, false, null),
            DateTimeOffset.UtcNow);
        db.AiAgents.Add(agent);
        await db.SaveChangesAsync(Cancellation);
        return agent.Id;
    }

    private async Task<EmbeddingSpace> IndexAsync(Knowledge knowledge, HashingEmbeddings? embeddings = null)
    {
        var space = embeddings?.Space ?? new EmbeddingSpace($"rt-{Guid.NewGuid():N}"[..20], 256);
        await PgVectorSeed.RegisterAsync(fixture, space);
        embeddings ??= new HashingEmbeddings(space);
        await using var db = fixture.CreateContext();
        await new KnowledgeIndexStore(db).DiscoverAsync(space, DateTimeOffset.UtcNow, 100_000, Cancellation);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "KnowledgeIndexStates" WHERE "SpaceKey" = {0} AND "WorkspaceId" <> {1}""",
            [space.Key, knowledge.Workspace], Cancellation);
        var processor = new KnowledgeIndexProcessor(new KnowledgeIndexStore(db), new KnowledgeVectorIndex(embeddings, new PgVectorKnowledgeStore(db)),
            TimeProvider.System);
        while (await processor.ProcessNextAsync(Cancellation) is not null) { }
        return space;
    }

    private static KnowledgeRetriever Retriever(MaicDbContext db, IEmbeddingGenerator embeddings, RetrievalSettings? settings = null)
    {
        settings ??= new RetrievalSettings { MaxQueriesPerWorkspacePerDay = 1000 };
        return new KnowledgeRetriever(settings, new KnowledgeRetrievalStore(db), new KnowledgeVectorIndex(embeddings, new PgVectorKnowledgeStore(db)),
            new KnowledgeQueryGate(settings, TimeProvider.System), TimeProvider.System);
    }

    [Fact]
    public async Task The_eval_set_retrieves_the_right_document_for_paraphrased_questions()
    {
        var knowledge = await KnowledgeAsync(RetrievalEvalFixtures.Documents);
        var agent = await AgentAsync(knowledge.Workspace);
        var space = await IndexAsync(knowledge);
        await using var db = fixture.CreateContext();
        var retriever = Retriever(db, new HashingEmbeddings(space), new RetrievalSettings { MaxQueriesPerWorkspacePerDay = 1000, QueriesPerMinutePerVisitor = 100 });

        var (top1, top3) = (0, 0);
        foreach (var (question, expected) in RetrievalEvalFixtures.Questions)
        {
            var result = await retriever.RetrieveAsync(new RetrievalRequest(knowledge.Workspace, agent, question, "eval"), Cancellation);
            Assert.Equal(RetrievalOutcome.FOUND, result.Outcome);
            var documents = result.Passages.Select(p => p.Citation.DocumentId).ToArray();
            var want = knowledge.Items[expected].Document;
            if (documents.FirstOrDefault() == want) top1++;
            if (documents.Take(3).Contains(want)) top3++;
        }
        // Guards the pipeline (reload, caps, ordering), not a model: every question finds its document in the top 3.
        Assert.Equal(RetrievalEvalFixtures.Questions.Length, top3);
        Assert.True(top1 >= RetrievalEvalFixtures.Questions.Length - 1, $"recall@1 {top1}/{RetrievalEvalFixtures.Questions.Length}");
    }

    [Fact]
    public async Task Reload_honours_workspace_ready_enabled_and_selection_and_never_trusts_vector_ids()
    {
        var knowledge = await KnowledgeAsync([("a", "refund policy alpha"), ("b", "refund policy beta"), ("c", "refund policy gamma")]);
        var other = await KnowledgeAsync([("x", "refund policy foreign")]);
        var agent = await AgentAsync(knowledge.Workspace, scope: AiKnowledgeScope.SELECTED_SOURCES, sources: [knowledge.Items["a"].Source, knowledge.Items["b"].Source]);
        await using var db = fixture.CreateContext();
        var store = new KnowledgeRetrievalStore(db);
        var all = knowledge.Items.Values.Select(i => i.Chunk).Append(other.Items["x"].Chunk).ToArray();

        var selected = await store.LoadChunksAsync(knowledge.Workspace, agent, AiKnowledgeScope.SELECTED_SOURCES, all, Cancellation);
        Assert.Equal(new[] { knowledge.Items["a"].Chunk, knowledge.Items["b"].Chunk }.Order(), selected.Select(r => r.ChunkId).Order());
        var allAgent = await AgentAsync(knowledge.Workspace);
        // Another workspace's chunk ID never loads, whatever scope.
        Assert.DoesNotContain(await store.LoadChunksAsync(knowledge.Workspace, allAgent, AiKnowledgeScope.ALL_SOURCES, all, Cancellation),
            r => r.ChunkId == other.Items["x"].Chunk);
        // The agent is re-checked in the same statement: a scope read before an admin changed it loads nothing.
        Assert.Empty(await store.LoadChunksAsync(knowledge.Workspace, agent, AiKnowledgeScope.ALL_SOURCES, all, Cancellation));
        // Another agent's selection (same workspace) does not apply.
        var otherAgent = await AgentAsync(knowledge.Workspace, scope: AiKnowledgeScope.SELECTED_SOURCES, sources: [knowledge.Items["c"].Source]);
        Assert.Equal([knowledge.Items["c"].Chunk], (await store.LoadChunksAsync(knowledge.Workspace, otherAgent, AiKnowledgeScope.SELECTED_SOURCES, all, Cancellation))
            .Select(r => r.ChunkId));

        await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Enabled" = false WHERE "Id" = {0}""", [knowledge.Items["a"].Source], Cancellation);
        await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeDocuments" SET "Status" = 'SUPERSEDED' WHERE "Id" = {0}""", [knowledge.Items["b"].Document], Cancellation);
        Assert.Equal([knowledge.Items["c"].Chunk], (await store.LoadChunksAsync(knowledge.Workspace, allAgent, AiKnowledgeScope.ALL_SOURCES, all, Cancellation))
            .Select(r => r.ChunkId));
        // A disabled (or deleted) agent loads nothing, even with a scope read while it was enabled.
        await db.Database.ExecuteSqlRawAsync("""UPDATE "AiAgents" SET "Enabled" = false WHERE "Id" = {0}""", [allAgent], Cancellation);
        Assert.Empty(await store.LoadChunksAsync(knowledge.Workspace, allAgent, AiKnowledgeScope.ALL_SOURCES, all, Cancellation));

        var scope = (await store.LoadAgentScopeAsync(knowledge.Workspace, agent, Cancellation))!;
        Assert.Equal((true, AiKnowledgeScope.SELECTED_SOURCES), (scope.Enabled, scope.Scope));
        Assert.Equal([knowledge.Items["a"].Source], scope.DisabledSourceIds);
        Assert.Null(await store.LoadAgentScopeAsync(other.Workspace, agent, Cancellation)); // the agent of another workspace
    }

    [Fact]
    public async Task End_to_end_retrieval_reports_coverage_and_excludes_a_disabled_source_before_topK()
    {
        var knowledge = await KnowledgeAsync([("near", "refund refund refund policy"), ("far", "refund policy for exchanges and returns"), ("late", "unrelated opening hours")]);
        var agent = await AgentAsync(knowledge.Workspace);
        var space = await IndexAsync(knowledge);
        await using var db = fixture.CreateContext();
        var embeddings = new HashingEmbeddings(space);
        var ask = new RetrievalRequest(knowledge.Workspace, agent, "refund policy", "visitor");

        var found = await Retriever(db, embeddings).RetrieveAsync(ask, Cancellation);
        Assert.Equal(RetrievalOutcome.FOUND, found.Outcome);
        Assert.Equal(knowledge.Items["near"].Document, found.Passages[0].Citation.DocumentId);
        Assert.Equal(("FAQ near", 1), (found.Passages[0].Citation.SourceName, found.Passages[0].Citation.ChunkSequence));
        Assert.Equal(new KnowledgeCoverage(3, 0, 0), found.Coverage);

        // A disabled source is excluded inside the vector query: the next source's passage takes its place.
        await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Enabled" = false WHERE "Id" = {0}""", [knowledge.Items["near"].Source], Cancellation);
        var withoutNear = await Retriever(db, embeddings, new RetrievalSettings { TopK = 1, CandidateK = 1 }).RetrieveAsync(ask, Cancellation);
        Assert.Equal(knowledge.Items["far"].Document, Assert.Single(withoutNear.Passages).Citation.DocumentId);

        // Nothing relevant with complete coverage: NO_MATCH. A document not indexed yet: UNCERTAIN, never "no knowledge".
        var strict = new RetrievalSettings { MinScore = 0.99, MaxQueriesPerWorkspacePerDay = 1000 };
        Assert.Equal(RetrievalOutcome.NO_MATCH, (await Retriever(db, embeddings, strict).RetrieveAsync(ask, Cancellation)).Outcome);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "KnowledgeIndexStates" WHERE "SpaceKey" = {0} AND "DocumentId" = {1}""",
            [space.Key, knowledge.Items["late"].Document], Cancellation);
        var uncertain = await Retriever(db, embeddings, strict).RetrieveAsync(ask, Cancellation);
        Assert.Equal((RetrievalOutcome.UNCERTAIN, 1), (uncertain.Outcome, uncertain.Coverage!.PendingDocuments));
    }

    [Fact]
    public async Task Coverage_counts_unindexed_rejected_failed_and_still_ingesting_documents_on_real_rows()
    {
        var knowledge = await KnowledgeAsync([("ok", "indexed fine"), ("rejected", "partly rejected"), ("failedIndex", "not indexable"),
            ("stale", "processed again"), ("ingesting", "still ingesting"), ("failedIngest", "ingestion failed")]);
        var agent = await AgentAsync(knowledge.Workspace);
        var space = await IndexAsync(knowledge);
        await using var db = fixture.CreateContext();
        var store = new KnowledgeRetrievalStore(db);
        Assert.Equal(new KnowledgeCoverage(6, 0, 0), await store.CoverageAsync(space, knowledge.Workspace, agent, AiKnowledgeScope.ALL_SOURCES, Cancellation));

        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "KnowledgeIndexStates" SET "RejectedChunks" = 1 WHERE "SpaceKey" = {0} AND "DocumentId" = {1};
            UPDATE "KnowledgeIndexStates" SET "Status" = 'FAILED', "Failure" = 'EMBEDDING_REJECTED' WHERE "SpaceKey" = {0} AND "DocumentId" = {2};
            UPDATE "KnowledgeDocuments" SET "ProcessedAt" = "ProcessedAt" + interval '1 second' WHERE "Id" = {3};
            UPDATE "KnowledgeDocuments" SET "Status" = 'PENDING', "ProcessedAt" = NULL, "ChunkCount" = 0, "CharacterCount" = 0,
                "ContentType" = NULL, "SizeBytes" = NULL, "ContentSha256" = NULL WHERE "Id" = {4};
            UPDATE "KnowledgeDocuments" SET "Status" = 'FAILED', "FailureReason" = 'INVALID_CONTENT', "ChunkCount" = 0, "CharacterCount" = 0
                WHERE "Id" = {5};
            """, [space.Key, knowledge.Items["rejected"].Document, knowledge.Items["failedIndex"].Document, knowledge.Items["stale"].Document,
            knowledge.Items["ingesting"].Document, knowledge.Items["failedIngest"].Document], Cancellation);
        // READY: ok, rejected, failedIndex, stale. Pending: stale (not indexed for its new content), ingesting.
        // Partial: rejected, failedIndex, failedIngest.
        Assert.Equal(new KnowledgeCoverage(4, 2, 3), await store.CoverageAsync(space, knowledge.Workspace, agent, AiKnowledgeScope.ALL_SOURCES, Cancellation));

        // An agent whose only knowledge is still being ingested is uncertain, and nothing is embedded.
        var fresh = await KnowledgeAsync([("new", "new text")]);
        var freshAgent = await AgentAsync(fresh.Workspace);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "KnowledgeDocuments" SET "Status" = 'PENDING', "ProcessedAt" = NULL, "ChunkCount" = 0, "CharacterCount" = 0,
                "ContentType" = NULL, "SizeBytes" = NULL, "ContentSha256" = NULL WHERE "Id" = {0}
            """, [fresh.Items["new"].Document], Cancellation);
        var embeddings = new HashingEmbeddings(space);
        var result = await Retriever(db, embeddings).RetrieveAsync(new RetrievalRequest(fresh.Workspace, freshAgent, "new text", "v"), Cancellation);
        Assert.Equal((RetrievalOutcome.UNCERTAIN, 0), (result.Outcome, embeddings.Calls));
        // With no documents at all: no knowledge, not "no match".
        var bare = new Workspace("No knowledge yet");
        db.Workspaces.Add(bare);
        await db.SaveChangesAsync(Cancellation);
        var empty = await AgentAsync(bare.Id);
        var none = await Retriever(db, embeddings).RetrieveAsync(new RetrievalRequest(bare.Id, empty, "anything", "v"), Cancellation);
        Assert.Equal((RetrievalOutcome.NO_KNOWLEDGE, (RetrievalReason?)RetrievalReason.NO_DOCUMENTS), (none.Outcome, none.Reason));
    }

    [Fact]
    public async Task The_daily_query_quota_is_shared_across_instances()
    {
        var knowledge = await KnowledgeAsync([("a", "refund")]);
        await using var db = fixture.CreateContext();
        var (first, second) = (new KnowledgeRetrievalStore(db), new KnowledgeRetrievalStore(fixture.CreateContext()));
        var now = DateTimeOffset.UtcNow;
        Assert.True(await first.TryCountQueryAsync(knowledge.Workspace, 2, now, Cancellation));
        Assert.True(await second.TryCountQueryAsync(knowledge.Workspace, 2, now, Cancellation));
        Assert.False(await first.TryCountQueryAsync(knowledge.Workspace, 2, now, Cancellation));
        Assert.True(await first.TryCountQueryAsync(knowledge.Workspace, 2, now.AddDays(1), Cancellation)); // next UTC day
    }

    private WebApplicationFactory<Program> Factory(EmbeddingSpace? space = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
        builder.UseSetting("KnowledgeLimits:WritesPerMinutePerUser", "100000");
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication("test-only").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test-only", _ => { });
            if (space is not null) services.AddSingleton<IEmbeddingGenerator>(new HashingEmbeddings(space));
        });
    });

    private static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> factory, Guid user)
    {
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", user.ToString());
        var csrf = await client.GetFromJsonAsync<JsonElement>("/auth/csrf", Cancellation);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return client;
    }

    [Fact]
    public async Task Members_can_preview_retrieval_and_everyone_else_is_refused()
    {
        var knowledge = await KnowledgeAsync([("refund", "Refund policy: refunds are paid within five days.")]);
        var agent = await AgentAsync(knowledge.Workspace);
        var space = await IndexAsync(knowledge);
        var path = $"/workspaces/{knowledge.Workspace}/ai-agents/{agent}/retrieval-preview";

        await using (var factory = Factory(space))
        {
            using var client = await ClientAsync(factory, knowledge.Owner);
            using var response = await client.PostAsJsonAsync(path, new { query = "refund policy" }, Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
            Assert.Equal("FOUND", body.GetProperty("outcome").GetString());
            var passage = body.GetProperty("passages")[0];
            Assert.Equal("Refund policy: refunds are paid within five days.", passage.GetProperty("text").GetString());
            Assert.Equal(knowledge.Items["refund"].Document, passage.GetProperty("citation").GetProperty("documentId").GetGuid());

            // Nonmembers, other workspaces' agents, unknown fields and CSRF-less requests are refused.
            using var stranger = await ClientAsync(factory, await PgVectorSeed.UserAsync(fixture));
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync(path, new { query = "refund" }, Cancellation)).StatusCode);
            var foreign = await KnowledgeAsync([("x", "refund")]);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(
                $"/workspaces/{knowledge.Workspace}/ai-agents/{await AgentAsync(foreign.Workspace)}/retrieval-preview", new { query = "refund" }, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { query = "refund", workspaceId = Guid.NewGuid() }, Cancellation)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { query = new string('x', 4001) }, Cancellation)).StatusCode);
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { query = "refund" }, Cancellation)).StatusCode);
        }

        // Without an embedding provider the agent has no usable knowledge; nothing is called.
        await using (var factory = Factory())
        {
            using var client = await ClientAsync(factory, knowledge.Owner);
            var body = await (await client.PostAsJsonAsync(path, new { query = "refund policy" }, Cancellation)).Content.ReadFromJsonAsync<JsonElement>(Cancellation);
            Assert.Equal(("NO_KNOWLEDGE", "EMBEDDINGS_DISABLED"), (body.GetProperty("outcome").GetString(), body.GetProperty("reason").GetString()));
        }
    }
}
