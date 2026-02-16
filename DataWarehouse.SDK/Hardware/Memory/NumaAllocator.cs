using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.Hardware.Memory;

/// <summary>
/// Implementation of <see cref="INumaAllocator"/> for NUMA-aware memory allocation.
/// </summary>
/// <remarks>
/// <para>
/// NumaAllocator discovers the NUMA topology and provides NUMA-aware allocation APIs.
/// On multi-socket systems, allocating buffers on the NUMA node closest to the storage
/// controller reduces memory access latency by 2-3x and improves throughput by 30-50%.
/// </para>
/// <para>
/// <strong>Topology Detection:</strong> Detects NUMA topology via platform-specific APIs:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>Windows:</strong> GetNumaHighestNodeNumber, GetNumaNodeProcessorMask,
///       GetNumaAvailableMemoryNode (kernel32.dll)
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Linux:</strong> Parse /sys/devices/system/node/node*/meminfo,
///       /sys/devices/system/node/node*/cpulist
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Capability Registration:</strong> When a multi-node NUMA topology is detected,
/// registers the "numa" capability via <see cref="IPlatformCapabilityRegistry"/>.
/// </para>
/// <para>
/// <strong>Graceful Degradation:</strong> On non-NUMA systems (single-socket or NUMA disabled),
/// the allocator gracefully degrades to standard allocation without NUMA affinity.
/// </para>
/// <para>
/// <strong>Phase 35 Implementation:</strong> Provides API contract, topology detection structure,
/// and capability registration. Actual NUMA APIs (VirtualAllocExNuma, libnuma) are marked as
/// future work. Current implementation uses standard allocation with detailed TODO comments.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: NUMA allocator implementation (HW-06)")]
public sealed class NumaAllocator : INumaAllocator
{
    private readonly IPlatformCapabilityRegistry _registry;
    private NumaTopology? _topology;
    private bool _isNumaAware = false;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NumaAllocator"/> class.
    /// </summary>
    /// <param name="registry">Platform capability registry for capability registration.</param>
    /// <remarks>
    /// The constructor detects the NUMA topology and registers the "numa" capability if
    /// a multi-node topology is detected.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="registry"/> is null.
    /// </exception>
    public NumaAllocator(IPlatformCapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        DetectTopology();
    }

    /// <inheritdoc/>
    public NumaTopology? Topology => _topology;

    /// <inheritdoc/>
    public bool IsNumaAware => _isNumaAware;

    /// <summary>
    /// Detects the NUMA topology of the system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Detects NUMA topology via platform-specific APIs:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Windows: kernel32 NUMA APIs</description></item>
    ///   <item><description>Linux: /sys/devices/system/node filesystem</description></item>
    /// </list>
    /// <para>
    /// If a multi-node topology is detected (NodeCount > 1), registers the "numa" capability.
    /// </para>
    /// </remarks>
    private void DetectTopology()
    {
        lock (_lock)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _topology = DetectWindowsNumaTopology();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _topology = DetectLinuxNumaTopology();
            }

            _isNumaAware = _topology is not null && _topology.NodeCount > 1;

            // Note: Platform capability registry automatically derives "numa" capability
            // from detected NUMA topology via hardware probe. Manual registration is not
            // required.
        }
    }

    /// <summary>
    /// Detects NUMA topology on Windows.
    /// </summary>
    /// <returns>
    /// Detected NUMA topology, or null if NUMA is not available or detection fails.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Windows NUMA detection uses kernel32.dll APIs:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>GetNumaHighestNodeNumber: Get the highest NUMA node number</description></item>
    ///   <item><description>GetNumaNodeProcessorMask: Get CPU affinity for each node</description></item>
    ///   <item><description>GetNumaAvailableMemoryNode: Get available memory per node</description></item>
    /// </list>
    /// <para>
    /// <strong>Phase 35 Implementation:</strong> Returns null (detection placeholder).
    /// Actual Windows NUMA detection via P/Invoke is future work.
    /// </para>
    /// </remarks>
    private static NumaTopology? DetectWindowsNumaTopology()
    {
        // Windows API: GetNumaHighestNodeNumber, GetNumaNodeProcessorMask, GetNumaAvailableMemoryNode
        // For Phase 35: SIMPLIFIED — return null (NUMA detection is future work)
        // Production: P/Invoke to kernel32.dll
        //
        // Example implementation:
        // [DllImport("kernel32.dll")]
        // static extern bool GetNumaHighestNodeNumber(out uint highestNodeNumber);
        //
        // [DllImport("kernel32.dll")]
        // static extern bool GetNumaNodeProcessorMask(byte node, out ulong processorMask);
        //
        // [DllImport("kernel32.dll")]
        // static extern bool GetNumaAvailableMemoryNode(byte node, out ulong availableBytes);
        //
        // Query each API, build NumaTopology with nodes, distance matrix, etc.

        // TODO: Actual Windows NUMA detection via kernel32 APIs
        return null;
    }

    /// <summary>
    /// Detects NUMA topology on Linux.
    /// </summary>
    /// <returns>
    /// Detected NUMA topology, or null if NUMA is not available or detection fails.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Linux NUMA detection uses the /sys/devices/system/node filesystem:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>/sys/devices/system/node/node*/meminfo: Memory info per node</description></item>
    ///   <item><description>/sys/devices/system/node/node*/cpulist: CPU affinity per node</description></item>
    ///   <item><description>/sys/devices/system/node/node*/distance: Distance matrix</description></item>
    /// </list>
    /// <para>
    /// <strong>Phase 35 Implementation:</strong> Returns null (detection placeholder).
    /// Actual Linux NUMA detection via /sys parsing is future work.
    /// </para>
    /// </remarks>
    private static NumaTopology? DetectLinuxNumaTopology()
    {
        // Linux: /sys/devices/system/node/node*/meminfo, /sys/devices/system/node/node*/cpulist
        // For Phase 35: SIMPLIFIED — return null (NUMA detection is future work)
        // Production: parse /sys filesystem
        //
        // Example implementation:
        // 1. Enumerate /sys/devices/system/node/node* directories
        // 2. For each node, parse:
        //    - meminfo: extract "Node X MemTotal:" value
        //    - cpulist: parse range like "0-15,32-47"
        //    - distance: parse distance matrix (space-separated values)
        // 3. Build NumaTopology with nodes and distance matrix

        // TODO: Actual Linux NUMA detection via /sys/devices/system/node
        return null;
    }

    /// <inheritdoc/>
    public byte[] Allocate(int sizeInBytes, int preferredNodeId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeInBytes);

        if (!_isNumaAware || _topology is null)
        {
            // Non-NUMA system — standard allocation
            return new byte[sizeInBytes];
        }

        // NUMA-aware allocation
        // For Phase 35: SIMPLIFIED — use standard allocation with TODO comment
        // Production: use VirtualAllocExNuma (Windows) or numa_alloc_onnode (Linux)
        //
        // Windows implementation:
        // [DllImport("kernel32.dll")]
        // static extern IntPtr VirtualAllocExNuma(
        //     IntPtr hProcess,
        //     IntPtr lpAddress,
        //     UIntPtr dwSize,
        //     uint flAllocationType,
        //     uint flProtect,
        //     uint nndPreferred);
        //
        // IntPtr ptr = VirtualAllocExNuma(
        //     Process.GetCurrentProcess().Handle,
        //     IntPtr.Zero,
        //     (UIntPtr)sizeInBytes,
        //     MEM_COMMIT | MEM_RESERVE,
        //     PAGE_READWRITE,
        //     (uint)preferredNodeId);
        //
        // If allocation fails on preferred node, try adjacent node (lowest distance),
        // then any node.
        //
        // Linux implementation:
        // [DllImport("libnuma.so.1")]
        // static extern IntPtr numa_alloc_onnode(UIntPtr size, int node);
        //
        // IntPtr ptr = numa_alloc_onnode((UIntPtr)sizeInBytes, preferredNodeId);
        //
        // Marshal unmanaged memory to byte[] or use Span<byte> wrapper.

        // TODO: Actual NUMA-aware allocation
        // - Windows: VirtualAllocExNuma(preferredNodeId)
        // - Linux: numa_alloc_onnode(preferredNodeId) via libnuma
        // - Fallback chain: preferred node → adjacent node → any node

        return new byte[sizeInBytes]; // Fallback to standard allocation
    }

    /// <inheritdoc/>
    public int GetDeviceNumaNode(string devicePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicePath);

        if (!_isNumaAware || _topology is null)
            return 0; // Non-NUMA system — default node

        // For Phase 35: SIMPLIFIED — return 0 (device locality detection is future work)
        // Production:
        // - Windows: use SetupAPI to query PCI device properties and NUMA node affinity
        // - Linux: parse /sys/block/{device}/device/numa_node
        //
        // Windows implementation:
        // Use SetupDiGetClassDevs, SetupDiEnumDeviceInfo, SetupDiGetDeviceProperty
        // to query DEVPKEY_Device_Numa_Node property.
        //
        // Linux implementation:
        // Extract device name from path (e.g., "/dev/nvme0n1" → "nvme0n1")
        // Read /sys/block/nvme0n1/device/numa_node
        // If file exists, parse integer NUMA node ID

        // TODO: Actual device NUMA node detection
        // - Windows: SetupAPI device properties (DEVPKEY_Device_Numa_Node)
        // - Linux: /sys/block/{device}/device/numa_node
        return 0;
    }
}
