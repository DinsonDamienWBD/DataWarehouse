using System.Reflection;
using DataWarehouse.SDK.Contracts.Storage;
using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage;

/// <summary>
/// Interface for storage strategy registry.
/// Provides auto-discovery and lookup of storage strategies.
/// </summary>
[Obsolete("Use inherited StoragePluginBase.StorageStrategyRegistry (StrategyRegistry<IStorageStrategy>). This interface will be removed in v6.0.")]
public interface IStorageStrategyRegistry
{
    /// <summary>Registers a storage strategy.</summary>
    void Register(IStorageStrategy strategy);

    /// <summary>Gets a strategy by its ID.</summary>
    IStorageStrategy? GetStrategy(string strategyId);

    /// <summary>Gets all registered strategies.</summary>
    IReadOnlyCollection<IStorageStrategy> GetAllStrategies();

    /// <summary>Gets strategies by category.</summary>
    IReadOnlyCollection<IStorageStrategy> GetStrategiesByCategory(string category);

    /// <summary>Gets strategies by tier.</summary>
    IReadOnlyCollection<IStorageStrategy> GetStrategiesByTier(StorageTier tier);

    /// <summary>Gets the default strategy.</summary>
    IStorageStrategy GetDefaultStrategy();

    /// <summary>Sets the default strategy.</summary>
    void SetDefaultStrategy(string strategyId);

    /// <summary>Discovers and registers strategies from assemblies.</summary>
    void DiscoverStrategies(params Assembly[] assemblies);
}

/// <summary>
/// Default implementation of storage strategy registry.
/// Provides thread-safe registration and lookup of strategies.
/// </summary>
[Obsolete("Use inherited StoragePluginBase.StorageStrategyRegistry (StrategyRegistry<IStorageStrategy>) instead. This class will be removed in v6.0.")]
public sealed class StorageStrategyRegistry : IStorageStrategyRegistry
{
    private readonly BoundedDictionary<string, IStorageStrategy> _strategies = new BoundedDictionary<string, IStorageStrategy>(1000);
    private volatile string _defaultStrategyId = "filesystem";

    /// <inheritdoc/>
    public void Register(IStorageStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <inheritdoc/>
    public IStorageStrategy? GetStrategy(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IStorageStrategy> GetAllStrategies()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IStorageStrategy> GetStrategiesByCategory(string category)
    {
        return _strategies.Values
            .Where(s => GetStrategyCategory(s).Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IStorageStrategy> GetStrategiesByTier(StorageTier tier)
    {
        return _strategies.Values
            .Where(s => s.Tier == tier)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IStorageStrategy GetDefaultStrategy()
    {
        return GetStrategy(_defaultStrategyId)
            ?? throw new InvalidOperationException($"Default strategy '{_defaultStrategyId}' not found");
    }

    /// <inheritdoc/>
    public void SetDefaultStrategy(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        if (!_strategies.ContainsKey(strategyId))
        {
            throw new ArgumentException($"Strategy '{strategyId}' not registered", nameof(strategyId));
        }
        _defaultStrategyId = strategyId;
    }

    /// <inheritdoc/>
    public void DiscoverStrategies(params Assembly[] assemblies)
    {
        var strategyType = typeof(IStorageStrategy);

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && strategyType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IStorageStrategy strategy)
                        {
                            Register(strategy);
                        }
                    }
                    catch
                    {
                        // Skip types that can't be instantiated
                    }
                }
            }
            catch
            {
                // Skip assemblies that can't be scanned
            }
        }
    }

    /// <summary>
    /// Gets the category of a storage strategy based on its ID.
    /// </summary>
    private static string GetStrategyCategory(IStorageStrategy strategy)
    {
        var id = strategy.StrategyId.ToLowerInvariant();

        if (id.Contains("s3") || id.Contains("azure") || id.Contains("gcs") || id.Contains("cloud") ||
            id.Contains("blob") || id.Contains("bucket") || id.Contains("minio") || id.Contains("spaces"))
            return "cloud";

        if (id.Contains("ipfs") || id.Contains("arweave") || id.Contains("storj") || id.Contains("sia") ||
            id.Contains("filecoin") || id.Contains("swarm"))
            return "distributed";

        if (id.Contains("nfs") || id.Contains("smb") || id.Contains("cifs") || id.Contains("ftp") ||
            id.Contains("sftp") || id.Contains("webdav"))
            return "network";

        if (id.Contains("mongo") || id.Contains("postgres") || id.Contains("sql") || id.Contains("redis") ||
            id.Contains("database") || id.Contains("gridfs"))
            return "database";

        if (id.Contains("file") || id.Contains("disk") || id.Contains("local") || id.Contains("memory") ||
            id.Contains("ram"))
            return "local";

        if (id.Contains("tape") || id.Contains("lto") || id.Contains("optical") || id.Contains("cold"))
            return "specialized";

        return "other";
    }

    /// <summary>
    /// Creates a pre-populated registry with common strategies.
    /// </summary>
    public static StorageStrategyRegistry CreateDefault()
    {
        return new StorageStrategyRegistry();
    }
}

/// <summary>
/// Extended storage strategy information for UltimateStorage plugin.
/// Adapts SDK's IStorageStrategy with additional properties.
/// </summary>
public interface IStorageStrategyExtended : IStorageStrategy
{
    /// <summary>Gets the unique identifier for this storage strategy.</summary>
    new string StrategyId { get; }

    /// <summary>Gets the human-readable name of this storage strategy.</summary>
    string StrategyName { get; }

    /// <summary>Gets the category of this storage strategy.</summary>
    string Category { get; }

    /// <summary>Gets whether this strategy is currently available.</summary>
    bool IsAvailable { get; }

    /// <summary>Gets whether this strategy supports tiering.</summary>
    bool SupportsTiering => Capabilities.SupportsTiering;

    /// <summary>Gets whether this strategy supports versioning.</summary>
    bool SupportsVersioning => Capabilities.SupportsVersioning;

    /// <summary>Gets whether this strategy supports replication.</summary>
    bool SupportsReplication { get; }

    /// <summary>Gets the maximum object size in bytes.</summary>
    long? MaxObjectSize => Capabilities.MaxObjectSize;

    /// <summary>Performs a health check on this storage backend.</summary>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);

    /// <summary>Writes data to storage.</summary>
    Task WriteAsync(string path, byte[] data, StorageOptions options, CancellationToken ct = default);

    /// <summary>Reads data from storage.</summary>
    Task<byte[]> ReadAsync(string path, StorageOptions options, CancellationToken ct = default);

    /// <summary>Deletes data from storage.</summary>
    Task DeleteAsync(string path, StorageOptions options, CancellationToken ct = default);

    /// <summary>Checks if object exists.</summary>
    Task<bool> ExistsAsync(string path, StorageOptions options, CancellationToken ct = default);

    /// <summary>Lists objects in storage.</summary>
    Task<List<StorageObjectMetadata>> ListAsync(string prefix, StorageOptions options, CancellationToken ct = default);

    /// <summary>Copies object to new location.</summary>
    Task CopyAsync(string sourcePath, string destinationPath, StorageOptions options, CancellationToken ct = default);

    /// <summary>Moves object to new location.</summary>
    Task MoveAsync(string sourcePath, string destinationPath, StorageOptions options, CancellationToken ct = default);

    /// <summary>Gets metadata for an object.</summary>
    Task<Dictionary<string, string>> GetMetadataAsync(string path, StorageOptions options, CancellationToken ct = default);

    /// <summary>Sets metadata for an object.</summary>
    Task SetMetadataAsync(string path, Dictionary<string, string> metadata, StorageOptions options, CancellationToken ct = default);
}

/// <summary>
/// Storage options for operations.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>Operation timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Buffer size for streaming operations.</summary>
    public int BufferSize { get; set; } = 81920;

    /// <summary>Whether to enable compression.</summary>
    public bool EnableCompression { get; set; }

    /// <summary>Custom metadata to attach to objects.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}
