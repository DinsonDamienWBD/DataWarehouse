using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory;

/// <summary>
/// Memory entry metadata for all memory strategies.
/// </summary>
public sealed record MemoryEntry
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? LastAccessedAt { get; set; }
    public int AccessCount { get; set; }
    public float ImportanceScore { get; set; } = 0.5f;
    public Dictionary<string, object>? Metadata { get; init; }
    public float[]? Embedding { get; init; }
}

/// <summary>
/// Retrieved memory with relevance score.
/// </summary>
public sealed record RetrievedMemory
{
    public required MemoryEntry Entry { get; init; }
    public required float RelevanceScore { get; init; }
}

/// <summary>
/// Statistics about memory usage.
/// </summary>
public sealed record MemoryStatistics
{
    public long TotalMemories { get; init; }
    public long WorkingMemoryCount { get; init; }
    public long ShortTermMemoryCount { get; init; }
    public long LongTermMemoryCount { get; init; }
    public long EpisodicMemoryCount { get; init; }
    public long SemanticMemoryCount { get; init; }
    public long TotalAccessCount { get; init; }
    public long ConsolidationCount { get; init; }
    public DateTime? LastConsolidation { get; init; }
    public long MemorySizeBytes { get; init; }
}

/// <summary>
/// Base class for Long-Term Memory strategies.
/// </summary>
public abstract class LongTermMemoryStrategyBase : IntelligenceStrategyBase
{
    protected long _totalMemoriesStored;
    protected long _totalMemoriesRetrieved;
    protected long _totalConsolidations;

    /// <inheritdoc/>
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.LongTermMemory;

    /// <summary>
    /// Stores a memory with optional metadata.
    /// </summary>
    public abstract Task<string> StoreMemoryAsync(string content, Dictionary<string, object>? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves memories similar to the query.
    /// </summary>
    public abstract Task<IEnumerable<RetrievedMemory>> RetrieveMemoriesAsync(string query, int topK = 10, float minRelevance = 0.0f, CancellationToken ct = default);

    /// <summary>
    /// Consolidates memories, moving important items from short-term to long-term storage.
    /// </summary>
    public abstract Task ConsolidateMemoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Forgets a specific memory by ID.
    /// </summary>
    public abstract Task ForgetMemoryAsync(string memoryId, CancellationToken ct = default);

    /// <summary>
    /// Gets statistics about memory usage.
    /// </summary>
    public abstract Task<MemoryStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default);

    protected void RecordMemoryStored()
    {
        Interlocked.Increment(ref _totalMemoriesStored);
    }

    protected void RecordMemoryRetrieved(int count = 1)
    {
        Interlocked.Add(ref _totalMemoriesRetrieved, count);
    }

    protected void RecordConsolidation()
    {
        Interlocked.Increment(ref _totalConsolidations);
    }
}

// =============================================================================
// 1. MemGPT-Style Memory Management
// =============================================================================

/// <summary>
/// MemGPT-style memory management with hierarchical memory tiers.
/// Manages working memory, short-term memory, and long-term memory with automatic consolidation.
/// </summary>
public sealed class MemGptStrategy : LongTermMemoryStrategyBase
{
    private readonly ConcurrentDictionary<string, MemoryEntry> _workingMemory = new();
    private readonly ConcurrentDictionary<string, MemoryEntry> _shortTermMemory = new();
    private readonly ConcurrentDictionary<string, MemoryEntry> _longTermMemory = new();
    private DateTime _lastConsolidation = DateTime.UtcNow;

    private const int MaxWorkingMemory = 100;
    private const int MaxShortTermMemory = 1000;
    private const int ConsolidationThreshold = 50;

    /// <inheritdoc/>
    public override string StrategyId => "memory-memgpt";

    /// <inheritdoc/>
    public override string StrategyName => "MemGPT Hierarchical Memory";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "MemGPT",
        Description = "MemGPT-style hierarchical memory management with working, short-term, and long-term storage tiers. Automatic memory consolidation during idle periods.",
        Capabilities = IntelligenceCapabilities.MemoryStorage | IntelligenceCapabilities.MemoryRetrieval |
                      IntelligenceCapabilities.MemoryConsolidation | IntelligenceCapabilities.HierarchicalMemory |
                      IntelligenceCapabilities.WorkingMemory,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "WorkingMemorySize", Description = "Maximum working memory entries", Required = false, DefaultValue = "100" },
            new ConfigurationRequirement { Key = "ShortTermMemorySize", Description = "Maximum short-term memory entries", Required = false, DefaultValue = "1000" },
            new ConfigurationRequirement { Key = "ConsolidationIntervalMinutes", Description = "Minutes between consolidations", Required = false, DefaultValue = "30" }
        },
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "memgpt", "hierarchical", "working-memory", "consolidation", "local" }
    };

    /// <inheritdoc/>
    public override async Task<string> StoreMemoryAsync(string content, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var memoryId = Guid.NewGuid().ToString();
            var entry = new MemoryEntry
            {
                Id = memoryId,
                Content = content,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                AccessCount = 0,
                ImportanceScore = CalculateImportance(content, metadata),
                Metadata = metadata
            };

            _workingMemory[memoryId] = entry;
            RecordMemoryStored();

            // Check if working memory overflow
            if (_workingMemory.Count > MaxWorkingMemory)
            {
                await OverflowWorkingMemoryAsync(ct);
            }

            return memoryId;
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<RetrievedMemory>> RetrieveMemoriesAsync(string query, int topK = 10, float minRelevance = 0.0f, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var allMemories = _workingMemory.Values
                .Concat(_shortTermMemory.Values)
                .Concat(_longTermMemory.Values)
                .ToList();

            var results = allMemories
                .Select(m =>
                {
                    m.AccessCount++;
                    m.LastAccessedAt = DateTime.UtcNow;
                    var score = CalculateRelevance(query, m);
                    return new RetrievedMemory { Entry = m, RelevanceScore = score };
                })
                .Where(r => r.RelevanceScore >= minRelevance)
                .OrderByDescending(r => r.RelevanceScore)
                .Take(topK)
                .ToList();

            RecordMemoryRetrieved(results.Count);
            await Task.CompletedTask;
            return results;
        });
    }

    /// <inheritdoc/>
    public override async Task ConsolidateMemoriesAsync(CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Move important working memories to short-term
            await OverflowWorkingMemoryAsync(ct);

            // Promote important short-term memories to long-term
            var shortTermToPromote = _shortTermMemory.Values
                .Where(m => m.ImportanceScore > 0.7f || m.AccessCount > 5)
                .ToList();

            foreach (var memory in shortTermToPromote)
            {
                _longTermMemory[memory.Id] = memory;
                _shortTermMemory.TryRemove(memory.Id, out _);
            }

            // Apply decay to old, unimportant memories
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var forgottenMemories = _shortTermMemory.Values
                .Where(m => m.LastAccessedAt < cutoff && m.ImportanceScore < 0.3f)
                .Select(m => m.Id)
                .ToList();

            foreach (var id in forgottenMemories)
            {
                _shortTermMemory.TryRemove(id, out _);
            }

            _lastConsolidation = DateTime.UtcNow;
            RecordConsolidation();
            await Task.CompletedTask;
        });
    }

    /// <inheritdoc/>
    public override async Task ForgetMemoryAsync(string memoryId, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            _workingMemory.TryRemove(memoryId, out _);
            _shortTermMemory.TryRemove(memoryId, out _);
            _longTermMemory.TryRemove(memoryId, out _);
            await Task.CompletedTask;
        });
    }

    /// <inheritdoc/>
    public override async Task<MemoryStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            await Task.CompletedTask;
            return new MemoryStatistics
            {
                TotalMemories = _workingMemory.Count + _shortTermMemory.Count + _longTermMemory.Count,
                WorkingMemoryCount = _workingMemory.Count,
                ShortTermMemoryCount = _shortTermMemory.Count,
                LongTermMemoryCount = _longTermMemory.Count,
                TotalAccessCount = Interlocked.Read(ref _totalMemoriesRetrieved),
                ConsolidationCount = Interlocked.Read(ref _totalConsolidations),
                LastConsolidation = _lastConsolidation
            };
        });
    }

    private async Task OverflowWorkingMemoryAsync(CancellationToken ct)
    {
        var toMove = _workingMemory.Values
            .OrderBy(m => m.ImportanceScore)
            .ThenBy(m => m.LastAccessedAt)
            .Take(ConsolidationThreshold)
            .ToList();

        foreach (var memory in toMove)
        {
            _shortTermMemory[memory.Id] = memory;
            _workingMemory.TryRemove(memory.Id, out _);
        }

        await Task.CompletedTask;
    }

    private float CalculateImportance(string content, Dictionary<string, object>? metadata)
    {
        var importance = 0.5f;

        // Longer content is more important
        importance += Math.Min(content.Length / 1000f, 0.2f);

        // Metadata indicates importance
        if (metadata?.Count > 0)
            importance += 0.1f;

        return Math.Clamp(importance, 0f, 1f);
    }

    private float CalculateRelevance(string query, MemoryEntry memory)
    {
        // Simple keyword-based relevance (could be enhanced with embeddings)
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var contentWords = memory.Content.ToLowerInvariant();

        var matchCount = queryWords.Count(word => contentWords.Contains(word));
        var relevance = queryWords.Length > 0 ? (float)matchCount / queryWords.Length : 0f;

        // Boost recent memories
        var recency = (DateTime.UtcNow - memory.CreatedAt).TotalDays;
        relevance += Math.Max(0, 0.2f - (float)(recency / 100));

        // Boost frequently accessed memories
        relevance += Math.Min(memory.AccessCount / 100f, 0.1f);

        return Math.Clamp(relevance, 0f, 1f);
    }
}

// =============================================================================
// 2. Chroma-Based Episodic Memory
// =============================================================================

/// <summary>
/// Chroma-based episodic memory with vector storage and temporal decay.
/// Uses ChromaDB for efficient vector-based memory retrieval with episode boundary detection.
/// </summary>
public sealed class ChromaMemoryStrategy : LongTermMemoryStrategyBase
{
    private readonly ConcurrentDictionary<string, MemoryEntry> _episodes = new();
    private readonly ConcurrentDictionary<string, List<string>> _episodeBoundaries = new();
    private DateTime _lastConsolidation = DateTime.UtcNow;

    /// <inheritdoc/>
    public override string StrategyId => "memory-chroma-episodic";

    /// <inheritdoc/>
    public override string StrategyName => "Chroma Episodic Memory";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "ChromaDB",
        Description = "Chroma-based episodic memory with vector storage, episode boundary detection, and temporal decay with importance-based retention.",
        Capabilities = IntelligenceCapabilities.MemoryStorage | IntelligenceCapabilities.MemoryRetrieval |
                      IntelligenceCapabilities.EpisodicMemory | IntelligenceCapabilities.MemoryDecay,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ChromaUrl", Description = "ChromaDB server URL", Required = true },
            new ConfigurationRequirement { Key = "CollectionName", Description = "Collection name for memories", Required = false, DefaultValue = "episodic_memory" },
            new ConfigurationRequirement { Key = "DecayRate", Description = "Memory decay rate per day (0-1)", Required = false, DefaultValue = "0.01" },
            new ConfigurationRequirement { Key = "EpisodeTimeoutMinutes", Description = "Minutes before starting new episode", Required = false, DefaultValue = "60" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "chroma", "chromadb", "episodic", "vector-memory", "temporal-decay" }
    };

    /// <inheritdoc/>
    public override async Task<string> StoreMemoryAsync(string content, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var memoryId = Guid.NewGuid().ToString();
            var episodeId = GetOrCreateEpisode();

            var entry = new MemoryEntry
            {
                Id = memoryId,
                Content = content,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                AccessCount = 0,
                ImportanceScore = 0.5f,
                Metadata = new Dictionary<string, object>(metadata ?? new Dictionary<string, object>())
                {
                    ["episode_id"] = episodeId
                }
            };

            _episodes[memoryId] = entry;

            if (!_episodeBoundaries.ContainsKey(episodeId))
                _episodeBoundaries[episodeId] = new List<string>();
            _episodeBoundaries[episodeId].Add(memoryId);

            RecordMemoryStored();

            // TODO: Store in actual ChromaDB when available
            await Task.CompletedTask;

            return memoryId;
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<RetrievedMemory>> RetrieveMemoriesAsync(string query, int topK = 10, float minRelevance = 0.0f, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            // Apply temporal decay
            var now = DateTime.UtcNow;
            var decayRate = float.Parse(GetConfig("DecayRate") ?? "0.01");

            var results = _episodes.Values
                .Select(m =>
                {
                    var daysSinceAccess = (now - (m.LastAccessedAt ?? m.CreatedAt)).TotalDays;
                    var decay = (float)Math.Pow(1 - decayRate, daysSinceAccess);
                    var relevance = CalculateRelevance(query, m) * decay * (1 + m.ImportanceScore);

                    m.AccessCount++;
                    m.LastAccessedAt = now;

                    return new RetrievedMemory { Entry = m, RelevanceScore = relevance };
                })
                .Where(r => r.RelevanceScore >= minRelevance)
                .OrderByDescending(r => r.RelevanceScore)
                .Take(topK)
                .ToList();

            RecordMemoryRetrieved(results.Count);
            await Task.CompletedTask;
            return results;
        });
    }

    /// <inheritdoc/>
    public override async Task ConsolidateMemoriesAsync(CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var decayRate = float.Parse(GetConfig("DecayRate") ?? "0.01");
            var cutoff = DateTime.UtcNow.AddDays(-90);

            // Remove very old, low-importance memories
            var toForget = _episodes.Values
                .Where(m => m.CreatedAt < cutoff && m.ImportanceScore < 0.2f && m.AccessCount < 2)
                .Select(m => m.Id)
                .ToList();

            foreach (var id in toForget)
            {
                _episodes.TryRemove(id, out _);
            }

            _lastConsolidation = DateTime.UtcNow;
            RecordConsolidation();
            await Task.CompletedTask;
        });
    }

    /// <inheritdoc/>
    public override async Task ForgetMemoryAsync(string memoryId, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            if (_episodes.TryRemove(memoryId, out var entry))
            {
                var episodeId = entry.Metadata?["episode_id"]?.ToString();
                if (episodeId != null && _episodeBoundaries.TryGetValue(episodeId, out var boundaries))
                {
                    boundaries.Remove(memoryId);
                }
            }
            await Task.CompletedTask;
        });
    }

    /// <inheritdoc/>
    public override async Task<MemoryStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            await Task.CompletedTask;
            return new MemoryStatistics
            {
                TotalMemories = _episodes.Count,
                EpisodicMemoryCount = _episodes.Count,
                TotalAccessCount = Interlocked.Read(ref _totalMemoriesRetrieved),
                ConsolidationCount = Interlocked.Read(ref _totalConsolidations),
                LastConsolidation = _lastConsolidation
            };
        });
    }

    private string GetOrCreateEpisode()
    {
        var timeout = int.Parse(GetConfig("EpisodeTimeoutMinutes") ?? "60");
        var latestEpisode = _episodeBoundaries.Keys.LastOrDefault();

        if (latestEpisode == null)
            return CreateNewEpisode();

        // Check if we should start a new episode based on timeout
        if (_episodeBoundaries.TryGetValue(latestEpisode, out var boundaries) && boundaries.Count > 0)
        {
            var lastMemoryId = boundaries.Last();
            if (_episodes.TryGetValue(lastMemoryId, out var lastMemory))
            {
                var timeSinceLastMemory = (DateTime.UtcNow - lastMemory.CreatedAt).TotalMinutes;
                if (timeSinceLastMemory > timeout)
                    return CreateNewEpisode();
            }
        }

        return latestEpisode;
    }

    private string CreateNewEpisode()
    {
        var episodeId = $"episode_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        _episodeBoundaries[episodeId] = new List<string>();
        return episodeId;
    }

    private float CalculateRelevance(string query, MemoryEntry memory)
    {
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var contentWords = memory.Content.ToLowerInvariant();
        var matchCount = queryWords.Count(word => contentWords.Contains(word));
        return queryWords.Length > 0 ? (float)matchCount / queryWords.Length : 0f;
    }
}

// =============================================================================
// 3. Redis-Based Working Memory
// =============================================================================

/// <summary>
/// Redis-based fast working memory with TTL and automatic promotion to long-term storage.
/// Provides high-speed session-scoped memory with automatic persistence.
/// </summary>
public sealed class RedisMemoryStrategy : LongTermMemoryStrategyBase
{
    private readonly ConcurrentDictionary<string, MemoryEntry> _cache = new();
    private readonly ConcurrentDictionary<string, MemoryEntry> _longTermStorage = new();
    private DateTime _lastConsolidation = DateTime.UtcNow;

    /// <inheritdoc/>
    public override string StrategyId => "memory-redis-working";

    /// <inheritdoc/>
    public override string StrategyName => "Redis Working Memory";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Redis",
        Description = "Redis-based high-speed working memory with TTL, automatic promotion to long-term storage, and session-scoped memory isolation.",
        Capabilities = IntelligenceCapabilities.MemoryStorage | IntelligenceCapabilities.MemoryRetrieval |
                      IntelligenceCapabilities.WorkingMemory | IntelligenceCapabilities.MemoryConsolidation,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "RedisConnectionString", Description = "Redis connection string", Required = true },
            new ConfigurationRequirement { Key = "DefaultTTLSeconds", Description = "Default TTL for working memory", Required = false, DefaultValue = "3600" },
            new ConfigurationRequirement { Key = "PromotionThreshold", Description = "Access count for promotion to long-term", Required = false, DefaultValue = "3" },
            new ConfigurationRequirement { Key = "SessionId", Description = "Session identifier for memory isolation", Required = false, DefaultValue = "default" }
        },
        CostTier = 2,
        LatencyTier = 1,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "redis", "working-memory", "cache", "fast", "session-scoped" }
    };

    /// <inheritdoc/>
    public override async Task<string> StoreMemoryAsync(string content, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var memoryId = Guid.NewGuid().ToString();
            var sessionId = GetConfig("SessionId") ?? "default";

            var entry = new MemoryEntry
            {
                Id = memoryId,
                Content = content,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                AccessCount = 0,
                ImportanceScore = 0.5f,
                Metadata = new Dictionary<string, object>(metadata ?? new Dictionary<string, object>())
                {
                    ["session_id"] = sessionId,
                    ["ttl_expires"] = DateTime.UtcNow.AddSeconds(int.Parse(GetConfig("DefaultTTLSeconds") ?? "3600"))
                }
            };

            _cache[memoryId] = entry;
            RecordMemoryStored();

            // TODO: Store in actual Redis when available
            await Task.CompletedTask;

            return memoryId;
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<RetrievedMemory>> RetrieveMemoriesAsync(string query, int topK = 10, float minRelevance = 0.0f, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var sessionId = GetConfig("SessionId") ?? "default";
            var now = DateTime.UtcNow;
            var promotionThreshold = int.Parse(GetConfig("PromotionThreshold") ?? "3");

            // Clean up expired entries
            var expired = _cache.Values
                .Where(m => m.Metadata?.TryGetValue("ttl_expires", out var exp) == true && (DateTime)exp < now)
                .Select(m => m.Id)
                .ToList();

            foreach (var id in expired)
            {
                _cache.TryRemove(id, out _);
            }

            // Search in both cache and long-term storage
            var allMemories = _cache.Values
                .Concat(_longTermStorage.Values)
                .Where(m => m.Metadata?.TryGetValue("session_id", out var sid) == true && sid.ToString() == sessionId)
                .ToList();

            var results = allMemories
                .Select(m =>
                {
                    m.AccessCount++;
                    m.LastAccessedAt = now;

                    // Promote to long-term if accessed frequently
                    if (m.AccessCount >= promotionThreshold && _cache.ContainsKey(m.Id))
                    {
                        _longTermStorage[m.Id] = m;
                        _cache.TryRemove(m.Id, out _);
                    }

                    var relevance = CalculateRelevance(query, m);
                    return new RetrievedMemory { Entry = m, RelevanceScore = relevance };
                })
                .Where(r => r.RelevanceScore >= minRelevance)
                .OrderByDescending(r => r.RelevanceScore)
                .Take(topK)
                .ToList();

            RecordMemoryRetrieved(results.Count);
            await Task.CompletedTask;
            return results;
        });
    }

    /// <inheritdoc/>
    public override async Task ConsolidateMemoriesAsync(CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var now = DateTime.UtcNow;

            // Remove expired cache entries
            var expired = _cache.Values
                .Where(m => m.Metadata?.TryGetValue("ttl_expires", out var exp) == true && (DateTime)exp < now)
                .Select(m => m.Id)
                .ToList();

            foreach (var id in expired)
            {
                _cache.TryRemove(id, out _);
            }

            // Clean old long-term entries (older than 90 days with low importance)
            var cutoff = DateTime.UtcNow.AddDays(-90);
            var oldEntries = _longTermStorage.Values
                .Where(m => m.CreatedAt < cutoff && m.ImportanceScore < 0.3f && m.AccessCount < 2)
                .Select(m => m.Id)
                .ToList();

            foreach (var id in oldEntries)
            {
                _longTermStorage.TryRemove(id, out _);
            }

            _lastConsolidation = DateTime.UtcNow;
            RecordConsolidation();
            await Task.CompletedTask;
        });
    }

    /// <inheritdoc/>
    public override async Task ForgetMemoryAsync(string memoryId, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            _cache.TryRemove(memoryId, out _);
            _longTermStorage.TryRemove(memoryId, out _);
            await Task.CompletedTask;
        });
    }

    /// <inheritdoc/>
    public override async Task<MemoryStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            await Task.CompletedTask;
            return new MemoryStatistics
            {
                TotalMemories = _cache.Count + _longTermStorage.Count,
                WorkingMemoryCount = _cache.Count,
                LongTermMemoryCount = _longTermStorage.Count,
                TotalAccessCount = Interlocked.Read(ref _totalMemoriesRetrieved),
                ConsolidationCount = Interlocked.Read(ref _totalConsolidations),
                LastConsolidation = _lastConsolidation
            };
        });
    }

    private float CalculateRelevance(string query, MemoryEntry memory)
    {
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var contentWords = memory.Content.ToLowerInvariant();
        var matchCount = queryWords.Count(word => contentWords.Contains(word));
        var relevance = queryWords.Length > 0 ? (float)matchCount / queryWords.Length : 0f;

        // Boost recently accessed memories
        var hoursSinceAccess = (DateTime.UtcNow - (memory.LastAccessedAt ?? memory.CreatedAt)).TotalHours;
        relevance += Math.Max(0, 0.3f - (float)(hoursSinceAccess / 24));

        return Math.Clamp(relevance, 0f, 1f);
    }
}

// =============================================================================
// 4. PgVector Semantic Memory
// =============================================================================

/// <summary>
/// PostgreSQL pgvector-based semantic memory with fact extraction and entity relationship tracking.
/// Provides semantic similarity-based retrieval using PostgreSQL with pgvector extension.
/// </summary>
public sealed class PgVectorMemoryStrategy : LongTermMemoryStrategyBase
{
    private readonly ConcurrentDictionary<string, MemoryEntry> _memories = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _entityRelationships = new();
    private DateTime _lastConsolidation = DateTime.UtcNow;

    /// <inheritdoc/>
    public override string StrategyId => "memory-pgvector-semantic";

    /// <inheritdoc/>
    public override string StrategyName => "PgVector Semantic Memory";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "PostgreSQL (pgvector)",
        Description = "PostgreSQL pgvector-based semantic memory with semantic similarity-based retrieval, fact extraction, and entity relationship tracking.",
        Capabilities = IntelligenceCapabilities.MemoryStorage | IntelligenceCapabilities.MemoryRetrieval |
                      IntelligenceCapabilities.SemanticMemory | IntelligenceCapabilities.MemoryConsolidation,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "ConnectionString", Description = "PostgreSQL connection string", Required = true },
            new ConfigurationRequirement { Key = "TableName", Description = "Table name for semantic memory", Required = false, DefaultValue = "semantic_memory" },
            new ConfigurationRequirement { Key = "VectorDimension", Description = "Embedding vector dimension", Required = false, DefaultValue = "1536" },
            new ConfigurationRequirement { Key = "SimilarityThreshold", Description = "Minimum similarity for retrieval", Required = false, DefaultValue = "0.7" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "postgresql", "pgvector", "semantic", "vector-db", "facts", "entities" }
    };

    /// <inheritdoc/>
    public override async Task<string> StoreMemoryAsync(string content, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var memoryId = Guid.NewGuid().ToString();

            // Extract entities from content (simplified)
            var entities = ExtractEntities(content);

            var entry = new MemoryEntry
            {
                Id = memoryId,
                Content = content,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                AccessCount = 0,
                ImportanceScore = CalculateImportance(content, entities),
                Metadata = new Dictionary<string, object>(metadata ?? new Dictionary<string, object>())
                {
                    ["entities"] = entities,
                    ["fact_type"] = InferFactType(content)
                }
            };

            _memories[memoryId] = entry;

            // Track entity relationships
            foreach (var entity in entities)
            {
                if (!_entityRelationships.ContainsKey(entity))
                    _entityRelationships[entity] = new HashSet<string>();
                _entityRelationships[entity].Add(memoryId);
            }

            RecordMemoryStored();

            // TODO: Store in actual PostgreSQL with pgvector when available
            await Task.CompletedTask;

            return memoryId;
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<RetrievedMemory>> RetrieveMemoriesAsync(string query, int topK = 10, float minRelevance = 0.0f, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var queryEntities = ExtractEntities(query);

            var results = _memories.Values
                .Select(m =>
                {
                    m.AccessCount++;
                    m.LastAccessedAt = DateTime.UtcNow;

                    var semanticScore = CalculateSemanticRelevance(query, m, queryEntities);
                    var entityScore = CalculateEntityOverlap(queryEntities, m);
                    var relevance = (semanticScore * 0.7f) + (entityScore * 0.3f);

                    return new RetrievedMemory { Entry = m, RelevanceScore = relevance };
                })
                .Where(r => r.RelevanceScore >= minRelevance)
                .OrderByDescending(r => r.RelevanceScore)
                .Take(topK)
                .ToList();

            RecordMemoryRetrieved(results.Count);
            await Task.CompletedTask;
            return results;
        });
    }

    /// <inheritdoc/>
    public override async Task ConsolidateMemoriesAsync(CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Merge similar facts (deduplication)
            var groups = _memories.Values
                .GroupBy(m => m.Metadata?["fact_type"]?.ToString() ?? "unknown")
                .ToList();

            foreach (var group in groups)
            {
                var facts = group.ToList();
                for (int i = 0; i < facts.Count; i++)
                {
                    for (int j = i + 1; j < facts.Count; j++)
                    {
                        if (AreSimilarFacts(facts[i], facts[j]))
                        {
                            // Merge into more important fact
                            if (facts[i].ImportanceScore > facts[j].ImportanceScore)
                            {
                                facts[i].AccessCount += facts[j].AccessCount;
                                _memories.TryRemove(facts[j].Id, out _);
                            }
                            else
                            {
                                facts[j].AccessCount += facts[i].AccessCount;
                                _memories.TryRemove(facts[i].Id, out _);
                            }
                        }
                    }
                }
            }

            _lastConsolidation = DateTime.UtcNow;
            RecordConsolidation();
            await Task.CompletedTask;
        });
    }

    /// <inheritdoc/>
    public override async Task ForgetMemoryAsync(string memoryId, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            if (_memories.TryRemove(memoryId, out var entry))
            {
                // Remove from entity relationships
                if (entry.Metadata?.TryGetValue("entities", out var entitiesObj) == true &&
                    entitiesObj is List<string> entities)
                {
                    foreach (var entity in entities)
                    {
                        if (_entityRelationships.TryGetValue(entity, out var memoryIds))
                        {
                            memoryIds.Remove(memoryId);
                        }
                    }
                }
            }
            await Task.CompletedTask;
        });
    }

    /// <inheritdoc/>
    public override async Task<MemoryStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            await Task.CompletedTask;
            return new MemoryStatistics
            {
                TotalMemories = _memories.Count,
                SemanticMemoryCount = _memories.Count,
                TotalAccessCount = Interlocked.Read(ref _totalMemoriesRetrieved),
                ConsolidationCount = Interlocked.Read(ref _totalConsolidations),
                LastConsolidation = _lastConsolidation
            };
        });
    }

    private List<string> ExtractEntities(string content)
    {
        // Simplified entity extraction (would use NLP in production)
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var entities = words
            .Where(w => w.Length > 3 && char.IsUpper(w[0]))
            .Distinct()
            .Take(10)
            .ToList();
        return entities;
    }

    private string InferFactType(string content)
    {
        if (content.Contains("is") || content.Contains("are"))
            return "definition";
        if (content.Contains("when") || content.Contains("date"))
            return "temporal";
        if (content.Contains("where") || content.Contains("location"))
            return "spatial";
        return "general";
    }

    private float CalculateImportance(string content, List<string> entities)
    {
        var importance = 0.5f;
        importance += Math.Min(entities.Count / 10f, 0.2f); // More entities = more important
        importance += Math.Min(content.Length / 500f, 0.2f); // Longer content = more important
        return Math.Clamp(importance, 0f, 1f);
    }

    private float CalculateSemanticRelevance(string query, MemoryEntry memory, List<string> queryEntities)
    {
        // Simplified semantic scoring (would use embeddings in production)
        var queryWords = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var contentWords = memory.Content.ToLowerInvariant();
        var matchCount = queryWords.Count(word => contentWords.Contains(word));
        return queryWords.Length > 0 ? (float)matchCount / queryWords.Length : 0f;
    }

    private float CalculateEntityOverlap(List<string> queryEntities, MemoryEntry memory)
    {
        if (memory.Metadata?.TryGetValue("entities", out var entitiesObj) != true ||
            entitiesObj is not List<string> memoryEntities)
            return 0f;

        var overlap = queryEntities.Intersect(memoryEntities, StringComparer.OrdinalIgnoreCase).Count();
        return queryEntities.Count > 0 ? (float)overlap / queryEntities.Count : 0f;
    }

    private bool AreSimilarFacts(MemoryEntry fact1, MemoryEntry fact2)
    {
        // Check content similarity
        var words1 = fact1.Content.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words2 = fact2.Content.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var overlap = words1.Intersect(words2).Count();
        var similarity = (float)overlap / Math.Max(words1.Length, words2.Length);
        return similarity > 0.8f;
    }
}

// =============================================================================
// 5. Hybrid Multi-Tier Memory
// =============================================================================

/// <summary>
/// Hybrid multi-tier memory combining working (Redis), episodic (Chroma), and semantic (PgVector).
/// Provides automatic memory routing based on content type with cross-tier consolidation.
/// </summary>
public sealed class HybridMemoryStrategy : LongTermMemoryStrategyBase
{
    private readonly RedisMemoryStrategy _workingMemory;
    private readonly ChromaMemoryStrategy _episodicMemory;
    private readonly PgVectorMemoryStrategy _semanticMemory;
    private DateTime _lastConsolidation = DateTime.UtcNow;

    public HybridMemoryStrategy()
    {
        _workingMemory = new RedisMemoryStrategy();
        _episodicMemory = new ChromaMemoryStrategy();
        _semanticMemory = new PgVectorMemoryStrategy();
    }

    /// <inheritdoc/>
    public override string StrategyId => "memory-hybrid";

    /// <inheritdoc/>
    public override string StrategyName => "Hybrid Multi-Tier Memory";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Hybrid (Redis + Chroma + PgVector)",
        Description = "Multi-tier memory hierarchy combining working memory (Redis), episodic memory (Chroma), and semantic memory (PgVector) with automatic routing and cross-tier consolidation.",
        Capabilities = IntelligenceCapabilities.MemoryStorage | IntelligenceCapabilities.MemoryRetrieval |
                      IntelligenceCapabilities.MemoryConsolidation | IntelligenceCapabilities.WorkingMemory |
                      IntelligenceCapabilities.EpisodicMemory | IntelligenceCapabilities.SemanticMemory |
                      IntelligenceCapabilities.HierarchicalMemory,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "RedisConnectionString", Description = "Redis connection string for working memory", Required = true },
            new ConfigurationRequirement { Key = "ChromaUrl", Description = "ChromaDB URL for episodic memory", Required = true },
            new ConfigurationRequirement { Key = "PostgresConnectionString", Description = "PostgreSQL connection string for semantic memory", Required = true },
            new ConfigurationRequirement { Key = "RoutingStrategy", Description = "Memory routing strategy (auto/manual)", Required = false, DefaultValue = "auto" }
        },
        CostTier = 3,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        SupportsOfflineMode = false,
        Tags = new[] { "hybrid", "multi-tier", "redis", "chroma", "pgvector", "hierarchical" }
    };

    /// <inheritdoc/>
    public override async Task<string> StoreMemoryAsync(string content, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var memoryType = DetermineMemoryType(content, metadata);
            string memoryId;

            metadata ??= new Dictionary<string, object>();
            metadata["memory_type"] = memoryType.ToString();

            switch (memoryType)
            {
                case MemoryType.Working:
                    memoryId = await _workingMemory.StoreMemoryAsync(content, metadata, ct);
                    break;
                case MemoryType.Episodic:
                    memoryId = await _episodicMemory.StoreMemoryAsync(content, metadata, ct);
                    break;
                case MemoryType.Semantic:
                    memoryId = await _semanticMemory.StoreMemoryAsync(content, metadata, ct);
                    break;
                default:
                    // Default to working memory
                    memoryId = await _workingMemory.StoreMemoryAsync(content, metadata, ct);
                    break;
            }

            RecordMemoryStored();
            return memoryId;
        });
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<RetrievedMemory>> RetrieveMemoriesAsync(string query, int topK = 10, float minRelevance = 0.0f, CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            // Search across all tiers in parallel
            var workingTask = _workingMemory.RetrieveMemoriesAsync(query, topK, minRelevance, ct);
            var episodicTask = _episodicMemory.RetrieveMemoriesAsync(query, topK, minRelevance, ct);
            var semanticTask = _semanticMemory.RetrieveMemoriesAsync(query, topK, minRelevance, ct);

            await Task.WhenAll(workingTask, episodicTask, semanticTask);

            // Combine results and re-rank
            var allResults = (await workingTask)
                .Concat(await episodicTask)
                .Concat(await semanticTask)
                .OrderByDescending(r => r.RelevanceScore)
                .Take(topK)
                .ToList();

            RecordMemoryRetrieved(allResults.Count);
            return allResults;
        });
    }

    /// <inheritdoc/>
    public override async Task ConsolidateMemoriesAsync(CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Consolidate each tier
            await Task.WhenAll(
                _workingMemory.ConsolidateMemoriesAsync(ct),
                _episodicMemory.ConsolidateMemoriesAsync(ct),
                _semanticMemory.ConsolidateMemoriesAsync(ct)
            );

            // Cross-tier consolidation: Promote important working memories to long-term tiers
            // This would involve analyzing working memory and deciding which items to promote
            // Implementation simplified for this example

            _lastConsolidation = DateTime.UtcNow;
            RecordConsolidation();
        });
    }

    /// <inheritdoc/>
    public override async Task ForgetMemoryAsync(string memoryId, CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            // Try forgetting from all tiers (one will succeed)
            await Task.WhenAll(
                _workingMemory.ForgetMemoryAsync(memoryId, ct),
                _episodicMemory.ForgetMemoryAsync(memoryId, ct),
                _semanticMemory.ForgetMemoryAsync(memoryId, ct)
            );
        });
    }

    /// <inheritdoc/>
    public override async Task<MemoryStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var workingStats = await _workingMemory.GetMemoryStatisticsAsync(ct);
            var episodicStats = await _episodicMemory.GetMemoryStatisticsAsync(ct);
            var semanticStats = await _semanticMemory.GetMemoryStatisticsAsync(ct);

            return new MemoryStatistics
            {
                TotalMemories = workingStats.TotalMemories + episodicStats.TotalMemories + semanticStats.TotalMemories,
                WorkingMemoryCount = workingStats.WorkingMemoryCount,
                EpisodicMemoryCount = episodicStats.EpisodicMemoryCount,
                SemanticMemoryCount = semanticStats.SemanticMemoryCount,
                TotalAccessCount = Interlocked.Read(ref _totalMemoriesRetrieved),
                ConsolidationCount = Interlocked.Read(ref _totalConsolidations),
                LastConsolidation = _lastConsolidation
            };
        });
    }

    private enum MemoryType
    {
        Working,
        Episodic,
        Semantic
    }

    private MemoryType DetermineMemoryType(string content, Dictionary<string, object>? metadata)
    {
        // Auto-routing based on content characteristics
        if (GetConfig("RoutingStrategy") == "manual" && metadata?.ContainsKey("memory_type") == true)
        {
            return Enum.Parse<MemoryType>(metadata["memory_type"].ToString()!);
        }

        // Heuristics for auto-routing
        if (content.Length < 100)
            return MemoryType.Working; // Short content -> working memory

        if (metadata?.ContainsKey("episode_id") == true || metadata?.ContainsKey("timestamp") == true)
            return MemoryType.Episodic; // Temporal/contextual -> episodic

        if (content.Contains("is") || content.Contains("definition") || content.Contains("fact"))
            return MemoryType.Semantic; // Factual content -> semantic

        return MemoryType.Working; // Default to working memory
    }
}
