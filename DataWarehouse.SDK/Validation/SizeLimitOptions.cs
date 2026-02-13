namespace DataWarehouse.SDK.Validation;

/// <summary>
/// Configurable size limits for all incoming data at SDK boundaries (VALID-03).
/// </summary>
public sealed class SizeLimitOptions
{
    /// <summary>Maximum size of a single message payload in bytes. Default: 10MB.</summary>
    public long MaxMessageSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Maximum size of a knowledge object in bytes. Default: 1MB.</summary>
    public long MaxKnowledgeObjectSizeBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>Maximum size of a capability registration payload in bytes. Default: 256KB.</summary>
    public long MaxCapabilityPayloadSizeBytes { get; set; } = 256 * 1024;

    /// <summary>Maximum string length for general text inputs. Default: 10,000 chars.</summary>
    public int MaxStringLength { get; set; } = 10_000;

    /// <summary>Maximum number of items in a single collection input. Default: 10,000.</summary>
    public int MaxCollectionCount { get; set; } = 10_000;

    /// <summary>Maximum storage key length. Default: 1,024 chars.</summary>
    public int MaxStorageKeyLength { get; set; } = 1_024;

    /// <summary>Maximum metadata entries per storage object. Default: 100.</summary>
    public int MaxMetadataEntries { get; set; } = 100;

    /// <summary>Maximum size of a single storage object in bytes. Default: 5GB.</summary>
    public long MaxStorageObjectSizeBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Default singleton instance with standard limits.</summary>
    public static SizeLimitOptions Default { get; } = new();
}
