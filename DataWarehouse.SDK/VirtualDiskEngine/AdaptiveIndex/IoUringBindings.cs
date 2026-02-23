using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Low-level P/Invoke bindings to liburing.so for io_uring kernel I/O interface.
/// Provides queue management, SQE preparation helpers, registered buffers,
/// and NVMe passthrough (IORING_OP_URING_CMD).
/// On non-Linux platforms, <see cref="IsAvailable"/> returns false and all operations
/// should be routed through the fallback <see cref="FileBlockDevice"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-12 io_uring")]
public static class IoUringBindings
{
    // ─── Constants ──────────────────────────────────────────────────────

    /// <summary>io_uring opcode for standard read.</summary>
    public const byte IORING_OP_READ = 22;

    /// <summary>io_uring opcode for standard write.</summary>
    public const byte IORING_OP_WRITE = 23;

    /// <summary>io_uring opcode for read from a registered (fixed) buffer.</summary>
    public const byte IORING_OP_READ_FIXED = 4;

    /// <summary>io_uring opcode for write from a registered (fixed) buffer.</summary>
    public const byte IORING_OP_WRITE_FIXED = 5;

    /// <summary>io_uring opcode for NVMe passthrough command.</summary>
    public const byte IORING_OP_URING_CMD = 80;

    /// <summary>Setup flag: kernel-side submission polling (no syscall per submit).</summary>
    public const uint IORING_SETUP_SQPOLL = 1u << 1;

    /// <summary>Setup flag: I/O polling (busy-wait for completions).</summary>
    public const uint IORING_SETUP_IOPOLL = 1u << 0;

    /// <summary>Setup flag: single issuer (thread-safety hint to kernel).</summary>
    public const uint IORING_SETUP_SINGLE_ISSUER = 1u << 12;

    /// <summary>SQE flag: use registered file descriptors.</summary>
    public const byte IOSQE_FIXED_FILE = 1 << 0;

    /// <summary>SQE flag: issue after previous completes.</summary>
    public const byte IOSQE_IO_LINK = 1 << 2;

    /// <summary>Linux O_DIRECT flag for bypassing the page cache.</summary>
    public const int O_DIRECT = 0x4000;

    /// <summary>Linux O_RDWR flag.</summary>
    public const int O_RDWR = 0x0002;

    /// <summary>Linux O_CREAT flag.</summary>
    public const int O_CREAT = 0x0040;

    // ─── Structs ────────────────────────────────────────────────────────

    /// <summary>
    /// io_uring setup parameters, matching the C struct io_uring_params.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringParams
    {
        /// <summary>Requested submission queue entries.</summary>
        public uint SqEntries;

        /// <summary>Requested completion queue entries.</summary>
        public uint CqEntries;

        /// <summary>Setup flags (e.g., IORING_SETUP_SQPOLL).</summary>
        public uint Flags;

        /// <summary>Idle time in milliseconds before SQ poll thread sleeps.</summary>
        public uint SqThreadIdle;

        /// <summary>CPU to bind the SQ poll thread to.</summary>
        public uint SqThreadCpu;

        /// <summary>Kernel feature flags (populated on return).</summary>
        public uint Features;

        /// <summary>WQ fd for sharing kernel worker threads.</summary>
        public uint WqFd;

        /// <summary>Reserved for future use.</summary>
        private unsafe fixed uint _resv[3];

        /// <summary>SQ ring offset parameters (populated on return).</summary>
        public IoSqRingOffsets SqOff;

        /// <summary>CQ ring offset parameters (populated on return).</summary>
        public IoCqRingOffsets CqOff;
    }

    /// <summary>
    /// Submission queue ring offsets returned by io_uring_queue_init_params.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoSqRingOffsets
    {
        public uint Head;
        public uint Tail;
        public uint RingMask;
        public uint RingEntries;
        public uint Flags;
        public uint Dropped;
        public uint Array;
        public uint Resv1;
        public ulong Resv2;
    }

    /// <summary>
    /// Completion queue ring offsets returned by io_uring_queue_init_params.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoCqRingOffsets
    {
        public uint Head;
        public uint Tail;
        public uint RingMask;
        public uint RingEntries;
        public uint Overflow;
        public uint Cqes;
        public uint Flags;
        public uint Resv1;
        public ulong Resv2;
    }

    /// <summary>
    /// Submission queue entry (SQE), matching the 64-byte C struct io_uring_sqe.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct IoUringSqe
    {
        /// <summary>Operation code (IORING_OP_*).</summary>
        [FieldOffset(0)] public byte Opcode;

        /// <summary>SQE flags (IOSQE_*).</summary>
        [FieldOffset(1)] public byte Flags;

        /// <summary>ioprio for the request.</summary>
        [FieldOffset(2)] public ushort Ioprio;

        /// <summary>File descriptor or registered file index.</summary>
        [FieldOffset(4)] public int Fd;

        /// <summary>Offset in the file.</summary>
        [FieldOffset(8)] public long Off;

        /// <summary>Buffer address.</summary>
        [FieldOffset(16)] public nint Addr;

        /// <summary>Length in bytes.</summary>
        [FieldOffset(24)] public uint Len;

        /// <summary>Union field: rw_flags, fsync_flags, etc.</summary>
        [FieldOffset(28)] public int RwFlags;

        /// <summary>User data for correlation on completion.</summary>
        [FieldOffset(32)] public ulong UserData;

        /// <summary>Buffer index for registered buffers (IORING_OP_*_FIXED).</summary>
        [FieldOffset(40)] public ushort BufIndex;

        /// <summary>Personality for credential switching.</summary>
        [FieldOffset(42)] public ushort Personality;

        /// <summary>Splice fd or file index for registered files.</summary>
        [FieldOffset(44)] public int SpliceFdIn;

        /// <summary>Padding / cmd field for IORING_OP_URING_CMD.</summary>
        [FieldOffset(48)] public nint CmdAddr;
    }

    /// <summary>
    /// Completion queue entry (CQE), matching the 16-byte C struct io_uring_cqe.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringCqe
    {
        /// <summary>User data from the corresponding SQE.</summary>
        public ulong UserData;

        /// <summary>Result of the operation (bytes transferred or negative errno).</summary>
        public int Res;

        /// <summary>CQE flags.</summary>
        public uint Flags;
    }

    /// <summary>
    /// Opaque io_uring ring structure. The actual layout is managed by liburing;
    /// this wrapper holds the ring fd and mmap pointers.
    /// We allocate sufficient space to cover the liburing struct io_uring.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 216)]
    public struct IoUring
    {
        // Opaque: contents managed by liburing.
        // 216 bytes covers the liburing struct io_uring on x86-64.
        private unsafe fixed byte _data[216];
    }

    /// <summary>
    /// iovec structure for registered buffer operations.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoVec
    {
        /// <summary>Base address of the buffer.</summary>
        public nint Base;

        /// <summary>Length of the buffer in bytes.</summary>
        public nuint Len;
    }

    // ─── P/Invoke Methods ───────────────────────────────────────────────

    /// <summary>
    /// Initializes an io_uring instance with the given parameters.
    /// </summary>
    /// <param name="entries">Number of SQ entries (rounded to power of two by kernel).</param>
    /// <param name="ring">Ring instance to initialize.</param>
    /// <param name="p">Parameters including flags and thread settings.</param>
    /// <returns>0 on success, negative errno on failure.</returns>
    [DllImport("liburing", EntryPoint = "io_uring_queue_init_params", SetLastError = true)]
    public static extern int QueueInitParams(uint entries, ref IoUring ring, ref IoUringParams p);

    /// <summary>
    /// Destroys an io_uring instance, releasing kernel resources.
    /// </summary>
    [DllImport("liburing", EntryPoint = "io_uring_queue_exit", SetLastError = true)]
    public static extern void QueueExit(ref IoUring ring);

    /// <summary>
    /// Gets the next available submission queue entry.
    /// </summary>
    /// <returns>Pointer to an SQE, or IntPtr.Zero if the SQ is full.</returns>
    [DllImport("liburing", EntryPoint = "io_uring_get_sqe", SetLastError = true)]
    public static extern nint GetSqe(ref IoUring ring);

    /// <summary>
    /// Submits all pending SQEs to the kernel.
    /// </summary>
    /// <returns>Number of SQEs submitted, or negative errno.</returns>
    [DllImport("liburing", EntryPoint = "io_uring_submit", SetLastError = true)]
    public static extern int Submit(ref IoUring ring);

    /// <summary>
    /// Waits for at least one completion queue entry.
    /// </summary>
    /// <returns>0 on success with cqe populated, negative errno on failure.</returns>
    [DllImport("liburing", EntryPoint = "io_uring_wait_cqe", SetLastError = true)]
    public static extern int WaitCqe(ref IoUring ring, out nint cqe);

    /// <summary>
    /// Peeks at a batch of completion queue entries without blocking.
    /// </summary>
    /// <returns>Number of CQEs available.</returns>
    [DllImport("liburing", EntryPoint = "io_uring_peek_batch_cqe", SetLastError = true)]
    public static extern unsafe uint PeekBatchCqe(ref IoUring ring, nint* cqes, uint count);

    /// <summary>
    /// Marks a CQE as consumed, advancing the CQ head.
    /// </summary>
    [DllImport("liburing", EntryPoint = "io_uring_cqe_seen", SetLastError = true)]
    public static extern void CqeSeen(ref IoUring ring, nint cqe);

    /// <summary>
    /// Registers pre-allocated buffers with the kernel for zero-copy I/O.
    /// </summary>
    /// <returns>0 on success, negative errno on failure.</returns>
    [DllImport("liburing", EntryPoint = "io_uring_register_buffers", SetLastError = true)]
    public static extern int RegisterBuffers(ref IoUring ring, nint iovecs, uint nrIovecs);

    /// <summary>
    /// Unregisters previously registered buffers.
    /// </summary>
    [DllImport("liburing", EntryPoint = "io_uring_unregister_buffers", SetLastError = true)]
    public static extern int UnregisterBuffers(ref IoUring ring);

    /// <summary>
    /// Registers file descriptors for use with IOSQE_FIXED_FILE.
    /// </summary>
    [DllImport("liburing", EntryPoint = "io_uring_register_files", SetLastError = true)]
    public static extern unsafe int RegisterFiles(ref IoUring ring, int* fds, uint nrFds);

    /// <summary>
    /// Opens a file via POSIX open(). Used on Linux for O_DIRECT file access.
    /// </summary>
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    public static extern int PosixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags, int mode);

    /// <summary>
    /// Closes a POSIX file descriptor.
    /// </summary>
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    public static extern int PosixClose(int fd);

    // ─── SQE Prep Helpers ───────────────────────────────────────────────

    /// <summary>
    /// Prepares a standard read SQE (IORING_OP_READ).
    /// </summary>
    public static unsafe void PrepRead(nint sqe, int fd, nint buf, uint nbytes, long offset)
    {
        var s = (IoUringSqe*)sqe;
        *s = default;
        s->Opcode = IORING_OP_READ;
        s->Fd = fd;
        s->Addr = buf;
        s->Len = nbytes;
        s->Off = offset;
    }

    /// <summary>
    /// Prepares a standard write SQE (IORING_OP_WRITE).
    /// </summary>
    public static unsafe void PrepWrite(nint sqe, int fd, nint buf, uint nbytes, long offset)
    {
        var s = (IoUringSqe*)sqe;
        *s = default;
        s->Opcode = IORING_OP_WRITE;
        s->Fd = fd;
        s->Addr = buf;
        s->Len = nbytes;
        s->Off = offset;
    }

    /// <summary>
    /// Prepares a read from a registered (fixed) buffer (IORING_OP_READ_FIXED).
    /// Eliminates per-I/O kernel buffer copies.
    /// </summary>
    public static unsafe void PrepReadFixed(nint sqe, int fd, nint buf, uint nbytes, long offset, int bufIndex)
    {
        var s = (IoUringSqe*)sqe;
        *s = default;
        s->Opcode = IORING_OP_READ_FIXED;
        s->Fd = fd;
        s->Addr = buf;
        s->Len = nbytes;
        s->Off = offset;
        s->BufIndex = (ushort)bufIndex;
    }

    /// <summary>
    /// Prepares a write from a registered (fixed) buffer (IORING_OP_WRITE_FIXED).
    /// Eliminates per-I/O kernel buffer copies.
    /// </summary>
    public static unsafe void PrepWriteFixed(nint sqe, int fd, nint buf, uint nbytes, long offset, int bufIndex)
    {
        var s = (IoUringSqe*)sqe;
        *s = default;
        s->Opcode = IORING_OP_WRITE_FIXED;
        s->Fd = fd;
        s->Addr = buf;
        s->Len = nbytes;
        s->Off = offset;
        s->BufIndex = (ushort)bufIndex;
    }

    /// <summary>
    /// Prepares an NVMe passthrough command SQE (IORING_OP_URING_CMD).
    /// Only available on raw NVMe block devices (/dev/nvme*).
    /// </summary>
    public static unsafe void PrepUringCmd(nint sqe, int fd, nint cmdBuf, uint cmdLen)
    {
        var s = (IoUringSqe*)sqe;
        *s = default;
        s->Opcode = IORING_OP_URING_CMD;
        s->Fd = fd;
        s->CmdAddr = cmdBuf;
        s->Len = cmdLen;
    }

    // ─── Availability Detection ─────────────────────────────────────────

    private static readonly Lazy<bool> _isAvailable = new(DetectAvailability);

    /// <summary>
    /// Returns true if io_uring is available on the current platform.
    /// Checks for Linux OS and the presence of liburing.so.
    /// </summary>
    public static bool IsAvailable => _isAvailable.Value;

    private static bool DetectAvailability()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        return NativeLibrary.TryLoad("liburing", out _);
    }
}
