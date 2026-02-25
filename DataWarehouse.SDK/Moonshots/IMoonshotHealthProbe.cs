using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Moonshots;

/// <summary>
/// Readiness level of a moonshot or one of its sub-components.
/// Used by health probes to report fine-grained operational state.
/// </summary>
public enum MoonshotReadiness
{
    /// <summary>Fully operational and ready to serve requests.</summary>
    Ready = 0,

    /// <summary>Not ready to serve requests (startup incomplete or dependency missing).</summary>
    NotReady = 1,

    /// <summary>Operational but with reduced capability or intermittent issues.</summary>
    Degraded = 2,

    /// <summary>Readiness cannot be determined (probe failed or timed out).</summary>
    Unknown = 3
}

/// <summary>
/// Health status of an individual component within a moonshot.
/// Moonshots are composed of sub-components (e.g. storage backend, index service)
/// each of which can report independent health.
/// </summary>
/// <param name="Name">Component name (e.g. "TagIndex", "ComplianceStore").</param>
/// <param name="Readiness">Current readiness level of this component.</param>
/// <param name="Message">Optional human-readable status message.</param>
/// <param name="Details">Optional key-value diagnostic details.</param>
public sealed record MoonshotComponentHealth(
    string Name,
    MoonshotReadiness Readiness,
    string? Message = null,
    IReadOnlyDictionary<string, string>? Details = null);

/// <summary>
/// Comprehensive health report for a single moonshot, including per-component breakdown.
/// Produced by <see cref="IMoonshotHealthProbe.CheckHealthAsync"/> and stored
/// in the <see cref="IMoonshotRegistry"/> for dashboard consumption.
/// </summary>
/// <param name="Id">The moonshot this report covers.</param>
/// <param name="Readiness">Overall readiness of the moonshot.</param>
/// <param name="Summary">Human-readable summary of health status.</param>
/// <param name="Components">Per-component health breakdown.</param>
/// <param name="CheckedAt">UTC timestamp when the check was performed.</param>
/// <param name="CheckDuration">How long the health check took to execute.</param>
public sealed record MoonshotHealthReport(
    MoonshotId Id,
    MoonshotReadiness Readiness,
    string Summary,
    IReadOnlyDictionary<string, MoonshotComponentHealth> Components,
    DateTimeOffset CheckedAt,
    TimeSpan CheckDuration);

/// <summary>
/// Per-moonshot health probe that performs independent readiness checks.
/// Each moonshot plugin implements this interface to report its operational health
/// to the central registry and dashboard.
///
/// Health probes are invoked periodically by the moonshot health monitor.
/// The <see cref="HealthCheckInterval"/> property suggests the check frequency,
/// though the monitor may adjust based on system load.
/// </summary>
public interface IMoonshotHealthProbe
{
    /// <summary>
    /// The moonshot this probe checks.
    /// </summary>
    MoonshotId MoonshotId { get; }

    /// <summary>
    /// Performs a health check and returns a comprehensive report.
    /// Implementations should check all critical sub-components and
    /// aggregate into an overall readiness assessment.
    /// </summary>
    /// <param name="ct">Cancellation token (health checks should be fast and respect cancellation).</param>
    /// <returns>A health report for this moonshot.</returns>
    Task<MoonshotHealthReport> CheckHealthAsync(CancellationToken ct);

    /// <summary>
    /// Suggested interval between health checks for this moonshot.
    /// The health monitor uses this as a hint but may adjust based on
    /// system conditions (e.g. more frequent checks when degraded).
    /// </summary>
    TimeSpan HealthCheckInterval { get; }
}
