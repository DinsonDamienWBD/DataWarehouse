using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DataWarehouse.SDK.Contracts;
using Microsoft.Win32.SafeHandles;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO.Windows;

/// <summary>
/// P/Invoke declarations for Windows OVERLAPPED I/O operations.
/// Provides low-level access to kernel32.dll file I/O functions that support
/// I/O completion ports (IOCP) for high-performance asynchronous block I/O.
/// </summary>
/// <remarks>
/// These methods are used by <see cref="WindowsOverlappedBlockDevice"/> to open
/// files with <c>FILE_FLAG_OVERLAPPED</c> and related flags for direct, unbuffered,
/// write-through I/O. On Windows 10+ the managed <see cref="RandomAccess"/> API
/// internally leverages IOCP when the handle is opened with <see cref="System.IO.FileOptions.Asynchronous"/>,
/// so the primary value of these declarations is to expose the exact flag constants
/// and the batch-completion <c>GetQueuedCompletionStatusEx</c> entry point.
/// </remarks>
[SupportedOSPlatform("windows")]
[SdkCompatibility("6.0.0", Notes = "VOPT-80: Windows OVERLAPPED I/O native methods")]
internal static partial class OverlappedNativeMethods
{
    // ── File creation / open flags ──────────────────────────────────────

    /// <summary>
    /// FILE_FLAG_OVERLAPPED: Opens the file for asynchronous (overlapped) I/O.
    /// </summary>
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>
    /// FILE_FLAG_NO_BUFFERING: Opens the file with no intermediate buffering or caching.
    /// All reads and writes must be aligned to the volume sector size.
    /// </summary>
    internal const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    /// <summary>
    /// FILE_FLAG_WRITE_THROUGH: Instructs the system to write through any intermediate cache
    /// and go directly to disk. Must be combined with FILE_FLAG_NO_BUFFERING for full bypass.
    /// </summary>
    internal const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    /// <summary>Generic read access right.</summary>
    internal const uint GENERIC_READ = 0x80000000;

    /// <summary>Generic write access right.</summary>
    internal const uint GENERIC_WRITE = 0x40000000;

    /// <summary>Opens a file only if it exists.</summary>
    internal const uint OPEN_EXISTING = 3;

    /// <summary>Creates a new file; fails if the file already exists.</summary>
    internal const uint CREATE_NEW = 1;

    // ── P/Invoke declarations ───────────────────────────────────────────

    /// <summary>
    /// Creates or opens a file with the specified flags and attributes.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// Creates an I/O completion port or associates a file handle with an existing one.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateIoCompletionPort(
        nint fileHandle,
        nint existingCompletionPort,
        nuint completionKey,
        uint numberOfConcurrentThreads);

    /// <summary>
    /// Dequeues multiple completion packets from the specified I/O completion port.
    /// More efficient than GetQueuedCompletionStatus for batch I/O workloads.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetQueuedCompletionStatusEx(
        nint completionPort,
        [Out] OverlappedEntry[] lpCompletionPortEntries,
        uint ulCount,
        out uint ulNumEntriesRemoved,
        uint dwMilliseconds,
        [MarshalAs(UnmanagedType.Bool)] bool fAlertable);

    /// <summary>
    /// Flushes the buffers of a specified file and causes all buffered data to be written to the file.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(SafeFileHandle hFile);

    /// <summary>
    /// Closes an open object handle.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    // ── Structs ─────────────────────────────────────────────────────────

    /// <summary>
    /// Contains information about a completed I/O operation, as returned by
    /// <see cref="GetQueuedCompletionStatusEx"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OverlappedEntry
    {
        /// <summary>The completion key associated with the file handle.</summary>
        internal nuint CompletionKey;

        /// <summary>Pointer to the OVERLAPPED structure for the completed I/O operation.</summary>
        internal nint Overlapped;

        /// <summary>Application-defined value (typically unused with IOCP).</summary>
        internal nuint Internal;

        /// <summary>The number of bytes transferred during the I/O operation.</summary>
        internal uint NumberOfBytesTransferred;
    }

    // ── Constants for completion port ───────────────────────────────────

    /// <summary>INFINITE timeout value for completion port waits.</summary>
    internal const uint INFINITE = 0xFFFFFFFF;

    /// <summary>Invalid handle sentinel value.</summary>
    internal static readonly nint INVALID_HANDLE_VALUE = new(-1);
}
