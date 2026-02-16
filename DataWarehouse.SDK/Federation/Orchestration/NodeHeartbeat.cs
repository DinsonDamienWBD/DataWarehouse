using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Federation.Orchestration;

/// <summary>
/// Represents a periodic heartbeat from a storage node with health and capacity metrics.
/// </summary>
/// <remarks>
/// <para>
/// NodeHeartbeat is sent periodically (typically every 10-30 seconds) to update the
/// orchestrator with current node health, available capacity, and performance metrics.
/// </para>
/// <para>
/// <strong>Health Score:</strong> Ranges from 0.0 (unhealthy/offline) to 1.0 (fully healthy).
/// The health score may reflect CPU usage, memory pressure, disk I/O latency, or network
/// congestion. Routing logic avoids nodes with low health scores.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Node heartbeat with health metrics")]
public sealed record NodeHeartbeat
{
    /// <summary>
    /// Gets the unique identifier for the node sending the heartbeat.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Gets the available free storage capacity in bytes.
    /// </summary>
    public required long FreeBytes { get; init; }

    /// <summary>
    /// Gets the total storage capacity in bytes.
    /// </summary>
    public required long TotalBytes { get; init; }

    /// <summary>
    /// Gets the health score for the node (0.0 to 1.0).
    /// </summary>
    /// <remarks>
    /// A value of 1.0 indicates fully healthy. Values below 0.5 may indicate degraded
    /// performance. Values below 0.1 typically exclude the node from routing.
    /// </remarks>
    public required double HealthScore { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when this heartbeat was generated.
    /// </summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Gets the number of active requests currently being processed by the node.
    /// </summary>
    public int ActiveRequests { get; init; }

    /// <summary>
    /// Gets the average request latency for the node.
    /// </summary>
    public TimeSpan AverageLatency { get; init; }
}
