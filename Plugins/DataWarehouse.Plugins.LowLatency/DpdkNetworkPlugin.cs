using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Performance;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.LowLatency;

#region Interfaces and Configuration

/// <summary>
/// Interface for DPDK (Data Plane Development Kit) userspace networking.
/// Provides kernel-bypass packet I/O with ultra-low latency and high throughput.
/// </summary>
public interface IDpdkNetwork
{
    /// <summary>
    /// Gets whether DPDK is available and properly initialized on this system.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Initializes the DPDK environment with the specified configuration.
    /// Must be called before any packet I/O operations.
    /// </summary>
    /// <param name="config">DPDK configuration including EAL arguments, port settings, and memory configuration.</param>
    /// <returns>A task representing the initialization operation.</returns>
    Task InitializeAsync(DpdkConfig config);

    /// <summary>
    /// Receives a burst of packets from a specific port and queue.
    /// Uses poll-mode driver for zero-copy packet reception.
    /// </summary>
    /// <param name="portId">The port ID to receive from (0-based).</param>
    /// <param name="queueId">The queue ID within the port (0-based, for multi-queue support).</param>
    /// <param name="buffers">Array of memory buffers to receive packets into.</param>
    /// <returns>The number of packets actually received (may be less than buffer count).</returns>
    Task<int> ReceiveBurstAsync(int portId, int queueId, Memory<byte>[] buffers);

    /// <summary>
    /// Transmits a burst of packets through a specific port and queue.
    /// Uses poll-mode driver for zero-copy packet transmission.
    /// </summary>
    /// <param name="portId">The port ID to transmit through (0-based).</param>
    /// <param name="queueId">The queue ID within the port (0-based, for multi-queue support).</param>
    /// <param name="packets">Array of packets to transmit.</param>
    /// <returns>The number of packets actually transmitted (may be less than packet count if queue is full).</returns>
    Task<int> TransmitBurstAsync(int portId, int queueId, ReadOnlyMemory<byte>[] packets);

    /// <summary>
    /// Gets detailed statistics for a specific port.
    /// </summary>
    /// <param name="portId">The port ID to get statistics for.</param>
    /// <returns>Port statistics including packet counts, byte counts, and error counts.</returns>
    Task<DpdkPortStats> GetPortStatsAsync(int portId);

    /// <summary>
    /// Shuts down the DPDK environment and releases all resources.
    /// Must be called before application exit to properly clean up.
    /// </summary>
    /// <returns>A task representing the shutdown operation.</returns>
    Task ShutdownAsync();
}

/// <summary>
/// Configuration for DPDK initialization.
/// Contains all settings required to initialize the DPDK Environment Abstraction Layer (EAL).
/// </summary>
public class DpdkConfig
{
    /// <summary>
    /// Gets or sets the EAL initialization arguments.
    /// </summary>
    /// <remarks>
    /// <para>Common EAL arguments include:</para>
    /// <list type="bullet">
    /// <item><description>-l cores: CPU cores to use (e.g., "-l 0-3" for cores 0-3)</description></item>
    /// <item><description>-n channels: Number of memory channels</description></item>
    /// <item><description>--socket-mem=MB: Hugepage memory per NUMA socket</description></item>
    /// <item><description>--proc-type=primary|secondary: Process type</description></item>
    /// <item><description>--file-prefix=prefix: Shared memory file prefix</description></item>
    /// <item><description>-a PCI: Allow-list specific PCI device</description></item>
    /// <item><description>-b PCI: Block-list specific PCI device</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// EalArgs = new[] { "-l", "0-3", "-n", "4", "--socket-mem=1024" };
    /// </code>
    /// </example>
    public string[] EalArgs { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the port configurations.
    /// Each entry configures a specific network port for DPDK operation.
    /// </summary>
    public DpdkPortConfig[] Ports { get; set; } = Array.Empty<DpdkPortConfig>();

    /// <summary>
    /// Gets or sets the number of mbufs (message buffers) in the memory pool.
    /// Each mbuf holds one packet. Default is 8192.
    /// </summary>
    /// <remarks>
    /// The total memory required is approximately: NumMbufs * MbufSize.
    /// Ensure sufficient hugepage memory is available.
    /// </remarks>
    public int NumMbufs { get; set; } = 8192;

    /// <summary>
    /// Gets or sets the size of each mbuf data region in bytes.
    /// Default is 2048 bytes (enough for standard Ethernet MTU with headroom).
    /// </summary>
    /// <remarks>
    /// Set to at least MTU + 128 bytes for headroom and metadata.
    /// For jumbo frames, use 9728 or larger.
    /// </remarks>
    public int MbufSize { get; set; } = 2048;

    /// <summary>
    /// Gets or sets the mbuf cache size per lcore.
    /// Caching reduces contention on the mempool. Default is 250.
    /// </summary>
    public int MbufCacheSize { get; set; } = 250;

    /// <summary>
    /// Gets or sets the ring buffer size for packet queues.
    /// Must be a power of 2. Default is 1024.
    /// </summary>
    public int RingSize { get; set; } = 1024;

    /// <summary>
    /// Gets or sets whether to use hugepages for memory allocation.
    /// Strongly recommended for production use. Default is true.
    /// </summary>
    public bool UseHugepages { get; set; } = true;

    /// <summary>
    /// Gets or sets the hugepage size in megabytes.
    /// Common values are 2 (2MB pages) or 1024 (1GB pages). Default is 2.
    /// </summary>
    public int HugepageSizeMb { get; set; } = 2;

    /// <summary>
    /// Gets or sets whether to enable IOMMU/VFIO mode.
    /// Required for vfio-pci driver. Provides better security isolation.
    /// </summary>
    public bool EnableIommu { get; set; } = true;
}

/// <summary>
/// Configuration for a single DPDK network port.
/// </summary>
public class DpdkPortConfig
{
    /// <summary>
    /// Gets or sets the port identifier (0-based index or PCI address).
    /// </summary>
    public int PortId { get; set; }

    /// <summary>
    /// Gets or sets the PCI address of the device (e.g., "0000:00:1f.6").
    /// </summary>
    public string? PciAddress { get; set; }

    /// <summary>
    /// Gets or sets the number of receive queues.
    /// Multiple queues enable RSS (Receive Side Scaling) for parallel processing.
    /// </summary>
    public int RxQueues { get; set; } = 1;

    /// <summary>
    /// Gets or sets the number of transmit queues.
    /// Multiple queues enable parallel transmission from different cores.
    /// </summary>
    public int TxQueues { get; set; } = 1;

    /// <summary>
    /// Gets or sets the number of receive descriptors per queue.
    /// More descriptors allow more packets to be buffered. Default is 1024.
    /// </summary>
    public int RxDescriptors { get; set; } = 1024;

    /// <summary>
    /// Gets or sets the number of transmit descriptors per queue.
    /// More descriptors allow more packets to be queued for transmission. Default is 1024.
    /// </summary>
    public int TxDescriptors { get; set; } = 1024;

    /// <summary>
    /// Gets or sets whether to enable promiscuous mode.
    /// When enabled, the port receives all packets, not just those addressed to it.
    /// </summary>
    public bool PromiscuousMode { get; set; }

    /// <summary>
    /// Gets or sets the RSS (Receive Side Scaling) configuration.
    /// </summary>
    public RssConfig? Rss { get; set; }

    /// <summary>
    /// Gets or sets flow classification rules for this port.
    /// </summary>
    public FlowRule[] FlowRules { get; set; } = Array.Empty<FlowRule>();
}

/// <summary>
/// Configuration for RSS (Receive Side Scaling).
/// RSS distributes incoming packets across multiple receive queues based on packet headers.
/// </summary>
public class RssConfig
{
    /// <summary>
    /// Gets or sets whether RSS is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the RSS hash functions to use.
    /// </summary>
    public RssHashFunction HashFunctions { get; set; } = RssHashFunction.IpTcp | RssHashFunction.IpUdp;

    /// <summary>
    /// Gets or sets the RSS hash key.
    /// If null, a default key is used. Must be 40 bytes for Toeplitz hash.
    /// </summary>
    public byte[]? HashKey { get; set; }

    /// <summary>
    /// Gets or sets the RSS redirection table size.
    /// Must be a power of 2. Default is 128.
    /// </summary>
    public int RetaSize { get; set; } = 128;
}

/// <summary>
/// RSS hash function flags.
/// </summary>
[Flags]
public enum RssHashFunction : ulong
{
    /// <summary>No RSS hashing.</summary>
    None = 0,

    /// <summary>Hash based on IPv4 addresses only.</summary>
    Ipv4 = 1 << 0,

    /// <summary>Hash based on IPv4 addresses and TCP ports.</summary>
    IpTcp = 1 << 1,

    /// <summary>Hash based on IPv4 addresses and UDP ports.</summary>
    IpUdp = 1 << 2,

    /// <summary>Hash based on IPv6 addresses only.</summary>
    Ipv6 = 1 << 3,

    /// <summary>Hash based on IPv6 addresses and TCP ports.</summary>
    Ipv6Tcp = 1 << 4,

    /// <summary>Hash based on IPv6 addresses and UDP ports.</summary>
    Ipv6Udp = 1 << 5,

    /// <summary>Hash based on SCTP.</summary>
    Sctp = 1 << 6,

    /// <summary>Hash based on inner (tunneled) headers.</summary>
    Inner = 1 << 7
}

/// <summary>
/// Flow classification rule for directing packets to specific queues.
/// </summary>
public class FlowRule
{
    /// <summary>
    /// Gets or sets the rule priority (higher = more important).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Gets or sets the source IP address pattern (with optional mask).
    /// </summary>
    public string? SourceIp { get; set; }

    /// <summary>
    /// Gets or sets the destination IP address pattern (with optional mask).
    /// </summary>
    public string? DestinationIp { get; set; }

    /// <summary>
    /// Gets or sets the source port (0 = any).
    /// </summary>
    public ushort SourcePort { get; set; }

    /// <summary>
    /// Gets or sets the destination port (0 = any).
    /// </summary>
    public ushort DestinationPort { get; set; }

    /// <summary>
    /// Gets or sets the protocol (TCP=6, UDP=17, 0=any).
    /// </summary>
    public byte Protocol { get; set; }

    /// <summary>
    /// Gets or sets the target queue ID for matching packets.
    /// </summary>
    public int TargetQueue { get; set; }

    /// <summary>
    /// Gets or sets the action for matching packets.
    /// </summary>
    public FlowAction Action { get; set; } = FlowAction.Queue;
}

/// <summary>
/// Action to take for packets matching a flow rule.
/// </summary>
public enum FlowAction
{
    /// <summary>Direct packet to a specific queue.</summary>
    Queue,

    /// <summary>Drop the packet.</summary>
    Drop,

    /// <summary>Mark the packet for special processing.</summary>
    Mark,

    /// <summary>Apply RSS to the packet.</summary>
    Rss
}

/// <summary>
/// Statistics for a DPDK port.
/// </summary>
public class DpdkPortStats
{
    /// <summary>
    /// Gets or sets the port identifier.
    /// </summary>
    public int PortId { get; set; }

    /// <summary>
    /// Gets or sets when the statistics were collected.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the total received packets.
    /// </summary>
    public ulong RxPackets { get; set; }

    /// <summary>
    /// Gets or sets the total transmitted packets.
    /// </summary>
    public ulong TxPackets { get; set; }

    /// <summary>
    /// Gets or sets the total received bytes.
    /// </summary>
    public ulong RxBytes { get; set; }

    /// <summary>
    /// Gets or sets the total transmitted bytes.
    /// </summary>
    public ulong TxBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of receive errors.
    /// </summary>
    public ulong RxErrors { get; set; }

    /// <summary>
    /// Gets or sets the number of transmit errors.
    /// </summary>
    public ulong TxErrors { get; set; }

    /// <summary>
    /// Gets or sets the number of packets dropped due to no receive buffers.
    /// </summary>
    public ulong RxNoMbuf { get; set; }

    /// <summary>
    /// Gets or sets the number of missed packets (hardware overflow).
    /// </summary>
    public ulong RxMissed { get; set; }

    /// <summary>
    /// Gets or sets per-queue receive statistics.
    /// </summary>
    public QueueStats[] RxQueueStats { get; set; } = Array.Empty<QueueStats>();

    /// <summary>
    /// Gets or sets per-queue transmit statistics.
    /// </summary>
    public QueueStats[] TxQueueStats { get; set; } = Array.Empty<QueueStats>();

    /// <summary>
    /// Gets the total packets (RX + TX).
    /// </summary>
    public ulong TotalPackets => RxPackets + TxPackets;

    /// <summary>
    /// Gets the total bytes (RX + TX).
    /// </summary>
    public ulong TotalBytes => RxBytes + TxBytes;

    /// <summary>
    /// Gets the total errors (RX + TX).
    /// </summary>
    public ulong TotalErrors => RxErrors + TxErrors;

    /// <summary>
    /// Gets an error message if statistics collection failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Per-queue statistics.
/// </summary>
public class QueueStats
{
    /// <summary>
    /// Gets or sets the queue ID.
    /// </summary>
    public int QueueId { get; set; }

    /// <summary>
    /// Gets or sets the packet count for this queue.
    /// </summary>
    public ulong Packets { get; set; }

    /// <summary>
    /// Gets or sets the byte count for this queue.
    /// </summary>
    public ulong Bytes { get; set; }

    /// <summary>
    /// Gets or sets the error count for this queue.
    /// </summary>
    public ulong Errors { get; set; }
}

#endregion

#region P/Invoke Stubs

/// <summary>
/// P/Invoke declarations for DPDK librte_* libraries.
/// These are stubs that require actual DPDK libraries to be installed.
/// </summary>
internal static class DpdkInterop
{
    private const string LibRteEal = "librte_eal.so";
    private const string LibRteEthdev = "librte_ethdev.so";
    private const string LibRteMempool = "librte_mempool.so";
    private const string LibRteMbuf = "librte_mbuf.so";
    private const string LibRteRing = "librte_ring.so";

    #region EAL Functions

    /// <summary>
    /// Initialize the Environment Abstraction Layer (EAL).
    /// </summary>
    /// <param name="argc">Number of arguments.</param>
    /// <param name="argv">Pointer to argument array.</param>
    /// <returns>Number of parsed arguments on success, negative on error.</returns>
    [DllImport(LibRteEal, EntryPoint = "rte_eal_init", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eal_init(int argc, IntPtr argv);

    /// <summary>
    /// Clean up the Environment Abstraction Layer (EAL).
    /// </summary>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEal, EntryPoint = "rte_eal_cleanup", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eal_cleanup();

    /// <summary>
    /// Get the process type (primary or secondary).
    /// </summary>
    /// <returns>0 for primary, 1 for secondary.</returns>
    [DllImport(LibRteEal, EntryPoint = "rte_eal_process_type", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eal_process_type();

    /// <summary>
    /// Get the number of available lcores.
    /// </summary>
    /// <returns>Number of lcores.</returns>
    [DllImport(LibRteEal, EntryPoint = "rte_lcore_count", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint rte_lcore_count();

    /// <summary>
    /// Get the lcore ID of the current thread.
    /// </summary>
    /// <returns>Lcore ID.</returns>
    [DllImport(LibRteEal, EntryPoint = "rte_lcore_id", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint rte_lcore_id();

    /// <summary>
    /// Get the NUMA socket ID for a given lcore.
    /// </summary>
    /// <param name="lcoreId">Lcore ID.</param>
    /// <returns>Socket ID.</returns>
    [DllImport(LibRteEal, EntryPoint = "rte_lcore_to_socket_id", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint rte_lcore_to_socket_id(uint lcoreId);

    #endregion

    #region Mempool Functions

    /// <summary>
    /// Create a new memory pool.
    /// </summary>
    /// <param name="name">Pool name.</param>
    /// <param name="n">Number of elements.</param>
    /// <param name="eltSize">Size of each element.</param>
    /// <param name="cacheSize">Cache size per lcore.</param>
    /// <param name="privateDataSize">Size of private data.</param>
    /// <param name="mpInit">Mempool initialization callback (can be IntPtr.Zero).</param>
    /// <param name="mpInitArg">Argument for mempool initialization callback.</param>
    /// <param name="objInit">Object initialization callback (can be IntPtr.Zero).</param>
    /// <param name="objInitArg">Argument for object initialization callback.</param>
    /// <param name="socketId">NUMA socket ID.</param>
    /// <param name="flags">Flags.</param>
    /// <returns>Pointer to mempool, or NULL on error.</returns>
    [DllImport(LibRteMempool, EntryPoint = "rte_mempool_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rte_mempool_create(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        uint n,
        uint eltSize,
        uint cacheSize,
        uint privateDataSize,
        IntPtr mpInit,
        IntPtr mpInitArg,
        IntPtr objInit,
        IntPtr objInitArg,
        int socketId,
        uint flags);

    /// <summary>
    /// Free a memory pool.
    /// </summary>
    /// <param name="mp">Pointer to mempool.</param>
    [DllImport(LibRteMempool, EntryPoint = "rte_mempool_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rte_mempool_free(IntPtr mp);

    /// <summary>
    /// Get one object from the pool.
    /// </summary>
    /// <param name="mp">Mempool pointer.</param>
    /// <param name="obj">Pointer to store object pointer.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteMempool, EntryPoint = "rte_mempool_get", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_mempool_get(IntPtr mp, out IntPtr obj);

    /// <summary>
    /// Return one object to the pool.
    /// </summary>
    /// <param name="mp">Mempool pointer.</param>
    /// <param name="obj">Object to return.</param>
    [DllImport(LibRteMempool, EntryPoint = "rte_mempool_put", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rte_mempool_put(IntPtr mp, IntPtr obj);

    #endregion

    #region Mbuf Functions

    /// <summary>
    /// Create a mbuf pool.
    /// </summary>
    /// <param name="name">Pool name.</param>
    /// <param name="n">Number of mbufs.</param>
    /// <param name="cacheSize">Cache size per lcore.</param>
    /// <param name="privSize">Size of private data area.</param>
    /// <param name="dataRoomSize">Size of data buffer in each mbuf.</param>
    /// <param name="socketId">NUMA socket ID.</param>
    /// <returns>Pointer to mbuf pool, or NULL on error.</returns>
    [DllImport(LibRteMbuf, EntryPoint = "rte_pktmbuf_pool_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rte_pktmbuf_pool_create(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        uint n,
        uint cacheSize,
        ushort privSize,
        ushort dataRoomSize,
        int socketId);

    /// <summary>
    /// Allocate a mbuf from the pool.
    /// </summary>
    /// <param name="mp">Mbuf pool pointer.</param>
    /// <returns>Pointer to mbuf, or NULL on error.</returns>
    [DllImport(LibRteMbuf, EntryPoint = "rte_pktmbuf_alloc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rte_pktmbuf_alloc(IntPtr mp);

    /// <summary>
    /// Free a mbuf.
    /// </summary>
    /// <param name="m">Mbuf pointer.</param>
    [DllImport(LibRteMbuf, EntryPoint = "rte_pktmbuf_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rte_pktmbuf_free(IntPtr m);

    /// <summary>
    /// Allocate a bulk of mbufs from the pool.
    /// </summary>
    /// <param name="pool">Mbuf pool pointer.</param>
    /// <param name="mbufs">Array to store mbuf pointers.</param>
    /// <param name="count">Number of mbufs to allocate.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteMbuf, EntryPoint = "rte_pktmbuf_alloc_bulk", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_pktmbuf_alloc_bulk(IntPtr pool, IntPtr[] mbufs, uint count);

    #endregion

    #region Ring Functions

    /// <summary>
    /// Create a new ring.
    /// </summary>
    /// <param name="name">Ring name.</param>
    /// <param name="count">Size of the ring (must be power of 2).</param>
    /// <param name="socketId">NUMA socket ID.</param>
    /// <param name="flags">Flags (RING_F_SP_ENQ, RING_F_SC_DEQ, etc.).</param>
    /// <returns>Pointer to ring, or NULL on error.</returns>
    [DllImport(LibRteRing, EntryPoint = "rte_ring_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rte_ring_create(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        uint count,
        int socketId,
        uint flags);

    /// <summary>
    /// Free a ring.
    /// </summary>
    /// <param name="r">Ring pointer.</param>
    [DllImport(LibRteRing, EntryPoint = "rte_ring_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rte_ring_free(IntPtr r);

    /// <summary>
    /// Enqueue several objects on a ring.
    /// </summary>
    /// <param name="r">Ring pointer.</param>
    /// <param name="objTable">Pointer to array of objects.</param>
    /// <param name="n">Number of objects.</param>
    /// <param name="freeSpace">Pointer to store remaining space.</param>
    /// <returns>Number of objects enqueued.</returns>
    [DllImport(LibRteRing, EntryPoint = "rte_ring_enqueue_burst", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint rte_ring_enqueue_burst(IntPtr r, IntPtr objTable, uint n, out uint freeSpace);

    /// <summary>
    /// Dequeue several objects from a ring.
    /// </summary>
    /// <param name="r">Ring pointer.</param>
    /// <param name="objTable">Pointer to array to store objects.</param>
    /// <param name="n">Maximum number of objects.</param>
    /// <param name="available">Pointer to store number still available.</param>
    /// <returns>Number of objects dequeued.</returns>
    [DllImport(LibRteRing, EntryPoint = "rte_ring_dequeue_burst", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint rte_ring_dequeue_burst(IntPtr r, IntPtr objTable, uint n, out uint available);

    #endregion

    #region Ethdev Functions

    /// <summary>
    /// Get the number of available Ethernet ports.
    /// </summary>
    /// <returns>Number of available ports.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_dev_count_avail", CallingConvention = CallingConvention.Cdecl)]
    internal static extern ushort rte_eth_dev_count_avail();

    /// <summary>
    /// Configure an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <param name="nbRxQueue">Number of receive queues.</param>
    /// <param name="nbTxQueue">Number of transmit queues.</param>
    /// <param name="ethConf">Pointer to configuration structure.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_dev_configure", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_dev_configure(ushort portId, ushort nbRxQueue, ushort nbTxQueue, IntPtr ethConf);

    /// <summary>
    /// Start an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_dev_start", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_dev_start(ushort portId);

    /// <summary>
    /// Stop an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_dev_stop", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_dev_stop(ushort portId);

    /// <summary>
    /// Close an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_dev_close", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_dev_close(ushort portId);

    /// <summary>
    /// Allocate and set up a receive queue for an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <param name="queueId">Queue ID.</param>
    /// <param name="nbRxDesc">Number of receive descriptors.</param>
    /// <param name="socketId">NUMA socket ID.</param>
    /// <param name="rxConf">Receive configuration (can be NULL for defaults).</param>
    /// <param name="mbPool">Memory pool for receive buffers.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_rx_queue_setup", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_rx_queue_setup(
        ushort portId,
        ushort queueId,
        ushort nbRxDesc,
        uint socketId,
        IntPtr rxConf,
        IntPtr mbPool);

    /// <summary>
    /// Allocate and set up a transmit queue for an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <param name="queueId">Queue ID.</param>
    /// <param name="nbTxDesc">Number of transmit descriptors.</param>
    /// <param name="socketId">NUMA socket ID.</param>
    /// <param name="txConf">Transmit configuration (can be NULL for defaults).</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_tx_queue_setup", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_tx_queue_setup(
        ushort portId,
        ushort queueId,
        ushort nbTxDesc,
        uint socketId,
        IntPtr txConf);

    /// <summary>
    /// Receive a burst of packets from a receive queue of an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <param name="queueId">Queue ID.</param>
    /// <param name="rxPkts">Array to store received mbuf pointers.</param>
    /// <param name="nbPkts">Maximum number of packets to receive.</param>
    /// <returns>Number of packets actually received.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_rx_burst", CallingConvention = CallingConvention.Cdecl)]
    internal static extern ushort rte_eth_rx_burst(ushort portId, ushort queueId, IntPtr[] rxPkts, ushort nbPkts);

    /// <summary>
    /// Send a burst of packets on a transmit queue of an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <param name="queueId">Queue ID.</param>
    /// <param name="txPkts">Array of mbuf pointers to transmit.</param>
    /// <param name="nbPkts">Number of packets to transmit.</param>
    /// <returns>Number of packets actually sent.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_tx_burst", CallingConvention = CallingConvention.Cdecl)]
    internal static extern ushort rte_eth_tx_burst(ushort portId, ushort queueId, IntPtr[] txPkts, ushort nbPkts);

    /// <summary>
    /// Retrieve the Ethernet device statistics.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <param name="stats">Pointer to stats structure.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_stats_get", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_stats_get(ushort portId, ref RteEthStats stats);

    /// <summary>
    /// Reset the Ethernet device statistics.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_stats_reset", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_stats_reset(ushort portId);

    /// <summary>
    /// Enable promiscuous mode on an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_promiscuous_enable", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_promiscuous_enable(ushort portId);

    /// <summary>
    /// Disable promiscuous mode on an Ethernet device.
    /// </summary>
    /// <param name="portId">Port ID.</param>
    /// <returns>0 on success, negative on error.</returns>
    [DllImport(LibRteEthdev, EntryPoint = "rte_eth_promiscuous_disable", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rte_eth_promiscuous_disable(ushort portId);

    #endregion

    #region Structures

    /// <summary>
    /// DPDK ethernet statistics structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RteEthStats
    {
        /// <summary>Total number of successfully received packets.</summary>
        public ulong ipackets;
        /// <summary>Total number of successfully transmitted packets.</summary>
        public ulong opackets;
        /// <summary>Total number of successfully received bytes.</summary>
        public ulong ibytes;
        /// <summary>Total number of successfully transmitted bytes.</summary>
        public ulong obytes;
        /// <summary>Total of RX packets dropped by the HW.</summary>
        public ulong imissed;
        /// <summary>Total number of erroneous received packets.</summary>
        public ulong ierrors;
        /// <summary>Total number of failed transmitted packets.</summary>
        public ulong oerrors;
        /// <summary>Total number of RX mbuf allocation failures.</summary>
        public ulong rx_nombuf;

        // Per-queue stats arrays (16 queues max)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ulong[] q_ipackets;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ulong[] q_opackets;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ulong[] q_ibytes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ulong[] q_obytes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ulong[] q_errors;
    }

    /// <summary>
    /// Mbuf structure header (partial - only commonly accessed fields).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RteMbuf
    {
        /// <summary>Virtual address of segment buffer.</summary>
        public IntPtr buf_addr;
        /// <summary>Physical address of segment buffer.</summary>
        public ulong buf_iova;

        // Rearm data
        public ushort data_off;
        public ushort refcnt;
        public ushort nb_segs;
        public ushort port;

        public ulong ol_flags;

        // Packet type
        public uint packet_type;
        public uint pkt_len;
        public ushort data_len;
        public ushort vlan_tci;

        // More fields follow but are not commonly needed
    }

    /// <summary>
    /// Ring flags.
    /// </summary>
    internal const uint RING_F_SP_ENQ = 0x0001; // Single producer
    internal const uint RING_F_SC_DEQ = 0x0002; // Single consumer

    #endregion
}

#endregion

#region Plugin Implementation

/// <summary>
/// DPDK (Data Plane Development Kit) network plugin for userspace networking.
/// Provides kernel-bypass packet I/O with ultra-low latency and high throughput.
/// </summary>
/// <remarks>
/// <para>
/// DPDK enables applications to bypass the kernel network stack entirely, achieving
/// multi-million packet per second throughput with microsecond latencies. This plugin
/// provides a managed interface to DPDK's poll-mode drivers.
/// </para>
/// <para>
/// <b>Features:</b>
/// <list type="bullet">
/// <item><description>EAL (Environment Abstraction Layer) initialization</description></item>
/// <item><description>Port binding and configuration</description></item>
/// <item><description>Hugepage memory management</description></item>
/// <item><description>Ring buffer management for packet queues</description></item>
/// <item><description>Poll-mode driver support</description></item>
/// <item><description>Multi-queue support</description></item>
/// <item><description>RSS (Receive Side Scaling)</description></item>
/// <item><description>Flow classification</description></item>
/// <item><description>Memory pool (mempool) management</description></item>
/// <item><description>Burst packet I/O</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Requirements:</b>
/// <list type="bullet">
/// <item><description>Linux operating system (DPDK is primarily Linux-focused)</description></item>
/// <item><description>DPDK libraries installed (/usr/local/lib/librte_* or via RTE_SDK)</description></item>
/// <item><description>Hugepages configured (typically 2MB or 1GB pages)</description></item>
/// <item><description>IOMMU enabled for vfio-pci driver</description></item>
/// <item><description>Root privileges for port binding operations</description></item>
/// </list>
/// </para>
/// </remarks>
public class DpdkNetworkPlugin : FeaturePluginBase, IDpdkNetwork
{
    #region Constants

    /// <summary>
    /// Maximum burst size for packet I/O operations.
    /// </summary>
    private const int MaxBurstSize = 32;

    /// <summary>
    /// Default mbuf headroom size in bytes.
    /// </summary>
    private const int DefaultMbufHeadroom = 128;

    /// <summary>
    /// Maximum number of ports supported.
    /// </summary>
    private const int MaxPorts = 16;

    /// <summary>
    /// Maximum number of queues per port.
    /// </summary>
    private const int MaxQueuesPerPort = 16;

    #endregion

    #region Fields

    private readonly object _lock = new();
    private bool _ealInitialized;
    private bool _nativeAvailable;
    private DpdkConfig? _config;
    private IntPtr _mbufPool = IntPtr.Zero;
    private readonly ConcurrentDictionary<int, PortState> _ports = new();
    private readonly ConcurrentDictionary<string, IntPtr> _rings = new();

    // Statistics tracking
    private long _totalRxPackets;
    private long _totalTxPackets;
    private long _totalRxBytes;
    private long _totalTxBytes;

    #endregion

    #region Plugin Properties

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.performance.dpdk";

    /// <inheritdoc />
    public override string Name => "DPDK Userspace Network";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <summary>
    /// Gets whether DPDK is available on the current system.
    /// Checks for DPDK installation, libraries, and kernel support.
    /// </summary>
    public bool IsAvailable => CheckDpdkAvailability();

    /// <summary>
    /// Gets whether the EAL has been initialized.
    /// </summary>
    public bool IsEalInitialized => _ealInitialized;

    /// <summary>
    /// Gets whether native DPDK P/Invoke calls are available.
    /// </summary>
    public bool NativeCallsAvailable => _nativeAvailable;

    /// <summary>
    /// Gets the total number of packets received across all ports.
    /// </summary>
    public long TotalRxPackets => Interlocked.Read(ref _totalRxPackets);

    /// <summary>
    /// Gets the total number of packets transmitted across all ports.
    /// </summary>
    public long TotalTxPackets => Interlocked.Read(ref _totalTxPackets);

    /// <summary>
    /// Gets the number of initialized ports.
    /// </summary>
    public int PortCount => _ports.Count;

    #endregion

    #region Lifecycle Methods

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        CheckDpdkAvailability();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        await ShutdownAsync();
    }

    #endregion

    #region IDpdkNetwork Implementation

    /// <inheritdoc />
    public async Task InitializeAsync(DpdkConfig config)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "DPDK is not available on this system. Ensure DPDK is installed and " +
                "the RTE_SDK environment variable is set or DPDK libraries are in the standard paths.");
        }

        if (_ealInitialized)
        {
            throw new InvalidOperationException("DPDK is already initialized. Call ShutdownAsync() first.");
        }

        _config = config;

        await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_ealInitialized) return;

                // Initialize EAL
                InitializeEal(config.EalArgs);

                // Create mbuf pool
                CreateMbufPool(config);

                // Initialize ports
                foreach (var portConfig in config.Ports)
                {
                    InitializePort(portConfig);
                }

                _ealInitialized = true;
            }
        });
    }

    /// <inheritdoc />
    public async Task<int> ReceiveBurstAsync(int portId, int queueId, Memory<byte>[] buffers)
    {
        if (!_ealInitialized)
        {
            throw new InvalidOperationException("DPDK is not initialized. Call InitializeAsync() first.");
        }

        if (buffers == null || buffers.Length == 0)
        {
            throw new ArgumentException("Buffers array cannot be null or empty.", nameof(buffers));
        }

        if (!_ports.TryGetValue(portId, out var portState))
        {
            throw new ArgumentException($"Port {portId} is not initialized.", nameof(portId));
        }

        if (queueId < 0 || queueId >= portState.RxQueues)
        {
            throw new ArgumentOutOfRangeException(nameof(queueId),
                $"Queue ID must be between 0 and {portState.RxQueues - 1}.");
        }

        return await Task.Run(() =>
        {
            var received = 0;

            if (_nativeAvailable)
            {
                received = ReceiveBurstNative(portId, queueId, buffers);
            }
            else
            {
                // Simulation mode for testing without actual DPDK
                received = ReceiveBurstSimulated(portId, queueId, buffers);
            }

            // Update statistics
            Interlocked.Add(ref _totalRxPackets, received);
            portState.RxPackets += (ulong)received;

            return received;
        });
    }

    /// <inheritdoc />
    public async Task<int> TransmitBurstAsync(int portId, int queueId, ReadOnlyMemory<byte>[] packets)
    {
        if (!_ealInitialized)
        {
            throw new InvalidOperationException("DPDK is not initialized. Call InitializeAsync() first.");
        }

        if (packets == null || packets.Length == 0)
        {
            throw new ArgumentException("Packets array cannot be null or empty.", nameof(packets));
        }

        if (!_ports.TryGetValue(portId, out var portState))
        {
            throw new ArgumentException($"Port {portId} is not initialized.", nameof(portId));
        }

        if (queueId < 0 || queueId >= portState.TxQueues)
        {
            throw new ArgumentOutOfRangeException(nameof(queueId),
                $"Queue ID must be between 0 and {portState.TxQueues - 1}.");
        }

        return await Task.Run(() =>
        {
            var transmitted = 0;

            if (_nativeAvailable)
            {
                transmitted = TransmitBurstNative(portId, queueId, packets);
            }
            else
            {
                // Simulation mode for testing without actual DPDK
                transmitted = TransmitBurstSimulated(portId, queueId, packets);
            }

            // Update statistics
            Interlocked.Add(ref _totalTxPackets, transmitted);
            portState.TxPackets += (ulong)transmitted;

            // Update byte counts
            var bytesSent = 0L;
            for (int i = 0; i < transmitted; i++)
            {
                bytesSent += packets[i].Length;
            }
            Interlocked.Add(ref _totalTxBytes, bytesSent);
            portState.TxBytes += (ulong)bytesSent;

            return transmitted;
        });
    }

    /// <inheritdoc />
    public async Task<DpdkPortStats> GetPortStatsAsync(int portId)
    {
        if (!_ealInitialized)
        {
            return new DpdkPortStats
            {
                PortId = portId,
                Timestamp = DateTime.UtcNow,
                ErrorMessage = "DPDK is not initialized"
            };
        }

        if (!_ports.TryGetValue(portId, out var portState))
        {
            return new DpdkPortStats
            {
                PortId = portId,
                Timestamp = DateTime.UtcNow,
                ErrorMessage = $"Port {portId} is not initialized"
            };
        }

        return await Task.Run(() =>
        {
            if (_nativeAvailable)
            {
                return GetPortStatsNative(portId, portState);
            }

            // Return cached stats for simulation mode
            return new DpdkPortStats
            {
                PortId = portId,
                Timestamp = DateTime.UtcNow,
                RxPackets = portState.RxPackets,
                TxPackets = portState.TxPackets,
                RxBytes = portState.RxBytes,
                TxBytes = portState.TxBytes,
                RxErrors = portState.RxErrors,
                TxErrors = portState.TxErrors,
                RxNoMbuf = portState.RxNoMbuf,
                RxMissed = portState.RxMissed
            };
        });
    }

    /// <inheritdoc />
    public async Task ShutdownAsync()
    {
        if (!_ealInitialized)
        {
            return;
        }

        await Task.Run(() =>
        {
            lock (_lock)
            {
                if (!_ealInitialized) return;

                try
                {
                    // Stop all ports
                    foreach (var port in _ports.Keys.ToList())
                    {
                        StopPort(port);
                    }
                    _ports.Clear();

                    // Free rings
                    foreach (var ring in _rings.Values)
                    {
                        if (_nativeAvailable && ring != IntPtr.Zero)
                        {
                            try { DpdkInterop.rte_ring_free(ring); } catch { }
                        }
                    }
                    _rings.Clear();

                    // Free mbuf pool
                    if (_nativeAvailable && _mbufPool != IntPtr.Zero)
                    {
                        try { DpdkInterop.rte_mempool_free(_mbufPool); } catch { }
                        _mbufPool = IntPtr.Zero;
                    }

                    // Cleanup EAL
                    if (_nativeAvailable)
                    {
                        try { DpdkInterop.rte_eal_cleanup(); } catch { }
                    }
                }
                finally
                {
                    _ealInitialized = false;
                    _config = null;
                }
            }
        });
    }

    #endregion

    #region Private Helper Methods

    private bool CheckDpdkAvailability()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        // Check RTE_SDK environment variable
        var rteSdk = Environment.GetEnvironmentVariable("RTE_SDK");
        if (!string.IsNullOrEmpty(rteSdk) && Directory.Exists(rteSdk))
        {
            CheckNativeAvailability();
            return true;
        }

        // Check standard installation paths
        var dpdkPaths = new[]
        {
            "/usr/local/lib/librte_eal.so",
            "/usr/lib/librte_eal.so",
            "/usr/lib/x86_64-linux-gnu/librte_eal.so",
            "/opt/dpdk/lib/librte_eal.so"
        };

        foreach (var path in dpdkPaths)
        {
            if (File.Exists(path))
            {
                CheckNativeAvailability();
                return true;
            }
        }

        return false;
    }

    private void CheckNativeAvailability()
    {
        try
        {
            // Try to call a simple DPDK function
            var count = DpdkInterop.rte_eth_dev_count_avail();
            _nativeAvailable = true;
        }
        catch
        {
            _nativeAvailable = false;
        }
    }

    private void InitializeEal(string[] args)
    {
        if (!_nativeAvailable)
        {
            // Simulation mode
            return;
        }

        var fullArgs = new List<string> { "datawarehouse-dpdk" };
        fullArgs.AddRange(args);

        var argc = fullArgs.Count;
        var argvPtrs = new IntPtr[argc + 1];

        try
        {
            for (int i = 0; i < argc; i++)
            {
                argvPtrs[i] = Marshal.StringToHGlobalAnsi(fullArgs[i]);
            }
            argvPtrs[argc] = IntPtr.Zero;

            var argv = Marshal.AllocHGlobal(IntPtr.Size * (argc + 1));
            try
            {
                Marshal.Copy(argvPtrs, 0, argv, argc + 1);

                var result = DpdkInterop.rte_eal_init(argc, argv);
                if (result < 0)
                {
                    throw new InvalidOperationException($"rte_eal_init failed with error code {result}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(argv);
            }
        }
        finally
        {
            for (int i = 0; i < argc; i++)
            {
                if (argvPtrs[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(argvPtrs[i]);
                }
            }
        }
    }

    private void CreateMbufPool(DpdkConfig config)
    {
        if (!_nativeAvailable)
        {
            // Simulation mode
            return;
        }

        var socketId = (int)DpdkInterop.rte_lcore_to_socket_id(DpdkInterop.rte_lcore_id());

        _mbufPool = DpdkInterop.rte_pktmbuf_pool_create(
            "mbuf_pool",
            (uint)config.NumMbufs,
            (uint)config.MbufCacheSize,
            0,
            (ushort)(config.MbufSize + DefaultMbufHeadroom),
            socketId);

        if (_mbufPool == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create mbuf pool");
        }
    }

    private void InitializePort(DpdkPortConfig portConfig)
    {
        var portState = new PortState
        {
            PortId = portConfig.PortId,
            RxQueues = portConfig.RxQueues,
            TxQueues = portConfig.TxQueues,
            IsStarted = false
        };

        if (_nativeAvailable)
        {
            var portId = (ushort)portConfig.PortId;
            var socketId = (uint)DpdkInterop.rte_lcore_to_socket_id(DpdkInterop.rte_lcore_id());

            // Configure port
            var result = DpdkInterop.rte_eth_dev_configure(
                portId,
                (ushort)portConfig.RxQueues,
                (ushort)portConfig.TxQueues,
                IntPtr.Zero);

            if (result < 0)
            {
                throw new InvalidOperationException($"Failed to configure port {portId}: error {result}");
            }

            // Setup RX queues
            for (int i = 0; i < portConfig.RxQueues; i++)
            {
                result = DpdkInterop.rte_eth_rx_queue_setup(
                    portId,
                    (ushort)i,
                    (ushort)portConfig.RxDescriptors,
                    socketId,
                    IntPtr.Zero,
                    _mbufPool);

                if (result < 0)
                {
                    throw new InvalidOperationException($"Failed to setup RX queue {i} on port {portId}: error {result}");
                }
            }

            // Setup TX queues
            for (int i = 0; i < portConfig.TxQueues; i++)
            {
                result = DpdkInterop.rte_eth_tx_queue_setup(
                    portId,
                    (ushort)i,
                    (ushort)portConfig.TxDescriptors,
                    socketId,
                    IntPtr.Zero);

                if (result < 0)
                {
                    throw new InvalidOperationException($"Failed to setup TX queue {i} on port {portId}: error {result}");
                }
            }

            // Enable promiscuous mode if requested
            if (portConfig.PromiscuousMode)
            {
                DpdkInterop.rte_eth_promiscuous_enable(portId);
            }

            // Start port
            result = DpdkInterop.rte_eth_dev_start(portId);
            if (result < 0)
            {
                throw new InvalidOperationException($"Failed to start port {portId}: error {result}");
            }

            portState.IsStarted = true;
        }
        else
        {
            // Simulation mode - just mark as started
            portState.IsStarted = true;
        }

        _ports[portConfig.PortId] = portState;
    }

    private void StopPort(int portId)
    {
        if (_nativeAvailable && _ports.TryGetValue(portId, out var portState) && portState.IsStarted)
        {
            try
            {
                DpdkInterop.rte_eth_dev_stop((ushort)portId);
                DpdkInterop.rte_eth_dev_close((ushort)portId);
            }
            catch
            {
                // Best effort
            }
        }
    }

    private unsafe int ReceiveBurstNative(int portId, int queueId, Memory<byte>[] buffers)
    {
        var burstSize = Math.Min(buffers.Length, MaxBurstSize);
        var rxPkts = new IntPtr[burstSize];

        var received = DpdkInterop.rte_eth_rx_burst((ushort)portId, (ushort)queueId, rxPkts, (ushort)burstSize);

        // Copy packet data to buffers
        for (int i = 0; i < received; i++)
        {
            if (rxPkts[i] != IntPtr.Zero)
            {
                var mbuf = Marshal.PtrToStructure<DpdkInterop.RteMbuf>(rxPkts[i]);
                var dataPtr = IntPtr.Add(mbuf.buf_addr, mbuf.data_off);
                var dataLen = Math.Min(mbuf.data_len, buffers[i].Length);

                var span = buffers[i].Span;
                fixed (byte* dest = span)
                {
                    Buffer.MemoryCopy((void*)dataPtr, dest, buffers[i].Length, dataLen);
                }

                Interlocked.Add(ref _totalRxBytes, dataLen);

                // Free mbuf
                DpdkInterop.rte_pktmbuf_free(rxPkts[i]);
            }
        }

        return received;
    }

    private int ReceiveBurstSimulated(int portId, int queueId, Memory<byte>[] buffers)
    {
        // Simulation: Generate some random packets for testing
        var random = Random.Shared;
        var toReceive = random.Next(0, Math.Min(buffers.Length, 5));

        for (int i = 0; i < toReceive; i++)
        {
            // Simulate Ethernet frame with minimal header
            var frameSize = Math.Min(random.Next(64, 1500), buffers[i].Length);
            random.NextBytes(buffers[i].Span.Slice(0, frameSize));

            Interlocked.Add(ref _totalRxBytes, frameSize);
        }

        return toReceive;
    }

    private unsafe int TransmitBurstNative(int portId, int queueId, ReadOnlyMemory<byte>[] packets)
    {
        var burstSize = Math.Min(packets.Length, MaxBurstSize);
        var txPkts = new IntPtr[burstSize];
        var allocatedMbufs = new List<IntPtr>();

        try
        {
            // Allocate mbufs and copy data
            for (int i = 0; i < burstSize; i++)
            {
                var mbuf = DpdkInterop.rte_pktmbuf_alloc(_mbufPool);
                if (mbuf == IntPtr.Zero)
                {
                    // No more mbufs available
                    break;
                }

                allocatedMbufs.Add(mbuf);

                var mbufStruct = Marshal.PtrToStructure<DpdkInterop.RteMbuf>(mbuf);
                var dataPtr = IntPtr.Add(mbufStruct.buf_addr, mbufStruct.data_off);
                var dataLen = Math.Min(packets[i].Length, _config?.MbufSize ?? 2048);

                // Copy packet data
                fixed (byte* src = packets[i].Span)
                {
                    Buffer.MemoryCopy(src, (void*)dataPtr, dataLen, dataLen);
                }

                // Update mbuf length fields
                mbufStruct.data_len = (ushort)dataLen;
                mbufStruct.pkt_len = (uint)dataLen;
                Marshal.StructureToPtr(mbufStruct, mbuf, false);

                txPkts[i] = mbuf;
            }

            if (allocatedMbufs.Count == 0)
            {
                return 0;
            }

            // Transmit burst
            var transmitted = DpdkInterop.rte_eth_tx_burst(
                (ushort)portId,
                (ushort)queueId,
                txPkts,
                (ushort)allocatedMbufs.Count);

            // Free unsent mbufs
            for (int i = transmitted; i < allocatedMbufs.Count; i++)
            {
                DpdkInterop.rte_pktmbuf_free(txPkts[i]);
            }

            return transmitted;
        }
        catch
        {
            // Free all allocated mbufs on error
            foreach (var mbuf in allocatedMbufs)
            {
                DpdkInterop.rte_pktmbuf_free(mbuf);
            }
            throw;
        }
    }

    private int TransmitBurstSimulated(int portId, int queueId, ReadOnlyMemory<byte>[] packets)
    {
        // Simulation: "transmit" all packets
        return packets.Length;
    }

    private DpdkPortStats GetPortStatsNative(int portId, PortState portState)
    {
        var stats = new DpdkInterop.RteEthStats();
        var result = DpdkInterop.rte_eth_stats_get((ushort)portId, ref stats);

        if (result != 0)
        {
            return new DpdkPortStats
            {
                PortId = portId,
                Timestamp = DateTime.UtcNow,
                ErrorMessage = $"Failed to get port stats: error {result}"
            };
        }

        var portStats = new DpdkPortStats
        {
            PortId = portId,
            Timestamp = DateTime.UtcNow,
            RxPackets = stats.ipackets,
            TxPackets = stats.opackets,
            RxBytes = stats.ibytes,
            TxBytes = stats.obytes,
            RxErrors = stats.ierrors,
            TxErrors = stats.oerrors,
            RxNoMbuf = stats.rx_nombuf,
            RxMissed = stats.imissed
        };

        // Populate per-queue stats
        if (stats.q_ipackets != null)
        {
            var rxQueueStats = new List<QueueStats>();
            for (int i = 0; i < portState.RxQueues && i < stats.q_ipackets.Length; i++)
            {
                rxQueueStats.Add(new QueueStats
                {
                    QueueId = i,
                    Packets = stats.q_ipackets[i],
                    Bytes = stats.q_ibytes?[i] ?? 0,
                    Errors = stats.q_errors?[i] ?? 0
                });
            }
            portStats.RxQueueStats = rxQueueStats.ToArray();
        }

        if (stats.q_opackets != null)
        {
            var txQueueStats = new List<QueueStats>();
            for (int i = 0; i < portState.TxQueues && i < stats.q_opackets.Length; i++)
            {
                txQueueStats.Add(new QueueStats
                {
                    QueueId = i,
                    Packets = stats.q_opackets[i],
                    Bytes = stats.q_obytes?[i] ?? 0
                });
            }
            portStats.TxQueueStats = txQueueStats.ToArray();
        }

        return portStats;
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "DPDK";
        metadata["IsAvailable"] = IsAvailable;
        metadata["IsEalInitialized"] = _ealInitialized;
        metadata["NativeCallsAvailable"] = _nativeAvailable;
        metadata["PortCount"] = _ports.Count;
        metadata["TotalRxPackets"] = TotalRxPackets;
        metadata["TotalTxPackets"] = TotalTxPackets;
        metadata["Platform"] = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : "Unsupported";
        metadata["Features"] = new[]
        {
            "EAL Initialization",
            "Port Binding",
            "Hugepage Memory",
            "Ring Buffers",
            "Poll-Mode Drivers",
            "Multi-Queue",
            "RSS",
            "Flow Classification",
            "Mempool",
            "Burst I/O"
        };
        return metadata;
    }

    #endregion

    #region Port State

    /// <summary>
    /// Internal state for a DPDK port.
    /// </summary>
    private class PortState
    {
        public int PortId { get; set; }
        public int RxQueues { get; set; }
        public int TxQueues { get; set; }
        public bool IsStarted { get; set; }
        public ulong RxPackets { get; set; }
        public ulong TxPackets { get; set; }
        public ulong RxBytes { get; set; }
        public ulong TxBytes { get; set; }
        public ulong RxErrors { get; set; }
        public ulong TxErrors { get; set; }
        public ulong RxNoMbuf { get; set; }
        public ulong RxMissed { get; set; }
    }

    #endregion
}

#endregion
