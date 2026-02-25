using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Performance;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DataWarehouse.SDK.Contracts.Hierarchy;

// FUTURE: Low-latency I/O -- interfaces preserved for RDMA, io_uring, and NUMA-aware storage per AD-06.
// These types have zero current implementations but define contracts for future
// low-latency-capable storage plugins. Do NOT delete during dead code cleanup.

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Abstract base class for low-latency storage plugins.
/// Provides infrastructure for sub-millisecond storage operations with hardware acceleration.
/// Implements common patterns for direct I/O, pre-warming, and latency tracking.
/// </summary>
public abstract class LowLatencyStoragePluginBase : Hierarchy.DataPipelinePluginBase, ILowLatencyStorage, IIntelligenceAware
{
    #region Intelligence Socket

    public new bool IsIntelligenceAvailable { get; protected set; }

    /// <summary>Intelligence capabilities (explicit interface implementation to avoid name conflict with HardwareCapabilities).</summary>
    private IntelligenceCapabilities _intelligenceCapabilities = new();
    IntelligenceCapabilities IIntelligenceAware.AvailableCapabilities => _intelligenceCapabilities;

    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.lowlatency",
            DisplayName = $"{Name} - Low-Latency Storage",
            Description = $"Sub-millisecond storage at {Tier} tier",
            Category = CapabilityCategory.Storage,
            SubCategory = "LowLatency",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "low-latency", "performance", Tier.ToString().ToLowerInvariant() },
            SemanticDescription = "Use for ultra-low latency storage operations"
        }
    };

    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.lowlatency.capability",
                Topic = "performance.lowlatency",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Low-latency storage at {Tier} tier with hardware caps: {AvailableCapabilities}",
                Payload = new Dictionary<string, object>
                {
                    ["tier"] = Tier.ToString(),
                    ["ioDepth"] = DefaultIoDepth,
                    ["blockSize"] = DefaultBlockSize,
                    ["useDirectIo"] = UseDirectIo
                },
                Tags = new[] { "low-latency", "storage" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted latency optimization.
    /// </summary>
    protected virtual async Task<LatencyOptimizationHint?> RequestLatencyOptimizationAsync(LatencyStatistics stats, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask;
        return null;
    }

    #endregion

    /// <summary>
    /// Gets the latency tier this storage provider operates in.
    /// Must be implemented by derived classes based on hardware characteristics.
    /// </summary>
    public abstract LatencyTier Tier { get; }

    /// <summary>
    /// Gets the available hardware capabilities on this system.
    /// Default implementation auto-detects common capabilities.
    /// Override to provide platform-specific detection.
    /// </summary>
    public new virtual HardwareCapabilities AvailableCapabilities => DetectHardwareCapabilities();

    /// <summary>
    /// Default I/O queue depth for async operations.
    /// Higher values increase throughput but may increase latency variance.
    /// </summary>
    protected virtual int DefaultIoDepth => 128;

    /// <summary>
    /// Default block size for I/O operations (bytes).
    /// Aligned to common page sizes (4KB) for efficiency.
    /// </summary>
    protected virtual int DefaultBlockSize => 4096;

    /// <summary>
    /// Whether to use direct I/O (O_DIRECT) to bypass OS page cache.
    /// Set to false for workloads that benefit from page cache.
    /// </summary>
    protected virtual bool UseDirectIo => true;

    /// <summary>
    /// Read data without OS page cache involvement.
    /// Must be implemented by derived classes using platform-specific direct I/O.
    /// </summary>
    /// <param name="key">Storage key to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only memory containing the data.</returns>
    protected abstract ValueTask<ReadOnlyMemory<byte>> ReadWithoutCacheAsync(string key, CancellationToken ct);

    /// <summary>
    /// Write data without OS page cache involvement.
    /// Must be implemented by derived classes using platform-specific direct I/O.
    /// </summary>
    /// <param name="key">Storage key to write.</param>
    /// <param name="data">Data to write.</param>
    /// <param name="sync">If true, ensure data is flushed to stable storage.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async task representing the write operation.</returns>
    protected abstract ValueTask WriteWithoutCacheAsync(string key, ReadOnlyMemory<byte> data, bool sync, CancellationToken ct);

    /// <summary>
    /// Direct memory read - bypasses OS page cache for predictable latency.
    /// Delegates to ReadWithoutCacheAsync for platform-specific implementation.
    /// </summary>
    /// <param name="key">Storage key to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only memory containing the data.</returns>
    public ValueTask<ReadOnlyMemory<byte>> ReadDirectAsync(string key, CancellationToken ct = default)
        => ReadWithoutCacheAsync(key, ct);

    /// <summary>
    /// Direct memory write with optional synchronous durability.
    /// Delegates to WriteWithoutCacheAsync for platform-specific implementation.
    /// </summary>
    /// <param name="key">Storage key to write.</param>
    /// <param name="data">Data to write.</param>
    /// <param name="sync">If true, ensures data is flushed to stable storage before returning.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async task representing the write operation.</returns>
    public ValueTask WriteDirectAsync(string key, ReadOnlyMemory<byte> data, bool sync = false, CancellationToken ct = default)
        => WriteWithoutCacheAsync(key, data, sync, ct);

    #region StorageAddress Overloads (HAL-05)

    /// <summary>Read without cache using a StorageAddress. Override for native StorageAddress support.</summary>
    protected virtual ValueTask<ReadOnlyMemory<byte>> ReadWithoutCacheAsync(StorageAddress address, CancellationToken ct)
        => ReadWithoutCacheAsync(address.ToKey(), ct);

    /// <summary>Write without cache using a StorageAddress. Override for native StorageAddress support.</summary>
    protected virtual ValueTask WriteWithoutCacheAsync(StorageAddress address, ReadOnlyMemory<byte> data, bool sync, CancellationToken ct)
        => WriteWithoutCacheAsync(address.ToKey(), data, sync, ct);

    /// <summary>Direct memory read using a StorageAddress. Delegates to ReadWithoutCacheAsync(StorageAddress).</summary>
    public ValueTask<ReadOnlyMemory<byte>> ReadDirectAsync(StorageAddress address, CancellationToken ct = default)
        => ReadWithoutCacheAsync(address, ct);

    /// <summary>Direct memory write using a StorageAddress. Delegates to WriteWithoutCacheAsync(StorageAddress).</summary>
    public ValueTask WriteDirectAsync(StorageAddress address, ReadOnlyMemory<byte> data, bool sync = false, CancellationToken ct = default)
        => WriteWithoutCacheAsync(address, data, sync, ct);

    #endregion

    /// <summary>
    /// Pre-warm data into the fastest available tier.
    /// Default implementation is a no-op. Override to implement caching strategies.
    /// </summary>
    /// <param name="keys">Array of keys to pre-warm.</param>
    /// <returns>Async task representing the pre-warming operation.</returns>
    public virtual Task PrewarmAsync(string[] keys) => Task.CompletedTask;

    /// <summary>
    /// Get latency statistics for monitoring and SLA tracking.
    /// Must be implemented by derived classes to provide actual metrics.
    /// </summary>
    /// <returns>Latency statistics snapshot.</returns>
    public abstract Task<LatencyStatistics> GetLatencyStatsAsync();

    /// <summary>
    /// Detect available hardware capabilities on the current system.
    /// Uses runtime intrinsics and platform detection to identify acceleration features.
    /// </summary>
    /// <returns>Flags representing available hardware capabilities.</returns>
    protected virtual HardwareCapabilities DetectHardwareCapabilities()
    {
        var caps = HardwareCapabilities.None;

        // Detect Intel AES-NI
        if (System.Runtime.Intrinsics.X86.Aes.IsSupported)
        {
            caps |= HardwareCapabilities.AesNi;
        }

        // Detect AVX-512 SIMD
        if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported)
        {
            caps |= HardwareCapabilities.AvxSimd;
        }

        // Platform-specific capabilities would be detected here
        // RDMA, NVMe-oF, DPDK, io_uring, Persistent Memory, Huge Pages, NUMA
        // These typically require native interop or platform-specific libraries

        // Detect io_uring on Linux
        if (OperatingSystem.IsLinux())
        {
            // Would check kernel version >= 5.1
            // caps |= HardwareCapabilities.IoUring;
        }

        // Detect NUMA awareness
        if (Environment.ProcessorCount > 1)
        {
            // Simplified check - real implementation would query NUMA topology
            // caps |= HardwareCapabilities.NumaAware;
        }

        return caps;
    }

    /// <summary>
    /// Gets plugin category - always StorageProvider for storage plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.StorageProvider;
}

/// <summary>
/// Abstract base class for RDMA transport plugins.
/// Provides infrastructure for zero-copy remote memory access via InfiniBand/RoCE.
/// Requires platform-specific RDMA libraries (libibverbs on Linux, NetworkDirect on Windows).
/// </summary>
public abstract class RdmaTransportPluginBase : InfrastructurePluginBase, IRdmaTransport, IIntelligenceAware
{
    /// <inheritdoc/>
    public override string InfrastructureDomain => "RDMA";

    #region Intelligence Socket

    public new bool IsIntelligenceAvailable { get; protected set; }
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.rdma",
            DisplayName = $"{Name} - RDMA Transport",
            Description = "Zero-copy remote memory access via InfiniBand/RoCE",
            Category = CapabilityCategory.Infrastructure,
            SubCategory = "Transport",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "rdma", "infiniband", "low-latency" },
            SemanticDescription = "Use for zero-copy network transport"
        }
    };

    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.rdma.capability",
                Topic = "transport.rdma",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"RDMA transport, Available: {IsAvailable}",
                Payload = new Dictionary<string, object> { ["isAvailable"] = IsAvailable },
                Tags = new[] { "rdma", "transport" }
            }
        };
    }

    #endregion

    /// <summary>
    /// Gets whether RDMA hardware is available and initialized.
    /// Must be implemented by derived classes to check for RDMA NICs.
    /// </summary>
    public abstract bool IsAvailable { get; }

    /// <summary>
    /// Establish an RDMA connection to a remote host.
    /// Must be implemented by derived classes using platform RDMA APIs.
    /// </summary>
    /// <param name="remoteHost">Remote host address.</param>
    /// <param name="port">Remote port number.</param>
    /// <returns>RDMA connection handle with registered memory regions.</returns>
    public abstract Task<RdmaConnection> ConnectAsync(string remoteHost, int port);

    /// <summary>
    /// Read from remote memory using RDMA READ operation.
    /// Must be implemented by derived classes using RDMA verbs.
    /// </summary>
    /// <param name="conn">RDMA connection.</param>
    /// <param name="remoteAddr">Remote memory address.</param>
    /// <param name="localBuffer">Local buffer to read into.</param>
    /// <returns>Number of bytes read.</returns>
    public abstract Task<int> ReadRemoteAsync(RdmaConnection conn, ulong remoteAddr, Memory<byte> localBuffer);

    /// <summary>
    /// Write to remote memory using RDMA WRITE operation.
    /// Must be implemented by derived classes using RDMA verbs.
    /// </summary>
    /// <param name="conn">RDMA connection.</param>
    /// <param name="remoteAddr">Remote memory address.</param>
    /// <param name="data">Data to write.</param>
    /// <returns>Async task representing the write operation.</returns>
    public abstract Task WriteRemoteAsync(RdmaConnection conn, ulong remoteAddr, ReadOnlyMemory<byte> data);

    /// <summary>
    /// Gets plugin category - always Infrastructure for transport plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;
}

/// <summary>
/// Abstract base class for io_uring providers (Linux 5.1+ only).
/// Provides infrastructure for ultra-low overhead async I/O using kernel io_uring.
/// Requires liburing or direct syscall implementation.
/// </summary>
public abstract class IoUringProviderPluginBase : InfrastructurePluginBase, IIoUringProvider, IIntelligenceAware
{
    /// <inheritdoc/>
    public override string InfrastructureDomain => "IoUring";

    #region Intelligence Socket

    public new bool IsIntelligenceAvailable { get; protected set; }
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.iouring",
            DisplayName = $"{Name} - io_uring Provider",
            Description = "Linux io_uring for ultra-low overhead async I/O",
            Category = CapabilityCategory.Infrastructure,
            SubCategory = "IO",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "iouring", "linux", "async-io" },
            SemanticDescription = "Use for ultra-low overhead async I/O on Linux 5.1+"
        }
    };

    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.iouring.capability",
                Topic = "io.iouring",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"io_uring provider, Supported: {IsSupported}",
                Payload = new Dictionary<string, object> { ["isSupported"] = IsSupported },
                Tags = new[] { "iouring", "io" }
            }
        };
    }

    #endregion

    /// <summary>
    /// Gets whether io_uring is supported on this kernel.
    /// Default implementation checks for Linux and minimum kernel version.
    /// </summary>
    public virtual bool IsSupported => OperatingSystem.IsLinux() && CheckKernelVersion();

    /// <summary>
    /// Check if kernel version supports io_uring (5.1+).
    /// Must be implemented by derived classes to perform actual version check.
    /// </summary>
    /// <returns>True if kernel version is 5.1 or higher.</returns>
    protected abstract bool CheckKernelVersion();

    /// <summary>
    /// Submit a read operation to the io_uring submission queue.
    /// Must be implemented by derived classes using liburing or direct syscalls.
    /// </summary>
    /// <param name="fd">File descriptor to read from.</param>
    /// <param name="buffer">Buffer to read into.</param>
    /// <param name="offset">File offset to read from.</param>
    /// <returns>Number of bytes read (awaited from completion queue).</returns>
    public abstract ValueTask<int> SubmitReadAsync(int fd, Memory<byte> buffer, long offset);

    /// <summary>
    /// Submit a write operation to the io_uring submission queue.
    /// Must be implemented by derived classes using liburing or direct syscalls.
    /// </summary>
    /// <param name="fd">File descriptor to write to.</param>
    /// <param name="data">Data to write.</param>
    /// <param name="offset">File offset to write to.</param>
    /// <returns>Number of bytes written (awaited from completion queue).</returns>
    public abstract ValueTask<int> SubmitWriteAsync(int fd, ReadOnlyMemory<byte> data, long offset);

    /// <summary>
    /// Wait for I/O completions from the completion queue.
    /// Must be implemented by derived classes to handle completion events.
    /// </summary>
    /// <param name="minCompletions">Minimum number of completions to wait for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of completions processed.</returns>
    public abstract Task<int> WaitCompletionsAsync(int minCompletions, CancellationToken ct = default);

    /// <summary>
    /// Gets plugin category - always Infrastructure for I/O providers.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;
}

/// <summary>
/// Abstract base class for NUMA-aware memory allocator plugins.
/// Provides infrastructure for explicit NUMA node memory allocation and thread affinity.
/// Requires platform-specific NUMA APIs (libnuma on Linux, NUMA API on Windows).
/// </summary>
public abstract class NumaAllocatorPluginBase : InfrastructurePluginBase, INumaAllocator, IIntelligenceAware
{
    /// <inheritdoc/>
    public override string InfrastructureDomain => "NUMA";

    #region Intelligence Socket

    public new bool IsIntelligenceAvailable { get; protected set; }
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.numa",
            DisplayName = $"{Name} - NUMA Allocator",
            Description = "NUMA-aware memory allocation and thread affinity",
            Category = CapabilityCategory.Infrastructure,
            SubCategory = "Memory",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "numa", "memory", "affinity" },
            SemanticDescription = "Use for NUMA-optimized memory allocation"
        }
    };

    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.numa.capability",
                Topic = "memory.numa",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"NUMA allocator with {NodeCount} nodes",
                Payload = new Dictionary<string, object>
                {
                    ["nodeCount"] = NodeCount,
                    ["currentNode"] = CurrentNode
                },
                Tags = new[] { "numa", "memory" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted NUMA placement advice.
    /// </summary>
    protected virtual async Task<NumaPlacementHint?> RequestNumaPlacementAsync(long allocationSize, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask;
        return null;
    }

    #endregion

    /// <summary>
    /// Gets the total number of NUMA nodes in the system.
    /// Must be implemented by derived classes to query system topology.
    /// </summary>
    public abstract int NodeCount { get; }

    /// <summary>
    /// Gets the current thread's preferred NUMA node.
    /// Must be implemented by derived classes to query thread affinity.
    /// </summary>
    public abstract int CurrentNode { get; }

    /// <summary>
    /// Allocate memory on a specific NUMA node.
    /// Must be implemented by derived classes using platform NUMA APIs.
    /// </summary>
    /// <param name="size">Size in bytes to allocate.</param>
    /// <param name="numaNode">NUMA node to allocate on (0-based).</param>
    /// <returns>Pointer to allocated memory.</returns>
    public abstract IntPtr AllocateOnNode(nuint size, int numaNode);

    /// <summary>
    /// Free memory allocated via AllocateOnNode.
    /// Must be implemented by derived classes to properly release NUMA memory.
    /// </summary>
    /// <param name="ptr">Pointer to memory to free.</param>
    public abstract void Free(IntPtr ptr);

    /// <summary>
    /// Set the current thread's affinity to a specific NUMA node.
    /// Must be implemented by derived classes using platform threading APIs.
    /// </summary>
    /// <param name="numaNode">NUMA node to pin thread to (0-based).</param>
    /// <returns>Async task representing the affinity change.</returns>
    public abstract Task SetThreadAffinityAsync(int numaNode);

    /// <summary>
    /// Gets plugin category - always Infrastructure for memory allocators.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;
}

#region Stub Types for Intelligence Integration

/// <summary>Stub type for latency optimization hints from AI.</summary>
public record LatencyOptimizationHint(
    string RecommendedAction,
    int? SuggestedIoDepth,
    int? SuggestedBlockSize,
    bool? UseDirectIo,
    double ConfidenceScore);

/// <summary>Stub type for NUMA placement hints from AI.</summary>
public record NumaPlacementHint(
    int RecommendedNode,
    string Reason,
    double ConfidenceScore);

#endregion
