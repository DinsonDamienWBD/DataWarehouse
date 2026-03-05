using System;using System.Collections.Generic;using System.Net.Http;using System.Runtime.CompilerServices;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;using Npgsql;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.AI;

/// <summary>PostgreSQL pgvector extension. Npgsql connection for vector operations. Embedding storage and cosine/L2 similarity search.</summary>
public sealed class PgVectorConnectionStrategy : AiConnectionStrategyBase
{
    public override string StrategyId => "ai-pgvector";public override string DisplayName => "PostgreSQL pgvector";public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: false, SupportsAuthentication: true);public override string SemanticDescription => "PostgreSQL pgvector extension for vector similarity search. Cosine, L2, inner product distance with HNSW and IVFFlat indexes.";public override string[] Tags => ["pgvector", "postgresql", "vector-db", "sql", "embeddings", "hnsw"];
    public PgVectorConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString ?? throw new ArgumentException("PostgreSQL connection string required");
        var conn = new NpgsqlConnection(connectionString);
        // Finding 1794: Dispose NpgsqlConnection on any error to avoid resource leaks.
        try
        {
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
            await cmd.ExecuteNonQueryAsync(ct);
            return new DefaultConnectionHandle(conn, new Dictionary<string, object> { ["Provider"] = "PgVector", ["Database"] = conn.Database ?? "" });
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct){var conn = handle.GetConnection<NpgsqlConnection>();using var cmd = conn.CreateCommand();cmd.CommandText = "SELECT 1";var result = await cmd.ExecuteScalarAsync(ct);return result != null;}
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){var conn = handle.GetConnection<NpgsqlConnection>();await conn.CloseAsync();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();}
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct){try{var conn = handle.GetConnection<NpgsqlConnection>();using var cmd = conn.CreateCommand();cmd.CommandText = "SELECT extversion FROM pg_extension WHERE extname = 'vector'";var version = await cmd.ExecuteScalarAsync(ct);return new ConnectionHealth(version != null, version != null ? $"pgvector v{version} active" : "pgvector extension not found", TimeSpan.Zero, DateTimeOffset.UtcNow);}catch (Exception ex){return new ConnectionHealth(false, $"PgVector error: {ex.Message}", TimeSpan.Zero, DateTimeOffset.UtcNow);}}
    public override Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default)
    {
        // SQL injection risk: do not execute raw user-supplied strings as SQL.
        // Callers must use the vector search helpers (e.g. parameterized pgvector queries)
        // via the options bag rather than passing raw SQL in prompt.
        throw new NotSupportedException(
            "PgVectorConnectionStrategy does not accept raw SQL in SendRequestAsync to prevent SQL injection. " +
            "Pass a named operation via options[\"action\"] = \"vector_search\" and supply parameters via options[\"query\"] etc.");
    }
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default){ThrowStreamingNotSupported("PgVector does not support streaming responses. Use SendRequestAsync for SQL vector operations.");yield break;}
}
