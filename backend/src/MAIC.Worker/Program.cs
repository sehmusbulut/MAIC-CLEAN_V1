using MAIC.Application.Knowledge;
using MAIC.Infrastructure.AI;
using MAIC.Infrastructure.Knowledge;
using MAIC.Infrastructure.Persistence;
using MAIC.Worker.Jobs;
using Microsoft.EntityFrameworkCore;

// Child mode: the Worker re-runs itself to parse one untrusted document in an isolated, memory-limited process.
if (args is [IsolatedExtractionOptions.Command, ..]) return await ExtractionChild.RunAsync(args);

var builder = Host.CreateApplicationBuilder(args);

// Operator commands (register-embedding-space, retire-embedding-spaces) run with migration credentials
// (ConnectionStrings:MaicMigrations, falling back to Maic), so the runtime role needs no DDL or space deletion rights.
if (EmbeddingSpaceCommands.IsCommand(args))
    return await EmbeddingSpaceCommands.RunAsync(args, builder.Configuration, Console.Out, CancellationToken.None);

var connection = builder.Configuration.GetConnectionString("Maic");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("ConnectionStrings:Maic is required for the worker.");
// The worker always reads uploads, so a missing storage root is a startup error rather than a per-job failure.
var storageRoot = builder.Configuration["KnowledgeStorage:RootPath"];
if (string.IsNullOrWhiteSpace(storageRoot))
    throw new InvalidOperationException("KnowledgeStorage:RootPath is required for the worker.");
builder.Services.AddDbContext<MaicDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddMaicKnowledgeInfrastructure(() => storageRoot,
    (builder.Configuration.GetSection("KnowledgeLimits").Get<KnowledgeLimits>() ?? new KnowledgeLimits()).Validate(),
    WebFetchOptions.ForDeployment(builder.Configuration.GetSection("KnowledgeFetch:BlockedNetworks").Get<string[]>()));
var embeddings = builder.Configuration.GetSection("Embeddings").Get<EmbeddingSettings>() ?? new EmbeddingSettings();
builder.Services.AddMaicEmbeddings(embeddings);
// Parsing untrusted files happens in a child process, never in the Worker itself.
builder.Services.AddSingleton(new IsolatedExtractionOptions());
builder.Services.AddSingleton<IsolatedDocumentTextExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractor>(services => services.GetRequiredService<IsolatedDocumentTextExtractor>());
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<StorageSweepWorker>();
// Without a provider nothing is indexed and the provider is never called.
if (embeddings.Enabled) builder.Services.AddHostedService<KnowledgeIndexWorker>();
// In-host retirement needs DELETE on KnowledgeEmbeddingSpaces; without schema management it is the operator's
// retire-embedding-spaces command instead.
if (embeddings.Enabled && embeddings.ManageSchema && embeddings.RetiredSpaceRetentionDays > 0)
    builder.Services.AddHostedService<EmbeddingSpaceSweepWorker>();

using var host = builder.Build();
if (builder.Configuration.GetSection("KnowledgeFetch:BlockedNetworks").Get<string[]>() is not { Length: > 0 })
    host.Services.GetRequiredService<ILogger<Program>>().LogWarning(
        "KnowledgeFetch:BlockedNetworks is empty. Configure your VPC/internal CIDRs (IPv4 and IPv6); this host's own " +
        "interface networks alone do not cover sibling subnets or cloud IPv6 prefixes assigned as /128.");
await host.Services.GetRequiredService<IsolatedDocumentTextExtractor>().VerifyAsync(CancellationToken.None);
await host.RunAsync();
return 0;
