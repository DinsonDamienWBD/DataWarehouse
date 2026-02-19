using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Moonshots;

/// <summary>
/// Operational status of a moonshot feature within the system.
/// Transitions are managed by the registry based on health probe reports
/// and administrative actions.
/// </summary>
public enum MoonshotStatus
{
    /// <summary>Moonshot plugin is not installed or not discovered.</summary>
    NotInstalled = 0,

    /// <summary>Moonshot is installed but administratively disabled.</summary>
    Disabled = 1,

    /// <summary>Moonshot is starting up and not yet ready to serve.</summary>
    Initializing = 2,

    /// <summary>Moonshot is fully operational and passing health checks.</summary>
    Ready = 3,

    /// <summary>Moonshot is operational but with reduced capability or intermittent issues.</summary>
    Degraded = 4,

    /// <summary>Moonshot has failed and cannot serve requests.</summary>
    Faulted = 5
}

/// <summary>
/// Registration record for a moonshot feature in the registry.
/// Captures identity, status, health information, and dependency graph.
/// </summary>
/// <param name="Id">The moonshot identifier.</param>
/// <param name="DisplayName">Human-readable name for the moonshot (e.g. "Universal Tags").</param>
/// <param name="Description">Brief description of the moonshot's purpose.</param>
/// <param name="Status">Current operational status.</param>
/// <param name="LastHealthCheck">When the last health check completed, if any.</param>
/// <param name="LastHealthReport">Most recent health probe report, if any.</param>
/// <param name="DependsOn">List of moonshot IDs this moonshot depends on.</param>
public sealed record MoonshotRegistration(
    MoonshotId Id,
    string DisplayName,
    string Description,
    MoonshotStatus Status,
    DateTimeOffset? LastHealthCheck = null,
    MoonshotHealthReport? LastHealthReport = null,
    IReadOnlyList<MoonshotId>? DependsOn = null)
{
    /// <summary>
    /// List of moonshot IDs this moonshot depends on.
    /// Defaults to empty if not specified.
    /// </summary>
    public IReadOnlyList<MoonshotId> DependsOn { get; init; } = DependsOn ?? Array.Empty<MoonshotId>();
}

/// <summary>
/// Event arguments raised when a moonshot's operational status changes.
/// </summary>
public class MoonshotStatusChangedEventArgs : EventArgs
{
    /// <summary>The moonshot whose status changed.</summary>
    public MoonshotId Id { get; init; }

    /// <summary>The previous status before the change.</summary>
    public MoonshotStatus OldStatus { get; init; }

    /// <summary>The new status after the change.</summary>
    public MoonshotStatus NewStatus { get; init; }

    /// <summary>UTC timestamp of the status change.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Central registry for all moonshot features. Tracks registration, status,
/// health reports, and emits status change events.
///
/// The registry is the single source of truth for which moonshots are installed,
/// their current operational status, and their health. The dashboard provider
/// and orchestrator both consult the registry.
/// </summary>
public interface IMoonshotRegistry
{
    /// <summary>
    /// Registers a moonshot feature with the registry.
    /// Called by each moonshot plugin during initialization.
    /// </summary>
    /// <param name="registration">The registration record for the moonshot.</param>
    void Register(MoonshotRegistration registration);

    /// <summary>
    /// Retrieves the registration for a specific moonshot, or null if not registered.
    /// </summary>
    /// <param name="id">The moonshot to look up.</param>
    /// <returns>The registration record, or null if not found.</returns>
    MoonshotRegistration? Get(MoonshotId id);

    /// <summary>
    /// Returns all registered moonshot features.
    /// </summary>
    /// <returns>List of all registrations.</returns>
    IReadOnlyList<MoonshotRegistration> GetAll();

    /// <summary>
    /// Gets the current status of a specific moonshot.
    /// Returns <see cref="MoonshotStatus.NotInstalled"/> if not registered.
    /// </summary>
    /// <param name="id">The moonshot to query.</param>
    /// <returns>Current operational status.</returns>
    MoonshotStatus GetStatus(MoonshotId id);

    /// <summary>
    /// Updates the operational status of a registered moonshot.
    /// Raises <see cref="StatusChanged"/> if the status actually changed.
    /// </summary>
    /// <param name="id">The moonshot to update.</param>
    /// <param name="status">The new status.</param>
    void UpdateStatus(MoonshotId id, MoonshotStatus status);

    /// <summary>
    /// Updates the health report for a registered moonshot.
    /// Typically called after a health probe completes.
    /// </summary>
    /// <param name="id">The moonshot to update.</param>
    /// <param name="report">The latest health report.</param>
    void UpdateHealthReport(MoonshotId id, MoonshotHealthReport report);

    /// <summary>
    /// Returns all moonshots matching the specified status.
    /// </summary>
    /// <param name="status">The status to filter by.</param>
    /// <returns>List of registrations with the given status.</returns>
    IReadOnlyList<MoonshotRegistration> GetByStatus(MoonshotStatus status);

    /// <summary>
    /// Raised when a moonshot's operational status changes.
    /// Subscribers can use this for monitoring, alerting, or cascade logic.
    /// </summary>
    event EventHandler<MoonshotStatusChangedEventArgs>? StatusChanged;
}
