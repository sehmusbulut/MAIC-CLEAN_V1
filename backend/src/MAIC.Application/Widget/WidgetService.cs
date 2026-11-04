using System.Security.Cryptography;
using MAIC.Domain.Channels;
using MAIC.Domain.Conversations;

namespace MAIC.Application.Widget;

public enum WidgetStatus { Success, Invalid, Unauthenticated, NotFound, Conflict, Limited }
public sealed record WidgetSessionView(Guid ConversationId, string Status, DateTimeOffset ExpiresAt);
public sealed record WidgetMessageView(Guid Id, Guid? ClientMessageId, long Sequence, string SenderKind,
    string Content, DateTimeOffset CreatedAt);
public sealed record WidgetResult(WidgetStatus Status, WidgetSessionView? Session = null,
    WidgetMessageView? Message = null, IReadOnlyList<WidgetMessageView>? Messages = null,
    string? ConversationStatus = null, long? NextAfterSequence = null, bool Replayed = false);

public sealed class WidgetLimits
{
    public int SessionsPerChannel { get; set; } = 10_000;
    public int SessionsPerChannelDay { get; set; } = 500;
    public int GuestMessagesPerConversation { get; set; } = 1_000;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    public WidgetLimits Validate()
    {
        if (SessionsPerChannel is < 1 or > 1_000_000 || SessionsPerChannelDay is < 1 or > 10_000
            || GuestMessagesPerConversation is < 1 or > 10_000)
            throw new InvalidOperationException("Widget durable limits must be positive and within the supported bounds.");
        return this;
    }
}

public interface IWidgetSessionStore
{
    Task<WidgetResult> StartAsync(string channelToken, string credentialHash, CancellationToken ct);
    Task<WidgetResult> GetAsync(string channelToken, string credentialHash, CancellationToken ct);
    Task<WidgetResult> RevokeAsync(string channelToken, string credentialHash, CancellationToken ct);
    Task<WidgetResult> ListMessagesAsync(string channelToken, string credentialHash, long afterSequence, int take, CancellationToken ct);
    Task<WidgetResult> AppendAsync(string channelToken, string credentialHash, Guid clientMessageId, string content, CancellationToken ct);
}

// Raw bearer capabilities terminate here; persistence receives only their canonical SHA-256 digest.
public sealed class WidgetService(IWidgetSessionStore store)
{
    public Task<WidgetResult> StartAsync(string? channelToken, string? credential, CancellationToken ct) =>
        Run(channelToken, credential, hash => store.StartAsync(channelToken!, hash, ct));
    public Task<WidgetResult> GetAsync(string? channelToken, string? credential, CancellationToken ct) =>
        Run(channelToken, credential, hash => store.GetAsync(channelToken!, hash, ct));
    public Task<WidgetResult> RevokeAsync(string? channelToken, string? credential, CancellationToken ct) =>
        Run(channelToken, credential, hash => store.RevokeAsync(channelToken!, hash, ct));
    public Task<WidgetResult> ListMessagesAsync(string? channelToken, string? credential, long afterSequence, int take, CancellationToken ct) =>
        Run(channelToken, credential, hash => afterSequence < 0 || take is < 1 or > 100
            ? Task.FromResult(new WidgetResult(WidgetStatus.Invalid))
            : store.ListMessagesAsync(channelToken!, hash, afterSequence, take, ct));
    public Task<WidgetResult> AppendAsync(string? channelToken, string? credential, Guid clientMessageId, string? content, CancellationToken ct) =>
        Run(channelToken, credential, hash => clientMessageId == Guid.Empty || !Message.IsValidContent(content)
            ? Task.FromResult(new WidgetResult(WidgetStatus.Invalid))
            : store.AppendAsync(channelToken!, hash, clientMessageId, content!, ct));

    private static Task<WidgetResult> Run(string? channelToken, string? credential, Func<string, Task<WidgetResult>> action)
    {
        if (!Channel.IsValidPublishableToken(channelToken)) return Task.FromResult(new WidgetResult(WidgetStatus.NotFound));
        var hash = HashCredential(credential);
        return hash is null ? Task.FromResult(new WidgetResult(WidgetStatus.Unauthenticated)) : action(hash);
    }

    public static string? HashCredential(string? credential)
    {
        if (!Channel.IsValidPublishableToken(credential)) return null;
        try
        {
            var bytes = Convert.FromBase64String(credential!.Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != 32 || Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != credential)
                return null;
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (FormatException) { return null; }
    }
}
