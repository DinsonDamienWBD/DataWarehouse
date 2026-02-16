using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Hardware.Memory;

/// <summary>
/// Represents the NUMA (Non-Uniform Memory Access) topology of the system.
/// </summary>
/// <remarks>
/// <para>
/// NUMA topology describes the physical memory architecture of multi-socket systems.
/// Each NUMA node represents a memory controller with locally attached memory and CPUs.
/// Memory access is fastest when CPUs access local memory (same NUMA node) and slower
/// when accessing remote memory (different NUMA node).
/// </para>
/// <para>
/// <strong>Performance Impact:</strong> On NUMA systems, local memory access can be 2-3x
/// faster than remote memory access. For storage workloads, allocating buffers on the
/// NUMA node closest to the storage controller significantly reduces latency.
/// </para>
/// <para>
/// <strong>NUMA Node Count:</strong>
/// </para>
/// <list type="bullet">
///   <item><description>Single-socket systems: 1 NUMA node (all memory is local)</description></item>
///   <item><description>Dual-socket systems: 2 NUMA nodes</description></item>
///   <item><description>Quad-socket systems: 4 NUMA nodes</description></item>
///   <item><description>8-socket systems: 8 NUMA nodes (high-end servers)</description></item>
/// </list>
/// <para>
/// <strong>Distance Matrix:</strong> The distance matrix quantifies memory access costs between
/// NUMA nodes. Local access (i == j) has distance 10 (normalized baseline). Adjacent nodes
/// typically have distance 20-21. Remote nodes can have distance 30-32 or higher on large systems.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: NUMA topology model (HW-06)")]
public sealed record NumaTopology
{
    /// <summary>
    /// Gets the number of NUMA nodes in the system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Single-socket systems typically have 1 NUMA node (or NUMA disabled).
    /// Multi-socket systems have one NUMA node per socket (e.g., dual-socket = 2 nodes).
    /// </para>
    /// <para>
    /// A value of 1 indicates either a single-socket system or a multi-socket system with
    /// NUMA disabled in BIOS.
    /// </para>
    /// </remarks>
    public required int NodeCount { get; init; }

    /// <summary>
    /// Gets the NUMA nodes.
    /// </summary>
    /// <remarks>
    /// Collection contains <see cref="NodeCount"/> elements, indexed by NUMA node ID (0-based).
    /// Never null; empty collection if NUMA topology cannot be determined.
    /// </remarks>
    public required IReadOnlyList<NumaNode> Nodes { get; init; }

    /// <summary>
    /// Gets the distance matrix between NUMA nodes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distance[i,j] represents the relative memory access cost from node i to node j.
    /// </para>
    /// <para>
    /// <strong>Distance Values:</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Local access (i == j): distance 10 (normalized baseline)</description></item>
    ///   <item><description>Adjacent nodes: distance 20-21 (directly connected via UPI/Infinity Fabric)</description></item>
    ///   <item><description>Remote nodes: distance 30-32+ (multi-hop interconnect)</description></item>
    /// </list>
    /// <para>
    /// May be null if distance information is unavailable (older systems or limited OS support).
    /// </para>
    /// </remarks>
    public int[,]? DistanceMatrix { get; init; }
}

/// <summary>
/// Represents a single NUMA node (memory controller with local memory and CPUs).
/// </summary>
/// <remarks>
/// <para>
/// A NUMA node represents a memory controller with locally attached memory, CPUs, and
/// PCI devices. On typical dual-socket systems, each socket is a NUMA node.
/// </para>
/// <para>
/// <strong>Local Devices:</strong> PCI devices (storage controllers, network adapters, GPUs)
/// are physically connected to a specific NUMA node via PCIe lanes. Allocating buffers on
/// the same NUMA node as the device minimizes memory access latency.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: NUMA node model (HW-06)")]
public sealed record NumaNode
{
    /// <summary>
    /// Gets the NUMA node ID (0-based).
    /// </summary>
    /// <remarks>
    /// NUMA node IDs are zero-based and sequential. On a dual-socket system, node IDs are
    /// typically 0 and 1. The node ID is used for NUMA-aware allocation APIs.
    /// </remarks>
    public required int NodeId { get; init; }

    /// <summary>
    /// Gets the total memory (in bytes) on this NUMA node.
    /// </summary>
    /// <remarks>
    /// Total physical memory installed on this memory controller. On systems with equal
    /// memory per socket, all nodes have the same total memory. Unbalanced configurations
    /// (different DIMM capacity per socket) result in different memory per node.
    /// </remarks>
    public required long TotalMemoryBytes { get; init; }

    /// <summary>
    /// Gets the CPU cores assigned to this NUMA node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// List of logical CPU core IDs (0-based) that are local to this NUMA node.
    /// On a dual-socket system with 64 cores (32 per socket), node 0 might have cores 0-31,
    /// and node 1 might have cores 32-63.
    /// </para>
    /// <para>
    /// Never null; empty collection if CPU affinity information is unavailable.
    /// </para>
    /// </remarks>
    public required IReadOnlyList<int> CpuCores { get; init; }

    /// <summary>
    /// Gets the PCI devices local to this NUMA node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// List of PCI device identifiers (bus:device.function format on Linux, instance path on Windows)
    /// that are physically connected to this NUMA node via PCIe lanes. Includes storage controllers,
    /// network adapters, GPUs, and other accelerators.
    /// </para>
    /// <para>
    /// <strong>Examples:</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Linux: "0000:3b:00.0" (NVMe controller on PCIe bus 3b)</description></item>
    ///   <item><description>Windows: "PCI\VEN_8086&amp;DEV_0953&amp;..." (instance path)</description></item>
    /// </list>
    /// <para>
    /// Empty list if device locality information is unavailable (older systems or limited OS support).
    /// Never null.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> LocalDevices { get; init; } = Array.Empty<string>();
}
