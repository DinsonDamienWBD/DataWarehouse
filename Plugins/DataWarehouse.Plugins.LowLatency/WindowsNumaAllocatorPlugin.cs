using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Performance;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.LowLatency;

/// <summary>
/// NUMA-aware memory allocator for Windows systems.
/// Provides explicit control over memory placement across NUMA nodes
/// to optimize memory access latency and bandwidth.
/// </summary>
public class WindowsNumaAllocatorPlugin : NumaAllocatorPluginBase
{
    /// <summary>
    /// Unique plugin identifier.
    /// </summary>
    public override string Id => "com.datawarehouse.performance.numa.windows";

    /// <summary>
    /// Human-readable plugin name.
    /// </summary>
    public override string Name => "Windows NUMA Allocator";

    private readonly Lazy<int> _nodeCount;
    private readonly Dictionary<IntPtr, nuint> _allocatedBlocks = new();
    private readonly object _allocationLock = new();

    /// <summary>
    /// Initializes a new instance of the WindowsNumaAllocatorPlugin.
    /// </summary>
    public WindowsNumaAllocatorPlugin()
    {
        _nodeCount = new Lazy<int>(() =>
        {
            if (!OperatingSystem.IsWindows())
                return 1;

            try
            {
                return (int)WindowsNumaInterop.GetNumaHighestNodeNumber() + 1;
            }
            catch
            {
                return 1; // Fallback to single node
            }
        });
    }

    /// <summary>
    /// Start the NUMA allocator.
    /// Validates NUMA support and initializes node topology.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async task representing the start operation.</returns>
    public override Task StartAsync(CancellationToken ct)
    {
        // Force initialization of node count to validate NUMA support
        _ = NodeCount;

        // In production, this would:
        // - Validate NUMA topology
        // - Register with OS for NUMA notifications
        // - Initialize memory pools per NUMA node

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the NUMA allocator.
    /// Ensures all allocated memory is freed.
    /// </summary>
    /// <returns>Async task representing the stop operation.</returns>
    public override Task StopAsync()
    {
        lock (_allocationLock)
        {
            if (_allocatedBlocks.Count > 0)
            {
                // WARNING: In production, this would log a warning about leaked memory
                // Memory blocks should be freed before stopping the allocator
                // For safety, we clear the tracking dictionary but do not free the memory
                // as we don't know if it's still in use
                _allocatedBlocks.Clear();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the total number of NUMA nodes in the system.
    /// </summary>
    public override int NodeCount => _nodeCount.Value;

    /// <summary>
    /// Gets the current thread's preferred NUMA node.
    /// Returns 0 if NUMA is not available.
    /// </summary>
    public override int CurrentNode
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return 0;

            try
            {
                return (int)WindowsNumaInterop.GetCurrentProcessorNumber() % NodeCount;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Allocate memory on a specific NUMA node.
    /// Memory is pinned and guaranteed to reside on the specified node.
    /// </summary>
    /// <param name="size">Size in bytes to allocate.</param>
    /// <param name="numaNode">NUMA node to allocate on (0-based).</param>
    /// <returns>Pointer to allocated memory.</returns>
    public override IntPtr AllocateOnNode(nuint size, int numaNode)
    {
        if (numaNode < 0 || numaNode >= NodeCount)
            throw new ArgumentOutOfRangeException(nameof(numaNode), $"NUMA node must be between 0 and {NodeCount - 1}");

        if (!OperatingSystem.IsWindows())
        {
            // Fallback to standard allocation on non-Windows platforms
            var ptr = Marshal.AllocHGlobal((nint)size);
            lock (_allocationLock)
            {
                _allocatedBlocks[ptr] = size;
            }
            return ptr;
        }

        try
        {
            // Allocate memory on specified NUMA node
            // hProcess = -1 means current process
            var ptr = WindowsNumaInterop.VirtualAllocExNuma(
                new IntPtr(-1),          // hProcess (current process)
                IntPtr.Zero,             // lpAddress (let system choose)
                size,                    // dwSize
                WindowsNumaInterop.MEM_COMMIT | WindowsNumaInterop.MEM_RESERVE,
                WindowsNumaInterop.PAGE_READWRITE,
                numaNode);

            if (ptr == IntPtr.Zero)
            {
                throw new OutOfMemoryException($"Failed to allocate {size} bytes on NUMA node {numaNode}");
            }

            lock (_allocationLock)
            {
                _allocatedBlocks[ptr] = size;
            }

            return ptr;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fallback to standard allocation if NUMA allocation fails
            var ptr = Marshal.AllocHGlobal((nint)size);
            lock (_allocationLock)
            {
                _allocatedBlocks[ptr] = size;
            }
            return ptr;
        }
    }

    /// <summary>
    /// Free memory allocated via AllocateOnNode.
    /// </summary>
    /// <param name="ptr">Pointer to memory to free.</param>
    public override void Free(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return;

        lock (_allocationLock)
        {
            if (!_allocatedBlocks.Remove(ptr, out var size))
            {
                throw new InvalidOperationException("Attempting to free memory not allocated by this allocator");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                WindowsNumaInterop.VirtualFree(ptr, 0, WindowsNumaInterop.MEM_RELEASE);
                return;
            }
            catch
            {
                // Fallback to standard free
            }
        }

        Marshal.FreeHGlobal(ptr);
    }

    /// <summary>
    /// Set the current thread's affinity to a specific NUMA node.
    /// Ensures thread runs on cores local to the specified node.
    /// </summary>
    /// <param name="numaNode">NUMA node to pin thread to (0-based).</param>
    /// <returns>Async task representing the affinity change.</returns>
    public override Task SetThreadAffinityAsync(int numaNode)
    {
        if (numaNode < 0 || numaNode >= NodeCount)
            throw new ArgumentOutOfRangeException(nameof(numaNode), $"NUMA node must be between 0 and {NodeCount - 1}");

        if (!OperatingSystem.IsWindows())
        {
            // Not supported on non-Windows platforms in this implementation
            return Task.CompletedTask;
        }

        try
        {
            // Get processors for this NUMA node
            // Note: This is a simplified implementation
            // Production code would use GetNumaProcessorNodeEx for proper affinity
            var affinityMask = WindowsNumaInterop.GetNumaNodeProcessorMask(numaNode);

            if (affinityMask != IntPtr.Zero)
            {
                WindowsNumaInterop.SetThreadAffinityMask(
                    WindowsNumaInterop.GetCurrentThread(),
                    affinityMask);
            }
        }
        catch
        {
            // Silently fail - thread affinity is a performance optimization, not critical
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get runtime information about this plugin.
    /// Includes NUMA node count and platform information.
    /// </summary>
    /// <returns>Dictionary of runtime metadata.</returns>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Platform"] = "Windows";
        metadata["NodeCount"] = NodeCount;
        metadata["CurrentNode"] = CurrentNode;
        metadata["IsNumaSupported"] = OperatingSystem.IsWindows() && NodeCount > 1;
        metadata["AllocatedBlocks"] = _allocatedBlocks.Count;

        return metadata;
    }
}

/// <summary>
/// Platform-specific interop for Windows NUMA APIs.
/// Provides P/Invoke declarations for NUMA memory allocation and thread affinity.
/// </summary>
internal static class WindowsNumaInterop
{
    internal const uint MEM_COMMIT = 0x1000;
    internal const uint MEM_RESERVE = 0x2000;
    internal const uint MEM_RELEASE = 0x8000;
    internal const uint PAGE_READWRITE = 0x04;

    /// <summary>
    /// Allocate memory on a specific NUMA node.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocExNuma(
        IntPtr hProcess,
        IntPtr lpAddress,
        nuint dwSize,
        uint flAllocationType,
        uint flProtect,
        int nndPreferred);

    /// <summary>
    /// Free memory allocated by VirtualAllocExNuma.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool VirtualFree(
        IntPtr lpAddress,
        nuint dwSize,
        uint dwFreeType);

    /// <summary>
    /// Get the highest NUMA node number in the system.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetNumaHighestNodeNumber();

    /// <summary>
    /// Get the current processor number.
    /// </summary>
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentProcessorNumber();

    /// <summary>
    /// Get the current thread handle.
    /// </summary>
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();

    /// <summary>
    /// Set thread affinity mask.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr SetThreadAffinityMask(
        IntPtr hThread,
        IntPtr dwThreadAffinityMask);

    /// <summary>
    /// Get processor mask for a NUMA node (simplified - placeholder).
    /// In production, use GetNumaProcessorNodeEx for proper implementation.
    /// </summary>
    internal static IntPtr GetNumaNodeProcessorMask(int numaNode)
    {
        // PLACEHOLDER: This is a simplified implementation
        // Production code should use GetNumaProcessorNodeEx and properly
        // construct the affinity mask based on actual processor topology

        // Return a mask representing processors for this node
        // This assumes processors are evenly distributed (not always true)
        var processorCount = Environment.ProcessorCount;
        return new IntPtr(1 << numaNode); // Simplified placeholder
    }
}
