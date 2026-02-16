using DataWarehouse.SDK.Contracts;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Hardware.Hypervisor;

/// <summary>
/// Information about the detected hypervisor environment.
/// </summary>
/// <remarks>
/// <para>
/// HypervisorInfo provides comprehensive metadata about the virtualization environment,
/// including the hypervisor type, version (if detectable), and environment-specific
/// optimization hints.
/// </para>
/// <para>
/// <strong>Optimization Hints:</strong> The hints guide storage, network, and compute
/// optimizations based on the detected environment. For example:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>VMware:</strong> "Use PVSCSI for storage I/O", "Use VMXNET3 for network"
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Hyper-V:</strong> "Enable Hyper-V enlightenments", "Use synthetic storage (SCSI)"
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>KVM:</strong> "Use virtio-blk for storage", "Use virtio-net for network"
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Bare Metal:</strong> "Direct hardware access available", "Use native NVMe drivers"
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Paravirtualized I/O:</strong> Most modern hypervisors (VMware, Hyper-V, KVM, Xen)
/// provide paravirtualized I/O drivers that bypass full hardware emulation for better performance.
/// The <see cref="ParavirtualizedIoAvailable"/> property indicates whether these optimizations
/// are available.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: Hypervisor detection info (HW-05)")]
public sealed record HypervisorInfo
{
    /// <summary>
    /// Gets the detected hypervisor type.
    /// </summary>
    public required HypervisorType Type { get; init; }

    /// <summary>
    /// Gets the hypervisor version string, if detectable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// May be null if version information is not available. Version detection is hypervisor-specific
    /// and may require additional platform APIs beyond basic CPUID detection.
    /// </para>
    /// <para>
    /// <strong>Examples:</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>"ESXi 7.0.3"</description></item>
    ///   <item><description>"Hyper-V 2022"</description></item>
    ///   <item><description>"KVM 5.15.0"</description></item>
    /// </list>
    /// </remarks>
    public string? Version { get; init; }

    /// <summary>
    /// Gets environment-specific optimization hints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provides recommendations for optimal performance in this environment. Hints are
    /// actionable guidance for storage strategy selection, network configuration, and
    /// memory management.
    /// </para>
    /// <para>
    /// <strong>Examples:</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>"use virtio-blk for storage" (KVM)</description></item>
    ///   <item><description>"use PVSCSI for storage" (VMware)</description></item>
    ///   <item><description>"enable Hyper-V enlightenments" (Hyper-V)</description></item>
    ///   <item><description>"direct hardware access available" (bare metal)</description></item>
    /// </list>
    /// </remarks>
    public required IReadOnlyList<string> OptimizationHints { get; init; }

    /// <summary>
    /// Gets whether paravirtualized I/O is available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paravirtualized I/O (virtio, PVSCSI, synthetic storage) provides better performance than
    /// full hardware emulation. When true, the DataWarehouse should prefer paravirtualized drivers
    /// over emulated devices.
    /// </para>
    /// <para>
    /// Always false for <see cref="HypervisorType.None"/> (bare metal). True for VMware, Hyper-V,
    /// KVM, and Xen.
    /// </para>
    /// </remarks>
    public bool ParavirtualizedIoAvailable { get; init; }

    /// <summary>
    /// Gets whether the hypervisor supports memory ballooning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Memory ballooning allows the hypervisor to reclaim unused memory from the guest. When true,
    /// the DataWarehouse should cooperate with the balloon driver to avoid over-committing memory.
    /// </para>
    /// <para>
    /// Always false for <see cref="HypervisorType.None"/> (bare metal). True for VMware, Hyper-V,
    /// KVM, and Xen.
    /// </para>
    /// </remarks>
    public bool BalloonDriverAvailable { get; init; }
}
