using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

/// <summary>
/// Classifies the type of a node in the device topology tree.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device topology tree types (BMDV-08)")]
public enum TopologyNodeType
{
    /// <summary>Root of the topology tree containing all controllers.</summary>
    Root,
    /// <summary>Storage controller (e.g., NVMe, SATA, SAS controller).</summary>
    Controller,
    /// <summary>Bus segment under a controller (e.g., NVMe, SATA, SAS bus).</summary>
    Bus,
    /// <summary>NVMe subsystem grouping multiple namespaces on one controller.</summary>
    NvmeSubsystem,
    /// <summary>Physical block device leaf node.</summary>
    Device,
    /// <summary>Individual NVMe namespace on a shared controller.</summary>
    NvmeNamespace
}

/// <summary>
/// Represents a node in the device topology tree. Each node has a type, optional NUMA affinity,
/// child nodes, and extensible key-value properties. Leaf nodes represent individual devices
/// or NVMe namespaces; internal nodes represent controllers, buses, or subsystems.
/// </summary>
/// <param name="NodeId">Unique identifier within the tree (e.g., "root", "ctrl:0000:00:1f.2", "bus:nvme", "dev:nvme0n1").</param>
/// <param name="NodeType">Classification of this topology node.</param>
/// <param name="DisplayName">Human-readable name (e.g., "Intel NVMe Controller", "SATA Bus 0", "Samsung 990 Pro 1TB").</param>
/// <param name="NumaNode">NUMA node affinity. Inherited from controller if the device does not specify one.</param>
/// <param name="Children">Child nodes in the topology tree. Empty list for leaf nodes.</param>
/// <param name="Properties">Extensible key-value properties (PCIe address, bus number, slot, etc.).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device topology tree types (BMDV-08)")]
public sealed record TopologyNode(
    string NodeId,
    TopologyNodeType NodeType,
    string DisplayName,
    int? NumaNode,
    IReadOnlyList<TopologyNode> Children,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// Complete device topology tree built from discovered physical devices. The tree is rooted
/// at a single root node containing controller -> bus -> device hierarchy, with summary counts
/// for quick introspection.
/// </summary>
/// <param name="Root">The root node containing all controllers as children.</param>
/// <param name="ControllerCount">Number of distinct storage controllers in the topology.</param>
/// <param name="DeviceCount">Total number of device leaf nodes across all controllers.</param>
/// <param name="NumaNodeCount">Number of distinct NUMA nodes referenced by devices.</param>
/// <param name="BuiltUtc">UTC timestamp when this topology tree was constructed.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device topology tree types (BMDV-08)")]
public sealed record DeviceTopologyTree(
    TopologyNode Root,
    int ControllerCount,
    int DeviceCount,
    int NumaNodeCount,
    DateTime BuiltUtc);

/// <summary>
/// NUMA affinity information for a set of devices, including the CPU cores available
/// on that NUMA node for optimal I/O scheduling.
/// </summary>
/// <param name="NumaNode">NUMA node identifier.</param>
/// <param name="DeviceIds">Device IDs of all devices affinitized to this NUMA node.</param>
/// <param name="CpuCores">CPU core IDs on this NUMA node (best-effort; may be empty if detection fails).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device topology tree types (BMDV-08)")]
public sealed record NumaAffinityInfo(
    int NumaNode,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<int> CpuCores);
