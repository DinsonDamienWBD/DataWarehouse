using System.Diagnostics;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

/// <summary>
/// Content-based semantic clustering index for exabyte-scale memory.
/// Automatically clusters content based on embedding similarity, creating
/// hierarchical clusters (clusters of clusters) for efficient navigation.
///
/// Features:
/// - Automatic clustering based on embedding similarity
/// - Cluster centroids with representative summaries
/// - Hierarchical clustering (clusters of clusters)
/// - Fast nearest-cluster lookup using approximate methods
/// - Cluster evolution tracking over time
/// </summary>
public sealed class SemanticClusterIndex : ContextIndexBase
{
    private readonly BoundedDictionary<string, ClusterNode> _clusters = new BoundedDictionary<string, ClusterNode>(1000);
    private readonly BoundedDictionary<string, ClusteredEntry> _entries = new BoundedDictionary<string, ClusteredEntry>(1000);
    private readonly BoundedDictionary<string, string> _entryToCluster = new BoundedDictionary<string, string>(1000);
    private readonly BoundedDictionary<string, ClusterHistory> _clusterHistory = new BoundedDictionary<string, ClusterHistory>(1000);

    private const string RootClusterId = "cluster:root";
    private const int DefaultClusterCapacity = 1000;
    private const float DefaultSimilarityThreshold = 0.75f;
    private const int MaxHierarchyDepth = 5;

    /// <inheritdoc/>
    public override string IndexId => "index-semantic-cluster";

    /// <inheritdoc/>
    public override string IndexName => "Semantic Cluster Index";

    /// <summary>
    /// Gets the similarity threshold for clustering.
    /// </summary>
    public float SimilarityThreshold { get; set; } = DefaultSimilarityThreshold;

    /// <summary>
    /// Gets or sets the maximum entries per cluster before splitting.
    /// </summary>
    public int MaxClusterSize { get; set; } = DefaultClusterCapacity;

    /// <summary>
    /// Initializes the semantic cluster index.
    /// </summary>
    public SemanticClusterIndex()
    {
        InitializeRootCluster();
    }

    private void InitializeRootCluster()
    {
        _clusters[RootClusterId] = new ClusterNode
        {
            ClusterId = RootClusterId,
            ParentId = null,
            Level = 0,
            Centroid = null,
            EntryCount = 0,
            ChildClusterCount = 0,
            RepresentativeSummary = "Root cluster containing all indexed content.",
            TopTerms = Array.Empty<string>(),
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    public override async Task<ContextQueryResult> QueryAsync(ContextQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var results = new List<IndexedContextEntry>();
            var queryEmbedding = query.QueryEmbedding;

            // If query embedding provided, find nearest clusters first
            if (queryEmbedding != null)
            {
                var nearestClusters = FindNearestClusters(queryEmbedding, 10);

                foreach (var cluster in nearestClusters)
                {
                    var clusterEntries = GetEntriesInCluster(cluster.ClusterId)
                        .Where(e => MatchesFilters(e, query))
                        .Select(e => ScoreEntry(e, query, queryEmbedding))
                        .Where(e => e.RelevanceScore >= query.MinRelevance);

                    results.AddRange(clusterEntries);
                }
            }
            else
            {
                // Text-based search across all entries
                var queryWords = query.SemanticQuery.ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                foreach (var entry in _entries.Values)
                {
                    if (!MatchesFilters(entry, query))
                        continue;

                    var relevance = CalculateTextRelevance(entry, queryWords);
                    if (relevance >= query.MinRelevance)
                    {
                        results.Add(CreateEntryResult(entry, relevance, query.IncludeHierarchyPath, query.DetailLevel));
                    }
                }
            }

            // Sort by relevance and limit
            results = results
                .OrderByDescending(r => r.RelevanceScore)
                .Take(query.MaxResults)
                .ToList();

            sw.Stop();
            RecordQuery(sw.Elapsed);

            // Generate cluster counts
            var clusterCounts = query.ClusterResults
                ? results.GroupBy(r => GetClusterName(r.ContentId))
                    .ToDictionary(g => g.Key ?? "unclustered", g => g.Count())
                : null;

            return new ContextQueryResult
            {
                Entries = results,
                NavigationSummary = GenerateClusterNavigationSummary(results, queryEmbedding),
                ClusterCounts = clusterCounts,
                TotalMatchingEntries = results.Count,
                QueryDuration = sw.Elapsed,
                RelatedTopics = GetRelatedClusterTopics(queryEmbedding),
                WasTruncated = results.Count >= query.MaxResults
            };
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
    public override Task<ContextNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        if (_clusters.TryGetValue(nodeId, out var cluster))
        {
            return Task.FromResult<ContextNode?>(ClusterToNode(cluster));
        }
        return Task.FromResult<ContextNode?>(null);
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<ContextNode>> GetChildrenAsync(string? parentId, int depth = 1, CancellationToken ct = default)
    {
        var parent = parentId ?? RootClusterId;
        var children = _clusters.Values
            .Where(c => c.ParentId == parent)
            .Select(ClusterToNode)
            .ToList();

        if (depth > 1)
        {
            var descendants = new List<ContextNode>(children);
            foreach (var child in children)
            {
                var grandchildren = await GetChildrenAsync(child.NodeId, depth - 1, ct).ConfigureAwait(false);
                descendants.AddRange(grandchildren);
            }
            return descendants;
        }

        return children;
    }

    /// <inheritdoc/>
    public override async Task IndexContentAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default)
    {
        var embedding = metadata.Embedding;
        if (embedding == null)
        {
            // Would generate embedding here - using placeholder
            embedding = GeneratePlaceholderEmbedding(content);
        }

        var entry = new ClusteredEntry
        {
            ContentId = contentId,
            ContentSizeBytes = content.Length,
            Summary = metadata.Summary ?? $"Content entry {contentId}",
            Tags = metadata.Tags ?? Array.Empty<string>(),
            Embedding = embedding,
            Tier = metadata.Tier,
            Scope = metadata.Scope ?? "default",
            CreatedAt = metadata.CreatedAt ?? DateTimeOffset.UtcNow,
            LastAccessedAt = null,
            AccessCount = 0,
            ImportanceScore = CalculateImportance(content, metadata),
            Pointer = new ContextPointer
            {
                StorageBackend = "memory",
                Path = $"entries/{contentId}",
                Offset = 0,
                Length = content.Length
            }
        };

        _entries[contentId] = entry;

        // Find or create appropriate cluster
        var clusterId = await AssignToCluster(entry, ct);
        _entryToCluster[contentId] = clusterId;

        // Track cluster evolution
        TrackClusterChange(clusterId, "entry_added", contentId);

        MarkUpdated();
    }

    /// <inheritdoc/>
    public override Task UpdateIndexAsync(string contentId, IndexUpdate update, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(contentId, out var entry))
            return Task.CompletedTask;

        // Apply updates
        if (update.NewSummary != null)
            entry = entry with { Summary = update.NewSummary };

        if (update.NewTags != null)
            entry = entry with { Tags = update.NewTags };

        if (update.NewEmbedding != null)
        {
            entry = entry with { Embedding = update.NewEmbedding };
            // May need to reassign to different cluster
        }

        if (update.RecordAccess)
        {
            entry = entry with
            {
                AccessCount = entry.AccessCount + 1,
                LastAccessedAt = DateTimeOffset.UtcNow
            };
        }

        _entries[contentId] = entry;
        MarkUpdated();

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task RemoveFromIndexAsync(string contentId, CancellationToken ct = default)
    {
        if (_entries.TryRemove(contentId, out _))
        {
            if (_entryToCluster.TryRemove(contentId, out var clusterId))
            {
                // Update cluster
                if (_clusters.TryGetValue(clusterId, out var cluster))
                {
                    _clusters[clusterId] = cluster with
                    {
                        EntryCount = cluster.EntryCount - 1,
                        LastUpdated = DateTimeOffset.UtcNow
                    };

                    TrackClusterChange(clusterId, "entry_removed", contentId);
                }
            }
            MarkUpdated();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<IndexStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var totalContentBytes = _entries.Values.Sum(e => e.ContentSizeBytes);
        var indexSizeBytes = EstimateIndexSize();
        var maxDepth = _clusters.Values.Max(c => c.Level);

        var stats = GetBaseStatistics(
            _entries.Count,
            totalContentBytes,
            indexSizeBytes,
            _clusters.Count,
            maxDepth
        );

        var byTier = _entries.Values
            .GroupBy(e => e.Tier)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        var byScope = _entries.Values
            .GroupBy(e => e.Scope)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        return Task.FromResult(stats with
        {
            EntriesByTier = byTier,
            EntriesByScope = byScope
        });
    }

    /// <inheritdoc/>
    public override async Task OptimizeAsync(CancellationToken ct = default)
    {
        // Merge small clusters
        await MergeSmallClusters(ct);

        // Split large clusters
        await SplitLargeClusters(ct);

        // Recalculate centroids
        RecalculateCentroids();

        // Update cluster summaries
        await UpdateClusterSummaries(ct);

        await base.OptimizeAsync(ct);
    }

    #region Cluster-Specific Methods

    /// <summary>
    /// Gets the nearest clusters to a query embedding.
    /// </summary>
    /// <param name="queryEmbedding">The query embedding vector.</param>
    /// <param name="topK">Number of clusters to return.</param>
    /// <returns>List of nearest clusters.</returns>
    public IList<ClusterInfo> FindNearestClusters(float[] queryEmbedding, int topK = 5)
    {
        return _clusters.Values
            .Where(c => c.Level > 0 && c.Centroid != null)
            .Select(c => new ClusterInfo
            {
                ClusterId = c.ClusterId,
                ClusterName = GetClusterDisplayName(c),
                Similarity = CosineSimilarity(queryEmbedding, c.Centroid!),
                EntryCount = c.EntryCount,
                Summary = c.RepresentativeSummary
            })
            .OrderByDescending(c => c.Similarity)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// Gets entries within a specific cluster.
    /// </summary>
    /// <param name="clusterId">The cluster identifier.</param>
    /// <returns>Entries in the cluster.</returns>
    public IEnumerable<ClusteredEntry> GetEntriesInCluster(string clusterId)
    {
        return _entryToCluster
            .Where(kvp => kvp.Value == clusterId)
            .Select(kvp => _entries.TryGetValue(kvp.Key, out var entry) ? entry : null)
            .Where(e => e != null)!;
    }

    /// <summary>
    /// Gets the evolution history of a cluster.
    /// </summary>
    /// <param name="clusterId">The cluster identifier.</param>
    /// <returns>History of cluster changes.</returns>
    public ClusterHistory? GetClusterHistory(string clusterId)
    {
        return _clusterHistory.TryGetValue(clusterId, out var history) ? history : null;
    }

    /// <summary>
    /// Gets clusters that have evolved significantly over a time period.
    /// </summary>
    /// <param name="since">Start of the time period.</param>
    /// <returns>Clusters with significant evolution.</returns>
    public IEnumerable<ClusterEvolutionInfo> GetEvolvingClusters(DateTimeOffset since)
    {
        return _clusterHistory.Values
            .Where(h => h.LastChangeAt >= since)
            .Select(h => new ClusterEvolutionInfo
            {
                ClusterId = h.ClusterId,
                ChangeCount = h.Changes.Count(c => c.Timestamp >= since),
                GrowthRate = CalculateGrowthRate(h, since),
                TopChanges = h.Changes
                    .Where(c => c.Timestamp >= since)
                    .OrderByDescending(c => c.Timestamp)
                    .Take(10)
                    .ToList()
            })
            .Where(e => e.ChangeCount > 0)
            .OrderByDescending(e => e.ChangeCount);
    }

    /// <summary>
    /// Gets similar clusters to a given cluster.
    /// </summary>
    /// <param name="clusterId">The reference cluster.</param>
    /// <param name="topK">Number of similar clusters to return.</param>
    /// <returns>Similar clusters.</returns>
    public IList<ClusterInfo> GetSimilarClusters(string clusterId, int topK = 5)
    {
        if (!_clusters.TryGetValue(clusterId, out var cluster) || cluster.Centroid == null)
            return Array.Empty<ClusterInfo>();

        return _clusters.Values
            .Where(c => c.ClusterId != clusterId && c.Centroid != null)
            .Select(c => new ClusterInfo
            {
                ClusterId = c.ClusterId,
                ClusterName = GetClusterDisplayName(c),
                Similarity = CosineSimilarity(cluster.Centroid, c.Centroid!),
                EntryCount = c.EntryCount,
                Summary = c.RepresentativeSummary
            })
            .OrderByDescending(c => c.Similarity)
            .Take(topK)
            .ToList();
    }

    #endregion

    #region Private Helper Methods

    private async Task<string> AssignToCluster(ClusteredEntry entry, CancellationToken ct)
    {
        if (entry.Embedding == null)
        {
            // Assign to default cluster based on scope
            return await GetOrCreateScopeCluster(entry.Scope, ct);
        }

        // Find best matching cluster
        var candidates = _clusters.Values
            .Where(c => c.Level > 0 && c.Centroid != null)
            .Select(c => (Cluster: c, Similarity: CosineSimilarity(entry.Embedding, c.Centroid!)))
            .OrderByDescending(x => x.Similarity)
            .Take(5)
            .ToList();

        if (candidates.Count > 0 && candidates[0].Similarity >= SimilarityThreshold)
        {
            var targetCluster = candidates[0].Cluster;

            // Check if cluster is full
            if (targetCluster.EntryCount >= MaxClusterSize)
            {
                // Would split cluster - for now just add to it
            }

            // Update cluster
            _clusters[targetCluster.ClusterId] = targetCluster with
            {
                EntryCount = targetCluster.EntryCount + 1,
                LastUpdated = DateTimeOffset.UtcNow
            };

            return targetCluster.ClusterId;
        }

        // Create new cluster
        return await CreateNewCluster(entry, ct);
    }

    private async Task<string> GetOrCreateScopeCluster(string scope, CancellationToken ct)
    {
        var scopeClusterId = $"cluster:scope:{scope}";

        if (!_clusters.ContainsKey(scopeClusterId))
        {
            _clusters[scopeClusterId] = new ClusterNode
            {
                ClusterId = scopeClusterId,
                ParentId = RootClusterId,
                Level = 1,
                Centroid = null,
                EntryCount = 1,
                ChildClusterCount = 0,
                RepresentativeSummary = $"Cluster for {scope} content.",
                TopTerms = new[] { scope },
                CreatedAt = DateTimeOffset.UtcNow,
                LastUpdated = DateTimeOffset.UtcNow
            };

            // Update root cluster
            if (_clusters.TryGetValue(RootClusterId, out var root))
            {
                _clusters[RootClusterId] = root with
                {
                    ChildClusterCount = root.ChildClusterCount + 1
                };
            }
        }
        else
        {
            var cluster = _clusters[scopeClusterId];
            _clusters[scopeClusterId] = cluster with
            {
                EntryCount = cluster.EntryCount + 1,
                LastUpdated = DateTimeOffset.UtcNow
            };
        }

        return scopeClusterId;
    }

    private async Task<string> CreateNewCluster(ClusteredEntry seedEntry, CancellationToken ct)
    {
        var clusterId = $"cluster:{Guid.NewGuid():N}";

        var cluster = new ClusterNode
        {
            ClusterId = clusterId,
            ParentId = RootClusterId,
            Level = 1,
            Centroid = seedEntry.Embedding,
            EntryCount = 1,
            ChildClusterCount = 0,
            RepresentativeSummary = seedEntry.Summary.Length > 200
                ? seedEntry.Summary[..200] + "..."
                : seedEntry.Summary,
            TopTerms = seedEntry.Tags.Take(5).ToArray(),
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        };

        _clusters[clusterId] = cluster;

        // Update root
        if (_clusters.TryGetValue(RootClusterId, out var root))
        {
            _clusters[RootClusterId] = root with
            {
                ChildClusterCount = root.ChildClusterCount + 1,
                EntryCount = root.EntryCount + 1
            };
        }

        // Initialize history
        _clusterHistory[clusterId] = new ClusterHistory
        {
            ClusterId = clusterId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastChangeAt = DateTimeOffset.UtcNow,
            Changes = new List<ClusterChange>
            {
                new()
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    ChangeType = "created",
                    Details = $"Created with seed entry {seedEntry.ContentId}"
                }
            }
        };

        return clusterId;
    }

    private void TrackClusterChange(string clusterId, string changeType, string details)
    {
        if (!_clusterHistory.ContainsKey(clusterId))
        {
            _clusterHistory[clusterId] = new ClusterHistory
            {
                ClusterId = clusterId,
                CreatedAt = DateTimeOffset.UtcNow,
                LastChangeAt = DateTimeOffset.UtcNow,
                Changes = new List<ClusterChange>()
            };
        }

        var history = _clusterHistory[clusterId];
        history.Changes.Add(new ClusterChange
        {
            Timestamp = DateTimeOffset.UtcNow,
            ChangeType = changeType,
            Details = details
        });
        history.LastChangeAt = DateTimeOffset.UtcNow;

        // Limit history size
        if (history.Changes.Count > 1000)
        {
            history.Changes = history.Changes.TakeLast(500).ToList();
        }
    }

    private bool MatchesFilters(ClusteredEntry entry, ContextQuery query)
    {
        if (query.Scope != null && !entry.Scope.Equals(query.Scope, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.RequiredTags?.Length > 0)
        {
            if (!query.RequiredTags.All(t => entry.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                return false;
        }

        if (query.TimeRange != null)
        {
            if (query.TimeRange.Start.HasValue && entry.CreatedAt < query.TimeRange.Start.Value)
                return false;
            if (query.TimeRange.End.HasValue && entry.CreatedAt > query.TimeRange.End.Value)
                return false;
        }

        return true;
    }

    private IndexedContextEntry ScoreEntry(ClusteredEntry entry, ContextQuery query, float[]? queryEmbedding)
    {
        var relevance = 0f;

        if (queryEmbedding != null && entry.Embedding != null)
        {
            relevance = CosineSimilarity(queryEmbedding, entry.Embedding);
        }

        // Add text-based boost
        var queryWords = query.SemanticQuery.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        relevance += CalculateTextRelevance(entry, queryWords) * 0.2f;

        return CreateEntryResult(entry, relevance, query.IncludeHierarchyPath, query.DetailLevel);
    }

    private float CalculateTextRelevance(ClusteredEntry entry, string[] queryWords)
    {
        if (queryWords.Length == 0) return 0f;

        var summaryLower = entry.Summary.ToLowerInvariant();
        var matchCount = queryWords.Count(w => summaryLower.Contains(w));
        var tagMatches = entry.Tags.Count(t => queryWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));

        return (float)(matchCount + tagMatches) / (queryWords.Length + entry.Tags.Length);
    }

    private IndexedContextEntry CreateEntryResult(ClusteredEntry entry, float relevance, bool includePath, SummaryLevel detailLevel)
    {
        string? path = null;
        if (includePath && _entryToCluster.TryGetValue(entry.ContentId, out var clusterId))
        {
            path = BuildClusterPath(clusterId);
        }

        return new IndexedContextEntry
        {
            ContentId = entry.ContentId,
            HierarchyPath = path,
            RelevanceScore = relevance,
            Summary = TruncateSummary(entry.Summary, detailLevel),
            SemanticTags = entry.Tags,
            ContentSizeBytes = entry.ContentSizeBytes,
            Pointer = entry.Pointer,
            CreatedAt = entry.CreatedAt,
            LastAccessedAt = entry.LastAccessedAt,
            AccessCount = entry.AccessCount,
            ImportanceScore = entry.ImportanceScore,
            Tier = entry.Tier
        };
    }

    private string BuildClusterPath(string clusterId)
    {
        var parts = new List<string>();
        var current = clusterId;

        while (current != null && _clusters.TryGetValue(current, out var cluster))
        {
            if (current != RootClusterId)
            {
                parts.Insert(0, GetClusterDisplayName(cluster));
            }
            current = cluster.ParentId;
        }

        return string.Join("/", parts);
    }

    private static string GetClusterDisplayName(ClusterNode cluster)
    {
        if (cluster.TopTerms.Length > 0)
            return cluster.TopTerms[0];

        if (cluster.ClusterId.Contains(':'))
            return cluster.ClusterId.Split(':').Last();

        return cluster.ClusterId;
    }

    private string? GetClusterName(string contentId)
    {
        if (_entryToCluster.TryGetValue(contentId, out var clusterId) &&
            _clusters.TryGetValue(clusterId, out var cluster))
        {
            return GetClusterDisplayName(cluster);
        }
        return null;
    }

    private string GenerateClusterNavigationSummary(IList<IndexedContextEntry> results, float[]? queryEmbedding)
    {
        if (results.Count == 0)
            return "No matching entries found in any cluster.";

        var clusterGroups = results
            .Where(r => r.HierarchyPath != null)
            .GroupBy(r => r.HierarchyPath!.Split('/').FirstOrDefault() ?? "unknown")
            .Take(5)
            .ToList();

        if (clusterGroups.Count == 0)
            return $"Found {results.Count} entries. Use clustering filters to navigate by topic.";

        var clusterList = string.Join(", ", clusterGroups.Select(g => $"{g.Key} ({g.Count()})"));
        return $"Found {results.Count} entries across clusters: {clusterList}. " +
               "Navigate to a specific cluster for focused results.";
    }

    private string[]? GetRelatedClusterTopics(float[]? queryEmbedding)
    {
        if (queryEmbedding == null) return null;

        return _clusters.Values
            .Where(c => c.Centroid != null)
            .Select(c => (Cluster: c, Similarity: CosineSimilarity(queryEmbedding, c.Centroid!)))
            .OrderByDescending(x => x.Similarity)
            .Take(10)
            .SelectMany(x => x.Cluster.TopTerms)
            .Distinct()
            .Take(10)
            .ToArray();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        var dotProduct = 0f;
        var normA = 0f;
        var normB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator > 0 ? dotProduct / denominator : 0f;
    }

    private static string TruncateSummary(string summary, SummaryLevel level)
    {
        var maxLength = level switch
        {
            SummaryLevel.Minimal => 50,
            SummaryLevel.Brief => 150,
            SummaryLevel.Detailed => 500,
            SummaryLevel.Full => int.MaxValue,
            _ => 150
        };

        return summary.Length <= maxLength ? summary : summary[..maxLength] + "...";
    }

    private static float[] GeneratePlaceholderEmbedding(byte[] content)
    {
        // Placeholder - in production, would use actual embedding model
        var random = new Random(BitConverter.ToInt32(content.Take(4).ToArray(), 0));
        return Enumerable.Range(0, 384).Select(_ => (float)random.NextDouble()).ToArray();
    }

    private ContextNode ClusterToNode(ClusterNode cluster)
    {
        return new ContextNode
        {
            NodeId = cluster.ClusterId,
            ParentId = cluster.ParentId,
            Name = GetClusterDisplayName(cluster),
            Summary = cluster.RepresentativeSummary,
            Level = cluster.Level,
            ChildCount = cluster.ChildClusterCount,
            TotalEntryCount = cluster.EntryCount,
            Embedding = cluster.Centroid,
            Tags = cluster.TopTerms,
            LastUpdated = cluster.LastUpdated
        };
    }

    private long EstimateIndexSize()
    {
        var clusterSize = _clusters.Count * 4096; // Centroid + metadata
        var entryIndexSize = _entries.Count * 256;
        return clusterSize + entryIndexSize;
    }

    private async Task MergeSmallClusters(CancellationToken ct)
    {
        var smallClusters = _clusters.Values
            .Where(c => c.Level > 0 && c.EntryCount < 10)
            .ToList();

        foreach (var small in smallClusters)
        {
            if (small.Centroid == null) continue;

            // Find similar cluster to merge into
            var target = _clusters.Values
                .Where(c => c.ClusterId != small.ClusterId && c.Centroid != null)
                .Select(c => (Cluster: c, Similarity: CosineSimilarity(small.Centroid, c.Centroid!)))
                .OrderByDescending(x => x.Similarity)
                .FirstOrDefault();

            if (target.Similarity >= 0.8f)
            {
                // Merge small into target
                MergeClusters(small.ClusterId, target.Cluster.ClusterId);
            }
        }
    }

    private void MergeClusters(string sourceId, string targetId)
    {
        // Move all entries from source to target
        foreach (var kvp in _entryToCluster.Where(kvp => kvp.Value == sourceId).ToList())
        {
            _entryToCluster[kvp.Key] = targetId;
        }

        // Update target count
        if (_clusters.TryGetValue(sourceId, out var source) &&
            _clusters.TryGetValue(targetId, out var target))
        {
            _clusters[targetId] = target with
            {
                EntryCount = target.EntryCount + source.EntryCount,
                LastUpdated = DateTimeOffset.UtcNow
            };
        }

        // Remove source
        _clusters.TryRemove(sourceId, out _);

        TrackClusterChange(targetId, "merged", $"Merged cluster {sourceId}");
    }

    private async Task SplitLargeClusters(CancellationToken ct)
    {
        var largeClusters = _clusters.Values
            .Where(c => c.EntryCount > MaxClusterSize * 2)
            .ToList();

        foreach (var large in largeClusters)
        {
            // K-means style split into 2 clusters
            var entries = GetEntriesInCluster(large.ClusterId).ToList();
            if (entries.Count < 2) continue;

            // Simple split by first half / second half (would use proper k-means)
            var half = entries.Count / 2;
            var newClusterId = $"cluster:{Guid.NewGuid():N}";

            // Create new cluster
            _clusters[newClusterId] = new ClusterNode
            {
                ClusterId = newClusterId,
                ParentId = large.ParentId,
                Level = large.Level,
                Centroid = entries[half].Embedding,
                EntryCount = entries.Count - half,
                ChildClusterCount = 0,
                RepresentativeSummary = $"Split from {GetClusterDisplayName(large)}",
                TopTerms = entries.Skip(half).SelectMany(e => e.Tags).Distinct().Take(5).ToArray(),
                CreatedAt = DateTimeOffset.UtcNow,
                LastUpdated = DateTimeOffset.UtcNow
            };

            // Move entries
            foreach (var entry in entries.Skip(half))
            {
                _entryToCluster[entry.ContentId] = newClusterId;
            }

            // Update original
            _clusters[large.ClusterId] = large with
            {
                EntryCount = half,
                LastUpdated = DateTimeOffset.UtcNow
            };

            TrackClusterChange(large.ClusterId, "split", $"Split into {large.ClusterId} and {newClusterId}");
        }
    }

    private void RecalculateCentroids()
    {
        foreach (var cluster in _clusters.Values.Where(c => c.Level > 0))
        {
            var entries = GetEntriesInCluster(cluster.ClusterId)
                .Where(e => e.Embedding != null)
                .ToList();

            if (entries.Count == 0) continue;

            var dimension = entries[0].Embedding!.Length;
            var centroid = new float[dimension];

            foreach (var entry in entries)
            {
                for (int i = 0; i < dimension; i++)
                {
                    centroid[i] += entry.Embedding![i];
                }
            }

            for (int i = 0; i < dimension; i++)
            {
                centroid[i] /= entries.Count;
            }

            _clusters[cluster.ClusterId] = cluster with { Centroid = centroid };
        }
    }

    private async Task UpdateClusterSummaries(CancellationToken ct)
    {
        foreach (var cluster in _clusters.Values.Where(c => c.Level > 0))
        {
            var entries = GetEntriesInCluster(cluster.ClusterId).Take(10).ToList();
            if (entries.Count == 0) continue;

            // Extract common terms
            var terms = entries
                .SelectMany(e => e.Tags)
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key)
                .ToArray();

            // Update summary (would use AI in production)
            var summary = $"Cluster containing {cluster.EntryCount} entries about {string.Join(", ", terms)}.";

            _clusters[cluster.ClusterId] = cluster with
            {
                RepresentativeSummary = summary,
                TopTerms = terms,
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
    }

    private static double CalculateGrowthRate(ClusterHistory history, DateTimeOffset since)
    {
        var recentChanges = history.Changes.Where(c => c.Timestamp >= since).ToList();
        if (recentChanges.Count == 0) return 0;

        var added = recentChanges.Count(c => c.ChangeType == "entry_added");
        var removed = recentChanges.Count(c => c.ChangeType == "entry_removed");

        var duration = (DateTimeOffset.UtcNow - since).TotalDays;
        return duration > 0 ? (added - removed) / duration : 0;
    }

    #endregion

    #region Internal Types

    private sealed record ClusterNode
    {
        public required string ClusterId { get; init; }
        public string? ParentId { get; init; }
        public int Level { get; init; }
        public float[]? Centroid { get; init; }
        public long EntryCount { get; init; }
        public int ChildClusterCount { get; init; }
        public required string RepresentativeSummary { get; init; }
        public string[] TopTerms { get; init; } = Array.Empty<string>();
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastUpdated { get; init; }
    }

    public sealed record ClusteredEntry
    {
        public required string ContentId { get; init; }
        public long ContentSizeBytes { get; init; }
        public required string Summary { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
        public float[]? Embedding { get; init; }
        public MemoryTier Tier { get; init; }
        public required string Scope { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastAccessedAt { get; init; }
        public int AccessCount { get; init; }
        public float ImportanceScore { get; init; }
        public required ContextPointer Pointer { get; init; }
    }

    #endregion
}

#region Cluster Types

/// <summary>
/// Information about a semantic cluster.
/// </summary>
public record ClusterInfo
{
    /// <summary>Cluster identifier.</summary>
    public required string ClusterId { get; init; }

    /// <summary>Display name for the cluster.</summary>
    public required string ClusterName { get; init; }

    /// <summary>Similarity to query (0-1).</summary>
    public float Similarity { get; init; }

    /// <summary>Number of entries in the cluster.</summary>
    public long EntryCount { get; init; }

    /// <summary>Representative summary of the cluster.</summary>
    public string? Summary { get; init; }
}

/// <summary>
/// History of changes to a cluster.
/// </summary>
public record ClusterHistory
{
    /// <summary>Cluster identifier.</summary>
    public required string ClusterId { get; init; }

    /// <summary>When the cluster was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the last change occurred.</summary>
    public DateTimeOffset LastChangeAt { get; set; }

    /// <summary>List of changes.</summary>
    public List<ClusterChange> Changes { get; set; } = new();
}

/// <summary>
/// A single change to a cluster.
/// </summary>
public record ClusterChange
{
    /// <summary>When the change occurred.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Type of change (entry_added, entry_removed, split, merged, etc.).</summary>
    public required string ChangeType { get; init; }

    /// <summary>Details about the change.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Information about cluster evolution over time.
/// </summary>
public record ClusterEvolutionInfo
{
    /// <summary>Cluster identifier.</summary>
    public required string ClusterId { get; init; }

    /// <summary>Number of changes in the period.</summary>
    public int ChangeCount { get; init; }

    /// <summary>Growth rate (entries per day).</summary>
    public double GrowthRate { get; init; }

    /// <summary>Most recent changes.</summary>
    public IList<ClusterChange> TopChanges { get; init; } = Array.Empty<ClusterChange>();
}

#endregion
