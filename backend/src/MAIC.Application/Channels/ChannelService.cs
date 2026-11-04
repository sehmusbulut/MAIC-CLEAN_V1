using MAIC.Application.Identity;
using MAIC.Domain.Channels;

namespace MAIC.Application.Channels;

public enum ChannelStatus { Success, Unauthenticated, Invalid, NotFound, Forbidden, Conflict }

public sealed record ChannelView(Guid Id, Guid WorkspaceId, string Type, string Name, string PublishableToken,
    bool Enabled, DateTimeOffset CreatedAt);
public sealed record PublicChannelView(string Name);
public sealed record ChannelResult(ChannelStatus Status, ChannelView? Channel = null,
    IReadOnlyList<ChannelView>? Items = null, PublicChannelView? Public = null);

public interface IChannelStore
{
    Task<ChannelResult> CreateAsync(Guid actorId, Channel channel, CancellationToken cancellationToken);
    Task<ChannelResult> ListAsync(Guid actorId, Guid workspaceId, int skip, int take, CancellationToken cancellationToken);
    Task<ChannelResult> GetAsync(Guid actorId, Guid workspaceId, Guid channelId, CancellationToken cancellationToken);
    Task<ChannelResult> UpdateAsync(Guid actorId, Guid workspaceId, Guid channelId, string? name, bool? enabled,
        CancellationToken cancellationToken);
    Task<ChannelResult> LookupPublicAsync(string publishableToken, CancellationToken cancellationToken);
}

public sealed class ChannelService(ICurrentUser currentUser, IChannelStore store)
{
    private Task<ChannelResult> Run(Func<Guid, Task<ChannelResult>> action) => currentUser.UserId is { } actor
        ? action(actor) : Task.FromResult(new ChannelResult(ChannelStatus.Unauthenticated));

    public Task<ChannelResult> CreateAsync(Guid workspaceId, string? name, CancellationToken ct) => Run(actor =>
        workspaceId == Guid.Empty || !Channel.IsValidName(name)
            ? Task.FromResult(new ChannelResult(ChannelStatus.Invalid))
            : store.CreateAsync(actor, new Channel(workspaceId, name!), ct));

    public Task<ChannelResult> ListAsync(Guid workspaceId, int skip, int take, CancellationToken ct) => Run(actor =>
        skip is < 0 or > 100000 || take is < 1 or > 100
            ? Task.FromResult(new ChannelResult(ChannelStatus.Invalid))
            : store.ListAsync(actor, workspaceId, skip, take, ct));

    public Task<ChannelResult> GetAsync(Guid workspaceId, Guid channelId, CancellationToken ct) => Run(actor =>
        store.GetAsync(actor, workspaceId, channelId, ct));

    public Task<ChannelResult> UpdateAsync(Guid workspaceId, Guid channelId, string? name, bool? enabled, CancellationToken ct) => Run(actor =>
        (name is null && enabled is null) || (name is not null && !Channel.IsValidName(name))
            ? Task.FromResult(new ChannelResult(ChannelStatus.Invalid))
            : store.UpdateAsync(actor, workspaceId, channelId, name, enabled, ct));

    public Task<ChannelResult> LookupPublicAsync(string? publishableToken, CancellationToken ct) =>
        !Channel.IsValidPublishableToken(publishableToken)
            ? Task.FromResult(new ChannelResult(ChannelStatus.NotFound))
            : store.LookupPublicAsync(publishableToken!, ct);
}
