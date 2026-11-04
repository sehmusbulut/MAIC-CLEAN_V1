using System.Security.Cryptography;
using System.Text;
using MAIC.Application.AI;
using MAIC.Application.Identity;
using MAIC.Application.Knowledge;
using MAIC.Domain.AI;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Knowledge;

public sealed record KnowledgeBaseRulesRequest(
    string? BusinessSummary,
    string? KeyFaq,
    string? AnswerBoundaries,
    string? EscalationRules,
    string? AgentName = null,
    bool? Enabled = null,
    string? Language = null,
    string? ModelTier = null,
    bool? HandoffOnRequest = null,
    bool? HandoffWhenUnanswered = null,
    int? MaxAiReplies = null);

public sealed record KnowledgeBaseTextRequest(string? Title, string? Content);
public sealed record KnowledgeBaseUrlRequest(string? Title, string? Url);
public sealed record KnowledgeBaseBulkDeleteRequest(Guid[]? DocumentIds);

public static class KnowledgeBaseEndpoints
{
    public const string DefaultPoolName = "Ana Bilgi Bankası";
    public const string DefaultAgentName = "Destek Asistanı";

    public static void MapKnowledgeBaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}/knowledge-base").RequireAuthorization();

        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        group.MapGet("", async (
            Guid workspaceId,
            string? search,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();
            var auth = await AuthorizeWorkspaceAsync(db, userId, currentUser.IsSuperAdmin, workspaceId, requireManage: false, cancellationToken);
            if (auth.Error is not null) return auth.Error;

            var workspace = auth.Workspace!;
            var (defaultSource, agent) = await EnsureDefaultPoolAsync(db, workspace, cancellationToken);
            var planDef = await GetPlanDefinitionAsync(db, workspace.Plan, cancellationToken);
            await SyncWorkspaceStorageAsync(db, workspace, cancellationToken);

            var query = from d in db.KnowledgeDocuments.AsNoTracking()
                        join s in db.KnowledgeSources.AsNoTracking() on new { d.WorkspaceId, Id = d.KnowledgeSourceId } equals new { s.WorkspaceId, s.Id }
                        where d.WorkspaceId == workspaceId && d.Status != KnowledgeDocumentStatus.SUPERSEDED
                        orderby d.CreatedAt descending
                        select new
                        {
                            Document = d,
                            SourceName = s.Name,
                            SourceKind = s.Kind,
                            WebsiteUrl = s.WebsiteUrl
                        };

            var rawItems = await query.ToListAsync(cancellationToken);

            var documentCount = rawItems.Count(x => x.Document.Origin != KnowledgeDocumentOrigin.WEBSITE && x.Document.SourceUrl == null);
            var urlCount = rawItems.Count(x => x.Document.Origin == KnowledgeDocumentOrigin.WEBSITE || x.Document.SourceUrl != null);

            var items = rawItems
                .Select(x => ToDocumentDto(x.Document, x.SourceName, x.WebsiteUrl))
                .Where(dto => string.IsNullOrWhiteSpace(search)
                    || dto.Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                    || (dto.FileName is not null && dto.FileName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
                    || (dto.SourceUrl is not null && dto.SourceUrl.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            var effectiveStorageLimit = planDef.StorageLimitBytes > 0 ? planDef.StorageLimitBytes : workspace.StorageLimitBytes;

            return Results.Ok(new
            {
                workspaceId = workspace.Id,
                defaultSourceId = defaultSource.Id,
                documentCount,
                maxDocumentCount = planDef.MaxDocumentCount,
                urlCount,
                maxUrlSourceCount = planDef.MaxUrlSourceCount,
                usedStorageBytes = workspace.UsedStorageBytes,
                storageLimitBytes = effectiveStorageLimit,
                maxFileSizeBytes = planDef.MaxFileSizeBytes,
                quota = new
                {
                    plan = workspace.Plan.ToString(),
                    documentCount,
                    maxDocumentCount = planDef.MaxDocumentCount,
                    urlCount,
                    maxUrlSourceCount = planDef.MaxUrlSourceCount,
                    usedStorageBytes = workspace.UsedStorageBytes,
                    storageLimitBytes = effectiveStorageLimit,
                    maxFileSizeBytes = planDef.MaxFileSizeBytes
                },
                rules = new
                {
                    businessSummary = workspace.BusinessSummary,
                    keyFaq = workspace.KeyFaq,
                    answerBoundaries = workspace.AnswerBoundaries,
                    escalationRules = workspace.EscalationRules
                },
                agent = ToAgentDto(agent),
                documents = items,
                items
            });
        });

        group.MapGet("/rules", async (
            Guid workspaceId,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();
            var auth = await AuthorizeWorkspaceAsync(db, userId, currentUser.IsSuperAdmin, workspaceId, requireManage: false, cancellationToken);
            if (auth.Error is not null) return auth.Error;

            var workspace = auth.Workspace!;
            var (_, agent) = await EnsureDefaultPoolAsync(db, workspace, cancellationToken);

            return Results.Ok(new
            {
                businessSummary = workspace.BusinessSummary,
                keyFaq = workspace.KeyFaq,
                answerBoundaries = workspace.AnswerBoundaries,
                escalationRules = workspace.EscalationRules,
                agent = ToAgentDto(agent)
            });
        });

        group.MapPut("/rules", async (
            Guid workspaceId,
            KnowledgeBaseRulesRequest? request,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();
            if (request is null) return Results.BadRequest(new { error = "Geçersiz istek gövdesi." });

            var auth = await AuthorizeWorkspaceAsync(db, userId, currentUser.IsSuperAdmin, workspaceId, requireManage: true, cancellationToken);
            if (auth.Error is not null) return auth.Error;

            var workspace = auth.Workspace!;
            var (_, agent) = await EnsureDefaultPoolAsync(db, workspace, cancellationToken);

            workspace.UpdateAiRules(
                request.BusinessSummary,
                request.KeyFaq,
                request.AnswerBoundaries,
                request.EscalationRules);

            var (useCaseHint, assistantRoleHint) = await MAIC.API.Workspaces.WorkspaceSettingsEndpoints.GetCatalogSystemPromptHintsAsync(
                db, workspace.UseCase, workspace.AssistantRole, cancellationToken);

            var compiledInstructions = BuildSystemPrompt(
                workspace.BusinessSummary,
                workspace.KeyFaq,
                workspace.AnswerBoundaries,
                workspace.EscalationRules,
                useCaseHint,
                assistantRoleHint);

            var modelTier = agent.ModelTier;
            if (!string.IsNullOrWhiteSpace(request.ModelTier) && AiAgent.TryParseModelTier(request.ModelTier.Trim().ToUpperInvariant(), out var parsedTier))
            {
                modelTier = parsedTier;
            }

            var language = request.Language is not null
                ? (string.IsNullOrWhiteSpace(request.Language) ? null : request.Language.Trim())
                : (agent.Language ?? "tr");
            if (language is not null && !AiAgent.IsValidLanguage(language))
            {
                language = "tr";
            }

            var agentName = !string.IsNullOrWhiteSpace(request.AgentName) && KnowledgeSource.IsValidName(request.AgentName)
                ? request.AgentName.Trim()
                : agent.Name;

            var maxAiReplies = request.MaxAiReplies ?? agent.MaxAiReplies;
            if (maxAiReplies is < 1 or > AiAgent.MaxAiRepliesLimit)
            {
                maxAiReplies = null;
            }

            var updatedSettings = new AiAgentSettings(
                Name: agentName,
                Enabled: request.Enabled ?? agent.Enabled,
                Instructions: compiledInstructions,
                Language: language,
                ModelTier: modelTier,
                KnowledgeScope: AiKnowledgeScope.ALL_SOURCES,
                KnowledgeSourceIds: [],
                HandoffOnRequest: request.HandoffOnRequest ?? agent.HandoffOnRequest,
                HandoffWhenUnanswered: request.HandoffWhenUnanswered ?? agent.HandoffWhenUnanswered,
                MaxAiReplies: maxAiReplies);

            agent.Apply(updatedSettings, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                businessSummary = workspace.BusinessSummary,
                keyFaq = workspace.KeyFaq,
                answerBoundaries = workspace.AnswerBoundaries,
                escalationRules = workspace.EscalationRules,
                agent = ToAgentDto(agent)
            });
        });

        group.MapPost("/files", async (
            Guid workspaceId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            IKnowledgeFileStorage storage,
            IngestionProcessor ingestionProcessor,
            KnowledgeIndexProcessor indexProcessor,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();
            var auth = await AuthorizeWorkspaceAsync(db, userId, currentUser.IsSuperAdmin, workspaceId, requireManage: true, cancellationToken);
            if (auth.Error is not null) return auth.Error;

            var workspace = auth.Workspace!;
            var (defaultSource, _) = await EnsureDefaultPoolAsync(db, workspace, cancellationToken);
            var planDef = await GetPlanDefinitionAsync(db, workspace.Plan, cancellationToken);
            await SyncWorkspaceStorageAsync(db, workspace, cancellationToken);

            var (fileName, contentType, bytes, readError) = await ReadUploadedFileAsync(httpContext, planDef.MaxFileSizeBytes, cancellationToken);
            if (readError is not null) return readError;

            var fileSize = (long)bytes!.Length;
            if (fileSize > planDef.MaxFileSizeBytes)
            {
                return Results.Conflict(new
                {
                    code = "FILE_TOO_LARGE",
                    error = $"Dosya boyutu paketinizin izin verdiği maksimum boyutu ({planDef.MaxFileSizeBytes / (1024 * 1024)} MB) aşıyor."
                });
            }

            var currentDocCount = await db.KnowledgeDocuments.CountAsync(
                d => d.WorkspaceId == workspaceId
                    && d.Status != KnowledgeDocumentStatus.SUPERSEDED
                    && d.Origin != KnowledgeDocumentOrigin.WEBSITE
                    && d.SourceUrl == null,
                cancellationToken);

            if (currentDocCount >= planDef.MaxDocumentCount)
            {
                return Results.Conflict(new
                {
                    code = "DOCUMENT_LIMIT_EXCEEDED",
                    error = $"Paketinizin maksimum döküman adedi limitine ({planDef.MaxDocumentCount}) ulaştınız."
                });
            }

            var effectiveStorageLimit = planDef.StorageLimitBytes > 0 ? planDef.StorageLimitBytes : workspace.StorageLimitBytes;
            if (workspace.UsedStorageBytes + fileSize > effectiveStorageLimit)
            {
                return Results.Conflict(new
                {
                    code = "STORAGE_LIMIT_EXCEEDED",
                    error = "Depolama alanı kotanız doldu. Lütfen mevcut dökümanları silin veya paketinizi yükseltin."
                });
            }

            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var document = KnowledgeDocument.ForUpload(
                workspaceId,
                defaultSource.Id,
                userId,
                Guid.NewGuid(),
                fileName!,
                contentType!,
                fileSize,
                sha256,
                planDef.MaxFileSizeBytes);

            await storage.SaveAsync(workspaceId, document.Id, bytes, cancellationToken);
            try
            {
                var job = new IngestionJob(workspaceId, document.Id, DateTimeOffset.UtcNow);
                db.KnowledgeDocuments.Add(document);
                db.IngestionJobs.Add(job);
                workspace.AddStorageUsage(fileSize);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                await storage.DeleteAsync(workspaceId, document.Id, CancellationToken.None);
                throw;
            }

            await RunZeroClickIngestionAsync(db, ingestionProcessor, indexProcessor, workspaceId, document.Id, cancellationToken);

            var refreshed = await db.KnowledgeDocuments.AsNoTracking()
                .SingleAsync(d => d.WorkspaceId == workspaceId && d.Id == document.Id, cancellationToken);

            return Results.Created(
                $"/workspaces/{workspaceId}/knowledge-base/documents/{refreshed.Id}",
                ToDocumentDto(refreshed, defaultSource.Name, null));
        });

        group.MapPost("/texts", async (
            Guid workspaceId,
            KnowledgeBaseTextRequest? request,
            ICurrentUser currentUser,
            MaicDbContext db,
            IngestionProcessor ingestionProcessor,
            KnowledgeIndexProcessor indexProcessor,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();
            if (request is null
                || string.IsNullOrWhiteSpace(request.Title)
                || !KnowledgeSource.IsValidName(request.Title)
                || string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.BadRequest(new { error = "Başlık ve metin içeriği zorunludur." });
            }

            var cleanedContent = CleanTextContent(request.Content);
            if (!KnowledgeSource.IsValidTextContent(cleanedContent))
            {
                return Results.BadRequest(new { error = "Metin içeriği geçersiz veya çok uzun (maksimum 64.000 karakter)." });
            }

            var auth = await AuthorizeWorkspaceAsync(db, userId, currentUser.IsSuperAdmin, workspaceId, requireManage: true, cancellationToken);
            if (auth.Error is not null) return auth.Error;

            var workspace = auth.Workspace!;
            await EnsureDefaultPoolAsync(db, workspace, cancellationToken);
            var planDef = await GetPlanDefinitionAsync(db, workspace.Plan, cancellationToken);
            await SyncWorkspaceStorageAsync(db, workspace, cancellationToken);

            var byteSize = (long)Encoding.UTF8.GetByteCount(cleanedContent);
            if (byteSize > planDef.MaxFileSizeBytes)
            {
                return Results.Conflict(new
                {
                    code = "FILE_TOO_LARGE",
                    error = "Metin boyutu paketinizin izin verdiği maksimum boyutu aşıyor."
                });
            }

            var currentDocCount = await db.KnowledgeDocuments.CountAsync(
                d => d.WorkspaceId == workspaceId
                    && d.Status != KnowledgeDocumentStatus.SUPERSEDED
                    && d.Origin != KnowledgeDocumentOrigin.WEBSITE
                    && d.SourceUrl == null,
                cancellationToken);

            if (currentDocCount >= planDef.MaxDocumentCount)
            {
                return Results.Conflict(new
                {
                    code = "DOCUMENT_LIMIT_EXCEEDED",
                    error = $"Paketinizin maksimum döküman adedi limitine ({planDef.MaxDocumentCount}) ulaştınız."
                });
            }

            var effectiveStorageLimit = planDef.StorageLimitBytes > 0 ? planDef.StorageLimitBytes : workspace.StorageLimitBytes;
            if (workspace.UsedStorageBytes + byteSize > effectiveStorageLimit)
            {
                return Results.Conflict(new
                {
                    code = "STORAGE_LIMIT_EXCEEDED",
                    error = "Depolama alanı kotanız doldu."
                });
            }

            var textSource = new KnowledgeSource(workspaceId, KnowledgeSourceKind.TEXT, request.Title.Trim(), cleanedContent, null);
            var document = KnowledgeDocument.ForSnapshot(workspaceId, textSource.Id, KnowledgeSourceKind.TEXT, userId, Guid.NewGuid());
            var job = new IngestionJob(workspaceId, document.Id, DateTimeOffset.UtcNow);

            db.KnowledgeSources.Add(textSource);
            db.KnowledgeDocuments.Add(document);
            db.IngestionJobs.Add(job);
            workspace.AddStorageUsage(byteSize);
            await db.SaveChangesAsync(cancellationToken);

            await RunZeroClickIngestionAsync(db, ingestionProcessor, indexProcessor, workspaceId, document.Id, cancellationToken);

            var refreshed = await db.KnowledgeDocuments.AsNoTracking()
                .SingleAsync(d => d.WorkspaceId == workspaceId && d.Id == document.Id, cancellationToken);

            return Results.Created(
                $"/workspaces/{workspaceId}/knowledge-base/documents/{refreshed.Id}",
                ToDocumentDto(refreshed, textSource.Name, null));
        });

        group.MapPost("/urls", async (
            Guid workspaceId,
            KnowledgeBaseUrlRequest? request,
            ICurrentUser currentUser,
            MaicDbContext db,
            IngestionProcessor ingestionProcessor,
            KnowledgeIndexProcessor indexProcessor,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();
            if (request is null || string.IsNullOrWhiteSpace(request.Url))
            {
                return Results.BadRequest(new { error = "Geçerli bir HTTPS adresi zorunludur." });
            }

            var url = request.Url.Trim();
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !KnowledgeSource.IsValidWebsiteUrl(url))
            {
                return Results.BadRequest(new { error = "Sadece geçerli https:// adresleri kabul edilmektedir." });
            }

            var title = !string.IsNullOrWhiteSpace(request.Title)
                ? request.Title.Trim()
                : Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ? parsedUri.Host : url;
            if (title.Length > KnowledgeSource.MaxNameLength) title = title[..KnowledgeSource.MaxNameLength];
            if (!KnowledgeSource.IsValidName(title)) return Results.BadRequest(new { error = "Kaynak başlığı geçersiz." });

            var auth = await AuthorizeWorkspaceAsync(db, userId, currentUser.IsSuperAdmin, workspaceId, requireManage: true, cancellationToken);
            if (auth.Error is not null) return auth.Error;

            var workspace = auth.Workspace!;
            await EnsureDefaultPoolAsync(db, workspace, cancellationToken);
            var planDef = await GetPlanDefinitionAsync(db, workspace.Plan, cancellationToken);
            await SyncWorkspaceStorageAsync(db, workspace, cancellationToken);

            var currentUrlCount = await db.KnowledgeDocuments.CountAsync(
                d => d.WorkspaceId == workspaceId
                    && d.Status != KnowledgeDocumentStatus.SUPERSEDED
                    && (d.Origin == KnowledgeDocumentOrigin.WEBSITE || d.SourceUrl != null),
                cancellationToken);

            if (currentUrlCount >= planDef.MaxUrlSourceCount)
            {
                return Results.Conflict(new
                {
                    code = "URL_LIMIT_EXCEEDED",
                    error = $"Paketinizin maksimum web linki (URL) limitine ({planDef.MaxUrlSourceCount}) ulaştınız."
                });
            }

            var urlSource = new KnowledgeSource(workspaceId, KnowledgeSourceKind.WEBSITE, title, null, url);
            var document = KnowledgeDocument.ForSnapshot(workspaceId, urlSource.Id, KnowledgeSourceKind.WEBSITE, userId, Guid.NewGuid());
            var job = new IngestionJob(workspaceId, document.Id, DateTimeOffset.UtcNow);

            db.KnowledgeSources.Add(urlSource);
            db.KnowledgeDocuments.Add(document);
            db.IngestionJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);

            await RunZeroClickIngestionAsync(db, ingestionProcessor, indexProcessor, workspaceId, document.Id, cancellationToken);
            await SyncWorkspaceStorageAsync(db, workspace, cancellationToken);

            var refreshed = await db.KnowledgeDocuments.AsNoTracking()
                .SingleAsync(d => d.WorkspaceId == workspaceId && d.Id == document.Id, cancellationToken);

            return Results.Created(
                $"/workspaces/{workspaceId}/knowledge-base/documents/{refreshed.Id}",
                ToDocumentDto(refreshed, urlSource.Name, urlSource.WebsiteUrl));
        });

        group.MapDelete("/documents/{id:guid}", async (
            Guid workspaceId,
            Guid id,
            ICurrentUser currentUser,
            MaicDbContext db,
            IKnowledgeFileStorage storage,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();
            var auth = await AuthorizeWorkspaceAsync(db, userId, currentUser.IsSuperAdmin, workspaceId, requireManage: true, cancellationToken);
            if (auth.Error is not null) return auth.Error;

            var workspace = auth.Workspace!;
            var document = await db.KnowledgeDocuments.AsNoTracking()
                .SingleOrDefaultAsync(d => d.WorkspaceId == workspaceId && d.Id == id, cancellationToken);
            if (document is null) return Results.NotFound();

            await DeleteDocumentsInternalAsync(db, storage, workspace, [document], cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/documents/bulk-delete", async (
            Guid workspaceId,
            KnowledgeBaseBulkDeleteRequest? request,
            ICurrentUser currentUser,
            MaicDbContext db,
            IKnowledgeFileStorage storage,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();
            if (request?.DocumentIds is not { Length: > 0 } ids)
                return Results.BadRequest(new { error = "Silinecek döküman kimlikleri belirtilmelidir." });

            var auth = await AuthorizeWorkspaceAsync(db, userId, currentUser.IsSuperAdmin, workspaceId, requireManage: true, cancellationToken);
            if (auth.Error is not null) return auth.Error;

            var workspace = auth.Workspace!;
            var distinctIds = ids.Distinct().ToArray();
            var documents = await db.KnowledgeDocuments.AsNoTracking()
                .Where(d => d.WorkspaceId == workspaceId && distinctIds.Contains(d.Id))
                .ToListAsync(cancellationToken);

            await DeleteDocumentsInternalAsync(db, storage, workspace, documents, cancellationToken);

            return Results.Ok(new
            {
                deletedCount = documents.Count,
                usedStorageBytes = workspace.UsedStorageBytes
            });
        });

        group.MapPost("/documents/{id:guid}/reindex", async (
            Guid workspaceId,
            Guid id,
            ICurrentUser currentUser,
            MaicDbContext db,
            IngestionProcessor ingestionProcessor,
            KnowledgeIndexProcessor indexProcessor,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();
            var auth = await AuthorizeWorkspaceAsync(db, userId, currentUser.IsSuperAdmin, workspaceId, requireManage: true, cancellationToken);
            if (auth.Error is not null) return auth.Error;

            var docWithSource = await db.KnowledgeDocuments
                .Where(d => d.WorkspaceId == workspaceId && d.Id == id)
                .Join(
                    db.KnowledgeSources.AsNoTracking(),
                    d => new { d.WorkspaceId, Id = d.KnowledgeSourceId },
                    s => new { s.WorkspaceId, s.Id },
                    (d, s) => new { Document = d, Source = s })
                .SingleOrDefaultAsync(cancellationToken);

            if (docWithSource is null) return Results.NotFound();

            await db.IngestionJobs
                .Where(j => j.WorkspaceId == workspaceId && j.DocumentId == id && (j.Status == IngestionJobStatus.QUEUED || j.Status == IngestionJobStatus.RUNNING))
                .ExecuteDeleteAsync(cancellationToken);

            await db.KnowledgeDocumentChunks
                .Where(c => c.WorkspaceId == workspaceId && c.DocumentId == id)
                .ExecuteDeleteAsync(cancellationToken);

            await db.KnowledgeDocuments
                .Where(d => d.WorkspaceId == workspaceId && d.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.Status, KnowledgeDocumentStatus.PENDING)
                    .SetProperty(d => d.FailureReason, (IngestionFailure?)null)
                    .SetProperty(d => d.ProcessedAt, (DateTimeOffset?)null)
                    .SetProperty(d => d.ChunkCount, 0)
                    .SetProperty(d => d.CharacterCount, 0),
                    cancellationToken);

            db.ChangeTracker.Clear();

            var job = new IngestionJob(workspaceId, id, DateTimeOffset.UtcNow);
            db.IngestionJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);

            await RunZeroClickIngestionAsync(db, ingestionProcessor, indexProcessor, workspaceId, id, cancellationToken);

            var refreshed = await db.KnowledgeDocuments.AsNoTracking()
                .SingleAsync(d => d.WorkspaceId == workspaceId && d.Id == id, cancellationToken);

            return Results.Ok(ToDocumentDto(refreshed, docWithSource.Source.Name, docWithSource.Source.WebsiteUrl));
        });
    }

    private static async Task<(Workspace? Workspace, IResult? Error)> AuthorizeWorkspaceAsync(
        MaicDbContext db,
        Guid userId,
        bool isSuperAdminClaim,
        Guid workspaceId,
        bool requireManage,
        CancellationToken cancellationToken)
    {
        await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);

        var workspace = await db.Workspaces
            .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
        if (workspace is null) return (null, Results.NotFound());

        var isSuperAdmin = isSuperAdminClaim
            || await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsSuperAdmin, cancellationToken);

        if (isSuperAdmin) return (workspace, null);

        var member = await db.WorkspaceMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, cancellationToken);
        if (member is null) return (null, Results.NotFound());

        if (!requireManage) return (workspace, null);

        var hasManagePermission = await db.WorkspaceRolePermissions.AsNoTracking()
            .AnyAsync(p => p.Role == member.Role && p.PermissionKey == "knowledge.manage" && p.IsGranted, cancellationToken);

        if (!hasManagePermission && member.Role is not (WorkspaceRole.OWNER or WorkspaceRole.ADMIN))
        {
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return (workspace, null);
    }

    public static async Task<(KnowledgeSource DefaultSource, AiAgent Agent)> EnsureDefaultPoolAsync(
        MaicDbContext db,
        Workspace workspace,
        CancellationToken cancellationToken)
    {
        var changed = false;

        var defaultSource = await db.KnowledgeSources
            .Where(s => s.WorkspaceId == workspace.Id && s.Kind == KnowledgeSourceKind.FILE)
            .OrderBy(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (defaultSource is null)
        {
            defaultSource = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, DefaultPoolName, null, null);
            db.KnowledgeSources.Add(defaultSource);
            changed = true;
        }
        else if (!defaultSource.Enabled)
        {
            defaultSource.SetEnabled(true);
            changed = true;
        }

        var agent = await db.AiAgents
            .Include(a => a.KnowledgeSources)
            .Where(a => a.WorkspaceId == workspace.Id)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (agent is null)
        {
            var (useCaseHint, assistantRoleHint) = await MAIC.API.Workspaces.WorkspaceSettingsEndpoints.GetCatalogSystemPromptHintsAsync(
                db, workspace.UseCase, workspace.AssistantRole, cancellationToken);

            var compiledInstructions = BuildSystemPrompt(
                workspace.BusinessSummary,
                workspace.KeyFaq,
                workspace.AnswerBoundaries,
                workspace.EscalationRules,
                useCaseHint,
                assistantRoleHint);

            var settings = new AiAgentSettings(
                Name: DefaultAgentName,
                Enabled: true,
                Instructions: compiledInstructions,
                Language: "tr",
                ModelTier: AiModelTier.BALANCED,
                KnowledgeScope: AiKnowledgeScope.ALL_SOURCES,
                KnowledgeSourceIds: [],
                HandoffOnRequest: true,
                HandoffWhenUnanswered: true,
                MaxAiReplies: null);
            agent = new AiAgent(workspace.Id, settings, DateTimeOffset.UtcNow);
            db.AiAgents.Add(agent);
            changed = true;
        }
        else if (agent.KnowledgeScope != AiKnowledgeScope.ALL_SOURCES)
        {
            var settings = new AiAgentSettings(
                Name: agent.Name,
                Enabled: agent.Enabled,
                Instructions: agent.Instructions,
                Language: agent.Language,
                ModelTier: agent.ModelTier,
                KnowledgeScope: AiKnowledgeScope.ALL_SOURCES,
                KnowledgeSourceIds: [],
                HandoffOnRequest: agent.HandoffOnRequest,
                HandoffWhenUnanswered: agent.HandoffWhenUnanswered,
                MaxAiReplies: agent.MaxAiReplies);
            agent.Apply(settings, DateTimeOffset.UtcNow);
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return (defaultSource, agent);
    }

    public static string? BuildSystemPrompt(
        string? businessSummary,
        string? keyFaq,
        string? answerBoundaries,
        string? escalationRules,
        string? useCaseHint = null,
        string? assistantRoleHint = null)
    {
        if (string.IsNullOrWhiteSpace(businessSummary)
            && string.IsNullOrWhiteSpace(keyFaq)
            && string.IsNullOrWhiteSpace(answerBoundaries)
            && string.IsNullOrWhiteSpace(escalationRules)
            && string.IsNullOrWhiteSpace(useCaseHint)
            && string.IsNullOrWhiteSpace(assistantRoleHint))
        {
            return null;
        }

        var roleSection = string.IsNullOrWhiteSpace(useCaseHint) && string.IsNullOrWhiteSpace(assistantRoleHint)
            ? string.Empty
            : $"""
            [SEKTÖR VE ASİSTAN ROLÜ DAVRANIŞ REHBERİ]
            {(string.IsNullOrWhiteSpace(useCaseHint) ? string.Empty : useCaseHint.Trim())}
            {(string.IsNullOrWhiteSpace(assistantRoleHint) ? string.Empty : assistantRoleHint.Trim())}

            """;

        var raw = $"""
            {roleSection}[İŞ ÖZETİ VE MARKA KİMLİĞİ]
            {(string.IsNullOrWhiteSpace(businessSummary) ? "Belirtilmedi." : businessSummary.Trim())}

            [ÖNEMLİ SORU BAŞLIKLARI VE STANDART YANITLAR (FAQ)]
            {(string.IsNullOrWhiteSpace(keyFaq) ? "Belirtilmedi." : keyFaq.Trim())}

            [CEVAP SINIRLARI VE YASAKLAR (GUARDRAILS)]
            {(string.IsNullOrWhiteSpace(answerBoundaries) ? "Sadece bilgi bankasındaki doğrulanmış bilgilere dayanarak yanıt ver." : answerBoundaries.Trim())}

            [İNSANA DEVİR KURALLARI (ESCALATION RULES)]
            {(string.IsNullOrWhiteSpace(escalationRules) ? "Kullanıcı canlı destek talep ettiğinde veya bilgi bankasında yanıt bulunamadığında görüşmeyi temsilciye devret." : escalationRules.Trim())}
            """;

        var sanitized = CleanTextContent(raw);
        if (sanitized.Length > AiAgent.MaxInstructionsLength)
        {
            sanitized = sanitized[..AiAgent.MaxInstructionsLength].TrimEnd();
        }

        return AiAgent.IsValidInstructions(sanitized) ? sanitized : null;
    }

    private static string CleanTextContent(string input)
    {
        var sb = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200B' or '\u2060' or '\uFEFF')
                continue;
            if (c == '\uDB40' && i + 1 < input.Length && input[i + 1] is >= '\uDC00' and <= '\uDC7F')
            {
                i++;
                continue;
            }
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
                continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static async Task<PlanDefinition> GetPlanDefinitionAsync(
        MaicDbContext db,
        PlanTier plan,
        CancellationToken cancellationToken) =>
        await db.PlanDefinitions.AsNoTracking().SingleOrDefaultAsync(p => p.Plan == plan, cancellationToken)
        ?? DynamicRbacSeeder.GetDefaultPlanDefinition(plan);

    private static async Task SyncWorkspaceStorageAsync(
        MaicDbContext db,
        Workspace workspace,
        CancellationToken cancellationToken)
    {
        var totalBytes = await db.KnowledgeDocuments.AsNoTracking()
            .Where(d => d.WorkspaceId == workspace.Id && d.Status != KnowledgeDocumentStatus.SUPERSEDED)
            .SumAsync(d => d.SizeBytes ?? 0L, cancellationToken);

        if (workspace.UsedStorageBytes != totalBytes)
        {
            workspace.SetUsedStorageBytes(totalBytes);
            await db.Workspaces
                .Where(w => w.Id == workspace.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.UsedStorageBytes, Math.Max(0L, totalBytes)), cancellationToken);
        }
    }

    private static async Task DeleteDocumentsInternalAsync(
        MaicDbContext db,
        IKnowledgeFileStorage storage,
        Workspace workspace,
        IReadOnlyList<KnowledgeDocument> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0) return;

        var docIds = documents.Select(d => d.Id).ToArray();
        var dedicatedSourceIds = documents
            .Where(d => d.Origin != KnowledgeDocumentOrigin.UPLOAD)
            .Select(d => d.KnowledgeSourceId)
            .Distinct()
            .ToArray();

        await db.KnowledgeDocuments
            .Where(d => d.WorkspaceId == workspace.Id && docIds.Contains(d.Id))
            .ExecuteDeleteAsync(cancellationToken);

        if (dedicatedSourceIds.Length > 0)
        {
            await db.KnowledgeSources
                .Where(s => s.WorkspaceId == workspace.Id
                    && dedicatedSourceIds.Contains(s.Id)
                    && s.Kind != KnowledgeSourceKind.FILE
                    && !db.KnowledgeDocuments.Any(d => d.WorkspaceId == workspace.Id && d.KnowledgeSourceId == s.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        foreach (var doc in documents.Where(d => d.Origin == KnowledgeDocumentOrigin.UPLOAD))
        {
            await storage.DeleteAsync(workspace.Id, doc.Id, CancellationToken.None);
        }

        await SyncWorkspaceStorageAsync(db, workspace, cancellationToken);
    }

    private static async Task RunZeroClickIngestionAsync(
        MaicDbContext db,
        IngestionProcessor ingestionProcessor,
        KnowledgeIndexProcessor indexProcessor,
        Guid workspaceId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var outcome = await ingestionProcessor.ProcessNextAsync(cancellationToken);
                if (outcome is null || outcome.DocumentId == documentId)
                    break;
            }

            if (indexProcessor.Space.Key != EmbeddingSpace.DisabledKey)
            {
                await indexProcessor.DiscoverAsync(cancellationToken);
                await indexProcessor.ProcessNextAsync(cancellationToken);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Background worker will retry any transient extraction/fetch/indexing failure.
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<(string? FileName, string? ContentType, byte[]? Bytes, IResult? Error)> ReadUploadedFileAsync(
        HttpContext httpContext,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        string? rawFileName = null;
        string? declaredContentType = null;
        byte[] bytes;

        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return (null, null, null, Results.BadRequest(new { error = "Yüklenecek dosya bulunamadı veya boş." }));
            }

            if (file.Length > maxFileSizeBytes)
            {
                return (null, null, null, Results.Conflict(new
                {
                    code = "FILE_TOO_LARGE",
                    error = $"Dosya boyutu maksimum limiti ({maxFileSizeBytes / (1024 * 1024)} MB) aşıyor."
                }));
            }

            rawFileName = Path.GetFileName(file.FileName)?.Trim();
            declaredContentType = file.ContentType;
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }
        else
        {
            rawFileName = httpContext.Request.Query["fileName"].ToString().Trim();
            declaredContentType = httpContext.Request.ContentType;
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await httpContext.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (ms.Length + read > maxFileSizeBytes)
                {
                    return (null, null, null, Results.Conflict(new
                    {
                        code = "FILE_TOO_LARGE",
                        error = $"Dosya boyutu maksimum limiti ({maxFileSizeBytes / (1024 * 1024)} MB) aşıyor."
                    }));
                }
                ms.Write(buffer, 0, read);
            }
            bytes = ms.ToArray();
        }

        if (bytes.Length == 0 || string.IsNullOrWhiteSpace(rawFileName) || !KnowledgeDocument.IsValidFileName(rawFileName))
        {
            return (null, null, null, Results.BadRequest(new { error = "Geçersiz dosya adı veya boş dosya." }));
        }

        var resolvedContentType = ResolveUploadContentType(rawFileName, declaredContentType);
        if (resolvedContentType is null || !KnowledgeDocumentService.MatchesContentType(resolvedContentType, bytes))
        {
            return (null, null, null, Results.BadRequest(new { error = "Sadece geçerli PDF, DOCX, TXT ve MD dosyaları desteklenmektedir." }));
        }

        return (rawFileName, resolvedContentType, bytes, null);
    }

    private static string? ResolveUploadContentType(string fileName, string? contentTypeHeader)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var byExtension = ext switch
        {
            ".pdf" => KnowledgeDocument.ApplicationPdf,
            ".docx" => KnowledgeDocument.ApplicationDocx,
            ".md" or ".markdown" => KnowledgeDocument.TextMarkdown,
            ".txt" => KnowledgeDocument.TextPlain,
            _ => null
        };

        if (byExtension is not null) return byExtension;
        return KnowledgeDocumentService.ParseUploadContentType(contentTypeHeader);
    }

    private static KnowledgeBaseDocumentDto ToDocumentDto(KnowledgeDocument d, string sourceName, string? websiteUrl)
    {
        var isUrl = d.Origin == KnowledgeDocumentOrigin.WEBSITE || d.SourceUrl is not null || websiteUrl is not null;
        var type = isUrl
            ? "URL"
            : d.Origin == KnowledgeDocumentOrigin.TEXT
                ? "TEXT"
                : "FILE";
        var title = d.FileName ?? sourceName;
        var resolvedUrl = d.SourceUrl ?? websiteUrl;

        return new KnowledgeBaseDocumentDto(
            Id: d.Id,
            Title: title,
            Type: type,
            FileName: d.FileName,
            SourceUrl: resolvedUrl,
            SizeBytes: d.SizeBytes ?? 0L,
            ChunkCount: d.ChunkCount,
            CharacterCount: d.CharacterCount,
            Status: d.Status.ToString(),
            FailureReason: d.FailureReason?.ToString(),
            CreatedAt: d.CreatedAt,
            UpdatedAt: d.ProcessedAt ?? d.CreatedAt);
    }

    private static object ToAgentDto(AiAgent a) => new
    {
        id = a.Id,
        name = a.Name,
        enabled = a.Enabled,
        language = a.Language,
        modelTier = a.ModelTier.ToString(),
        knowledgeScope = a.KnowledgeScope.ToString(),
        handoffOnRequest = a.HandoffOnRequest,
        handoffWhenUnanswered = a.HandoffWhenUnanswered,
        maxAiReplies = a.MaxAiReplies,
        instructions = a.Instructions,
        updatedAt = a.UpdatedAt
    };
}

public sealed record KnowledgeBaseDocumentDto(
    Guid Id,
    string Title,
    string Type,
    string? FileName,
    string? SourceUrl,
    long SizeBytes,
    int ChunkCount,
    int CharacterCount,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
