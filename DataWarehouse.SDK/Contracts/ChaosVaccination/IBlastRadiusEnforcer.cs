using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.ChaosVaccination
{
    /// <summary>
    /// Contract for enforcing blast radius limits during chaos experiments.
    /// Creates isolation zones that contain fault effects within defined boundaries.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public interface IBlastRadiusEnforcer
    {
        /// <summary>
        /// Creates an isolation zone with the specified policy for containing experiment effects.
        /// </summary>
        /// <param name="policy">The blast radius policy to enforce.</param>
        /// <param name="targetPlugins">The plugin IDs to isolate within the zone.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The created isolation zone.</returns>
        Task<IsolationZone> CreateIsolationZoneAsync(BlastRadiusPolicy policy, string[] targetPlugins, CancellationToken ct = default);

        /// <summary>
        /// Monitors and enforces the blast radius policy for an active isolation zone.
        /// Returns the containment result indicating whether the fault was successfully contained.
        /// </summary>
        /// <param name="zoneId">The ID of the isolation zone to enforce.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The result of the containment enforcement.</returns>
        Task<FailureContainmentResult> EnforceAsync(string zoneId, CancellationToken ct = default);

        /// <summary>
        /// Releases an isolation zone, removing all containment restrictions.
        /// </summary>
        /// <param name="zoneId">The ID of the isolation zone to release.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        Task ReleaseZoneAsync(string zoneId, CancellationToken ct = default);

        /// <summary>
        /// Gets all currently active isolation zones.
        /// </summary>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A list of all active isolation zones.</returns>
        Task<IReadOnlyList<IsolationZone>> GetActiveZonesAsync(CancellationToken ct = default);

        /// <summary>
        /// Raised when a blast radius breach is detected during enforcement.
        /// </summary>
        event Action<BlastRadiusBreachEvent>? OnBreachDetected;
    }

    /// <summary>
    /// Strategy used to isolate fault effects within an isolation zone.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public enum IsolationStrategy
    {
        /// <summary>Uses bulkhead isolation to limit concurrent operations.</summary>
        Bulkhead,

        /// <summary>Uses circuit breakers to halt cascading failures.</summary>
        CircuitBreaker,

        /// <summary>Isolates the experiment in a separate process.</summary>
        ProcessIsolation,

        /// <summary>Uses network-level partitioning to contain effects.</summary>
        NetworkPartition
    }

    /// <summary>
    /// Policy defining blast radius limits and isolation behavior for a chaos experiment.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record BlastRadiusPolicy
    {
        /// <summary>
        /// The maximum blast radius level allowed.
        /// </summary>
        public required BlastRadiusLevel MaxLevel { get; init; }

        /// <summary>
        /// Maximum number of plugins that can be affected.
        /// </summary>
        public required int MaxAffectedPlugins { get; init; }

        /// <summary>
        /// Maximum number of nodes that can be affected.
        /// </summary>
        public required int MaxAffectedNodes { get; init; }

        /// <summary>
        /// Maximum duration in milliseconds for the experiment under this policy.
        /// </summary>
        public required long MaxDurationMs { get; init; }

        /// <summary>
        /// Whether to automatically abort the experiment if the blast radius is breached. Default: true.
        /// </summary>
        public bool AutoAbortOnBreach { get; init; } = true;

        /// <summary>
        /// The isolation strategy to use for containing fault effects.
        /// </summary>
        public required IsolationStrategy IsolationStrategy { get; init; }
    }

    /// <summary>
    /// Represents an active isolation zone containing fault effects.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record IsolationZone
    {
        /// <summary>
        /// Unique identifier for the isolation zone.
        /// </summary>
        public required string ZoneId { get; init; }

        /// <summary>
        /// Plugin IDs contained within this zone.
        /// </summary>
        public string[] ContainedPlugins { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Node IDs contained within this zone.
        /// </summary>
        public string[] ContainedNodes { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The blast radius policy being enforced for this zone.
        /// </summary>
        public required BlastRadiusPolicy Policy { get; init; }

        /// <summary>
        /// Whether the isolation zone is currently active.
        /// </summary>
        public required bool IsActive { get; init; }
    }

    /// <summary>
    /// Result of a failure containment enforcement action.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record FailureContainmentResult
    {
        /// <summary>
        /// Whether the failure was successfully contained within the blast radius policy.
        /// </summary>
        public required bool Contained { get; init; }

        /// <summary>
        /// The actual blast radius observed.
        /// </summary>
        public required BlastRadiusLevel ActualRadius { get; init; }

        /// <summary>
        /// Plugin IDs that were affected beyond the allowed blast radius.
        /// </summary>
        public string[] BreachedPlugins { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Node IDs that were affected beyond the allowed blast radius.
        /// </summary>
        public string[] BreachedNodes { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Descriptions of containment actions that were taken.
        /// </summary>
        public string[] ContainmentActions { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Event raised when a blast radius breach is detected.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record BlastRadiusBreachEvent
    {
        /// <summary>
        /// The isolation zone where the breach occurred.
        /// </summary>
        public required string ZoneId { get; init; }

        /// <summary>
        /// The policy that was breached.
        /// </summary>
        public required BlastRadiusPolicy Policy { get; init; }

        /// <summary>
        /// The actual blast radius at the time of breach.
        /// </summary>
        public required BlastRadiusLevel ActualRadius { get; init; }

        /// <summary>
        /// When the breach was detected.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }
    }
}
