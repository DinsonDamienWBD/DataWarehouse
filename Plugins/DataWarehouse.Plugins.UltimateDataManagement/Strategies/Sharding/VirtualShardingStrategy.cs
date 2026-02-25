using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Virtual shard information.
/// </summary>
public sealed class VirtualShard
{
    /// <summary>
    /// Virtual shard identifier.
    /// </summary>
    public required string VirtualShardId { get; init; }

    /// <summary>
    /// Physical shard this virtual shard maps to.
    /// </summary>
    public required string PhysicalShardId { get; set; }

    /// <summary>
    /// Alias names for this virtual shard.
    /// </summary>
    public HashSet<string> Aliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the virtual shard was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the mapping was last updated.
    /// </summary>
    public DateTime LastMappingChange { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Version number for optimistic concurrency.
    /// </summary>
    public long Version { get; set; } = 1;

    /// <summary>
    /// Whether this virtual shard is active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Migration plan for moving virtual shards.
/// </summary>
public sealed class VirtualShardMigrationPlan
{
    /// <summary>
    /// Virtual shard to migrate.
    /// </summary>
    public required string VirtualShardId { get; init; }

    /// <summary>
    /// Source physical shard.
    /// </summary>
    public required string SourcePhysicalShardId { get; init; }

    /// <summary>
    /// Target physical shard.
    /// </summary>
    public required string TargetPhysicalShardId { get; init; }

    /// <summary>
    /// Whether to copy data (true) or just remap (false).
    /// </summary>
    public bool CopyData { get; init; } = true;

    /// <summary>
    /// Whether to verify data after migration.
    /// </summary>
    public bool VerifyAfterMigration { get; init; } = true;

    /// <summary>
    /// Estimated migration time.
    /// </summary>
    public TimeSpan? EstimatedDuration { get; init; }
}

/// <summary>
/// Virtual shard mapping strategy with logical to physical shard mapping.
/// </summary>
/// <remarks>
/// Features:
/// - Logical to physical shard mapping
/// - Easy physical shard migration without key changes
/// - Shard aliasing for backward compatibility
/// - Version tracking for concurrency control
/// - Thread-safe mapping operations
/// </remarks>
public sealed class VirtualShardingStrategy : ShardingStrategyBase
{
    private readonly BoundedDictionary<string, VirtualShard> _virtualShards = new BoundedDictionary<string, VirtualShard>(1000);
    private readonly BoundedDictionary<string, string> _aliasToVirtual = new BoundedDictionary<string, string>(1000);
    private readonly BoundedDictionary<string, string> _cache = new BoundedDictionary<string, string>(1000);
    private readonly ReaderWriterLockSlim _mappingLock = new();
    private readonly int _virtualShardsCount;
    private readonly int _cacheMaxSize;

    /// <summary>
    /// Initializes a new VirtualShardingStrategy with default settings.
    /// </summary>
    public VirtualShardingStrategy() : this(64, 100000) { }

    /// <summary>
    /// Initializes a new VirtualShardingStrategy with specified settings.
    /// </summary>
    /// <param name="virtualShardCount">Number of virtual shards to create.</param>
    /// <param name="cacheMaxSize">Maximum cache size.</param>
    public VirtualShardingStrategy(int virtualShardCount, int cacheMaxSize = 100000)
    {
        if (virtualShardCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(virtualShardCount), "Must be positive.");

        _virtualShardsCount = virtualShardCount;
        _cacheMaxSize = cacheMaxSize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.virtual";

    /// <inheritdoc/>
    public override string DisplayName => "Virtual Shard Mapping";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = false,
        MaxThroughput = 350_000,
        TypicalLatencyMs = 0.02
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Virtual sharding strategy that provides a logical to physical shard mapping layer. " +
        "Enables easy shard migration and aliasing without changing application keys. " +
        "Best for systems requiring flexible shard topology changes.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "virtual", "mapping", "logical", "migration", "alias"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Create physical shards
        var physicalShardCount = Math.Max(4, _virtualShardsCount / 16);
        for (int i = 0; i < physicalShardCount; i++)
        {
            var shardId = $"physical-{i:D4}";
            ShardRegistry[shardId] = new ShardInfo(
                shardId,
                $"node-{i % 4}/db-virtual",
                ShardStatus.Online,
                0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
        }

        // Create virtual shards mapped to physical shards
        var physicalIds = ShardRegistry.Keys.ToList();
        for (int i = 0; i < _virtualShardsCount; i++)
        {
            var virtualId = $"virtual-{i:D4}";
            var physicalId = physicalIds[i % physicalIds.Count];

            _virtualShards[virtualId] = new VirtualShard
            {
                VirtualShardId = virtualId,
                PhysicalShardId = physicalId,
                CreatedAt = DateTime.UtcNow
            };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct)
    {
        // Check cache first
        if (_cache.TryGetValue(key, out var cachedPhysicalId))
        {
            if (ShardRegistry.TryGetValue(cachedPhysicalId, out var cachedShard) &&
                cachedShard.Status == ShardStatus.Online)
            {
                return Task.FromResult(cachedShard);
            }
            _cache.TryRemove(key, out _);
        }

        // Determine virtual shard
        var virtualShardId = GetVirtualShardId(key);
        var physicalShardId = ResolvePhysicalShard(virtualShardId);

        if (!ShardRegistry.TryGetValue(physicalShardId, out var shard))
        {
            throw new InvalidOperationException($"Physical shard '{physicalShardId}' not found.");
        }

        // Cache the result
        CacheKeyMapping(key, physicalShardId);

        return Task.FromResult(shard);
    }

    /// <inheritdoc/>
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct)
    {
        if (!options.AllowDataMovement)
        {
            return true;
        }

        var physicalShards = GetOnlineShards();
        if (physicalShards.Count < 2)
        {
            return true;
        }

        // Count virtual shards per physical shard
        var virtualPerPhysical = _virtualShards.Values
            .GroupBy(v => v.PhysicalShardId)
            .ToDictionary(g => g.Key, g => g.Count());

        var avgVirtual = _virtualShards.Count / (double)physicalShards.Count;
        var maxMoves = (int)(_virtualShards.Count * options.MaxDataMovementPercent / 100);
        var moves = 0;

        var overloaded = virtualPerPhysical
            .Where(kvp => kvp.Value > avgVirtual * 1.2)
            .OrderByDescending(kvp => kvp.Value)
            .ToList();

        var underloaded = physicalShards
            .Where(s => !virtualPerPhysical.ContainsKey(s.ShardId) ||
                       virtualPerPhysical[s.ShardId] < avgVirtual * 0.8)
            .ToList();

        foreach (var (sourcePhysical, count) in overloaded)
        {
            if (moves >= maxMoves) break;

            var virtualToMove = _virtualShards.Values
                .Where(v => v.PhysicalShardId == sourcePhysical && v.IsActive)
                .Take((int)(count - avgVirtual))
                .ToList();

            foreach (var virtualShard in virtualToMove)
            {
                if (moves >= maxMoves) break;
                ct.ThrowIfCancellationRequested();

                var targetShard = underloaded.FirstOrDefault();
                if (targetShard == null) break;

                await RemapVirtualShardAsync(virtualShard.VirtualShardId, targetShard.ShardId, ct);
                moves++;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the virtual shard for a key.
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <returns>The virtual shard ID.</returns>
    public string GetVirtualShardId(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var hash = ComputeHash(key);
        var index = hash % (uint)_virtualShardsCount;
        return $"virtual-{index:D4}";
    }

    /// <summary>
    /// Gets the virtual shard information.
    /// </summary>
    /// <param name="virtualShardId">The virtual shard ID.</param>
    /// <returns>Virtual shard info or null if not found.</returns>
    public VirtualShard? GetVirtualShard(string virtualShardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualShardId);

        // Check alias first
        if (_aliasToVirtual.TryGetValue(virtualShardId, out var resolvedId))
        {
            virtualShardId = resolvedId;
        }

        return _virtualShards.TryGetValue(virtualShardId, out var shard) ? shard : null;
    }

    /// <summary>
    /// Gets all virtual shards.
    /// </summary>
    /// <param name="includeInactive">Whether to include inactive shards.</param>
    /// <returns>All virtual shards.</returns>
    public IReadOnlyList<VirtualShard> GetAllVirtualShards(bool includeInactive = false)
    {
        return _virtualShards.Values
            .Where(v => includeInactive || v.IsActive)
            .OrderBy(v => v.VirtualShardId)
            .ToList();
    }

    /// <summary>
    /// Gets virtual shards mapped to a physical shard.
    /// </summary>
    /// <param name="physicalShardId">The physical shard ID.</param>
    /// <returns>Virtual shards on the physical shard.</returns>
    public IReadOnlyList<VirtualShard> GetVirtualShardsOnPhysical(string physicalShardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalShardId);

        return _virtualShards.Values
            .Where(v => v.PhysicalShardId == physicalShardId && v.IsActive)
            .ToList();
    }

    /// <summary>
    /// Remaps a virtual shard to a different physical shard.
    /// </summary>
    /// <param name="virtualShardId">The virtual shard to remap.</param>
    /// <param name="newPhysicalShardId">The new physical shard.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if remap was successful.</returns>
    public Task<bool> RemapVirtualShardAsync(
        string virtualShardId,
        string newPhysicalShardId,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualShardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPhysicalShardId);

        if (!ShardRegistry.ContainsKey(newPhysicalShardId))
        {
            throw new InvalidOperationException($"Physical shard '{newPhysicalShardId}' does not exist.");
        }

        _mappingLock.EnterWriteLock();
        try
        {
            if (!_virtualShards.TryGetValue(virtualShardId, out var virtualShard))
            {
                return Task.FromResult(false);
            }

            var oldPhysical = virtualShard.PhysicalShardId;
            virtualShard.PhysicalShardId = newPhysicalShardId;
            virtualShard.LastMappingChange = DateTime.UtcNow;
            virtualShard.Version++;

            // Update shard metrics
            if (ShardRegistry.TryGetValue(oldPhysical, out var oldShard))
            {
                IncrementShardMetrics(oldPhysical, -1, 0);
            }
            IncrementShardMetrics(newPhysicalShardId, 1, 0);

            // Clear cache entries that might be affected
            ClearCacheForVirtualShard(virtualShardId);

            return Task.FromResult(true);
        }
        finally
        {
            _mappingLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Creates a migration plan for a virtual shard.
    /// </summary>
    /// <param name="virtualShardId">The virtual shard to migrate.</param>
    /// <param name="targetPhysicalShardId">The target physical shard.</param>
    /// <returns>Migration plan.</returns>
    public VirtualShardMigrationPlan CreateMigrationPlan(
        string virtualShardId,
        string targetPhysicalShardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualShardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPhysicalShardId);

        if (!_virtualShards.TryGetValue(virtualShardId, out var virtualShard))
        {
            throw new InvalidOperationException($"Virtual shard '{virtualShardId}' not found.");
        }

        if (!ShardRegistry.ContainsKey(targetPhysicalShardId))
        {
            throw new InvalidOperationException($"Physical shard '{targetPhysicalShardId}' does not exist.");
        }

        return new VirtualShardMigrationPlan
        {
            VirtualShardId = virtualShardId,
            SourcePhysicalShardId = virtualShard.PhysicalShardId,
            TargetPhysicalShardId = targetPhysicalShardId,
            CopyData = true,
            VerifyAfterMigration = true,
            EstimatedDuration = TimeSpan.FromMinutes(5) // Estimate based on typical migration
        };
    }

    /// <summary>
    /// Adds an alias for a virtual shard.
    /// </summary>
    /// <param name="alias">The alias name.</param>
    /// <param name="virtualShardId">The virtual shard to alias.</param>
    public void AddAlias(string alias, string virtualShardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualShardId);

        if (!_virtualShards.TryGetValue(virtualShardId, out var virtualShard))
        {
            throw new InvalidOperationException($"Virtual shard '{virtualShardId}' not found.");
        }

        _aliasToVirtual[alias] = virtualShardId;
        virtualShard.Aliases.Add(alias);
    }

    /// <summary>
    /// Removes an alias.
    /// </summary>
    /// <param name="alias">The alias to remove.</param>
    /// <returns>True if alias was removed.</returns>
    public bool RemoveAlias(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        if (_aliasToVirtual.TryRemove(alias, out var virtualShardId))
        {
            if (_virtualShards.TryGetValue(virtualShardId, out var virtualShard))
            {
                virtualShard.Aliases.Remove(alias);
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets all aliases for a virtual shard.
    /// </summary>
    /// <param name="virtualShardId">The virtual shard ID.</param>
    /// <returns>Aliases for the shard.</returns>
    public IReadOnlySet<string> GetAliases(string virtualShardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualShardId);

        if (_virtualShards.TryGetValue(virtualShardId, out var virtualShard))
        {
            return virtualShard.Aliases;
        }

        return new HashSet<string>();
    }

    /// <summary>
    /// Deactivates a virtual shard.
    /// </summary>
    /// <param name="virtualShardId">The virtual shard to deactivate.</param>
    /// <returns>True if deactivated.</returns>
    public bool DeactivateVirtualShard(string virtualShardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualShardId);

        if (_virtualShards.TryGetValue(virtualShardId, out var virtualShard))
        {
            virtualShard.IsActive = false;
            virtualShard.Version++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reactivates a virtual shard.
    /// </summary>
    /// <param name="virtualShardId">The virtual shard to reactivate.</param>
    /// <returns>True if reactivated.</returns>
    public bool ReactivateVirtualShard(string virtualShardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualShardId);

        if (_virtualShards.TryGetValue(virtualShardId, out var virtualShard))
        {
            virtualShard.IsActive = true;
            virtualShard.Version++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the mapping statistics.
    /// </summary>
    /// <returns>Mapping statistics.</returns>
    public VirtualMappingStats GetMappingStats()
    {
        var virtualPerPhysical = _virtualShards.Values
            .Where(v => v.IsActive)
            .GroupBy(v => v.PhysicalShardId)
            .ToDictionary(g => g.Key, g => g.Count());

        return new VirtualMappingStats
        {
            TotalVirtualShards = _virtualShards.Count,
            ActiveVirtualShards = _virtualShards.Values.Count(v => v.IsActive),
            TotalAliases = _aliasToVirtual.Count,
            VirtualShardsPerPhysical = virtualPerPhysical,
            AverageVirtualPerPhysical = virtualPerPhysical.Count > 0
                ? virtualPerPhysical.Values.Average()
                : 0
        };
    }

    /// <summary>
    /// Resolves a virtual shard to its physical shard.
    /// </summary>
    private string ResolvePhysicalShard(string virtualShardId)
    {
        // Check alias first
        if (_aliasToVirtual.TryGetValue(virtualShardId, out var resolvedId))
        {
            virtualShardId = resolvedId;
        }

        if (_virtualShards.TryGetValue(virtualShardId, out var virtualShard))
        {
            if (!virtualShard.IsActive)
            {
                throw new InvalidOperationException($"Virtual shard '{virtualShardId}' is not active.");
            }
            return virtualShard.PhysicalShardId;
        }

        throw new InvalidOperationException($"Virtual shard '{virtualShardId}' not found.");
    }

    /// <summary>
    /// Clears cache entries for a virtual shard.
    /// </summary>
    private void ClearCacheForVirtualShard(string virtualShardId)
    {
        // Since we hash keys to virtual shards, we need to clear the entire cache
        // when a virtual-to-physical mapping changes
        // A more sophisticated implementation might track which keys map to which virtual shards
        _cache.Clear();
    }

    /// <summary>
    /// Caches a key-to-physical-shard mapping.
    /// </summary>
    private void CacheKeyMapping(string key, string physicalShardId)
    {
        if (_cache.Count >= _cacheMaxSize)
        {
            var toRemove = _cache.Keys.Take(_cacheMaxSize / 10).ToList();
            foreach (var k in toRemove)
            {
                _cache.TryRemove(k, out _);
            }
        }

        _cache[key] = physicalShardId;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _mappingLock.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Statistics about virtual shard mappings.
/// </summary>
public sealed class VirtualMappingStats
{
    /// <summary>
    /// Total virtual shards.
    /// </summary>
    public int TotalVirtualShards { get; init; }

    /// <summary>
    /// Active virtual shards.
    /// </summary>
    public int ActiveVirtualShards { get; init; }

    /// <summary>
    /// Total aliases.
    /// </summary>
    public int TotalAliases { get; init; }

    /// <summary>
    /// Virtual shards per physical shard.
    /// </summary>
    public required IReadOnlyDictionary<string, int> VirtualShardsPerPhysical { get; init; }

    /// <summary>
    /// Average virtual shards per physical shard.
    /// </summary>
    public double AverageVirtualPerPhysical { get; init; }
}
