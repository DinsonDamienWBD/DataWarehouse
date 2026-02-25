using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Directory entry for shard routing.
/// </summary>
public sealed class DirectoryEntry
{
    /// <summary>
    /// The key or key pattern.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// The assigned shard ID.
    /// </summary>
    public required string ShardId { get; init; }

    /// <summary>
    /// When the entry was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the entry was last accessed.
    /// </summary>
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of times this entry has been accessed.
    /// </summary>
    public long AccessCount { get; set; }

    /// <summary>
    /// Optional metadata for the entry.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Directory-based sharding strategy using a lookup service for shard routing.
/// </summary>
/// <remarks>
/// Features:
/// - Explicit key-to-shard mapping via directory service
/// - Cached lookups for performance
/// - Directory persistence to file
/// - Support for key patterns with wildcards
/// - Thread-safe directory operations
/// </remarks>
public sealed class DirectoryShardingStrategy : ShardingStrategyBase
{
    private readonly BoundedDictionary<string, DirectoryEntry> _directory = new BoundedDictionary<string, DirectoryEntry>(1000);
    private readonly BoundedDictionary<string, string> _cache = new BoundedDictionary<string, string>(1000);
    private readonly ReaderWriterLockSlim _directoryLock = new();
    private readonly int _cacheMaxSize;
    private readonly string? _persistencePath;
    private readonly Timer? _persistenceTimer;
    private bool _isDirty;
    private string _defaultShardId = "shard-default";

    /// <summary>
    /// Initializes a new DirectoryShardingStrategy with default settings.
    /// </summary>
    public DirectoryShardingStrategy() : this(null, 100000) { }

    /// <summary>
    /// Initializes a new DirectoryShardingStrategy with specified settings.
    /// </summary>
    /// <param name="persistencePath">Path for directory persistence (null to disable).</param>
    /// <param name="cacheMaxSize">Maximum size of the lookup cache.</param>
    /// <param name="persistenceIntervalSeconds">Interval for automatic persistence.</param>
    public DirectoryShardingStrategy(
        string? persistencePath,
        int cacheMaxSize = 100000,
        int persistenceIntervalSeconds = 60)
    {
        _persistencePath = persistencePath;
        _cacheMaxSize = cacheMaxSize;

        if (persistencePath != null && persistenceIntervalSeconds > 0)
        {
            _persistenceTimer = new Timer(
                async _ => await PersistDirectoryAsync(),
                null,
                TimeSpan.FromSeconds(persistenceIntervalSeconds),
                TimeSpan.FromSeconds(persistenceIntervalSeconds));
        }
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.directory";

    /// <inheritdoc/>
    public override string DisplayName => "Directory-Based Sharding";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = false,
        MaxThroughput = 300_000,
        TypicalLatencyMs = 0.03
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Directory-based sharding strategy using explicit key-to-shard mappings stored in a lookup service. " +
        "Supports cached lookups and directory persistence for durability. " +
        "Best for scenarios requiring explicit control over data placement.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "directory", "lookup", "explicit", "mapping"];

    /// <inheritdoc/>
    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        // Try to load persisted directory
        if (_persistencePath != null && File.Exists(_persistencePath))
        {
            await LoadDirectoryAsync(ct);
        }

        // Create default shard if no shards exist
        if (ShardRegistry.IsEmpty)
        {
            ShardRegistry[_defaultShardId] = new ShardInfo(
                _defaultShardId,
                "node-0/db-directory",
                ShardStatus.Online,
                0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
        }
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
                // Update access stats
                if (_directory.TryGetValue(key, out var entry))
                {
                    entry.LastAccessedAt = DateTime.UtcNow;
                    entry.AccessCount++;
                }
                return Task.FromResult(cachedShard);
            }
            _cache.TryRemove(key, out _);
        }

        // Look up in directory
        var shardId = LookupInDirectory(key);

        if (!ShardRegistry.TryGetValue(shardId, out var shard))
        {
            throw new InvalidOperationException($"Shard '{shardId}' not found in registry.");
        }

        if (shard.Status != ShardStatus.Online)
        {
            throw new InvalidOperationException($"Shard '{shardId}' is not online (status: {shard.Status}).");
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

        var shards = GetOnlineShards();
        if (shards.Count < 2)
        {
            return true;
        }

        // Get directory entries grouped by shard
        var entriesByShard = _directory.Values
            .GroupBy(e => e.ShardId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var avgEntries = _directory.Count / (double)shards.Count;
        var maxMovement = (int)(_directory.Count * options.MaxDataMovementPercent / 100);
        var moved = 0;

        // Find overloaded and underloaded shards
        var overloaded = entriesByShard
            .Where(kvp => kvp.Value.Count > avgEntries * (1 + (1 - options.TargetBalanceRatio)))
            .OrderByDescending(kvp => kvp.Value.Count)
            .ToList();

        var underloaded = shards
            .Where(s => !entriesByShard.ContainsKey(s.ShardId) ||
                        entriesByShard[s.ShardId].Count < avgEntries * options.TargetBalanceRatio)
            .ToList();

        foreach (var (sourceShardId, entries) in overloaded)
        {
            if (moved >= maxMovement) break;

            var excess = (int)(entries.Count - avgEntries);

            foreach (var targetShard in underloaded)
            {
                if (moved >= maxMovement) break;
                ct.ThrowIfCancellationRequested();

                var toMove = Math.Min(excess, maxMovement - moved);
                var entriesToMove = entries.Take(toMove).ToList();

                foreach (var entry in entriesToMove)
                {
                    await UpdateMappingAsync(entry.Key, targetShard.ShardId, ct);
                    moved++;
                    excess--;
                }

                if (excess <= 0) break;
            }
        }

        return true;
    }

    /// <summary>
    /// Registers a key-to-shard mapping in the directory.
    /// </summary>
    /// <param name="key">The key to register.</param>
    /// <param name="shardId">The target shard.</param>
    /// <param name="metadata">Optional metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created directory entry.</returns>
    public Task<DirectoryEntry> RegisterMappingAsync(
        string key,
        string shardId,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        if (!ShardRegistry.ContainsKey(shardId))
        {
            throw new InvalidOperationException($"Shard '{shardId}' does not exist.");
        }

        var entry = new DirectoryEntry
        {
            Key = key,
            ShardId = shardId,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            AccessCount = 0,
            Metadata = metadata
        };

        _directory[key] = entry;
        _cache[key] = shardId;
        _isDirty = true;

        return Task.FromResult(entry);
    }

    /// <summary>
    /// Registers multiple key-to-shard mappings in batch.
    /// </summary>
    /// <param name="mappings">Dictionary of key to shard ID mappings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of mappings registered.</returns>
    public Task<int> RegisterMappingsBatchAsync(
        IReadOnlyDictionary<string, string> mappings,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(mappings);

        int count = 0;

        foreach (var (key, shardId) in mappings)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(shardId))
                continue;

            if (!ShardRegistry.ContainsKey(shardId))
                continue;

            var entry = new DirectoryEntry
            {
                Key = key,
                ShardId = shardId,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                AccessCount = 0
            };

            _directory[key] = entry;
            _cache[key] = shardId;
            count++;
        }

        _isDirty = true;
        return Task.FromResult(count);
    }

    /// <summary>
    /// Updates an existing key-to-shard mapping.
    /// </summary>
    /// <param name="key">The key to update.</param>
    /// <param name="newShardId">The new target shard.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the mapping was updated.</returns>
    public Task<bool> UpdateMappingAsync(string key, string newShardId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(newShardId);

        if (!ShardRegistry.ContainsKey(newShardId))
        {
            throw new InvalidOperationException($"Shard '{newShardId}' does not exist.");
        }

        if (_directory.TryGetValue(key, out var existingEntry))
        {
            var updated = new DirectoryEntry
            {
                Key = key,
                ShardId = newShardId,
                CreatedAt = existingEntry.CreatedAt,
                LastAccessedAt = DateTime.UtcNow,
                AccessCount = existingEntry.AccessCount,
                Metadata = existingEntry.Metadata
            };

            _directory[key] = updated;
            _cache[key] = newShardId;
            _isDirty = true;

            return Task.FromResult(true);
        }

        // Create new entry if not exists
        _directory[key] = new DirectoryEntry
        {
            Key = key,
            ShardId = newShardId,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow
        };
        _cache[key] = newShardId;
        _isDirty = true;

        return Task.FromResult(true);
    }

    /// <summary>
    /// Removes a key mapping from the directory.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the mapping was removed.</returns>
    public Task<bool> RemoveMappingAsync(string key, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var removed = _directory.TryRemove(key, out _);
        _cache.TryRemove(key, out _);

        if (removed)
        {
            _isDirty = true;
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Gets a directory entry by key.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <returns>The directory entry or null if not found.</returns>
    public DirectoryEntry? GetEntry(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _directory.TryGetValue(key, out var entry) ? entry : null;
    }

    /// <summary>
    /// Gets all directory entries.
    /// </summary>
    /// <returns>All directory entries.</returns>
    public IReadOnlyCollection<DirectoryEntry> GetAllEntries()
    {
        return _directory.Values.ToList();
    }

    /// <summary>
    /// Gets entries for a specific shard.
    /// </summary>
    /// <param name="shardId">The shard to query.</param>
    /// <returns>Entries mapped to the shard.</returns>
    public IReadOnlyCollection<DirectoryEntry> GetEntriesForShard(string shardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        return _directory.Values
            .Where(e => e.ShardId == shardId)
            .ToList();
    }

    /// <summary>
    /// Sets the default shard for unmapped keys.
    /// </summary>
    /// <param name="shardId">The default shard ID.</param>
    public void SetDefaultShard(string shardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        if (!ShardRegistry.ContainsKey(shardId))
        {
            throw new InvalidOperationException($"Shard '{shardId}' does not exist.");
        }

        _defaultShardId = shardId;
    }

    /// <summary>
    /// Persists the directory to storage.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task PersistDirectoryAsync(CancellationToken ct = default)
    {
        if (_persistencePath == null || !_isDirty)
        {
            return;
        }

        _directoryLock.EnterReadLock();
        try
        {
            var data = new DirectoryPersistenceData
            {
                Entries = _directory.Values.ToList(),
                DefaultShardId = _defaultShardId,
                PersistedAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(_persistencePath, json, ct);
            _isDirty = false;
        }
        finally
        {
            _directoryLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Loads the directory from storage.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadDirectoryAsync(CancellationToken ct = default)
    {
        if (_persistencePath == null || !File.Exists(_persistencePath))
        {
            return;
        }

        _directoryLock.EnterWriteLock();
        try
        {
            var json = await File.ReadAllTextAsync(_persistencePath, ct);
            var data = JsonSerializer.Deserialize<DirectoryPersistenceData>(json);

            if (data != null)
            {
                _directory.Clear();
                _cache.Clear();

                foreach (var entry in data.Entries)
                {
                    _directory[entry.Key] = entry;
                }

                if (!string.IsNullOrWhiteSpace(data.DefaultShardId))
                {
                    _defaultShardId = data.DefaultShardId;
                }
            }

            _isDirty = false;
        }
        finally
        {
            _directoryLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Looks up a key in the directory, with pattern matching support.
    /// </summary>
    private string LookupInDirectory(string key)
    {
        // Exact match first
        if (_directory.TryGetValue(key, out var entry))
        {
            entry.LastAccessedAt = DateTime.UtcNow;
            entry.AccessCount++;
            return entry.ShardId;
        }

        // Try prefix matching (keys ending with *)
        _directoryLock.EnterReadLock();
        try
        {
            foreach (var kvp in _directory)
            {
                if (kvp.Key.EndsWith('*'))
                {
                    var prefix = kvp.Key[..^1];
                    if (key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        kvp.Value.LastAccessedAt = DateTime.UtcNow;
                        kvp.Value.AccessCount++;
                        return kvp.Value.ShardId;
                    }
                }
            }
        }
        finally
        {
            _directoryLock.ExitReadLock();
        }

        // Return default shard
        return _defaultShardId;
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
    protected override async Task DisposeCoreAsync()
    {
        _persistenceTimer?.Dispose();

        // Final persistence
        if (_isDirty && _persistencePath != null)
        {
            await PersistDirectoryAsync();
        }

        _directoryLock.Dispose();
    }

    /// <summary>
    /// Data structure for directory persistence.
    /// </summary>
    private sealed class DirectoryPersistenceData
    {
        public List<DirectoryEntry> Entries { get; set; } = new();
        public string DefaultShardId { get; set; } = "shard-default";
        public DateTime PersistedAt { get; set; }
    }
}
