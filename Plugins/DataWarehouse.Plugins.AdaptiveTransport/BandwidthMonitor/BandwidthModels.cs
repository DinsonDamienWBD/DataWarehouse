namespace DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor;

/// <summary>
/// Classification of network link types based on performance characteristics.
/// </summary>
public enum LinkClass
{
    /// <summary>Fiber optic connection: &gt;100 Mbps, &lt;5ms latency.</summary>
    Fiber,

    /// <summary>Broadband connection: 10-100 Mbps, &lt;50ms latency.</summary>
    Broadband,

    /// <summary>4G mobile connection: 1-50 Mbps, 30-100ms latency.</summary>
    Mobile4G,

    /// <summary>5G mobile connection: 50-500 Mbps, 10-30ms latency.</summary>
    Mobile5G,

    /// <summary>Satellite connection: any bandwidth, &gt;500ms latency.</summary>
    Satellite,

    /// <summary>Intermittent connection: &gt;5% packet loss.</summary>
    Intermittent,

    /// <summary>Connection type cannot be determined.</summary>
    Unknown
}

/// <summary>
/// Snapshot of bandwidth and network quality measurements at a specific point in time.
/// </summary>
/// <param name="Timestamp">When the measurement was taken (UTC).</param>
/// <param name="DownloadBytesPerSecond">Measured download throughput in bytes per second.</param>
/// <param name="UploadBytesPerSecond">Measured upload throughput in bytes per second.</param>
/// <param name="LatencyMs">Round-trip time in milliseconds.</param>
/// <param name="JitterMs">Latency variation in milliseconds.</param>
/// <param name="PacketLossPercent">Packet loss percentage (0-100).</param>
public sealed record BandwidthMeasurement(
    DateTimeOffset Timestamp,
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond,
    double LatencyMs,
    double JitterMs,
    double PacketLossPercent);

/// <summary>
/// Result of classifying a network link based on measured characteristics.
/// </summary>
/// <param name="Class">The classified link type.</param>
/// <param name="Confidence">Confidence in the classification (0.0-1.0).</param>
/// <param name="MeasuredBandwidth">The measurement that led to this classification.</param>
/// <param name="ClassifiedAt">When the classification was performed (UTC).</param>
public sealed record LinkClassification(
    LinkClass Class,
    double Confidence,
    BandwidthMeasurement MeasuredBandwidth,
    DateTimeOffset ClassifiedAt);

/// <summary>
/// Synchronization mode for data transfer operations.
/// </summary>
public enum SyncMode
{
    /// <summary>Full replication of all data.</summary>
    FullReplication,

    /// <summary>Only transfer changes/deltas.</summary>
    DeltaOnly,

    /// <summary>Queue locally and forward when possible.</summary>
    StoreAndForward,

    /// <summary>Synchronization is paused.</summary>
    Paused
}

/// <summary>
/// Retry policy for failed transfer operations.
/// </summary>
/// <param name="MaxRetries">Maximum number of retry attempts.</param>
/// <param name="InitialBackoffMs">Initial backoff delay in milliseconds.</param>
/// <param name="MaxBackoffMs">Maximum backoff delay in milliseconds.</param>
/// <param name="BackoffMultiplier">Multiplier for exponential backoff.</param>
public sealed record RetryPolicy(
    int MaxRetries,
    int InitialBackoffMs,
    int MaxBackoffMs,
    double BackoffMultiplier);

/// <summary>
/// Parameters that control synchronization behavior based on network conditions.
/// </summary>
/// <param name="Mode">The synchronization mode to use.</param>
/// <param name="MaxConcurrency">Maximum number of concurrent transfers.</param>
/// <param name="ChunkSizeBytes">Size of individual transfer chunks in bytes.</param>
/// <param name="RetryPolicy">Policy for retrying failed transfers.</param>
/// <param name="CompressionEnabled">Whether to enable data compression.</param>
public sealed record SyncParameters(
    SyncMode Mode,
    int MaxConcurrency,
    int ChunkSizeBytes,
    RetryPolicy RetryPolicy,
    bool CompressionEnabled);

/// <summary>
/// Priority level for synchronization operations.
/// </summary>
public enum SyncPriority
{
    /// <summary>Background operations that can be delayed.</summary>
    Background,

    /// <summary>Low priority, non-urgent operations.</summary>
    Low,

    /// <summary>Normal priority operations.</summary>
    Normal,

    /// <summary>High priority, time-sensitive operations.</summary>
    High,

    /// <summary>Critical operations requiring immediate attention.</summary>
    Critical
}

/// <summary>
/// Represents a pending synchronization operation with associated metadata.
/// </summary>
/// <param name="Id">Unique identifier for this operation.</param>
/// <param name="Priority">Priority level of this operation.</param>
/// <param name="DataSizeBytes">Size of data to synchronize in bytes.</param>
/// <param name="Source">Source endpoint or path.</param>
/// <param name="Destination">Destination endpoint or path.</param>
/// <param name="CreatedAt">When the operation was created (UTC).</param>
public sealed record SyncOperation(
    string Id,
    SyncPriority Priority,
    long DataSizeBytes,
    string Source,
    string Destination,
    DateTimeOffset CreatedAt);
