using MAIC.Application.Knowledge;
using MAIC.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MAIC.Infrastructure.Knowledge;

public static class KnowledgeInfrastructure
{
    /// <summary>
    /// Registers document storage, extraction, fetching and ingestion persistence shared by the API and Worker.
    /// The storage root is resolved on first byte access, so reads that never touch bytes work without it.
    /// </summary>
    public static IServiceCollection AddMaicKnowledgeInfrastructure(this IServiceCollection services, Func<string?> storageRoot,
        KnowledgeLimits limits, WebFetchOptions fetchOptions)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(limits);
        services.AddSingleton<IKnowledgeFileStorage>(_ => new LocalKnowledgeFileStorage(storageRoot));
        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
        services.AddSingleton<IWebPageFetcher>(_ => new SafeWebPageFetcher(fetchOptions));
        services.AddScoped<IKnowledgeDocumentStore, KnowledgeDocumentStore>();
        services.AddScoped<IIngestionJobStore, IngestionJobStore>();
        services.AddScoped<IngestionProcessor>();
        services.AddScoped<IKnowledgeStorageIndex, KnowledgeStorageIndex>();
        services.AddScoped<KnowledgeStorageSweeper>();
        return services;
    }
}
