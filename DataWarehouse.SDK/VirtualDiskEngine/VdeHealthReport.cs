using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine;

/// <summary>
/// Health status report for the Virtual Disk Engine.
/// Aggregates metrics from all subsystems: allocator, WAL, checksums, snapshots.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE engine facade (VDE-07)")]
public sealed record VdeHealthReport
{
    /// <summary>
    /// Total number of blocks in the container.
    /// </summary>
    public required long TotalBlocks { get; init; }

    /// <summary>
    /// Number of free blocks available for allocation.
    /// </summary>
    public required long FreeBlocks { get; init; }

    /// <summary>
    /// Number of blocks currently in use.
    /// </summary>
    public required long UsedBlocks { get; init; }

    /// <summary>
    /// Percentage of capacity used (0.0 to 100.0).
    /// </summary>
    public double UsagePercent => TotalBlocks > 0 ? (UsedBlocks * 100.0 / TotalBlocks) : 0.0;

    /// <summary>
    /// Total number of inodes (allocated + free).
    /// </summary>
    public required long TotalInodes { get; init; }

    /// <summary>
    /// Number of inodes currently allocated.
    /// </summary>
    public required long AllocatedInodes { get; init; }

    /// <summary>
    /// Write-ahead log utilization as a percentage (0.0 to 100.0).
    /// </summary>
    public required double WalUtilizationPercent { get; init; }

    /// <summary>
    /// Number of checksum errors detected during integrity checks.
    /// </summary>
    public required long ChecksumErrorCount { get; init; }

    /// <summary>
    /// Number of active snapshots.
    /// </summary>
    public required int SnapshotCount { get; init; }

    /// <summary>
    /// Overall health status based on capacity, WAL utilization, and checksum errors.
    /// </summary>
    public required string HealthStatus { get; init; }

    /// <summary>
    /// Timestamp when this health report was generated (UTC).
    /// </summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>
    /// Block size in bytes for this VDE instance.
    /// </summary>
    public required int BlockSize { get; init; }

    /// <summary>
    /// Converts this VDE health report to the SDK's StorageHealthInfo format
    /// for compatibility with IStorageStrategy.
    /// </summary>
    /// <returns>StorageHealthInfo representing this VDE's health.</returns>
    public StorageHealthInfo ToStorageHealthInfo()
    {
        var status = HealthStatus switch
        {
            "Healthy" => Contracts.Storage.HealthStatus.Healthy,
            "Degraded" => Contracts.Storage.HealthStatus.Degraded,
            "Critical" => Contracts.Storage.HealthStatus.Unhealthy,
            _ => Contracts.Storage.HealthStatus.Unknown
        };

        return new StorageHealthInfo
        {
            Status = status,
            LatencyMs = 0, // Will be filled by the base class metrics
            AvailableCapacity = FreeBlocks * BlockSize,
            TotalCapacity = TotalBlocks * BlockSize,
            UsedCapacity = UsedBlocks * BlockSize,
            Message = $"WAL: {WalUtilizationPercent:F1}%, Snapshots: {SnapshotCount}, Checksum errors: {ChecksumErrorCount}",
            CheckedAt = GeneratedAtUtc.UtcDateTime
        };
    }

    /// <summary>
    /// Determines overall health status based on metrics.
    /// </summary>
    /// <param name="totalBlocks">Total blocks.</param>
    /// <param name="freeBlocks">Free blocks.</param>
    /// <param name="walUtilization">WAL utilization percentage.</param>
    /// <param name="checksumErrorCount">Number of checksum errors.</param>
    /// <returns>Health status string: "Healthy", "Degraded", or "Critical".</returns>
    public static string DetermineHealthStatus(long totalBlocks, long freeBlocks, double walUtilization, long checksumErrorCount)
    {
        // Critical: checksum errors, or capacity > 95%, or WAL > 90%
        if (checksumErrorCount > 0)
        {
            return "Critical";
        }

        double usagePercent = totalBlocks > 0 ? ((totalBlocks - freeBlocks) * 100.0 / totalBlocks) : 0.0;

        if (usagePercent > 95.0 || walUtilization > 90.0)
        {
            return "Critical";
        }

        // Degraded: capacity > 80%, or WAL > 75%
        if (usagePercent > 80.0 || walUtilization > 75.0)
        {
            return "Degraded";
        }

        return "Healthy";
    }
}
