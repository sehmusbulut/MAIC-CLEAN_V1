using System.Security.Cryptography;
using MAIC.Application.Widget;
using Xunit;

namespace MAIC.Application.Tests;

public sealed class WidgetServiceTests
{
    private static readonly string Channel = new('C', 43);
    private static readonly string Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/")]
    public async Task Malformed_or_noncanonical_credentials_never_reach_persistence(string? credential)
    {
        var store = new RecordingStore();
        var service = new WidgetService(store);
        Assert.Null(WidgetService.HashCredential(credential));
        Assert.Equal(WidgetStatus.Unauthenticated, (await service.StartAsync(Channel, credential, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WidgetStatus.Unauthenticated, (await service.GetAsync(Channel, credential, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WidgetStatus.Unauthenticated, (await service.RevokeAsync(Channel, credential, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WidgetStatus.Unauthenticated, (await service.ListMessagesAsync(Channel, credential, 0, 50, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WidgetStatus.Unauthenticated, (await service.AppendAsync(Channel, credential, Guid.NewGuid(), "message", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Every_operation_passes_only_the_digest_scoped_channel_and_cancellation()
    {
        var store = new RecordingStore();
        var service = new WidgetService(store);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var decoded = Convert.FromBase64String(Credential.Replace('-', '+').Replace('_', '/') + "=");
        var digest = Convert.ToHexString(SHA256.HashData(decoded));
        Assert.Equal(digest, WidgetService.HashCredential(Credential));
        foreach (var call in new Func<Task<WidgetResult>>[]
                 {
                     () => service.StartAsync(Channel, Credential, cancellation.Token),
                     () => service.GetAsync(Channel, Credential, cancellation.Token),
                     () => service.RevokeAsync(Channel, Credential, cancellation.Token),
                     () => service.ListMessagesAsync(Channel, Credential, 2, 100, cancellation.Token),
                     () => service.AppendAsync(Channel, Credential, Guid.NewGuid(), "Merhaba 😀", cancellation.Token)
                 })
        {
            Assert.Same(store.Result, await call());
            Assert.Equal(Channel, store.Channel);
            Assert.Equal(digest, store.Hash);
            Assert.Equal(cancellation.Token, store.Cancellation);
        }
        Assert.Equal(5, store.Calls);
    }

    [Fact]
    public async Task Invalid_channel_body_and_cursor_are_rejected_without_store_calls()
    {
        var store = new RecordingStore();
        var service = new WidgetService(store);
        Assert.Equal(WidgetStatus.NotFound, (await service.StartAsync("bad", Credential, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WidgetStatus.Invalid, (await service.ListMessagesAsync(Channel, Credential, -1, 1, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WidgetStatus.Invalid, (await service.ListMessagesAsync(Channel, Credential, 0, 0, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WidgetStatus.Invalid, (await service.ListMessagesAsync(Channel, Credential, 0, 101, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(WidgetStatus.Invalid, (await service.AppendAsync(Channel, Credential, Guid.Empty, "x", TestContext.Current.CancellationToken)).Status);
        foreach (var content in new[] { "", "  ", "\0", "\ud800", new string('x', 4001) })
            Assert.Equal(WidgetStatus.Invalid, (await service.AppendAsync(Channel, Credential, Guid.NewGuid(), content, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, store.Calls);
    }

    private sealed class RecordingStore : IWidgetSessionStore
    {
        public WidgetResult Result { get; } = new(WidgetStatus.Unauthenticated);
        public int Calls { get; private set; }
        public string? Channel { get; private set; }
        public string? Hash { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        private Task<WidgetResult> Record(string channel, string hash, CancellationToken ct)
        { Calls++; Channel = channel; Hash = hash; Cancellation = ct; return Task.FromResult(Result); }
        public Task<WidgetResult> StartAsync(string channel, string hash, CancellationToken ct) => Record(channel, hash, ct);
        public Task<WidgetResult> GetAsync(string channel, string hash, CancellationToken ct) => Record(channel, hash, ct);
        public Task<WidgetResult> RevokeAsync(string channel, string hash, CancellationToken ct) => Record(channel, hash, ct);
        public Task<WidgetResult> ListMessagesAsync(string channel, string hash, long afterSequence, int take, CancellationToken ct) => Record(channel, hash, ct);
        public Task<WidgetResult> AppendAsync(string channel, string hash, Guid id, string content, CancellationToken ct) => Record(channel, hash, ct);
    }
}
