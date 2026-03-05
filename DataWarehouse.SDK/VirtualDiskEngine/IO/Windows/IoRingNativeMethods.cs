using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DataWarehouse.SDK.Contracts;
using Microsoft.Win32.SafeHandles;

namespace DataWarehouse.SDK.VirtualDiskEngine.IO.Windows;

/// <summary>
/// P/Invoke declarations for the Windows 11 IoRing API (Build 22000+).
/// Provides submission-queue / completion-queue (SQE/CQE) based I/O similar
/// to Linux io_uring, enabling batched kernel I/O with minimal syscall overhead.
/// </summary>
/// <remarks>
/// <para>
/// The IoRing API is available starting with Windows 11 (Build 22000). The write
/// API (<c>BuildIoRingWriteFile</c>) was added in later Windows 11 builds and may
/// not be available on all Windows 11 installations. Use <see cref="IsWriteSupported"/>
/// to check write availability separately from read support.
/// </para>
/// <para>
/// All functions are accessed via <c>kernel32.dll</c>. If the entry points are not
/// found (e.g., on Windows 10), <see cref="IsSupported"/> returns <c>false</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SdkCompatibility("6.0.0", Notes = "VOPT-80: Windows 11 IoRing native methods")]
internal static class IoRingNativeMethods
{
    // ── IoRing API version and flags ────────────────────────────────────

    /// <summary>IoRing API version 3 (Windows 11 22H2+).</summary>
    internal const int IoringVersion3 = 3;

    /// <summary>No special flags for IoRing creation.</summary>
    internal const uint IoringCreateFlagsNone = 0;

    /// <summary>SOk HRESULT indicating success.</summary>
    internal const int SOk = 0;

    // ── Structs ─────────────────────────────────────────────────────────

    /// <summary>
    /// Flags for <see cref="CreateIoRing"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoringCreateFlags
    {
        internal uint Required;
        internal uint Advisory;
    }

    /// <summary>
    /// Describes a registered buffer for zero-copy I/O.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoringBufferInfo
    {
        internal nint Address;
        internal uint Length;
    }

    /// <summary>
    /// Completion queue entry returned after an I/O operation completes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoringCqe
    {
        internal uint UserData;
        internal int ResultCode;
        internal ulong Information;
    }

    /// <summary>
    /// Handle reference for registered file handles in IoRing operations.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoringHandleRef
    {
        internal uint Kind; // 0 = raw handle, 1 = registered index
        internal nint HandleOrIndex;

        /// <summary>Creates a handle reference from a raw OS handle value.</summary>
        internal static IoringHandleRef FromHandle(nint handle) => new()
        {
            Kind = 0,
            HandleOrIndex = handle
        };

        /// <summary>Creates a handle reference from a registered handle index.</summary>
        internal static IoringHandleRef FromIndex(uint index) => new()
        {
            Kind = 1,
            HandleOrIndex = (nint)index
        };
    }

    /// <summary>
    /// Buffer reference for registered buffers in IoRing operations.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoringBufferRef
    {
        internal uint Kind; // 0 = raw address, 1 = registered index
        internal nint AddressOrIndex;

        /// <summary>Creates a buffer reference from a raw memory address.</summary>
        internal static IoringBufferRef FromAddress(nint address) => new()
        {
            Kind = 0,
            AddressOrIndex = address
        };

        /// <summary>Creates a buffer reference from a registered buffer index.</summary>
        internal static IoringBufferRef FromIndex(uint index) => new()
        {
            Kind = 1,
            AddressOrIndex = (nint)index
        };
    }

    // ── P/Invoke declarations ───────────────────────────────────────────

    /// <summary>
    /// Creates an I/O ring instance.
    /// </summary>
    /// <param name="ioringVersion">The requested IoRing version (use <see cref="IoringVersion3"/>).</param>
    /// <param name="flags">Creation flags.</param>
    /// <param name="submissionQueueSize">Number of submission queue entries.</param>
    /// <param name="completionQueueSize">Number of completion queue entries.</param>
    /// <param name="handle">Receives the IoRing handle on success.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [DllImport("kernel32.dll", EntryPoint = "CreateIoRing", SetLastError = true)]
    internal static extern int CreateIoRing(
        int ioringVersion,
        IoringCreateFlags flags,
        uint submissionQueueSize,
        uint completionQueueSize,
        out nint handle);

    /// <summary>
    /// Closes an I/O ring handle and releases associated resources.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "CloseIoRing", SetLastError = true)]
    internal static extern int CloseIoRing(nint ioringHandle);

    /// <summary>
    /// Enqueues a read operation into the submission queue.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "BuildIoRingReadFile", SetLastError = true)]
    internal static extern int BuildIoRingReadFile(
        nint ioringHandle,
        IoringHandleRef fileRef,
        IoringBufferRef dataRef,
        uint numberOfBytesToRead,
        ulong fileOffset,
        uint userData,
        int sqeFlags);

    /// <summary>
    /// Enqueues a write operation into the submission queue.
    /// Available on later Windows 11 builds only.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "BuildIoRingWriteFile", SetLastError = true)]
    internal static extern int BuildIoRingWriteFile(
        nint ioringHandle,
        IoringHandleRef fileRef,
        IoringBufferRef dataRef,
        uint numberOfBytesToWrite,
        ulong fileOffset,
        uint userData,
        int sqeFlags);

    /// <summary>
    /// Submits queued SQEs and optionally waits for completions.
    /// </summary>
    /// <param name="ioringHandle">The IoRing handle.</param>
    /// <param name="waitOperations">Number of completions to wait for (0 for fire-and-forget).</param>
    /// <param name="milliseconds">Timeout in milliseconds (0xFFFFFFFF for infinite).</param>
    /// <param name="submittedEntries">Receives the number of SQEs submitted.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [DllImport("kernel32.dll", EntryPoint = "SubmitIoRing", SetLastError = true)]
    internal static extern int SubmitIoRing(
        nint ioringHandle,
        uint waitOperations,
        uint milliseconds,
        out uint submittedEntries);

    /// <summary>
    /// Registers file handles for use with registered-handle I/O operations.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "BuildIoRingRegisterFileHandles", SetLastError = true)]
    internal static extern int BuildIoRingRegisterFileHandles(
        nint ioringHandle,
        uint count,
        nint[] handles,
        uint userData);

    /// <summary>
    /// Registers buffers for zero-copy I/O operations.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "BuildIoRingRegisterBuffers", SetLastError = true)]
    internal static extern int BuildIoRingRegisterBuffers(
        nint ioringHandle,
        uint count,
        IoringBufferInfo[] buffers,
        uint userData);

    /// <summary>
    /// Pops a completed CQE from the completion queue.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "PopIoRingCompletion", SetLastError = true)]
    internal static extern int PopIoRingCompletion(
        nint ioringHandle,
        out IoringCqe cqe);

    // ── Availability detection ──────────────────────────────────────────

    private static readonly Lazy<bool> SIsSupported = new(DetectSupport, LazyThreadSafetyMode.PublicationOnly);
    private static readonly Lazy<bool> SIsWriteSupported = new(DetectWriteSupport, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>
    /// Gets whether the IoRing API is available on the current system.
    /// Returns <c>false</c> on Windows 10 or non-Windows platforms.
    /// The result is cached after the first call.
    /// </summary>
    internal static bool IsSupported => SIsSupported.Value;

    /// <summary>
    /// Gets whether the IoRing write API (<c>BuildIoRingWriteFile</c>) is available.
    /// Returns <c>false</c> on early Windows 11 builds that only support reads.
    /// </summary>
    internal static bool IsWriteSupported => SIsWriteSupported.Value;

    private static bool DetectSupport()
    {
        try
        {
            var flags = new IoringCreateFlags { Required = IoringCreateFlagsNone, Advisory = IoringCreateFlagsNone };
            int hr = CreateIoRing(IoringVersion3, flags, 2, 2, out nint handle);

            if (hr == SOk && handle != nint.Zero)
            {
                CloseIoRing(handle);
                return true;
            }

            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool DetectWriteSupport()
    {
        if (!IsSupported) return false;

        try
        {
            // Attempt to resolve the BuildIoRingWriteFile entry point.
            // If it throws EntryPointNotFoundException, writes are not available.
            var flags = new IoringCreateFlags { Required = IoringCreateFlagsNone, Advisory = IoringCreateFlagsNone };
            int hr = CreateIoRing(IoringVersion3, flags, 2, 2, out nint handle);
            if (hr != SOk || handle == nint.Zero) return false;

            try
            {
                // Try calling with invalid parameters just to verify the entry point exists.
                // The call will fail with an error, but won't throw EntryPointNotFoundException.
                var dummyHandle = IoringHandleRef.FromHandle(nint.Zero);
                var dummyBuffer = IoringBufferRef.FromAddress(nint.Zero);
                BuildIoRingWriteFile(handle, dummyHandle, dummyBuffer, 0, 0, 0, 0);
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                CloseIoRing(handle);
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
