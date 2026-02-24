using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

/// <summary>
/// Builds and queries a device topology tree (controller -> bus -> device) from discovered
/// physical devices. Supports NVMe namespace grouping, NUMA affinity inheritance, and
/// topology queries for failure domain isolation and sibling device discovery.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device topology mapping (BMDV-08)")]
public sealed class DeviceTopologyMapper
{
    private const string UnknownController = "unknown-controller";

    /// <summary>
    /// Builds a complete device topology tree from the provided list of physical devices.
    /// Devices are grouped by controller path, then by bus type. NVMe devices sharing the
    /// same controller with different namespace IDs are grouped under an NvmeSubsystem node.
    /// </summary>
    /// <param name="devices">List of discovered physical devices.</param>
    /// <returns>A fully constructed device topology tree.</returns>
    public DeviceTopologyTree BuildTopology(IReadOnlyList<PhysicalDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var controllerGroups = devices.GroupBy(d => d.ControllerPath ?? UnknownController);
        var controllerNodes = new List<TopologyNode>();
        var allDeviceCount = 0;
        var numaNodes = new HashSet<int>();

        foreach (var ctrlGroup in controllerGroups)
        {
            var controllerPath = ctrlGroup.Key;
            var controllerDevices = ctrlGroup.ToList();

            // Inherit NUMA node from first device that reports it
            int? controllerNuma = controllerDevices
                .Where(d => d.NumaNode.HasValue)
                .Select(d => d.NumaNode)
                .FirstOrDefault();

            var controllerProperties = new Dictionary<string, string>
            {
                ["PciAddress"] = controllerPath
            };

            // Group by bus type within this controller
            var busGroups = controllerDevices.GroupBy(d => d.BusType);
            var busNodes = new List<TopologyNode>();

            foreach (var busGroup in busGroups)
            {
                var busType = busGroup.Key;
                var busDevices = busGroup.ToList();

                // Check for NVMe namespace grouping: multiple devices with different NvmeNamespaceId
                var nvmeNamespacedDevices = busDevices
                    .Where(d => d.NvmeNamespaceId > 0)
                    .GroupBy(d => d.ControllerPath ?? UnknownController)
                    .Where(g => g.Select(d => d.NvmeNamespaceId).Distinct().Count() > 1)
                    .SelectMany(g => g)
                    .ToHashSet();

                var directDeviceNodes = new List<TopologyNode>();

                if (nvmeNamespacedDevices.Count > 0)
                {
                    // Create NvmeSubsystem node for namespaced devices
                    var namespaceNodes = new List<TopologyNode>();
                    foreach (var nsDev in nvmeNamespacedDevices.OrderBy(d => d.NvmeNamespaceId))
                    {
                        int? devNuma = nsDev.NumaNode ?? controllerNuma;
                        if (devNuma.HasValue) numaNodes.Add(devNuma.Value);
                        allDeviceCount++;

                        namespaceNodes.Add(new TopologyNode(
                            NodeId: $"ns:{nsDev.DeviceId}",
                            NodeType: TopologyNodeType.NvmeNamespace,
                            DisplayName: $"{nsDev.ModelNumber} NS{nsDev.NvmeNamespaceId}",
                            NumaNode: devNuma,
                            Children: Array.Empty<TopologyNode>(),
                            Properties: new Dictionary<string, string>
                            {
                                ["DeviceId"] = nsDev.DeviceId,
                                ["DevicePath"] = nsDev.DevicePath,
                                ["NamespaceId"] = nsDev.NvmeNamespaceId.ToString(),
                                ["CapacityBytes"] = nsDev.CapacityBytes.ToString()
                            }));
                    }

                    var subsystemNode = new TopologyNode(
                        NodeId: $"nvme-subsys:{controllerPath}:{busType}",
                        NodeType: TopologyNodeType.NvmeSubsystem,
                        DisplayName: $"NVMe Subsystem ({controllerPath})",
                        NumaNode: controllerNuma,
                        Children: namespaceNodes,
                        Properties: new Dictionary<string, string>
                        {
                            ["NamespaceCount"] = namespaceNodes.Count.ToString()
                        });

                    directDeviceNodes.Add(subsystemNode);
                }

                // Add non-namespaced devices as regular device nodes
                foreach (var dev in busDevices.Where(d => !nvmeNamespacedDevices.Contains(d)))
                {
                    int? devNuma = dev.NumaNode ?? controllerNuma;
                    if (devNuma.HasValue) numaNodes.Add(devNuma.Value);
                    allDeviceCount++;

                    directDeviceNodes.Add(new TopologyNode(
                        NodeId: $"dev:{dev.DeviceId}",
                        NodeType: TopologyNodeType.Device,
                        DisplayName: $"{dev.ModelNumber} {FormatCapacity(dev.CapacityBytes)}",
                        NumaNode: devNuma,
                        Children: Array.Empty<TopologyNode>(),
                        Properties: new Dictionary<string, string>
                        {
                            ["DeviceId"] = dev.DeviceId,
                            ["DevicePath"] = dev.DevicePath,
                            ["SerialNumber"] = dev.SerialNumber,
                            ["MediaType"] = dev.MediaType.ToString(),
                            ["CapacityBytes"] = dev.CapacityBytes.ToString()
                        }));
                }

                var busNode = new TopologyNode(
                    NodeId: $"bus:{controllerPath}:{busType}",
                    NodeType: TopologyNodeType.Bus,
                    DisplayName: $"{busType} Bus",
                    NumaNode: controllerNuma,
                    Children: directDeviceNodes,
                    Properties: new Dictionary<string, string>
                    {
                        ["BusType"] = busType.ToString(),
                        ["DeviceCount"] = busDevices.Count.ToString()
                    });

                busNodes.Add(busNode);
            }

            var controllerDisplayName = controllerPath == UnknownController
                ? "Unknown Controller"
                : $"Controller {controllerPath}";

            var controllerNode = new TopologyNode(
                NodeId: $"ctrl:{controllerPath}",
                NodeType: TopologyNodeType.Controller,
                DisplayName: controllerDisplayName,
                NumaNode: controllerNuma,
                Children: busNodes,
                Properties: controllerProperties);

            controllerNodes.Add(controllerNode);
        }

        var root = new TopologyNode(
            NodeId: "root",
            NodeType: TopologyNodeType.Root,
            DisplayName: "Device Topology Root",
            NumaNode: null,
            Children: controllerNodes,
            Properties: new Dictionary<string, string>());

        return new DeviceTopologyTree(
            Root: root,
            ControllerCount: controllerNodes.Count,
            DeviceCount: allDeviceCount,
            NumaNodeCount: numaNodes.Count,
            BuiltUtc: DateTime.UtcNow);
    }

    /// <summary>
    /// Finds all device leaf nodes under a specific controller.
    /// </summary>
    /// <param name="tree">The topology tree to search.</param>
    /// <param name="controllerNodeId">The NodeId of the controller to search under.</param>
    /// <returns>All device and namespace leaf nodes under the specified controller.</returns>
    public IReadOnlyList<TopologyNode> GetDevicesByController(DeviceTopologyTree tree, string controllerNodeId)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(controllerNodeId);

        var controllerNode = FindNodeById(tree.Root, controllerNodeId);
        if (controllerNode == null) return Array.Empty<TopologyNode>();

        var devices = new List<TopologyNode>();
        CollectLeafDevices(controllerNode, devices);
        return devices;
    }

    /// <summary>
    /// Performs a DFS search to find a device node by its DeviceId property value.
    /// </summary>
    /// <param name="tree">The topology tree to search.</param>
    /// <param name="deviceId">The DeviceId to search for (matches Properties["DeviceId"]).</param>
    /// <returns>The matching topology node, or null if not found.</returns>
    public TopologyNode? FindDeviceNode(DeviceTopologyTree tree, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(deviceId);

        return FindDeviceByIdDfs(tree.Root, deviceId);
    }

    /// <summary>
    /// Finds all sibling devices that share the same parent node as the specified device.
    /// Siblings are devices under the same controller or bus node.
    /// </summary>
    /// <param name="tree">The topology tree to search.</param>
    /// <param name="deviceId">The DeviceId of the device whose siblings to find.</param>
    /// <returns>DeviceIds of sibling devices (excluding the specified device itself).</returns>
    public IReadOnlyList<string> GetSiblingDevices(DeviceTopologyTree tree, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(deviceId);

        var parent = FindParentOfDevice(tree.Root, deviceId);
        if (parent == null) return Array.Empty<string>();

        var siblings = new List<string>();
        foreach (var child in parent.Children)
        {
            CollectDeviceIds(child, siblings, deviceId);
        }
        return siblings;
    }

    /// <summary>
    /// Computes NUMA affinity information by grouping all device nodes by their NUMA node
    /// and attempting to discover CPU cores for each NUMA node.
    /// </summary>
    /// <param name="tree">The topology tree to analyze.</param>
    /// <returns>NUMA affinity information per NUMA node.</returns>
    public IReadOnlyList<NumaAffinityInfo> GetNumaAffinity(DeviceTopologyTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var numaDevices = new Dictionary<int, List<string>>();
        CollectNumaDevices(tree.Root, numaDevices);

        var result = new List<NumaAffinityInfo>();
        foreach (var (numaNode, deviceIds) in numaDevices.OrderBy(kv => kv.Key))
        {
            var cpuCores = DiscoverCpuCoresForNumaNode(numaNode);
            result.Add(new NumaAffinityInfo(numaNode, deviceIds, cpuCores));
        }
        return result;
    }

    private static TopologyNode? FindNodeById(TopologyNode node, string nodeId)
    {
        if (string.Equals(node.NodeId, nodeId, StringComparison.Ordinal))
            return node;

        foreach (var child in node.Children)
        {
            var found = FindNodeById(child, nodeId);
            if (found != null) return found;
        }
        return null;
    }

    private static void CollectLeafDevices(TopologyNode node, List<TopologyNode> devices)
    {
        if (node.NodeType is TopologyNodeType.Device or TopologyNodeType.NvmeNamespace)
        {
            devices.Add(node);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectLeafDevices(child, devices);
        }
    }

    private static TopologyNode? FindDeviceByIdDfs(TopologyNode node, string deviceId)
    {
        if ((node.NodeType is TopologyNodeType.Device or TopologyNodeType.NvmeNamespace)
            && node.Properties.TryGetValue("DeviceId", out var id)
            && string.Equals(id, deviceId, StringComparison.Ordinal))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindDeviceByIdDfs(child, deviceId);
            if (found != null) return found;
        }
        return null;
    }

    private static TopologyNode? FindParentOfDevice(TopologyNode node, string deviceId)
    {
        foreach (var child in node.Children)
        {
            if ((child.NodeType is TopologyNodeType.Device or TopologyNodeType.NvmeNamespace)
                && child.Properties.TryGetValue("DeviceId", out var id)
                && string.Equals(id, deviceId, StringComparison.Ordinal))
            {
                return node;
            }

            var found = FindParentOfDevice(child, deviceId);
            if (found != null) return found;
        }
        return null;
    }

    private static void CollectDeviceIds(TopologyNode node, List<string> deviceIds, string excludeDeviceId)
    {
        if ((node.NodeType is TopologyNodeType.Device or TopologyNodeType.NvmeNamespace)
            && node.Properties.TryGetValue("DeviceId", out var id)
            && !string.Equals(id, excludeDeviceId, StringComparison.Ordinal))
        {
            deviceIds.Add(id);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectDeviceIds(child, deviceIds, excludeDeviceId);
        }
    }

    private static void CollectNumaDevices(TopologyNode node, Dictionary<int, List<string>> numaDevices)
    {
        if ((node.NodeType is TopologyNodeType.Device or TopologyNodeType.NvmeNamespace)
            && node.NumaNode.HasValue)
        {
            if (node.Properties.TryGetValue("DeviceId", out var id))
            {
                if (!numaDevices.TryGetValue(node.NumaNode.Value, out var list))
                {
                    list = new List<string>();
                    numaDevices[node.NumaNode.Value] = list;
                }
                list.Add(id);
            }
        }

        foreach (var child in node.Children)
        {
            CollectNumaDevices(child, numaDevices);
        }
    }

    private static IReadOnlyList<int> DiscoverCpuCoresForNumaNode(int numaNode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return DiscoverLinuxCpuCores(numaNode);
        }

        // On Windows and other platforms, return empty list as best-effort.
        // Windows would require GetLogicalProcessorInformationEx P/Invoke which
        // is complex and platform-specific. Consumers should handle empty cores gracefully.
        return Array.Empty<int>();
    }

    private static IReadOnlyList<int> DiscoverLinuxCpuCores(int numaNode)
    {
        try
        {
            var cpuListPath = $"/sys/devices/system/node/node{numaNode}/cpulist";
            if (!File.Exists(cpuListPath)) return Array.Empty<int>();

            var cpuListText = File.ReadAllText(cpuListPath).Trim();
            if (string.IsNullOrEmpty(cpuListText)) return Array.Empty<int>();

            return ParseCpuList(cpuListText);
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    /// <summary>
    /// Parses a Linux cpulist format string (e.g., "0-3,8-11") into individual core IDs.
    /// </summary>
    internal static IReadOnlyList<int> ParseCpuList(string cpuList)
    {
        var cores = new List<int>();
        var ranges = cpuList.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var range in ranges)
        {
            var dashIndex = range.IndexOf('-');
            if (dashIndex >= 0)
            {
                if (int.TryParse(range.AsSpan(0, dashIndex), out var start)
                    && int.TryParse(range.AsSpan(dashIndex + 1), out var end))
                {
                    for (var i = start; i <= end; i++)
                    {
                        cores.Add(i);
                    }
                }
            }
            else if (int.TryParse(range, out var single))
            {
                cores.Add(single);
            }
        }

        return cores;
    }

    private static string FormatCapacity(long bytes)
    {
        if (bytes >= 1_099_511_627_776L)
            return $"{bytes / 1_099_511_627_776.0:F1}TB";
        if (bytes >= 1_073_741_824L)
            return $"{bytes / 1_073_741_824.0:F1}GB";
        if (bytes >= 1_048_576L)
            return $"{bytes / 1_048_576.0:F1}MB";
        return $"{bytes}B";
    }
}
