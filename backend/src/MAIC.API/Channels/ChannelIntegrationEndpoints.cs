using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MAIC.API.Knowledge;
using MAIC.API.Widget;
using MAIC.API.Workspaces;
using MAIC.Application.AI;
using MAIC.Application.Identity;
using MAIC.Domain.AI;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Widget;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Channels;

public sealed record TestAndConnectIntegrationRequest(
    // WhatsApp fields
    string? PhoneNumberId,
    string? WabaId,
    // Instagram fields
    string? InstagramAccountId,
    string? PageId,
    // Trendyol fields
    string? SupplierId,
    string? ApiKey,
    string? ApiSecret,
    // Common / fallback fields
    string? AccessToken,
    string? AppSecret,
    string? ExternalAccountId,
    string? SecondaryAccountId);

public static class ChannelIntegrationEndpoints
{
    public const string GraphApiBaseUrl = "https://graph.facebook.com/v21.0";

    public static void MapChannelIntegrationEndpoints(this WebApplication app)
    {
        var workspaceGroup = app.MapGroup("/workspaces/{workspaceId:guid}/integrations").RequireAuthorization();

        // 2. GET /workspaces/{workspaceId}/integrations
        workspaceGroup.MapGet("", async (
            Guid workspaceId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, _, authError) = await AuthorizeIntegrationAccessAsync(
                db, currentUser, workspaceId, requireManage: false, cancellationToken);
            if (authError is not null) return authError;

            var integrations = await EnsureWorkspaceIntegrationsAsync(db, workspace!.Id, cancellationToken);
            var origin = ResolveOrigin(httpContext);
            var dtos = integrations
                .Select(i => ToIntegrationDto(i, origin))
                .ToArray();

            return Results.Ok(new
            {
                workspaceId = workspace.Id,
                items = dtos,
                integrations = dtos,
                whatsapp = dtos.FirstOrDefault(x => string.Equals(x.Provider, ChannelIntegration.ProviderWhatsApp, StringComparison.OrdinalIgnoreCase)),
                instagram = dtos.FirstOrDefault(x => string.Equals(x.Provider, ChannelIntegration.ProviderInstagram, StringComparison.OrdinalIgnoreCase)),
                trendyol = dtos.FirstOrDefault(x => string.Equals(x.Provider, ChannelIntegration.ProviderTrendyol, StringComparison.OrdinalIgnoreCase))
            });
        });

        // 3. POST /workspaces/{workspaceId}/integrations/{provider}/test-and-connect
        workspaceGroup.MapPost("/{provider}/test-and-connect", async (
            Guid workspaceId,
            string provider,
            TestAndConnectIntegrationRequest? request,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, _, authError) = await AuthorizeIntegrationAccessAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            var normalizedProvider = (provider ?? string.Empty).Trim().ToUpperInvariant();
            if (!ChannelIntegration.SupportedProviders.Contains(normalizedProvider, StringComparer.Ordinal))
            {
                return Results.BadRequest(new
                {
                    success = false,
                    errorCode = "INVALID_PROVIDER",
                    error = "Geçersiz kanal sağlayıcısı. Desteklenenler: WHATSAPP, INSTAGRAM, TRENDYOL."
                });
            }

            if (request is null)
            {
                return Results.BadRequest(new
                {
                    success = false,
                    errorCode = "INVALID_PAYLOAD",
                    error = "Bağlantı bilgileri boş olamaz."
                });
            }

            await EnsureWorkspaceIntegrationsAsync(db, workspace!.Id, cancellationToken);
            var integration = await db.ChannelIntegrations
                .SingleAsync(i => i.WorkspaceId == workspace.Id && i.Provider == normalizedProvider, cancellationToken);

            var logger = loggerFactory.CreateLogger("MAIC.Channels.IntegrationTest");
            var origin = ResolveOrigin(httpContext);

            return normalizedProvider switch
            {
                ChannelIntegration.ProviderWhatsApp => await TestAndConnectWhatsAppAsync(
                    db, httpClientFactory, logger, workspace, integration, request, origin, cancellationToken),
                ChannelIntegration.ProviderInstagram => await TestAndConnectInstagramAsync(
                    db, httpClientFactory, logger, workspace, integration, request, origin, cancellationToken),
                ChannelIntegration.ProviderTrendyol => await TestAndConnectTrendyolAsync(
                    db, httpClientFactory, logger, workspace, integration, request, origin, cancellationToken),
                _ => Results.BadRequest(new { success = false, error = "Desteklenmeyen sağlayıcı." })
            };
        });

        // 3.D DELETE /workspaces/{workspaceId}/integrations/{provider}
        workspaceGroup.MapDelete("/{provider}", async (
            Guid workspaceId,
            string provider,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, _, authError) = await AuthorizeIntegrationAccessAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            var normalizedProvider = (provider ?? string.Empty).Trim().ToUpperInvariant();
            if (!ChannelIntegration.SupportedProviders.Contains(normalizedProvider, StringComparer.Ordinal))
            {
                return Results.BadRequest(new { error = "Geçersiz kanal sağlayıcısı." });
            }

            var integration = await db.ChannelIntegrations
                .SingleOrDefaultAsync(i => i.WorkspaceId == workspace!.Id && i.Provider == normalizedProvider, cancellationToken);

            if (integration is null)
            {
                integration = new ChannelIntegration(workspace!.Id, normalizedProvider);
                db.ChannelIntegrations.Add(integration);
            }
            else
            {
                integration.MarkDisconnected(DateTimeOffset.UtcNow);
            }

            await db.SaveChangesAsync(cancellationToken);

            var origin = ResolveOrigin(httpContext);
            return Results.Ok(new
            {
                success = true,
                integration = ToIntegrationDto(integration, origin)
            });
        });

        // 4.A GET /webhooks/channels/{workspaceId:guid}/{provider} (Meta Webhook Verification Challenge)
        app.MapGet("/webhooks/channels/{workspaceId:guid}/{provider}", async (
            Guid workspaceId,
            string provider,
            [FromQuery(Name = "hub.mode")] string? hubMode,
            [FromQuery(Name = "hub.verify_token")] string? hubVerifyToken,
            [FromQuery(Name = "hub.challenge")] string? hubChallenge,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            var normalizedProvider = (provider ?? string.Empty).Trim().ToUpperInvariant();
            if (!ChannelIntegration.SupportedProviders.Contains(normalizedProvider, StringComparer.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var integration = await db.ChannelIntegrations.AsNoTracking()
                .SingleOrDefaultAsync(i => i.WorkspaceId == workspaceId && i.Provider == normalizedProvider, cancellationToken);

            if (integration is null || string.IsNullOrWhiteSpace(hubVerifyToken))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!string.IsNullOrWhiteSpace(hubMode)
                && !string.Equals(hubMode, "subscribe", StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var expectedBytes = Encoding.UTF8.GetBytes(integration.WebhookVerifyToken.Trim());
            var providedBytes = Encoding.UTF8.GetBytes(hubVerifyToken.Trim());

            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return Results.Text(hubChallenge ?? string.Empty, "text/plain", Encoding.UTF8, StatusCodes.Status200OK);
        }).AllowAnonymous();

        // 4.B POST /webhooks/channels/{workspaceId:guid}/{provider} (Real Incoming Meta Webhook Receiver)
        app.MapPost("/webhooks/channels/{workspaceId:guid}/{provider}", async (
            Guid workspaceId,
            string provider,
            HttpContext httpContext,
            MaicDbContext db,
            KnowledgeRetriever retriever,
            ITextGenerator textGenerator,
            TimeProvider timeProvider,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var normalizedProvider = (provider ?? string.Empty).Trim().ToUpperInvariant();
            if (!ChannelIntegration.SupportedProviders.Contains(normalizedProvider, StringComparer.Ordinal))
            {
                return Results.NotFound(new { error = "Desteklenmeyen kanal sağlayıcısı." });
            }

            var workspace = await db.Workspaces
                .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
            if (workspace is null)
            {
                return Results.NotFound(new { error = "Workspace bulunamadı." });
            }

            var integration = await db.ChannelIntegrations
                .SingleOrDefaultAsync(i => i.WorkspaceId == workspaceId && i.Provider == normalizedProvider, cancellationToken);
            if (integration is null)
            {
                return Results.NotFound(new { error = "Kanal entegrasyonu bulunamadı." });
            }

            byte[] rawBodyBytes;
            using (var ms = new MemoryStream())
            {
                await httpContext.Request.Body.CopyToAsync(ms, cancellationToken);
                rawBodyBytes = ms.ToArray();
            }

            // Verify X-Hub-Signature-256 if AppSecret is configured on the integration
            if (!string.IsNullOrWhiteSpace(integration.AppSecret))
            {
                var signatureHeader = httpContext.Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
                if (!VerifyMetaHubSignature(rawBodyBytes, integration.AppSecret, signatureHeader))
                {
                    return Results.Json(new
                    {
                        code = "INVALID_WEBHOOK_SIGNATURE",
                        error = "X-Hub-Signature-256 imzası doğrulanamadı."
                    }, statusCode: StatusCodes.Status401Unauthorized);
                }
            }

            var logger = loggerFactory.CreateLogger("MAIC.Channels.WebhookReceiver");
            var now = DateTimeOffset.UtcNow;
            integration.RecordWebhookReceived(now);
            await db.SaveChangesAsync(cancellationToken);

            if (rawBodyBytes.Length == 0)
            {
                return Results.Ok(new { received = true, processedMessages = 0 });
            }

            List<IncomingExternalMessage> incomingMessages;
            try
            {
                using var doc = JsonDocument.Parse(rawBodyBytes);
                incomingMessages = ParseIncomingWebhookMessages(normalizedProvider, doc.RootElement);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid JSON payload on webhook for Workspace={WorkspaceId}, Provider={Provider}", workspaceId, normalizedProvider);
                return Results.BadRequest(new { error = "Geçersiz webhook JSON gövdesi." });
            }

            var processedCount = 0;
            foreach (var incoming in incomingMessages)
            {
                if (string.IsNullOrWhiteSpace(incoming.SenderId) || string.IsNullOrWhiteSpace(incoming.Text))
                {
                    continue;
                }

                await ProcessIncomingExternalMessageAsync(
                    db,
                    retriever,
                    textGenerator,
                    timeProvider,
                    httpClientFactory,
                    logger,
                    workspace,
                    integration,
                    incoming,
                    cancellationToken);
                processedCount++;
            }

            return Results.Ok(new
            {
                received = true,
                provider = normalizedProvider,
                processedMessages = processedCount
            });
        }).AllowAnonymous();
    }

    private static async Task<IResult> TestAndConnectWhatsAppAsync(
        MaicDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        Workspace workspace,
        ChannelIntegration integration,
        TestAndConnectIntegrationRequest request,
        string origin,
        CancellationToken cancellationToken)
    {
        var phoneNumberId = (request.PhoneNumberId ?? request.ExternalAccountId ?? string.Empty).Trim();
        var wabaId = (request.WabaId ?? request.SecondaryAccountId ?? string.Empty).Trim();
        var accessToken = (request.AccessToken ?? string.Empty).Trim();
        var appSecret = string.IsNullOrWhiteSpace(request.AppSecret) ? null : request.AppSecret.Trim();

        if (string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(accessToken))
        {
            return Results.BadRequest(new
            {
                success = false,
                errorCode = "MISSING_CREDENTIALS",
                error = "WhatsApp bağlantısı için Phone Number ID ve Kalıcı Access Token zorunludur."
            });
        }

        var url = $"{GraphApiBaseUrl}/{Uri.EscapeDataString(phoneNumberId)}?fields=id,display_phone_number,verified_name,quality_rating,code_verification_status";
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var httpReq = new HttpRequestMessage(HttpMethod.Get, url);
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        string responseBody;
        int statusCode;
        try
        {
            using var response = await client.SendAsync(httpReq, cancellationToken);
            statusCode = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var metaError = ExtractGraphErrorMessage(responseBody, statusCode);
                integration.MarkError(metaError, phoneNumberId, wabaId);
                await db.SaveChangesAsync(cancellationToken);

                return Results.BadRequest(new
                {
                    success = false,
                    errorCode = "META_API_ERROR",
                    error = $"WhatsApp Cloud API Doğrulama Hatası: {metaError}"
                });
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "WhatsApp Cloud API test request failed for WorkspaceId={WorkspaceId}", workspace.Id);
            var errMsg = $"Meta Graph API sunucusuna ulaşılamadı: {ex.Message}";
            integration.MarkError(errMsg, phoneNumberId, wabaId);
            await db.SaveChangesAsync(cancellationToken);

            return Results.BadRequest(new
            {
                success = false,
                errorCode = "META_API_ERROR",
                error = $"WhatsApp Cloud API Doğrulama Hatası: {errMsg}"
            });
        }

        string? displayPhoneNumber = null;
        string? verifiedName = null;
        string? qualityRating = null;
        string? codeVerificationStatus = null;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            displayPhoneNumber = TryGetString(root, "display_phone_number");
            verifiedName = TryGetString(root, "verified_name");
            qualityRating = TryGetString(root, "quality_rating");
            codeVerificationStatus = TryGetString(root, "code_verification_status");
        }
        catch (JsonException)
        {
            // Keep extracted fields null if unexpected format
        }

        var displayName = !string.IsNullOrWhiteSpace(verifiedName) && !string.IsNullOrWhiteSpace(displayPhoneNumber)
            ? $"{verifiedName} ({displayPhoneNumber})"
            : verifiedName ?? displayPhoneNumber ?? $"WhatsApp ({phoneNumberId})";

        var verifiedMetadataObj = new
        {
            phoneNumberId,
            wabaId = string.IsNullOrWhiteSpace(wabaId) ? null : wabaId,
            displayPhoneNumber,
            verifiedName,
            qualityRating,
            codeVerificationStatus
        };
        var verifiedMetadataJson = JsonSerializer.Serialize(verifiedMetadataObj);

        integration.MarkConnected(
            displayName: displayName,
            externalAccountId: phoneNumberId,
            secondaryAccountId: wabaId,
            accessToken: accessToken,
            appSecret: appSecret,
            verifiedMetadataJson: verifiedMetadataJson,
            now: DateTimeOffset.UtcNow);

        var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
        await EnsureNamedChannelAsync(db, workspace.Id, "WhatsApp Business", agent.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new
        {
            success = true,
            provider = ChannelIntegration.ProviderWhatsApp,
            status = integration.Status,
            displayName = integration.DisplayName,
            verifiedMetadata = verifiedMetadataObj,
            integration = ToIntegrationDto(integration, origin)
        });
    }

    private static async Task<IResult> TestAndConnectInstagramAsync(
        MaicDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        Workspace workspace,
        ChannelIntegration integration,
        TestAndConnectIntegrationRequest request,
        string origin,
        CancellationToken cancellationToken)
    {
        var instagramAccountId = (request.InstagramAccountId ?? request.ExternalAccountId ?? string.Empty).Trim();
        var pageId = (request.PageId ?? request.SecondaryAccountId ?? string.Empty).Trim();
        var accessToken = (request.AccessToken ?? string.Empty).Trim();
        var appSecret = string.IsNullOrWhiteSpace(request.AppSecret) ? null : request.AppSecret.Trim();

        if (string.IsNullOrWhiteSpace(instagramAccountId) || string.IsNullOrWhiteSpace(accessToken))
        {
            return Results.BadRequest(new
            {
                success = false,
                errorCode = "MISSING_CREDENTIALS",
                error = "Instagram bağlantısı için Instagram Account ID ve Access Token zorunludur."
            });
        }

        var url = $"{GraphApiBaseUrl}/{Uri.EscapeDataString(instagramAccountId)}?fields=id,username,name,profile_picture_url";
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var httpReq = new HttpRequestMessage(HttpMethod.Get, url);
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        string responseBody;
        int statusCode;
        try
        {
            using var response = await client.SendAsync(httpReq, cancellationToken);
            statusCode = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var metaError = ExtractGraphErrorMessage(responseBody, statusCode);
                integration.MarkError(metaError, instagramAccountId, pageId);
                await db.SaveChangesAsync(cancellationToken);

                return Results.BadRequest(new
                {
                    success = false,
                    errorCode = "META_API_ERROR",
                    error = $"Instagram Graph API Doğrulama Hatası: {metaError}"
                });
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Instagram Graph API test request failed for WorkspaceId={WorkspaceId}", workspace.Id);
            var errMsg = $"Meta Graph API sunucusuna ulaşılamadı: {ex.Message}";
            integration.MarkError(errMsg, instagramAccountId, pageId);
            await db.SaveChangesAsync(cancellationToken);

            return Results.BadRequest(new
            {
                success = false,
                errorCode = "META_API_ERROR",
                error = $"Instagram Graph API Doğrulama Hatası: {errMsg}"
            });
        }

        string? username = null;
        string? name = null;
        string? profilePictureUrl = null;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            username = TryGetString(root, "username");
            name = TryGetString(root, "name");
            profilePictureUrl = TryGetString(root, "profile_picture_url");
        }
        catch (JsonException)
        {
            // Keep null if unexpected
        }

        var displayName = !string.IsNullOrWhiteSpace(username)
            ? (!string.IsNullOrWhiteSpace(name) ? $"{name} (@{username})" : $"@{username}")
            : name ?? $"Instagram ({instagramAccountId})";

        var verifiedMetadataObj = new
        {
            instagramAccountId,
            pageId = string.IsNullOrWhiteSpace(pageId) ? null : pageId,
            username = string.IsNullOrWhiteSpace(username) ? null : $"@{username.TrimStart('@')}",
            name,
            profilePictureUrl
        };
        var verifiedMetadataJson = JsonSerializer.Serialize(verifiedMetadataObj);

        integration.MarkConnected(
            displayName: displayName,
            externalAccountId: instagramAccountId,
            secondaryAccountId: pageId,
            accessToken: accessToken,
            appSecret: appSecret,
            verifiedMetadataJson: verifiedMetadataJson,
            now: DateTimeOffset.UtcNow);

        var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
        await EnsureNamedChannelAsync(db, workspace.Id, "Instagram DM", agent.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new
        {
            success = true,
            provider = ChannelIntegration.ProviderInstagram,
            status = integration.Status,
            displayName = integration.DisplayName,
            verifiedMetadata = verifiedMetadataObj,
            integration = ToIntegrationDto(integration, origin)
        });
    }

    private static async Task<IResult> TestAndConnectTrendyolAsync(
        MaicDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        Workspace workspace,
        ChannelIntegration integration,
        TestAndConnectIntegrationRequest request,
        string origin,
        CancellationToken cancellationToken)
    {
        var supplierId = (request.SupplierId ?? request.ExternalAccountId ?? string.Empty).Trim();
        var apiKey = (request.ApiKey ?? request.SecondaryAccountId ?? string.Empty).Trim();
        var apiSecret = (request.ApiSecret ?? request.AccessToken ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(supplierId)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(apiSecret))
        {
            return Results.BadRequest(new
            {
                success = false,
                errorCode = "MISSING_CREDENTIALS",
                error = "Trendyol bağlantısı için Satıcı ID (Supplier ID), API Key ve API Secret zorunludur."
            });
        }

        var url = $"https://api.trendyol.com/sapigw/suppliers/{Uri.EscapeDataString(supplierId)}/questions/filter?size=1";
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        var basicCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"));
        using var httpReq = new HttpRequestMessage(HttpMethod.Get, url);
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredentials);
        httpReq.Headers.TryAddWithoutValidation("User-Agent", $"{supplierId} - SelfIntegration");

        string responseBody;
        int statusCode;
        try
        {
            using var response = await client.SendAsync(httpReq, cancellationToken);
            statusCode = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var trendyolError = ExtractTrendyolErrorMessage(responseBody, statusCode);
                integration.MarkError(trendyolError, supplierId, apiKey);
                await db.SaveChangesAsync(cancellationToken);

                return Results.BadRequest(new
                {
                    success = false,
                    errorCode = "TRENDYOL_API_ERROR",
                    error = $"Trendyol API Doğrulama Hatası: {trendyolError}"
                });
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Trendyol API test request failed for WorkspaceId={WorkspaceId}", workspace.Id);
            var errMsg = $"Trendyol API sunucusuna ulaşılamadı: {ex.Message}";
            integration.MarkError(errMsg, supplierId, apiKey);
            await db.SaveChangesAsync(cancellationToken);

            return Results.BadRequest(new
            {
                success = false,
                errorCode = "TRENDYOL_API_ERROR",
                error = $"Trendyol API Doğrulama Hatası: {errMsg}"
            });
        }

        var displayName = $"Trendyol Mağaza #{supplierId}";
        var verifiedMetadataObj = new
        {
            supplierId,
            apiKeyMasked = MaskToken(apiKey),
            verified = true
        };
        var verifiedMetadataJson = JsonSerializer.Serialize(verifiedMetadataObj);

        integration.MarkConnected(
            displayName: displayName,
            externalAccountId: supplierId,
            secondaryAccountId: apiKey,
            accessToken: apiSecret,
            appSecret: null,
            verifiedMetadataJson: verifiedMetadataJson,
            now: DateTimeOffset.UtcNow);

        var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
        await EnsureNamedChannelAsync(db, workspace.Id, "Trendyol Soru-Cevap", agent.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new
        {
            success = true,
            provider = ChannelIntegration.ProviderTrendyol,
            status = integration.Status,
            displayName = integration.DisplayName,
            verifiedMetadata = verifiedMetadataObj,
            integration = ToIntegrationDto(integration, origin)
        });
    }

    private sealed record IncomingExternalMessage(
        string SenderId,
        string? SenderDisplayName,
        string Text,
        string? ExternalMessageId);

    private static List<IncomingExternalMessage> ParseIncomingWebhookMessages(string provider, JsonElement root)
    {
        var list = new List<IncomingExternalMessage>();

        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            // 1. WhatsApp Cloud API format: entry[].changes[].value.messages[]
            if (entry.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
            {
                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var val) || val.ValueKind != JsonValueKind.Object)
                        continue;

                    var contactNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (val.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var contact in contacts.EnumerateArray())
                        {
                            var waId = TryGetString(contact, "wa_id");
                            string? profileName = null;
                            if (contact.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
                            {
                                profileName = TryGetString(profile, "name");
                            }
                            if (!string.IsNullOrWhiteSpace(waId) && !string.IsNullOrWhiteSpace(profileName))
                            {
                                contactNames[waId] = profileName!;
                            }
                        }
                    }

                    if (val.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var msg in messages.EnumerateArray())
                        {
                            var from = TryGetString(msg, "from");
                            var msgId = TryGetString(msg, "id");
                            string? bodyText = null;

                            if (msg.TryGetProperty("text", out var textObj) && textObj.ValueKind == JsonValueKind.Object)
                            {
                                bodyText = TryGetString(textObj, "body");
                            }
                            else if (msg.TryGetProperty("button", out var btnObj) && btnObj.ValueKind == JsonValueKind.Object)
                            {
                                bodyText = TryGetString(btnObj, "text");
                            }
                            else if (msg.TryGetProperty("interactive", out var interObj) && interObj.ValueKind == JsonValueKind.Object)
                            {
                                if (interObj.TryGetProperty("button_reply", out var br) && br.ValueKind == JsonValueKind.Object)
                                    bodyText = TryGetString(br, "title");
                                else if (interObj.TryGetProperty("list_reply", out var lr) && lr.ValueKind == JsonValueKind.Object)
                                    bodyText = TryGetString(lr, "title");
                            }

                            if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(bodyText))
                            {
                                contactNames.TryGetValue(from!, out var senderName);
                                list.Add(new IncomingExternalMessage(from!, senderName, bodyText!, msgId));
                            }
                        }
                    }
                }
            }

            // 2. Instagram Messaging API format: entry[].messaging[]
            if (entry.TryGetProperty("messaging", out var messagingArr) && messagingArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var messaging in messagingArr.EnumerateArray())
                {
                    string? senderId = null;
                    if (messaging.TryGetProperty("sender", out var senderObj) && senderObj.ValueKind == JsonValueKind.Object)
                    {
                        senderId = TryGetString(senderObj, "id");
                    }

                    if (!messaging.TryGetProperty("message", out var msgObj) || msgObj.ValueKind != JsonValueKind.Object)
                        continue;

                    // Ignore echo messages sent by the business page itself
                    if (msgObj.TryGetProperty("is_echo", out var echoProp) && echoProp.ValueKind == JsonValueKind.True)
                        continue;

                    var mid = TryGetString(msgObj, "mid");
                    var text = TryGetString(msgObj, "text");

                    if (!string.IsNullOrWhiteSpace(senderId) && !string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(new IncomingExternalMessage(senderId!, null, text!, mid));
                    }
                }
            }
        }

        return list;
    }

    private static async Task ProcessIncomingExternalMessageAsync(
        MaicDbContext db,
        KnowledgeRetriever retriever,
        ITextGenerator textGenerator,
        TimeProvider timeProvider,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        Workspace workspace,
        ChannelIntegration integration,
        IncomingExternalMessage incoming,
        CancellationToken cancellationToken)
    {
        var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
        var channelName = integration.Provider switch
        {
            ChannelIntegration.ProviderWhatsApp => "WhatsApp Business",
            ChannelIntegration.ProviderInstagram => "Instagram DM",
            _ => "Trendyol Soru-Cevap"
        };

        var channel = await EnsureNamedChannelAsync(db, workspace.Id, channelName, agent.Id, cancellationToken);

        var identityKind = integration.Provider switch
        {
            ChannelIntegration.ProviderWhatsApp => "whatsapp",
            ChannelIntegration.ProviderInstagram => "instagram",
            _ => "trendyol"
        };

        var cleanSenderId = incoming.SenderId.Trim();
        var existingIdentity = await db.ContactIdentities.AsNoTracking()
            .SingleOrDefaultAsync(ci => ci.WorkspaceId == workspace.Id
                                        && ci.Kind == identityKind
                                        && ci.Value == cleanSenderId, cancellationToken);

        Contact contact;
        if (existingIdentity is not null)
        {
            contact = await db.Contacts
                .Include(c => c.Identities)
                .SingleAsync(c => c.WorkspaceId == workspace.Id && c.Id == existingIdentity.ContactId, cancellationToken);

            if (!string.IsNullOrWhiteSpace(incoming.SenderDisplayName)
                && (contact.DisplayName.StartsWith("WhatsApp ", StringComparison.OrdinalIgnoreCase)
                    || contact.DisplayName.StartsWith("Instagram ", StringComparison.OrdinalIgnoreCase)))
            {
                contact.UpdateLeadCard(incoming.SenderDisplayName, contact.Email, contact.Phone, contact.Notes, null);
            }
        }
        else
        {
            var defaultDisplayName = !string.IsNullOrWhiteSpace(incoming.SenderDisplayName)
                ? incoming.SenderDisplayName.Trim()
                : integration.Provider == ChannelIntegration.ProviderWhatsApp
                    ? $"WhatsApp +{cleanSenderId.TrimStart('+')}"
                    : $"Instagram @{cleanSenderId}";

            if (defaultDisplayName.Length > 200) defaultDisplayName = defaultDisplayName[..200];

            contact = new Contact(
                workspace.Id,
                defaultDisplayName,
                [new ContactIdentityValue(identityKind, cleanSenderId)]);

            if (integration.Provider == ChannelIntegration.ProviderWhatsApp)
            {
                contact.UpdateLeadCard(defaultDisplayName, null, $"+{cleanSenderId.TrimStart('+')}", null, ["WhatsApp"]);
            }
            else if (integration.Provider == ChannelIntegration.ProviderInstagram)
            {
                contact.UpdateLeadCard(defaultDisplayName, null, null, null, ["Instagram"]);
            }

            db.Contacts.Add(contact);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Find open conversation for this contact & channel, or create a new GuestSession + Conversation
        var conversation = await db.Conversations
            .Where(c => c.WorkspaceId == workspace.Id
                        && c.ChannelId == channel.Id
                        && c.ContactId == contact.Id
                        && c.Status == ConversationState.OPEN)
            .OrderByDescending(c => c.LastActivityAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (conversation is null || !conversation.CreatedByGuestSessionId.HasValue)
        {
            var now = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
            var digestBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"ext:{workspace.Id}:{channel.Id}:{contact.Id}:{Guid.NewGuid():N}"));
            var credentialHash = Convert.ToHexString(digestBytes); // Uppercase 64-char hex

            var guestSession = new GuestSession(
                workspace.Id,
                channel.Id,
                contact.Id,
                credentialHash,
                channel.GuestAccessGeneration,
                now);
            db.GuestSessions.Add(guestSession);

            conversation = Conversation.CreateGuest(guestSession);
            conversation.BindAiAgent(agent.Id);
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(cancellationToken);
        }

        var cleanText = incoming.Text.Trim();
        if (cleanText.Length > 4000) cleanText = cleanText[..4000];
        if (!Message.IsValidContent(cleanText)) return;

        var clientMsgId = CreateDeterministicGuid(
            $"webhook-msg:{workspace.Id}:{integration.Provider}:{incoming.ExternalMessageId ?? Guid.NewGuid().ToString("N")}");

        var alreadyRecorded = await db.Messages.AsNoTracking()
            .AnyAsync(m => m.WorkspaceId == workspace.Id
                           && m.ConversationId == conversation.Id
                           && m.SenderGuestSessionId == conversation.CreatedByGuestSessionId
                           && m.ClientMessageId == clientMsgId, cancellationToken);
        if (alreadyRecorded)
        {
            return;
        }

        var guestMessage = Message.CreateGuest(
            conversation,
            conversation.CreatedByGuestSessionId!.Value,
            clientMsgId,
            conversation.AdvanceMessageSequence(),
            cleanText);

        conversation.IncrementUnread(1, guestMessage.CreatedAt);
        db.Messages.Add(guestMessage);
        await db.SaveChangesAsync(cancellationToken);

        // If conversation is in HUMAN_ACTIVE mode or assigned to an agent, do not run AI
        var isHumanActive = conversation.AssignedUserId is not null
            || string.Equals(conversation.Mode, Conversation.ModeHumanActive, StringComparison.OrdinalIgnoreCase);
        if (isHumanActive)
        {
            return;
        }

        var isPendingHandoff = string.Equals(conversation.Mode, Conversation.ModePendingHandoff, StringComparison.OrdinalIgnoreCase);
        if (isPendingHandoff)
        {
            return;
        }

        var actorUserId = await ResolveWorkspaceOwnerIdAsync(db, workspace.Id, cancellationToken);
        if (actorUserId == Guid.Empty)
        {
            return;
        }

        // Check if customer message requests human support
        if (WidgetEndpoints.RequestsHumanEscalation(cleanText))
        {
            const string handoffNotice = "Talebiniz müşteri temsilcimize iletildi, ekip arkadaşımız kısa süre içinde sohbete katılacaktır.";
            await PersistAiReplyMessageAsync(
                db, timeProvider, workspace, conversation, agent, guestMessage, actorUserId,
                handoffNotice, outcome: "HANDOFF", requestHandoff: true, cancellationToken);

            await SendOutboundProviderMessageAsync(
                httpClientFactory, logger, integration, cleanSenderId, handoffNotice, cancellationToken);
            return;
        }

        var isManualOnly = string.Equals(workspace.AiScheduleMode, "MANUAL_ONLY", StringComparison.OrdinalIgnoreCase);
        if (isManualOnly || !agent.Enabled)
        {
            return;
        }

        await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
        var planDef = await db.PlanDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Plan == workspace.Plan, cancellationToken)
            ?? DynamicRbacSeeder.GetDefaultPlanDefinition(workspace.Plan);

        var effectiveMonthlyCap = workspace.MonthlyMessageCap > 0
            ? workspace.MonthlyMessageCap
            : (planDef.MonthlyMessageLimit > 0 ? planDef.MonthlyMessageLimit : 5000);

        if (workspace.UsedMessagesThisMonth >= effectiveMonthlyCap)
        {
            return;
        }

        // Retrieve RAG passages + fallback chunks
        var knowledgeSnippets = new List<string>();
        try
        {
            var retrieval = await retriever.RetrieveAsync(
                new RetrievalRequest(
                    WorkspaceId: workspace.Id,
                    AgentId: agent.Id,
                    Query: cleanText,
                    VisitorKey: $"{identityKind}:{cleanSenderId}",
                    Preview: false),
                cancellationToken);

            foreach (var passage in retrieval.Passages)
            {
                knowledgeSnippets.Add(passage.Text);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fallback to DB chunks below
        }

        if (knowledgeSnippets.Count == 0)
        {
            var fallbackChunks = await WorkspaceSettingsEndpoints.LoadFallbackChunksAsync(
                db, workspace.Id, cleanText, cancellationToken);
            foreach (var chunk in fallbackChunks)
            {
                knowledgeSnippets.Add(chunk.Content);
            }
        }

        var (useCaseHint, assistantRoleHint) = await WorkspaceSettingsEndpoints.GetCatalogSystemPromptHintsAsync(
            db, workspace.UseCase, workspace.AssistantRole, cancellationToken);

        var systemPrompt = WorkspaceSettingsEndpoints.BuildPlaygroundSystemPrompt(
            workspace, agent, knowledgeSnippets, useCaseHint, assistantRoleHint);

        var historyRows = await db.Messages.AsNoTracking()
            .Where(m => m.WorkspaceId == workspace.Id
                        && m.ConversationId == conversation.Id
                        && m.Id != guestMessage.Id)
            .OrderByDescending(m => m.Sequence)
            .Take(10)
            .ToListAsync(cancellationToken);
        historyRows.Reverse();

        var genMessages = new List<TextGenerationMessage>
        {
            new(TextGenerationRole.System, systemPrompt)
        };

        foreach (var row in historyRows)
        {
            var role = row.SenderKind == MessageSenderKind.AI
                ? TextGenerationRole.Assistant
                : TextGenerationRole.User;
            var msgText = row.Content.Length > 2000 ? row.Content[..2000] : row.Content;
            genMessages.Add(new TextGenerationMessage(role, msgText));
        }

        genMessages.Add(new TextGenerationMessage(TextGenerationRole.User, cleanText));

        string aiReplyText;
        try
        {
            var generated = await textGenerator.GenerateAsync(
                new TextGenerationRequest(agent.ModelTier, genMessages, 1024),
                cancellationToken);
            aiReplyText = generated.Text.Trim();
        }
        catch (TextGenerationException)
        {
            aiReplyText = WorkspaceSettingsEndpoints.BuildFallbackReply(workspace, cleanText, knowledgeSnippets);
        }

        if (aiReplyText.Length > 4000) aiReplyText = aiReplyText[..4000];
        if (!Message.IsValidContent(aiReplyText))
        {
            aiReplyText = WorkspaceSettingsEndpoints.BuildFallbackReply(workspace, cleanText, []);
        }

        var quotaUpdated = await db.Workspaces
            .Where(w => w.Id == workspace.Id && w.UsedMessagesThisMonth < effectiveMonthlyCap)
            .ExecuteUpdateAsync(
                s => s.SetProperty(w => w.UsedMessagesThisMonth, w => w.UsedMessagesThisMonth + 1),
                cancellationToken);

        if (quotaUpdated == 0)
        {
            return;
        }

        await PersistAiReplyMessageAsync(
            db, timeProvider, workspace, conversation, agent, guestMessage, actorUserId,
            aiReplyText, outcome: "ANSWERED", requestHandoff: false, cancellationToken);

        // Send the generated AI reply back to the customer's real WhatsApp / Instagram account!
        await SendOutboundProviderMessageAsync(
            httpClientFactory, logger, integration, cleanSenderId, aiReplyText, cancellationToken);
    }

    /// <summary>
    /// Step 5: Called when a human agent replies from the Inbox (POST /workspaces/{w}/conversations/{id}/messages).
    /// If the conversation belongs to a WhatsApp or Instagram channel/contact, dispatches the message to Meta Graph API.
    /// </summary>
    public static async Task TryDispatchOutboundAgentMessageAsync(
        MaicDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        Guid workspaceId,
        Guid conversationId,
        string? messageContent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageContent)) return;

        var convInfo = await db.Conversations.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.Id == conversationId)
            .Join(
                db.Channels.AsNoTracking(),
                c => new { c.WorkspaceId, Id = c.ChannelId },
                ch => new { ch.WorkspaceId, ch.Id },
                (c, ch) => new { c.ContactId, ChannelName = ch.Name })
            .SingleOrDefaultAsync(cancellationToken);

        if (convInfo is null) return;

        var identities = await db.ContactIdentities.AsNoTracking()
            .Where(ci => ci.WorkspaceId == workspaceId && ci.ContactId == convInfo.ContactId)
            .ToListAsync(cancellationToken);

        string? provider = null;
        string? recipientId = null;

        var waIdentity = identities.FirstOrDefault(i => string.Equals(i.Kind, "whatsapp", StringComparison.OrdinalIgnoreCase));
        var igIdentity = identities.FirstOrDefault(i => string.Equals(i.Kind, "instagram", StringComparison.OrdinalIgnoreCase));

        if (waIdentity is not null
            || convInfo.ChannelName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
        {
            provider = ChannelIntegration.ProviderWhatsApp;
            recipientId = waIdentity?.Value;
            if (string.IsNullOrWhiteSpace(recipientId))
            {
                var contactPhone = await db.Contacts.AsNoTracking()
                    .Where(c => c.WorkspaceId == workspaceId && c.Id == convInfo.ContactId)
                    .Select(c => c.Phone)
                    .FirstOrDefaultAsync(cancellationToken);
                recipientId = contactPhone?.TrimStart('+');
            }
        }
        else if (igIdentity is not null
                 || convInfo.ChannelName.Contains("Instagram", StringComparison.OrdinalIgnoreCase))
        {
            provider = ChannelIntegration.ProviderInstagram;
            recipientId = igIdentity?.Value;
        }

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(recipientId))
        {
            return;
        }

        var integration = await db.ChannelIntegrations.AsNoTracking()
            .SingleOrDefaultAsync(i => i.WorkspaceId == workspaceId
                                       && i.Provider == provider
                                       && i.Status == ChannelIntegration.StatusConnected,
                cancellationToken);

        if (integration is null
            || string.IsNullOrWhiteSpace(integration.ExternalAccountId)
            || string.IsNullOrWhiteSpace(integration.AccessToken))
        {
            return;
        }

        await SendOutboundProviderMessageAsync(
            httpClientFactory, logger, integration, recipientId, messageContent.Trim(), cancellationToken);
    }

    public static async Task<bool> SendOutboundProviderMessageAsync(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        ChannelIntegration integration,
        string recipientExternalId,
        string messageText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(integration.ExternalAccountId)
            || string.IsNullOrWhiteSpace(integration.AccessToken)
            || string.IsNullOrWhiteSpace(recipientExternalId)
            || string.IsNullOrWhiteSpace(messageText))
        {
            return false;
        }

        var url = $"{GraphApiBaseUrl}/{Uri.EscapeDataString(integration.ExternalAccountId)}/messages";
        object payload = integration.Provider switch
        {
            ChannelIntegration.ProviderWhatsApp => new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = recipientExternalId.TrimStart('+'),
                type = "text",
                text = new
                {
                    preview_url = false,
                    body = messageText
                }
            },
            ChannelIntegration.ProviderInstagram => new
            {
                recipient = new { id = recipientExternalId },
                message = new { text = messageText }
            },
            _ => new { }
        };

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", integration.AccessToken);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await client.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Outbound {Provider} message delivery returned HTTP {StatusCode}: {Body}",
                    integration.Provider,
                    (int)resp.StatusCode,
                    errBody);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Failed to send outbound {Provider} message for WorkspaceId={WorkspaceId}",
                integration.Provider,
                integration.WorkspaceId);
            return false;
        }
    }

    private static async Task PersistAiReplyMessageAsync(
        MaicDbContext db,
        TimeProvider timeProvider,
        Workspace workspace,
        Conversation conversation,
        AiAgent agent,
        Message triggerGuestMessage,
        Guid actorUserId,
        string replyText,
        string outcome,
        bool requestHandoff,
        CancellationToken cancellationToken)
    {
        var execNow = timeProvider.GetUtcNow();
        var agentSnapshotJson = JsonSerializer.Serialize(new AiAgentSettings(
            agent.Name,
            agent.Enabled,
            agent.Instructions,
            agent.Language,
            agent.ModelTier,
            agent.KnowledgeScope,
            [],
            agent.HandoffOnRequest,
            agent.HandoffWhenUnanswered,
            agent.MaxAiReplies));

        var aiExecution = new AiExecution(
            workspace.Id,
            conversation.Id,
            triggerGuestMessage.Id,
            actorUserId,
            agent.Id,
            agentSnapshotJson,
            conversation.AiBindingRevision,
            triggerGuestMessage.Sequence,
            conversation.LastAssignmentSequence,
            conversation.LastHandoffSequence,
            execNow,
            execNow.AddSeconds(45));

        aiExecution.Complete(outcome, requestHandoff ? "HUMAN_REQUEST" : null, execNow);

        if (requestHandoff)
        {
            conversation.SetMode(Conversation.ModePendingHandoff, execNow);
            var hasExistingPending = await db.HandoffRequests.AsNoTracking()
                .AnyAsync(h => h.WorkspaceId == workspace.Id
                               && h.ConversationId == conversation.Id
                               && h.Status == HandoffState.PENDING, cancellationToken);
            if (!hasExistingPending)
            {
                var handoff = conversation.RequestHandoff(actorUserId, Guid.NewGuid(), aiExecution.Id);
                db.HandoffRequests.Add(handoff);
            }
        }

        var aiMessage = Message.CreateAi(
            workspace.Id,
            conversation.Id,
            aiExecution.Id,
            conversation.AdvanceMessageSequence(),
            replyText);
        conversation.TouchActivity(aiMessage.CreatedAt);

        db.AiExecutions.Add(aiExecution);
        db.Messages.Add(aiMessage);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static async Task<IReadOnlyList<ChannelIntegration>> EnsureWorkspaceIntegrationsAsync(
        MaicDbContext db,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var existing = await db.ChannelIntegrations
            .Where(i => i.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        var added = false;
        foreach (var provider in ChannelIntegration.SupportedProviders)
        {
            if (!existing.Any(i => string.Equals(i.Provider, provider, StringComparison.OrdinalIgnoreCase)))
            {
                var created = new ChannelIntegration(workspaceId, provider);
                db.ChannelIntegrations.Add(created);
                existing.Add(created);
                added = true;
            }
        }

        if (added)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return existing
            .OrderBy(i => Array.IndexOf(ChannelIntegration.SupportedProviders, i.Provider))
            .ToList();
    }

    internal static async Task<Channel> EnsureNamedChannelAsync(
        MaicDbContext db,
        Guid workspaceId,
        string channelName,
        Guid defaultAgentId,
        CancellationToken cancellationToken)
    {
        var existing = await db.Channels
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Name == channelName, cancellationToken);

        if (existing is null)
        {
            existing = new Channel(workspaceId, channelName, defaultAgentId);
            db.Channels.Add(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            if (!existing.Enabled) existing.SetEnabled(true);
            if (existing.DefaultAiAgentId is null) existing.SetDefaultAiAgent(defaultAgentId);
        }

        return existing;
    }

    private static async Task<(Workspace? Workspace, WorkspaceMember? Member, IResult? Error)> AuthorizeIntegrationAccessAsync(
        MaicDbContext db,
        ICurrentUser currentUser,
        Guid workspaceId,
        bool requireManage,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return (null, null, Results.Unauthorized());
        }

        var workspace = await db.Workspaces
            .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
        if (workspace is null)
        {
            return (null, null, Results.NotFound());
        }

        var isSuperAdmin = currentUser.IsSuperAdmin
            || await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsSuperAdmin, cancellationToken);

        var member = await db.WorkspaceMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, cancellationToken);

        if (isSuperAdmin)
        {
            return (workspace, member, null);
        }

        if (member is null)
        {
            return (null, null, Results.NotFound());
        }

        if (!requireManage)
        {
            return (workspace, member, null);
        }

        var hasChannelsManage = await db.WorkspaceRolePermissions.AsNoTracking()
            .AnyAsync(p => p.Role == member.Role && p.PermissionKey == "channels.manage" && p.IsGranted, cancellationToken);

        if (!hasChannelsManage && member.Role is not (WorkspaceRole.OWNER or WorkspaceRole.ADMIN))
        {
            return (null, null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return (workspace, member, null);
    }

    private static async Task<Guid> ResolveWorkspaceOwnerIdAsync(
        MaicDbContext db,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var ownerId = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .OrderBy(m => m.Role == WorkspaceRole.OWNER ? 0 : 1)
            .Select(m => m.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (ownerId != Guid.Empty) return ownerId;

        return await db.Users.AsNoTracking()
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public sealed record ChannelIntegrationDto(
        Guid Id,
        Guid WorkspaceId,
        string Provider,
        string Status,
        string? DisplayName,
        string ExternalAccountId,
        string? SecondaryAccountId,
        string? MaskedAccessToken,
        bool HasAppSecret,
        string WebhookUrl,
        string WebhookVerifyToken,
        JsonElement VerifiedMetadata,
        string VerifiedMetadataJson,
        DateTimeOffset? LastTestedAt,
        DateTimeOffset? LastWebhookReceivedAt,
        string? LastErrorMessage,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private static ChannelIntegrationDto ToIntegrationDto(ChannelIntegration integration, string origin)
    {
        JsonElement metadataElement;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(integration.VerifiedMetadataJson) ? "{}" : integration.VerifiedMetadataJson);
            metadataElement = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            metadataElement = doc.RootElement.Clone();
        }

        var providerSegment = integration.Provider.ToLowerInvariant();
        var webhookUrl = $"{origin.TrimEnd('/')}/webhooks/channels/{integration.WorkspaceId}/{providerSegment}";

        return new ChannelIntegrationDto(
            Id: integration.Id,
            WorkspaceId: integration.WorkspaceId,
            Provider: integration.Provider,
            Status: integration.Status,
            DisplayName: integration.DisplayName,
            ExternalAccountId: integration.ExternalAccountId,
            SecondaryAccountId: integration.SecondaryAccountId,
            MaskedAccessToken: MaskToken(integration.AccessToken),
            HasAppSecret: !string.IsNullOrWhiteSpace(integration.AppSecret),
            WebhookUrl: webhookUrl,
            WebhookVerifyToken: integration.WebhookVerifyToken,
            VerifiedMetadata: metadataElement,
            VerifiedMetadataJson: integration.VerifiedMetadataJson,
            LastTestedAt: integration.LastTestedAt,
            LastWebhookReceivedAt: integration.LastWebhookReceivedAt,
            LastErrorMessage: integration.LastErrorMessage,
            CreatedAt: integration.CreatedAt,
            UpdatedAt: integration.UpdatedAt);
    }

    private static string ResolveOrigin(HttpContext httpContext)
    {
        var scheme = httpContext.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
            ?? httpContext.Request.Scheme;
        var host = httpContext.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
            ?? httpContext.Request.Host.Value;

        if (string.IsNullOrWhiteSpace(host))
        {
            return "https://api.daribase.com";
        }

        return $"{scheme}://{host}";
    }

    private static string? MaskToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var trimmed = token.Trim();
        if (trimmed.Length <= 8) return "****";
        return $"{trimmed[..4]}...{trimmed[^4..]}";
    }

    private static string ExtractGraphErrorMessage(string responseBody, int statusCode)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("error", out var errorObj) && errorObj.ValueKind == JsonValueKind.Object)
                {
                    var message = TryGetString(errorObj, "message");
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message!;
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore JSON parse error
            }
        }

        return $"HTTP {statusCode} hatası döndü.";
    }

    private static string ExtractTrendyolErrorMessage(string responseBody, int statusCode)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("errors", out var errorsArr) && errorsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in errorsArr.EnumerateArray())
                    {
                        var msg = TryGetString(item, "message");
                        if (!string.IsNullOrWhiteSpace(msg)) return msg!;
                    }
                }

                var directMsg = TryGetString(doc.RootElement, "message") ?? TryGetString(doc.RootElement, "error");
                if (!string.IsNullOrWhiteSpace(directMsg)) return directMsg!;
            }
            catch (JsonException)
            {
                // Ignore JSON parse error
            }
        }

        return statusCode switch
        {
            401 => "Yetkisiz erişim (401): API Key, API Secret veya Satıcı ID hatalı.",
            403 => "Erişim reddedildi (403): Satıcı ID veya API yetkisi geçersiz.",
            404 => "Satıcı bulunamadı (404).",
            _ => $"HTTP {statusCode} hatası döndü."
        };
    }

    private static bool VerifyMetaHubSignature(byte[] rawBody, string appSecret, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;
        var trimmed = signatureHeader.Trim();
        const string prefix = "sha256=";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var providedHex = trimmed[prefix.Length..].Trim();
        var hmacBytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret.Trim()), rawBody);
        var computedHex = Convert.ToHexStringLower(hmacBytes);

        var left = Encoding.UTF8.GetBytes(providedHex.ToLowerInvariant());
        var right = Encoding.UTF8.GetBytes(computedHex);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static Guid CreateDeterministicGuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        // Ensure it is never Guid.Empty
        guidBytes[0] |= 0x01;
        return new Guid(guidBytes);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var prop))
        {
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.GetRawText(),
                _ => null
            };
        }
        return null;
    }
}
