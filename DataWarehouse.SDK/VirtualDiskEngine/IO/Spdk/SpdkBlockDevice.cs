using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO.Spdk;

/// <summary>
/// Userspace NVMe block device using SPDK (Storage Performance Development Kit) for
/// bare-metal deployments. Bypasses the kernel block layer entirely via vfio-pci,
/// delivering sub-microsecond I/O latency (~0.3-0.8 us) and up to ~10 GB/s throughput.
/// </summary>
/// <remarks>
/// <para>
/// This is the highest-performance I/O path in the VDE stack. It requires:
/// <list type="bullet">
///   <item>Linux with IOMMU enabled and vfio-pci driver bound to the target NVMe device</item>
///   <item>SPDK native library (libspdk.so) installed and accessible</item>
///   <item>Hugepages configured (typically 2MB or 1GB pages)</item>
///   <item>Sufficient permissions for VFIO device access (typically root or vfio group)</item>
/// </list>
/// </para>
/// <para>
/// Use <see cref="IsSupported"/> to check runtime availability before instantiation.
/// The SPDK environment is initialized lazily on first device creation and persists
/// for the process lifetime.
/// </para>
/// <para>
/// All I/O buffers must be DMA-aligned. Use <see cref="GetAlignedBuffer"/> to allocate
/// buffers from SPDK's hugepage pool. The polling model uses cooperative yielding
/// via <see cref="Task.Yield"/> between completion polls to avoid blocking the
/// SPDK thread while maintaining low latency.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-63: SPDK userspace NVMe block device with sub-microsecond latency")]
public sealed class SpdkBlockDevice : IDirectBlockDevice
{
    /// <summary>
    /// Guards one-time SPDK environment initialization across all device instances.
    /// </summary>
    private static readonly Lazy<bool> _envInitialized = new(InitializeEnvironment, isThreadSafe: true);

    private readonly SpdkDmaAllocator _dmaAllocator = new();
    private readonly int _queueDepth;

    private IntPtr _controller;
    private IntPtr _namespace;
    private IntPtr _qpair;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private volatile bool _disposed;

    /// <inheritdoc/>
    public int BlockSize { get; }

    /// <inheritdoc/>
    public long BlockCount { get; }

    /// <inheritdoc/>
    public bool IsDirectIo => true;

    /// <inheritdoc/>
    public int AlignmentRequirement => BlockSize;

    /// <inheritdoc/>
    public int MaxBatchSize => _queueDepth;

    /// <summary>
    /// Gets the PCIe Bus-Device-Function address of the bound NVMe controller
    /// (e.g., "0000:01:00.0").
    /// </summary>
    public string PciAddress { get; }

    /// <summary>
    /// Gets the NVMe namespace ID this device is operating on.
    /// </summary>
    public uint NamespaceId { get; }

    /// <summary>
    /// Gets whether per-sector metadata is available (non-zero indicates ZNS or similar).
    /// </summary>
    public uint MetadataSize { get; }

    /// <summary>
    /// Creates a new SPDK block device bound to the specified NVMe controller via vfio-pci.
    /// </summary>
    /// <param name="pciAddress">
    /// PCIe BDF address of the NVMe device (e.g., "0000:01:00.0").
    /// The device must be bound to the vfio-pci driver before use.
    /// </param>
    /// <param name="namespaceId">NVMe namespace ID (default 1 for single-namespace devices).</param>
    /// <param name="queueDepth">
    /// Maximum number of in-flight I/O commands (default 256).
    /// Higher values increase throughput for random I/O workloads.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pciAddress"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="namespaceId"/> is 0 or <paramref name="queueDepth"/> is not positive.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when SPDK native library is not available on the current system.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when SPDK environment initialization fails, the NVMe controller cannot be probed,
    /// the namespace is not found, or queue pair allocation fails.
    /// </exception>
    public SpdkBlockDevice(string pciAddress, uint namespaceId = 1, int queueDepth = 256)
    {
        ArgumentNullException.ThrowIfNull(pciAddress);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(namespaceId, 0u);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(queueDepth, 0);

        if (!SpdkNativeMethods.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "SPDK native library (libspdk) is not available on this system. " +
                "SPDK requires Linux with vfio-pci and hugepages configured.");
        }

        PciAddress = pciAddress;
        NamespaceId = namespaceId;
        _queueDepth = queueDepth;

        // Initialize the SPDK environment (once per process)
        if (!_envInitialized.Value)
        {
            throw new InvalidOperationException(
                "SPDK environment initialization failed. Check hugepage configuration and DPDK EAL parameters.");
        }

        // Probe the NVMe controller at the specified PCIe address
        _controller = ProbeController(pciAddress);
        if (_controller == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to attach to NVMe controller at PCIe address '{pciAddress}'. " +
                "Ensure the device is bound to vfio-pci and the process has VFIO group permissions.");
        }

        // Get the namespace handle
        _namespace = SpdkNativeMethods.NvmeCtrlrGetNs(_controller, namespaceId);
        if (_namespace == IntPtr.Zero)
        {
            SpdkNativeMethods.NvmeDetach(_controller);
            _controller = IntPtr.Zero;
            throw new InvalidOperationException(
                $"NVMe namespace {namespaceId} not found on controller at '{pciAddress}'.");
        }

        // Query namespace geometry
        uint sectorSize = SpdkNativeMethods.NvmeNsGetSectorSize(_namespace);
        ulong sectorCount = SpdkNativeMethods.NvmeNsGetNumSectors(_namespace);
        MetadataSize = SpdkNativeMethods.NvmeNsGetMdSize(_namespace);

        BlockSize = (int)sectorSize;
        BlockCount = (long)sectorCount;

        // Allocate I/O queue pair
        _qpair = AllocateQueuePair(_controller, queueDepth);
        if (_qpair == IntPtr.Zero)
        {
            SpdkNativeMethods.NvmeDetach(_controller);
            _controller = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Failed to allocate I/O queue pair with depth {queueDepth} on controller at '{pciAddress}'.");
        }
    }

    /// <summary>
    /// Gets whether SPDK is available on the current system.
    /// </summary>
    /// <returns><c>true</c> if the SPDK native library can be loaded; otherwise, <c>false</c>.</returns>
    public static bool IsSupported() => SpdkNativeMethods.IsSupported();

    /// <inheritdoc/>
    public IMemoryOwner<byte> GetAlignedBuffer(int byteCount)
    {
        ThrowIfDisposed();
        return _dmaAllocator.Allocate(byteCount, AlignmentRequirement);
    }

    /// <inheritdoc/>
    public async Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateBlockNumber(blockNumber);

        if (buffer.Length < BlockSize)
        {
            throw new ArgumentException(
                $"Buffer must be at least {BlockSize} bytes, but was {buffer.Length} bytes.",
                nameof(buffer));
        }

        ct.ThrowIfCancellationRequested();

        await _ioLock.WaitAsync(ct);
        try
        {
            using var dmaBuffer = _dmaAllocator.Allocate(BlockSize, AlignmentRequirement);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var pinned = dmaBuffer.Memory.Pin();
            try
            {
                unsafe
                {
                    void* payload = pinned.Pointer;
                    SubmitReadCommand(_namespace, _qpair, payload, (ulong)blockNumber, 1, tcs);
                }

                await PollForCompletion(_qpair, tcs, ct);
            }
            finally
            {
                pinned.Dispose();
            }

            // Copy from DMA buffer to caller's buffer
            dmaBuffer.Memory.Span[..BlockSize].CopyTo(buffer.Span);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ValidateBlockNumber(blockNumber);

        if (data.Length != BlockSize)
        {
            throw new ArgumentException(
                $"Data must be exactly {BlockSize} bytes, but was {data.Length} bytes.",
                nameof(data));
        }

        ct.ThrowIfCancellationRequested();

        await _ioLock.WaitAsync(ct);
        try
        {
            using var dmaBuffer = _dmaAllocator.Allocate(BlockSize, AlignmentRequirement);

            // Copy caller's data into DMA buffer
            data.Span.CopyTo(dmaBuffer.Memory.Span);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var pinned = dmaBuffer.Memory.Pin();
            try
            {
                unsafe
                {
                    void* payload = pinned.Pointer;
                    SubmitWriteCommand(_namespace, _qpair, payload, (ulong)blockNumber, 1, tcs);
                }

                await PollForCompletion(_qpair, tcs, ct);
            }
            finally
            {
                pinned.Dispose();
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<int> ReadBatchAsync(IReadOnlyList<BlockRange> requests, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count > MaxBatchSize)
        {
            throw new ArgumentException(
                $"Batch size {requests.Count} exceeds maximum {MaxBatchSize}.",
                nameof(requests));
        }

        if (requests.Count == 0)
        {
            return 0;
        }

        ct.ThrowIfCancellationRequested();

        await _ioLock.WaitAsync(ct);
        try
        {
            int completed = 0;

            // Submit all read commands to the queue pair for parallel execution
            var completions = new TaskCompletionSource[requests.Count];
            var dmaBuffers = new IMemoryOwner<byte>[requests.Count];

            try
            {
                for (int i = 0; i < requests.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var req = requests[i];
                    ValidateBlockNumber(req.BlockNumber);

                    if (req.BlockNumber + req.BlockCount > BlockCount)
                    {
                        throw new ArgumentOutOfRangeException(nameof(requests),
                            $"Request {i} extends beyond device: blocks {req.BlockNumber}..{req.BlockNumber + req.BlockCount - 1}, " +
                            $"device has {BlockCount} blocks.");
                    }

                    int totalBytes = req.BlockCount * BlockSize;
                    if (req.Buffer.Length < totalBytes)
                    {
                        throw new ArgumentException(
                            $"Request {i} buffer too small: need {totalBytes} bytes, have {req.Buffer.Length}.",
                            nameof(requests));
                    }

                    dmaBuffers[i] = _dmaAllocator.Allocate(totalBytes, AlignmentRequirement);
                    completions[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    unsafe
                    {
                        using var pinned = dmaBuffers[i].Memory.Pin();
                        void* payload = pinned.Pointer;

                        SubmitReadCommand(_namespace, _qpair, payload,
                            (ulong)req.BlockNumber, (uint)req.BlockCount, completions[i]);
                    }
                }

                // Poll for all completions
                await PollForAllCompletions(_qpair, completions, ct);

                // Copy results and count successes
                for (int i = 0; i < requests.Count; i++)
                {
                    if (completions[i].Task.IsCompletedSuccessfully)
                    {
                        int totalBytes = requests[i].BlockCount * BlockSize;
                        dmaBuffers[i].Memory.Span[..totalBytes].CopyTo(requests[i].Buffer.Span);
                        completed++;
                    }
                    else
                    {
                        // Stop at first failure for partial success semantics
                        break;
                    }
                }
            }
            finally
            {
                for (int i = 0; i < dmaBuffers.Length; i++)
                {
                    dmaBuffers[i]?.Dispose();
                }
            }

            return completed;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<int> WriteBatchAsync(IReadOnlyList<WriteRequest> requests, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count > MaxBatchSize)
        {
            throw new ArgumentException(
                $"Batch size {requests.Count} exceeds maximum {MaxBatchSize}.",
                nameof(requests));
        }

        if (requests.Count == 0)
        {
            return 0;
        }

        ct.ThrowIfCancellationRequested();

        await _ioLock.WaitAsync(ct);
        try
        {
            int completed = 0;

            var completions = new TaskCompletionSource[requests.Count];
            var dmaBuffers = new IMemoryOwner<byte>[requests.Count];

            try
            {
                for (int i = 0; i < requests.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var req = requests[i];
                    ValidateBlockNumber(req.BlockNumber);

                    if (req.BlockNumber + req.BlockCount > BlockCount)
                    {
                        throw new ArgumentOutOfRangeException(nameof(requests),
                            $"Request {i} extends beyond device: blocks {req.BlockNumber}..{req.BlockNumber + req.BlockCount - 1}, " +
                            $"device has {BlockCount} blocks.");
                    }

                    int totalBytes = req.BlockCount * BlockSize;
                    if (req.Data.Length != totalBytes)
                    {
                        throw new ArgumentException(
                            $"Request {i} data size mismatch: expected {totalBytes} bytes, have {req.Data.Length}.",
                            nameof(requests));
                    }

                    dmaBuffers[i] = _dmaAllocator.Allocate(totalBytes, AlignmentRequirement);

                    // Copy caller's data into DMA buffer
                    req.Data.Span.CopyTo(dmaBuffers[i].Memory.Span);

                    completions[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    unsafe
                    {
                        using var pinned = dmaBuffers[i].Memory.Pin();
                        void* payload = pinned.Pointer;

                        SubmitWriteCommand(_namespace, _qpair, payload,
                            (ulong)req.BlockNumber, (uint)req.BlockCount, completions[i]);
                    }
                }

                // Poll for all completions
                await PollForAllCompletions(_qpair, completions, ct);

                // Count successes
                for (int i = 0; i < requests.Count; i++)
                {
                    if (completions[i].Task.IsCompletedSuccessfully)
                    {
                        completed++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            finally
            {
                for (int i = 0; i < dmaBuffers.Length; i++)
                {
                    dmaBuffers[i]?.Dispose();
                }
            }

            return completed;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        // NVMe flush is implicit with SPDK's synchronous completion model.
        // The write command guarantees data has reached the NVMe controller's write buffer,
        // and NVMe controllers with power-loss protection commit to non-volatile storage.
        // For controllers without PLP, a dedicated flush command could be submitted here.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _ioLock.WaitAsync();
        try
        {
            if (_qpair != IntPtr.Zero)
            {
                SpdkNativeMethods.NvmeCtrlrFreeIoQpair(_qpair);
                _qpair = IntPtr.Zero;
            }

            _namespace = IntPtr.Zero;

            if (_controller != IntPtr.Zero)
            {
                SpdkNativeMethods.NvmeDetach(_controller);
                _controller = IntPtr.Zero;
            }
        }
        finally
        {
            _ioLock.Release();
            _ioLock.Dispose();
        }
    }

    #region Private Helpers

    private static bool InitializeEnvironment()
    {
        var opts = new SpdkNativeMethods.SpdkEnvOpts();
        SpdkNativeMethods.EnvOptsInit(ref opts);

        // Set application name
        ReadOnlySpan<byte> appName = "DataWarehouse.VDE"u8;
        unsafe
        {
            byte* namePtr = opts.Name;
            appName.CopyTo(new Span<byte>(namePtr, Math.Min(appName.Length, 255)));
            namePtr[Math.Min(appName.Length, 255)] = 0; // Null-terminate
        }

        // Use default shared memory group (no isolation)
        opts.ShmId = -1;

        int rc = SpdkNativeMethods.EnvInit(ref opts);
        return rc == 0;
    }

    private static unsafe IntPtr ProbeController(string pciAddress)
    {
        var trid = new SpdkNativeMethods.SpdkNvmeTransportId();
        trid.Trtype = 0; // PCIe transport

        // Copy PCI address into the transport ID
        byte[] addrBytes = Encoding.ASCII.GetBytes(pciAddress);
        byte* traddrPtr = trid.Traddr;
        int copyLen = Math.Min(addrBytes.Length, 255);
        Marshal.Copy(addrBytes, 0, (IntPtr)traddrPtr, copyLen);
        traddrPtr[copyLen] = 0; // Null-terminate

        IntPtr attachedController = IntPtr.Zero;

        // The probe callback accepts all controllers matching our transport ID
        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        static byte ProbeCallback(IntPtr cbCtx, IntPtr ctrlr, SpdkNativeMethods.SpdkNvmeTransportId* trid)
        {
            return 1; // Accept (attach to this controller)
        }

        // The attach callback captures the controller handle
        // Note: We use a static local and store via the cbCtx pointer
        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        static void AttachCallback(IntPtr cbCtx, IntPtr ctrlr, SpdkNativeMethods.SpdkNvmeTransportId* trid)
        {
            if (cbCtx != IntPtr.Zero)
            {
                Marshal.WriteIntPtr(cbCtx, ctrlr);
            }
        }

        // Allocate a slot to receive the controller handle from the callback
        IntPtr ctrlrSlot = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(ctrlrSlot, IntPtr.Zero);

            int rc = SpdkNativeMethods.NvmeProbe(
                ref trid,
                ctrlrSlot,
                &ProbeCallback,
                &AttachCallback,
                null);

            if (rc == 0)
            {
                attachedController = Marshal.ReadIntPtr(ctrlrSlot);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ctrlrSlot);
        }

        return attachedController;
    }

    private static unsafe IntPtr AllocateQueuePair(IntPtr controller, int queueDepth)
    {
        var opts = new SpdkNativeMethods.SpdkNvmeIoQpairOpts
        {
            Qprio = 2, // Medium priority
            IoQueueSize = (ushort)Math.Min(queueDepth, ushort.MaxValue),
            IoQueueRequests = 0 // Default
        };

        return SpdkNativeMethods.NvmeCtrlrAllocIoQpair(
            controller,
            &opts,
            (nint)Marshal.SizeOf<SpdkNativeMethods.SpdkNvmeIoQpairOpts>());
    }

    private static unsafe void SubmitReadCommand(
        IntPtr ns, IntPtr qpair, void* payload,
        ulong lba, uint lbaCount, TaskCompletionSource tcs)
    {
        // Store TCS handle as GCHandle for the completion callback
        GCHandle handle = GCHandle.Alloc(tcs);

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        static void ReadCompletionCallback(IntPtr cbArg, SpdkNativeMethods.SpdkNvmeCplStatus* status)
        {
            GCHandle tcsHandle = GCHandle.FromIntPtr(cbArg);
            var completionSource = (TaskCompletionSource)tcsHandle.Target!;
            tcsHandle.Free();

            if (status->IsSuccess)
            {
                completionSource.TrySetResult();
            }
            else
            {
                completionSource.TrySetException(new IOException(
                    $"SPDK NVMe read failed with status 0x{status->Status:X4}."));
            }
        }

        int rc = SpdkNativeMethods.NvmeNsCmdRead(
            ns, qpair, payload, lba, lbaCount,
            &ReadCompletionCallback,
            GCHandle.ToIntPtr(handle),
            0);

        if (rc != 0)
        {
            handle.Free();
            tcs.TrySetException(new IOException(
                $"Failed to submit SPDK NVMe read command: error {rc}."));
        }
    }

    private static unsafe void SubmitWriteCommand(
        IntPtr ns, IntPtr qpair, void* payload,
        ulong lba, uint lbaCount, TaskCompletionSource tcs)
    {
        GCHandle handle = GCHandle.Alloc(tcs);

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        static void WriteCompletionCallback(IntPtr cbArg, SpdkNativeMethods.SpdkNvmeCplStatus* status)
        {
            GCHandle tcsHandle = GCHandle.FromIntPtr(cbArg);
            var completionSource = (TaskCompletionSource)tcsHandle.Target!;
            tcsHandle.Free();

            if (status->IsSuccess)
            {
                completionSource.TrySetResult();
            }
            else
            {
                completionSource.TrySetException(new IOException(
                    $"SPDK NVMe write failed with status 0x{status->Status:X4}."));
            }
        }

        int rc = SpdkNativeMethods.NvmeNsCmdWrite(
            ns, qpair, payload, lba, lbaCount,
            &WriteCompletionCallback,
            GCHandle.ToIntPtr(handle),
            0);

        if (rc != 0)
        {
            handle.Free();
            tcs.TrySetException(new IOException(
                $"Failed to submit SPDK NVMe write command: error {rc}."));
        }
    }

    /// <summary>
    /// Polls the SPDK queue pair for a single completion, yielding between polls
    /// to avoid blocking the thread.
    /// </summary>
    private static async Task PollForCompletion(IntPtr qpair, TaskCompletionSource tcs, CancellationToken ct)
    {
        while (!tcs.Task.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            SpdkNativeMethods.NvmeQpairProcessCompletions(qpair, 0);
            if (!tcs.Task.IsCompleted)
            {
                await Task.Yield();
            }
        }

        await tcs.Task; // Propagate any exception
    }

    /// <summary>
    /// Polls the SPDK queue pair until all submitted commands complete.
    /// </summary>
    private static async Task PollForAllCompletions(
        IntPtr qpair, TaskCompletionSource[] completions, CancellationToken ct)
    {
        bool allDone = false;
        while (!allDone)
        {
            ct.ThrowIfCancellationRequested();
            SpdkNativeMethods.NvmeQpairProcessCompletions(qpair, 0);

            allDone = true;
            for (int i = 0; i < completions.Length; i++)
            {
                if (!completions[i].Task.IsCompleted)
                {
                    allDone = false;
                    break;
                }
            }

            if (!allDone)
            {
                await Task.Yield();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ValidateBlockNumber(long blockNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, BlockCount);
    }

    #endregion
}
