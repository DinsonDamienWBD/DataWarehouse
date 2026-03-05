using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO.Unix;

/// <summary>
/// P/Invoke declarations for kqueue, kevent, POSIX AIO, and related macOS/FreeBSD
/// system calls used by <see cref="KqueueBlockDevice"/> for async block I/O.
/// </summary>
/// <remarks>
/// <para>
/// All struct layouts target the macOS (arm64/x86_64) ABI. FreeBSD struct sizes may
/// differ due to field ordering and padding differences.
/// </para>
/// <para>
/// The kqueue API provides a scalable event notification mechanism. Combined with
/// POSIX AIO (aio_read/aio_write), it enables efficient async block I/O with
/// batch completion polling via kevent.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "VOPT-81: P/Invoke for kqueue + POSIX AIO on macOS/FreeBSD")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("freebsd")]
internal static class KqueueNativeMethods
{
    private const string Libc = "libc";

    // --- File descriptor operations ---

    /// <summary>Opens a file descriptor with the specified flags and mode.</summary>
    [DllImport(Libc, EntryPoint = "open", SetLastError = true)]
    internal static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        int mode);

    /// <summary>Closes a file descriptor.</summary>
    [DllImport(Libc, EntryPoint = "close", SetLastError = true)]
    internal static extern int Close(int fd);

    /// <summary>Applies file control commands (e.g., FNocache on macOS).</summary>
    [DllImport(Libc, EntryPoint = "fcntl", SetLastError = true)]
    internal static extern int Fcntl(int fd, int cmd, int arg);

    /// <summary>Truncates a file to the specified length.</summary>
    [DllImport(Libc, EntryPoint = "ftruncate", SetLastError = true)]
    internal static extern int Ftruncate(int fd, long length);

    /// <summary>Flushes file data and metadata to disk.</summary>
    [DllImport(Libc, EntryPoint = "fsync", SetLastError = true)]
    internal static extern int Fsync(int fd);

    // --- kqueue operations ---

    /// <summary>Creates a new kqueue descriptor.</summary>
    [DllImport(Libc, EntryPoint = "kqueue", SetLastError = true)]
    internal static extern int Kqueue();

    /// <summary>
    /// Registers events on a kqueue and/or polls for completed events.
    /// </summary>
    /// <param name="kq">The kqueue descriptor.</param>
    /// <param name="changelist">Pointer to array of events to register (may be null).</param>
    /// <param name="nchanges">Number of entries in changelist.</param>
    /// <param name="eventlist">Pointer to array to receive triggered events (may be null).</param>
    /// <param name="nevents">Max events to return in eventlist.</param>
    /// <param name="timeout">Pointer to timeout (null = block indefinitely).</param>
    /// <returns>Number of events returned, or -1 on error.</returns>
    [DllImport(Libc, EntryPoint = "kevent", SetLastError = true)]
    internal static extern unsafe int KeventCall(
        int kq,
        KqueueEvent* changelist,
        int nchanges,
        KqueueEvent* eventlist,
        int nevents,
        Timespec* timeout);

    // --- POSIX AIO operations ---

    /// <summary>Submits an asynchronous read operation.</summary>
    [DllImport(Libc, EntryPoint = "aio_read", SetLastError = true)]
    internal static extern unsafe int AioRead(Aiocb* cb);

    /// <summary>Submits an asynchronous write operation.</summary>
    [DllImport(Libc, EntryPoint = "aio_write", SetLastError = true)]
    internal static extern unsafe int AioWrite(Aiocb* cb);

    /// <summary>
    /// Returns the error status of an asynchronous I/O operation.
    /// Returns 0 if complete, Einprogress if still pending, or an error code.
    /// </summary>
    [DllImport(Libc, EntryPoint = "aio_error", SetLastError = true)]
    internal static extern unsafe int AioError(Aiocb* cb);

    /// <summary>
    /// Returns the result (bytes transferred) of a completed asynchronous I/O operation.
    /// Must only be called after <see cref="AioError"/> returns 0.
    /// </summary>
    [DllImport(Libc, EntryPoint = "aio_return", SetLastError = true)]
    internal static extern unsafe nint AioReturn(Aiocb* cb);

    /// <summary>Cancels a pending asynchronous I/O operation.</summary>
    [DllImport(Libc, EntryPoint = "aio_cancel", SetLastError = true)]
    internal static extern unsafe int AioCancel(int fd, Aiocb* cb);

    // --- Open flags ---

    /// <summary>Open for reading and writing.</summary>
    internal const int ORdwr = 0x0002;

    /// <summary>Create file if it does not exist.</summary>
    internal const int OCreat = 0x0200;

    /// <summary>Error if OCreat and file already exists.</summary>
    internal const int OExcl = 0x0800;

    // --- fcntl commands ---

    /// <summary>
    /// macOS-specific fcntl command to disable the unified buffer cache for a file descriptor.
    /// Equivalent to ODirect on Linux. When set to 1, the OS page cache is bypassed.
    /// </summary>
    internal const int FNocache = 48;

    /// <summary>Set file status flags.</summary>
    internal const int FSetfl = 4;

    // --- kqueue event filter and flags ---

    /// <summary>Kqueue filter for asynchronous I/O events (aio completion).</summary>
    internal const short EvfiltAio = -3;

    /// <summary>Add event to kqueue.</summary>
    internal const ushort EvAdd = 0x0001;

    /// <summary>Enable event reporting.</summary>
    internal const ushort EvEnable = 0x0004;

    /// <summary>Remove event after first trigger.</summary>
    internal const ushort EvOneshot = 0x0010;

    /// <summary>Indicates an error occurred for this event.</summary>
    internal const ushort EvError = 0x4000;

    // --- AIO signal notification modes ---

    /// <summary>No notification on AIO completion. Use polling instead.</summary>
    internal const int SigevNone = 0;

    /// <summary>
    /// FreeBSD-specific: deliver AIO completion as a kevent. Not available on macOS;
    /// use SigevNone with explicit polling on macOS.
    /// </summary>
    internal const int SigevKevent = 3;

    // --- AIO error codes ---

    /// <summary>AIO operation is still in progress (not yet complete).</summary>
    internal const int Einprogress = 36;

    // --- File permissions ---

    /// <summary>Owner read/write permission (0600).</summary>
    internal const int SIrusrIwusr = 0x180; // 0600 octal

    // --- Structs ---

    /// <summary>
    /// BSD kqueue event structure. Carries one event registration or one triggered event.
    /// Layout matches macOS arm64/x86_64: 32 bytes total (no packing needed, natural alignment).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KqueueEvent
    {
        /// <summary>Event identifier (e.g., pointer to aiocb for EvfiltAio).</summary>
        internal nuint Ident;

        /// <summary>Event filter type (e.g., EvfiltAio).</summary>
        internal short Filter;

        /// <summary>Action flags (e.g., EvAdd | EvEnable | EvOneshot).</summary>
        internal ushort Flags;

        /// <summary>Filter-specific flags.</summary>
        internal uint FilterFlags;

        /// <summary>Filter-specific data.</summary>
        internal nint Data;

        /// <summary>Opaque user data passed through from registration to delivery.</summary>
        internal IntPtr UserData;
    }

    /// <summary>
    /// POSIX timespec structure for kqueue timeout specification.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Timespec
    {
        /// <summary>Seconds component.</summary>
        internal long Seconds;

        /// <summary>Nanoseconds component (0 to 999,999,999).</summary>
        internal long Nanoseconds;
    }

    /// <summary>
    /// POSIX sigevent structure for AIO completion notification configuration.
    /// On macOS, only SigevNone is reliably supported for AIO.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Sigevent
    {
        /// <summary>Notification type (SigevNone or SigevKevent).</summary>
        internal int Notify;

        /// <summary>Signal number (unused when Notify is SigevNone).</summary>
        internal int Signo;

        /// <summary>Signal value / user data (unused when Notify is SigevNone).</summary>
        internal IntPtr Value;

        /// <summary>Notification function pointer (unused in this implementation).</summary>
        internal IntPtr NotifyFunction;

        /// <summary>Notification attributes (unused in this implementation).</summary>
        internal IntPtr NotifyAttributes;
    }

    /// <summary>
    /// POSIX AIO control block. Describes one asynchronous I/O operation.
    /// Layout targets macOS arm64/x86_64 ABI. FreeBSD field order may differ.
    /// </summary>
    /// <remarks>
    /// On macOS, the aiocb struct layout is:
    /// aio_fildes (int), padding, aio_offset (off_t=long), aio_buf (void*),
    /// aio_nbytes (size_t), aio_reqprio (int), aio_sigevent (sigevent), aio_lio_opcode (int).
    /// We include explicit padding to match the native layout.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Aiocb
    {
        /// <summary>File descriptor for the I/O operation.</summary>
        internal int FileDescriptor;

        /// <summary>Padding to align aio_offset to 8-byte boundary on macOS.</summary>
        private int _padding0;

        /// <summary>File offset at which the I/O starts.</summary>
        internal long Offset;

        /// <summary>Pointer to the I/O buffer (read destination or write source).</summary>
        internal IntPtr Buffer;

        /// <summary>Number of bytes to transfer.</summary>
        internal nuint ByteCount;

        /// <summary>Request priority (typically 0).</summary>
        internal int RequestPriority;

        /// <summary>Padding after reqprio before sigevent, platform-dependent.</summary>
        private int _padding1;

        /// <summary>Signal event configuration for completion notification.</summary>
        internal Sigevent SigEvent;

        /// <summary>
        /// Listio opcode (LIO_READ, LIO_WRITE, LIO_NOP). Not used for individual
        /// aio_read/aio_write calls; set to 0.
        /// </summary>
        internal int LioOpcode;

        /// <summary>Padding to match native struct tail alignment.</summary>
        private int _padding2;
    }

    // --- Platform detection ---

    private static readonly Lazy<bool> _isSupported = new(DetectSupport);

    /// <summary>
    /// Returns <c>true</c> if the current platform supports kqueue-based I/O
    /// (macOS or FreeBSD).
    /// </summary>
    internal static bool IsSupported => _isSupported.Value;

    private static bool DetectSupport()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return true;
        }

        // FreeBSD is not a first-class OSPlatform in .NET; detect via OS description.
        if (RuntimeInformation.OSDescription.Contains("FreeBSD", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if running on macOS (for FNocache and macOS-specific struct layouts).
    /// </summary>
    internal static bool IsMacOs => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
}
