using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Tenant isolation mode for sharding.
/// </summary>
public enum TenantIsolationMode
{
    /// <summary>
    /// Each tenant gets their own dedicated shard.
    /// </summary>
    Dedicated,

    /// <summary>
    /// Multiple tenants share shards with prefix-based isolation.
    /// </summary>
    Shared,

    /// <summary>
    /// Hybrid mode where large tenants get dedicated shards.
    /// </summary>
    Hybrid
}

/// <summary>
/// Tenant configuration for sharding.
/// </summary>
public sealed class TenantConfig
{
    /// <summary>
    /// Unique tenant identifier.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Display name for the tenant.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Isolation mode for this tenant.
    /// </summary>
    public TenantIsolationMode IsolationMode { get; init; } = TenantIsolationMode.Shared;

    /// <summary>
    /// Assigned shard ID (null = auto-assigned).
    /// </summary>
    public string? AssignedShardId { get; set; }

    /// <summary>
    /// When the tenant was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Current object count for the tenant.
    /// </summary>
    public long ObjectCount { get; set; }

    /// <summary>
    /// Current storage size in bytes.
    /// </summary>
    public long StorageSizeBytes { get; set; }

    /// <summary>
    /// Tenant tier (affects shard assignment in hybrid mode).
    /// </summary>
    public string Tier { get; init; } = "standard";

    /// <summary>
    /// Additional tenant metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Tenant migration status.
/// </summary>
public sealed class TenantMigrationStatus
{
    /// <summary>
    /// Tenant being migrated.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Source shard.
    /// </summary>
    public required string SourceShardId { get; init; }

    /// <summary>
    /// Target shard.
    /// </summary>
    public required string TargetShardId { get; init; }

    /// <summary>
    /// Current migration state.
    /// </summary>
    public TenantMigrationState State { get; set; } = TenantMigrationState.Pending;

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// When migration started.
    /// </summary>
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When migration completed (null if in progress).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Error message if migration failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Tenant migration state.
/// </summary>
public enum TenantMigrationState
{
    /// <summary>Migration is pending.</summary>
    Pending,
    /// <summary>Migration is in progress.</summary>
    InProgress,
    /// <summary>Migration completed successfully.</summary>
    Completed,
    /// <summary>Migration failed.</summary>
    Failed,
    /// <summary>Migration was cancelled.</summary>
    Cancelled
}

/// <summary>
/// Per-tenant sharding strategy that provides tenant isolation.
/// </summary>
/// <remarks>
/// Features:
/// - Dedicated shard per tenant option
/// - Shared shards with tenant prefix isolation
/// - Hybrid mode for mixed tenant sizes
/// - Tenant migration between shards
/// - Thread-safe tenant management
/// </remarks>
public sealed class TenantShardingStrategy : ShardingStrategyBase
{
    private readonly BoundedDictionary<string, TenantConfig> _tenants = new BoundedDictionary<string, TenantConfig>(1000);
    private readonly BoundedDictionary<string, List<string>> _shardToTenants = new BoundedDictionary<string, List<string>>(1000);
    private readonly BoundedDictionary<string, TenantMigrationStatus> _migrations = new BoundedDictionary<string, TenantMigrationStatus>(1000);
    private readonly BoundedDictionary<string, string> _cache = new BoundedDictionary<string, string>(1000);
    private readonly ReaderWriterLockSlim _tenantLock = new();
    private readonly TenantIsolationMode _defaultIsolationMode;
    private readonly int _maxTenantsPerSharedShard;
    private readonly long _dedicatedShardThreshold;
    private readonly int _cacheMaxSize;

    /// <summary>
    /// Initializes a new TenantShardingStrategy with default settings.
    /// </summary>
    public TenantShardingStrategy() : this(TenantIsolationMode.Hybrid) { }

    /// <summary>
    /// Initializes a new TenantShardingStrategy with specified settings.
    /// </summary>
    /// <param name="defaultIsolationMode">Default isolation mode for new tenants.</param>
    /// <param name="maxTenantsPerSharedShard">Max tenants per shared shard.</param>
    /// <param name="dedicatedShardThreshold">Object count threshold for dedicated shard in hybrid mode.</param>
    /// <param name="cacheMaxSize">Maximum cache size.</param>
    public TenantShardingStrategy(
        TenantIsolationMode defaultIsolationMode,
        int maxTenantsPerSharedShard = 100,
        long dedicatedShardThreshold = 1_000_000,
        int cacheMaxSize = 100000)
    {
        _defaultIsolationMode = defaultIsolationMode;
        _maxTenantsPerSharedShard = maxTenantsPerSharedShard;
        _dedicatedShardThreshold = dedicatedShardThreshold;
        _cacheMaxSize = cacheMaxSize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.tenant";

    /// <inheritdoc/>
    public override string DisplayName => "Tenant-Based Sharding";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = false,
        MaxThroughput = 200_000,
        TypicalLatencyMs = 0.05
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Tenant-based sharding strategy that provides data isolation per tenant. " +
        "Supports dedicated shards, shared shards with prefixes, and hybrid mode. " +
        "Best for multi-tenant SaaS applications requiring data isolation.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "tenant", "multi-tenant", "isolation", "saas"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Create initial shared shards
        for (int i = 0; i < 4; i++)
        {
            var shardId = $"shard-shared-{i:D3}";
            ShardRegistry[shardId] = new ShardInfo(
                shardId,
                $"node-{i % 4}/db-tenant",
                ShardStatus.Online,
                0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
            _shardToTenants[shardId] = new List<string>();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct)
    {
        // Check cache first
        if (_cache.TryGetValue(key, out var cachedShardId))
        {
            if (ShardRegistry.TryGetValue(cachedShardId, out var cachedShard) &&
                cachedShard.Status == ShardStatus.Online)
            {
                return Task.FromResult(cachedShard);
            }
            _cache.TryRemove(key, out _);
        }

        // Extract tenant from key
        var tenantId = ExtractTenantId(key);
        var shardId = GetShardForTenant(tenantId);

        if (!ShardRegistry.TryGetValue(shardId, out var shard))
        {
            throw new InvalidOperationException($"Shard '{shardId}' not found for tenant '{tenantId}'.");
        }

        // Cache the result
        CacheKeyMapping(key, shardId);

        return Task.FromResult(shard);
    }

    /// <inheritdoc/>
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct)
    {
        if (!options.AllowDataMovement)
        {
            return true;
        }

        // In hybrid mode, check if any shared tenants should be migrated to dedicated shards
        if (_defaultIsolationMode == TenantIsolationMode.Hybrid)
        {
            var largeTenants = _tenants.Values
                .Where(t => t.IsolationMode == TenantIsolationMode.Shared &&
                           t.ObjectCount >= _dedicatedShardThreshold)
                .ToList();

            foreach (var tenant in largeTenants)
            {
                ct.ThrowIfCancellationRequested();

                // Create dedicated shard and migrate
                var newShardId = $"shard-tenant-{tenant.TenantId}";
                if (!ShardRegistry.ContainsKey(newShardId))
                {
                    await AddShardAsync(newShardId, $"node-dedicated/db-{tenant.TenantId}", ct);
                    await MigrateTenantAsync(tenant.TenantId, newShardId, ct);
                }
            }
        }

        // Balance shared shards
        await BalanceSharedShardsAsync(options, ct);

        return true;
    }

    /// <summary>
    /// Registers a new tenant.
    /// </summary>
    /// <param name="tenantId">Unique tenant identifier.</param>
    /// <param name="displayName">Display name for the tenant.</param>
    /// <param name="isolationMode">Isolation mode (null = use default).</param>
    /// <param name="tier">Tenant tier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tenant configuration.</returns>
    public async Task<TenantConfig> RegisterTenantAsync(
        string tenantId,
        string? displayName = null,
        TenantIsolationMode? isolationMode = null,
        string tier = "standard",
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (_tenants.ContainsKey(tenantId))
        {
            throw new InvalidOperationException($"Tenant '{tenantId}' already exists.");
        }

        var mode = isolationMode ?? _defaultIsolationMode;
        var config = new TenantConfig
        {
            TenantId = tenantId,
            DisplayName = displayName,
            IsolationMode = mode,
            Tier = tier,
            CreatedAt = DateTime.UtcNow
        };

        // Assign shard based on isolation mode
        if (mode == TenantIsolationMode.Dedicated)
        {
            var shardId = $"shard-tenant-{tenantId}";
            await AddShardAsync(shardId, $"node-dedicated/db-{tenantId}", ct);
            config.AssignedShardId = shardId;
        }
        else
        {
            config.AssignedShardId = FindBestSharedShard();
        }

        _tenants[tenantId] = config;

        // Track tenant on shard
        if (config.AssignedShardId != null)
        {
            if (!_shardToTenants.TryGetValue(config.AssignedShardId, out var tenantList))
            {
                tenantList = new List<string>();
                _shardToTenants[config.AssignedShardId] = tenantList;
            }

            lock (tenantList)
            {
                tenantList.Add(tenantId);
            }
        }

        return config;
    }

    /// <summary>
    /// Gets tenant configuration by ID.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <returns>Tenant configuration or null if not found.</returns>
    public TenantConfig? GetTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return _tenants.TryGetValue(tenantId, out var config) ? config : null;
    }

    /// <summary>
    /// Gets all registered tenants.
    /// </summary>
    /// <returns>All tenant configurations.</returns>
    public IReadOnlyCollection<TenantConfig> GetAllTenants()
    {
        return _tenants.Values.ToList();
    }

    /// <summary>
    /// Gets tenants on a specific shard.
    /// </summary>
    /// <param name="shardId">The shard ID.</param>
    /// <returns>Tenants on the shard.</returns>
    public IReadOnlyList<TenantConfig> GetTenantsOnShard(string shardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        if (!_shardToTenants.TryGetValue(shardId, out var tenantIds))
        {
            return Array.Empty<TenantConfig>();
        }

        lock (tenantIds)
        {
            return tenantIds
                .Select(id => _tenants.TryGetValue(id, out var t) ? t : null)
                .Where(t => t != null)
                .Cast<TenantConfig>()
                .ToList();
        }
    }

    /// <summary>
    /// Migrates a tenant to a different shard.
    /// </summary>
    /// <param name="tenantId">The tenant to migrate.</param>
    /// <param name="targetShardId">The target shard.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Migration status.</returns>
    public async Task<TenantMigrationStatus> MigrateTenantAsync(
        string tenantId,
        string targetShardId,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetShardId);

        if (!_tenants.TryGetValue(tenantId, out var tenant))
        {
            throw new InvalidOperationException($"Tenant '{tenantId}' not found.");
        }

        if (!ShardRegistry.ContainsKey(targetShardId))
        {
            throw new InvalidOperationException($"Target shard '{targetShardId}' does not exist.");
        }

        if (tenant.AssignedShardId == targetShardId)
        {
            throw new InvalidOperationException($"Tenant '{tenantId}' is already on shard '{targetShardId}'.");
        }

        var sourceShardId = tenant.AssignedShardId ?? "unknown";
        var migration = new TenantMigrationStatus
        {
            TenantId = tenantId,
            SourceShardId = sourceShardId,
            TargetShardId = targetShardId,
            State = TenantMigrationState.InProgress,
            StartedAt = DateTime.UtcNow
        };

        _migrations[tenantId] = migration;

        try
        {
            // P2-2485: Tenant sharding operates on an in-memory index; the actual
            // "migration" is an atomic re-assignment of the shard pointer for the tenant.
            // Progress tracking reflects the three logical phases: locking, moving, and
            // updating the config -- no artificial delays are needed.
            ct.ThrowIfCancellationRequested();
            migration.ProgressPercent = 10;

            // Update tenant assignment
            _tenantLock.EnterWriteLock();
            try
            {
                // Remove from source shard
                if (!string.IsNullOrEmpty(sourceShardId) &&
                    _shardToTenants.TryGetValue(sourceShardId, out var sourceList))
                {
                    lock (sourceList)
                    {
                        sourceList.Remove(tenantId);
                    }
                }

                // Add to target shard
                if (!_shardToTenants.TryGetValue(targetShardId, out var targetList))
                {
                    targetList = new List<string>();
                    _shardToTenants[targetShardId] = targetList;
                }

                lock (targetList)
                {
                    targetList.Add(tenantId);
                }

                // Update tenant config
                tenant.AssignedShardId = targetShardId;

                // Update isolation mode if moving to dedicated shard
                if (targetShardId.StartsWith("shard-tenant-"))
                {
                    _tenants[tenantId] = new TenantConfig
                    {
                        TenantId = tenant.TenantId,
                        DisplayName = tenant.DisplayName,
                        IsolationMode = TenantIsolationMode.Dedicated,
                        AssignedShardId = targetShardId,
                        CreatedAt = tenant.CreatedAt,
                        ObjectCount = tenant.ObjectCount,
                        StorageSizeBytes = tenant.StorageSizeBytes,
                        Tier = tenant.Tier,
                        Metadata = tenant.Metadata
                    };
                }
            }
            finally
            {
                _tenantLock.ExitWriteLock();
            }

            // Clear cache for this tenant
            ClearCacheForTenant(tenantId);

            migration.ProgressPercent = 100;
            migration.State = TenantMigrationState.Completed;
            migration.CompletedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            migration.State = TenantMigrationState.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            migration.State = TenantMigrationState.Failed;
            migration.ErrorMessage = ex.Message;
            throw;
        }

        return migration;
    }

    /// <summary>
    /// Gets migration status for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <returns>Migration status or null if no migration in progress.</returns>
    public TenantMigrationStatus? GetMigrationStatus(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return _migrations.TryGetValue(tenantId, out var status) ? status : null;
    }

    /// <summary>
    /// Updates tenant metrics.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="objectCount">New object count.</param>
    /// <param name="sizeBytes">New storage size.</param>
    public void UpdateTenantMetrics(string tenantId, long objectCount, long sizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (_tenants.TryGetValue(tenantId, out var tenant))
        {
            tenant.ObjectCount = objectCount;
            tenant.StorageSizeBytes = sizeBytes;
        }
    }

    /// <summary>
    /// Gets the prefixed key for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="key">The original key.</param>
    /// <returns>The tenant-prefixed key.</returns>
    public static string GetTenantPrefixedKey(string tenantId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return $"t:{tenantId}:{key}";
    }

    /// <summary>
    /// Removes a tenant and optionally their data.
    /// </summary>
    /// <param name="tenantId">The tenant to remove.</param>
    /// <param name="deleteData">Whether to delete tenant data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if tenant was removed.</returns>
    public async Task<bool> RemoveTenantAsync(
        string tenantId,
        bool deleteData = false,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!_tenants.TryRemove(tenantId, out var tenant))
        {
            return false;
        }

        // Remove from shard tracking
        if (tenant.AssignedShardId != null &&
            _shardToTenants.TryGetValue(tenant.AssignedShardId, out var tenantList))
        {
            lock (tenantList)
            {
                tenantList.Remove(tenantId);
            }
        }

        // Delete dedicated shard if exists
        if (tenant.IsolationMode == TenantIsolationMode.Dedicated &&
            tenant.AssignedShardId != null)
        {
            await RemoveShardAsync(tenant.AssignedShardId, false, ct);
        }

        ClearCacheForTenant(tenantId);

        return true;
    }

    /// <summary>
    /// Extracts tenant ID from a key.
    /// </summary>
    private static string ExtractTenantId(string key)
    {
        // Expected format: "t:{tenantId}:{rest}" or "{tenantId}:{rest}"
        if (key.StartsWith("t:"))
        {
            var secondColon = key.IndexOf(':', 2);
            if (secondColon > 2)
            {
                return key.Substring(2, secondColon - 2);
            }
        }

        var firstColon = key.IndexOf(':');
        if (firstColon > 0)
        {
            return key.Substring(0, firstColon);
        }

        // No tenant prefix, use default
        return "default";
    }

    /// <summary>
    /// Gets the shard for a tenant.
    /// </summary>
    private string GetShardForTenant(string tenantId)
    {
        if (_tenants.TryGetValue(tenantId, out var tenant) && tenant.AssignedShardId != null)
        {
            return tenant.AssignedShardId;
        }

        // Auto-register unknown tenant
        if (!_tenants.ContainsKey(tenantId))
        {
            var shardId = FindBestSharedShard();
            _tenants[tenantId] = new TenantConfig
            {
                TenantId = tenantId,
                IsolationMode = TenantIsolationMode.Shared,
                AssignedShardId = shardId
            };

            if (_shardToTenants.TryGetValue(shardId, out var tenantList))
            {
                lock (tenantList)
                {
                    tenantList.Add(tenantId);
                }
            }

            return shardId;
        }

        return FindBestSharedShard();
    }

    /// <summary>
    /// Finds the best shared shard for a new tenant.
    /// </summary>
    private string FindBestSharedShard()
    {
        var sharedShards = ShardRegistry.Keys
            .Where(id => id.StartsWith("shard-shared-"))
            .ToList();

        if (sharedShards.Count == 0)
        {
            // Create a new shared shard
            var newShardId = $"shard-shared-{Guid.NewGuid():N}".Substring(0, 20);
            ShardRegistry[newShardId] = new ShardInfo(
                newShardId,
                $"node-shared/db-{newShardId}",
                ShardStatus.Online,
                0, 0);
            _shardToTenants[newShardId] = new List<string>();
            return newShardId;
        }

        // Find shard with fewest tenants
        var bestShard = sharedShards
            .Select(id => (id, count: _shardToTenants.TryGetValue(id, out var list) ? list.Count : 0))
            .OrderBy(x => x.count)
            .First();

        if (bestShard.count >= _maxTenantsPerSharedShard)
        {
            // Create a new shared shard
            var newShardId = $"shard-shared-{sharedShards.Count:D3}";
            ShardRegistry[newShardId] = new ShardInfo(
                newShardId,
                $"node-shared/db-{newShardId}",
                ShardStatus.Online,
                0, 0);
            _shardToTenants[newShardId] = new List<string>();
            return newShardId;
        }

        return bestShard.id;
    }

    /// <summary>
    /// Balances tenants across shared shards.
    /// </summary>
    private async Task BalanceSharedShardsAsync(RebalanceOptions options, CancellationToken ct)
    {
        var sharedShards = _shardToTenants
            .Where(kvp => kvp.Key.StartsWith("shard-shared-"))
            .ToList();

        if (sharedShards.Count < 2)
        {
            return;
        }

        var avgTenants = sharedShards.Average(kvp => kvp.Value.Count);
        var maxMoves = (int)(sharedShards.Sum(kvp => kvp.Value.Count) * options.MaxDataMovementPercent / 100);
        var moves = 0;

        var overloaded = sharedShards
            .Where(kvp => kvp.Value.Count > avgTenants * 1.2)
            .OrderByDescending(kvp => kvp.Value.Count)
            .ToList();

        var underloaded = sharedShards
            .Where(kvp => kvp.Value.Count < avgTenants * 0.8)
            .OrderBy(kvp => kvp.Value.Count)
            .ToList();

        foreach (var (sourceShardId, sourceTenants) in overloaded)
        {
            if (moves >= maxMoves) break;

            var toMove = (int)(sourceTenants.Count - avgTenants);

            foreach (var (targetShardId, _) in underloaded)
            {
                if (moves >= maxMoves || toMove <= 0) break;
                ct.ThrowIfCancellationRequested();

                List<string> tenantsToMove;
                lock (sourceTenants)
                {
                    tenantsToMove = sourceTenants.Take(toMove).ToList();
                }

                foreach (var tenantId in tenantsToMove)
                {
                    if (moves >= maxMoves) break;

                    try
                    {
                        await MigrateTenantAsync(tenantId, targetShardId, ct);
                        moves++;
                        toMove--;
                    }
                    catch
                    {

                        // Continue with other tenants
                        System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Clears cache entries for a tenant.
    /// </summary>
    private void ClearCacheForTenant(string tenantId)
    {
        var prefix = $"t:{tenantId}:";
        var keysToRemove = _cache.Keys
            .Where(k => k.StartsWith(prefix) || k.StartsWith($"{tenantId}:"))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Caches a key-to-shard mapping.
    /// </summary>
    private void CacheKeyMapping(string key, string shardId)
    {
        if (_cache.Count >= _cacheMaxSize)
        {
            var toRemove = _cache.Keys.Take(_cacheMaxSize / 10).ToList();
            foreach (var k in toRemove)
            {
                _cache.TryRemove(k, out _);
            }
        }

        _cache[key] = shardId;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _tenantLock.Dispose();
        return Task.CompletedTask;
    }
}
