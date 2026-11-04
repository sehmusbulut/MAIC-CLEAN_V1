using MAIC.Application.Identity;
using MAIC.Domain.AI;
using MAIC.Domain.Workspaces;

namespace MAIC.Application.AI;

public enum AiAgentStatus { Success, Unauthenticated, Invalid, NotFound, Forbidden, Conflict }

/// <summary>Full configuration. Knowledge source IDs are sorted and empty for ALL_SOURCES.</summary>
public sealed record AiAgentView(Guid Id, Guid WorkspaceId, string Name, bool Enabled, string? Instructions, string? Language,
    string ModelTier, string KnowledgeScope, IReadOnlyList<Guid> KnowledgeSourceIds, bool HandoffOnRequest,
    bool HandoffWhenUnanswered, int? MaxAiReplies, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>List item: no instructions and no source list.</summary>
public sealed record AiAgentSummary(Guid Id, Guid WorkspaceId, string Name, bool Enabled, string ModelTier, string KnowledgeScope,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record AiAgentResult(AiAgentStatus Status, AiAgentView? Agent = null, IReadOnlyList<AiAgentSummary>? Items = null,
    Guid? NextAfter = null);

/// <summary>Raw request values; tier and scope are exact, case-sensitive names.</summary>
public sealed record AiAgentInput(string? Name, bool Enabled, string? Instructions, string? Language, string? ModelTier,
    string? KnowledgeScope, IReadOnlyList<Guid>? KnowledgeSourceIds, bool HandoffOnRequest, bool HandoffWhenUnanswered,
    int? MaxAiReplies);

public interface IAiAgentStore
{
    Task<AiAgentResult> CreateAsync(Guid actorId, Guid workspaceId, AiAgentSettings settings, IReadOnlyCollection<WorkspaceRole> writers,
        CancellationToken cancellationToken);
    Task<AiAgentResult> ListAsync(Guid actorId, Guid workspaceId, Guid? after, int take, CancellationToken cancellationToken);
    Task<AiAgentResult> GetAsync(Guid actorId, Guid workspaceId, Guid agentId, CancellationToken cancellationToken);
    Task<AiAgentResult> UpdateAsync(Guid actorId, Guid workspaceId, Guid agentId, AiAgentSettings settings,
        IReadOnlyCollection<WorkspaceRole> writers, CancellationToken cancellationToken);
    Task<AiAgentResult> DeleteAsync(Guid actorId, Guid workspaceId, Guid agentId, IReadOnlyCollection<WorkspaceRole> writers,
        CancellationToken cancellationToken);
}

/// <summary>
/// Every member can read agent configuration; only OWNER and ADMIN may change what answers customers. As for other
/// workspace writes, static input validation (400) precedes the store's membership (404) and role (403) checks.
/// </summary>
public sealed class AiAgentService(ICurrentUser currentUser, IAiAgentStore store)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public static readonly IReadOnlyCollection<WorkspaceRole> Writers = [WorkspaceRole.OWNER, WorkspaceRole.ADMIN];

    private static Task<AiAgentResult> Status(AiAgentStatus status) => Task.FromResult(new AiAgentResult(status));

    private Task<AiAgentResult> Run(Func<Guid, Task<AiAgentResult>> action) => currentUser.UserId is { } actor
        ? action(actor) : Status(AiAgentStatus.Unauthenticated);

    public static AiAgentSettings? Parse(AiAgentInput input) =>
        input.Name is not null && AiAgent.TryParseModelTier(input.ModelTier, out var tier)
        && AiAgent.TryParseKnowledgeScope(input.KnowledgeScope, out var scope) && input.KnowledgeSourceIds is not null
        && new AiAgentSettings(input.Name, input.Enabled, input.Instructions, input.Language, tier, scope, input.KnowledgeSourceIds,
            input.HandoffOnRequest, input.HandoffWhenUnanswered, input.MaxAiReplies) is var settings
        && AiAgent.IsValid(settings) ? settings : null;

    public Task<AiAgentResult> CreateAsync(Guid workspaceId, AiAgentInput input, CancellationToken ct) => Run(async actor =>
    {
        if (workspaceId == Guid.Empty || Parse(input) is not { } settings) return new AiAgentResult(AiAgentStatus.Invalid);
        var existing = await store.ListAsync(actor, workspaceId, null, 1, ct);
        if (existing.Status != AiAgentStatus.Success) return new AiAgentResult(existing.Status);
        if (existing.Items is { Count: > 0 }) return new AiAgentResult(AiAgentStatus.Conflict);
        return await store.CreateAsync(actor, workspaceId, settings, Writers, ct);
    });

    public Task<AiAgentResult> ListAsync(Guid workspaceId, Guid? after, int take, CancellationToken ct) => Run(actor =>
        take is < 1 or > MaxPageSize || after == Guid.Empty ? Status(AiAgentStatus.Invalid)
            : store.ListAsync(actor, workspaceId, after, take, ct));

    public Task<AiAgentResult> GetAsync(Guid workspaceId, Guid agentId, CancellationToken ct) =>
        Run(actor => store.GetAsync(actor, workspaceId, agentId, ct));

    public Task<AiAgentResult> UpdateAsync(Guid workspaceId, Guid agentId, AiAgentInput input, CancellationToken ct) =>
        Run(actor => Parse(input) is not { } settings ? Status(AiAgentStatus.Invalid)
            : store.UpdateAsync(actor, workspaceId, agentId, settings, Writers, ct));

    public Task<AiAgentResult> DeleteAsync(Guid workspaceId, Guid agentId, CancellationToken ct) =>
        Run(actor => store.DeleteAsync(actor, workspaceId, agentId, Writers, ct));
}
