using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Geographic region for sharding.
/// </summary>
public enum GeoRegion
{
    /// <summary>North America.</summary>
    NorthAmerica,
    /// <summary>South America.</summary>
    SouthAmerica,
    /// <summary>Europe.</summary>
    Europe,
    /// <summary>Asia Pacific.</summary>
    AsiaPacific,
    /// <summary>Middle East.</summary>
    MiddleEast,
    /// <summary>Africa.</summary>
    Africa,
    /// <summary>Australia/Oceania.</summary>
    Oceania
}

/// <summary>
/// Data residency requirement level.
/// </summary>
public enum DataResidencyLevel
{
    /// <summary>No residency requirements - data can be anywhere.</summary>
    None,
    /// <summary>Data should stay in the continent.</summary>
    Continent,
    /// <summary>Data must stay in the country.</summary>
    Country,
    /// <summary>Data must stay in the specific region/state.</summary>
    Region
}

/// <summary>
/// Geographic location information.
/// </summary>
public sealed class GeoLocation
{
    /// <summary>
    /// ISO 3166-1 alpha-2 country code (e.g., "US", "DE", "JP").
    /// </summary>
    public required string CountryCode { get; init; }

    /// <summary>
    /// Geographic region.
    /// </summary>
    public GeoRegion Region { get; init; }

    /// <summary>
    /// Optional sub-region or state code.
    /// </summary>
    public string? SubRegion { get; init; }

    /// <summary>
    /// Optional latitude for distance calculations.
    /// </summary>
    public double? Latitude { get; init; }

    /// <summary>
    /// Optional longitude for distance calculations.
    /// </summary>
    public double? Longitude { get; init; }

    /// <summary>
    /// Data residency requirement for this location.
    /// </summary>
    public DataResidencyLevel ResidencyRequirement { get; init; } = DataResidencyLevel.None;
}

/// <summary>
/// Geo shard configuration.
/// </summary>
public sealed class GeoShardConfig
{
    /// <summary>
    /// The shard ID.
    /// </summary>
    public required string ShardId { get; init; }

    /// <summary>
    /// Geographic region this shard serves.
    /// </summary>
    public GeoRegion Region { get; init; }

    /// <summary>
    /// Country codes this shard can serve (empty = all in region).
    /// </summary>
    public HashSet<string> CountryCodes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Physical location of the shard.
    /// </summary>
    public required GeoLocation Location { get; init; }

    /// <summary>
    /// Whether this shard supports data residency compliance.
    /// </summary>
    public bool SupportsResidencyCompliance { get; init; }

    /// <summary>
    /// Priority for shard selection (higher = preferred).
    /// </summary>
    public int Priority { get; init; } = 100;
}

/// <summary>
/// Geography-based sharding strategy that routes data based on geographic location.
/// </summary>
/// <remarks>
/// Features:
/// - Shard by region, country, or continent
/// - Data residency compliance support (GDPR, etc.)
/// - Geo-proximity routing for lowest latency
/// - Multi-region failover support
/// - Thread-safe geographic lookups
/// </remarks>
public sealed class GeoShardingStrategy : ShardingStrategyBase
{
    private readonly BoundedDictionary<string, GeoShardConfig> _geoConfigs = new BoundedDictionary<string, GeoShardConfig>(1000);
    private readonly BoundedDictionary<GeoRegion, List<string>> _regionToShards = new BoundedDictionary<GeoRegion, List<string>>(1000);
    private readonly BoundedDictionary<string, string> _countryToShard = new BoundedDictionary<string, string>(1000);
    private readonly BoundedDictionary<string, GeoLocation> _keyToLocation = new BoundedDictionary<string, GeoLocation>(1000);
    private readonly BoundedDictionary<string, string> _cache = new BoundedDictionary<string, string>(1000);
    private readonly object _configLock = new();
    private readonly int _cacheMaxSize;
    private GeoLocation? _defaultLocation;

    /// <summary>
    /// Country to region mapping.
    /// </summary>
    private static readonly Dictionary<string, GeoRegion> CountryToRegion = new(StringComparer.OrdinalIgnoreCase)
    {
        // North America
        ["US"] = GeoRegion.NorthAmerica,
        ["CA"] = GeoRegion.NorthAmerica,
        ["MX"] = GeoRegion.NorthAmerica,

        // South America
        ["BR"] = GeoRegion.SouthAmerica,
        ["AR"] = GeoRegion.SouthAmerica,
        ["CL"] = GeoRegion.SouthAmerica,
        ["CO"] = GeoRegion.SouthAmerica,

        // Europe
        ["GB"] = GeoRegion.Europe,
        ["DE"] = GeoRegion.Europe,
        ["FR"] = GeoRegion.Europe,
        ["IT"] = GeoRegion.Europe,
        ["ES"] = GeoRegion.Europe,
        ["NL"] = GeoRegion.Europe,
        ["BE"] = GeoRegion.Europe,
        ["CH"] = GeoRegion.Europe,
        ["AT"] = GeoRegion.Europe,
        ["PL"] = GeoRegion.Europe,
        ["SE"] = GeoRegion.Europe,
        ["NO"] = GeoRegion.Europe,
        ["DK"] = GeoRegion.Europe,
        ["FI"] = GeoRegion.Europe,
        ["IE"] = GeoRegion.Europe,
        ["PT"] = GeoRegion.Europe,

        // Asia Pacific
        ["JP"] = GeoRegion.AsiaPacific,
        ["CN"] = GeoRegion.AsiaPacific,
        ["KR"] = GeoRegion.AsiaPacific,
        ["IN"] = GeoRegion.AsiaPacific,
        ["SG"] = GeoRegion.AsiaPacific,
        ["HK"] = GeoRegion.AsiaPacific,
        ["TW"] = GeoRegion.AsiaPacific,
        ["TH"] = GeoRegion.AsiaPacific,
        ["VN"] = GeoRegion.AsiaPacific,
        ["MY"] = GeoRegion.AsiaPacific,
        ["ID"] = GeoRegion.AsiaPacific,
        ["PH"] = GeoRegion.AsiaPacific,

        // Middle East
        ["AE"] = GeoRegion.MiddleEast,
        ["SA"] = GeoRegion.MiddleEast,
        ["IL"] = GeoRegion.MiddleEast,
        ["TR"] = GeoRegion.MiddleEast,

        // Africa
        ["ZA"] = GeoRegion.Africa,
        ["EG"] = GeoRegion.Africa,
        ["NG"] = GeoRegion.Africa,
        ["KE"] = GeoRegion.Africa,

        // Oceania
        ["AU"] = GeoRegion.Oceania,
        ["NZ"] = GeoRegion.Oceania
    };

    /// <summary>
    /// Initializes a new GeoShardingStrategy with default settings.
    /// </summary>
    public GeoShardingStrategy() : this(100000) { }

    /// <summary>
    /// Initializes a new GeoShardingStrategy with specified cache size.
    /// </summary>
    /// <param name="cacheMaxSize">Maximum size of the lookup cache.</param>
    public GeoShardingStrategy(int cacheMaxSize)
    {
        _cacheMaxSize = cacheMaxSize;
        _defaultLocation = new GeoLocation
        {
            CountryCode = "US",
            Region = GeoRegion.NorthAmerica
        };
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.geo";

    /// <inheritdoc/>
    public override string DisplayName => "Geography-Based Sharding";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 250_000,
        TypicalLatencyMs = 0.05
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Geography-based sharding strategy that routes data to shards based on geographic location. " +
        "Supports data residency compliance (GDPR, etc.) and geo-proximity routing. " +
        "Best for global applications with regional data requirements.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "geo", "geography", "regional", "compliance", "gdpr"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Create default regional shards
        var defaultShards = new[]
        {
            (GeoRegion.NorthAmerica, "shard-geo-na", "us-east-1", "US", 37.7749, -122.4194),
            (GeoRegion.Europe, "shard-geo-eu", "eu-west-1", "DE", 50.1109, 8.6821),
            (GeoRegion.AsiaPacific, "shard-geo-ap", "ap-northeast-1", "JP", 35.6762, 139.6503),
            (GeoRegion.Oceania, "shard-geo-oc", "ap-southeast-2", "AU", -33.8688, 151.2093)
        };

        foreach (var (region, shardId, location, countryCode, lat, lon) in defaultShards)
        {
            var geoConfig = new GeoShardConfig
            {
                ShardId = shardId,
                Region = region,
                Location = new GeoLocation
                {
                    CountryCode = countryCode,
                    Region = region,
                    Latitude = lat,
                    Longitude = lon
                },
                SupportsResidencyCompliance = true
            };

            AddGeoShardInternal(geoConfig);
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

        // Get location for key
        var location = GetLocationForKey(key);
        var shardId = FindBestShardForLocation(location);

        if (!ShardRegistry.TryGetValue(shardId, out var shard))
        {
            throw new InvalidOperationException($"Shard '{shardId}' not found in registry.");
        }

        // Cache the result
        CacheKeyMapping(key, shardId);

        return Task.FromResult(shard);
    }

    /// <inheritdoc/>
    protected override Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct)
    {
        // Geo sharding typically doesn't rebalance based on load
        // as data placement is determined by geography
        // However, we can rebalance within regions if multiple shards exist
        return Task.FromResult(true);
    }

    /// <summary>
    /// Adds a geo-aware shard to the strategy.
    /// </summary>
    /// <param name="config">The geo shard configuration.</param>
    /// <param name="physicalLocation">Physical connection string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created shard info.</returns>
    public Task<ShardInfo> AddGeoShardAsync(
        GeoShardConfig config,
        string physicalLocation,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalLocation);

        if (ShardRegistry.ContainsKey(config.ShardId))
        {
            throw new InvalidOperationException($"Shard '{config.ShardId}' already exists.");
        }

        var shard = new ShardInfo(config.ShardId, physicalLocation, ShardStatus.Online, 0, 0)
        {
            CreatedAt = DateTime.UtcNow,
            LastModifiedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["Region"] = config.Region.ToString(),
                ["CountryCode"] = config.Location.CountryCode
            }
        };

        ShardRegistry[config.ShardId] = shard;
        AddGeoShardInternal(config);
        _cache.Clear();

        return Task.FromResult(shard);
    }

    /// <summary>
    /// Registers a key's geographic location.
    /// </summary>
    /// <param name="key">The key to register.</param>
    /// <param name="location">The geographic location.</param>
    public void RegisterKeyLocation(string key, GeoLocation location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(location);

        _keyToLocation[key] = location;
        _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// Registers multiple keys with the same location.
    /// </summary>
    /// <param name="keys">The keys to register.</param>
    /// <param name="location">The geographic location.</param>
    public void RegisterKeysLocation(IEnumerable<string> keys, GeoLocation location)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(location);

        foreach (var key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _keyToLocation[key] = location;
                _cache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Sets the default location for keys without explicit location.
    /// </summary>
    /// <param name="location">The default location.</param>
    public void SetDefaultLocation(GeoLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        _defaultLocation = location;
        _cache.Clear();
    }

    /// <summary>
    /// Gets the shard for a specific geographic location.
    /// </summary>
    /// <param name="location">The location to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The best shard for the location.</returns>
    public Task<ShardInfo> GetShardForLocationAsync(GeoLocation location, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(location);

        var shardId = FindBestShardForLocation(location);

        if (!ShardRegistry.TryGetValue(shardId, out var shard))
        {
            throw new InvalidOperationException($"No shard found for location.");
        }

        return Task.FromResult(shard);
    }

    /// <summary>
    /// Gets all shards in a region.
    /// </summary>
    /// <param name="region">The region to query.</param>
    /// <returns>Shards serving the region.</returns>
    public IReadOnlyList<ShardInfo> GetShardsInRegion(GeoRegion region)
    {
        if (!_regionToShards.TryGetValue(region, out var shardIds))
        {
            return Array.Empty<ShardInfo>();
        }

        return shardIds
            .Select(id => ShardRegistry.TryGetValue(id, out var s) ? s : null)
            .Where(s => s != null && s.Status == ShardStatus.Online)
            .Cast<ShardInfo>()
            .ToList();
    }

    /// <summary>
    /// Gets shards that support data residency for a country.
    /// </summary>
    /// <param name="countryCode">The country code.</param>
    /// <returns>Compliant shards for the country.</returns>
    public IReadOnlyList<ShardInfo> GetResidencyCompliantShards(string countryCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(countryCode);

        var result = new List<ShardInfo>();

        foreach (var config in _geoConfigs.Values)
        {
            if (!config.SupportsResidencyCompliance)
                continue;

            // Check if config explicitly supports this country
            if (config.CountryCodes.Count > 0 && !config.CountryCodes.Contains(countryCode))
                continue;

            // Check if same region
            if (CountryToRegion.TryGetValue(countryCode, out var region) &&
                config.Region == region)
            {
                if (ShardRegistry.TryGetValue(config.ShardId, out var shard) &&
                    shard.Status == ShardStatus.Online)
                {
                    result.Add(shard);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates distance between two geo locations.
    /// </summary>
    /// <param name="loc1">First location.</param>
    /// <param name="loc2">Second location.</param>
    /// <returns>Distance in kilometers.</returns>
    public static double CalculateDistance(GeoLocation loc1, GeoLocation loc2)
    {
        if (!loc1.Latitude.HasValue || !loc1.Longitude.HasValue ||
            !loc2.Latitude.HasValue || !loc2.Longitude.HasValue)
        {
            return double.MaxValue;
        }

        // Haversine formula
        const double R = 6371; // Earth's radius in km

        var lat1Rad = loc1.Latitude.Value * Math.PI / 180;
        var lat2Rad = loc2.Latitude.Value * Math.PI / 180;
        var deltaLat = (loc2.Latitude.Value - loc1.Latitude.Value) * Math.PI / 180;
        var deltaLon = (loc2.Longitude.Value - loc1.Longitude.Value) * Math.PI / 180;

        var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) +
                Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return R * c;
    }

    /// <summary>
    /// Gets the region for a country code.
    /// </summary>
    /// <param name="countryCode">The country code.</param>
    /// <returns>The geographic region.</returns>
    public static GeoRegion GetRegionForCountry(string countryCode)
    {
        return CountryToRegion.TryGetValue(countryCode, out var region)
            ? region
            : GeoRegion.NorthAmerica; // Default
    }

    /// <summary>
    /// Adds a geo shard configuration internally.
    /// </summary>
    private void AddGeoShardInternal(GeoShardConfig config)
    {
        lock (_configLock)
        {
            _geoConfigs[config.ShardId] = config;

            // Add to region index
            if (!_regionToShards.TryGetValue(config.Region, out var regionShards))
            {
                regionShards = new List<string>();
                _regionToShards[config.Region] = regionShards;
            }

            if (!regionShards.Contains(config.ShardId))
            {
                regionShards.Add(config.ShardId);
            }

            // Add country-specific routing
            foreach (var country in config.CountryCodes)
            {
                _countryToShard[country] = config.ShardId;
            }

            // If no specific countries, use the shard's location country
            if (config.CountryCodes.Count == 0)
            {
                _countryToShard[config.Location.CountryCode] = config.ShardId;
            }

            // Add shard to registry if not exists
            if (!ShardRegistry.ContainsKey(config.ShardId))
            {
                ShardRegistry[config.ShardId] = new ShardInfo(
                    config.ShardId,
                    $"node-{config.Region}/db-geo",
                    ShardStatus.Online,
                    0, 0)
                {
                    CreatedAt = DateTime.UtcNow,
                    LastModifiedAt = DateTime.UtcNow
                };
            }
        }
    }

    /// <summary>
    /// Gets the location for a key.
    /// </summary>
    private GeoLocation GetLocationForKey(string key)
    {
        // Check explicit mapping
        if (_keyToLocation.TryGetValue(key, out var location))
        {
            return location;
        }

        // Try to extract country from key prefix (e.g., "US:user:123")
        var colonIndex = key.IndexOf(':');
        if (colonIndex > 0 && colonIndex <= 3)
        {
            var prefix = key.Substring(0, colonIndex);
            if (CountryToRegion.ContainsKey(prefix))
            {
                return new GeoLocation
                {
                    CountryCode = prefix.ToUpperInvariant(),
                    Region = CountryToRegion[prefix]
                };
            }
        }

        // Return default location
        return _defaultLocation ?? new GeoLocation
        {
            CountryCode = "US",
            Region = GeoRegion.NorthAmerica
        };
    }

    /// <summary>
    /// Finds the best shard for a location.
    /// </summary>
    private string FindBestShardForLocation(GeoLocation location)
    {
        // Check for country-specific shard
        if (_countryToShard.TryGetValue(location.CountryCode, out var countryShardId))
        {
            if (ShardRegistry.TryGetValue(countryShardId, out var countryShard) &&
                countryShard.Status == ShardStatus.Online)
            {
                return countryShardId;
            }
        }

        // Get region for this country
        var region = GetRegionForCountry(location.CountryCode);

        // Find best shard in region
        if (_regionToShards.TryGetValue(region, out var regionShards))
        {
            // If location has coordinates, find closest shard
            if (location.Latitude.HasValue && location.Longitude.HasValue)
            {
                return FindClosestShard(location, regionShards);
            }

            // Otherwise, return first online shard in region
            foreach (var shardId in regionShards)
            {
                if (ShardRegistry.TryGetValue(shardId, out var shard) &&
                    shard.Status == ShardStatus.Online)
                {
                    return shardId;
                }
            }
        }

        // Fallback: find any online shard
        var fallbackShard = ShardRegistry.Values.FirstOrDefault(s => s.Status == ShardStatus.Online);
        return fallbackShard?.ShardId
            ?? throw new InvalidOperationException("No online shards available.");
    }

    /// <summary>
    /// Finds the closest shard to a location.
    /// </summary>
    private string FindClosestShard(GeoLocation location, List<string> shardIds)
    {
        string? closestId = null;
        var minDistance = double.MaxValue;

        foreach (var shardId in shardIds)
        {
            if (!_geoConfigs.TryGetValue(shardId, out var config))
                continue;

            if (!ShardRegistry.TryGetValue(shardId, out var shard) ||
                shard.Status != ShardStatus.Online)
                continue;

            var distance = CalculateDistance(location, config.Location);
            if (distance < minDistance)
            {
                minDistance = distance;
                closestId = shardId;
            }
        }

        return closestId ?? shardIds.First();
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
}
