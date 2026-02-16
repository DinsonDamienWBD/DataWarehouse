using DataWarehouse.SDK.Contracts;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Interface for deployment environment detection.
/// Implementations detect specific deployment scenarios (VM, hypervisor, bare metal, cloud, edge).
/// </summary>
/// <remarks>
/// <para>
/// Deployment detectors are pluggable and run in sequence at startup. Each detector:
/// 1. Checks if its target environment is present (e.g., hypervisor signatures, cloud metadata)
/// 2. Returns a populated <see cref="DeploymentContext"/> if detected
/// 3. Returns null if the environment is not applicable
/// </para>
/// <para>
/// The first detector to return a non-null context wins. Detection order:
/// - EdgeDetector (most specific: GPIO/I2C/SPI presence)
/// - CloudDetector (metadata endpoint check)
/// - BareMetalDetector (no hypervisor, no cloud)
/// - HypervisorDetector (paravirt capabilities)
/// - HostedVmDetector (hypervisor + filesystem)
/// </para>
/// <para>
/// Detectors should fail gracefully (return null) rather than throwing exceptions
/// when their target environment is absent.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Deployment environment detection interface (ENV-01-05)")]
public interface IDeploymentDetector
{
    /// <summary>
    /// Detects the deployment environment and returns a context record.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DeploymentContext"/> if this detector applies to the current environment.
    /// Returns null if this detector's target environment is not present.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Return value semantics:</b>
    /// - Non-null context: "I detected my target environment, use this configuration"
    /// - Null: "This is not my environment, try the next detector"
    /// </para>
    /// <para>
    /// <b>Example:</b> CloudDetector returns null on bare metal systems (no metadata endpoint),
    /// allowing BareMetalDetector to run next.
    /// </para>
    /// </remarks>
    Task<DeploymentContext?> DetectAsync(CancellationToken ct = default);
}
