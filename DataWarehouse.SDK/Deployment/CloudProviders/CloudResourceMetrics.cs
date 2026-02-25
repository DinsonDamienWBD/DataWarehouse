using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Deployment.CloudProviders;

/// <summary>
/// Cloud resource metrics for auto-scaling decisions.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Cloud resource metrics (ENV-04)")]
public sealed record CloudResourceMetrics
{
    /// <summary>Gets the resource ID (instance ID, volume ID, etc.).</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets CPU utilization percentage (0-100).</summary>
    public double CpuUtilizationPercent { get; init; }

    /// <summary>Gets memory utilization percentage (0-100).</summary>
    public double MemoryUtilizationPercent { get; init; }

    /// <summary>Gets storage utilization percentage (0-100).</summary>
    public double StorageUtilizationPercent { get; init; }

    /// <summary>Gets network inbound bytes per second.</summary>
    public long NetworkInBytesPerSec { get; init; }

    /// <summary>Gets network outbound bytes per second.</summary>
    public long NetworkOutBytesPerSec { get; init; }

    /// <summary>Gets disk read bytes per second.</summary>
    public long DiskReadBytesPerSec { get; init; }

    /// <summary>Gets disk write bytes per second.</summary>
    public long DiskWriteBytesPerSec { get; init; }

    /// <summary>Gets the metric timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }
}
