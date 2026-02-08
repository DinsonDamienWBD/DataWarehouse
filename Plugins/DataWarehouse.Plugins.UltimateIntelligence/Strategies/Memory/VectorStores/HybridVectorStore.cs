using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// Strategy for hybrid store search operations.
/// </summary>
public enum HybridSearchStrategy
{
    /// <summary>Search primary only.</summary>
    PrimaryOnly,

    /// <summary>Search secondary only.</summary>
    SecondaryOnly,

    /// <summary>Search both and merge results.</summary>
    Both,

    /// <summary>Search both in parallel and merge results with RRF.</summary>
    ParallelRrf,

    /// <summary>Fallback to secondary if primary fails.</summary>
    PrimaryWithFallback
}

/// <summary>
/// Strategy for hybrid store write operations.
/// </summary>
public enum HybridWriteStrategy
{
    /// <summary>Write to primary only.</summary>
    PrimaryOnly,

    /// <summary>Write to both stores synchronously.</summary>
    Both,

    /// <summary>Write to primary, async replicate to secondary.</summary>
    PrimaryWithAsyncReplication,

    /// <summary>Write to secondary (cold) after TTL from primary (hot).</summary>
    HotColdTiering
}

/// <summary>
/// Configuration for hybrid vector store.
/// </summary>
public sealed class HybridVectorStoreOptions : VectorStoreOptions
{
    /// <summary>Search strategy to use.</summary>
    public HybridSearchStrategy SearchStrategy { get; init; } = HybridSearchStrategy.PrimaryWithFallback;

    /// <summary>Write strategy to use.</summary>
    public HybridWriteStrategy WriteStrategy { get; init; } = HybridWriteStrategy.Both;

    /// <summary>TTL in seconds before moving from hot to cold tier (for tiering).</summary>
    public int HotTierTtlSeconds { get; init; } = 86400; // 24 hours

    /// <summary>Weight for primary store in RRF merge (0.0 - 1.0).</summary>
    public float PrimaryWeight { get; init; } = 0.6f;

    /// <summary>RRF k parameter for rank fusion.</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Whether to deduplicate results by ID.</summary>
    public bool DeduplicateResults { get; init; } = true;

    /// <summary>Maximum total results to return from merged search.</summary>
    public int MaxMergedResults { get; init; } = 100;
}

/// <summary>
/// Hybrid vector store that combines multiple backends with hot/cold tiering.
/// </summary>
/// <remarks>
/// The hybrid store provides:
/// <list type="bullet">
/// <item>Hot/cold tiering for cost optimization</item>
/// <item>Fallback between primary and secondary stores</item>
/// <item>Parallel search with Reciprocal Rank Fusion (RRF)</item>
/// <item>Async replication for write performance</item>
/// <item>Automatic failover on store failures</item>
/// </list>
/// </remarks>
public sealed class HybridVectorStore : IProductionVectorStore
{
    private readonly IProductionVectorStore _primary;
    private readonly IProductionVectorStore? _secondary;
    private readonly HybridVectorStoreOptions _options;
    private readonly VectorStoreMetrics _metrics = new();
    private bool _isDisposed;

    /// <inheritdoc/>
    public string StoreId => $"hybrid-{_primary.StoreId}";

    /// <inheritdoc/>
    public string DisplayName => _secondary != null
        ? $"Hybrid ({_primary.DisplayName} + {_secondary.DisplayName})"
        : $"Hybrid ({_primary.DisplayName})";

    /// <inheritdoc/>
    public int VectorDimensions => _primary.VectorDimensions;

    /// <summary>
    /// Creates a new hybrid vector store.
    /// </summary>
    /// <param name="primary">Primary (hot) store.</param>
    /// <param name="secondary">Optional secondary (cold) store.</param>
    /// <param name="options">Hybrid store options.</param>
    public HybridVectorStore(
        IProductionVectorStore primary,
        IProductionVectorStore? secondary = null,
        HybridVectorStoreOptions? options = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary;
        _options = options ?? new HybridVectorStoreOptions();
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(
        string id,
        float[] vector,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        switch (_options.WriteStrategy)
        {
            case HybridWriteStrategy.PrimaryOnly:
                await _primary.UpsertAsync(id, vector, metadata, ct);
                break;

            case HybridWriteStrategy.Both:
                await _primary.UpsertAsync(id, vector, metadata, ct);
                if (_secondary != null)
                {
                    await _secondary.UpsertAsync(id, vector, metadata, ct);
                }
                break;

            case HybridWriteStrategy.PrimaryWithAsyncReplication:
                await _primary.UpsertAsync(id, vector, metadata, ct);
                if (_secondary != null)
                {
                    // Fire and forget async replication
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _secondary.UpsertAsync(id, vector, metadata, CancellationToken.None);
                        }
                        catch
                        {
                            // Log replication failure
                        }
                    });
                }
                break;

            case HybridWriteStrategy.HotColdTiering:
                // Write to hot tier only; background process handles tiering
                var enhancedMetadata = metadata != null
                    ? new Dictionary<string, object>(metadata)
                    : new Dictionary<string, object>();
                enhancedMetadata["_insertedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _primary.UpsertAsync(id, vector, enhancedMetadata, ct);
                break;
        }
    }

    /// <inheritdoc/>
    public async Task UpsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken ct = default)
    {
        var recordList = records.ToList();

        switch (_options.WriteStrategy)
        {
            case HybridWriteStrategy.PrimaryOnly:
                await _primary.UpsertBatchAsync(recordList, ct);
                break;

            case HybridWriteStrategy.Both:
                await _primary.UpsertBatchAsync(recordList, ct);
                if (_secondary != null)
                {
                    await _secondary.UpsertBatchAsync(recordList, ct);
                }
                break;

            case HybridWriteStrategy.PrimaryWithAsyncReplication:
                await _primary.UpsertBatchAsync(recordList, ct);
                if (_secondary != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _secondary.UpsertBatchAsync(recordList, CancellationToken.None);
                        }
                        catch
                        {
                            // Log replication failure
                        }
                    });
                }
                break;

            case HybridWriteStrategy.HotColdTiering:
                var tieredRecords = recordList.Select(r =>
                {
                    var meta = r.Metadata != null
                        ? new Dictionary<string, object>(r.Metadata)
                        : new Dictionary<string, object>();
                    meta["_insertedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return new VectorRecord(r.Id, r.Vector, meta);
                });
                await _primary.UpsertBatchAsync(tieredRecords, ct);
                break;
        }
    }

    /// <inheritdoc/>
    public async Task<VectorRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        // Try primary first
        var result = await _primary.GetAsync(id, ct);
        if (result != null) return result;

        // Fallback to secondary
        if (_secondary != null)
        {
            return await _secondary.GetAsync(id, ct);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        // Delete from both stores
        await _primary.DeleteAsync(id, ct);
        if (_secondary != null)
        {
            await _secondary.DeleteAsync(id, ct);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteBatchAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        await _primary.DeleteBatchAsync(idList, ct);
        if (_secondary != null)
        {
            await _secondary.DeleteBatchAsync(idList, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<VectorSearchResult>> SearchAsync(
        float[] query,
        int topK = 10,
        float minScore = 0f,
        Dictionary<string, object>? filter = null,
        CancellationToken ct = default)
    {
        switch (_options.SearchStrategy)
        {
            case HybridSearchStrategy.PrimaryOnly:
                return await _primary.SearchAsync(query, topK, minScore, filter, ct);

            case HybridSearchStrategy.SecondaryOnly when _secondary != null:
                return await _secondary.SearchAsync(query, topK, minScore, filter, ct);

            case HybridSearchStrategy.Both when _secondary != null:
                var primary = await _primary.SearchAsync(query, topK, minScore, filter, ct);
                var secondary = await _secondary.SearchAsync(query, topK, minScore, filter, ct);
                return MergeResults(primary, secondary, topK);

            case HybridSearchStrategy.ParallelRrf when _secondary != null:
                return await SearchParallelRrfAsync(query, topK, minScore, filter, ct);

            case HybridSearchStrategy.PrimaryWithFallback:
            default:
                try
                {
                    return await _primary.SearchAsync(query, topK, minScore, filter, ct);
                }
                catch when (_secondary != null)
                {
                    return await _secondary.SearchAsync(query, topK, minScore, filter, ct);
                }
        }
    }

    private async Task<IEnumerable<VectorSearchResult>> SearchParallelRrfAsync(
        float[] query,
        int topK,
        float minScore,
        Dictionary<string, object>? filter,
        CancellationToken ct)
    {
        var primaryTask = _primary.SearchAsync(query, _options.MaxMergedResults, minScore, filter, ct);
        var secondaryTask = _secondary!.SearchAsync(query, _options.MaxMergedResults, minScore, filter, ct);

        await Task.WhenAll(primaryTask, secondaryTask);

        var primaryResults = primaryTask.Result.ToList();
        var secondaryResults = secondaryTask.Result.ToList();

        return ReciprocatRankFusion(primaryResults, secondaryResults, topK);
    }

    private IEnumerable<VectorSearchResult> ReciprocatRankFusion(
        List<VectorSearchResult> primary,
        List<VectorSearchResult> secondary,
        int topK)
    {
        var rrfScores = new Dictionary<string, (float score, VectorSearchResult result)>();

        // Process primary results
        for (int i = 0; i < primary.Count; i++)
        {
            var result = primary[i];
            var rrfScore = _options.PrimaryWeight / (_options.RrfK + i + 1);
            rrfScores[result.Id] = (rrfScore, result);
        }

        // Process secondary results
        for (int i = 0; i < secondary.Count; i++)
        {
            var result = secondary[i];
            var rrfScore = (1 - _options.PrimaryWeight) / (_options.RrfK + i + 1);

            if (rrfScores.TryGetValue(result.Id, out var existing))
            {
                rrfScores[result.Id] = (existing.score + rrfScore, existing.result);
            }
            else
            {
                rrfScores[result.Id] = (rrfScore, result);
            }
        }

        return rrfScores.Values
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => new VectorSearchResult(x.result.Id, x.score, x.result.Vector, x.result.Metadata));
    }

    private IEnumerable<VectorSearchResult> MergeResults(
        IEnumerable<VectorSearchResult> primary,
        IEnumerable<VectorSearchResult> secondary,
        int topK)
    {
        var combined = primary.Concat(secondary);

        if (_options.DeduplicateResults)
        {
            combined = combined
                .GroupBy(r => r.Id)
                .Select(g => g.OrderByDescending(r => r.Score).First());
        }

        return combined
            .OrderByDescending(r => r.Score)
            .Take(topK);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<VectorSearchResult>> SearchByTextAsync(
        string text,
        IEmbeddingProvider embedder,
        int topK = 10,
        CancellationToken ct = default)
    {
        var vector = await embedder.EmbedAsync(text, ct);
        return await SearchAsync(vector, topK, ct: ct);
    }

    /// <inheritdoc/>
    public async Task CreateCollectionAsync(
        string name,
        int dimensions,
        DistanceMetric metric = DistanceMetric.Cosine,
        CancellationToken ct = default)
    {
        await _primary.CreateCollectionAsync(name, dimensions, metric, ct);
        if (_secondary != null)
        {
            await _secondary.CreateCollectionAsync(name, dimensions, metric, ct);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteCollectionAsync(string name, CancellationToken ct = default)
    {
        await _primary.DeleteCollectionAsync(name, ct);
        if (_secondary != null)
        {
            await _secondary.DeleteCollectionAsync(name, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CollectionExistsAsync(string name, CancellationToken ct = default)
    {
        return await _primary.CollectionExistsAsync(name, ct);
    }

    /// <inheritdoc/>
    public async Task<VectorStoreStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var primaryStats = await _primary.GetStatisticsAsync(ct);

        var extended = new Dictionary<string, object>(primaryStats.ExtendedStats)
        {
            ["primaryStore"] = _primary.StoreId,
            ["searchStrategy"] = _options.SearchStrategy.ToString(),
            ["writeStrategy"] = _options.WriteStrategy.ToString()
        };

        if (_secondary != null)
        {
            var secondaryStats = await _secondary.GetStatisticsAsync(ct);
            extended["secondaryStore"] = _secondary.StoreId;
            extended["secondaryVectorCount"] = secondaryStats.TotalVectors;
            extended["totalVectorCount"] = primaryStats.TotalVectors + secondaryStats.TotalVectors;
        }

        return new VectorStoreStatistics
        {
            TotalVectors = primaryStats.TotalVectors,
            Dimensions = primaryStats.Dimensions,
            StorageSizeBytes = primaryStats.StorageSizeBytes,
            CollectionCount = primaryStats.CollectionCount,
            QueryCount = _metrics.QueryCount + primaryStats.QueryCount,
            AverageQueryLatencyMs = primaryStats.AverageQueryLatencyMs,
            IndexType = $"Hybrid ({primaryStats.IndexType})",
            Metric = primaryStats.Metric,
            ExtendedStats = extended
        };
    }

    /// <inheritdoc/>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        var primaryHealthy = await _primary.IsHealthyAsync(ct);

        if (_secondary != null)
        {
            var secondaryHealthy = await _secondary.IsHealthyAsync(ct);

            // Healthy if at least one store is healthy
            return primaryHealthy || secondaryHealthy;
        }

        return primaryHealthy;
    }

    /// <summary>
    /// Migrates data from cold to hot tier based on access patterns.
    /// </summary>
    public async Task PromoteToColdAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        if (_secondary == null) return;

        foreach (var id in ids)
        {
            var record = await _primary.GetAsync(id, ct);
            if (record != null)
            {
                await _secondary.UpsertAsync(record.Id, record.Vector, record.Metadata, ct);
                await _primary.DeleteAsync(id, ct);
            }
        }
    }

    /// <summary>
    /// Migrates data from cold to hot tier.
    /// </summary>
    public async Task PromoteToHotAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        if (_secondary == null) return;

        foreach (var id in ids)
        {
            var record = await _secondary.GetAsync(id, ct);
            if (record != null)
            {
                await _primary.UpsertAsync(record.Id, record.Vector, record.Metadata, ct);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            await _primary.DisposeAsync();
            if (_secondary != null)
            {
                await _secondary.DisposeAsync();
            }
        }
    }
}
