namespace DataWarehouse.Shared.Models;

/// <summary>
/// Represents the capabilities of a DataWarehouse instance
/// </summary>
public class InstanceCapabilities
{
    /// <summary>
    /// Instance unique identifier
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// Instance name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// DataWarehouse version
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// List of loaded plugin IDs
    /// </summary>
    public List<string> LoadedPlugins { get; set; } = new();

    /// <summary>
    /// Whether encryption is supported
    /// </summary>
    public bool SupportsEncryption { get; set; }

    /// <summary>
    /// Whether compression is supported
    /// </summary>
    public bool SupportsCompression { get; set; }

    /// <summary>
    /// Whether metadata is supported
    /// </summary>
    public bool SupportsMetadata { get; set; }

    /// <summary>
    /// Whether versioning is supported
    /// </summary>
    public bool SupportsVersioning { get; set; }

    /// <summary>
    /// Whether deduplication is supported
    /// </summary>
    public bool SupportsDeduplication { get; set; }

    /// <summary>
    /// Whether RAID is supported
    /// </summary>
    public bool SupportsRaid { get; set; }

    /// <summary>
    /// Whether replication is supported
    /// </summary>
    public bool SupportsReplication { get; set; }

    /// <summary>
    /// Whether backup is supported
    /// </summary>
    public bool SupportsBackup { get; set; }

    /// <summary>
    /// Whether tiering is supported
    /// </summary>
    public bool SupportsTiering { get; set; }

    /// <summary>
    /// Whether search is supported
    /// </summary>
    public bool SupportsSearch { get; set; }

    /// <summary>
    /// Available storage backends
    /// </summary>
    public List<string> StorageBackends { get; set; } = new();

    /// <summary>
    /// Available encryption algorithms
    /// </summary>
    public List<string> EncryptionAlgorithms { get; set; } = new();

    /// <summary>
    /// Available compression algorithms
    /// </summary>
    public List<string> CompressionAlgorithms { get; set; } = new();

    /// <summary>
    /// Maximum storage capacity in bytes (0 = unlimited)
    /// </summary>
    public long MaxStorageCapacity { get; set; }

    /// <summary>
    /// Current storage usage in bytes
    /// </summary>
    public long CurrentStorageUsage { get; set; }

    /// <summary>
    /// Additional feature flags
    /// </summary>
    public Dictionary<string, bool> FeatureFlags { get; set; } = new();

    /// <summary>
    /// Additional metadata about the instance
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Checks if a specific feature flag is enabled
    /// </summary>
    /// <param name="featureName">Name of the feature</param>
    /// <returns>True if enabled</returns>
    public bool HasFeature(string featureName)
    {
        return FeatureFlags.TryGetValue(featureName, out var enabled) && enabled;
    }
}
