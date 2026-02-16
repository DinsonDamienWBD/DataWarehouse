using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Hardware.Memory;

/// <summary>
/// Interface for NUMA-aware memory allocation.
/// </summary>
/// <remarks>
/// <para>
/// NUMA (Non-Uniform Memory Access) systems have multiple memory controllers, each with
/// local CPUs and devices. Allocating memory on the NUMA node closest to the storage
/// controller reduces latency by 2-3x compared to remote memory access.
/// </para>
/// <para>
/// <strong>What is NUMA?</strong>
/// </para>
/// <para>
/// In multi-socket systems, each CPU socket has its own memory controller and locally
/// attached memory. CPUs can access local memory quickly (low latency) and remote memory
/// slowly (high latency). The same applies to PCI devices like storage controllers.
/// </para>
/// <para>
/// <strong>Performance Impact:</strong> On a dual-socket system with NVMe attached to socket 0:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>Local allocation (socket 0 memory):</strong> ~100ns latency, full memory bandwidth
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Remote allocation (socket 1 memory):</strong> ~200-300ns latency, reduced bandwidth
///     </description>
///   </item>
/// </list>
/// <para>
/// For storage workloads with high I/O rates, NUMA-aware allocation can improve throughput by 30-50%.
/// </para>
/// <para>
/// <strong>Fallback Strategy:</strong> If allocation on the preferred NUMA node fails (out of memory),
/// the allocator automatically falls back to:
/// </para>
/// <list type="number">
///   <item><description>Adjacent NUMA node (lowest distance)</description></item>
///   <item><description>Any available NUMA node</description></item>
/// </list>
/// <para>
/// <strong>Non-NUMA Systems:</strong> On single-socket systems or systems with NUMA disabled,
/// <see cref="IsNumaAware"/> returns false and <see cref="Allocate"/> degrades gracefully to
/// standard allocation (no NUMA pinning).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: NUMA allocator interface (HW-06)")]
public interface INumaAllocator
{
    /// <summary>
    /// Gets the NUMA topology of the system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the detected NUMA topology including node count, memory per node, CPU affinity,
    /// and distance matrix. Returns null on non-NUMA systems (single-socket or NUMA disabled).
    /// </para>
    /// <para>
    /// This property is populated during initialization and does not require repeated hardware
    /// detection.
    /// </para>
    /// </remarks>
    NumaTopology? Topology { get; }

    /// <summary>
    /// Gets whether NUMA-aware allocation is available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns true when the system has multiple NUMA nodes and NUMA-aware allocation APIs
    /// are available. Returns false on single-socket systems, systems with NUMA disabled,
    /// or when NUMA APIs are unavailable.
    /// </para>
    /// <para>
    /// When false, <see cref="Allocate"/> falls back to standard allocation without NUMA
    /// affinity.
    /// </para>
    /// </remarks>
    bool IsNumaAware { get; }

    /// <summary>
    /// Allocates a buffer on the specified NUMA node.
    /// </summary>
    /// <param name="sizeInBytes">Size of the buffer to allocate (in bytes).</param>
    /// <param name="preferredNodeId">Preferred NUMA node ID (0-based).</param>
    /// <returns>
    /// Allocated byte array. If allocation on the preferred node fails (out of memory),
    /// falls back to adjacent node (lowest distance), then any node.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>NUMA-Aware Systems:</strong> Attempts to allocate memory on the specified NUMA node
    /// using platform-specific APIs:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Windows: VirtualAllocExNuma</description></item>
    ///   <item><description>Linux: numa_alloc_onnode (libnuma)</description></item>
    /// </list>
    /// <para>
    /// <strong>Non-NUMA Systems:</strong> Ignores <paramref name="preferredNodeId"/> and uses
    /// standard allocation (all memory is local).
    /// </para>
    /// <para>
    /// <strong>Fallback Chain:</strong> If the preferred node is out of memory:
    /// </para>
    /// <list type="number">
    ///   <item><description>Try adjacent node (lowest distance from distance matrix)</description></item>
    ///   <item><description>Try any available node</description></item>
    /// </list>
    /// <para>
    /// <strong>Phase 35 Implementation:</strong> Provides API contract and topology detection.
    /// Actual NUMA-aware allocation APIs (VirtualAllocExNuma, libnuma) are marked as future work.
    /// Current implementation falls back to standard allocation on all systems.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="sizeInBytes"/> is negative or zero.
    /// </exception>
    byte[] Allocate(int sizeInBytes, int preferredNodeId);

    /// <summary>
    /// Gets the NUMA node ID for a storage device.
    /// </summary>
    /// <param name="devicePath">
    /// Device path (e.g., "/dev/nvme0n1" on Linux, "\\.\PhysicalDrive0" on Windows).
    /// </param>
    /// <returns>
    /// NUMA node ID (0-based) where the device is located. Returns 0 if device locality
    /// cannot be determined or on non-NUMA systems.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Determines which NUMA node a storage device is physically connected to via PCIe lanes.
    /// Use this to get the preferred node ID for <see cref="Allocate"/>.
    /// </para>
    /// <para>
    /// <strong>Platform-Specific Detection:</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <strong>Windows:</strong> Use SetupAPI to query PCI device properties and NUMA node affinity.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <strong>Linux:</strong> Parse /sys/block/{device}/device/numa_node sysfs entry.
    ///     </description>
    ///   </item>
    /// </list>
    /// <para>
    /// <strong>Phase 35 Implementation:</strong> Provides API contract. Actual device locality
    /// detection is marked as future work. Current implementation returns 0 (default node).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="devicePath"/> is null or empty.
    /// </exception>
    int GetDeviceNumaNode(string devicePath);
}
