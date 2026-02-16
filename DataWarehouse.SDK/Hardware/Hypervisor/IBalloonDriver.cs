using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Hardware.Hypervisor;

/// <summary>
/// Interface for cooperating with hypervisor memory balloon drivers.
/// </summary>
/// <remarks>
/// <para>
/// Balloon drivers allow hypervisors to reclaim guest memory under host memory pressure.
/// DataWarehouse cooperates by releasing cache buffers when notified of memory pressure.
/// </para>
/// <para>
/// <strong>How Balloon Drivers Work:</strong>
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       Host memory pressure detected by hypervisor (VMware ESXi, Hyper-V, KVM, Xen).
///     </description>
///   </item>
///   <item>
///     <description>
///       Hypervisor inflates the balloon driver in the guest VM (allocates guest physical memory).
///     </description>
///   </item>
///   <item>
///     <description>
///       Guest OS experiences memory pressure and notifies applications via OS APIs.
///     </description>
///   </item>
///   <item>
///     <description>
///       DataWarehouse receives notification via <see cref="RegisterPressureHandler"/> and
///       releases cache buffers to free memory.
///     </description>
///   </item>
///   <item>
///     <description>
///       DataWarehouse reports released memory via <see cref="ReportMemoryReleased"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       Hypervisor reclaims the freed guest physical memory for use by other VMs.
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Availability:</strong> Balloon driver cooperation is only available when running in a
/// hypervisor environment that supports memory ballooning (VMware, Hyper-V, KVM, Xen).
/// <see cref="IsAvailable"/> returns false on bare metal or when the balloon driver is unavailable.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: Balloon driver interface (HW-06)")]
public interface IBalloonDriver
{
    /// <summary>
    /// Gets whether balloon driver cooperation is available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns true when running in a hypervisor environment that supports memory ballooning
    /// (VMware, Hyper-V, KVM, Xen). Returns false on bare metal or when the balloon driver
    /// is unavailable.
    /// </para>
    /// <para>
    /// This property is cached after the first check and does not require repeated hypervisor
    /// detection.
    /// </para>
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Registers for memory pressure notifications.
    /// </summary>
    /// <param name="onMemoryPressure">
    /// Callback invoked when the hypervisor requests memory release.
    /// The callback receives the amount of memory (in bytes) to release.
    /// </param>
    /// <remarks>
    /// <para>
    /// The callback is invoked when the guest OS detects memory pressure caused by balloon
    /// driver inflation. The application should release cache buffers, trim working sets,
    /// or perform other memory-reduction operations.
    /// </para>
    /// <para>
    /// After releasing memory, call <see cref="ReportMemoryReleased"/> to inform the
    /// hypervisor that memory has been freed.
    /// </para>
    /// <para>
    /// Only one handler can be registered at a time. Calling this method multiple times
    /// replaces the previous handler.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="onMemoryPressure"/> is null.
    /// </exception>
    void RegisterPressureHandler(Action<long> onMemoryPressure);

    /// <summary>
    /// Reports memory release to the hypervisor.
    /// </summary>
    /// <param name="bytesReleased">Number of bytes released.</param>
    /// <remarks>
    /// <para>
    /// Call this method after releasing cache buffers to inform the hypervisor that
    /// memory is available for reclamation. The hypervisor can then deflate the balloon
    /// and use the freed memory for other VMs.
    /// </para>
    /// <para>
    /// This method is a no-op if <see cref="IsAvailable"/> is false (bare metal or
    /// balloon driver unavailable).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="bytesReleased"/> is negative or zero.
    /// </exception>
    void ReportMemoryReleased(long bytesReleased);
}
