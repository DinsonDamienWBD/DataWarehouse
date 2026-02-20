using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory;

#region Tiered Memory Configuration

/// <summary>
/// Overall configuration for the tiered memory system.
/// </summary>
public sealed record TieredMemoryConfig
{
    /// <summary>Configuration for each tier.</summary>
    public Dictionary<MemoryTier, TierConfig> Tiers { get; init; } = new()
    {
        [MemoryTier.Immediate] = new TierConfig
        {
            Tier = MemoryTier.Immediate,
            Persistence = MemoryPersistence.Volatile,
            MaxCapacityBytes = 10 * 1024 * 1024, // 10MB
            TTL = TimeSpan.FromMinutes(30)
        },
        [MemoryTier.Working] = new TierConfig
        {
            Tier = MemoryTier.Working,
            Persistence = MemoryPersistence.Volatile,
            MaxCapacityBytes = 50 * 1024 * 1024, // 50MB
            TTL = TimeSpan.FromHours(24)
        },
        [MemoryTier.ShortTerm] = new TierConfig
        {
            Tier = MemoryTier.ShortTerm,
            Persistence = MemoryPersistence.Hybrid,
            MaxCapacityBytes = 200 * 1024 * 1024, // 200MB
            TTL = TimeSpan.FromDays(30)
        },
        [MemoryTier.LongTerm] = new TierConfig
        {
            Tier = MemoryTier.LongTerm,
            Persistence = MemoryPersistence.Persistent,
            MaxCapacityBytes = 1024 * 1024 * 1024, // 1GB
            TTL = null // No expiration
        }
    };

    /// <summary>Default scope for memories without explicit scope.</summary>
    public string DefaultScope { get; init; } = "global";

    /// <summary>Interval between automatic consolidation runs.</summary>
    public TimeSpan ConsolidationInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether to enable automatic tier management.</summary>
    public bool AutoTierManagement { get; init; } = true;
}

#endregion

#region In-Memory Tiered System Implementation

/// <summary>
/// In-memory implementation of a tiered memory system that integrates with the existing
/// memory infrastructure (VolatileMemoryStore, PersistentMemoryStore, etc.)
/// </summary>
public sealed class InMemoryTieredMemorySystem
{
    private readonly BoundedDictionary<MemoryTier, BoundedDictionary<string, TieredMemoryEntry>> _tiers = new BoundedDictionary<MemoryTier, BoundedDictionary<string, TieredMemoryEntry>>(1000);
    private readonly BoundedDictionary<MemoryTier, TierStatistics> _statistics = new BoundedDictionary<MemoryTier, TierStatistics>(1000);
    private readonly TieredMemoryConfig _config;
    private readonly BoundedDictionary<string, MemoryTier> _entryTierMap = new BoundedDictionary<string, MemoryTier>(1000);

    public InMemoryTieredMemorySystem(TieredMemoryConfig? config = null)
    {
        _config = config ?? new TieredMemoryConfig();
        foreach (MemoryTier tier in Enum.GetValues<MemoryTier>())
        {
            _tiers[tier] = new BoundedDictionary<string, TieredMemoryEntry>(1000);
            _statistics[tier] = new TierStatistics
            {
                Tier = tier,
                MaxCapacityBytes = _config.Tiers.TryGetValue(tier, out var tc) ? tc.MaxCapacityBytes : 100 * 1024 * 1024
            };
        }
    }

    public Task<string> StoreAsync(string content, MemoryTier tier, string scope, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        var entry = new TieredMemoryEntry
        {
            Id = id,
            Content = content,
            Tier = tier,
            Scope = scope,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            Metadata = metadata
        };

        _tiers[tier][id] = entry;
        _entryTierMap[id] = tier;
        UpdateStatistics(tier);
        return Task.FromResult(id);
    }

    public Task<IEnumerable<RetrievedMemory>> RecallAsync(string query, MemoryTier minTier, string scope, int topK = 10, CancellationToken ct = default)
    {
        var queryLower = query.ToLowerInvariant();
        var queryWords = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var results = new List<RetrievedMemory>();

        foreach (var tier in Enum.GetValues<MemoryTier>().Where(t => t >= minTier))
        {
            if (!_tiers.TryGetValue(tier, out var entries)) continue;

            foreach (var entry in entries.Values.Where(e => e.Scope == scope || scope == "global"))
            {
                var contentLower = entry.Content.ToLowerInvariant();
                var matchCount = queryWords.Count(w => contentLower.Contains(w));
                var relevance = queryWords.Length > 0 ? (float)matchCount / queryWords.Length : 0f;

                // Boost by tier (higher tiers = more important)
                relevance += (int)tier * 0.1f;

                entry.AccessCount++;
                entry.LastAccessedAt = DateTime.UtcNow;

                results.Add(new RetrievedMemory
                {
                    Entry = new MemoryEntry
                    {
                        Id = entry.Id,
                        Content = entry.Content,
                        CreatedAt = entry.CreatedAt,
                        LastAccessedAt = entry.LastAccessedAt,
                        AccessCount = entry.AccessCount,
                        ImportanceScore = entry.ImportanceScore,
                        Metadata = entry.Metadata
                    },
                    RelevanceScore = Math.Clamp(relevance, 0f, 1f)
                });
            }
        }

        return Task.FromResult<IEnumerable<RetrievedMemory>>(
            results.OrderByDescending(r => r.RelevanceScore).Take(topK));
    }

    public Task PromoteAsync(string memoryId, MemoryTier targetTier, CancellationToken ct = default)
    {
        if (!_entryTierMap.TryGetValue(memoryId, out var currentTier))
            return Task.CompletedTask;

        if (currentTier >= targetTier)
            return Task.CompletedTask;

        if (_tiers[currentTier].TryRemove(memoryId, out var entry))
        {
            var promoted = entry with { Tier = targetTier };
            _tiers[targetTier][memoryId] = promoted;
            _entryTierMap[memoryId] = targetTier;
            UpdateStatistics(currentTier);
            UpdateStatistics(targetTier);
        }
        return Task.CompletedTask;
    }

    public Task DemoteAsync(string memoryId, MemoryTier targetTier, CancellationToken ct = default)
    {
        if (!_entryTierMap.TryGetValue(memoryId, out var currentTier))
            return Task.CompletedTask;

        if (currentTier <= targetTier)
            return Task.CompletedTask;

        if (_tiers[currentTier].TryRemove(memoryId, out var entry))
        {
            var demoted = entry with { Tier = targetTier };
            _tiers[targetTier][memoryId] = demoted;
            _entryTierMap[memoryId] = targetTier;
            UpdateStatistics(currentTier);
            UpdateStatistics(targetTier);
        }
        return Task.CompletedTask;
    }

    public Task<TierStatistics> GetTierStatisticsAsync(MemoryTier tier, CancellationToken ct = default)
    {
        UpdateStatistics(tier);
        return Task.FromResult(_statistics[tier]);
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        // In-memory implementation - nothing to flush
        return Task.CompletedTask;
    }

    public Task RestoreAsync(CancellationToken ct = default)
    {
        // In-memory implementation - nothing to restore
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string memoryId, CancellationToken ct = default)
    {
        if (_entryTierMap.TryRemove(memoryId, out var tier))
        {
            _tiers[tier].TryRemove(memoryId, out _);
            UpdateStatistics(tier);
        }
        return Task.CompletedTask;
    }

    private void UpdateStatistics(MemoryTier tier)
    {
        if (!_tiers.TryGetValue(tier, out var entries)) return;

        var entryList = entries.Values.ToList();
        var totalBytes = entryList.Sum(e => e.EstimatedSizeBytes);

        _statistics[tier] = new TierStatistics
        {
            Tier = tier,
            EntryCount = entryList.Count,
            BytesUsed = totalBytes,
            MaxCapacityBytes = _config.Tiers.TryGetValue(tier, out var tc) ? tc.MaxCapacityBytes : 100 * 1024 * 1024,
            AverageAccessCount = entryList.Any() ? entryList.Average(e => e.AccessCount) : 0,
            AverageImportanceScore = entryList.Any() ? entryList.Average(e => e.ImportanceScore) : 0
        };
    }
}

/// <summary>
/// Statistics for a single memory tier.
/// </summary>
public sealed record TierStatistics
{
    /// <summary>The tier these statistics apply to.</summary>
    public MemoryTier Tier { get; init; }

    /// <summary>Number of entries in this tier.</summary>
    public long EntryCount { get; init; }

    /// <summary>Total bytes used by this tier.</summary>
    public long BytesUsed { get; init; }

    /// <summary>Maximum capacity in bytes.</summary>
    public long MaxCapacityBytes { get; init; }

    /// <summary>Utilization percentage (0-100).</summary>
    public double UtilizationPercent => MaxCapacityBytes > 0 ? (double)BytesUsed / MaxCapacityBytes * 100 : 0;

    /// <summary>Number of promotions from this tier.</summary>
    public long PromotionCount { get; init; }

    /// <summary>Number of demotions to this tier.</summary>
    public long DemotionCount { get; init; }

    /// <summary>Average access count for entries.</summary>
    public double AverageAccessCount { get; init; }

    /// <summary>Average importance score for entries.</summary>
    public double AverageImportanceScore { get; init; }
}

#endregion

#region Tiered Memory Strategy

/// <summary>
/// Intelligence strategy for hierarchical tiered memory.
/// Provides 4-tier memory with volatile/persistent options.
/// Integrates with the existing memory infrastructure.
/// </summary>
public sealed class TieredMemoryStrategy : LongTermMemoryStrategyBase, ITierAwareMemoryStrategy
{
    private readonly InMemoryTieredMemorySystem _memorySystem;
    private EvolvingContextManager? _evolutionManager;
    private AIContextRegenerator? _regenerator;

    /// <inheritdoc/>
    public override string StrategyId => "ltm-tiered-hierarchical";

    /// <inheritdoc/>
    public override string StrategyName => "Tiered Hierarchical Memory";

    /// <summary>
    /// Gets the default tier for this strategy.
    /// </summary>
    public MemoryTier DefaultTier => MemoryTier.Working;

    /// <summary>
    /// Gets the supported tiers for this strategy.
    /// </summary>
    public IReadOnlyList<MemoryTier> SupportedTiers => new[] { MemoryTier.Immediate, MemoryTier.Working, MemoryTier.ShortTerm, MemoryTier.LongTerm };

    /// <summary>
    /// Gets the current tier configuration.
    /// </summary>
    public new TieredMemoryConfig Configuration { get; private set; }

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Tiered Hierarchical Memory",
        Description = "4-tier hierarchical memory system with volatile/persistent options, automatic tier management, context evolution, and data regeneration capabilities.",
        Capabilities = IntelligenceCapabilities.MemoryStorage | IntelligenceCapabilities.MemoryRetrieval |
                      IntelligenceCapabilities.MemoryConsolidation | IntelligenceCapabilities.HierarchicalMemory |
                      IntelligenceCapabilities.WorkingMemory | IntelligenceCapabilities.EpisodicMemory |
                      IntelligenceCapabilities.SemanticMemory | IntelligenceCapabilities.MemoryDecay,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "StoragePath", Description = "Path to persist memory data", Required = false, DefaultValue = "./memory-data" },
            new ConfigurationRequirement { Key = "DefaultScope", Description = "Default scope for memories", Required = false, DefaultValue = "global" },
            new ConfigurationRequirement { Key = "AutoTierManagement", Description = "Enable automatic tier management", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "ConsolidationIntervalMinutes", Description = "Interval between consolidations", Required = false, DefaultValue = "15" }
        },
        CostTier = 2,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "tiered", "hierarchical", "memory", "persistent", "volatile", "evolution", "regeneration" }
    };

    /// <summary>
    /// Initializes the tiered memory strategy with default configuration.
    /// </summary>
    public TieredMemoryStrategy()
    {
        Configuration = new TieredMemoryConfig();
        _memorySystem = new InMemoryTieredMemorySystem(Configuration);
        _regenerator = new AIContextRegenerator();
    }

    #region ITierAwareMemoryStrategy Implementation

    /// <inheritdoc/>
    public async Task<string> StoreAtTierAsync(string content, MemoryTier tier, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        return await StoreWithTierAsync(content, tier, Configuration.DefaultScope, metadata, ct);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<RetrievedMemory>> RetrieveFromTierAsync(string query, MemoryTier tier, int topK = 10, float minRelevance = 0.0f, CancellationToken ct = default)
    {
        var results = await RecallWithTierAsync(query, tier, Configuration.DefaultScope, topK, ct);
        return results.Where(r => r.RelevanceScore >= minRelevance);
    }

    /// <inheritdoc/>
    public async Task MoveBetweenTiersAsync(string memoryId, MemoryTier targetTier, CancellationToken ct = default)
    {
        await _memorySystem.PromoteAsync(memoryId, targetTier, ct);
    }

    #endregion

    #region Tier-Aware Operations

    /// <summary>
    /// Stores memory with explicit tier and scope.
    /// </summary>
    public async Task<string> StoreWithTierAsync(
        string content,
        MemoryTier tier,
        string scope,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var memoryId = await _memorySystem.StoreAsync(content, tier, scope, metadata, ct);
            RecordMemoryStored();
            return memoryId;
        });
    }

    /// <summary>
    /// Recalls memories with tier filtering.
    /// </summary>
    public async Task<IEnumerable<RetrievedMemory>> RecallWithTierAsync(
        string query,
        MemoryTier minTier,
        string scope,
        int topK = 10,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var results = await _memorySystem.RecallAsync(query, minTier, scope, topK, ct);
            var resultList = results.ToList();
            RecordMemoryRetrieved(resultList.Count);
            return resultList;
        });
    }

    /// <summary>
    /// Configures a specific tier.
    /// </summary>
    public async Task ConfigureTierAsync(
        MemoryTier tier,
        bool enabled,
        MemoryPersistence persistence,
        long maxCapacityBytes,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            var tierConfig = new TierConfig
            {
                Tier = tier,
                Enabled = enabled,
                Persistence = persistence,
                MaxCapacityBytes = maxCapacityBytes,
                TTL = ttl
            };

            var newTiers = new Dictionary<MemoryTier, TierConfig>(Configuration.Tiers)
            {
                [tier] = tierConfig
            };

            Configuration = Configuration with { Tiers = newTiers };
            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Gets evolution metrics for context management.
    /// </summary>
    public ContextEvolutionMetrics GetEvolutionMetrics()
    {
        return _evolutionManager?.GetEvolutionMetrics() ?? new ContextEvolutionMetrics();
    }

    /// <summary>
    /// Regenerates data from context using the AI context regenerator.
    /// </summary>
    public async Task<RegenerationResult> RegenerateDataAsync(
        string contextEntryId,
        string expectedFormat,
        CancellationToken ct = default)
    {
        if (_regenerator == null)
        {
            return new RegenerationResult
            {
                Success = false,
                Warnings = new List<string> { "Regenerator not configured" }
            };
        }

        return await ExecuteWithTrackingAsync(async () =>
        {
            // Get the context from memory
            var results = await _memorySystem.RecallAsync(contextEntryId, MemoryTier.Immediate, Configuration.DefaultScope, 1, ct);
            var entry = results.FirstOrDefault();

            if (entry == null)
            {
                return new RegenerationResult
                {
                    Success = false,
                    Warnings = new List<string> { $"Context entry {contextEntryId} not found" }
                };
            }

            var contextBytes = System.Text.Encoding.UTF8.GetBytes(entry.Entry.Content);
            return await _regenerator.RegenerateAsync(contextBytes, expectedFormat, ct);
        });
    }

    /// <summary>
    /// Gets statistics for a specific tier.
    /// </summary>
    public async Task<TierStatistics> GetTierStatisticsAsync(MemoryTier tier, CancellationToken ct = default)
    {
        return await _memorySystem.GetTierStatisticsAsync(tier, ct);
    }

    /// <summary>
    /// Promotes a memory to a higher tier.
    /// </summary>
    public async Task PromoteMemoryAsync(string memoryId, MemoryTier targetTier, CancellationToken ct = default)
    {
        await _memorySystem.PromoteAsync(memoryId, targetTier, ct);
    }

    /// <summary>
    /// Demotes a memory to a lower tier.
    /// </summary>
    public async Task DemoteMemoryAsync(string memoryId, MemoryTier targetTier, CancellationToken ct = default)
    {
        await _memorySystem.DemoteAsync(memoryId, targetTier, ct);
    }

    /// <summary>
    /// Flushes volatile memory to persistent storage.
    /// </summary>
    public async Task FlushToPersistentAsync(CancellationToken ct = default)
    {
        await _memorySystem.FlushAsync(ct);
    }

    /// <summary>
    /// Restores memory from persistent storage.
    /// </summary>
    public async Task RestoreFromPersistentAsync(CancellationToken ct = default)
    {
        await _memorySystem.RestoreAsync(ct);
    }

    /// <summary>
    /// Refines context by compressing older entries.
    /// </summary>
    public async Task RefineContextAsync(string? scope = null, CancellationToken ct = default)
    {
        if (_evolutionManager != null)
        {
            await _evolutionManager.RefineContextAsync(scope ?? Configuration.DefaultScope, ct);
        }
    }

    #endregion

    #region Base Method Overrides

    /// <inheritdoc/>
    public override async Task<string> StoreMemoryAsync(string content, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
    {
        // Default to Working tier for base method
        return await StoreWithTierAsync(content, MemoryTier.Working, Configuration.DefaultScope, metadata, ct);
    }

    /// <inheritdoc/>
    public override async Task<IEnumerable<RetrievedMemory>> RetrieveMemoriesAsync(string query, int topK = 10, float minRelevance = 0.0f, CancellationToken ct = default)
    {
        // Search from Working tier and above
        var results = await RecallWithTierAsync(query, MemoryTier.Working, Configuration.DefaultScope, topK, ct);
        return results.Where(r => r.RelevanceScore >= minRelevance);
    }

    /// <inheritdoc/>
    public override async Task ConsolidateMemoriesAsync(CancellationToken ct = default)
    {
        await ExecuteWithTrackingAsync(async () =>
        {
            if (_evolutionManager != null)
            {
                await _evolutionManager.ConsolidateAsync(MemoryTier.Working, ct);
            }
            RecordConsolidation();
        });
    }

    /// <inheritdoc/>
    public override async Task ForgetMemoryAsync(string memoryId, CancellationToken ct = default)
    {
        await _memorySystem.DeleteAsync(memoryId, ct);
    }

    /// <inheritdoc/>
    public override async Task<MemoryStatistics> GetMemoryStatisticsAsync(CancellationToken ct = default)
    {
        var immediateStats = await GetTierStatisticsAsync(MemoryTier.Immediate, ct);
        var workingStats = await GetTierStatisticsAsync(MemoryTier.Working, ct);
        var shortTermStats = await GetTierStatisticsAsync(MemoryTier.ShortTerm, ct);
        var longTermStats = await GetTierStatisticsAsync(MemoryTier.LongTerm, ct);

        return new MemoryStatistics
        {
            TotalMemories = immediateStats.EntryCount + workingStats.EntryCount + shortTermStats.EntryCount + longTermStats.EntryCount,
            WorkingMemoryCount = immediateStats.EntryCount + workingStats.EntryCount,
            ShortTermMemoryCount = shortTermStats.EntryCount,
            LongTermMemoryCount = longTermStats.EntryCount,
            TotalAccessCount = Interlocked.Read(ref _totalMemoriesRetrieved),
            ConsolidationCount = Interlocked.Read(ref _totalConsolidations),
            MemorySizeBytes = immediateStats.BytesUsed + workingStats.BytesUsed + shortTermStats.BytesUsed + longTermStats.BytesUsed
        };
    }

    #endregion
}

#endregion
