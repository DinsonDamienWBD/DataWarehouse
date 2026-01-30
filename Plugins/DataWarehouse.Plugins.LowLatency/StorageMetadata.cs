namespace DataWarehouse.Plugins.LowLatency;

/// <summary>
/// Metadata for stored items in low-latency storage.
/// Provides information about storage artifacts without requiring data retrieval.
/// </summary>
public class StorageMetadata
{
    /// <summary>
    /// Storage key for this item.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Size in bytes of the stored data.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Timestamp when the item was last modified.
    /// </summary>
    public DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// Content type of the stored data (MIME type).
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// ETag or version identifier for concurrency control.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>
    /// Custom metadata tags associated with this item.
    /// </summary>
    public Dictionary<string, string>? Tags { get; init; }
}
