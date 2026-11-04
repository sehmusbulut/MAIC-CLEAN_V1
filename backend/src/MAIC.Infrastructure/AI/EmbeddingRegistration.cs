using MAIC.Application.AI;
using MAIC.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MAIC.Infrastructure.AI;

public static class EmbeddingRegistration
{
    /// <summary>
    /// Registers the embedding generator chosen by configuration, the pgvector store and the indexing services. Settings are
    /// validated here, so a host with a broken Embeddings section does not start; with a provider configured, the space is
    /// registered (or verified against the registry) when the host starts. Types are registered without factory lambdas so
    /// the RAG architecture test sees no other type depending on the raw ports. Requires a registered MaicDbContext.
    /// </summary>
    public static IServiceCollection AddMaicEmbeddings(this IServiceCollection services, EmbeddingSettings settings)
    {
        settings.Validate();
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        if (settings.Provider == EmbeddingProvider.OpenAICompatible)
            services.AddSingleton<IEmbeddingGenerator, OpenAICompatibleEmbeddingGenerator>();
        else
            services.AddSingleton<IEmbeddingGenerator, DisabledEmbeddingGenerator>();
        services.AddSingleton(new PgVectorSearchOptions());
        services.AddScoped<IKnowledgeVectorStore, PgVectorKnowledgeStore>();
        services.AddScoped<KnowledgeVectorIndex>();
        services.AddSingleton(new KnowledgeIndexLimits(settings.MaxIndexedChunksPerWorkspacePerDay, settings.MaxIndexingChunksPerMinute));
        services.AddScoped<IKnowledgeIndexStore, KnowledgeIndexStore>();
        services.AddScoped<KnowledgeIndexProcessor>();
        services.AddScoped<EmbeddingSpaceRegistry>();
        services.AddScoped<MAIC.Application.Knowledge.IKnowledgeIndexStatusReader, KnowledgeIndexStatusReader>();
        if (settings.Enabled) services.AddHostedService<EmbeddingSpaceStartupCheck>();
        return services;
    }
}
