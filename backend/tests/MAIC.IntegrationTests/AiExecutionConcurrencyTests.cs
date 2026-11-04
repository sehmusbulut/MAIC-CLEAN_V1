using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MAIC.Application.AI;
using MAIC.Domain.Conversations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class AiExecutionConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_duplicate_from_another_host_and_actor_claims_once_then_replays_the_same_answer()
    {
        var harness = new AiExecutionHarness(fixture, hold: true);
        var seed = await harness.SeedAsync();
        await using var firstHost = harness.Factory(seed);
        await using var secondHost = harness.Factory(seed);
        using var first = await AiExecutionHarness.ClientAsync(firstHost, seed.Member);
        using var second = await AiExecutionHarness.ClientAsync(secondHost, seed.Owner);
        using var timeout = Timeout();
        var running = AiExecutionHarness.ExecuteAsync(first, seed, timeout.Token);
        try
        {
            await harness.Generator.Entered.WaitAsync(timeout.Token);
            await AiExecutionHarness.StatusAsync(HttpStatusCode.Conflict, await AiExecutionHarness.ExecuteAsync(second, seed, timeout.Token));
            Assert.Equal(1, harness.Generator.Calls);
            harness.Generator.Release();
            var answer = await AiExecutionHarness.ReadAsync(await running);
            var replay = await AiExecutionHarness.ReadAsync(await AiExecutionHarness.ExecuteAsync(second, seed, timeout.Token));
            Assert.True(replay.GetProperty("replayed").GetBoolean());
            Assert.Equal(answer.GetProperty("executionId").GetGuid(), replay.GetProperty("executionId").GetGuid());
            Assert.Equal(1, harness.Generator.Calls);
            await using var db = fixture.CreateContext();
            Assert.Equal(new long[] { 1, 2 }, await db.Messages.Where(m => m.ConversationId == seed.Conversation)
                .OrderBy(m => m.Sequence).Select(m => m.Sequence).ToArrayAsync(Cancellation));
        }
        finally { harness.Generator.Release(); await DrainAsync(running); }
    }

    [Theory]
    [InlineData("agent_disabled")]
    [InlineData("scope_changed")]
    [InlineData("instructions_same_timestamp")]
    [InlineData("source_disabled")]
    [InlineData("document_superseded")]
    [InlineData("chunk_changed")]
    [InlineData("binding_aba")]
    [InlineData("resolve_reopen")]
    [InlineData("new_message")]
    [InlineData("human_assignment")]
    [InlineData("pending_handoff")]
    [InlineData("member_removed")]
    [InlineData("channel_disabled")]
    public async Task Mutation_while_provider_runs_finishes_without_a_lock_and_fences_the_stale_answer(string mutation)
    {
        var harness = new AiExecutionHarness(fixture, hold: true);
        var seed = await harness.SeedAsync();
        await using var factory = harness.Factory(seed);
        using var member = await AiExecutionHarness.ClientAsync(factory, seed.Member);
        using var owner = await AiExecutionHarness.ClientAsync(factory, seed.Owner);
        using var timeout = Timeout();
        var running = AiExecutionHarness.ExecuteAsync(member, seed, timeout.Token);
        try
        {
            await harness.Generator.Entered.WaitAsync(timeout.Token);
            // Completing this mutation before releasing generation also proves the external call holds no DB locks.
            await MutateAsync(mutation, seed, owner, timeout.Token);
            Assert.False(running.IsCompleted);
            harness.Generator.Release();
            using var response = await running;
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal(mutation == "member_removed" ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, response.StatusCode);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
                Assert.Equal("SUPERSEDED", result.GetProperty("outcome").GetString());
                Assert.Equal(JsonValueKind.Null, result.GetProperty("message").ValueKind);
            }
            await using var db = fixture.CreateContext();
            Assert.Empty(await db.Messages.Where(m => m.ConversationId == seed.Conversation && m.SenderKind == MessageSenderKind.AI).ToListAsync(Cancellation));
            Assert.Equal(1, harness.Generator.Calls);
            // The same trigger remains terminal even if a currently authorized member asks again.
            var replay = await AiExecutionHarness.ReadAsync(await AiExecutionHarness.ExecuteAsync(owner, seed), HttpStatusCode.Conflict);
            Assert.True(replay.GetProperty("replayed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, replay.GetProperty("message").ValueKind);
            Assert.Equal(1, harness.Generator.Calls);
        }
        finally { harness.Generator.Release(); await DrainAsync(running); }
    }

    [Fact]
    public async Task Expired_claim_stays_terminal_and_late_provider_completion_records_known_usage_without_publishing()
    {
        var harness = new AiExecutionHarness(fixture, hold: true);
        var seed = await harness.SeedAsync();
        await using var firstHost = harness.Factory(seed);
        await using var secondHost = harness.Factory(seed);
        using var first = await AiExecutionHarness.ClientAsync(firstHost, seed.Member);
        using var second = await AiExecutionHarness.ClientAsync(secondHost, seed.Owner);
        using var timeout = Timeout();
        var running = AiExecutionHarness.ExecuteAsync(first, seed, timeout.Token);
        try
        {
            await harness.Generator.Entered.WaitAsync(timeout.Token);
            await using (var expirer = fixture.CreateContext())
                await expirer.Database.ExecuteSqlRawAsync("""
                    UPDATE "AiExecutions" SET "CreatedAt" = clock_timestamp() - interval '2 minutes',
                        "ExpiresAt" = clock_timestamp() - interval '1 minute'
                    WHERE "WorkspaceId" = {0} AND "ConversationId" = {1}
                    """, [seed.Workspace, seed.Conversation], timeout.Token);
            var expired = await AiExecutionHarness.ReadAsync(await AiExecutionHarness.ExecuteAsync(second, seed, timeout.Token), HttpStatusCode.ServiceUnavailable);
            Assert.Equal("EXPIRED", expired.GetProperty("failure").GetString());
            await using var db = fixture.CreateContext();
            var before = await db.AiExecutions.AsNoTracking().SingleAsync(e => e.ConversationId == seed.Conversation, Cancellation);
            Assert.Null(before.ActualCost);
            harness.Generator.Release();
            var late = await AiExecutionHarness.ReadAsync(await running, HttpStatusCode.ServiceUnavailable);
            Assert.Equal(expired.GetProperty("executionId").GetGuid(), late.GetProperty("executionId").GetGuid());
            Assert.Equal("EXPIRED", late.GetProperty("failure").GetString());
            Assert.Equal(1, harness.Generator.Calls);
            var ledger = Assert.Single(await db.AiExecutions.Where(e => e.ConversationId == seed.Conversation).ToListAsync(Cancellation));
            Assert.True(ledger.ReservedCost > 0);
            Assert.Equal(before.ReservedCost, ledger.ReservedCost);
            Assert.Equal(40, ledger.InputTokens);
            Assert.Equal(12, ledger.OutputTokens);
            Assert.Equal(0.000076m, ledger.ActualCost);
            Assert.Equal(AiExecutionState.TERMINAL, ledger.State);
            Assert.Equal("UNAVAILABLE", ledger.Outcome);
            Assert.Equal("EXPIRED", ledger.Failure);
            Assert.Equal(before.CompletedAt, ledger.CompletedAt);
            Assert.Empty(await db.Messages.Where(m => m.ConversationId == seed.Conversation && m.SenderKind == MessageSenderKind.AI).ToListAsync(Cancellation));
            Assert.Empty(await db.HandoffRequests.Where(h => h.ConversationId == seed.Conversation).ToListAsync(Cancellation));
        }
        finally { harness.Generator.Release(); await DrainAsync(running); }
    }

    [Fact]
    public async Task Aborted_request_keeps_its_reservation_and_fences_a_provider_that_ignores_cancellation()
    {
        var harness = new AiExecutionHarness(fixture, hold: true, ignoreCancellation: true);
        var seed = await harness.SeedAsync();
        await using var factory = harness.Factory(seed);
        using var first = await AiExecutionHarness.ClientAsync(factory, seed.Member);
        using var retryClient = await AiExecutionHarness.ClientAsync(factory, seed.Owner);
        using var timeout = Timeout();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var running = AiExecutionHarness.ExecuteAsync(first, seed, request.Token);
        try
        {
            await harness.Generator.Entered.WaitAsync(timeout.Token);
            await request.CancelAsync();
            harness.Generator.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
            // Request abort may reach HttpClient before bounded server cleanup finishes; poll its durable result.
            JsonElement replay;
            while (true)
            {
                using var response = await AiExecutionHarness.ExecuteAsync(retryClient, seed, timeout.Token);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    await Task.Delay(10, timeout.Token);
                    continue;
                }
                replay = await AiExecutionHarness.ReadAsync(response, HttpStatusCode.ServiceUnavailable);
                break;
            }
            Assert.Equal("CANCELLED", replay.GetProperty("outcome").GetString());
            Assert.True(replay.GetProperty("replayed").GetBoolean());
            Assert.Equal(1, harness.Generator.Calls);
            await using var db = fixture.CreateContext();
            var ledger = Assert.Single(await db.AiExecutions.Where(e => e.ConversationId == seed.Conversation).ToListAsync(Cancellation));
            Assert.True(ledger.ReservedCost > 0);
            Assert.Null(ledger.ActualCost);
            Assert.Empty(await db.Messages.Where(m => m.ConversationId == seed.Conversation && m.SenderKind == MessageSenderKind.AI).ToListAsync(Cancellation));
        }
        finally { harness.Generator.Release(); await DrainAsync(running); }
    }

    [Theory]
    [InlineData("daily_execution")]
    [InlineData("minute_execution")]
    [InlineData("minute_generation")]
    public async Task Host_clock_skew_cannot_expire_live_claims_or_reset_shared_admission_windows(string limit)
    {
        var harness = new AiExecutionHarness(fixture, hold: true);
        var seed = await harness.SeedAsync();
        var other = await harness.ConversationAsync(seed);
        var price = new AiTierPrice { InputPerMillionTokens = 1m, OutputPerMillionTokens = 3m };
        var settings = new AiExecutionSettings
        {
            MaxExecutionsPerWorkspacePerDay = limit == "daily_execution" ? 1 : 5000,
            ExecutionsPerMinutePerWorkspace = limit == "minute_execution" ? 1 : 30,
            GenerationsPerMinutePerWorkspace = limit == "minute_generation" ? 1 : 20,
            Fast = price, Balanced = price, Quality = price
        };
        var skewedClock = new AdvancingClock();
        skewedClock.Advance(TimeSpan.FromDays(2));
        await using var firstHost = harness.Factory(seed, settings);
        await using var secondHost = harness.Factory(seed, settings, skewedClock);
        using var first = await AiExecutionHarness.ClientAsync(firstHost, seed.Member);
        using var second = await AiExecutionHarness.ClientAsync(secondHost, seed.Owner);
        using var timeout = Timeout();
        var running = AiExecutionHarness.ExecuteAsync(first, seed, timeout.Token);
        try
        {
            await harness.Generator.Entered.WaitAsync(timeout.Token);
            // Each quota is isolated: a high daily quota must not mask a broken per-minute execution/provider window.
            await AiExecutionHarness.StatusAsync(HttpStatusCode.Conflict, await AiExecutionHarness.ExecuteAsync(second, seed, timeout.Token));
            var expected = limit == "minute_generation" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
            var refused = await AiExecutionHarness.ReadAsync(await AiExecutionHarness.ExecuteAsync(second, other, timeout.Token), expected);
            Assert.Equal(limit == "daily_execution" ? "QUOTA_EXCEEDED" : "RATE_LIMITED", refused.GetProperty("failure").GetString());
            if (limit == "minute_generation") Assert.Equal("FALLBACK", refused.GetProperty("outcome").GetString());
            Assert.Equal(1, harness.Generator.Calls);
            harness.Generator.Release();
            await AiExecutionHarness.ReadAsync(await running);
            await AiExecutionHarness.StatusAsync(expected, await AiExecutionHarness.ExecuteAsync(second, other, timeout.Token));
            Assert.Equal(1, harness.Generator.Calls);
            await using var db = fixture.CreateContext();
            Assert.Equal(limit == "minute_generation" ? 2 : 1,
                await db.Messages.CountAsync(m => m.ConversationId == other.Conversation, Cancellation));
        }
        finally { harness.Generator.Release(); await DrainAsync(running); }
    }

    private async Task MutateAsync(string mutation, AiExecutionSeed seed, HttpClient owner, CancellationToken ct)
    {
        await using var db = fixture.CreateContext();
        switch (mutation)
        {
            case "agent_disabled":
                await db.Database.ExecuteSqlRawAsync("""UPDATE "AiAgents" SET "Enabled" = false WHERE "Id" = {0}""", [seed.Agent], ct); break;
            case "scope_changed":
                await db.Database.ExecuteSqlRawAsync("""UPDATE "AiAgents" SET "KnowledgeScope" = 'SELECTED_SOURCES' WHERE "Id" = {0}""", [seed.Agent], ct); break;
            case "instructions_same_timestamp":
                await db.Database.ExecuteSqlRawAsync("""UPDATE "AiAgents" SET "Instructions" = 'Changed policy without timestamp change.' WHERE "Id" = {0}""", [seed.Agent], ct); break;
            case "source_disabled":
                await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Enabled" = false WHERE "Id" = {0}""", [seed.Source], ct); break;
            case "document_superseded":
                await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeDocuments" SET "Status" = 'SUPERSEDED' WHERE "Id" = {0}""", [seed.Document], ct); break;
            case "chunk_changed":
                await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeDocumentChunks" SET "Content" = 'Refund policy changed while generation was running.' WHERE "Id" = {0}""", [seed.Chunk], ct); break;
            case "binding_aba":
                await AiExecutionHarness.StatusAsync(HttpStatusCode.OK, await owner.PutAsJsonAsync(seed.Path + "/ai-agent", new { agentId = (Guid?)null }, ct));
                await AiExecutionHarness.StatusAsync(HttpStatusCode.OK, await owner.PutAsJsonAsync(seed.Path + "/ai-agent", new { agentId = seed.Agent }, ct)); break;
            case "resolve_reopen":
                await AiExecutionHarness.StatusAsync(HttpStatusCode.OK, await owner.PostAsJsonAsync(seed.Path + "/resolve", new { }, ct));
                await AiExecutionHarness.StatusAsync(HttpStatusCode.OK, await owner.PostAsJsonAsync(seed.Path + "/reopen", new { }, ct)); break;
            case "new_message":
                await AiExecutionHarness.StatusAsync(HttpStatusCode.Created, await owner.PostAsJsonAsync(seed.Path + "/messages", new { clientMessageId = Guid.NewGuid(), content = "A newer question" }, ct)); break;
            case "human_assignment":
                await AiExecutionHarness.StatusAsync(HttpStatusCode.OK, await owner.PutAsJsonAsync(seed.Path + "/assignment", new { assignedUserId = seed.Owner }, ct)); break;
            case "pending_handoff":
                await AiExecutionHarness.StatusAsync(HttpStatusCode.Created, await owner.PostAsJsonAsync(seed.Path + "/handoffs", new { clientRequestId = Guid.NewGuid() }, ct)); break;
            case "member_removed":
                await db.WorkspaceMembers.Where(m => m.WorkspaceId == seed.Workspace && m.UserId == seed.Member).ExecuteDeleteAsync(ct); break;
            case "channel_disabled":
                await AiExecutionHarness.StatusAsync(HttpStatusCode.OK, await owner.PutAsJsonAsync($"/workspaces/{seed.Workspace}/channels/{seed.Channel}", new { enabled = false }, ct)); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static CancellationTokenSource Timeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return timeout;
    }

    private static async Task DrainAsync(Task task)
    {
        try { await task; }
        catch (Exception) { /* Observe cleanup; the original assertion is the failure. */ }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }
}
