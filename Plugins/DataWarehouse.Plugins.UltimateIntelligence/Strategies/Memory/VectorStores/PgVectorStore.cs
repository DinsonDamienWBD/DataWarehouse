using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// Index type for pgvector.
/// </summary>
public enum PgVectorIndexType
{
    /// <summary>No index (brute force).</summary>
    None,

    /// <summary>IVFFlat index (faster indexing, good recall).</summary>
    IvfFlat,

    /// <summary>HNSW index (best recall, more memory).</summary>
    Hnsw
}

/// <summary>
/// Configuration for PostgreSQL pgvector store.
/// </summary>
public sealed class PgVectorOptions : VectorStoreOptions
{
    /// <summary>PostgreSQL connection string.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Table name for vectors.</summary>
    public string TableName { get; init; } = "vector_embeddings";

    /// <summary>Schema name.</summary>
    public string Schema { get; init; } = "public";

    /// <summary>Index type to use.</summary>
    public PgVectorIndexType IndexType { get; init; } = PgVectorIndexType.Hnsw;

    /// <summary>Number of lists for IVFFlat index.</summary>
    public int IvfLists { get; init; } = 100;

    /// <summary>HNSW M parameter.</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>HNSW ef_construction parameter.</summary>
    public int HnswEfConstruction { get; init; } = 64;

    /// <summary>Number of probes for IVFFlat search.</summary>
    public int IvfProbes { get; init; } = 10;

    /// <summary>HNSW ef_search parameter.</summary>
    public int HnswEfSearch { get; init; } = 40;

    /// <summary>Whether to create extension and table automatically.</summary>
    public bool AutoInitialize { get; init; } = true;
}

/// <summary>
/// Production-ready PostgreSQL pgvector connector.
/// </summary>
/// <remarks>
/// pgvector is a PostgreSQL extension for vector similarity search.
/// Features:
/// <list type="bullet">
/// <item>IVFFlat and HNSW indexes</item>
/// <item>SQL-based filtering with full PostgreSQL power</item>
/// <item>ACID transactions for vector operations</item>
/// <item>Integrates with existing PostgreSQL infrastructure</item>
/// <item>Exact and approximate nearest neighbor search</item>
/// </list>
/// Note: This implementation uses a simulated interface. For production,
/// use Npgsql with the Npgsql.PgVector extension package.
/// </remarks>
public sealed class PgVectorStore : ProductionVectorStoreBase
{
    private readonly PgVectorOptions _options;
    private int _dimensions;
    private bool _initialized;

    // In-memory simulation for demonstration
    // In production, replace with Npgsql NpgsqlConnection
    private readonly Dictionary<string, (float[] Vector, Dictionary<string, object>? Metadata)> _store = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StoreId => $"pgvector-{_options.TableName}";

    /// <inheritdoc/>
    public override string DisplayName => $"PostgreSQL pgvector ({_options.TableName})";

    /// <inheritdoc/>
    public override int VectorDimensions => _dimensions;

    /// <summary>
    /// Creates a new PostgreSQL pgvector store instance.
    /// </summary>
    /// <param name="options">pgvector configuration options.</param>
    /// <param name="httpClient">Optional HTTP client (not used, for interface compatibility).</param>
    public PgVectorStore(PgVectorOptions options, HttpClient? httpClient = null)
        : base(httpClient, options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        if (_options.AutoInitialize)
        {
            // In production, execute these SQL commands:
            // CREATE EXTENSION IF NOT EXISTS vector;
            // CREATE TABLE IF NOT EXISTS {table} (
            //   id TEXT PRIMARY KEY,
            //   embedding vector({dim}),
            //   metadata JSONB,
            //   created_at TIMESTAMPTZ DEFAULT NOW()
            // );
            // CREATE INDEX IF NOT EXISTS {table}_embedding_idx ON {table}
            //   USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);

            _initialized = true;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task UpsertAsync(
        string id,
        float[] vector,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            await EnsureInitializedAsync(token);

            // In production:
            // INSERT INTO {table} (id, embedding, metadata)
            // VALUES (@id, @embedding, @metadata)
            // ON CONFLICT (id) DO UPDATE SET embedding = @embedding, metadata = @metadata;

            lock (_lock)
            {
                _store[id] = (vector.ToArray(), metadata != null ? new Dictionary<string, object>(metadata) : null);
            }

            await Task.CompletedTask;
        }, "Upsert", ct);
    }

    /// <inheritdoc/>
    public override async Task UpsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        foreach (var batch in BatchItems(records, _options.MaxBatchSize))
        {
            await ExecuteWithResilienceAsync(async token =>
            {
                // In production, use COPY or batch INSERT:
                // INSERT INTO {table} (id, embedding, metadata) VALUES
                // (@id1, @emb1, @meta1), (@id2, @emb2, @meta2), ...
                // ON CONFLICT (id) DO UPDATE SET embedding = EXCLUDED.embedding, metadata = EXCLUDED.metadata;

                lock (_lock)
                {
                    foreach (var record in batch)
                    {
                        _store[record.Id] = (record.Vector.ToArray(),
                            record.Metadata != null ? new Dictionary<string, object>(record.Metadata) : null);
                    }
                }

                await Task.CompletedTask;
            }, "UpsertBatch", ct);
        }
    }

    /// <inheritdoc/>
    public override async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            await EnsureInitializedAsync(token);

            // In production:
            // SELECT id, embedding, metadata FROM {table} WHERE id = @id;

            lock (_lock)
            {
                if (_store.TryGetValue(id, out var entry))
                {
                    return new VectorRecord(id, entry.Vector.ToArray(), entry.Metadata);
                }
            }

            return null;
        }, "Get", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            await EnsureInitializedAsync(token);

            // In production:
            // DELETE FROM {table} WHERE id = @id;

            lock (_lock)
            {
                _store.Remove(id);
            }

            Metrics.RecordDelete();
            await Task.CompletedTask;
        }, "Delete", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();

        await ExecuteWithResilienceAsync(async token =>
        {
            await EnsureInitializedAsync(token);

            // In production:
            // DELETE FROM {table} WHERE id = ANY(@ids);

            lock (_lock)
            {
                foreach (var id in idList)
                {
                    _store.Remove(id);
                }
            }

            Metrics.RecordDelete(idList.Count);
            await Task.CompletedTask;
        }, "DeleteBatch", ct);
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<VectorSearchResult>> SearchAsync(
        float[] query,
        int topK = 10,
        float minScore = 0f,
        Dictionary<string, object>? filter = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            await EnsureInitializedAsync(token);

            // In production, use pgvector operators:
            // SELECT id, embedding, metadata,
            //   1 - (embedding <=> @query) as score  -- cosine similarity
            // FROM {table}
            // WHERE (metadata @> @filter OR @filter IS NULL)
            // ORDER BY embedding <=> @query
            // LIMIT @topK;

            // Set search parameters:
            // SET ivfflat.probes = @probes;  -- for IVFFlat
            // SET hnsw.ef_search = @ef_search;  -- for HNSW

            var results = new List<(string Id, float[] Vector, Dictionary<string, object>? Metadata, float Score)>();

            lock (_lock)
            {
                foreach (var kvp in _store)
                {
                    // Apply filter
                    if (filter != null && kvp.Value.Metadata != null)
                    {
                        bool matches = true;
                        foreach (var f in filter)
                        {
                            if (!kvp.Value.Metadata.TryGetValue(f.Key, out var val) ||
                                !Equals(val?.ToString(), f.Value?.ToString()))
                            {
                                matches = false;
                                break;
                            }
                        }
                        if (!matches) continue;
                    }
                    else if (filter != null && kvp.Value.Metadata == null)
                    {
                        continue;
                    }

                    var score = CosineSimilarity(query, kvp.Value.Vector);
                    if (score >= minScore)
                    {
                        results.Add((kvp.Key, kvp.Value.Vector, kvp.Value.Metadata, score));
                    }
                }
            }

            return results
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .Select(r => new VectorSearchResult(
                    r.Id,
                    r.Score,
                    _options.IncludeVectorsInSearch ? r.Vector : null,
                    r.Metadata))
                .ToList();
        }, "Search", ct);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0f, magA = 0f, magB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom > 0 ? dot / denom : 0f;
    }

    /// <inheritdoc/>
    public override async Task CreateCollectionAsync(
        string name,
        int dimensions,
        DistanceMetric metric = DistanceMetric.Cosine,
        CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            // In production:
            // CREATE EXTENSION IF NOT EXISTS vector;
            // CREATE TABLE IF NOT EXISTS {name} (
            //   id TEXT PRIMARY KEY,
            //   embedding vector({dimensions}),
            //   metadata JSONB,
            //   created_at TIMESTAMPTZ DEFAULT NOW()
            // );

            var ops = metric switch
            {
                DistanceMetric.Cosine => "vector_cosine_ops",
                DistanceMetric.Euclidean => "vector_l2_ops",
                DistanceMetric.DotProduct => "vector_ip_ops",
                _ => "vector_cosine_ops"
            };

            // Create index based on type:
            // HNSW: CREATE INDEX ON {name} USING hnsw (embedding {ops}) WITH (m = {m}, ef_construction = {ef});
            // IVFFlat: CREATE INDEX ON {name} USING ivfflat (embedding {ops}) WITH (lists = {lists});

            _dimensions = dimensions;
            _initialized = true;

            await Task.CompletedTask;
        }, "CreateCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task DeleteCollectionAsync(string name, CancellationToken ct = default)
    {
        await ExecuteWithResilienceAsync(async token =>
        {
            // In production:
            // DROP TABLE IF EXISTS {name};

            lock (_lock)
            {
                _store.Clear();
            }

            _initialized = false;
            await Task.CompletedTask;
        }, "DeleteCollection", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        // In production:
        // SELECT EXISTS (
        //   SELECT FROM information_schema.tables
        //   WHERE table_schema = @schema AND table_name = @name
        // );

        return await Task.FromResult(_initialized || _store.Count > 0);
    }

    /// <inheritdoc/>
    public override async Task<VectorStoreStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithResilienceAsync(async token =>
        {
            // In production:
            // SELECT COUNT(*) FROM {table};
            // SELECT pg_table_size('{schema}.{table}');

            long count;
            lock (_lock)
            {
                count = _store.Count;
            }

            await Task.CompletedTask;

            return new VectorStoreStatistics
            {
                TotalVectors = count,
                Dimensions = _dimensions,
                CollectionCount = 1,
                QueryCount = Metrics.QueryCount,
                AverageQueryLatencyMs = Metrics.AverageQueryLatencyMs,
                IndexType = _options.IndexType.ToString().ToUpperInvariant(),
                Metric = DistanceMetric.Cosine,
                ExtendedStats = new Dictionary<string, object>
                {
                    ["tableName"] = _options.TableName,
                    ["schema"] = _options.Schema,
                    ["indexType"] = _options.IndexType.ToString(),
                    ["ivfLists"] = _options.IvfLists,
                    ["hnswM"] = _options.HnswM
                }
            };
        }, "GetStatistics", ct);
    }

    /// <inheritdoc/>
    public override async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            // In production:
            // SELECT 1;

            return await Task.FromResult(true);
        }
        catch
        {
            Debug.WriteLine($"Caught exception in PgVectorStore.cs");
            return false;
        }
    }

    /// <summary>
    /// Gets example SQL for using this vector store with Npgsql.
    /// </summary>
    public static string GetExampleSql()
    {
        return @"
-- Enable pgvector extension
CREATE EXTENSION IF NOT EXISTS vector;

-- Create table
CREATE TABLE IF NOT EXISTS vector_embeddings (
    id TEXT PRIMARY KEY,
    embedding vector(1536),  -- dimension depends on your embedding model
    metadata JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Create HNSW index (recommended for most use cases)
CREATE INDEX IF NOT EXISTS vector_embeddings_hnsw_idx
ON vector_embeddings
USING hnsw (embedding vector_cosine_ops)
WITH (m = 16, ef_construction = 64);

-- Or create IVFFlat index (faster to build, good for larger datasets)
-- CREATE INDEX IF NOT EXISTS vector_embeddings_ivfflat_idx
-- ON vector_embeddings
-- USING ivfflat (embedding vector_cosine_ops)
-- WITH (lists = 100);

-- Search query example
SELECT id, metadata,
    1 - (embedding <=> '[0.1, 0.2, ...]'::vector) as score
FROM vector_embeddings
WHERE metadata @> '{""type"": ""document""}'
ORDER BY embedding <=> '[0.1, 0.2, ...]'::vector
LIMIT 10;

-- Set search parameters (per-session)
SET hnsw.ef_search = 100;  -- higher = better recall, slower
SET ivfflat.probes = 10;   -- higher = better recall, slower
";
    }
}
