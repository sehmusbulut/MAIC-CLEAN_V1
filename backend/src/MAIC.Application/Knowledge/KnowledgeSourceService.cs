using MAIC.Application.Identity;
using MAIC.Domain.Knowledge;

namespace MAIC.Application.Knowledge;

public enum KnowledgeSourceStatus { Success, Unauthenticated, Invalid, NotFound }

/// <summary>List item: metadata only, never the text or URL payload.</summary>
public sealed record KnowledgeSourceSummary(Guid Id, Guid WorkspaceId, string Kind, string Name, bool Enabled,
    DateTimeOffset CreatedAt);

/// <summary>Detail view; TextContent/WebsiteUrl are null where the kind does not carry them.</summary>
public sealed record KnowledgeSourceView(Guid Id, Guid WorkspaceId, string Kind, string Name, bool Enabled,
    DateTimeOffset CreatedAt, string? TextContent, string? WebsiteUrl);

public sealed record KnowledgeSourceResult(KnowledgeSourceStatus Status, KnowledgeSourceView? Source = null,
    IReadOnlyList<KnowledgeSourceSummary>? Items = null, Guid? NextAfter = null);

public interface IKnowledgeSourceStore
{
    Task<KnowledgeSourceResult> CreateAsync(Guid actorId, KnowledgeSource source, CancellationToken cancellationToken);
    Task<KnowledgeSourceResult> ListAsync(Guid actorId, Guid workspaceId, Guid? after, int take, CancellationToken cancellationToken);
    Task<KnowledgeSourceResult> GetAsync(Guid actorId, Guid workspaceId, Guid sourceId, CancellationToken cancellationToken);
    Task<KnowledgeSourceResult> UpdateAsync(Guid actorId, Guid workspaceId, Guid sourceId, string? name, bool? enabled,
        string? textContent, string? websiteUrl, CancellationToken cancellationToken);
}

public sealed class KnowledgeSourceService(ICurrentUser currentUser, IKnowledgeSourceStore store)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    private static Task<KnowledgeSourceResult> Invalid() => Task.FromResult(new KnowledgeSourceResult(KnowledgeSourceStatus.Invalid));

    private Task<KnowledgeSourceResult> Run(Func<Guid, Task<KnowledgeSourceResult>> action) => currentUser.UserId is { } actor
        ? action(actor) : Task.FromResult(new KnowledgeSourceResult(KnowledgeSourceStatus.Unauthenticated));

    public Task<KnowledgeSourceResult> CreateAsync(Guid workspaceId, string? kind, string? name, string? textContent,
        string? websiteUrl, CancellationToken ct) => Run(actor =>
        workspaceId == Guid.Empty || !KnowledgeSource.TryParseKind(kind, out var parsed)
            || !KnowledgeSource.IsValidName(name) || !KnowledgeSource.IsValidPayload(parsed, textContent, websiteUrl)
            ? Invalid()
            : store.CreateAsync(actor, new KnowledgeSource(workspaceId, parsed, name!, textContent, websiteUrl), ct));

    public Task<KnowledgeSourceResult> ListAsync(Guid workspaceId, Guid? after, int take, CancellationToken ct) => Run(actor =>
        take is < 1 or > MaxPageSize || after == Guid.Empty
            ? Invalid()
            : store.ListAsync(actor, workspaceId, after, take, ct));

    public Task<KnowledgeSourceResult> GetAsync(Guid workspaceId, Guid sourceId, CancellationToken ct) => Run(actor =>
        store.GetAsync(actor, workspaceId, sourceId, ct));

    public Task<KnowledgeSourceResult> UpdateAsync(Guid workspaceId, Guid sourceId, string? name, bool? enabled,
        string? textContent, string? websiteUrl, CancellationToken ct) => Run(actor =>
        !KnowledgeSource.IsValidUpdate(name, enabled, textContent, websiteUrl)
            ? Invalid()
            : store.UpdateAsync(actor, workspaceId, sourceId, name, enabled, textContent, websiteUrl, ct));
}
