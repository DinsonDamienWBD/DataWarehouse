using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hardware.Hypervisor;

/// <summary>
/// Interface for detecting the hypervisor/virtualization environment.
/// </summary>
/// <remarks>
/// <para>
/// IHypervisorDetector provides a single method for detecting the current hypervisor environment.
/// Detection is performed via CPUID instruction (x86/x64), platform-specific APIs (Windows registry,
/// Linux /sys filesystem), or other heuristics.
/// </para>
/// <para>
/// <strong>Detection Strategy:</strong>
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <strong>CPUID (x86/x64 only):</strong> Query CPUID leaf 0x40000000 for hypervisor
///       signature. Signatures like "VMwareVMware", "Microsoft Hv", "KVMKVMKVM" identify
///       the hypervisor.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Windows Registry:</strong> Check registry keys like
///       HKLM\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters (Hyper-V),
///       HKLM\SOFTWARE\VMware, Inc.\VMware Tools (VMware).
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Linux /sys filesystem:</strong> Check /sys/hypervisor/type for Xen,
///       /proc/cpuinfo for hypervisor flag.
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Performance:</strong> Detection completes in under 100ms and results are cached.
/// Repeated calls return the cached result without re-probing hardware.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// var detector = new HypervisorDetector(registry);
/// var info = detector.Detect();
///
/// if (info.Type == HypervisorType.VMware)
/// {
///     // Enable PVSCSI storage strategy
/// }
/// else if (info.Type == HypervisorType.None)
/// {
///     // Use native NVMe drivers
/// }
/// </code>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: Hypervisor detector interface (HW-05)")]
public interface IHypervisorDetector
{
    /// <summary>
    /// Detects the current hypervisor environment.
    /// </summary>
    /// <returns>
    /// Information about the detected hypervisor. Returns <see cref="HypervisorType.None"/>
    /// when running on bare metal (no hypervisor detected).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Detection is performed via CPUID instruction (x86/x64), platform-specific APIs
    /// (Windows registry, Linux /sys filesystem), or other heuristics. Detection completes
    /// in under 100ms and is safe to call repeatedly (results are cached).
    /// </para>
    /// <para>
    /// <strong>Caching:</strong> Results are cached after the first call. Subsequent calls
    /// return the cached result immediately without re-probing hardware.
    /// </para>
    /// <para>
    /// <strong>Capability Registration:</strong> After detection, the detector registers
    /// platform capabilities via <see cref="IPlatformCapabilityRegistry"/>:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>"hypervisor" (if any hypervisor is detected)</description></item>
    ///   <item><description>"hypervisor.vmware" (if VMware detected)</description></item>
    ///   <item><description>"hypervisor.hyperv" (if Hyper-V detected)</description></item>
    ///   <item><description>"hypervisor.kvm" (if KVM detected)</description></item>
    ///   <item><description>"hypervisor.xen" (if Xen detected)</description></item>
    /// </list>
    /// </remarks>
    HypervisorInfo Detect();
}
