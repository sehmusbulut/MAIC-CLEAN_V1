using MAIC.Application.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MAIC.Infrastructure.AI;

public static class TextGenerationRegistration
{
    /// <summary>Validates locally at startup. Registration never sends a paid request or a content probe.</summary>
    public static IServiceCollection AddMaicTextGeneration(this IServiceCollection services, TextGenerationSettings settings)
    {
        settings.Validate();
        services.AddSingleton(settings);
        if (settings.Provider == TextGenerationProvider.OpenAICompatible)
            services.AddSingleton<ITextGenerator, OpenAICompatibleTextGenerator>();
        else
            services.AddSingleton<ITextGenerator, DisabledTextGenerator>();
        return services;
    }
}

internal sealed class DisabledTextGenerator : ITextGenerator
{
    public bool Enabled => false;

    public Task<TextGenerationResult> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new TextGenerationException(TextGenerationFailure.MISCONFIGURED);
    }
}
