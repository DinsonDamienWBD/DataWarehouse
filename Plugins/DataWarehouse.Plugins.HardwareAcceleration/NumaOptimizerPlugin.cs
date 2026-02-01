using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.HardwareAcceleration;

/// <summary>
/// NUMA (Non-Uniform Memory Access) optimization plugin for high-performance computing.
/// Provides NUMA-aware memory allocation, CPU affinity management, and interrupt affinity inspection.
/// Supports both Linux (libnuma) and Windows (NUMA APIs) platforms.
/// </summary>
/// <remarks>
/// <para>
/// NUMA systems have multiple memory controllers with different access latencies.
/// Optimizing data placement and thread affinity can significantly improve performance.
/// </para>
/// <para>
/// Key features:
/// - Query NUMA topology (node count, CPUs per node)
/// - Allocate memory on specific NUMA nodes
/// - Set CPU affinity for threads
/// - Query NIC interrupt affinity for optimal I/O thread placement
/// </para>
/// </remarks>
public class NumaOptimizerPlugin : FeaturePluginBase
{
    #region Native P/Invoke - Linux

    /// <summary>
    /// libnuma availability check.
    /// </summary>
    [DllImport("libnuma", EntryPoint = "numa_available", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern int numa_available();

    /// <summary>
    /// Get the maximum NUMA node number.
    /// </summary>
    [DllImport("libnuma", EntryPoint = "numa_max_node", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern int numa_max_node();

    /// <summary>
    /// Get the number of CPUs.
    /// </summary>
    [DllImport("libnuma", EntryPoint = "numa_num_configured_cpus", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern int numa_num_configured_cpus();

    /// <summary>
    /// Get the NUMA node for the current CPU.
    /// </summary>
    [DllImport("libnuma", EntryPoint = "numa_node_of_cpu", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern int numa_node_of_cpu(int cpu);

    /// <summary>
    /// Allocate memory on a specific NUMA node.
    /// </summary>
    [DllImport("libnuma", EntryPoint = "numa_alloc_onnode", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern IntPtr numa_alloc_onnode(UIntPtr size, int node);

    /// <summary>
    /// Free NUMA-allocated memory.
    /// </summary>
    [DllImport("libnuma", EntryPoint = "numa_free", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern void numa_free(IntPtr ptr, UIntPtr size);

    /// <summary>
    /// Set thread CPU affinity.
    /// </summary>
    [DllImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern int sched_setaffinity(int pid, UIntPtr cpusetsize, ref ulong mask);

    /// <summary>
    /// Get thread CPU affinity.
    /// </summary>
    [DllImport("libc", EntryPoint = "sched_getaffinity", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern int sched_getaffinity(int pid, UIntPtr cpusetsize, ref ulong mask);

    /// <summary>
    /// Get current CPU.
    /// </summary>
    [DllImport("libc", EntryPoint = "sched_getcpu", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern int sched_getcpu();

    #endregion

    #region Native P/Invoke - Windows

    /// <summary>
    /// Windows: Get the highest NUMA node number.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern bool GetNumaHighestNodeNumber(out uint HighestNodeNumber);

    /// <summary>
    /// Windows: Get the NUMA node for a processor.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern bool GetNumaProcessorNode(byte Processor, out byte NodeNumber);

    /// <summary>
    /// Windows: Allocate memory on a specific NUMA node.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr VirtualAllocExNuma(
        IntPtr hProcess,
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint flAllocationType,
        uint flProtect,
        uint nndPreferred);

    /// <summary>
    /// Windows: Free virtual memory.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    /// <summary>
    /// Windows: Get current process handle.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Windows: Get current thread handle.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr GetCurrentThread();

    /// <summary>
    /// Windows: Set thread affinity mask.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

    /// <summary>
    /// Windows: Get current processor number.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern uint GetCurrentProcessorNumber();

    // Windows constants
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    #endregion

    #region Fields

    private bool _initialized;
    private bool _numaAvailable;
    private int _nodeCount;
    private int _cpuCount;
    private readonly ConcurrentDictionary<IntPtr, NumaAllocation> _allocations = new();
    private long _totalAllocated;
    private long _totalFreed;

    #endregion

    #region Plugin Properties

    /// <inheritdoc />
    public override string Id => "datawarehouse.hwaccel.numa-optimizer";

    /// <inheritdoc />
    public override string Name => "NUMA Optimizer";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>
    /// Gets whether NUMA optimization is available on this system.
    /// </summary>
    public bool IsAvailable => _numaAvailable;

    /// <summary>
    /// Gets whether the plugin has been initialized.
    /// </summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Gets the number of NUMA nodes in the system.
    /// Returns 1 for non-NUMA systems.
    /// </summary>
    public int NodeCount => _nodeCount;

    /// <summary>
    /// Gets the total number of CPUs in the system.
    /// </summary>
    public int CpuCount => _cpuCount;

    /// <summary>
    /// Gets the total bytes currently allocated via NUMA-aware allocation.
    /// </summary>
    public long TotalBytesAllocated => Interlocked.Read(ref _totalAllocated) - Interlocked.Read(ref _totalFreed);

    #endregion

    #region Lifecycle Methods

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        if (_initialized) return Task.CompletedTask;

        _cpuCount = Environment.ProcessorCount;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            InitializeLinux();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            InitializeWindows();
        }
        else
        {
            // Unsupported platform - single NUMA node fallback
            _numaAvailable = false;
            _nodeCount = 1;
        }

        _initialized = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task StopAsync()
    {
        // Free any remaining allocations
        foreach (var allocation in _allocations.Values)
        {
            try
            {
                FreeNumaMemory(allocation.Pointer, allocation.Size);
            }
            catch
            {
                // Best effort cleanup
            }
        }
        _allocations.Clear();

        _initialized = false;
        return Task.CompletedTask;
    }

    #endregion

    #region Public NUMA Methods

    /// <summary>
    /// Gets the number of NUMA nodes in the system.
    /// </summary>
    /// <returns>Number of NUMA nodes (minimum 1).</returns>
    public int GetNumaNodeCount()
    {
        EnsureInitialized();
        return _nodeCount;
    }

    /// <summary>
    /// Gets the NUMA node ID for the CPU running the current thread.
    /// </summary>
    /// <returns>NUMA node ID (0-based), or 0 if NUMA is not available.</returns>
    public int GetLocalNumaNode()
    {
        EnsureInitialized();

        if (!_numaAvailable)
            return 0;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return GetLocalNumaNodeLinux();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetLocalNumaNodeWindows();
            }
        }
        catch
        {
            // Fallback on error
        }

        return 0;
    }

    /// <summary>
    /// Allocates memory on a specific NUMA node.
    /// </summary>
    /// <param name="size">Size in bytes to allocate.</param>
    /// <param name="nodeId">NUMA node ID (0-based).</param>
    /// <returns>Handle to the allocated memory.</returns>
    /// <exception cref="ArgumentException">Thrown when nodeId is out of range.</exception>
    /// <exception cref="OutOfMemoryException">Thrown when allocation fails.</exception>
    public NumaMemoryHandle AllocateOnNode(long size, int nodeId)
    {
        EnsureInitialized();

        if (nodeId < 0 || nodeId >= _nodeCount)
            throw new ArgumentOutOfRangeException(nameof(nodeId), $"Node ID must be between 0 and {_nodeCount - 1}");

        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive");

        IntPtr ptr;

        if (!_numaAvailable)
        {
            // Fallback to regular allocation
            ptr = Marshal.AllocHGlobal((int)size);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            ptr = AllocateOnNodeLinux(size, nodeId);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ptr = AllocateOnNodeWindows(size, nodeId);
        }
        else
        {
            ptr = Marshal.AllocHGlobal((int)size);
        }

        if (ptr == IntPtr.Zero)
        {
            throw new OutOfMemoryException($"Failed to allocate {size} bytes on NUMA node {nodeId}");
        }

        var allocation = new NumaAllocation
        {
            Pointer = ptr,
            Size = size,
            NodeId = nodeId,
            AllocatedAt = DateTime.UtcNow
        };

        _allocations[ptr] = allocation;
        Interlocked.Add(ref _totalAllocated, size);

        return new NumaMemoryHandle(ptr, size, nodeId, this);
    }

    /// <summary>
    /// Frees NUMA-allocated memory.
    /// </summary>
    /// <param name="pointer">Pointer to the memory to free.</param>
    /// <param name="size">Size of the allocation.</param>
    internal void FreeNumaMemory(IntPtr pointer, long size)
    {
        if (pointer == IntPtr.Zero) return;

        try
        {
            if (!_numaAvailable)
            {
                Marshal.FreeHGlobal(pointer);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                FreeNumaMemoryLinux(pointer, size);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                FreeNumaMemoryWindows(pointer);
            }
            else
            {
                Marshal.FreeHGlobal(pointer);
            }

            _allocations.TryRemove(pointer, out _);
            Interlocked.Add(ref _totalFreed, size);
        }
        catch
        {
            // Best effort - don't throw on free
        }
    }

    /// <summary>
    /// Sets the CPU affinity for the current thread.
    /// </summary>
    /// <param name="cpuSet">Set of CPU IDs to which the thread should be bound.</param>
    /// <returns>True if successful, false otherwise.</returns>
    /// <exception cref="ArgumentException">Thrown when cpuSet is empty.</exception>
    public bool SetCpuAffinity(IEnumerable<int> cpuSet)
    {
        EnsureInitialized();

        var cpuList = cpuSet.ToList();
        if (cpuList.Count == 0)
            throw new ArgumentException("CPU set cannot be empty", nameof(cpuSet));

        // Validate CPU IDs
        foreach (var cpu in cpuList)
        {
            if (cpu < 0 || cpu >= _cpuCount)
                throw new ArgumentOutOfRangeException(nameof(cpuSet), $"CPU {cpu} is out of range (0-{_cpuCount - 1})");
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return SetCpuAffinityLinux(cpuList);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return SetCpuAffinityWindows(cpuList);
            }
        }
        catch
        {
            // Affinity change failed
        }

        return false;
    }

    /// <summary>
    /// Sets the CPU affinity for the current thread to all CPUs on a specific NUMA node.
    /// </summary>
    /// <param name="nodeId">NUMA node ID.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public bool SetCpuAffinityToNode(int nodeId)
    {
        EnsureInitialized();

        if (nodeId < 0 || nodeId >= _nodeCount)
            throw new ArgumentOutOfRangeException(nameof(nodeId), $"Node ID must be between 0 and {_nodeCount - 1}");

        var cpusOnNode = GetCpusOnNode(nodeId);
        if (cpusOnNode.Count == 0)
            return false;

        return SetCpuAffinity(cpusOnNode);
    }

    /// <summary>
    /// Gets the list of CPUs on a specific NUMA node.
    /// </summary>
    /// <param name="nodeId">NUMA node ID.</param>
    /// <returns>List of CPU IDs on the specified node.</returns>
    public IReadOnlyList<int> GetCpusOnNode(int nodeId)
    {
        EnsureInitialized();

        if (nodeId < 0 || nodeId >= _nodeCount)
            throw new ArgumentOutOfRangeException(nameof(nodeId), $"Node ID must be between 0 and {_nodeCount - 1}");

        var cpus = new List<int>();

        if (!_numaAvailable)
        {
            // All CPUs on node 0 for non-NUMA systems
            if (nodeId == 0)
            {
                for (int i = 0; i < _cpuCount; i++)
                    cpus.Add(i);
            }
            return cpus;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return GetCpusOnNodeLinux(nodeId);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetCpusOnNodeWindows(nodeId);
        }

        // Fallback: assume even distribution
        var cpusPerNode = _cpuCount / _nodeCount;
        var startCpu = nodeId * cpusPerNode;
        var endCpu = nodeId == _nodeCount - 1 ? _cpuCount : startCpu + cpusPerNode;

        for (int i = startCpu; i < endCpu; i++)
            cpus.Add(i);

        return cpus;
    }

    /// <summary>
    /// Gets the interrupt affinity for network interfaces.
    /// Useful for placing I/O threads near the NIC's interrupt handlers.
    /// </summary>
    /// <returns>Dictionary mapping network interface names to their interrupt CPU affinities.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> GetInterruptAffinity()
    {
        EnsureInitialized();

        var result = new Dictionary<string, IReadOnlyList<int>>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return GetInterruptAffinityLinux();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return GetInterruptAffinityWindows();
        }

        return result;
    }

    /// <summary>
    /// Gets NUMA topology information for the system.
    /// </summary>
    /// <returns>NUMA topology information.</returns>
    public NumaTopology GetTopology()
    {
        EnsureInitialized();

        var nodes = new List<NumaNodeInfo>();
        for (int i = 0; i < _nodeCount; i++)
        {
            var cpusOnNode = GetCpusOnNode(i);
            nodes.Add(new NumaNodeInfo
            {
                NodeId = i,
                CpuCount = cpusOnNode.Count,
                CpuIds = cpusOnNode.ToArray()
            });
        }

        return new NumaTopology
        {
            NodeCount = _nodeCount,
            TotalCpuCount = _cpuCount,
            IsNumaAvailable = _numaAvailable,
            Nodes = nodes
        };
    }

    /// <summary>
    /// Gets NUMA statistics.
    /// </summary>
    /// <returns>Current NUMA statistics.</returns>
    public NumaStatistics GetStatistics()
    {
        return new NumaStatistics
        {
            IsAvailable = _numaAvailable,
            IsInitialized = _initialized,
            NodeCount = _nodeCount,
            CpuCount = _cpuCount,
            CurrentNode = GetLocalNumaNode(),
            TotalBytesAllocated = TotalBytesAllocated,
            ActiveAllocations = _allocations.Count
        };
    }

    #endregion

    #region Private Platform-Specific Methods

    [SupportedOSPlatform("linux")]
    private void InitializeLinux()
    {
        try
        {
            if (numa_available() >= 0)
            {
                _numaAvailable = true;
                _nodeCount = numa_max_node() + 1;
            }
            else
            {
                _numaAvailable = false;
                _nodeCount = 1;
            }
        }
        catch (DllNotFoundException)
        {
            // libnuma not installed
            _numaAvailable = false;
            _nodeCount = 1;
        }
        catch (EntryPointNotFoundException)
        {
            // Old version of libnuma
            _numaAvailable = false;
            _nodeCount = 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private void InitializeWindows()
    {
        try
        {
            if (GetNumaHighestNodeNumber(out uint highestNode))
            {
                _nodeCount = (int)highestNode + 1;
                _numaAvailable = _nodeCount > 1;
            }
            else
            {
                _numaAvailable = false;
                _nodeCount = 1;
            }
        }
        catch
        {
            _numaAvailable = false;
            _nodeCount = 1;
        }
    }

    [SupportedOSPlatform("linux")]
    private int GetLocalNumaNodeLinux()
    {
        var cpu = sched_getcpu();
        if (cpu < 0) return 0;
        return numa_node_of_cpu(cpu);
    }

    [SupportedOSPlatform("windows")]
    private int GetLocalNumaNodeWindows()
    {
        var processor = (byte)GetCurrentProcessorNumber();
        if (GetNumaProcessorNode(processor, out byte node))
            return node;
        return 0;
    }

    [SupportedOSPlatform("linux")]
    private IntPtr AllocateOnNodeLinux(long size, int nodeId)
    {
        return numa_alloc_onnode(new UIntPtr((ulong)size), nodeId);
    }

    [SupportedOSPlatform("windows")]
    private IntPtr AllocateOnNodeWindows(long size, int nodeId)
    {
        return VirtualAllocExNuma(
            GetCurrentProcess(),
            IntPtr.Zero,
            new UIntPtr((ulong)size),
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE,
            (uint)nodeId);
    }

    [SupportedOSPlatform("linux")]
    private void FreeNumaMemoryLinux(IntPtr pointer, long size)
    {
        numa_free(pointer, new UIntPtr((ulong)size));
    }

    [SupportedOSPlatform("windows")]
    private void FreeNumaMemoryWindows(IntPtr pointer)
    {
        VirtualFree(pointer, UIntPtr.Zero, MEM_RELEASE);
    }

    [SupportedOSPlatform("linux")]
    private bool SetCpuAffinityLinux(IList<int> cpuSet)
    {
        ulong mask = 0;
        foreach (var cpu in cpuSet)
        {
            if (cpu < 64) // ulong has 64 bits
                mask |= 1UL << cpu;
        }

        return sched_setaffinity(0, new UIntPtr((ulong)sizeof(ulong)), ref mask) == 0;
    }

    [SupportedOSPlatform("windows")]
    private bool SetCpuAffinityWindows(IList<int> cpuSet)
    {
        ulong mask = 0;
        foreach (var cpu in cpuSet)
        {
            if (cpu < 64)
                mask |= 1UL << cpu;
        }

        var result = SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(mask));
        return result != UIntPtr.Zero;
    }

    [SupportedOSPlatform("linux")]
    private IReadOnlyList<int> GetCpusOnNodeLinux(int nodeId)
    {
        var cpus = new List<int>();
        for (int cpu = 0; cpu < _cpuCount; cpu++)
        {
            if (numa_node_of_cpu(cpu) == nodeId)
                cpus.Add(cpu);
        }
        return cpus;
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<int> GetCpusOnNodeWindows(int nodeId)
    {
        var cpus = new List<int>();
        for (byte cpu = 0; cpu < _cpuCount && cpu < 64; cpu++)
        {
            if (GetNumaProcessorNode(cpu, out byte node) && node == nodeId)
                cpus.Add(cpu);
        }
        return cpus;
    }

    [SupportedOSPlatform("linux")]
    private IReadOnlyDictionary<string, IReadOnlyList<int>> GetInterruptAffinityLinux()
    {
        var result = new Dictionary<string, IReadOnlyList<int>>();

        try
        {
            // Read /proc/interrupts to find network device interrupts
            if (!File.Exists("/proc/interrupts"))
                return result;

            var lines = File.ReadAllLines("/proc/interrupts");
            var networkInterrupts = new Dictionary<string, List<int>>();

            foreach (var line in lines.Skip(1)) // Skip header
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < _cpuCount + 2)
                    continue;

                var deviceName = parts[^1];
                if (!deviceName.Contains("eth") && !deviceName.Contains("eno") &&
                    !deviceName.Contains("ens") && !deviceName.Contains("enp") &&
                    !deviceName.Contains("mlx") && !deviceName.Contains("bnx"))
                    continue;

                // Parse IRQ affinity from /proc/irq/{irq}/smp_affinity
                var irqNum = parts[0].TrimEnd(':');
                if (int.TryParse(irqNum, out var irq))
                {
                    var affinity = GetIrqAffinity(irq);
                    if (affinity.Count > 0)
                    {
                        if (!networkInterrupts.ContainsKey(deviceName))
                            networkInterrupts[deviceName] = new List<int>();
                        networkInterrupts[deviceName].AddRange(affinity);
                    }
                }
            }

            foreach (var kvp in networkInterrupts)
            {
                result[kvp.Key] = kvp.Value.Distinct().ToList();
            }
        }
        catch
        {
            // Best effort - return empty on error
        }

        return result;
    }

    [SupportedOSPlatform("linux")]
    private static List<int> GetIrqAffinity(int irq)
    {
        var cpus = new List<int>();
        var affinityPath = $"/proc/irq/{irq}/smp_affinity";

        try
        {
            if (File.Exists(affinityPath))
            {
                var hex = File.ReadAllText(affinityPath).Trim().Replace(",", "");
                // Parse hex mask to CPU list
                if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var mask))
                {
                    for (int i = 0; i < 64; i++)
                    {
                        if ((mask & (1UL << i)) != 0)
                            cpus.Add(i);
                    }
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return cpus;
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyDictionary<string, IReadOnlyList<int>> GetInterruptAffinityWindows()
    {
        // Windows doesn't expose interrupt affinity in the same way
        // This would require WMI queries or registry access
        return new Dictionary<string, IReadOnlyList<int>>();
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "NUMA";
        metadata["IsAvailable"] = _numaAvailable;
        metadata["IsInitialized"] = _initialized;
        metadata["NodeCount"] = _nodeCount;
        metadata["CpuCount"] = _cpuCount;
        metadata["Platform"] = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
                               RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Other";
        return metadata;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Handle to NUMA-allocated memory.
/// Implements IDisposable for automatic cleanup.
/// </summary>
public sealed class NumaMemoryHandle : IDisposable
{
    private readonly NumaOptimizerPlugin _plugin;
    private bool _disposed;

    /// <summary>
    /// Gets the pointer to the allocated memory.
    /// </summary>
    public IntPtr Pointer { get; }

    /// <summary>
    /// Gets the size of the allocation in bytes.
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// Gets the NUMA node ID where the memory was allocated.
    /// </summary>
    public int NodeId { get; }

    /// <summary>
    /// Creates a new NUMA memory handle.
    /// </summary>
    internal NumaMemoryHandle(IntPtr pointer, long size, int nodeId, NumaOptimizerPlugin plugin)
    {
        Pointer = pointer;
        Size = size;
        NodeId = nodeId;
        _plugin = plugin;
    }

    /// <summary>
    /// Gets a Span over the allocated memory.
    /// </summary>
    /// <returns>Span of bytes representing the allocated memory.</returns>
    public unsafe Span<byte> AsSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Span<byte>(Pointer.ToPointer(), (int)Math.Min(Size, int.MaxValue));
    }

    /// <summary>
    /// Gets a Span over a portion of the allocated memory.
    /// </summary>
    /// <param name="offset">Offset from the start of the allocation.</param>
    /// <param name="length">Length of the span.</param>
    /// <returns>Span of bytes representing the specified portion.</returns>
    public unsafe Span<byte> AsSpan(int offset, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || length < 0 || offset + length > Size)
            throw new ArgumentOutOfRangeException();
        return new Span<byte>((byte*)Pointer.ToPointer() + offset, length);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _plugin.FreeNumaMemory(Pointer, Size);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer to ensure cleanup.
    /// </summary>
    ~NumaMemoryHandle()
    {
        Dispose();
    }
}

/// <summary>
/// Internal tracking of NUMA allocations.
/// </summary>
internal class NumaAllocation
{
    public IntPtr Pointer { get; init; }
    public long Size { get; init; }
    public int NodeId { get; init; }
    public DateTime AllocatedAt { get; init; }
}

/// <summary>
/// Information about a NUMA node.
/// </summary>
public class NumaNodeInfo
{
    /// <summary>
    /// Gets or sets the NUMA node ID.
    /// </summary>
    public int NodeId { get; init; }

    /// <summary>
    /// Gets or sets the number of CPUs on this node.
    /// </summary>
    public int CpuCount { get; init; }

    /// <summary>
    /// Gets or sets the array of CPU IDs on this node.
    /// </summary>
    public int[] CpuIds { get; init; } = Array.Empty<int>();
}

/// <summary>
/// NUMA topology information.
/// </summary>
public class NumaTopology
{
    /// <summary>
    /// Gets or sets the number of NUMA nodes.
    /// </summary>
    public int NodeCount { get; init; }

    /// <summary>
    /// Gets or sets the total CPU count across all nodes.
    /// </summary>
    public int TotalCpuCount { get; init; }

    /// <summary>
    /// Gets or sets whether NUMA is available on this system.
    /// </summary>
    public bool IsNumaAvailable { get; init; }

    /// <summary>
    /// Gets or sets the list of NUMA node information.
    /// </summary>
    public IReadOnlyList<NumaNodeInfo> Nodes { get; init; } = Array.Empty<NumaNodeInfo>();
}

/// <summary>
/// NUMA optimizer statistics.
/// </summary>
public class NumaStatistics
{
    /// <summary>
    /// Gets or sets whether NUMA is available.
    /// </summary>
    public bool IsAvailable { get; init; }

    /// <summary>
    /// Gets or sets whether the optimizer is initialized.
    /// </summary>
    public bool IsInitialized { get; init; }

    /// <summary>
    /// Gets or sets the number of NUMA nodes.
    /// </summary>
    public int NodeCount { get; init; }

    /// <summary>
    /// Gets or sets the total CPU count.
    /// </summary>
    public int CpuCount { get; init; }

    /// <summary>
    /// Gets or sets the current NUMA node.
    /// </summary>
    public int CurrentNode { get; init; }

    /// <summary>
    /// Gets or sets the total bytes currently allocated.
    /// </summary>
    public long TotalBytesAllocated { get; init; }

    /// <summary>
    /// Gets or sets the number of active allocations.
    /// </summary>
    public int ActiveAllocations { get; init; }
}

#endregion
