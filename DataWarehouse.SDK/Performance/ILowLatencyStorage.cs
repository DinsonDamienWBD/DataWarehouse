namespace DataWarehouse.SDK.Performance;

/// <summary>
/// Storage tier classification based on latency characteristics.
/// Defines the expected performance tier for storage operations.
/// </summary>
public enum LatencyTier
{
    /// <summary>
    /// Ultra-low latency tier (&lt;100μs).
    /// Typically Intel Optane PMEM or DRAM-backed storage.
    /// </summary>
    UltraLow,

    /// <summary>
    /// Low latency tier (&lt;1ms).
    /// Typically NVMe SSDs with direct I/O.
    /// </summary>
    Low,

    /// <summary>
    /// Standard latency tier (&lt;10ms).
    /// Typically SATA SSDs or cached HDDs.
    /// </summary>
    Standard,

    /// <summary>
    /// Archive latency tier (&gt;100ms).
    /// Typically spinning disks or tape libraries.
    /// </summary>
    Archive
}

/// <summary>
/// Hardware acceleration capabilities available for storage and network operations.
/// Used to detect and enable platform-specific optimizations.
/// </summary>
[Flags]
public enum HardwareCapabilities
{
    /// <summary>
    /// No hardware acceleration available.
    /// </summary>
    None = 0,

    /// <summary>
    /// Intel AES-NI instructions for hardware-accelerated encryption.
    /// </summary>
    AesNi = 1,

    /// <summary>
    /// AVX-512/SVE SIMD instructions for parallel processing.
    /// </summary>
    AvxSimd = 2,

    /// <summary>
    /// RDMA support (InfiniBand/RoCE) for zero-copy networking.
    /// </summary>
    Rdma = 4,

    /// <summary>
    /// NVMe over Fabrics for remote direct storage access.
    /// </summary>
    NvmeOf = 8,

    /// <summary>
    /// DPDK (Data Plane Development Kit) for userspace networking.
    /// </summary>
    Dpdk = 16,

    /// <summary>
    /// Linux io_uring for efficient async I/O.
    /// </summary>
    IoUring = 32,

    /// <summary>
    /// Intel Optane persistent memory support.
    /// </summary>
    PersistentMemory = 64,

    /// <summary>
    /// Huge pages (2MB/1GB) for TLB optimization.
    /// </summary>
    HugePages = 128,

    /// <summary>
    /// NUMA-aware memory allocation and thread pinning.
    /// </summary>
    NumaAware = 256
}

/// <summary>
/// Low-latency storage provider interface optimized for sub-millisecond operations.
/// Provides direct memory access, hardware acceleration, and minimal overhead I/O.
/// </summary>
public interface ILowLatencyStorage
{
    /// <summary>
    /// Gets the latency tier this storage provider operates in.
    /// </summary>
    LatencyTier Tier { get; }

    /// <summary>
    /// Gets the hardware acceleration capabilities available on this system.
    /// </summary>
    HardwareCapabilities AvailableCapabilities { get; }

    /// <summary>
    /// Direct memory read - bypasses OS page cache for predictable latency.
    /// Uses O_DIRECT or similar mechanisms to avoid cache pollution.
    /// </summary>
    /// <param name="key">Storage key to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only memory containing the data.</returns>
    ValueTask<ReadOnlyMemory<byte>> ReadDirectAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Direct memory write with optional synchronous durability.
    /// Uses O_DIRECT and optional fsync/fdatasync for durability guarantees.
    /// </summary>
    /// <param name="key">Storage key to write.</param>
    /// <param name="data">Data to write.</param>
    /// <param name="sync">If true, ensures data is flushed to stable storage before returning.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async task representing the write operation.</returns>
    ValueTask WriteDirectAsync(string key, ReadOnlyMemory<byte> data, bool sync = false, CancellationToken ct = default);

    /// <summary>
    /// Pre-warm data into the fastest available tier (e.g., load into DRAM cache).
    /// Useful for predictable latency in read-heavy workloads.
    /// </summary>
    /// <param name="keys">Array of keys to pre-warm.</param>
    /// <returns>Async task representing the pre-warming operation.</returns>
    Task PrewarmAsync(string[] keys);

    /// <summary>
    /// Get latency statistics for monitoring and SLA tracking.
    /// Provides percentile-based latency metrics for read and write operations.
    /// </summary>
    /// <returns>Latency statistics snapshot.</returns>
    Task<LatencyStatistics> GetLatencyStatsAsync();
}

/// <summary>
/// Latency statistics for monitoring storage performance.
/// Captures percentile-based latency metrics and operation counts.
/// </summary>
/// <param name="P50ReadMicroseconds">Median read latency in microseconds.</param>
/// <param name="P99ReadMicroseconds">99th percentile read latency in microseconds.</param>
/// <param name="P50WriteMicroseconds">Median write latency in microseconds.</param>
/// <param name="P99WriteMicroseconds">99th percentile write latency in microseconds.</param>
/// <param name="ReadCount">Total number of read operations measured.</param>
/// <param name="WriteCount">Total number of write operations measured.</param>
/// <param name="MeasuredAt">Timestamp when these statistics were captured.</param>
public record LatencyStatistics(
    double P50ReadMicroseconds,
    double P99ReadMicroseconds,
    double P50WriteMicroseconds,
    double P99WriteMicroseconds,
    long ReadCount,
    long WriteCount,
    DateTimeOffset MeasuredAt
);

/// <summary>
/// RDMA-enabled network transport for zero-copy remote memory access.
/// Supports InfiniBand and RoCE (RDMA over Converged Ethernet).
/// </summary>
public interface IRdmaTransport
{
    /// <summary>
    /// Gets whether RDMA hardware is available and initialized.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Establish an RDMA connection to a remote host.
    /// Performs connection setup and memory region registration.
    /// </summary>
    /// <param name="remoteHost">Remote host address.</param>
    /// <param name="port">Remote port number.</param>
    /// <returns>RDMA connection handle.</returns>
    Task<RdmaConnection> ConnectAsync(string remoteHost, int port);

    /// <summary>
    /// Read from remote memory using RDMA READ operation.
    /// Bypasses remote CPU for true zero-copy transfer.
    /// </summary>
    /// <param name="conn">RDMA connection.</param>
    /// <param name="remoteAddr">Remote memory address.</param>
    /// <param name="localBuffer">Local buffer to read into.</param>
    /// <returns>Number of bytes read.</returns>
    Task<int> ReadRemoteAsync(RdmaConnection conn, ulong remoteAddr, Memory<byte> localBuffer);

    /// <summary>
    /// Write to remote memory using RDMA WRITE operation.
    /// Bypasses remote CPU for true zero-copy transfer.
    /// </summary>
    /// <param name="conn">RDMA connection.</param>
    /// <param name="remoteAddr">Remote memory address.</param>
    /// <param name="data">Data to write.</param>
    /// <returns>Async task representing the write operation.</returns>
    Task WriteRemoteAsync(RdmaConnection conn, ulong remoteAddr, ReadOnlyMemory<byte> data);
}

/// <summary>
/// RDMA connection handle with registered memory regions.
/// Disposable to ensure proper cleanup of RDMA resources.
/// </summary>
/// <param name="RemoteHost">Remote host address.</param>
/// <param name="Port">Remote port number.</param>
/// <param name="LocalKey">Local memory region key for RDMA operations.</param>
/// <param name="RemoteKey">Remote memory region key for RDMA operations.</param>
public record RdmaConnection(string RemoteHost, int Port, ulong LocalKey, ulong RemoteKey) : IAsyncDisposable
{
    /// <summary>
    /// Dispose RDMA connection and free registered memory regions.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Linux io_uring based async I/O provider for ultra-low overhead operations.
/// Provides kernel-bypass I/O with batched submission and completion.
/// </summary>
public interface IIoUringProvider
{
    /// <summary>
    /// Gets whether io_uring is supported on this kernel (Linux 5.1+).
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Submit a read operation to the io_uring submission queue.
    /// Operation completes asynchronously via completion queue.
    /// </summary>
    /// <param name="fd">File descriptor to read from.</param>
    /// <param name="buffer">Buffer to read into.</param>
    /// <param name="offset">File offset to read from.</param>
    /// <returns>Number of bytes read (awaited from completion queue).</returns>
    ValueTask<int> SubmitReadAsync(int fd, Memory<byte> buffer, long offset);

    /// <summary>
    /// Submit a write operation to the io_uring submission queue.
    /// Operation completes asynchronously via completion queue.
    /// </summary>
    /// <param name="fd">File descriptor to write to.</param>
    /// <param name="data">Data to write.</param>
    /// <param name="offset">File offset to write to.</param>
    /// <returns>Number of bytes written (awaited from completion queue).</returns>
    ValueTask<int> SubmitWriteAsync(int fd, ReadOnlyMemory<byte> data, long offset);

    /// <summary>
    /// Wait for I/O completions from the completion queue.
    /// Allows batching multiple operations for efficiency.
    /// </summary>
    /// <param name="minCompletions">Minimum number of completions to wait for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of completions processed.</returns>
    Task<int> WaitCompletionsAsync(int minCompletions, CancellationToken ct = default);
}

/// <summary>
/// NUMA-aware memory allocator for optimized memory placement.
/// Provides explicit control over memory allocation across NUMA nodes.
/// </summary>
public interface INumaAllocator
{
    /// <summary>
    /// Gets the total number of NUMA nodes in the system.
    /// </summary>
    int NodeCount { get; }

    /// <summary>
    /// Gets the current thread's preferred NUMA node.
    /// </summary>
    int CurrentNode { get; }

    /// <summary>
    /// Allocate memory on a specific NUMA node.
    /// Memory is pinned and guaranteed to reside on the specified node.
    /// </summary>
    /// <param name="size">Size in bytes to allocate.</param>
    /// <param name="numaNode">NUMA node to allocate on (0-based).</param>
    /// <returns>Pointer to allocated memory.</returns>
    IntPtr AllocateOnNode(nuint size, int numaNode);

    /// <summary>
    /// Free memory allocated via AllocateOnNode.
    /// </summary>
    /// <param name="ptr">Pointer to memory to free.</param>
    void Free(IntPtr ptr);

    /// <summary>
    /// Set the current thread's affinity to a specific NUMA node.
    /// Ensures thread runs on cores local to the specified node.
    /// </summary>
    /// <param name="numaNode">NUMA node to pin thread to (0-based).</param>
    /// <returns>Async task representing the affinity change.</returns>
    Task SetThreadAffinityAsync(int numaNode);
}
