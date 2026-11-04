using MAIC.Infrastructure.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MAIC.Infrastructure.Persistence;

/// <summary>
/// Operator commands run with migration credentials, so the API/Worker runtime role needs neither DDL rights nor DELETE
/// on <c>KnowledgeEmbeddingSpaces</c>: <c>MAIC.Worker register-embedding-space</c> registers the configured space and
/// builds its index, <c>MAIC.Worker retire-embedding-spaces</c> deletes spaces no host used for
/// <c>RetiredSpaceRetentionDays</c> (never the configured one). They require <c>ConnectionStrings:MaicMigrations</c> (pass it
/// as an environment variable, not a command-line argument, and keep it out of the long-running API/Worker environment);
/// <c>--use-runtime-connection</c> explicitly uses <c>ConnectionStrings:Maic</c> instead. Connection strings are never
/// printed, only their names.
/// </summary>
public static class EmbeddingSpaceCommands
{
    public const string Register = EmbeddingSpaceRegistry.RegisterCommand;
    public const string Retire = "retire-embedding-spaces";

    public static bool IsCommand(string[] args) => args is [Register or Retire, ..];

    /// <summary>Opt-in for deployments whose runtime role owns the schema (ManageSchema=true); otherwise MaicMigrations is required.</summary>
    public const string UseRuntimeConnection = "--use-runtime-connection";

    /// <summary>Returns 0 on success; on failure prints only the error message (no stack trace, no secrets) and returns 1.</summary>
    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(args, configuration, output, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or Npgsql.NpgsqlException)
        {
            // Npgsql messages carry no password; configuration and registry messages name settings, never values.
            await output.WriteLineAsync($"Error: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(string[] args, IConfiguration configuration, TextWriter output, CancellationToken cancellationToken)
    {
        var settings = (configuration.GetSection("Embeddings").Get<EmbeddingSettings>() ?? new EmbeddingSettings()).Validate();
        if (!settings.Enabled) throw new InvalidOperationException("Embeddings:Provider is None; there is no embedding space to manage.");
        var name = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("MaicMigrations")) ? "MaicMigrations"
            : args.Contains(UseRuntimeConnection) ? "Maic"
            : throw new InvalidOperationException(
                $"ConnectionStrings:MaicMigrations is required (pass {UseRuntimeConnection} to use ConnectionStrings:Maic when the runtime role owns the schema).");
        var connection = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException($"ConnectionStrings:{name} is required.");
        await output.WriteLineAsync($"Using ConnectionStrings:{name}.");
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(connection).Options);
        var registry = new EmbeddingSpaceRegistry(db);

        if (args[0] == Register)
        {
            await registry.EnsureAsync(settings.Space(), settings.Model!, cancellationToken, manageSchema: true);
            await output.WriteLineAsync($"Embedding space '{settings.Space().Key}' is registered and indexed.");
            return 0;
        }
        if (settings.RetiredSpaceRetentionDays < 1)
            throw new InvalidOperationException("Embeddings:RetiredSpaceRetentionDays must be at least 1 to retire spaces.");
        var retired = await registry.DeleteRetiredAsync(settings.Space(), settings.RetiredSpaceRetentionDays, manageSchema: true, cancellationToken);
        foreach (var space in retired)
            await output.WriteLineAsync($"Deleted retired embedding space '{space.Key}' with {space.VectorsDeleted} vectors.");
        await output.WriteLineAsync($"{retired.Count} retired embedding space(s) deleted.");
        return 0;
    }
}
