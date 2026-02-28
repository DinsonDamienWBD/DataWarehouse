using System.Threading;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Knowledge entry stored in the lake.
/// </summary>
public record KnowledgeEntry
{
    /// <summary>The knowledge object.</summary>
    public required KnowledgeObject Knowledge { get; init; }

    /// <summary>When stored.</summary>
    public DateTimeOffset StoredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When last accessed. Stored as UTC ticks for atomic 64-bit writes via Interlocked.
    /// Use <see cref="RecordAccess"/> to update atomically.
    /// </summary>
    private long _lastAccessedAtTicks;
    public DateTimeOffset LastAccessedAt
    {
        get => new DateTimeOffset(Interlocked.Read(ref _lastAccessedAtTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _lastAccessedAtTicks, value.UtcTicks);
    }

    /// <summary>Access count (for caching decisions). Use Interlocked.Increment for thread-safe updates.</summary>
    private long _accessCount;
    public long AccessCount
    {
        get => Interlocked.Read(ref _accessCount);
        set => Interlocked.Exchange(ref _accessCount, value);
    }

    /// <summary>
    /// Atomically records an access, incrementing the counter and updating the timestamp.
    /// </summary>
    public void RecordAccess()
    {
        Interlocked.Increment(ref _accessCount);
        Interlocked.Exchange(ref _lastAccessedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>Time-to-live (null = permanent).</summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>Whether this is static (load-time) or dynamic (runtime).</summary>
    public bool IsStatic { get; init; }
}

/// <summary>
/// Query for knowledge retrieval.
/// </summary>
public record KnowledgeQuery
{
    /// <summary>Filter by topic pattern (supports wildcards).</summary>
    public string? TopicPattern { get; init; }

    /// <summary>Filter by source plugin.</summary>
    public string? SourcePluginId { get; init; }

    /// <summary>Filter by knowledge type.</summary>
    public string? KnowledgeType { get; init; }

    /// <summary>Filter by tags (all must match).</summary>
    public string[]? RequiredTags { get; init; }

    /// <summary>Text search.</summary>
    public string? SearchText { get; init; }

    /// <summary>Only static knowledge.</summary>
    public bool? OnlyStatic { get; init; }

    /// <summary>Temporal query: as of this time.</summary>
    public DateTimeOffset? AsOfTime { get; init; }

    /// <summary>Maximum results.</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// Central knowledge storage for AI and caching.
/// Stores static (load-time) and dynamic (runtime) knowledge.
/// </summary>
public interface IKnowledgeLake
{
    // Storage
    Task StoreAsync(KnowledgeObject knowledge, bool isStatic = false, TimeSpan? ttl = null, CancellationToken ct = default);
    Task StoreBatchAsync(IEnumerable<KnowledgeObject> knowledge, bool isStatic = false, CancellationToken ct = default);
    Task RemoveAsync(string knowledgeId, CancellationToken ct = default);
    Task RemoveByPluginAsync(string pluginId, CancellationToken ct = default);
    Task RemoveByTopicAsync(string topic, CancellationToken ct = default);

    // Retrieval
    KnowledgeEntry? Get(string knowledgeId);
    IReadOnlyList<KnowledgeEntry> GetByTopic(string topic);
    IReadOnlyList<KnowledgeEntry> GetByPlugin(string pluginId);
    Task<IReadOnlyList<KnowledgeEntry>> QueryAsync(KnowledgeQuery query, CancellationToken ct = default);

    // For AI: Get all knowledge for context building
    IReadOnlyList<KnowledgeEntry> GetAllStatic();
    IReadOnlyList<KnowledgeEntry> GetRecent(int count = 100);

    // Cache management
    Task InvalidateAsync(string pluginId, CancellationToken ct = default);
    Task ClearExpiredAsync(CancellationToken ct = default);

    // Statistics
    KnowledgeLakeStatistics GetStatistics();
}

/// <summary>
/// Statistics about the knowledge lake.
/// </summary>
public record KnowledgeLakeStatistics
{
    public int TotalEntries { get; init; }
    public int StaticEntries { get; init; }
    public int DynamicEntries { get; init; }
    public int ExpiredEntries { get; init; }
    public long TotalAccessCount { get; init; }
    public IReadOnlyDictionary<string, int> EntriesByPlugin { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> EntriesByTopic { get; init; } = new Dictionary<string, int>();
}
