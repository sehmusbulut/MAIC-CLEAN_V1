using MAIC.Application.AI;
using MAIC.Domain.AI;
using MAIC.Infrastructure.AI;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class NvidiaProfileTests
{
    [Fact]
    public void Committed_profile_binds_valid_host_settings_without_a_live_provider_request()
    {
        var path = Path.GetFullPath("../../../../../nvidia.settings.example.json", AppContext.BaseDirectory);
        using var input = File.OpenRead(path);
        var configuration = new ConfigurationBuilder().AddJsonStream(input).AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Embeddings:ApiKey"] = "offline-placeholder",
                ["TextGeneration:ApiKey"] = "offline-placeholder"
            }).Build();
        Assert.Equal("", configuration["ApiKey"]);
        var embeddings = configuration.GetSection("Embeddings").Get<EmbeddingSettings>()!.Validate();
        var text = configuration.GetSection("TextGeneration").Get<TextGenerationSettings>()!.Validate();
        var execution = configuration.GetSection("AiExecution").Get<AiExecutionSettings>()!.Validate(requirePrices: true);
        Assert.Equal(EmbeddingProvider.OpenAICompatible, embeddings.Provider);
        Assert.Equal(2048, embeddings.Dimensions);
        Assert.Equal("nvidia/nemotron-3-embed-1b", embeddings.Model);
        Assert.Equal("passage", embeddings.DocumentInputType);
        Assert.Equal("query", embeddings.QueryInputType);
        Assert.False(embeddings.SendDimensions);
        Assert.Equal(new Uri("https://integrate.api.nvidia.com/v1/embeddings"), embeddings.EmbeddingsUri());
        Assert.Equal(TextGenerationProvider.OpenAICompatible, text.Provider);
        Assert.Equal("nvidia/nemotron-3.5-lightning-30b-a3b", text.Model(AiModelTier.FAST));
        Assert.Equal("nvidia/nemotron-3-super-120b-a12b", text.Model(AiModelTier.BALANCED));
        Assert.Equal(text.Model(AiModelTier.BALANCED), text.Model(AiModelTier.QUALITY));
        Assert.Equal(TextGenerationTokenLimitParameter.MaxTokens, text.TokenLimitParameter);
        Assert.False(text.EnableThinking);
        Assert.True(execution.ExecutionTimeoutSeconds > text.TimeoutSeconds + embeddings.TimeoutSeconds);
        Assert.True(execution.MaxOutputTokens <= text.MaxOutputTokens);
    }
}
