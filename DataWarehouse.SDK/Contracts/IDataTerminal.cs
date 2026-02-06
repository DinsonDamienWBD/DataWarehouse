using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Terminal stage contract for data sinks (storage backends).
/// Unlike IDataTransformation which passes data through, terminals consume and persist data.
/// Supports fan-out patterns where data flows to multiple terminals in parallel.
/// </summary>
public interface IDataTerminal : IPlugin
{
    /// <summary>
    /// Subcategory for grouping (e.g., "Cloud", "Local", "Database").
    /// </summary>
    string SubCategory { get; }

    /// <summary>
    /// Quality/priority level for terminal selection.
    /// </summary>
    int QualityLevel { get; }

    /// <summary>
    /// Terminal stage ID for routing (e.g., "primary", "metadata", "worm", "index", "replica").
    /// </summary>
    string TerminalId { get; }

    /// <summary>
    /// Capabilities of this terminal (tier, durability, features).
    /// </summary>
    TerminalCapabilities Capabilities { get; }

    /// <summary>
    /// Write data to this terminal. Does not return data - it's a sink.
    /// </summary>
    Task WriteAsync(Stream input, TerminalContext context, CancellationToken ct = default);

    /// <summary>
    /// Read data back from this terminal (reverse of Write).
    /// </summary>
    Task<Stream> ReadAsync(TerminalContext context, CancellationToken ct = default);

    /// <summary>
    /// Delete data from this terminal.
    /// </summary>
    Task<bool> DeleteAsync(TerminalContext context, CancellationToken ct = default);

    /// <summary>
    /// Check if data exists in this terminal.
    /// </summary>
    Task<bool> ExistsAsync(TerminalContext context, CancellationToken ct = default);
}

/// <summary>
/// Context passed to terminal operations.
/// </summary>
public record TerminalContext
{
    /// <summary>Unique blob/object identifier.</summary>
    public string BlobId { get; init; } = string.Empty;

    /// <summary>Storage path within the terminal.</summary>
    public string StoragePath { get; init; } = string.Empty;

    /// <summary>Terminal-specific parameters.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>Kernel context for service access.</summary>
    public IKernelContext? KernelContext { get; init; }

    /// <summary>Manifest being written (for metadata terminals).</summary>
    public object? Manifest { get; init; }

    /// <summary>Content type hint.</summary>
    public string? ContentType { get; init; }

    /// <summary>Expected content length for pre-allocation.</summary>
    public long? ContentLength { get; init; }

    /// <summary>Tags/labels for the stored object.</summary>
    public Dictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// Capabilities of a terminal stage.
/// </summary>
public record TerminalCapabilities
{
    /// <summary>Storage tier (Hot, Warm, Cold, Archive).</summary>
    public StorageTier Tier { get; init; } = StorageTier.Hot;

    /// <summary>Whether the terminal supports object versioning.</summary>
    public bool SupportsVersioning { get; init; }

    /// <summary>Whether the terminal supports WORM (Write Once Read Many) compliance.</summary>
    public bool SupportsWorm { get; init; }

    /// <summary>Whether the terminal uses content-addressable storage.</summary>
    public bool IsContentAddressable { get; init; }

    /// <summary>Whether writes can be done in parallel with other terminals.</summary>
    public bool SupportsParallelWrite { get; init; } = true;

    /// <summary>Expected latency for operations.</summary>
    public TimeSpan? ExpectedLatency { get; init; }

    /// <summary>Durability guarantee (e.g., "11 nines").</summary>
    public string? DurabilityGuarantee { get; init; }

    /// <summary>Maximum object size supported.</summary>
    public long? MaxObjectSize { get; init; }

    /// <summary>Whether terminal supports streaming writes without buffering.</summary>
    public bool SupportsStreaming { get; init; }
}

/// <summary>
/// Storage tier classification.
/// </summary>
public enum StorageTier
{
    /// <summary>Frequently accessed, lowest latency.</summary>
    Hot = 0,

    /// <summary>Infrequently accessed, moderate latency.</summary>
    Warm = 1,

    /// <summary>Rarely accessed, higher latency, lower cost.</summary>
    Cold = 2,

    /// <summary>Long-term retention, highest latency, lowest cost.</summary>
    Archive = 3,

    /// <summary>In-memory only, volatile.</summary>
    Memory = 4
}
