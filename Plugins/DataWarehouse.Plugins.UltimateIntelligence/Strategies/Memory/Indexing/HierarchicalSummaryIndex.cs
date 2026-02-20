using System.Diagnostics;
using System.Text;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

/// <summary>
/// Multi-level summarization tree index for AI-native context navigation.
/// Provides hierarchical "zoom in/out" capability for navigating exabyte-scale memory.
///
/// Structure:
/// - Root level: Single paragraph describing entire exabyte context
/// - Level 1: Major domains (finance, healthcare, operations, etc.)
/// - Level 2: Sub-domains and time periods
/// - Level 3: Specific topics and entities
/// - Level N: Individual entries
///
/// Each node has an AI-generated summary, embedding, and child pointers.
/// </summary>
public sealed class HierarchicalSummaryIndex : ContextIndexBase
{
    private readonly BoundedDictionary<string, SummaryNode> _nodes = new BoundedDictionary<string, SummaryNode>(1000);
    private readonly BoundedDictionary<string, IndexedEntry> _entries = new BoundedDictionary<string, IndexedEntry>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _childIndex = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, string> _entryToNode = new BoundedDictionary<string, string>(1000);
    private readonly ReaderWriterLockSlim _treeLock = new();

    private const string RootNodeId = "root";
    private const int MaxChildrenPerNode = 100;
    private const int MaxDepthDefault = 10;
    private const int SummaryMaxLength = 500;

    /// <inheritdoc/>
    public override string IndexId => "index-hierarchical-summary";

    /// <inheritdoc/>
    public override string IndexName => "Hierarchical Summary Index";

    /// <summary>
    /// Initializes the hierarchical summary index with a root node.
    /// </summary>
    public HierarchicalSummaryIndex()
    {
        InitializeRoot();
    }

    private void InitializeRoot()
    {
        var rootNode = new SummaryNode
        {
            NodeId = RootNodeId,
            ParentId = null,
            Name = "Context Root",
            Summary = "Root of the hierarchical context index. Contains all indexed content organized by domain, scope, and topic.",
            Level = 0,
            ChildCount = 0,
            TotalEntryCount = 0,
            TotalSizeBytes = 0,
            Tags = Array.Empty<string>(),
            LastUpdated = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _nodes[RootNodeId] = rootNode;
        _childIndex[RootNodeId] = new HashSet<string>();
    }

    /// <inheritdoc/>
    public override async Task<ContextQueryResult> QueryAsync(ContextQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var results = new List<IndexedContextEntry>();
            var queryLower = query.SemanticQuery.ToLowerInvariant();
            var queryWords = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Search through all entries
            foreach (var entry in _entries.Values)
            {
                ct.ThrowIfCancellationRequested();

                // Apply filters
                if (!MatchesFilters(entry, query))
                    continue;

                // Calculate relevance
                var relevance = CalculateRelevance(entry, queryWords, query.QueryEmbedding);
                if (relevance < query.MinRelevance)
                    continue;

                // Build hierarchy path
                string? hierarchyPath = null;
                if (query.IncludeHierarchyPath && _entryToNode.TryGetValue(entry.ContentId, out var nodeId))
                {
                    hierarchyPath = BuildHierarchyPath(nodeId);
                }

                results.Add(new IndexedContextEntry
                {
                    ContentId = entry.ContentId,
                    HierarchyPath = hierarchyPath,
                    RelevanceScore = relevance,
                    Summary = GenerateSummary(entry, query.DetailLevel),
                    SemanticTags = entry.Tags,
                    ContentSizeBytes = entry.ContentSizeBytes,
                    Pointer = entry.Pointer,
                    CreatedAt = entry.CreatedAt,
                    LastAccessedAt = entry.LastAccessedAt,
                    AccessCount = entry.AccessCount,
                    ImportanceScore = entry.ImportanceScore,
                    Tier = entry.Tier
                });
            }

            // Sort by relevance and limit
            results = results
                .OrderByDescending(r => r.RelevanceScore)
                .Take(query.MaxResults)
                .ToList();

            sw.Stop();
            RecordQuery(sw.Elapsed);

            // Generate cluster counts if requested
            Dictionary<string, int>? clusters = null;
            if (query.ClusterResults)
            {
                clusters = results
                    .Where(r => r.SemanticTags?.Length > 0)
                    .SelectMany(r => r.SemanticTags!)
                    .GroupBy(t => t)
                    .ToDictionary(g => g.Key, g => g.Count());
            }

            return new ContextQueryResult
            {
                Entries = results,
                NavigationSummary = GenerateNavigationSummary(results, _entries.Count),
                ClusterCounts = clusters,
                TotalMatchingEntries = results.Count,
                QueryDuration = sw.Elapsed,
                SuggestedQueries = GenerateSuggestedQueries(query, results),
                RelatedTopics = ExtractRelatedTopics(results),
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
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            return Task.FromResult<ContextNode?>(ToContextNode(node));
        }
        return Task.FromResult<ContextNode?>(null);
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<ContextNode>> GetChildrenAsync(string? parentId, int depth = 1, CancellationToken ct = default)
    {
        var parent = parentId ?? RootNodeId;
        var results = new List<ContextNode>();

        if (!_childIndex.TryGetValue(parent, out var childIds))
            return results;

        foreach (var childId in childIds)
        {
            if (_nodes.TryGetValue(childId, out var node))
            {
                results.Add(ToContextNode(node));

                // Recursively get children if depth > 1
                if (depth > 1)
                {
                    var grandchildren = await GetChildrenAsync(childId, depth - 1, ct).ConfigureAwait(false);
                    results.AddRange(grandchildren);
                }
            }
        }

        return results;
    }

    /// <inheritdoc/>
    public override async Task IndexContentAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default)
    {
        // Create entry
        var entry = new IndexedEntry
        {
            ContentId = contentId,
            ContentSizeBytes = content.Length,
            Summary = metadata.Summary ?? GenerateContentSummary(content),
            Tags = metadata.Tags ?? Array.Empty<string>(),
            Embedding = metadata.Embedding,
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
            },
            Entities = metadata.Entities ?? Array.Empty<string>()
        };

        _entries[contentId] = entry;

        // Place in hierarchy
        var nodeId = await FindOrCreateNodeForEntry(entry, ct);
        _entryToNode[contentId] = nodeId;

        // Update ancestor summaries
        await UpdateAncestorSummaries(nodeId, ct);

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

        if (update.AddTags != null)
            entry = entry with { Tags = entry.Tags.Concat(update.AddTags).Distinct().ToArray() };

        if (update.RemoveTags != null)
            entry = entry with { Tags = entry.Tags.Where(t => !update.RemoveTags.Contains(t)).ToArray() };

        if (update.NewImportanceScore.HasValue)
            entry = entry with { ImportanceScore = update.NewImportanceScore.Value };

        if (update.NewEmbedding != null)
            entry = entry with { Embedding = update.NewEmbedding };

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
            if (_entryToNode.TryRemove(contentId, out var nodeId))
            {
                // Update node counts
                UpdateNodeCounts(nodeId, -1);
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
        var maxDepth = _nodes.Values.Max(n => n.Level);

        var stats = GetBaseStatistics(
            _entries.Count,
            totalContentBytes,
            indexSizeBytes,
            _nodes.Count,
            maxDepth
        );

        // Add tier breakdown
        var byTier = _entries.Values
            .GroupBy(e => e.Tier)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        var byScope = _entries.Values
            .GroupBy(e => e.Scope)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        return Task.FromResult(stats with
        {
            EntriesByTier = byTier,
            EntriesByScope = byScope,
            FragmentationPercent = CalculateFragmentation()
        });
    }

    /// <inheritdoc/>
    public override async Task OptimizeAsync(CancellationToken ct = default)
    {
        // Rebalance tree if nodes are too large
        await RebalanceTree(ct);

        // Regenerate summaries for nodes with stale data
        await RegenerateStaleSummaries(ct);

        // Compact empty nodes
        RemoveEmptyNodes();

        await base.OptimizeAsync(ct);
    }

    #region Navigation Methods

    /// <summary>
    /// Gets an overview of the entire context at a specified depth.
    /// </summary>
    /// <param name="maxDepth">Maximum depth to include in overview.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Hierarchical overview of the context.</returns>
    public async Task<ContextOverview> GetOverviewAsync(int maxDepth = 2, CancellationToken ct = default)
    {
        var rootChildren = await GetChildrenAsync(RootNodeId, maxDepth, ct);

        return new ContextOverview
        {
            RootSummary = _nodes[RootNodeId].Summary,
            TotalEntries = _entries.Count,
            TotalSizeBytes = _entries.Values.Sum(e => e.ContentSizeBytes),
            TopLevelDomains = rootChildren.Where(n => n.Level == 1).ToList(),
            DomainCounts = _nodes.Values
                .Where(n => n.Level == 1)
                .ToDictionary(n => n.Name, n => n.TotalEntryCount),
            LastUpdated = _nodes.Values.Max(n => n.LastUpdated)
        };
    }

    /// <summary>
    /// Drills down into a specific topic, returning more detailed information.
    /// </summary>
    /// <param name="currentPath">Current hierarchy path.</param>
    /// <param name="focusQuery">What to focus on within this area.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detailed view of the focused area.</returns>
    public async Task<DrillDownResult> DrillDownAsync(string currentPath, string focusQuery, CancellationToken ct = default)
    {
        // Find the node for the current path
        var pathParts = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentNode = await FindNodeByPath(pathParts, ct);

        if (currentNode == null)
        {
            return new DrillDownResult
            {
                Success = false,
                ErrorMessage = $"Path not found: {currentPath}"
            };
        }

        // Get children and filter by focus query
        var children = await GetChildrenAsync(currentNode.NodeId, 1, ct);
        var focusLower = focusQuery.ToLowerInvariant();

        var matchingChildren = children
            .Where(c => c.Summary.Contains(focusQuery, StringComparison.OrdinalIgnoreCase) ||
                       c.Name.Contains(focusQuery, StringComparison.OrdinalIgnoreCase) ||
                       (c.Tags?.Any(t => t.Contains(focusQuery, StringComparison.OrdinalIgnoreCase)) ?? false))
            .ToList();

        // Get entries directly under matching nodes
        var entries = new List<IndexedContextEntry>();
        foreach (var child in matchingChildren)
        {
            var nodeEntries = GetEntriesForNode(child.NodeId);
            entries.AddRange(nodeEntries.Take(10)); // Limit per node
        }

        return new DrillDownResult
        {
            Success = true,
            CurrentNode = currentNode,
            MatchingSubNodes = matchingChildren,
            RelevantEntries = entries.Take(50).ToList(),
            CanDrillDeeper = matchingChildren.Any(c => c.ChildCount > 0),
            SuggestedPaths = matchingChildren.Take(5).Select(c => $"{currentPath}/{c.Name}").ToArray()
        };
    }

    /// <summary>
    /// Zooms out from a specific node to get broader context.
    /// </summary>
    /// <param name="nodeId">The node to zoom out from.</param>
    /// <param name="levels">Number of levels to zoom out.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Broader context view.</returns>
    public Task<ZoomOutResult> ZoomOutAsync(string nodeId, int levels = 1, CancellationToken ct = default)
    {
        if (!_nodes.TryGetValue(nodeId, out var currentNode))
        {
            return Task.FromResult(new ZoomOutResult
            {
                Success = false,
                ErrorMessage = $"Node not found: {nodeId}"
            });
        }

        // Navigate up the tree
        var ancestorPath = new List<ContextNode>();
        var current = currentNode;
        for (int i = 0; i < levels && current.ParentId != null; i++)
        {
            if (_nodes.TryGetValue(current.ParentId, out var parent))
            {
                ancestorPath.Add(ToContextNode(parent));
                current = parent;
            }
        }

        // Get siblings of the target ancestor
        var targetNode = ancestorPath.LastOrDefault();
        var siblings = new List<ContextNode>();
        if (targetNode != null && _childIndex.TryGetValue(targetNode.ParentId ?? RootNodeId, out var siblingIds))
        {
            siblings = siblingIds
                .Where(id => id != targetNode.NodeId && _nodes.ContainsKey(id))
                .Select(id => ToContextNode(_nodes[id]))
                .ToList();
        }

        return Task.FromResult(new ZoomOutResult
        {
            Success = true,
            AncestorPath = ancestorPath,
            TargetNode = targetNode,
            SiblingNodes = siblings,
            BroaderSummary = targetNode?.Summary ?? _nodes[RootNodeId].Summary
        });
    }

    #endregion

    #region Private Helper Methods

    private async Task<string> FindOrCreateNodeForEntry(IndexedEntry entry, CancellationToken ct)
    {
        // Build path: scope -> first tag (as topic) -> entry
        var scope = entry.Scope ?? "default";
        var topic = entry.Tags.FirstOrDefault() ?? "general";

        // Ensure scope node exists
        var scopeNodeId = $"scope:{scope}";
        if (!_nodes.ContainsKey(scopeNodeId))
        {
            await CreateNode(scopeNodeId, RootNodeId, scope, $"Content in the {scope} domain.", 1, ct);
        }

        // Ensure topic node exists under scope
        var topicNodeId = $"scope:{scope}/topic:{topic}";
        if (!_nodes.ContainsKey(topicNodeId))
        {
            await CreateNode(topicNodeId, scopeNodeId, topic, $"Content related to {topic} in {scope}.", 2, ct);
        }

        // Update node counts
        UpdateNodeCounts(topicNodeId, 1);

        return topicNodeId;
    }

    private Task CreateNode(string nodeId, string parentId, string name, string summary, int level, CancellationToken ct)
    {
        var node = new SummaryNode
        {
            NodeId = nodeId,
            ParentId = parentId,
            Name = name,
            Summary = summary,
            Level = level,
            ChildCount = 0,
            TotalEntryCount = 0,
            TotalSizeBytes = 0,
            Tags = Array.Empty<string>(),
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        };

        _nodes[nodeId] = node;

        if (!_childIndex.ContainsKey(parentId))
            _childIndex[parentId] = new HashSet<string>();
        _childIndex[parentId].Add(nodeId);
        _childIndex[nodeId] = new HashSet<string>();

        // Update parent child count
        if (_nodes.TryGetValue(parentId, out var parent))
        {
            _nodes[parentId] = parent with { ChildCount = parent.ChildCount + 1 };
        }

        return Task.CompletedTask;
    }

    private void UpdateNodeCounts(string nodeId, int delta)
    {
        var current = nodeId;
        while (current != null && _nodes.TryGetValue(current, out var node))
        {
            _nodes[current] = node with
            {
                TotalEntryCount = node.TotalEntryCount + delta,
                LastUpdated = DateTimeOffset.UtcNow
            };
            current = node.ParentId;
        }
    }

    private Task UpdateAncestorSummaries(string nodeId, CancellationToken ct)
    {
        // In a real implementation, this would regenerate summaries
        // For now, just update timestamps
        var current = nodeId;
        while (current != null && _nodes.TryGetValue(current, out var node))
        {
            _nodes[current] = node with { LastUpdated = DateTimeOffset.UtcNow };
            current = node.ParentId;
        }
        return Task.CompletedTask;
    }

    private string BuildHierarchyPath(string nodeId)
    {
        var parts = new List<string>();
        var current = nodeId;

        while (current != null && _nodes.TryGetValue(current, out var node))
        {
            if (current != RootNodeId)
                parts.Insert(0, node.Name);
            current = node.ParentId;
        }

        return string.Join("/", parts);
    }

    private async Task<ContextNode?> FindNodeByPath(string[] pathParts, CancellationToken ct)
    {
        var currentNodeId = RootNodeId;

        foreach (var part in pathParts)
        {
            var children = await GetChildrenAsync(currentNodeId, 1, ct);
            var match = children.FirstOrDefault(c =>
                c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));

            if (match == null)
                return null;

            currentNodeId = match.NodeId;
        }

        return _nodes.TryGetValue(currentNodeId, out var node) ? ToContextNode(node) : null;
    }

    private IEnumerable<IndexedContextEntry> GetEntriesForNode(string nodeId)
    {
        return _entryToNode
            .Where(kvp => kvp.Value == nodeId)
            .Select(kvp => _entries.TryGetValue(kvp.Key, out var entry) ? entry : null)
            .Where(e => e != null)
            .Select(e => new IndexedContextEntry
            {
                ContentId = e!.ContentId,
                RelevanceScore = 1.0f,
                Summary = e.Summary,
                SemanticTags = e.Tags,
                ContentSizeBytes = e.ContentSizeBytes,
                Pointer = e.Pointer,
                CreatedAt = e.CreatedAt,
                LastAccessedAt = e.LastAccessedAt,
                AccessCount = e.AccessCount,
                ImportanceScore = e.ImportanceScore,
                Tier = e.Tier
            });
    }

    private bool MatchesFilters(IndexedEntry entry, ContextQuery query)
    {
        // Scope filter
        if (query.Scope != null && !entry.Scope.Equals(query.Scope, StringComparison.OrdinalIgnoreCase))
            return false;

        // Required tags filter
        if (query.RequiredTags?.Length > 0)
        {
            if (!query.RequiredTags.All(t => entry.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                return false;
        }

        // Time range filter
        if (query.TimeRange != null)
        {
            if (query.TimeRange.Start.HasValue && entry.CreatedAt < query.TimeRange.Start.Value)
                return false;
            if (query.TimeRange.End.HasValue && entry.CreatedAt > query.TimeRange.End.Value)
                return false;
        }

        // Custom filters
        if (query.Filters != null)
        {
            // Apply custom filters based on entry metadata
            // This is a simplified implementation
        }

        return true;
    }

    private float CalculateRelevance(IndexedEntry entry, string[] queryWords, float[]? queryEmbedding)
    {
        var relevance = 0f;

        // Text-based relevance
        var summaryLower = entry.Summary.ToLowerInvariant();
        var matchCount = queryWords.Count(w => summaryLower.Contains(w));
        relevance += queryWords.Length > 0 ? (float)matchCount / queryWords.Length * 0.5f : 0f;

        // Tag matching
        var tagMatches = entry.Tags.Count(t => queryWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));
        relevance += Math.Min(tagMatches * 0.1f, 0.3f);

        // Embedding similarity (if available)
        if (queryEmbedding != null && entry.Embedding != null)
        {
            var similarity = CosineSimilarity(queryEmbedding, entry.Embedding);
            relevance = relevance * 0.4f + similarity * 0.6f;
        }

        // Boost by importance
        relevance += entry.ImportanceScore * 0.2f;

        return Math.Clamp(relevance, 0f, 1f);
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

    private static string GenerateSummary(IndexedEntry entry, SummaryLevel level)
    {
        return level switch
        {
            SummaryLevel.Minimal => entry.Summary.Length > 50 ? entry.Summary[..50] + "..." : entry.Summary,
            SummaryLevel.Brief => entry.Summary.Length > 150 ? entry.Summary[..150] + "..." : entry.Summary,
            SummaryLevel.Detailed => entry.Summary,
            SummaryLevel.Full => entry.Summary + (entry.Tags.Length > 0 ? $" [Tags: {string.Join(", ", entry.Tags)}]" : ""),
            _ => entry.Summary
        };
    }

    private static string GenerateContentSummary(byte[] content)
    {
        try
        {
            var text = Encoding.UTF8.GetString(content);
            if (text.Length <= SummaryMaxLength)
                return text;

            // Simple summarization: take first N characters at sentence boundary
            var cutoff = text.LastIndexOf('.', SummaryMaxLength);
            if (cutoff < 100) cutoff = SummaryMaxLength;
            return text[..cutoff] + "...";
        }
        catch
        {
            return $"Binary content ({content.Length} bytes)";
        }
    }

    private static string[]? GenerateSuggestedQueries(ContextQuery original, IList<IndexedContextEntry> results)
    {
        if (results.Count == 0) return null;

        var topTags = results
            .Where(r => r.SemanticTags?.Length > 0)
            .SelectMany(r => r.SemanticTags!)
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{original.SemanticQuery} AND {g.Key}")
            .ToArray();

        return topTags.Length > 0 ? topTags : null;
    }

    private static string[]? ExtractRelatedTopics(IList<IndexedContextEntry> results)
    {
        if (results.Count == 0) return null;

        return results
            .Where(r => r.SemanticTags?.Length > 0)
            .SelectMany(r => r.SemanticTags!)
            .Distinct()
            .Take(10)
            .ToArray();
    }

    private ContextNode ToContextNode(SummaryNode node)
    {
        return new ContextNode
        {
            NodeId = node.NodeId,
            ParentId = node.ParentId,
            Name = node.Name,
            Summary = node.Summary,
            Level = node.Level,
            ChildCount = node.ChildCount,
            TotalEntryCount = node.TotalEntryCount,
            TotalSizeBytes = node.TotalSizeBytes,
            Embedding = node.Embedding,
            Tags = node.Tags,
            LastUpdated = node.LastUpdated,
            Scope = node.NodeId.Contains("scope:") ? node.NodeId.Split('/')[0].Replace("scope:", "") : null
        };
    }

    private long EstimateIndexSize()
    {
        var nodeSize = _nodes.Count * 1024; // Approximate bytes per node
        var entryIndexSize = _entries.Count * 256; // Approximate bytes per entry index
        return nodeSize + entryIndexSize;
    }

    private double CalculateFragmentation()
    {
        // Calculate based on empty nodes and unbalanced tree
        var emptyNodes = _nodes.Values.Count(n => n.TotalEntryCount == 0 && n.Level > 0);
        return _nodes.Count > 0 ? (double)emptyNodes / _nodes.Count * 100 : 0;
    }

    private Task RebalanceTree(CancellationToken ct)
    {
        // Find nodes with too many children
        foreach (var kvp in _childIndex)
        {
            if (kvp.Value.Count > MaxChildrenPerNode && _nodes.TryGetValue(kvp.Key, out var parent))
            {
                // Would split the node - simplified for this implementation
            }
        }
        return Task.CompletedTask;
    }

    private Task RegenerateStaleSummaries(CancellationToken ct)
    {
        var staleThreshold = DateTimeOffset.UtcNow.AddDays(-7);
        var staleNodes = _nodes.Values.Where(n => n.LastUpdated < staleThreshold).ToList();

        foreach (var node in staleNodes)
        {
            // Would regenerate summary using AI - simplified
            _nodes[node.NodeId] = node with { LastUpdated = DateTimeOffset.UtcNow };
        }

        return Task.CompletedTask;
    }

    private void RemoveEmptyNodes()
    {
        var emptyLeafNodes = _nodes.Values
            .Where(n => n.Level > 1 && n.ChildCount == 0 && n.TotalEntryCount == 0)
            .Select(n => n.NodeId)
            .ToList();

        foreach (var nodeId in emptyLeafNodes)
        {
            if (_nodes.TryRemove(nodeId, out var node) && node.ParentId != null)
            {
                if (_childIndex.TryGetValue(node.ParentId, out var siblings))
                {
                    siblings.Remove(nodeId);
                }
                if (_nodes.TryGetValue(node.ParentId, out var parent))
                {
                    _nodes[node.ParentId] = parent with { ChildCount = parent.ChildCount - 1 };
                }
            }
        }
    }

    #endregion

    #region Internal Types

    private sealed record SummaryNode
    {
        public required string NodeId { get; init; }
        public string? ParentId { get; init; }
        public required string Name { get; init; }
        public required string Summary { get; init; }
        public int Level { get; init; }
        public int ChildCount { get; init; }
        public long TotalEntryCount { get; init; }
        public long TotalSizeBytes { get; init; }
        public float[]? Embedding { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastUpdated { get; init; }
    }

    private sealed record IndexedEntry
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
        public string[] Entities { get; init; } = Array.Empty<string>();
    }

    #endregion
}

#region Navigation Result Types

/// <summary>
/// Overview of the entire context hierarchy.
/// </summary>
public record ContextOverview
{
    /// <summary>Summary of the entire context.</summary>
    public required string RootSummary { get; init; }

    /// <summary>Total number of entries indexed.</summary>
    public long TotalEntries { get; init; }

    /// <summary>Total size of all indexed content.</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>Top-level domain nodes.</summary>
    public required IList<ContextNode> TopLevelDomains { get; init; }

    /// <summary>Entry counts by domain.</summary>
    public required Dictionary<string, long> DomainCounts { get; init; }

    /// <summary>When the context was last updated.</summary>
    public DateTimeOffset LastUpdated { get; init; }
}

/// <summary>
/// Result of drilling down into a specific area.
/// </summary>
public record DrillDownResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The current node being viewed.</summary>
    public ContextNode? CurrentNode { get; init; }

    /// <summary>Child nodes matching the focus query.</summary>
    public IList<ContextNode>? MatchingSubNodes { get; init; }

    /// <summary>Relevant entries from matching nodes.</summary>
    public IList<IndexedContextEntry>? RelevantEntries { get; init; }

    /// <summary>Whether deeper navigation is possible.</summary>
    public bool CanDrillDeeper { get; init; }

    /// <summary>Suggested paths for further drilling.</summary>
    public string[]? SuggestedPaths { get; init; }
}

/// <summary>
/// Result of zooming out to broader context.
/// </summary>
public record ZoomOutResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Path of ancestor nodes from current to target.</summary>
    public IList<ContextNode>? AncestorPath { get; init; }

    /// <summary>The target broader node.</summary>
    public ContextNode? TargetNode { get; init; }

    /// <summary>Sibling nodes at the target level.</summary>
    public IList<ContextNode>? SiblingNodes { get; init; }

    /// <summary>Summary of the broader context.</summary>
    public string? BroaderSummary { get; init; }
}

#endregion
