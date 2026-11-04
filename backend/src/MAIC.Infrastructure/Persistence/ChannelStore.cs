using MAIC.Application.Channels;
using MAIC.Domain.Channels;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

public sealed class ChannelStore(MaicDbContext db) : IChannelStore
{
    private const string TokenUniqueIndex = "IX_Channels_PublishableToken";

    private async Task<bool> AuthorizeWriteAsync(Guid actorId, Guid workspaceId, CancellationToken ct)
    {
        // All workspace writers use workspace -> actor membership lock order and retain
        // both locks until commit, so removal/role changes cannot race this mutation.
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var members = await db.WorkspaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "WorkspaceMembers" WHERE "WorkspaceId" = {workspaceId} AND "UserId" = {actorId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        return members.Count == 1;
    }

    public async Task<ChannelResult> CreateAsync(Guid actorId, Channel channel, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AuthorizeWriteAsync(actorId, channel.WorkspaceId, cancellationToken))
            return new(ChannelStatus.NotFound);
        db.Channels.Add(channel);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(ChannelStatus.Success, Channel: View(channel));
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: TokenUniqueIndex })
        {
            // Discard the failed insert before this scoped context can be used again.
            // Disposing the uncommitted transaction rolls back the attempted write.
            db.Entry(channel).State = EntityState.Detached;
            return new(ChannelStatus.Conflict);
        }
    }

    public async Task<ChannelResult> ListAsync(Guid actorId, Guid workspaceId, int skip, int take, CancellationToken cancellationToken)
    {
        var result = await db.WorkspaceMembers.AsNoTracking().AsSingleQuery()
            .Where(actor => actor.WorkspaceId == workspaceId && actor.UserId == actorId)
            .Select(actor => new
            {
                Items = db.Channels.Where(c => c.WorkspaceId == workspaceId && c.WorkspaceId == actor.WorkspaceId)
                    .OrderBy(c => c.Id).Skip(skip).Take(take).Select(c => new ChannelView(
                        c.Id, c.WorkspaceId, c.Type.ToString(), c.Name, c.PublishableToken, c.Enabled, c.CreatedAt)).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        return result is null ? new(ChannelStatus.NotFound) : new(ChannelStatus.Success, Items: result.Items);
    }

    public async Task<ChannelResult> GetAsync(Guid actorId, Guid workspaceId, Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await db.Channels.AsNoTracking().AsSingleQuery()
            .Where(c => c.WorkspaceId == workspaceId && c.Id == channelId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == actorId))
            .Select(c => new ChannelView(c.Id, c.WorkspaceId, c.Type.ToString(), c.Name, c.PublishableToken, c.Enabled, c.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
        return channel is null ? new(ChannelStatus.NotFound) : new(ChannelStatus.Success, Channel: channel);
    }

    public async Task<ChannelResult> UpdateAsync(Guid actorId, Guid workspaceId, Guid channelId, string? name,
        bool? enabled, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AuthorizeWriteAsync(actorId, workspaceId, cancellationToken))
            return new(ChannelStatus.NotFound);
        var channel = await db.Channels.SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Id == channelId, cancellationToken);
        if (channel is null) return new(ChannelStatus.NotFound);
        await db.Entry(channel).ReloadAsync(cancellationToken);
        if (name is not null) channel.Rename(name);
        if (enabled is { } state) channel.SetEnabled(state);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ChannelStatus.Success, Channel: View(channel));
    }

    public async Task<ChannelResult> LookupPublicAsync(string publishableToken, CancellationToken cancellationToken)
    {
        var channel = await db.Channels.AsNoTracking()
            .Where(c => c.Type == ChannelType.WEB && c.Enabled && c.PublishableToken == publishableToken)
            .Select(c => new PublicChannelView(c.Name))
            .SingleOrDefaultAsync(cancellationToken);
        return channel is null ? new(ChannelStatus.NotFound) : new(ChannelStatus.Success, Public: channel);
    }

    private static ChannelView View(Channel channel) => new(channel.Id, channel.WorkspaceId, channel.Type.ToString(),
        channel.Name, channel.PublishableToken, channel.Enabled, channel.CreatedAt);
}
