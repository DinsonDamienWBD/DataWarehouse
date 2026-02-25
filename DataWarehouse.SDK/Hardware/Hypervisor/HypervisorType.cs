using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hardware.Hypervisor;

/// <summary>
/// Types of hypervisors/virtualization environments.
/// </summary>
/// <remarks>
/// <para>
/// HypervisorType identifies the virtualization environment the DataWarehouse is running on.
/// Detection is performed via CPUID instructions, platform-specific APIs (Windows registry,
/// Linux /sys filesystem), and heuristics.
/// </para>
/// <para>
/// <strong>Optimization Strategy by Type:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>None (Bare Metal):</strong> Direct hardware access, no virtualization overhead.
///       Use native NVMe drivers, NUMA-aware allocation.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>VMware:</strong> Use PVSCSI for storage, VMXNET3 for network. Enable VMware Tools
///       for guest operations.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Hyper-V:</strong> Enable Hyper-V enlightenments (synthetic storage/network).
///       Use Dynamic Memory cooperation.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>KVM:</strong> Use virtio-blk for storage, virtio-net for network. Enable
///       paravirtualized clock.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Xen:</strong> Use Xen paravirtualized drivers (blkfront, netfront). Enable
///       Xen PV clock.
///     </description>
///   </item>
/// </list>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: Hypervisor types (HW-05)")]
public enum HypervisorType
{
    /// <summary>No hypervisor detected — running on bare metal.</summary>
    None,

    /// <summary>VMware ESXi hypervisor.</summary>
    VMware,

    /// <summary>Microsoft Hyper-V hypervisor.</summary>
    HyperV,

    /// <summary>KVM (Kernel-based Virtual Machine) hypervisor.</summary>
    KVM,

    /// <summary>Xen hypervisor.</summary>
    Xen,

    /// <summary>QEMU without KVM acceleration.</summary>
    QEMU,

    /// <summary>Microsoft Virtual PC (legacy).</summary>
    VirtualPC,

    /// <summary>Oracle VirtualBox.</summary>
    VirtualBox,

    /// <summary>Parallels Desktop.</summary>
    Parallels,

    /// <summary>Unknown or unrecognized hypervisor.</summary>
    Unknown
}
