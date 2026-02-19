using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;

namespace DataWarehouse.Plugins.ChaosVaccination.Engine.FaultInjectors;

/// <summary>
/// Strategy interface for fault injectors. Each fault type (network partition, disk failure,
/// node crash, latency spike, memory pressure) has a dedicated injector implementing this interface.
/// The chaos injection engine selects the appropriate injector based on the experiment's fault type.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos injection engine")]
public interface IFaultInjector
{
    /// <summary>
    /// The fault type this injector handles.
    /// </summary>
    FaultType SupportedFaultType { get; }

    /// <summary>
    /// Human-readable name for this injector.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Injects the fault defined by the experiment and returns the injection result.
    /// Implementations must never throw; return <see cref="FaultInjectionResult"/> with Success=false on errors.
    /// </summary>
    /// <param name="experiment">The chaos experiment defining the fault to inject.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The result of the fault injection.</returns>
    Task<FaultInjectionResult> InjectAsync(ChaosExperiment experiment, CancellationToken ct);

    /// <summary>
    /// Cleans up / undoes an injected fault for the specified experiment.
    /// Called after the experiment completes or is aborted to restore normal operation.
    /// </summary>
    /// <param name="experimentId">The ID of the experiment whose fault should be cleaned up.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task CleanupAsync(string experimentId, CancellationToken ct);

    /// <summary>
    /// Pre-checks whether the fault can be safely injected for the given experiment.
    /// Returns false if preconditions are not met (e.g., target not reachable, resources insufficient).
    /// </summary>
    /// <param name="experiment">The chaos experiment to validate.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>True if the fault can be injected; false otherwise.</returns>
    Task<bool> CanInjectAsync(ChaosExperiment experiment, CancellationToken ct);
}

/// <summary>
/// Result of a fault injection operation, capturing success state, affected components,
/// and quantitative metrics.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos injection engine")]
public record FaultInjectionResult
{
    /// <summary>
    /// Whether the fault was successfully injected.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The fault signature derived from this injection, if one was generated.
    /// Used by the immune response system to recognize this fault pattern.
    /// </summary>
    public FaultSignature? FaultSignature { get; init; }

    /// <summary>
    /// IDs of components that were affected by the injected fault.
    /// </summary>
    public string[] AffectedComponents { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Quantitative metrics collected during the fault injection.
    /// Keys are metric names (e.g., "partitionDurationMs", "affectedEndpoints"),
    /// values are numeric measurements.
    /// </summary>
    public Dictionary<string, double> Metrics { get; init; } = new();

    /// <summary>
    /// Whether cleanup is required after this injection.
    /// If true, <see cref="IFaultInjector.CleanupAsync"/> must be called to restore normal operation.
    /// </summary>
    public bool CleanupRequired { get; init; }

    /// <summary>
    /// Optional error message when Success is false.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
