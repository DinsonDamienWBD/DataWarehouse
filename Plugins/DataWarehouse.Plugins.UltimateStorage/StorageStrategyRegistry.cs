using DataWarehouse.SDK.Contracts.Storage;
using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;

namespace DataWarehouse.Plugins.UltimateStorage;

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
