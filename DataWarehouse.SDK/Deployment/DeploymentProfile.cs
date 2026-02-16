using DataWarehouse.SDK.Contracts;
using System.Collections.Immutable;

namespace DataWarehouse.SDK.Deployment;

/// <summary>
/// Deployment-specific configuration profile defining which optimizations to apply.
/// Profiles are selected based on detected <see cref="DeploymentContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each deployment environment has an optimal configuration profile:
/// - <b>HostedVm:</b> DoubleWalBypassEnabled = true (when VDE WAL active)
/// - <b>Hypervisor:</b> ParavirtIoEnabled = true (virtio-blk, PVSCSI)
/// - <b>BareMetalSpdk:</b> SpdkEnabled = true (user-space NVMe)
/// - <b>HyperscaleCloud:</b> AutoScalingEnabled = true (provision nodes dynamically)
/// - <b>EdgeDevice:</b> MaxMemoryBytes set, AllowedPlugins filtered
/// </para>
/// <para>
/// Profiles configure DataWarehouse to leverage platform-specific capabilities while
/// gracefully degrading when features are unavailable.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 37: Deployment-specific configuration profile (ENV-01-05)")]
public sealed record DeploymentProfile
{
    /// <summary>
    /// Gets the profile name for identification and logging.
    /// Examples: "hosted-vm", "hypervisor", "bare-metal-spdk", "hyperscale-cloud", "raspberry-pi".
    /// </summary>
    public string Name { get; init; } = "default";

    /// <summary>
    /// Gets the target deployment environment for this profile.
    /// </summary>
    public DeploymentEnvironment Environment { get; init; }

    /// <summary>
    /// Gets whether double-WAL bypass optimization is enabled.
    /// Applies to <see cref="DeploymentEnvironment.HostedVm"/> and <see cref="DeploymentEnvironment.HyperscaleCloud"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Double-WAL bypass disables OS-level journaling for VDE container files when VDE WAL is active.
    /// This eliminates the performance penalty of journaling writes twice (filesystem + VDE WAL).
    /// </para>
    /// <para>
    /// <b>CRITICAL SAFETY:</b> Only enabled when <c>VirtualDiskEngineConfig.WriteAheadLogEnabled == true</c>.
    /// Disabling OS journaling without VDE WAL active would compromise data safety.
    /// </para>
    /// </remarks>
    public bool DoubleWalBypassEnabled { get; init; }

    /// <summary>
    /// Gets whether paravirtualized I/O is enabled.
    /// Applies to <see cref="DeploymentEnvironment.Hypervisor"/> and <see cref="DeploymentEnvironment.HyperscaleCloud"/>.
    /// </summary>
    /// <remarks>
    /// Enables virtio-blk, virtio-scsi (KVM), PVSCSI (VMware), Hyper-V VSC for near-native I/O performance.
    /// Paravirt I/O achieves 95%+ of bare metal throughput compared to 60-70% for emulated I/O.
    /// </remarks>
    public bool ParavirtIoEnabled { get; init; }

    /// <summary>
    /// Gets whether SPDK user-space NVMe driver is enabled.
    /// Applies to <see cref="DeploymentEnvironment.BareMetalSpdk"/> only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SPDK (Storage Performance Development Kit) bypasses the kernel NVMe driver for maximum throughput.
    /// Sequential writes achieve 90%+ of NVMe hardware spec, 4K random reads achieve 90%+ IOPS.
    /// </para>
    /// <para>
    /// <b>REQUIREMENT:</b> Dedicated NVMe namespace (cannot share with OS). Validated by <c>SpdkBindingValidator</c>.
    /// </para>
    /// </remarks>
    public bool SpdkEnabled { get; init; }

    /// <summary>
    /// Gets whether auto-scaling is enabled.
    /// Applies to <see cref="DeploymentEnvironment.HyperscaleCloud"/> only.
    /// </summary>
    /// <remarks>
    /// Auto-scaling provisions new cloud VMs when storage utilization >80%, deprovisions when <40%.
    /// Min/max node counts and evaluation interval configured via <c>AutoScalingPolicy</c>.
    /// </remarks>
    public bool AutoScalingEnabled { get; init; }

    /// <summary>
    /// Gets the hard memory ceiling in bytes (edge deployments only).
    /// Null for non-edge deployments (no memory limit).
    /// </summary>
    /// <remarks>
    /// Enforced via <c>GC.RegisterMemoryLimit</c> (.NET 9+) for edge/IoT devices.
    /// Examples: 256MB (Raspberry Pi), 1GB (industrial gateway).
    /// </remarks>
    public long? MaxMemoryBytes { get; init; }

    /// <summary>
    /// Gets the list of allowed plugins for edge deployments.
    /// Null for non-edge deployments (all plugins allowed).
    /// </summary>
    /// <remarks>
    /// Edge devices filter plugins to essential-only: UltimateStorage, TamperProof, EdgeSensorMesh.
    /// Enforced via <c>KernelInfrastructure.DisablePluginsExcept</c>.
    /// </remarks>
    public IReadOnlyList<string>? AllowedPlugins { get; init; }

    /// <summary>
    /// Gets custom settings for profile-specific configuration.
    /// Used to pass environment-specific data (e.g., full EdgeProfile for edge deployments).
    /// </summary>
    public IReadOnlyDictionary<string, object> CustomSettings { get; init; } = ImmutableDictionary<string, object>.Empty;
}
