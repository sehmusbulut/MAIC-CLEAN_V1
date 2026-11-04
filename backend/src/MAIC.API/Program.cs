using Scalar.AspNetCore;
using MAIC.API.AI;
using MAIC.Application.AI;
using MAIC.API.Conversations;
using MAIC.Application.Conversations;
using MAIC.API.Channels;
using MAIC.Application.Channels;
using MAIC.API.Contacts;
using MAIC.Application.Contacts;
using MAIC.API.Identity;
using MAIC.API.Realtime;
using MAIC.API.Widget;
using MAIC.API.Knowledge;
using MAIC.Application.Knowledge;
using MAIC.API.Workspaces;
using MAIC.Application.Workspaces;
using MAIC.Infrastructure.AI;
using MAIC.Infrastructure.Knowledge;
using MAIC.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
// Keep invalid JSON/body binding a client error in every environment.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddMaicIdentity(builder.Configuration);
builder.Services.AddConversationRealtime(builder.Configuration);
builder.Services.AddWidget(builder.Configuration);
builder.Services.AddScoped<IWorkspaceStore, WorkspaceStore>();
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<IMembershipStore, MembershipStore>();
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<IContactStore, ContactStore>();
builder.Services.AddScoped<ContactService>();
builder.Services.AddScoped<IChannelStore, ChannelStore>();
builder.Services.AddScoped<ChannelService>();
builder.Services.AddScoped<IConversationStore, ConversationStore>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddHostedService<AutoArchiveSweepService>();
builder.Services.AddScoped<IKnowledgeSourceStore, KnowledgeSourceStore>();
builder.Services.AddScoped<KnowledgeSourceService>();
var knowledgeLimits = (builder.Configuration.GetSection("KnowledgeLimits").Get<KnowledgeLimits>() ?? new KnowledgeLimits()).Validate();
builder.Services.AddMaicKnowledgeInfrastructure(() => builder.Configuration["KnowledgeStorage:RootPath"], knowledgeLimits,
    WebFetchOptions.ForDeployment(builder.Configuration.GetSection("KnowledgeFetch:BlockedNetworks").Get<string[]>()));
builder.Services.AddKnowledgeRateLimits(knowledgeLimits);

var geminiApiKey = builder.Configuration["GEMINI_API_KEY"]
                   ?? builder.Configuration["GOOGLE_API_KEY"]
                   ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                   ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

var embeddings = builder.Configuration.GetSection("Embeddings").Get<EmbeddingSettings>() ?? new EmbeddingSettings();
if (string.IsNullOrWhiteSpace(embeddings.ApiKey) && !string.IsNullOrWhiteSpace(geminiApiKey))
{
    embeddings = new EmbeddingSettings
    {
        Provider = embeddings.Provider,
        SpaceKey = embeddings.SpaceKey,
        Dimensions = embeddings.Dimensions,
        Model = embeddings.Model,
        Endpoint = embeddings.Endpoint,
        ApiKey = geminiApiKey.Trim(),
        AllowInsecureHttp = embeddings.AllowInsecureHttp,
        Proxy = embeddings.Proxy,
        SendDimensions = embeddings.SendDimensions,
        DocumentPrefix = embeddings.DocumentPrefix,
        QueryPrefix = embeddings.QueryPrefix,
        DocumentInputType = embeddings.DocumentInputType,
        QueryInputType = embeddings.QueryInputType,
        MaxBatchSize = embeddings.MaxBatchSize,
        MaxInputLength = embeddings.MaxInputLength,
        TimeoutSeconds = embeddings.TimeoutSeconds,
        ManageSchema = embeddings.ManageSchema,
        RetiredSpaceRetentionDays = embeddings.RetiredSpaceRetentionDays,
        MaxIndexedChunksPerWorkspacePerDay = embeddings.MaxIndexedChunksPerWorkspacePerDay,
        MaxIndexingChunksPerMinute = embeddings.MaxIndexingChunksPerMinute
    };
}
else if (embeddings.Provider == EmbeddingProvider.OpenAICompatible
         && string.IsNullOrWhiteSpace(embeddings.ApiKey)
         && embeddings.Endpoint?.StartsWith("https://generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) == true)
{
    embeddings = new EmbeddingSettings { Provider = EmbeddingProvider.None };
}

builder.Services.AddMaicEmbeddings(embeddings);
builder.Services.AddMaicRetrieval(builder.Configuration.GetSection("Retrieval").Get<RetrievalSettings>() ?? new RetrievalSettings(), embeddings);

var textGeneration = builder.Configuration.GetSection("TextGeneration").Get<TextGenerationSettings>() ?? new TextGenerationSettings();
if (string.IsNullOrWhiteSpace(textGeneration.ApiKey) && !string.IsNullOrWhiteSpace(geminiApiKey))
{
    textGeneration = new TextGenerationSettings
    {
        Provider = textGeneration.Provider,
        FastModel = textGeneration.FastModel,
        BalancedModel = textGeneration.BalancedModel,
        QualityModel = textGeneration.QualityModel,
        Endpoint = textGeneration.Endpoint,
        ApiKey = geminiApiKey.Trim(),
        AllowInsecureHttp = textGeneration.AllowInsecureHttp,
        Proxy = textGeneration.Proxy,
        TimeoutSeconds = textGeneration.TimeoutSeconds,
        MaxInputCharacters = textGeneration.MaxInputCharacters,
        MaxOutputTokens = textGeneration.MaxOutputTokens,
        TokenLimitParameter = textGeneration.TokenLimitParameter,
        EnableThinking = textGeneration.EnableThinking
    };
}
else if (textGeneration.Provider == TextGenerationProvider.OpenAICompatible
         && string.IsNullOrWhiteSpace(textGeneration.ApiKey)
         && textGeneration.Endpoint?.StartsWith("https://generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) == true)
{
    textGeneration = new TextGenerationSettings { Provider = TextGenerationProvider.None };
}

builder.Services.AddMaicTextGeneration(textGeneration);
var aiExecution = (builder.Configuration.GetSection("AiExecution").Get<AiExecutionSettings>() ?? new AiExecutionSettings()).Validate(textGeneration.Enabled);
if (textGeneration.Enabled && (textGeneration.MaxInputCharacters < aiExecution.MaxPromptCharacters || textGeneration.MaxOutputTokens < aiExecution.MaxOutputTokens))
    throw new InvalidOperationException("TextGeneration limits must cover AiExecution prompt and output limits.");
builder.Services.AddSingleton(aiExecution);
builder.Services.AddScoped<IConversationAiBindingStore, ConversationAiBindingStore>();
builder.Services.AddScoped<ConversationAiBindingService>();
builder.Services.AddScoped<IConversationAiExecutionStore, ConversationAiExecutionStore>();
builder.Services.AddScoped<IAiExecutionKnowledgeRetriever, AiExecutionKnowledgeRetriever>();
builder.Services.AddScoped<ConversationAiExecutionService>();

builder.Services.AddScoped<KnowledgeDocumentService>();
builder.Services.AddScoped<IAiAgentStore, AiAgentStore>();
builder.Services.AddScoped<AiAgentService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy(ChannelEndpoints.PublicBootstrapCorsPolicy,
        policy => policy.AllowAnyOrigin().WithMethods("GET"));
    options.AddPolicy(WidgetEndpoints.DaribaseWidgetCorsPolicy,
        policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("Retry-After"));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseConversationRealtimeOrigin();
app.UseHttpsRedirection();
app.UseCors();
app.UseWidgetBoundaries();
// Set this before authorization and JSON binding so errors also remain uncacheable.
app.Use(async (context, next) =>
{
    if (context.Request.Path.Value?.Split('/') is ["", var resource, _, var collection, ..]
        && string.Equals(resource, "workspaces", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(collection, "conversations", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "knowledge-sources", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "knowledge-base", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "settings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "widget-config", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "playground", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "overview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "team", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "billing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "integrations", StringComparison.OrdinalIgnoreCase)
            || string.Equals(collection, "ai-agents", StringComparison.OrdinalIgnoreCase)))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthentication();
app.UseConversationRealtimeAdmission();
app.UseAuthorization();
app.UseRateLimiter();
app.MapIdentityEndpoints();
app.MapWorkspaceEndpoints();
app.MapWorkspaceSettingsEndpoints();
app.MapTeamEndpoints();
app.MapOverviewAndBillingEndpoints();
app.MapSuperAdminPlanEndpoints();
app.MapMembershipEndpoints();
app.MapContactEndpoints();
app.MapChannelEndpoints();
app.MapChannelIntegrationEndpoints();
app.MapConversationEndpoints();
// Primary unified knowledge base endpoint (/workspaces/{workspaceId}/knowledge-base)
app.MapKnowledgeBaseEndpoints();
app.MapKnowledgeSourceEndpoints();
app.MapKnowledgeDocumentEndpoints();
app.MapAiAgentEndpoints();
app.MapConversationAiEndpoints();
app.MapConversationRealtime();
app.MapWidgetEndpoints();
app.MapScalarApiReference("/scalar");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.MapScalarApiReference("/docs", options =>
    {
        options.WithTitle("MAIC API");
    });
}

app.MapHealthChecks("/health");

await app.RunAsync();

public partial class Program { }
