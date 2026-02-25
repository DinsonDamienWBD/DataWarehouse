namespace DataWarehouse.Plugins.UltimateRAID;

/// <summary>
/// Represents a RAID strategy for data redundancy, performance, and fault tolerance.
/// Extends base RAID functionality with plugin-specific enhancements.
/// </summary>
public interface IRaidStrategy
{
    /// <summary>
    /// Gets the unique identifier for this RAID strategy.
    /// Examples: "raid0", "raid1", "raid5", "raid6", "raid10", "raid50", "raid60"
    /// </summary>
    string StrategyId { get; }

    /// <summary>
    /// Gets the human-readable name of this RAID strategy.
    /// </summary>
    string StrategyName { get; }

    /// <summary>
    /// Gets the RAID level (0-6, 10, 50, 60, etc.).
    /// </summary>
    int RaidLevel { get; }

    /// <summary>
    /// Gets the category of this RAID strategy.
    /// Categories: "standard", "nested", "advanced", "vendor-specific", "software-defined"
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Gets whether this strategy is currently available for use.
    /// May require specific hardware or software capabilities.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the minimum number of disks required for this RAID level.
    /// </summary>
    int MinimumDisks { get; }

    /// <summary>
    /// Gets the maximum number of disk failures that can be tolerated.
    /// </summary>
    int FaultTolerance { get; }

    /// <summary>
    /// Gets whether this strategy supports hot-spare disks.
    /// </summary>
    bool SupportsHotSpare { get; }

    /// <summary>
    /// Gets whether this strategy supports online capacity expansion.
    /// </summary>
    bool SupportsOnlineExpansion { get; }

    /// <summary>
    /// Gets whether this strategy supports hardware acceleration.
    /// </summary>
    bool SupportsHardwareAcceleration { get; }

    /// <summary>
    /// Gets the default stripe size in bytes.
    /// Common values: 64KB, 128KB, 256KB, 512KB, 1MB
    /// </summary>
    int DefaultStripeSizeBytes { get; }

    /// <summary>
    /// Gets the storage efficiency ratio (usable capacity / total capacity).
    /// For RAID 1: 0.5, RAID 5 with 4 disks: 0.75, RAID 6 with 6 disks: 0.67
    /// </summary>
    double StorageEfficiency { get; }

    /// <summary>
    /// Gets the relative read performance multiplier compared to a single disk.
    /// RAID 0: disk count, RAID 1: disk count, RAID 5: disk count - 1
    /// </summary>
    double ReadPerformanceMultiplier { get; }

    /// <summary>
    /// Gets the relative write performance multiplier compared to a single disk.
    /// RAID 0: disk count, RAID 1: 1.0, RAID 5: (disk count - 1) / 2
    /// </summary>
    double WritePerformanceMultiplier { get; }

    /// <summary>
    /// Initializes the RAID array with the specified configuration.
    /// </summary>
    /// <param name="config">RAID configuration parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(RaidConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Writes data to the RAID array with parity/redundancy handling.
    /// </summary>
    /// <param name="logicalBlockAddress">Logical block address to write to.</param>
    /// <param name="data">Data to write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteAsync(long logicalBlockAddress, byte[] data, CancellationToken ct = default);

    /// <summary>
    /// Reads data from the RAID array with automatic reconstruction if needed.
    /// </summary>
    /// <param name="logicalBlockAddress">Logical block address to read from.</param>
    /// <param name="length">Number of bytes to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The requested data.</returns>
    Task<byte[]> ReadAsync(long logicalBlockAddress, int length, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds a failed disk using parity/redundancy data.
    /// </summary>
    /// <param name="failedDiskIndex">Index of the failed disk to rebuild.</param>
    /// <param name="progress">Progress callback (0.0 to 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RebuildAsync(int failedDiskIndex, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Verifies data integrity across the RAID array.
    /// </summary>
    /// <param name="progress">Progress callback (0.0 to 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification results including error count.</returns>
    Task<RaidVerificationResult> VerifyAsync(IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Performs a scrub operation to detect and correct errors.
    /// </summary>
    /// <param name="progress">Progress callback (0.0 to 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scrub results including corrected errors.</returns>
    Task<RaidScrubResult> ScrubAsync(IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Gets the current health status of the RAID array.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health status information.</returns>
    Task<RaidHealthStatus> GetHealthStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets statistics about the RAID array performance and usage.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>RAID statistics.</returns>
    Task<RaidStatistics> GetStatisticsAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds a disk to the RAID array (if supported by the RAID level).
    /// </summary>
    /// <param name="disk">Virtual disk to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddDiskAsync(VirtualDisk disk, CancellationToken ct = default);

    /// <summary>
    /// Removes a disk from the RAID array (if supported by the RAID level).
    /// </summary>
    /// <param name="diskIndex">Index of the disk to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveDiskAsync(int diskIndex, CancellationToken ct = default);

    /// <summary>
    /// Replaces a failed disk with a new one and initiates rebuild.
    /// </summary>
    /// <param name="failedDiskIndex">Index of the failed disk.</param>
    /// <param name="replacementDisk">New disk to use as replacement.</param>
    /// <param name="progress">Progress callback (0.0 to 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReplaceDiskAsync(int failedDiskIndex, VirtualDisk replacementDisk,
        IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Performs a health check on the RAID array.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if healthy, false otherwise.</returns>
    Task<bool> HealthCheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Disposes resources used by the RAID strategy.
    /// </summary>
    void Dispose();
}

/// <summary>
/// Configuration parameters for initializing a RAID array.
/// </summary>
public sealed class RaidConfiguration
{
    /// <summary>
    /// The disks to use in the RAID array.
    /// </summary>
    public required List<VirtualDisk> Disks { get; init; }

    /// <summary>
    /// Stripe size in bytes.
    /// Common values: 65536 (64KB), 131072 (128KB), 262144 (256KB), 524288 (512KB), 1048576 (1MB)
    /// </summary>
    public int StripeSizeBytes { get; init; } = 131072; // 128KB default

    /// <summary>
    /// Hot spare disks for automatic rebuild.
    /// </summary>
    public List<VirtualDisk>? HotSpares { get; init; }

    /// <summary>
    /// Whether to enable hardware acceleration if available.
    /// </summary>
    public bool EnableHardwareAcceleration { get; init; } = true;

    /// <summary>
    /// Whether to enable write-back caching.
    /// </summary>
    public bool EnableWriteBackCache { get; init; } = false;

    /// <summary>
    /// Write-back cache size in bytes.
    /// </summary>
    public long WriteBackCacheSizeBytes { get; init; } = 16 * 1024 * 1024; // 16MB default

    /// <summary>
    /// Whether to enable read-ahead caching.
    /// </summary>
    public bool EnableReadAheadCache { get; init; } = true;

    /// <summary>
    /// Read-ahead cache size in bytes.
    /// </summary>
    public long ReadAheadCacheSizeBytes { get; init; } = 64 * 1024 * 1024; // 64MB default

    /// <summary>
    /// Rebuild priority (0-100, higher = faster rebuild but more performance impact).
    /// </summary>
    public int RebuildPriority { get; init; } = 50;

    /// <summary>
    /// Scrub schedule in cron format (null = manual only).
    /// </summary>
    public string? ScrubSchedule { get; init; }

    /// <summary>
    /// Custom metadata for this RAID array.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Represents a virtual disk in the RAID array.
/// Can represent physical disks, cloud storage, or simulated storage for testing.
/// </summary>
public sealed class VirtualDisk
{
    /// <summary>
    /// Unique identifier for this disk.
    /// </summary>
    public required string DiskId { get; init; }

    /// <summary>
    /// Human-readable name for this disk.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Capacity in bytes.
    /// </summary>
    public required long CapacityBytes { get; init; }

    /// <summary>
    /// Current health status of the disk.
    /// </summary>
    public DiskHealthStatus HealthStatus { get; set; } = DiskHealthStatus.Healthy;

    /// <summary>
    /// SMART attributes for this disk (if available).
    /// </summary>
    public SmartAttributes? SmartData { get; set; }

    /// <summary>
    /// I/O operations delegate for reading/writing to this disk.
    /// </summary>
    public required IDiskIO DiskIO { get; init; }

    /// <summary>
    /// Whether this disk supports TRIM/UNMAP commands.
    /// </summary>
    public bool SupportsTrim { get; init; } = false;

    /// <summary>
    /// Disk metadata.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Interface for disk I/O operations.
/// Abstracts physical disk access for testing and flexibility.
/// </summary>
public interface IDiskIO
{
    /// <summary>
    /// Reads data from the disk.
    /// </summary>
    Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default);

    /// <summary>
    /// Writes data to the disk.
    /// </summary>
    Task WriteAsync(long offset, byte[] data, CancellationToken ct = default);

    /// <summary>
    /// Flushes any pending writes to the disk.
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the current I/O statistics for this disk.
    /// </summary>
    DiskIOStatistics GetStatistics();
}

/// <summary>
/// Disk I/O statistics.
/// </summary>
public sealed class DiskIOStatistics
{
    public long TotalReads { get; set; }
    public long TotalWrites { get; set; }
    public long BytesRead { get; set; }
    public long BytesWritten { get; set; }
    public long ReadErrors { get; set; }
    public long WriteErrors { get; set; }
    public double AverageReadLatencyMs { get; set; }
    public double AverageWriteLatencyMs { get; set; }
}

/// <summary>
/// Disk health status enumeration.
/// </summary>
public enum DiskHealthStatus
{
    Healthy,
    Warning,
    Failed,
    Rebuilding,
    Missing
}

/// <summary>
/// SMART (Self-Monitoring, Analysis and Reporting Technology) attributes.
/// </summary>
public sealed class SmartAttributes
{
    public int Temperature { get; set; }
    public long PowerOnHours { get; set; }
    public int ReallocatedSectorCount { get; set; }
    public int PendingSectorCount { get; set; }
    public int UncorrectableErrorCount { get; set; }
    public int HealthPercentage { get; set; } = 100;
    public Dictionary<string, object>? RawAttributes { get; set; }
}

/// <summary>
/// RAID verification result.
/// </summary>
public sealed class RaidVerificationResult
{
    public bool IsHealthy { get; set; }
    public long TotalBlocks { get; set; }
    public long VerifiedBlocks { get; set; }
    public long ErrorCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// RAID scrub result.
/// </summary>
public sealed class RaidScrubResult
{
    public bool IsHealthy { get; set; }
    public long TotalBlocks { get; set; }
    public long ScrubbedBlocks { get; set; }
    public long ErrorsDetected { get; set; }
    public long ErrorsCorrected { get; set; }
    public long ErrorsUncorrectable { get; set; }
    public List<string> Details { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// RAID health status information.
/// </summary>
public sealed class RaidHealthStatus
{
    public RaidState State { get; set; }
    public int HealthyDisks { get; set; }
    public int FailedDisks { get; set; }
    public int RebuildingDisks { get; set; }
    public double RebuildProgress { get; set; }
    public TimeSpan? EstimatedRebuildTime { get; set; }
    public long UsableCapacityBytes { get; set; }
    public long UsedBytes { get; set; }
    public List<DiskStatus> DiskStatuses { get; set; } = new();
    public DateTime LastScrubTime { get; set; }
    public DateTime? NextScrubTime { get; set; }
}

/// <summary>
/// RAID array state.
/// </summary>
public enum RaidState
{
    Optimal,
    Degraded,
    Failed,
    Rebuilding,
    Verifying,
    Scrubbing
}

/// <summary>
/// Individual disk status within RAID array.
/// </summary>
public sealed class DiskStatus
{
    public required string DiskId { get; init; }
    public DiskHealthStatus Health { get; set; }
    public long ReadErrors { get; set; }
    public long WriteErrors { get; set; }
    public double TemperatureCelsius { get; set; }
    public SmartAttributes? SmartData { get; set; }
}

/// <summary>
/// RAID array statistics.
/// </summary>
public sealed class RaidStatistics
{
    public long TotalReads { get; set; }
    public long TotalWrites { get; set; }
    public long BytesRead { get; set; }
    public long BytesWritten { get; set; }
    public long ParityCalculations { get; set; }
    public long ReconstructionOperations { get; set; }
    public double AverageReadLatencyMs { get; set; }
    public double AverageWriteLatencyMs { get; set; }
    public double ReadThroughputMBps { get; set; }
    public double WriteThroughputMBps { get; set; }
    public DateTime StatsSince { get; set; }
    public TimeSpan Uptime { get; set; }
}
