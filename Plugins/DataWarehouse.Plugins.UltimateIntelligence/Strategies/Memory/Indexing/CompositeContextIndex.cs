using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

/// <summary>
/// Composite index that combines all specialized indexes for comprehensive
/// context navigation. Routes queries to the most appropriate index based
/// on query characteristics and merges results intelligently.
///
/// Features:
/// - Query routing based on query type and available indexes
/// - Parallel index queries for performance
/// - Intelligent result merging and ranking
/// - Index health monitoring
/// - Automatic fallback to alternative indexes
/// </summary>
public sealed class CompositeContextIndex : ContextIndexBase
{
    private readonly Dictionary<string, IContextIndex> _indexes = new();
    private readonly ConcurrentDictionary<string, IndexHealth> _indexHealth = new();
    private readonly QueryRouter _router;

    /// <inheritdoc/>
    public override string IndexId => "index-composite";

    /// <inheritdoc/>
    public override string IndexName => "Composite Context Index";

    /// <summary>
    /// Gets the registered indexes.
    /// </summary>
    public IReadOnlyDictionary<string, IContextIndex> Indexes => _indexes;

    /// <summary>
    /// Initializes the composite index with default specialized indexes.
    /// </summary>
    public CompositeContextIndex()
    {
        _router = new QueryRouter();

        // Register default indexes
        RegisterIndex(new HierarchicalSummaryIndex());
        RegisterIndex(new SemanticClusterIndex());
        RegisterIndex(new EntityRelationshipIndex());
        RegisterIndex(new TemporalContextIndex());
        RegisterIndex(new TopicModelIndex());
        RegisterIndex(new CompressedManifestIndex());
    }

    /// <summary>
    /// Registers an index for use in the composite.
    /// </summary>
    /// <param name="index">The index to register.</param>
    public void RegisterIndex(IContextIndex index)
    {
        _indexes[index.IndexId] = index;
        _indexHealth[index.IndexId] = new IndexHealth
        {
            IndexId = index.IndexId,
            Status = IndexStatus.Healthy,
            LastChecked = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Unregisters an index.
    /// </summary>
    /// <param name="indexId">Index identifier to remove.</param>
    public void UnregisterIndex(string indexId)
    {
        _indexes.Remove(indexId);
        _indexHealth.TryRemove(indexId, out _);
    }

    /// <inheritdoc/>
    public override async Task<ContextQueryResult> QueryAsync(ContextQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Determine which indexes to query
            var selectedIndexes = _router.SelectIndexes(query, _indexes.Values.ToList(), _indexHealth);

            if (selectedIndexes.Count == 0)
            {
                // Fallback to querying all available indexes
                selectedIndexes = _indexes.Values.Where(i => i.IsAvailable).ToList();
            }

            // Query indexes in parallel
            var queryTasks = selectedIndexes
                .Select(async index =>
                {
                    try
                    {
                        var result = await index.QueryAsync(query, ct);
                        UpdateIndexHealth(index.IndexId, true, result.QueryDuration);
                        return (IndexId: index.IndexId, Result: result, Success: true);
                    }
                    catch (Exception ex)
                    {
                        UpdateIndexHealth(index.IndexId, false, TimeSpan.Zero, ex.Message);
                        return (IndexId: index.IndexId, Result: ContextQueryResult.Empty, Success: false);
                    }
                });

            var results = await Task.WhenAll(queryTasks);

            // Merge results
            var mergedResult = MergeResults(results.Where(r => r.Success).Select(r => r.Result).ToList(), query);

            sw.Stop();
            RecordQuery(sw.Elapsed);

            return mergedResult with { QueryDuration = sw.Elapsed };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            RecordQuery(sw.Elapsed);
            throw;
        }
    }

    /// <inheritdoc/>
    public override async Task<ContextNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        // Try each index that supports hierarchical navigation
        foreach (var index in _indexes.Values)
        {
            var node = await index.GetNodeAsync(nodeId, ct);
            if (node != null)
                return node;
        }
        return null;
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<ContextNode>> GetChildrenAsync(string? parentId, int depth = 1, CancellationToken ct = default)
    {
        // Aggregate children from all hierarchical indexes
        var allChildren = new List<ContextNode>();

        foreach (var index in _indexes.Values)
        {
            try
            {
                var children = await index.GetChildrenAsync(parentId, depth, ct);
                allChildren.AddRange(children);
            }
            catch
            {
                // Ignore errors from individual indexes
            }
        }

        // Deduplicate by NodeId
        return allChildren.GroupBy(c => c.NodeId).Select(g => g.First());
    }

    /// <inheritdoc/>
    public override async Task IndexContentAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default)
    {
        // Index in all registered indexes in parallel
        var indexTasks = _indexes.Values.Select(async index =>
        {
            try
            {
                await index.IndexContentAsync(contentId, content, metadata, ct);
                return (IndexId: index.IndexId, Success: true, Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (IndexId: index.IndexId, Success: false, Error: ex.Message);
            }
        });

        var results = await Task.WhenAll(indexTasks);

        // Log any failures
        foreach (var result in results.Where(r => !r.Success))
        {
            UpdateIndexHealth(result.IndexId, false, TimeSpan.Zero, result.Error);
        }

        MarkUpdated();
    }

    /// <inheritdoc/>
    public override async Task UpdateIndexAsync(string contentId, IndexUpdate update, CancellationToken ct = default)
    {
        var updateTasks = _indexes.Values.Select(index =>
            index.UpdateIndexAsync(contentId, update, ct));

        await Task.WhenAll(updateTasks);
        MarkUpdated();
    }

    /// <inheritdoc/>
    public override async Task RemoveFromIndexAsync(string contentId, CancellationToken ct = default)
    {
        var removeTasks = _indexes.Values.Select(index =>
            index.RemoveFromIndexAsync(contentId, ct));

        await Task.WhenAll(removeTasks);
        MarkUpdated();
    }

    /// <inheritdoc/>
    public override async Task<IndexStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        // Aggregate statistics from all indexes
        var statsTasks = _indexes.Values.Select(async index =>
        {
            try
            {
                return await index.GetStatisticsAsync(ct);
            }
            catch
            {
                return null;
            }
        });

        var allStats = (await Task.WhenAll(statsTasks)).Where(s => s != null).ToList();

        if (allStats.Count == 0)
        {
            return GetBaseStatistics(0, 0, 0, 0, 0);
        }

        // Take the max values for entries (they should be the same)
        var totalEntries = allStats.Max(s => s!.TotalEntries);
        var totalContent = allStats.Max(s => s!.TotalContentBytes);
        var indexSize = allStats.Sum(s => s!.IndexSizeBytes);
        var nodeCount = allStats.Sum(s => s!.NodeCount);
        var maxDepth = allStats.Max(s => s!.MaxDepth);

        var stats = GetBaseStatistics(totalEntries, totalContent, indexSize, nodeCount, maxDepth);

        // Merge tier breakdowns
        var byTier = allStats
            .Where(s => s!.EntriesByTier != null)
            .SelectMany(s => s!.EntriesByTier!)
            .GroupBy(kvp => kvp.Key)
            .ToDictionary(g => g.Key, g => g.Max(kvp => kvp.Value));

        return stats with { EntriesByTier = byTier };
    }

    /// <inheritdoc/>
    public override async Task OptimizeAsync(CancellationToken ct = default)
    {
        var optimizeTasks = _indexes.Values.Select(index => index.OptimizeAsync(ct));
        await Task.WhenAll(optimizeTasks);
        await base.OptimizeAsync(ct);
    }

    #region Index Health Monitoring

    /// <summary>
    /// Gets health status of all indexes.
    /// </summary>
    /// <returns>Health status for each index.</returns>
    public IList<IndexHealth> GetIndexHealth()
    {
        return _indexHealth.Values.ToList();
    }

    /// <summary>
    /// Gets a specific index's health.
    /// </summary>
    /// <param name="indexId">Index identifier.</param>
    /// <returns>Health status if found.</returns>
    public IndexHealth? GetIndexHealth(string indexId)
    {
        return _indexHealth.TryGetValue(indexId, out var health) ? health : null;
    }

    /// <summary>
    /// Performs a health check on all indexes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Overall health status.</returns>
    public async Task<CompositeHealthStatus> CheckHealthAsync(CancellationToken ct = default)
    {
        var healthTasks = _indexes.Select(async kvp =>
        {
            try
            {
                var stats = await kvp.Value.GetStatisticsAsync(ct);
                UpdateIndexHealth(kvp.Key, true, TimeSpan.Zero);
                return (IndexId: kvp.Key, Available: kvp.Value.IsAvailable, Stats: stats);
            }
            catch (Exception ex)
            {
                UpdateIndexHealth(kvp.Key, false, TimeSpan.Zero, ex.Message);
                return (IndexId: kvp.Key, Available: false, Stats: (IndexStatistics?)null);
            }
        });

        var results = await Task.WhenAll(healthTasks);

        var healthyCount = results.Count(r => r.Available);
        var totalCount = results.Length;

        return new CompositeHealthStatus
        {
            OverallStatus = healthyCount == totalCount ? IndexStatus.Healthy :
                           healthyCount > 0 ? IndexStatus.Degraded : IndexStatus.Unhealthy,
            HealthyIndexCount = healthyCount,
            TotalIndexCount = totalCount,
            IndexStatuses = _indexHealth.Values.ToList(),
            CheckedAt = DateTimeOffset.UtcNow
        };
    }

    private void UpdateIndexHealth(string indexId, bool success, TimeSpan latency, string? error = null)
    {
        if (!_indexHealth.TryGetValue(indexId, out var health))
        {
            health = new IndexHealth { IndexId = indexId };
        }

        var newHealth = health with
        {
            Status = success ? IndexStatus.Healthy : IndexStatus.Unhealthy,
            LastChecked = DateTimeOffset.UtcNow,
            LastError = success ? null : error,
            LastLatency = latency,
            ConsecutiveFailures = success ? 0 : health.ConsecutiveFailures + 1
        };

        _indexHealth[indexId] = newHealth;
    }

    #endregion

    #region Result Merging

    private ContextQueryResult MergeResults(IList<ContextQueryResult> results, ContextQuery query)
    {
        if (results.Count == 0)
            return ContextQueryResult.Empty;

        if (results.Count == 1)
            return results[0];

        // Merge entries, deduplicating by ContentId and taking highest relevance
        var mergedEntries = results
            .SelectMany(r => r.Entries)
            .GroupBy(e => e.ContentId)
            .Select(g => g.OrderByDescending(e => e.RelevanceScore).First())
            .OrderByDescending(e => e.RelevanceScore)
            .Take(query.MaxResults)
            .ToList();

        // Merge cluster counts
        Dictionary<string, int>? mergedClusters = null;
        if (query.ClusterResults)
        {
            mergedClusters = results
                .Where(r => r.ClusterCounts != null)
                .SelectMany(r => r.ClusterCounts!)
                .GroupBy(kvp => kvp.Key)
                .ToDictionary(g => g.Key, g => g.Sum(kvp => kvp.Value));
        }

        // Merge related topics
        var relatedTopics = results
            .Where(r => r.RelatedTopics != null)
            .SelectMany(r => r.RelatedTopics!)
            .Distinct()
            .Take(20)
            .ToArray();

        // Combine navigation summaries
        var navSummaries = results
            .Where(r => !string.IsNullOrEmpty(r.NavigationSummary))
            .Select(r => r.NavigationSummary!)
            .ToList();

        var combinedSummary = navSummaries.Count > 0
            ? string.Join(" ", navSummaries.Take(2))
            : null;

        return new ContextQueryResult
        {
            Entries = mergedEntries,
            NavigationSummary = combinedSummary,
            ClusterCounts = mergedClusters,
            TotalMatchingEntries = results.Sum(r => r.TotalMatchingEntries),
            QueryDuration = TimeSpan.Zero, // Will be set by caller
            SuggestedQueries = results.FirstOrDefault(r => r.SuggestedQueries != null)?.SuggestedQueries,
            RelatedTopics = relatedTopics.Length > 0 ? relatedTopics : null,
            WasTruncated = mergedEntries.Count >= query.MaxResults
        };
    }

    #endregion
}

#region Query Router

/// <summary>
/// Routes queries to the most appropriate indexes based on query characteristics.
/// </summary>
internal sealed class QueryRouter
{
    /// <summary>
    /// Selects the best indexes for a query.
    /// </summary>
    public IList<IContextIndex> SelectIndexes(
        ContextQuery query,
        IList<IContextIndex> availableIndexes,
        ConcurrentDictionary<string, IndexHealth> health)
    {
        var selected = new List<IContextIndex>();

        // Filter to healthy indexes
        var healthyIndexes = availableIndexes
            .Where(i => i.IsAvailable)
            .Where(i => !health.TryGetValue(i.IndexId, out var h) || h.Status != IndexStatus.Unhealthy)
            .ToList();

        if (healthyIndexes.Count == 0)
            return selected;

        // Determine query type and select appropriate indexes
        var queryType = AnalyzeQuery(query);

        foreach (var index in healthyIndexes)
        {
            var score = ScoreIndexForQuery(index, queryType, query);
            if (score > 0)
            {
                selected.Add(index);
            }
        }

        // If embedding-based query, prioritize semantic cluster index
        if (query.QueryEmbedding != null)
        {
            var semanticIndex = selected.FirstOrDefault(i => i.IndexId == "index-semantic-cluster");
            if (semanticIndex != null)
            {
                selected.Remove(semanticIndex);
                selected.Insert(0, semanticIndex);
            }
        }

        // Limit to top 3 indexes for performance
        return selected.Take(3).ToList();
    }

    private QueryType AnalyzeQuery(ContextQuery query)
    {
        var flags = QueryType.None;

        if (query.QueryEmbedding != null)
            flags |= QueryType.Semantic;

        if (query.TimeRange != null)
            flags |= QueryType.Temporal;

        if (query.RequiredTags?.Length > 0)
            flags |= QueryType.Tagged;

        var queryLower = query.SemanticQuery.ToLowerInvariant();

        if (queryLower.Contains("entity") || queryLower.Contains("relationship") || queryLower.Contains("related to"))
            flags |= QueryType.Entity;

        if (queryLower.Contains("topic") || queryLower.Contains("about"))
            flags |= QueryType.Topic;

        if (queryLower.Contains("overview") || queryLower.Contains("summary") || queryLower.Contains("hierarchy"))
            flags |= QueryType.Hierarchical;

        if (flags == QueryType.None)
            flags = QueryType.General;

        return flags;
    }

    private float ScoreIndexForQuery(IContextIndex index, QueryType queryType, ContextQuery query)
    {
        var score = 0.5f; // Base score

        switch (index.IndexId)
        {
            case "index-hierarchical-summary":
                if (queryType.HasFlag(QueryType.Hierarchical))
                    score += 0.5f;
                if (query.DetailLevel == SummaryLevel.Full)
                    score += 0.2f;
                break;

            case "index-semantic-cluster":
                if (queryType.HasFlag(QueryType.Semantic))
                    score += 0.5f;
                if (query.ClusterResults)
                    score += 0.3f;
                break;

            case "index-entity-relationship":
                if (queryType.HasFlag(QueryType.Entity))
                    score += 0.5f;
                break;

            case "index-temporal-context":
                if (queryType.HasFlag(QueryType.Temporal))
                    score += 0.5f;
                if (query.TimeRange != null)
                    score += 0.3f;
                break;

            case "index-topic-model":
                if (queryType.HasFlag(QueryType.Topic))
                    score += 0.5f;
                break;

            case "index-compressed-manifest":
                if (queryType.HasFlag(QueryType.Tagged))
                    score += 0.3f;
                // Good for existence checks
                score += 0.1f;
                break;
        }

        return score;
    }

    [Flags]
    private enum QueryType
    {
        None = 0,
        General = 1,
        Semantic = 2,
        Temporal = 4,
        Tagged = 8,
        Entity = 16,
        Topic = 32,
        Hierarchical = 64
    }
}

#endregion

#region Health Types

/// <summary>
/// Health status of an individual index.
/// </summary>
public record IndexHealth
{
    /// <summary>Index identifier.</summary>
    public required string IndexId { get; init; }

    /// <summary>Current status.</summary>
    public IndexStatus Status { get; init; }

    /// <summary>When health was last checked.</summary>
    public DateTimeOffset LastChecked { get; init; }

    /// <summary>Last error message if unhealthy.</summary>
    public string? LastError { get; init; }

    /// <summary>Last query latency.</summary>
    public TimeSpan LastLatency { get; init; }

    /// <summary>Number of consecutive failures.</summary>
    public int ConsecutiveFailures { get; init; }
}

/// <summary>
/// Status of an index.
/// </summary>
public enum IndexStatus
{
    /// <summary>Index is functioning normally.</summary>
    Healthy,

    /// <summary>Index is experiencing issues but still working.</summary>
    Degraded,

    /// <summary>Index is not functioning.</summary>
    Unhealthy
}

/// <summary>
/// Overall health status of the composite index.
/// </summary>
public record CompositeHealthStatus
{
    /// <summary>Overall status.</summary>
    public IndexStatus OverallStatus { get; init; }

    /// <summary>Number of healthy indexes.</summary>
    public int HealthyIndexCount { get; init; }

    /// <summary>Total number of indexes.</summary>
    public int TotalIndexCount { get; init; }

    /// <summary>Status of each index.</summary>
    public IList<IndexHealth> IndexStatuses { get; init; } = Array.Empty<IndexHealth>();

    /// <summary>When health was checked.</summary>
    public DateTimeOffset CheckedAt { get; init; }
}

#endregion
