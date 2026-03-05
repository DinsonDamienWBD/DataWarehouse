using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO.Spdk;

/// <summary>
/// P/Invoke declarations for the SPDK (Storage Performance Development Kit) NVMe driver.
/// Provides direct userspace access to NVMe devices via vfio-pci, bypassing the kernel
/// block layer entirely for sub-microsecond I/O latency.
/// </summary>
/// <remarks>
/// <para>
/// SPDK is primarily a Linux bare-metal technology. These declarations compile on all
/// platforms (P/Invoke is just metadata), but will only function on systems with
/// libspdk installed and NVMe devices bound to vfio-pci.
/// </para>
/// <para>
/// Use <see cref="IsSupported"/> to detect runtime availability before attempting
/// to use any SPDK functionality.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-63: SPDK P/Invoke bindings for userspace NVMe")]
internal static partial class SpdkNativeMethods
{
    private const string LibName = "spdk";

    private static readonly Lazy<bool> _isSupported = new(ProbeLibrary, isThreadSafe: true);

    #region Environment Init

    /// <summary>
    /// Initializes the SPDK environment, including hugepage allocation and DPDK EAL setup.
    /// Must be called once before any other SPDK function.
    /// </summary>
    /// <param name="opts">Pointer to initialized <see cref="SpdkEnvOpts"/>.</param>
    /// <returns>0 on success, negative errno on failure.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_env_init")]
    internal static partial int EnvInit(ref SpdkEnvOpts opts);

    /// <summary>
    /// Cleans up the SPDK environment, releasing hugepages and DPDK resources.
    /// </summary>
    [LibraryImport(LibName, EntryPoint = "spdk_env_fini")]
    internal static partial void EnvFini();

    /// <summary>
    /// Initializes an <see cref="SpdkEnvOpts"/> structure with default values.
    /// </summary>
    /// <param name="opts">Pointer to the options structure to initialize.</param>
    [LibraryImport(LibName, EntryPoint = "spdk_env_opts_init")]
    internal static partial void EnvOptsInit(ref SpdkEnvOpts opts);

    #endregion

    #region NVMe Probe

    /// <summary>
    /// Enumerates NVMe controllers and attaches to those accepted by the probe callback.
    /// </summary>
    /// <param name="trid">Transport identifier filtering which controllers to probe.</param>
    /// <param name="cbCtx">Opaque context pointer passed to all callbacks.</param>
    /// <param name="probeCb">Called for each discovered controller. Return true to attach.</param>
    /// <param name="attachCb">Called after successful attachment to a controller.</param>
    /// <param name="removeCb">Called if a controller is hot-removed (may be null).</param>
    /// <returns>0 on success, negative errno on failure.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_probe")]
    internal static unsafe partial int NvmeProbe(
        ref SpdkNvmeTransportId trid,
        IntPtr cbCtx,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, SpdkNvmeTransportId*, byte> probeCb,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, SpdkNvmeTransportId*, void> attachCb,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> removeCb);

    /// <summary>
    /// Detaches from an NVMe controller, releasing all associated resources.
    /// </summary>
    /// <param name="ctrlr">Controller handle obtained from the attach callback.</param>
    /// <returns>0 on success, negative errno on failure.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_detach")]
    internal static partial int NvmeDetach(IntPtr ctrlr);

    #endregion

    #region Namespace

    /// <summary>
    /// Gets the namespace handle for the specified namespace ID on a controller.
    /// </summary>
    /// <param name="ctrlr">Controller handle.</param>
    /// <param name="nsid">Namespace ID (typically 1 for single-namespace devices).</param>
    /// <returns>Namespace handle, or <see cref="IntPtr.Zero"/> if not found.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_ctrlr_get_ns")]
    internal static partial IntPtr NvmeCtrlrGetNs(IntPtr ctrlr, uint nsid);

    /// <summary>
    /// Gets the sector size in bytes for the specified namespace.
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <returns>Sector size in bytes (typically 512 or 4096).</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_ns_get_sector_size")]
    internal static partial uint NvmeNsGetSectorSize(IntPtr ns);

    /// <summary>
    /// Gets the total number of sectors in the specified namespace.
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <returns>Total sector count.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_ns_get_num_sectors")]
    internal static partial ulong NvmeNsGetNumSectors(IntPtr ns);

    /// <summary>
    /// Gets the metadata size per sector for the namespace.
    /// Returns 0 for standard NVMe namespaces without metadata (non-ZNS).
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <returns>Metadata size in bytes per sector.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_ns_get_md_size")]
    internal static partial uint NvmeNsGetMdSize(IntPtr ns);

    #endregion

    #region Queue Pair

    /// <summary>
    /// Allocates an I/O queue pair for submitting NVMe commands to a controller.
    /// </summary>
    /// <param name="ctrlr">Controller handle.</param>
    /// <param name="opts">Queue pair options (queue depth, priority, etc.), or null for defaults.</param>
    /// <param name="optsSize">Size of the options structure in bytes.</param>
    /// <returns>Queue pair handle, or <see cref="IntPtr.Zero"/> on failure.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_ctrlr_alloc_io_qpair")]
    internal static unsafe partial IntPtr NvmeCtrlrAllocIoQpair(
        IntPtr ctrlr,
        SpdkNvmeIoQpairOpts* opts,
        nint optsSize);

    /// <summary>
    /// Frees an I/O queue pair previously allocated with <see cref="NvmeCtrlrAllocIoQpair"/>.
    /// </summary>
    /// <param name="qpair">Queue pair handle to free.</param>
    /// <returns>0 on success, negative errno on failure.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_ctrlr_free_io_qpair")]
    internal static partial int NvmeCtrlrFreeIoQpair(IntPtr qpair);

    #endregion

    #region I/O Commands

    /// <summary>
    /// Submits an NVMe read command to the specified namespace and queue pair.
    /// The completion callback fires when the I/O is done.
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <param name="qpair">Queue pair handle.</param>
    /// <param name="payload">DMA-aligned buffer to receive read data.</param>
    /// <param name="lba">Starting logical block address.</param>
    /// <param name="lbaCount">Number of logical blocks to read.</param>
    /// <param name="cb">Completion callback invoked on the polling thread.</param>
    /// <param name="cbArg">Opaque argument passed to the completion callback.</param>
    /// <param name="ioFlags">I/O flags (typically 0).</param>
    /// <returns>0 on successful submission, negative errno on failure.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_ns_cmd_read")]
    internal static unsafe partial int NvmeNsCmdRead(
        IntPtr ns,
        IntPtr qpair,
        void* payload,
        ulong lba,
        uint lbaCount,
        delegate* unmanaged[Cdecl]<IntPtr, SpdkNvmeCplStatus*, void> cb,
        IntPtr cbArg,
        uint ioFlags);

    /// <summary>
    /// Submits an NVMe write command to the specified namespace and queue pair.
    /// The completion callback fires when the I/O is done.
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <param name="qpair">Queue pair handle.</param>
    /// <param name="payload">DMA-aligned buffer containing data to write.</param>
    /// <param name="lba">Starting logical block address.</param>
    /// <param name="lbaCount">Number of logical blocks to write.</param>
    /// <param name="cb">Completion callback invoked on the polling thread.</param>
    /// <param name="cbArg">Opaque argument passed to the completion callback.</param>
    /// <param name="ioFlags">I/O flags (typically 0).</param>
    /// <returns>0 on successful submission, negative errno on failure.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_ns_cmd_write")]
    internal static unsafe partial int NvmeNsCmdWrite(
        IntPtr ns,
        IntPtr qpair,
        void* payload,
        ulong lba,
        uint lbaCount,
        delegate* unmanaged[Cdecl]<IntPtr, SpdkNvmeCplStatus*, void> cb,
        IntPtr cbArg,
        uint ioFlags);

    /// <summary>
    /// Polls for I/O completions on the specified queue pair.
    /// Invokes previously registered completion callbacks for any finished commands.
    /// </summary>
    /// <param name="qpair">Queue pair handle to poll.</param>
    /// <param name="maxCompletions">Maximum number of completions to process (0 = unlimited).</param>
    /// <returns>Number of completions processed, or negative errno on failure.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_nvme_qpair_process_completions")]
    internal static partial int NvmeQpairProcessCompletions(IntPtr qpair, uint maxCompletions);

    #endregion

    #region DMA Memory

    /// <summary>
    /// Allocates a DMA-safe memory buffer from SPDK's hugepage pool.
    /// The buffer is guaranteed to be physically contiguous and suitable for NVMe DMA transfers.
    /// </summary>
    /// <param name="size">Number of bytes to allocate.</param>
    /// <param name="align">Alignment in bytes (typically 4096 for NVMe sector alignment).</param>
    /// <param name="physAddr">Output: physical address of the allocation (may be IntPtr.Zero if not needed).</param>
    /// <returns>Pointer to the allocated buffer, or <see cref="IntPtr.Zero"/> on failure.</returns>
    [LibraryImport(LibName, EntryPoint = "spdk_dma_malloc")]
    internal static partial IntPtr DmaMalloc(nint size, nint align, IntPtr physAddr);

    /// <summary>
    /// Frees a DMA buffer previously allocated with <see cref="DmaMalloc"/>.
    /// </summary>
    /// <param name="buf">Pointer to the buffer to free.</param>
    [LibraryImport(LibName, EntryPoint = "spdk_dma_free")]
    internal static partial void DmaFree(IntPtr buf);

    #endregion

    #region Structs

    /// <summary>
    /// SPDK environment initialization options controlling hugepage allocation,
    /// shared memory, and DPDK EAL parameters.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SpdkEnvOpts
    {
        /// <summary>Application name used for DPDK EAL identification.</summary>
        public fixed byte Name[256];

        /// <summary>Shared memory group ID (-1 for no shared memory).</summary>
        public int ShmId;

        /// <summary>Memory size in MB to reserve for hugepages (0 for default).</summary>
        public int MemSize;

        /// <summary>Number of memory channels (0 for auto-detect).</summary>
        public int NumMemChannels;

        /// <summary>Core mask for DPDK EAL thread affinity (0 for default).</summary>
        public int CoreMask;
    }

    /// <summary>
    /// NVMe transport identifier specifying how to reach a controller
    /// (e.g., PCIe BDF address for local NVMe devices).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SpdkNvmeTransportId
    {
        /// <summary>Transport type: 0 = PCIe, 1 = RDMA, 2 = TCP.</summary>
        public byte Trtype;

        /// <summary>Padding for alignment.</summary>
        private readonly byte _pad0;
        private readonly byte _pad1;
        private readonly byte _pad2;

        /// <summary>Transport address (PCIe BDF e.g., "0000:01:00.0").</summary>
        public fixed byte Traddr[256];
    }

    /// <summary>
    /// NVMe completion status returned by the controller for each completed I/O command.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpdkNvmeCplStatus
    {
        /// <summary>Completion queue entry DW0 (command-specific).</summary>
        public uint Cdw0;

        /// <summary>Reserved field.</summary>
        public uint Reserved;

        /// <summary>Submission queue head pointer.</summary>
        public ushort SqHead;

        /// <summary>Submission queue identifier.</summary>
        public ushort SqId;

        /// <summary>Command identifier.</summary>
        public ushort Cid;

        /// <summary>
        /// Status field containing Status Code Type (SCT), Status Code (SC), and
        /// Do Not Retry (DNR) bit. A zero value indicates success.
        /// </summary>
        public ushort Status;

        /// <summary>
        /// Gets whether the completion indicates a successful operation.
        /// </summary>
        public readonly bool IsSuccess => (Status & 0xFFFE) == 0;
    }

    /// <summary>
    /// Options for allocating an NVMe I/O queue pair, controlling queue depth and priority.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpdkNvmeIoQpairOpts
    {
        /// <summary>Queue priority class (0 = urgent, 1 = high, 2 = medium, 3 = low).</summary>
        public byte Qprio;

        /// <summary>Padding for alignment.</summary>
        private readonly byte _pad0;

        /// <summary>I/O queue size (number of entries). Must be a power of 2.</summary>
        public ushort IoQueueSize;

        /// <summary>I/O queue requests (number of trackers). 0 for default.</summary>
        public ushort IoQueueRequests;

        /// <summary>Reserved for future use.</summary>
        private readonly ushort _reserved;
    }

    #endregion

    #region Runtime Detection

    /// <summary>
    /// Gets whether the SPDK native library is available on the current system.
    /// Returns <c>true</c> if libspdk can be loaded, <c>false</c> otherwise.
    /// The result is cached after the first call.
    /// </summary>
    /// <returns><c>true</c> if SPDK is available; otherwise, <c>false</c>.</returns>
    internal static bool IsSupported() => _isSupported.Value;

    private static bool ProbeLibrary()
    {
        try
        {
            return NativeLibrary.TryLoad(LibName, typeof(SpdkNativeMethods).Assembly,
                DllImportSearchPath.SafeDirectories | DllImportSearchPath.System32
                | DllImportSearchPath.UseDllDirectoryForDependencies,
                out _);
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
