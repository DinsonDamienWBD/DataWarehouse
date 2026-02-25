using System;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// ROCm HIP Runtime API interop layer for AMD GPU acceleration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HIP (Heterogeneous-compute Interface for Portability) is AMD's CUDA-compatible
    /// runtime API for ROCm. The API surface mirrors CUDA, allowing code portability
    /// between NVIDIA and AMD GPUs.
    /// </para>
    /// <para>
    /// HIP Runtime versions:
    /// - Windows: amdhip64.dll
    /// - Linux: libamdhip64.so
    /// </para>
    /// <para>
    /// For production GPU compute workloads, consider using rocBLAS (matrix ops),
    /// MIOpen (deep learning), or rocFFT (FFT operations).
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 35: ROCm/HIP interop (HW-02)")]
    internal static partial class RocmInterop
    {
        // Platform-specific library names
        private const string HipLibraryWindows = "amdhip64";
        private const string HipLibraryLinux = "libamdhip64.so";

        // Determine library name based on platform
        private static readonly string HipLibrary = OperatingSystem.IsWindows()
            ? HipLibraryWindows
            : HipLibraryLinux;

        /// <summary>
        /// Success return code for HIP operations.
        /// </summary>
        internal const int hipSuccess = 0;

        /// <summary>
        /// HIP error codes (minimal subset).
        /// </summary>
        internal enum hipError
        {
            hipSuccess = 0,
            hipErrorInvalidValue = 1,
            hipErrorMemoryAllocation = 2,
            hipErrorInitializationError = 3,
            hipErrorInvalidDevice = 10,
            hipErrorNoDevice = 100,
        }

        /// <summary>
        /// Memory copy direction constants.
        /// </summary>
        internal const int hipMemcpyHostToDevice = 1;
        internal const int hipMemcpyDeviceToHost = 2;
        internal const int hipMemcpyDeviceToDevice = 3;

        /// <summary>
        /// Gets the number of HIP-capable devices.
        /// </summary>
        /// <param name="count">Output: number of devices.</param>
        /// <returns>HIP error code (0 = success).</returns>
        [LibraryImport(HipLibraryWindows, EntryPoint = "hipGetDeviceCount")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetDeviceCount(out int count);

        /// <summary>
        /// Sets the active HIP device for subsequent operations.
        /// </summary>
        /// <param name="device">Device index (0-based).</param>
        /// <returns>HIP error code (0 = success).</returns>
        [LibraryImport(HipLibraryWindows, EntryPoint = "hipSetDevice")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int SetDevice(int device);

        /// <summary>
        /// Allocates memory on the GPU device.
        /// </summary>
        /// <param name="devPtr">Output: device memory pointer.</param>
        /// <param name="size">Size in bytes to allocate.</param>
        /// <returns>HIP error code (0 = success).</returns>
        [LibraryImport(HipLibraryWindows, EntryPoint = "hipMalloc")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Malloc(out IntPtr devPtr, nuint size);

        /// <summary>
        /// Copies memory between host and device.
        /// </summary>
        /// <param name="dst">Destination pointer.</param>
        /// <param name="src">Source pointer.</param>
        /// <param name="count">Number of bytes to copy.</param>
        /// <param name="kind">Copy direction (hipMemcpyHostToDevice, hipMemcpyDeviceToHost, etc.).</param>
        /// <returns>HIP error code (0 = success).</returns>
        [LibraryImport(HipLibraryWindows, EntryPoint = "hipMemcpy")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Memcpy(IntPtr dst, IntPtr src, nuint count, int kind);

        /// <summary>
        /// Frees GPU device memory.
        /// </summary>
        /// <param name="devPtr">Device memory pointer to free.</param>
        /// <returns>HIP error code (0 = success).</returns>
        [LibraryImport(HipLibraryWindows, EntryPoint = "hipFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Free(IntPtr devPtr);

        /// <summary>
        /// Synchronizes the device (waits for all GPU operations to complete).
        /// </summary>
        /// <returns>HIP error code (0 = success).</returns>
        [LibraryImport(HipLibraryWindows, EntryPoint = "hipDeviceSynchronize")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int DeviceSynchronize();

        /// <summary>
        /// Gets device properties (name, memory, compute capability).
        /// </summary>
        /// <param name="prop">Output: device properties structure.</param>
        /// <param name="device">Device index.</param>
        /// <returns>HIP error code (0 = success).</returns>
        [LibraryImport(HipLibraryWindows, EntryPoint = "hipGetDeviceProperties")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetDeviceProperties(IntPtr prop, int device);
    }
}
