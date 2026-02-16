using System;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// CUDA Runtime API interop layer for NVIDIA GPU acceleration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a minimal P/Invoke surface for CUDA Runtime API. The full CUDA API
    /// contains hundreds of functions; this implementation provides only the essential
    /// subset needed for memory management and device queries.
    /// </para>
    /// <para>
    /// CUDA Runtime versions:
    /// - Windows: cudart64_12.dll (CUDA 12.x)
    /// - Linux: libcudart.so.12
    /// </para>
    /// <para>
    /// For production GPU compute workloads, consider using CuBLAS (matrix ops),
    /// CuDNN (deep learning), or CuFFT (FFT operations) instead of raw CUDA kernels.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 35: CUDA interop (HW-02)")]
    internal static partial class CudaInterop
    {
        // Platform-specific library names
        private const string CudaLibraryWindows = "cudart64_12";
        private const string CudaLibraryLinux = "libcudart.so.12";

        // Determine library name based on platform
        private static readonly string CudaLibrary = OperatingSystem.IsWindows()
            ? CudaLibraryWindows
            : CudaLibraryLinux;

        /// <summary>
        /// Success return code for CUDA operations.
        /// </summary>
        internal const int CUDA_SUCCESS = 0;

        /// <summary>
        /// CUDA error codes (minimal subset).
        /// </summary>
        internal enum cudaError
        {
            cudaSuccess = 0,
            cudaErrorInvalidValue = 1,
            cudaErrorMemoryAllocation = 2,
            cudaErrorInitializationError = 3,
            cudaErrorInvalidDevice = 10,
            cudaErrorNoDevice = 100,
        }

        /// <summary>
        /// Memory copy direction constants.
        /// </summary>
        internal const int cudaMemcpyHostToDevice = 1;
        internal const int cudaMemcpyDeviceToHost = 2;
        internal const int cudaMemcpyDeviceToDevice = 3;

        /// <summary>
        /// Gets the number of CUDA-capable devices.
        /// </summary>
        /// <param name="count">Output: number of devices.</param>
        /// <returns>CUDA error code (0 = success).</returns>
        [LibraryImport(CudaLibraryWindows, EntryPoint = "cudaGetDeviceCount")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetDeviceCount(out int count);

        /// <summary>
        /// Sets the active CUDA device for subsequent operations.
        /// </summary>
        /// <param name="device">Device index (0-based).</param>
        /// <returns>CUDA error code (0 = success).</returns>
        [LibraryImport(CudaLibraryWindows, EntryPoint = "cudaSetDevice")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int SetDevice(int device);

        /// <summary>
        /// Allocates memory on the GPU device.
        /// </summary>
        /// <param name="devPtr">Output: device memory pointer.</param>
        /// <param name="size">Size in bytes to allocate.</param>
        /// <returns>CUDA error code (0 = success).</returns>
        [LibraryImport(CudaLibraryWindows, EntryPoint = "cudaMalloc")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Malloc(out IntPtr devPtr, nuint size);

        /// <summary>
        /// Copies memory between host and device.
        /// </summary>
        /// <param name="dst">Destination pointer.</param>
        /// <param name="src">Source pointer.</param>
        /// <param name="count">Number of bytes to copy.</param>
        /// <param name="kind">Copy direction (cudaMemcpyHostToDevice, cudaMemcpyDeviceToHost, etc.).</param>
        /// <returns>CUDA error code (0 = success).</returns>
        [LibraryImport(CudaLibraryWindows, EntryPoint = "cudaMemcpy")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Memcpy(IntPtr dst, IntPtr src, nuint count, int kind);

        /// <summary>
        /// Frees GPU device memory.
        /// </summary>
        /// <param name="devPtr">Device memory pointer to free.</param>
        /// <returns>CUDA error code (0 = success).</returns>
        [LibraryImport(CudaLibraryWindows, EntryPoint = "cudaFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Free(IntPtr devPtr);

        /// <summary>
        /// Synchronizes the device (waits for all GPU operations to complete).
        /// </summary>
        /// <returns>CUDA error code (0 = success).</returns>
        [LibraryImport(CudaLibraryWindows, EntryPoint = "cudaDeviceSynchronize")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int DeviceSynchronize();

        /// <summary>
        /// Gets device properties (name, memory, compute capability).
        /// </summary>
        /// <param name="prop">Output: device properties structure.</param>
        /// <param name="device">Device index.</param>
        /// <returns>CUDA error code (0 = success).</returns>
        [LibraryImport(CudaLibraryWindows, EntryPoint = "cudaGetDeviceProperties")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetDeviceProperties(IntPtr prop, int device);
    }
}
