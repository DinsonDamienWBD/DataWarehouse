using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Component of a composite shard key.
/// </summary>
public sealed class ShardKeyComponent
{
    /// <summary>
    /// Name of the component.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Order of this component in the composite key (lower = first).
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Weight of this component in shard selection (0-1).
    /// </summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>
    /// Extractor function to get this component from a key.
    /// </summary>
    public Func<string, string>? Extractor { get; init; }
}

/// <summary>
/// Composite shard key with multiple components.
/// </summary>
public sealed class CompositeShardKey
{
    /// <summary>
    /// Component values keyed by component name.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Components { get; init; }

    /// <summary>
    /// Creates a composite key from component values.
    /// </summary>
    public static CompositeShardKey Create(params (string Name, string Value)[] components)
    {
        return new CompositeShardKey
        {
            Components = components.ToDictionary(c => c.Name, c => c.Value)
        };
    }

    /// <summary>
    /// Gets a string representation of the composite key.
    /// </summary>
    public override string ToString()
    {
        return string.Join(":", Components.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"));
    }
}

/// <summary>
/// Multi-key sharding strategy that shards on multiple attributes.
/// </summary>
/// <remarks>
/// Features:
/// - Compound shard key support
/// - Hierarchical sharding (e.g., tenant + time)
/// - Weighted component sharding
/// - Multiple attribute routing
/// - Thread-safe composite key parsing
/// </remarks>
public sealed class CompositeShardingStrategy : ShardingStrategyBase
{
    private readonly List<ShardKeyComponent> _components = new();
    private readonly BoundedDictionary<string, string> _compositeToShard = new BoundedDictionary<string, string>(1000);
    private readonly BoundedDictionary<string, string> _cache = new BoundedDictionary<string, string>(1000);
    private readonly ReaderWriterLockSlim _componentLock = new();
    private readonly int _cacheMaxSize;
    private string _keyDelimiter = ":";
    private string _componentDelimiter = "=";

    /// <summary>
    /// Initializes a new CompositeShardingStrategy with default settings.
    /// </summary>
    public CompositeShardingStrategy() : this(100000) { }

    /// <summary>
    /// Initializes a new CompositeShardingStrategy with specified cache size.
    /// </summary>
    /// <param name="cacheMaxSize">Maximum cache size.</param>
    public CompositeShardingStrategy(int cacheMaxSize)
    {
        _cacheMaxSize = cacheMaxSize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.composite";

    /// <inheritdoc/>
    public override string DisplayName => "Composite Multi-Key Sharding";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 250_000,
        TypicalLatencyMs = 0.04
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Composite sharding strategy that uses multiple key components for shard selection. " +
        "Supports hierarchical sharding (e.g., tenant + region + time) with weighted components. " +
        "Best for complex data models requiring multi-dimensional partitioning.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "composite", "multi-key", "hierarchical", "compound"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Create default shards
        for (int i = 0; i < 16; i++)
        {
            var shardId = $"shard-composite-{i:D4}";
            ShardRegistry[shardId] = new ShardInfo(
                shardId,
                $"node-{i % 4}/db-composite",
                ShardStatus.Online,
                0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
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

        // Parse composite key
        var compositeKey = ParseCompositeKey(key);
        var shardId = DetermineShardForCompositeKey(compositeKey);

        if (!ShardRegistry.TryGetValue(shardId, out var shard))
        {
            throw new InvalidOperationException($"Shard '{shardId}' not found for key '{key}'.");
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

        // Redistribute composite key mappings for balance
        var avgMappings = _compositeToShard.Count / (double)shards.Count;
        var shardMappingCounts = _compositeToShard
            .GroupBy(kvp => kvp.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var overloaded = shardMappingCounts
            .Where(kvp => kvp.Value > avgMappings * 1.2)
            .ToList();

        var underloaded = shards
            .Where(s => !shardMappingCounts.ContainsKey(s.ShardId) ||
                       shardMappingCounts[s.ShardId] < avgMappings * 0.8)
            .ToList();

        var maxMoves = (int)(_compositeToShard.Count * options.MaxDataMovementPercent / 100);
        var moves = 0;

        foreach (var (sourceShardId, count) in overloaded)
        {
            if (moves >= maxMoves) break;

            var keysToMove = _compositeToShard
                .Where(kvp => kvp.Value == sourceShardId)
                .Take((int)(count - avgMappings))
                .ToList();

            foreach (var (compositeKey, _) in keysToMove)
            {
                if (moves >= maxMoves) break;
                ct.ThrowIfCancellationRequested();

                var targetShard = underloaded.FirstOrDefault();
                if (targetShard == null) break;

                _compositeToShard[compositeKey] = targetShard.ShardId;
                moves++;
            }
        }

        _cache.Clear();
        return true;
    }

    /// <summary>
    /// Adds a key component to the composite key definition.
    /// </summary>
    /// <param name="component">The component to add.</param>
    public void AddKeyComponent(ShardKeyComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        _componentLock.EnterWriteLock();
        try
        {
            if (_components.Any(c => c.Name.Equals(component.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Component '{component.Name}' already exists.");
            }

            _components.Add(component);
            _components.Sort((a, b) => a.Order.CompareTo(b.Order));
            _cache.Clear();
        }
        finally
        {
            _componentLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Configures common composite key patterns.
    /// </summary>
    /// <param name="pattern">The pattern name (e.g., "tenant-time", "geo-tenant").</param>
    public void ConfigurePattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        _componentLock.EnterWriteLock();
        try
        {
            _components.Clear();

            switch (pattern.ToLowerInvariant())
            {
                case "tenant-time":
                    _components.Add(new ShardKeyComponent
                    {
                        Name = "tenant",
                        Order = 0,
                        Weight = 0.6,
                        Extractor = key => ExtractComponent(key, 0)
                    });
                    _components.Add(new ShardKeyComponent
                    {
                        Name = "time",
                        Order = 1,
                        Weight = 0.4,
                        Extractor = key => ExtractTimeComponent(key)
                    });
                    break;

                case "geo-tenant":
                    _components.Add(new ShardKeyComponent
                    {
                        Name = "geo",
                        Order = 0,
                        Weight = 0.5,
                        Extractor = key => ExtractComponent(key, 0)
                    });
                    _components.Add(new ShardKeyComponent
                    {
                        Name = "tenant",
                        Order = 1,
                        Weight = 0.5,
                        Extractor = key => ExtractComponent(key, 1)
                    });
                    break;

                case "tenant-category-id":
                    _components.Add(new ShardKeyComponent
                    {
                        Name = "tenant",
                        Order = 0,
                        Weight = 0.5,
                        Extractor = key => ExtractComponent(key, 0)
                    });
                    _components.Add(new ShardKeyComponent
                    {
                        Name = "category",
                        Order = 1,
                        Weight = 0.3,
                        Extractor = key => ExtractComponent(key, 1)
                    });
                    _components.Add(new ShardKeyComponent
                    {
                        Name = "id",
                        Order = 2,
                        Weight = 0.2,
                        Extractor = key => ExtractComponent(key, 2)
                    });
                    break;

                default:
                    throw new ArgumentException($"Unknown pattern: {pattern}");
            }

            _cache.Clear();
        }
        finally
        {
            _componentLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Sets the delimiters used for parsing composite keys.
    /// </summary>
    /// <param name="keyDelimiter">Delimiter between key parts (default: ":").</param>
    /// <param name="componentDelimiter">Delimiter between component name and value (default: "=").</param>
    public void SetDelimiters(string keyDelimiter = ":", string componentDelimiter = "=")
    {
        _keyDelimiter = keyDelimiter ?? ":";
        _componentDelimiter = componentDelimiter ?? "=";
        _cache.Clear();
    }

    /// <summary>
    /// Gets the shard for an explicit composite key.
    /// </summary>
    /// <param name="compositeKey">The composite key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The shard for the composite key.</returns>
    public Task<ShardInfo> GetShardForCompositeKeyAsync(
        CompositeShardKey compositeKey,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(compositeKey);

        var shardId = DetermineShardForCompositeKey(compositeKey);

        if (!ShardRegistry.TryGetValue(shardId, out var shard))
        {
            throw new InvalidOperationException($"Shard '{shardId}' not found.");
        }

        return Task.FromResult(shard);
    }

    /// <summary>
    /// Registers an explicit mapping from composite key to shard.
    /// </summary>
    /// <param name="compositeKey">The composite key.</param>
    /// <param name="shardId">The target shard.</param>
    public void RegisterCompositeMapping(CompositeShardKey compositeKey, string shardId)
    {
        ArgumentNullException.ThrowIfNull(compositeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        if (!ShardRegistry.ContainsKey(shardId))
        {
            throw new InvalidOperationException($"Shard '{shardId}' does not exist.");
        }

        _compositeToShard[compositeKey.ToString()] = shardId;
        _cache.Clear();
    }

    /// <summary>
    /// Gets all composite key mappings.
    /// </summary>
    /// <returns>All composite to shard mappings.</returns>
    public IReadOnlyDictionary<string, string> GetAllMappings()
    {
        return _compositeToShard.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Creates a formatted composite key string.
    /// </summary>
    /// <param name="components">Component name-value pairs.</param>
    /// <returns>Formatted key string.</returns>
    public string CreateCompositeKeyString(params (string Name, string Value)[] components)
    {
        var parts = new List<string>();

        _componentLock.EnterReadLock();
        try
        {
            foreach (var componentDef in _components.OrderBy(c => c.Order))
            {
                var match = components.FirstOrDefault(c =>
                    c.Name.Equals(componentDef.Name, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(match.Value))
                {
                    parts.Add(match.Value);
                }
            }
        }
        finally
        {
            _componentLock.ExitReadLock();
        }

        return string.Join(_keyDelimiter, parts);
    }

    /// <summary>
    /// Parses a string key into a composite key.
    /// </summary>
    private CompositeShardKey ParseCompositeKey(string key)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = key.Split(_keyDelimiter, StringSplitOptions.RemoveEmptyEntries);

        _componentLock.EnterReadLock();
        try
        {
            // Try named components first (e.g., "tenant=abc:time=2024")
            foreach (var part in parts)
            {
                if (part.Contains(_componentDelimiter))
                {
                    var nameValue = part.Split(_componentDelimiter, 2);
                    if (nameValue.Length == 2)
                    {
                        values[nameValue[0]] = nameValue[1];
                    }
                }
            }

            // If no named components, use positional
            if (values.Count == 0)
            {
                for (int i = 0; i < Math.Min(parts.Length, _components.Count); i++)
                {
                    var component = _components[i];
                    values[component.Name] = component.Extractor?.Invoke(key) ?? parts[i];
                }
            }
        }
        finally
        {
            _componentLock.ExitReadLock();
        }

        return new CompositeShardKey { Components = values };
    }

    /// <summary>
    /// Determines the shard for a composite key.
    /// </summary>
    private string DetermineShardForCompositeKey(CompositeShardKey compositeKey)
    {
        // Check for explicit mapping
        var compositeString = compositeKey.ToString();
        if (_compositeToShard.TryGetValue(compositeString, out var mappedShard))
        {
            return mappedShard;
        }

        // Calculate weighted hash
        var onlineShards = GetOnlineShards();
        if (onlineShards.Count == 0)
        {
            throw new InvalidOperationException("No online shards available.");
        }

        uint weightedHash = 0;

        _componentLock.EnterReadLock();
        try
        {
            foreach (var component in _components)
            {
                if (compositeKey.Components.TryGetValue(component.Name, out var value))
                {
                    var componentHash = ComputeHash(value);
                    weightedHash += (uint)(componentHash * component.Weight);
                }
            }
        }
        finally
        {
            _componentLock.ExitReadLock();
        }

        var shardIndex = (int)(weightedHash % (uint)onlineShards.Count);
        return onlineShards[shardIndex].ShardId;
    }

    /// <summary>
    /// Extracts a component by position from a key.
    /// </summary>
    private string ExtractComponent(string key, int position)
    {
        var parts = key.Split(_keyDelimiter, StringSplitOptions.RemoveEmptyEntries);
        return position < parts.Length ? parts[position] : string.Empty;
    }

    /// <summary>
    /// Extracts a time component from a key.
    /// </summary>
    private static string ExtractTimeComponent(string key)
    {
        // Try to find ISO date pattern
        if (key.Length >= 10)
        {
            var potential = key.Substring(0, 10);
            if (DateTime.TryParse(potential, out var date))
            {
                return date.ToString("yyyy-MM");
            }
        }

        // Look for embedded timestamp
        var parts = key.Split(':');
        foreach (var part in parts)
        {
            if (DateTime.TryParse(part, out var date))
            {
                return date.ToString("yyyy-MM");
            }
        }

        return DateTime.UtcNow.ToString("yyyy-MM");
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
        _componentLock.Dispose();
        return Task.CompletedTask;
    }
}
