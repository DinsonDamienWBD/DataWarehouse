using System;
using System.Runtime.InteropServices;
using System.Security;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Preamble;

/// <summary>
/// P/Invoke declarations for the SPDK NVMe driver, specialised for the preamble bare-metal
/// boot path (VOPT-63). These bindings target the <c>libspdk_nvme</c> library that ships
/// inside the preamble SPDK driver pack.
/// </summary>
/// <remarks>
/// <para>
/// For the general-purpose SPDK device used in hosted environments, see
/// <see cref="IO.Spdk.SpdkNativeMethods"/>. This class exists separately because the
/// preamble boot scenario links against the stripped <c>libspdk_nvme</c> library bundled
/// in the preamble payload, whereas hosted environments use the full SPDK installation.
/// </para>
/// <para>
/// All methods are decorated with <see cref="SuppressUnmanagedCodeSecurityAttribute"/>
/// to eliminate the per-call stack walk overhead in the hot I/O path.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5 Wave 6: VOPT-63 preamble SPDK P/Invoke bindings")]
[SuppressUnmanagedCodeSecurity]
internal static partial class SpdkNativeBindings
{
    /// <summary>
    /// Native library name for the preamble-bundled SPDK NVMe driver.
    /// This is the stripped single-library build shipped inside the preamble driver pack.
    /// </summary>
    private const string SpdkLib = "libspdk_nvme";

    private static readonly Lazy<bool> IsAvailableLazy = new(ProbeLibrary, isThreadSafe: true);

    #region Environment Initialisation

    /// <summary>
    /// Initialises an <see cref="SpdkPreambleEnvOpts"/> structure with default values.
    /// Must be called before modifying fields and passing to <see cref="EnvInit"/>.
    /// </summary>
    /// <param name="opts">Options structure to initialise.</param>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_env_opts_init")]
    internal static partial void EnvOptsInit(ref SpdkPreambleEnvOpts opts);

    /// <summary>
    /// Initialises the SPDK environment, setting up hugepages, DPDK EAL, and IOMMU.
    /// Must be called exactly once before any other SPDK function.
    /// </summary>
    /// <param name="opts">Initialised environment options.</param>
    /// <returns>0 on success, negative errno on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_env_init")]
    internal static partial int EnvInit(ref SpdkPreambleEnvOpts opts);

    #endregion

    #region NVMe Probe & Attach

    /// <summary>
    /// Probes for NVMe controllers matching the specified transport identifier.
    /// In the preamble boot path this targets a single PCI BDF address.
    /// </summary>
    /// <param name="trid">Transport identifier (PCIe BDF address).</param>
    /// <param name="cbCtx">Opaque context pointer forwarded to all callbacks.</param>
    /// <param name="probeCb">Called per discovered controller; return 1 to attach, 0 to skip.</param>
    /// <param name="attachCb">Called after successful controller attachment.</param>
    /// <param name="removeCb">Called on hot-removal (may be <see cref="IntPtr.Zero"/>).</param>
    /// <returns>0 on success, negative errno on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_probe")]
    internal static unsafe partial int NvmeProbe(
        ref SpdkPreambleTransportId trid,
        IntPtr cbCtx,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, SpdkPreambleTransportId*, byte> probeCb,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, SpdkPreambleTransportId*, void> attachCb,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> removeCb);

    /// <summary>
    /// Detaches from an NVMe controller and releases all associated resources.
    /// </summary>
    /// <param name="ctrlr">Controller handle obtained from the attach callback.</param>
    /// <returns>0 on success, negative errno on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_detach")]
    internal static partial int NvmeDetach(IntPtr ctrlr);

    #endregion

    #region Namespace

    /// <summary>
    /// Gets the namespace handle for the given namespace ID on a controller.
    /// </summary>
    /// <param name="ctrlr">Controller handle.</param>
    /// <param name="nsid">Namespace ID (typically 1).</param>
    /// <returns>Namespace handle, or <see cref="IntPtr.Zero"/> if not found.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_ctrlr_get_ns")]
    internal static partial IntPtr NvmeCtrlrGetNs(IntPtr ctrlr, uint nsid);

    /// <summary>
    /// Gets the sector size in bytes for the specified namespace.
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <returns>Sector size in bytes (typically 512 or 4096).</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_ns_get_sector_size")]
    internal static partial uint NvmeNsGetSectorSize(IntPtr ns);

    /// <summary>
    /// Gets the total number of sectors in the specified namespace.
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <returns>Total sector count.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_ns_get_num_sectors")]
    internal static partial ulong NvmeNsGetNumSectors(IntPtr ns);

    #endregion

    #region Queue Pair

    /// <summary>
    /// Allocates an I/O queue pair for submitting NVMe commands.
    /// </summary>
    /// <param name="ctrlr">Controller handle.</param>
    /// <param name="opts">Queue pair options, or <c>null</c> for defaults.</param>
    /// <param name="optsSize">Size of the options structure in bytes.</param>
    /// <returns>Queue pair handle, or <see cref="IntPtr.Zero"/> on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_ctrlr_alloc_io_qpair")]
    internal static partial IntPtr NvmeCtrlrAllocIoQpair(
        IntPtr ctrlr,
        IntPtr opts,
        nuint optsSize);

    /// <summary>
    /// Frees an I/O queue pair previously allocated with <see cref="NvmeCtrlrAllocIoQpair"/>.
    /// </summary>
    /// <param name="qpair">Queue pair handle.</param>
    /// <returns>0 on success, negative errno on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_ctrlr_free_io_qpair")]
    internal static partial int NvmeCtrlrFreeIoQpair(IntPtr qpair);

    #endregion

    #region I/O Commands

    /// <summary>
    /// Submits an asynchronous NVMe read command.
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <param name="qpair">Queue pair handle.</param>
    /// <param name="payload">DMA-aligned buffer to receive data.</param>
    /// <param name="lba">Starting logical block address.</param>
    /// <param name="lbaCount">Number of logical blocks to read.</param>
    /// <param name="cb">Completion callback.</param>
    /// <param name="cbArg">Opaque argument for the callback.</param>
    /// <param name="ioFlags">I/O flags (typically 0).</param>
    /// <returns>0 on successful submission, negative errno on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_ns_cmd_read")]
    internal static unsafe partial int NvmeNsCmdRead(
        IntPtr ns,
        IntPtr qpair,
        void* payload,
        ulong lba,
        uint lbaCount,
        delegate* unmanaged[Cdecl]<IntPtr, SpdkPreambleCplStatus*, void> cb,
        IntPtr cbArg,
        uint ioFlags);

    /// <summary>
    /// Submits an asynchronous NVMe write command.
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <param name="qpair">Queue pair handle.</param>
    /// <param name="payload">DMA-aligned buffer containing data to write.</param>
    /// <param name="lba">Starting logical block address.</param>
    /// <param name="lbaCount">Number of logical blocks to write.</param>
    /// <param name="cb">Completion callback.</param>
    /// <param name="cbArg">Opaque argument for the callback.</param>
    /// <param name="ioFlags">I/O flags (typically 0).</param>
    /// <returns>0 on successful submission, negative errno on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_ns_cmd_write")]
    internal static unsafe partial int NvmeNsCmdWrite(
        IntPtr ns,
        IntPtr qpair,
        void* payload,
        ulong lba,
        uint lbaCount,
        delegate* unmanaged[Cdecl]<IntPtr, SpdkPreambleCplStatus*, void> cb,
        IntPtr cbArg,
        uint ioFlags);

    /// <summary>
    /// Submits an NVMe flush command to persist all previously written data.
    /// </summary>
    /// <param name="ns">Namespace handle.</param>
    /// <param name="qpair">Queue pair handle.</param>
    /// <param name="cb">Completion callback.</param>
    /// <param name="cbArg">Opaque argument for the callback.</param>
    /// <returns>0 on successful submission, negative errno on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_ns_cmd_flush")]
    internal static unsafe partial int NvmeNsCmdFlush(
        IntPtr ns,
        IntPtr qpair,
        delegate* unmanaged[Cdecl]<IntPtr, SpdkPreambleCplStatus*, void> cb,
        IntPtr cbArg);

    /// <summary>
    /// Polls for I/O completions on the specified queue pair, invoking registered
    /// completion callbacks for finished commands.
    /// </summary>
    /// <param name="qpair">Queue pair handle.</param>
    /// <param name="maxCompletions">Maximum completions to process (0 = unlimited).</param>
    /// <returns>Number of completions processed, or negative errno on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_nvme_qpair_process_completions")]
    internal static partial int NvmeQpairProcessCompletions(IntPtr qpair, uint maxCompletions);

    #endregion

    #region DMA Memory

    /// <summary>
    /// Allocates a zeroed, DMA-safe memory buffer from SPDK's hugepage pool.
    /// The buffer is physically contiguous and suitable for NVMe DMA transfers.
    /// </summary>
    /// <param name="size">Number of bytes to allocate.</param>
    /// <param name="align">Alignment in bytes (typically matches block size).</param>
    /// <param name="physAddr">Output physical address (pass <see cref="IntPtr.Zero"/> if not needed).</param>
    /// <returns>Pointer to allocated buffer, or <see cref="IntPtr.Zero"/> on failure.</returns>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_dma_zmalloc")]
    internal static partial IntPtr DmaZmalloc(nuint size, nuint align, IntPtr physAddr);

    /// <summary>
    /// Frees a DMA buffer previously allocated with <see cref="DmaZmalloc"/>.
    /// </summary>
    /// <param name="buf">Pointer to the buffer to free.</param>
    [LibraryImport(SpdkLib, EntryPoint = "spdk_dma_free")]
    internal static partial void DmaFree(IntPtr buf);

    #endregion

    #region Runtime Detection

    /// <summary>
    /// Gets whether the preamble SPDK library (<c>libspdk_nvme</c>) is available
    /// on the current system. The result is cached after the first probe.
    /// </summary>
    internal static bool IsAvailable => IsAvailableLazy.Value;

    private static bool ProbeLibrary()
    {
        try
        {
            return NativeLibrary.TryLoad(SpdkLib, typeof(SpdkNativeBindings).Assembly,
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

    #region Structs

    /// <summary>
    /// SPDK environment initialisation options for the preamble boot scenario.
    /// Configures hugepage allocation, shared memory, and DPDK EAL parameters.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SpdkPreambleEnvOpts
    {
        /// <summary>Application name for DPDK EAL identification.</summary>
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
    /// NVMe transport identifier for the preamble boot path, specifying a single
    /// PCI BDF address for the target NVMe device bound to vfio-pci.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SpdkPreambleTransportId
    {
        /// <summary>Transport type: 0 = PCIe, 1 = RDMA, 2 = TCP.</summary>
        public byte Trtype;

        /// <summary>Padding for alignment.</summary>
        private readonly byte _pad0;
        private readonly byte _pad1;
        private readonly byte _pad2;

        /// <summary>Transport address (PCIe BDF, e.g. "0000:01:00.0").</summary>
        public fixed byte Traddr[256];
    }

    /// <summary>
    /// NVMe completion status for the preamble boot path. Zero <see cref="Status"/>
    /// indicates success.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpdkPreambleCplStatus
    {
        /// <summary>Command-specific completion DW0.</summary>
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
        /// Status field: SCT (bits 9-11), SC (bits 1-8), DNR (bit 14).
        /// Zero indicates success.
        /// </summary>
        public ushort Status;

        /// <summary>Gets whether the completion indicates success.</summary>
        public readonly bool IsSuccess => (Status & 0xFFFE) == 0;
    }

    #endregion
}
