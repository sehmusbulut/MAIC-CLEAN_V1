using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MAIC.Application.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

/// <summary>Raw SQL helpers for tables EF does not map (vectors, embedding spaces, index states).</summary>
internal static partial class PgSql
{
    /// <summary>Runs <paramref name="work"/> in a transaction on the context's connection and commits it.</summary>
    public static async Task<T> InTransactionAsync<T>(MaicDbContext db, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work,
        CancellationToken cancellationToken, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(isolation, cancellationToken);
        var result = await work((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    /// <summary>
    /// A space key as a SQL literal. Keys match <c>^[a-z0-9][a-z0-9._-]{0,63}$</c> (checked again here), so they contain no
    /// quote; literals are used only where a per-space partial index must match the query at plan time.
    /// </summary>
    public static string SpaceLiteral(EmbeddingSpace space) =>
        SpaceKeyPattern().IsMatch(space.Key) ? $"'{space.Key}'" : throw new ArgumentException("Invalid space key.", nameof(space));

    /// <summary>
    /// The indexed expression for a space: vector(n) up to 2000 dimensions, halfvec(n) up to 4000 (pgvector HNSW limits),
    /// otherwise the column itself (no ANN index; exact scans).
    /// </summary>
    public static (string Expression, string Operators)? IndexedExpression(EmbeddingSpace space) => space.Dimensions switch
    {
        <= 2000 => ($"(\"Embedding\"::vector({space.Dimensions}))", "vector_cosine_ops"),
        <= 4000 => ($"(\"Embedding\"::halfvec({space.Dimensions}))", "halfvec_cosine_ops"),
        _ => null
    };

    public static string QueryCast(EmbeddingSpace space) => space.Dimensions switch
    {
        <= 2000 => $"vector({space.Dimensions})",
        <= 4000 => $"halfvec({space.Dimensions})",
        _ => "vector"
    };

    public static string IndexName(EmbeddingSpace space) =>
        "IX_KnowledgeChunkVectors_Hnsw_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(space.Key)))[..16];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}\z")]
    private static partial Regex SpaceKeyPattern();
}
