using DataWarehouse.SDK.Contracts;
using System;
using System.Runtime.InteropServices;

namespace DataWarehouse.SDK.Hardware.Accelerators;

/// <summary>
/// P/Invoke declarations for Intel QuickAssist Technology (QAT) native library.
/// </summary>
/// <remarks>
/// <para>
/// This class provides a minimal P/Invoke surface for integrating with Intel QAT hardware
/// acceleration. The full QAT API is much larger; this surface includes only the functions
/// required for compression, decompression, encryption, and decryption operations within
/// DataWarehouse.
/// </para>
/// <para>
/// <b>Runtime Dependencies:</b>
/// <list type="bullet">
/// <item><description>Linux: libqat.so (Intel QAT driver and library must be installed)</description></item>
/// <item><description>Windows: qat.dll (Intel QAT driver and library must be installed)</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Hardware Requirements:</b>
/// Intel QAT hardware must be present (PCIe card or integrated QAT in Intel Xeon CPUs).
/// Check availability before calling QAT functions.
/// </para>
/// <para>
/// <b>Thread Safety:</b> QAT library functions are thread-safe. Multiple threads can call
/// QAT functions concurrently on different instances.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 35: QAT native interop (HW-01)")]
internal static partial class QatNativeInterop
{
    /// <summary>
    /// QAT library name (platform-specific).
    /// </summary>
    private const string QatLibrary = "qat";

    /// <summary>
    /// QAT operation completed successfully.
    /// </summary>
    public const int QAT_STATUS_SUCCESS = 0;

    /// <summary>
    /// QAT operation failed.
    /// </summary>
    public const int QAT_STATUS_FAIL = -1;

    /// <summary>
    /// QAT operation should be retried (resource temporarily unavailable).
    /// </summary>
    public const int QAT_STATUS_RETRY = -2;

    /// <summary>
    /// CPA (Cryptographic API) buffer list structure.
    /// Contains an array of flat buffers for scatter-gather operations.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CpaBufferList
    {
        /// <summary>Number of buffers in the list.</summary>
        public uint NumBuffers;

        /// <summary>Pointer to array of CpaFlatBuffer structures.</summary>
        public IntPtr Buffers;

        /// <summary>Private metadata for internal QAT use.</summary>
        public IntPtr PrivateMetaData;
    }

    /// <summary>
    /// CPA flat buffer structure representing a contiguous memory region.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CpaFlatBuffer
    {
        /// <summary>Length of data in bytes.</summary>
        public uint DataLenInBytes;

        /// <summary>Pointer to data buffer.</summary>
        public IntPtr PData;
    }

    /// <summary>
    /// Gets the number of available QAT data compression instances.
    /// </summary>
    /// <param name="numInstances">Output: number of available instances.</param>
    /// <returns>QAT_STATUS_SUCCESS on success, QAT_STATUS_FAIL on failure.</returns>
    [LibraryImport(QatLibrary, EntryPoint = "cpaDcGetNumInstances")]
    internal static partial int GetNumInstances(out ushort numInstances);

    /// <summary>
    /// Gets handles to all QAT data compression instances.
    /// </summary>
    /// <param name="numInstances">Number of instances to retrieve.</param>
    /// <param name="instances">Array to receive instance handles.</param>
    /// <returns>QAT_STATUS_SUCCESS on success, QAT_STATUS_FAIL on failure.</returns>
    [LibraryImport(QatLibrary, EntryPoint = "cpaDcGetInstances")]
    internal static partial int GetInstances(ushort numInstances, IntPtr[] instances);

    /// <summary>
    /// Starts a QAT data compression instance.
    /// Must be called before using the instance for operations.
    /// </summary>
    /// <param name="instance">The instance handle to start.</param>
    /// <returns>QAT_STATUS_SUCCESS on success, QAT_STATUS_FAIL on failure.</returns>
    [LibraryImport(QatLibrary, EntryPoint = "cpaDcStartInstance")]
    internal static partial int StartInstance(IntPtr instance);

    /// <summary>
    /// Stops a QAT data compression instance.
    /// Should be called during cleanup to release resources.
    /// </summary>
    /// <param name="instance">The instance handle to stop.</param>
    /// <returns>QAT_STATUS_SUCCESS on success, QAT_STATUS_FAIL on failure.</returns>
    [LibraryImport(QatLibrary, EntryPoint = "cpaDcStopInstance")]
    internal static partial int StopInstance(IntPtr instance);

    /// <summary>
    /// Compresses data using QAT hardware acceleration.
    /// </summary>
    /// <param name="instance">The QAT instance handle.</param>
    /// <param name="sessionHandle">Session handle (can be IntPtr.Zero for stateless).</param>
    /// <param name="srcBuffer">Source buffer list containing data to compress.</param>
    /// <param name="dstBuffer">Destination buffer list to receive compressed data.</param>
    /// <param name="results">Pointer to results structure (can be IntPtr.Zero).</param>
    /// <param name="callbackTag">Callback context pointer for asynchronous operations.</param>
    /// <returns>QAT_STATUS_SUCCESS on success, QAT_STATUS_FAIL on failure, QAT_STATUS_RETRY if busy.</returns>
    [LibraryImport(QatLibrary, EntryPoint = "cpaDcCompressData")]
    internal static partial int CompressData(
        IntPtr instance,
        IntPtr sessionHandle,
        ref CpaBufferList srcBuffer,
        ref CpaBufferList dstBuffer,
        ref IntPtr results,
        IntPtr callbackTag);

    /// <summary>
    /// Decompresses data using QAT hardware acceleration.
    /// </summary>
    /// <param name="instance">The QAT instance handle.</param>
    /// <param name="sessionHandle">Session handle (can be IntPtr.Zero for stateless).</param>
    /// <param name="srcBuffer">Source buffer list containing compressed data.</param>
    /// <param name="dstBuffer">Destination buffer list to receive decompressed data.</param>
    /// <param name="results">Pointer to results structure (can be IntPtr.Zero).</param>
    /// <param name="callbackTag">Callback context pointer for asynchronous operations.</param>
    /// <returns>QAT_STATUS_SUCCESS on success, QAT_STATUS_FAIL on failure, QAT_STATUS_RETRY if busy.</returns>
    [LibraryImport(QatLibrary, EntryPoint = "cpaDcDecompressData")]
    internal static partial int DecompressData(
        IntPtr instance,
        IntPtr sessionHandle,
        ref CpaBufferList srcBuffer,
        ref CpaBufferList dstBuffer,
        ref IntPtr results,
        IntPtr callbackTag);
}
