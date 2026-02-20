using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Indexing;

#region Core Interfaces

/// <summary>
/// AI-native index that provides efficient access to exabyte-scale context.
/// AI agents query this index to find exact relevant information without
/// reading through the full context. Designed for hierarchical navigation
/// and semantic querying across massive memory systems.
/// </summary>
public interface IContextIndex
{
    /// <summary>
    /// Gets the unique identifier for this index.
    /// </summary>
    string IndexId { get; }

    /// <summary>
    /// Gets the human-readable name of this index.
    /// </summary>
    string IndexName { get; }

    /// <summary>
    /// Gets whether the index is currently available and initialized.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Performs a semantic query - AI describes what it needs, index returns pointers.
    /// </summary>
    /// <param name="query">The context query with semantic description and filters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query result with matching entries and navigation hints.</returns>
    Task<ContextQueryResult> QueryAsync(ContextQuery query, CancellationToken ct = default);

    /// <summary>
    /// Gets a specific node in the hierarchical index for drill-down navigation.
    /// </summary>
    /// <param name="nodeId">The unique identifier of the node.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The context node if found, null otherwise.</returns>
    Task<ContextNode?> GetNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Gets children of a node for hierarchical navigation (zoom in).
    /// </summary>
    /// <param name="parentId">The parent node identifier. Use null for root nodes.</param>
    /// <param name="depth">How many levels of children to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of child nodes.</returns>
    Task<IEnumerable<ContextNode>> GetChildrenAsync(string? parentId, int depth = 1, CancellationToken ct = default);

    /// <summary>
    /// Indexes new content into the system.
    /// </summary>
    /// <param name="contentId">Unique identifier for the content.</param>
    /// <param name="content">The raw content bytes to index.</param>
    /// <param name="metadata">Metadata about the content.</param>
    /// <param name="ct">Cancellation token.</param>
    Task IndexContentAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing index entry.
    /// </summary>
    /// <param name="contentId">The content identifier to update.</param>
    /// <param name="update">The update to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateIndexAsync(string contentId, IndexUpdate update, CancellationToken ct = default);

    /// <summary>
    /// Removes content from the index.
    /// </summary>
    /// <param name="contentId">The content identifier to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveFromIndexAsync(string contentId, CancellationToken ct = default);

    /// <summary>
    /// Gets statistics about the index.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Index statistics including size, freshness, and performance metrics.</returns>
    Task<IndexStatistics> GetStatisticsAsync(CancellationToken ct = default);

    /// <summary>
    /// Optimizes the index for query performance.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task OptimizeAsync(CancellationToken ct = default);
}

#endregion

#region Query Types

/// <summary>
/// A semantic query for context retrieval. AI agents use natural language
/// to describe what they need, and the index returns precise pointers.
/// </summary>
public record ContextQuery
{
    /// <summary>
    /// Natural language description of what information is needed.
    /// Example: "Find all database schemas related to customer data"
    /// </summary>
    public required string SemanticQuery { get; init; }

    /// <summary>
    /// Optional pre-computed embedding vector for the query.
    /// If not provided, the index will generate one.
    /// </summary>
    public float[]? QueryEmbedding { get; init; }

    /// <summary>
    /// Tags that matching entries must have (AND logic).
    /// </summary>
    public string[]? RequiredTags { get; init; }

    /// <summary>
    /// Additional filters to apply (key-value pairs).
    /// </summary>
    public Dictionary<string, object>? Filters { get; init; }

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int MaxResults { get; init; } = 100;

    /// <summary>
    /// Minimum relevance score (0.0 to 1.0) for results.
    /// </summary>
    public float MinRelevance { get; init; } = 0.5f;

    /// <summary>
    /// Whether to include the full hierarchy path in results.
    /// </summary>
    public bool IncludeHierarchyPath { get; init; } = true;

    /// <summary>
    /// Level of detail for summaries in results.
    /// </summary>
    public SummaryLevel DetailLevel { get; init; } = SummaryLevel.Brief;

    /// <summary>
    /// Optional scope to limit the search (e.g., "finance", "healthcare").
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Optional time range filter for temporal queries.
    /// </summary>
    public TimeRange? TimeRange { get; init; }

    /// <summary>
    /// Whether to cluster results by semantic similarity.
    /// </summary>
    public bool ClusterResults { get; init; } = false;
}

/// <summary>
/// Level of detail for AI-generated summaries.
/// </summary>
public enum SummaryLevel
{
    /// <summary>Single sentence or title only.</summary>
    Minimal,

    /// <summary>2-3 sentences covering key points.</summary>
    Brief,

    /// <summary>Full paragraph with context.</summary>
    Detailed,

    /// <summary>Complete summary with all relevant details.</summary>
    Full
}

/// <summary>
/// A time range for temporal filtering.
/// </summary>
public record TimeRange
{
    /// <summary>Start of the time range (inclusive).</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>End of the time range (inclusive).</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>
    /// Creates a time range for the last N hours.
    /// </summary>
    public static TimeRange LastHours(int hours) => new()
    {
        Start = DateTimeOffset.UtcNow.AddHours(-hours),
        End = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Creates a time range for the last N days.
    /// </summary>
    public static TimeRange LastDays(int days) => new()
    {
        Start = DateTimeOffset.UtcNow.AddDays(-days),
        End = DateTimeOffset.UtcNow
    };
}

#endregion

#region Result Types

/// <summary>
/// Result of a context query, containing matching entries and navigation hints.
/// </summary>
public record ContextQueryResult
{
    /// <summary>
    /// List of matching indexed entries, sorted by relevance.
    /// </summary>
    public required IList<IndexedContextEntry> Entries { get; init; }

    /// <summary>
    /// AI-readable summary of where to find more related information.
    /// Guides the AI on how to navigate deeper if needed.
    /// </summary>
    public string? NavigationSummary { get; init; }

    /// <summary>
    /// Semantic clusters found in the results with entry counts.
    /// </summary>
    public Dictionary<string, int>? ClusterCounts { get; init; }

    /// <summary>
    /// Total number of matching entries (may exceed returned entries).
    /// </summary>
    public long TotalMatchingEntries { get; init; }

    /// <summary>
    /// Time taken to execute the query.
    /// </summary>
    public TimeSpan QueryDuration { get; init; }

    /// <summary>
    /// Suggested queries for refining results.
    /// </summary>
    public string[]? SuggestedQueries { get; init; }

    /// <summary>
    /// Related topics that might be relevant.
    /// </summary>
    public string[]? RelatedTopics { get; init; }

    /// <summary>
    /// Whether results were truncated due to limits.
    /// </summary>
    public bool WasTruncated { get; init; }

    /// <summary>
    /// Creates an empty result.
    /// </summary>
    public static ContextQueryResult Empty => new()
    {
        Entries = Array.Empty<IndexedContextEntry>(),
        TotalMatchingEntries = 0,
        QueryDuration = TimeSpan.Zero
    };
}

/// <summary>
/// A single indexed context entry with pointers to the actual content.
/// </summary>
public record IndexedContextEntry
{
    /// <summary>
    /// Unique identifier for this content.
    /// </summary>
    public required string ContentId { get; init; }

    /// <summary>
    /// Hierarchical path to this content.
    /// Example: "memory/tier3/scope:finance/cluster:schemas"
    /// </summary>
    public string? HierarchyPath { get; init; }

    /// <summary>
    /// Relevance score for this entry (0.0 to 1.0).
    /// </summary>
    public float RelevanceScore { get; init; }

    /// <summary>
    /// AI-generated brief summary of the content.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Semantic tags associated with this content.
    /// </summary>
    public string[]? SemanticTags { get; init; }

    /// <summary>
    /// Size of the actual content in bytes.
    /// </summary>
    public long ContentSizeBytes { get; init; }

    /// <summary>
    /// Exact location pointer to read the content if needed.
    /// </summary>
    public required ContextPointer Pointer { get; init; }

    /// <summary>
    /// When this content was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When this content was last accessed.
    /// </summary>
    public DateTimeOffset? LastAccessedAt { get; init; }

    /// <summary>
    /// Number of times this content has been accessed.
    /// </summary>
    public int AccessCount { get; init; }

    /// <summary>
    /// Importance score based on access patterns and content analysis.
    /// </summary>
    public float ImportanceScore { get; init; }

    /// <summary>
    /// The memory tier this content belongs to.
    /// </summary>
    public MemoryTier Tier { get; init; }
}

/// <summary>
/// Pointer to the exact location of content in storage.
/// </summary>
public record ContextPointer
{
    /// <summary>
    /// The storage backend identifier (e.g., "azure-blob", "s3", "local").
    /// </summary>
    public required string StorageBackend { get; init; }

    /// <summary>
    /// Path within the storage backend.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Byte offset within the file/object.
    /// </summary>
    public long Offset { get; init; }

    /// <summary>
    /// Length of the content in bytes.
    /// </summary>
    public int Length { get; init; }

    /// <summary>
    /// Optional checksum for verification.
    /// </summary>
    public string? Checksum { get; init; }

    /// <summary>
    /// Optional compression algorithm used.
    /// </summary>
    public string? Compression { get; init; }

    /// <summary>
    /// Optional encryption key identifier.
    /// </summary>
    public string? EncryptionKeyId { get; init; }
}

#endregion

#region Node Types

/// <summary>
/// A node in the hierarchical context index. Represents a summarization
/// level that can be navigated (zoomed in/out).
/// </summary>
public record ContextNode
{
    /// <summary>
    /// Unique identifier for this node.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Parent node identifier (null for root nodes).
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Human-readable name/title for this node.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// AI-generated summary of all content under this node.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Level in the hierarchy (0 = root).
    /// </summary>
    public int Level { get; init; }

    /// <summary>
    /// Number of direct children.
    /// </summary>
    public int ChildCount { get; init; }

    /// <summary>
    /// Total number of leaf entries under this node.
    /// </summary>
    public long TotalEntryCount { get; init; }

    /// <summary>
    /// Total size of all content under this node in bytes.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Embedding vector for this node's summary.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Semantic tags aggregated from children.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// When this node was last updated.
    /// </summary>
    public DateTimeOffset LastUpdated { get; init; }

    /// <summary>
    /// Optional metadata for this node.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// The scope/domain this node belongs to.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Time range covered by content under this node.
    /// </summary>
    public TimeRange? CoveredTimeRange { get; init; }
}

#endregion

#region Metadata and Updates

/// <summary>
/// Metadata about indexed content.
/// </summary>
public record ContextMetadata
{
    /// <summary>
    /// The source of this content (e.g., "user-input", "system", "external").
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Content type/format (e.g., "text/plain", "application/json").
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The scope/domain this content belongs to.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Semantic tags for categorization.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// The memory tier to store this content in.
    /// </summary>
    public MemoryTier Tier { get; init; } = MemoryTier.Working;

    /// <summary>
    /// Pre-computed embedding for the content.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Pre-generated summary of the content.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Extracted entities from the content.
    /// </summary>
    public string[]? Entities { get; init; }

    /// <summary>
    /// When this content was originally created.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Additional custom metadata.
    /// </summary>
    public Dictionary<string, object>? Custom { get; init; }

    /// <summary>
    /// Importance score override (if not set, will be calculated).
    /// </summary>
    public float? ImportanceScore { get; init; }
}

/// <summary>
/// An update to apply to an indexed entry.
/// </summary>
public record IndexUpdate
{
    /// <summary>
    /// New summary to set (null = no change).
    /// </summary>
    public string? NewSummary { get; init; }

    /// <summary>
    /// New tags to set (null = no change).
    /// </summary>
    public string[]? NewTags { get; init; }

    /// <summary>
    /// Tags to add (merged with existing).
    /// </summary>
    public string[]? AddTags { get; init; }

    /// <summary>
    /// Tags to remove.
    /// </summary>
    public string[]? RemoveTags { get; init; }

    /// <summary>
    /// New importance score (null = no change).
    /// </summary>
    public float? NewImportanceScore { get; init; }

    /// <summary>
    /// New embedding (null = no change).
    /// </summary>
    public float[]? NewEmbedding { get; init; }

    /// <summary>
    /// Metadata updates to apply.
    /// </summary>
    public Dictionary<string, object>? MetadataUpdates { get; init; }

    /// <summary>
    /// Whether to recalculate the summary.
    /// </summary>
    public bool RecalculateSummary { get; init; }

    /// <summary>
    /// Whether to recalculate the embedding.
    /// </summary>
    public bool RecalculateEmbedding { get; init; }

    /// <summary>
    /// Record an access (increments access count, updates last accessed).
    /// </summary>
    public bool RecordAccess { get; init; }
}

#endregion

#region Statistics

/// <summary>
/// Statistics about an index.
/// </summary>
public record IndexStatistics
{
    /// <summary>
    /// Unique identifier for the index.
    /// </summary>
    public required string IndexId { get; init; }

    /// <summary>
    /// Total number of indexed entries.
    /// </summary>
    public long TotalEntries { get; init; }

    /// <summary>
    /// Total size of all indexed content in bytes.
    /// </summary>
    public long TotalContentBytes { get; init; }

    /// <summary>
    /// Size of the index itself in bytes.
    /// </summary>
    public long IndexSizeBytes { get; init; }

    /// <summary>
    /// Number of hierarchy nodes.
    /// </summary>
    public long NodeCount { get; init; }

    /// <summary>
    /// Maximum depth of the hierarchy.
    /// </summary>
    public int MaxDepth { get; init; }

    /// <summary>
    /// When the index was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the index was last updated.
    /// </summary>
    public DateTimeOffset LastUpdated { get; init; }

    /// <summary>
    /// When the index was last optimized.
    /// </summary>
    public DateTimeOffset? LastOptimized { get; init; }

    /// <summary>
    /// Total number of queries performed.
    /// </summary>
    public long TotalQueries { get; init; }

    /// <summary>
    /// Average query latency in milliseconds.
    /// </summary>
    public double AverageQueryLatencyMs { get; init; }

    /// <summary>
    /// Number of entries by tier.
    /// </summary>
    public Dictionary<MemoryTier, long>? EntriesByTier { get; init; }

    /// <summary>
    /// Number of entries by scope.
    /// </summary>
    public Dictionary<string, long>? EntriesByScope { get; init; }

    /// <summary>
    /// Index freshness score (0.0 to 1.0, higher = more up-to-date).
    /// </summary>
    public float FreshnessScore { get; init; }

    /// <summary>
    /// Whether the index needs optimization.
    /// </summary>
    public bool NeedsOptimization { get; init; }

    /// <summary>
    /// Fragmentation percentage (0-100).
    /// </summary>
    public double FragmentationPercent { get; init; }
}

#endregion

#region Base Implementation

/// <summary>
/// Abstract base class for context index implementations.
/// Provides common functionality for statistics, configuration, and tracking.
/// </summary>
public abstract class ContextIndexBase : IContextIndex
{
    private long _totalQueries;
    private long _totalLatencyTicks;
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastUpdated = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastOptimized;

    /// <summary>
    /// Configuration dictionary for this index.
    /// </summary>
    protected readonly BoundedDictionary<string, string> Configuration = new BoundedDictionary<string, string>(1000);

    /// <inheritdoc/>
    public abstract string IndexId { get; }

    /// <inheritdoc/>
    public abstract string IndexName { get; }

    /// <inheritdoc/>
    public virtual bool IsAvailable => true;

    /// <summary>
    /// Sets a configuration value.
    /// </summary>
    public void Configure(string key, string value)
    {
        Configuration[key] = value;
    }

    /// <summary>
    /// Gets a configuration value.
    /// </summary>
    protected string? GetConfig(string key)
    {
        return Configuration.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc/>
    public abstract Task<ContextQueryResult> QueryAsync(ContextQuery query, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<ContextNode?> GetNodeAsync(string nodeId, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<IEnumerable<ContextNode>> GetChildrenAsync(string? parentId, int depth = 1, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task IndexContentAsync(string contentId, byte[] content, ContextMetadata metadata, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task UpdateIndexAsync(string contentId, IndexUpdate update, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task RemoveFromIndexAsync(string contentId, CancellationToken ct = default);

    /// <inheritdoc/>
    public abstract Task<IndexStatistics> GetStatisticsAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public virtual Task OptimizeAsync(CancellationToken ct = default)
    {
        _lastOptimized = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records a query execution for statistics.
    /// </summary>
    protected void RecordQuery(TimeSpan duration)
    {
        Interlocked.Increment(ref _totalQueries);
        Interlocked.Add(ref _totalLatencyTicks, duration.Ticks);
    }

    /// <summary>
    /// Marks the index as updated.
    /// </summary>
    protected void MarkUpdated()
    {
        _lastUpdated = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Gets base statistics that subclasses can extend.
    /// </summary>
    protected IndexStatistics GetBaseStatistics(long totalEntries, long totalContentBytes, long indexSizeBytes, long nodeCount, int maxDepth)
    {
        var queries = Interlocked.Read(ref _totalQueries);
        var latencyTicks = Interlocked.Read(ref _totalLatencyTicks);

        return new IndexStatistics
        {
            IndexId = IndexId,
            TotalEntries = totalEntries,
            TotalContentBytes = totalContentBytes,
            IndexSizeBytes = indexSizeBytes,
            NodeCount = nodeCount,
            MaxDepth = maxDepth,
            CreatedAt = _createdAt,
            LastUpdated = _lastUpdated,
            LastOptimized = _lastOptimized,
            TotalQueries = queries,
            AverageQueryLatencyMs = queries > 0 ? TimeSpan.FromTicks(latencyTicks / queries).TotalMilliseconds : 0,
            FreshnessScore = CalculateFreshness(),
            NeedsOptimization = ShouldOptimize()
        };
    }

    /// <summary>
    /// Calculates index freshness based on last update time.
    /// </summary>
    protected virtual float CalculateFreshness()
    {
        var age = DateTimeOffset.UtcNow - _lastUpdated;
        if (age.TotalHours < 1) return 1.0f;
        if (age.TotalDays < 1) return 0.9f;
        if (age.TotalDays < 7) return 0.7f;
        if (age.TotalDays < 30) return 0.5f;
        return 0.3f;
    }

    /// <summary>
    /// Determines if the index should be optimized.
    /// </summary>
    protected virtual bool ShouldOptimize()
    {
        if (_lastOptimized == null) return true;
        var sinceOptimized = DateTimeOffset.UtcNow - _lastOptimized.Value;
        return sinceOptimized.TotalDays > 7;
    }

    /// <summary>
    /// Generates an AI-readable navigation summary.
    /// </summary>
    protected static string GenerateNavigationSummary(IList<IndexedContextEntry> entries, long totalMatches)
    {
        if (!entries.Any())
            return "No matching entries found. Try broadening your search terms or checking different scopes.";

        var scopes = entries.Where(e => e.HierarchyPath != null)
            .Select(e => e.HierarchyPath!.Split('/').FirstOrDefault())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .Take(5)
            .ToList();

        var scopeList = scopes.Count > 0 ? string.Join(", ", scopes) : "various domains";

        if (totalMatches > entries.Count)
        {
            return $"Found {totalMatches} matching entries across {scopeList}. " +
                   $"Showing top {entries.Count} by relevance. " +
                   "To see more results, refine your query with specific filters or navigate deeper into relevant clusters.";
        }

        return $"Found {entries.Count} matching entries across {scopeList}. " +
               "Use hierarchy navigation to explore related content.";
    }

    /// <summary>
    /// Calculates importance score for content.
    /// </summary>
    protected static float CalculateImportance(byte[] content, ContextMetadata metadata)
    {
        var importance = metadata.ImportanceScore ?? 0.5f;

        // Boost for explicit tags
        if (metadata.Tags?.Length > 0)
            importance += 0.1f;

        // Boost for entities
        if (metadata.Entities?.Length > 0)
            importance += Math.Min(metadata.Entities.Length * 0.02f, 0.2f);

        // Boost for larger content (more information)
        if (content.Length > 1000)
            importance += 0.1f;

        return Math.Clamp(importance, 0f, 1f);
    }
}

#endregion
