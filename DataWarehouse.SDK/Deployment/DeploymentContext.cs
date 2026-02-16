using DataWarehouse.SDK.Contracts;
using System.Collections.Immutable;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Detected deployment context capturing runtime environment characteristics.
/// Populated by <see cref="IDeploymentDetector"/> implementations during environment detection.
/// </summary>
/// <remarks>
/// <para>
/// This record holds the result of environment detection, including:
/// - Environment type (VM, hypervisor, bare metal, cloud, edge)
/// - Hypervisor type (if virtualized)
/// - Cloud provider (if cloud)
/// - Filesystem type (if applicable)
/// - Platform-specific metadata
/// </para>
/// <para>
/// Detection is performed at startup by running all registered <see cref="IDeploymentDetector"/> implementations.
/// The first detector to return a non-null context wins (detectors return null if not applicable).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Detected deployment context (ENV-01-05)")]
public sealed record DeploymentContext
{
    /// <summary>
    /// Gets the detected deployment environment type.
    /// Default is <see cref="DeploymentEnvironment.Unknown"/> if detection fails.
    /// </summary>
    public DeploymentEnvironment Environment { get; init; } = DeploymentEnvironment.Unknown;

    /// <summary>
    /// Gets the hypervisor type if running virtualized.
    /// Null for bare metal deployments.
    /// Examples: "VMware", "KVM", "Hyper-V", "Xen".
    /// </summary>
    public string? HypervisorType { get; init; }

    /// <summary>
    /// Gets the cloud provider name if running in a hyperscale cloud.
    /// Null for non-cloud deployments.
    /// Examples: "AWS", "Azure", "GCP".
    /// </summary>
    public string? CloudProvider { get; init; }

    /// <summary>
    /// Gets whether this is a bare metal SPDK deployment.
    /// True only when <see cref="Environment"/> is <see cref="DeploymentEnvironment.BareMetalSpdk"/>.
    /// </summary>
    public bool IsBareMetalSpdk { get; init; }

    /// <summary>
    /// Gets whether this is an edge/IoT device deployment.
    /// True only when <see cref="Environment"/> is <see cref="DeploymentEnvironment.EdgeDevice"/>.
    /// </summary>
    public bool IsEdgeDevice { get; init; }

    /// <summary>
    /// Gets platform-specific metadata as key-value pairs.
    /// Examples: hypervisor version, cloud region, NVMe controller count, GPIO pin count.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Gets the detected filesystem type if applicable.
    /// Examples: "ext4", "xfs", "NTFS", "ReFS", null.
    /// Null for bare metal SPDK deployments (no OS filesystem layer).
    /// </summary>
    public string? FilesystemType { get; init; }
}
