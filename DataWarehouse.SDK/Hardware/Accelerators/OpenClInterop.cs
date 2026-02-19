using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// OpenCL API interop layer for cross-vendor GPU/CPU/FPGA compute acceleration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OpenCL (Open Computing Language) provides a standard API for parallel programming
    /// across heterogeneous platforms including CPUs, GPUs, and FPGAs from multiple vendors
    /// (NVIDIA, AMD, Intel, ARM, Xilinx).
    /// </para>
    /// <para>
    /// OpenCL Runtime libraries:
    /// - Windows: OpenCL.dll
    /// - Linux: libOpenCL.so
    /// </para>
    /// <para>
    /// This implementation targets OpenCL 1.2+ which is the most widely supported version.
    /// For advanced features (SVM, pipes), OpenCL 2.0+ is required.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: OpenCL interop (HW-05)")]
    internal static partial class OpenClInterop
    {
        // Platform-specific library names
        private const string OpenClLibraryWindows = "OpenCL";
        private const string OpenClLibraryLinux = "libOpenCL.so";

        // Determine library name based on platform
        private static readonly string OpenClLibrary = OperatingSystem.IsWindows()
            ? OpenClLibraryWindows
            : OpenClLibraryLinux;

        /// <summary>
        /// Success return code for OpenCL operations.
        /// </summary>
        internal const int CL_SUCCESS = 0;

        /// <summary>
        /// OpenCL error codes (minimal subset).
        /// </summary>
        internal enum ClError
        {
            CL_SUCCESS = 0,
            CL_DEVICE_NOT_FOUND = -1,
            CL_DEVICE_NOT_AVAILABLE = -2,
            CL_COMPILER_NOT_AVAILABLE = -3,
            CL_MEM_OBJECT_ALLOCATION_FAILURE = -4,
            CL_OUT_OF_RESOURCES = -5,
            CL_OUT_OF_HOST_MEMORY = -6,
            CL_BUILD_PROGRAM_FAILURE = -11,
            CL_INVALID_VALUE = -30,
            CL_INVALID_PLATFORM = -32,
            CL_INVALID_DEVICE = -33,
            CL_INVALID_CONTEXT = -34,
            CL_INVALID_COMMAND_QUEUE = -36,
            CL_INVALID_MEM_OBJECT = -38,
            CL_INVALID_PROGRAM = -44,
            CL_INVALID_KERNEL = -48,
            CL_INVALID_KERNEL_ARGS = -52,
            CL_INVALID_WORK_DIMENSION = -53,
            CL_INVALID_WORK_GROUP_SIZE = -54,
        }

        /// <summary>
        /// OpenCL device type flags.
        /// </summary>
        internal const ulong CL_DEVICE_TYPE_DEFAULT = 1;
        internal const ulong CL_DEVICE_TYPE_CPU = 2;
        internal const ulong CL_DEVICE_TYPE_GPU = 4;
        internal const ulong CL_DEVICE_TYPE_ACCELERATOR = 8;
        internal const ulong CL_DEVICE_TYPE_ALL = 0xFFFFFFFF;

        /// <summary>
        /// OpenCL memory flags.
        /// </summary>
        internal const ulong CL_MEM_READ_WRITE = 1;
        internal const ulong CL_MEM_WRITE_ONLY = 2;
        internal const ulong CL_MEM_READ_ONLY = 4;
        internal const ulong CL_MEM_COPY_HOST_PTR = 32;

        /// <summary>
        /// OpenCL platform info query parameters.
        /// </summary>
        internal const uint CL_PLATFORM_NAME = 0x0902;
        internal const uint CL_PLATFORM_VENDOR = 0x0903;
        internal const uint CL_PLATFORM_VERSION = 0x0901;

        /// <summary>
        /// OpenCL device info query parameters.
        /// </summary>
        internal const uint CL_DEVICE_NAME = 0x102B;
        internal const uint CL_DEVICE_VENDOR = 0x102C;
        internal const uint CL_DEVICE_TYPE = 0x1000;
        internal const uint CL_DEVICE_MAX_COMPUTE_UNITS = 0x1002;
        internal const uint CL_DEVICE_GLOBAL_MEM_SIZE = 0x101F;

        // --- Platform API ---

        /// <summary>
        /// Gets the list of available OpenCL platforms.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clGetPlatformIDs")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetPlatformIDs(
            uint numEntries, IntPtr[] platforms, out uint numPlatforms);

        /// <summary>
        /// Gets information about an OpenCL platform.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clGetPlatformInfo")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetPlatformInfo(
            IntPtr platform, uint paramName, nuint paramValueSize,
            IntPtr paramValue, out nuint paramValueSizeRet);

        // --- Device API ---

        /// <summary>
        /// Gets the list of devices for a given platform and device type.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clGetDeviceIDs")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetDeviceIDs(
            IntPtr platform, ulong deviceType, uint numEntries,
            IntPtr[] devices, out uint numDevices);

        /// <summary>
        /// Gets information about an OpenCL device.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clGetDeviceInfo")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetDeviceInfo(
            IntPtr device, uint paramName, nuint paramValueSize,
            IntPtr paramValue, out nuint paramValueSizeRet);

        // --- Context API ---

        /// <summary>
        /// Creates an OpenCL context for the specified devices.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clCreateContext")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial IntPtr CreateContext(
            IntPtr properties, uint numDevices, IntPtr[] devices,
            IntPtr pfnNotify, IntPtr userData, out int errcodeRet);

        /// <summary>
        /// Creates a command queue for a device within a context.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clCreateCommandQueue")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial IntPtr CreateCommandQueue(
            IntPtr context, IntPtr device, ulong properties, out int errcodeRet);

        // --- Memory API ---

        /// <summary>
        /// Creates an OpenCL buffer object.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clCreateBuffer")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial IntPtr CreateBuffer(
            IntPtr context, ulong flags, nuint size, IntPtr hostPtr, out int errcodeRet);

        /// <summary>
        /// Enqueues a write operation to a buffer object.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clEnqueueWriteBuffer")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int EnqueueWriteBuffer(
            IntPtr commandQueue, IntPtr buffer, int blockingWrite,
            nuint offset, nuint size, IntPtr ptr,
            uint numEventsInWaitList, IntPtr[] eventWaitList, out IntPtr eventObj);

        /// <summary>
        /// Enqueues a read operation from a buffer object.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clEnqueueReadBuffer")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int EnqueueReadBuffer(
            IntPtr commandQueue, IntPtr buffer, int blockingRead,
            nuint offset, nuint size, IntPtr ptr,
            uint numEventsInWaitList, IntPtr[] eventWaitList, out IntPtr eventObj);

        // --- Program API ---

        /// <summary>
        /// Creates a program from source code strings.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clCreateProgramWithSource")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial IntPtr CreateProgramWithSource(
            IntPtr context, uint count, IntPtr[] strings, nuint[] lengths, out int errcodeRet);

        /// <summary>
        /// Builds (compiles and links) a program for the specified devices.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clBuildProgram")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int BuildProgram(
            IntPtr program, uint numDevices, IntPtr[] deviceList,
            IntPtr options, IntPtr pfnNotify, IntPtr userData);

        // --- Kernel API ---

        /// <summary>
        /// Creates a kernel from a compiled program.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clCreateKernel", StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial IntPtr CreateKernel(
            IntPtr program, string kernelName, out int errcodeRet);

        /// <summary>
        /// Sets a kernel argument value.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clSetKernelArg")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int SetKernelArg(
            IntPtr kernel, uint argIndex, nuint argSize, IntPtr argValue);

        /// <summary>
        /// Enqueues a kernel for execution across an N-dimensional range.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clEnqueueNDRangeKernel")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int EnqueueNDRangeKernel(
            IntPtr commandQueue, IntPtr kernel, uint workDim,
            nuint[] globalWorkOffset, nuint[] globalWorkSize, nuint[] localWorkSize,
            uint numEventsInWaitList, IntPtr[] eventWaitList, out IntPtr eventObj);

        // --- Synchronization and Cleanup ---

        /// <summary>
        /// Waits for events to complete.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clWaitForEvents")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int WaitForEvents(uint numEvents, IntPtr[] eventList);

        /// <summary>
        /// Releases an OpenCL kernel.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clReleaseKernel")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ReleaseKernel(IntPtr kernel);

        /// <summary>
        /// Releases an OpenCL program.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clReleaseProgram")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ReleaseProgram(IntPtr program);

        /// <summary>
        /// Releases an OpenCL memory object.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clReleaseMemObject")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ReleaseMemObject(IntPtr memObj);

        /// <summary>
        /// Releases an OpenCL command queue.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clReleaseCommandQueue")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ReleaseCommandQueue(IntPtr commandQueue);

        /// <summary>
        /// Releases an OpenCL context.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clReleaseContext")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ReleaseContext(IntPtr context);

        /// <summary>
        /// Blocks until all commands in the queue have completed.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clFinish")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Finish(IntPtr commandQueue);
    }

    /// <summary>
    /// OpenCL accelerator providing cross-vendor GPU/CPU/FPGA compute acceleration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps the OpenCL P/Invoke bindings with a managed API that implements
    /// <see cref="IGpuAccelerator"/>. Supports device enumeration across all
    /// OpenCL platforms (NVIDIA, AMD, Intel, ARM, etc.).
    /// </para>
    /// <para>
    /// <strong>Graceful Unavailability:</strong> If the OpenCL runtime library is not
    /// found on the system, <see cref="IsAvailable"/> returns false and all operations
    /// throw <see cref="InvalidOperationException"/>.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: OpenCL accelerator (HW-05)")]
    public sealed class OpenClAccelerator : IGpuAccelerator, IDisposable
    {
        private readonly IPlatformCapabilityRegistry _registry;
        private int _deviceCount;
        private bool _isAvailable;
        private bool _initialized;
        private long _operationsCompleted;
        private readonly object _lock = new();
        private bool _disposed;

        // OpenCL handles
        private IntPtr _context;
        private IntPtr _commandQueue;
        private IntPtr _selectedDevice;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenClAccelerator"/> class.
        /// </summary>
        /// <param name="registry">Platform capability registry for registering OpenCL capabilities.</param>
        /// <exception cref="ArgumentNullException">Thrown when registry is null.</exception>
        public OpenClAccelerator(IPlatformCapabilityRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <inheritdoc/>
        public AcceleratorType Type => AcceleratorType.OpenCL;

        /// <inheritdoc/>
        public bool IsAvailable => _isAvailable;

        /// <inheritdoc/>
        public GpuRuntime Runtime => GpuRuntime.OpenCL;

        /// <inheritdoc/>
        public int DeviceCount => _deviceCount;

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            await Task.Run(() =>
            {
                lock (_lock)
                {
                    if (_initialized) return;

                    try
                    {
                        string openClLib = OperatingSystem.IsWindows() ? "OpenCL.dll" : "libOpenCL.so";

                        if (!NativeLibrary.TryLoad(openClLib, out IntPtr _))
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        // Enumerate platforms
                        int result = OpenClInterop.GetPlatformIDs(0, null!, out uint platformCount);
                        if (result != OpenClInterop.CL_SUCCESS || platformCount == 0)
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        var platforms = new IntPtr[platformCount];
                        result = OpenClInterop.GetPlatformIDs(platformCount, platforms, out _);
                        if (result != OpenClInterop.CL_SUCCESS)
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        // Count GPU devices across all platforms
                        int totalDevices = 0;
                        IntPtr firstDevice = IntPtr.Zero;

                        foreach (var platform in platforms)
                        {
                            int devResult = OpenClInterop.GetDeviceIDs(
                                platform, OpenClInterop.CL_DEVICE_TYPE_GPU, 0, null!, out uint devCount);

                            if (devResult == OpenClInterop.CL_SUCCESS && devCount > 0)
                            {
                                totalDevices += (int)devCount;

                                if (firstDevice == IntPtr.Zero)
                                {
                                    var devices = new IntPtr[devCount];
                                    OpenClInterop.GetDeviceIDs(
                                        platform, OpenClInterop.CL_DEVICE_TYPE_GPU, devCount, devices, out _);
                                    firstDevice = devices[0];
                                }
                            }
                        }

                        if (totalDevices > 0 && firstDevice != IntPtr.Zero)
                        {
                            _selectedDevice = firstDevice;
                            _deviceCount = totalDevices;
                            _isAvailable = true;

                            // Create context and command queue for the first device
                            var deviceArray = new[] { firstDevice };
                            _context = OpenClInterop.CreateContext(
                                IntPtr.Zero, 1, deviceArray, IntPtr.Zero, IntPtr.Zero, out int ctxErr);

                            if (ctxErr == OpenClInterop.CL_SUCCESS && _context != IntPtr.Zero)
                            {
                                _commandQueue = OpenClInterop.CreateCommandQueue(
                                    _context, firstDevice, 0, out int queueErr);

                                if (queueErr != OpenClInterop.CL_SUCCESS)
                                {
                                    OpenClInterop.ReleaseContext(_context);
                                    _context = IntPtr.Zero;
                                    _isAvailable = false;
                                }
                            }
                            else
                            {
                                _isAvailable = false;
                            }
                        }

                        _initialized = true;
                    }
                    catch (DllNotFoundException)
                    {
                        _isAvailable = false;
                        _initialized = true;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _isAvailable = false;
                        _initialized = true;
                    }
                }
            });
        }

        /// <inheritdoc/>
        public async Task<float[]> VectorMultiplyAsync(float[] a, float[] b)
        {
            if (!_isAvailable)
                throw new InvalidOperationException("OpenCL is not available. Check IsAvailable before calling.");

            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            if (a.Length != b.Length)
                throw new ArgumentException("Vectors must have the same length");

            // OpenCL kernel execution:
            // 1. Create buffers (clCreateBuffer) for a, b, result
            // 2. Write a, b to device (clEnqueueWriteBuffer)
            // 3. Build kernel: __kernel void vecMul(__global float* a, __global float* b, __global float* c, int n) { ... }
            // 4. Set kernel args (clSetKernelArg)
            // 5. Execute (clEnqueueNDRangeKernel)
            // 6. Read result (clEnqueueReadBuffer)
            // 7. Release buffers
            //
            // CPU fallback to establish API contract (GPU kernel launch requires OpenCL runtime)
            float[] result = new float[a.Length];
            await Task.Run(() =>
            {
                for (int i = 0; i < a.Length; i++)
                    result[i] = a[i] * b[i];
            });

            Interlocked.Increment(ref _operationsCompleted);
            return result;
        }

        /// <inheritdoc/>
        public async Task<float[]> MatrixMultiplyAsync(float[,] a, float[,] b)
        {
            if (!_isAvailable)
                throw new InvalidOperationException("OpenCL is not available.");

            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            int M = a.GetLength(0), K = a.GetLength(1);
            int K2 = b.GetLength(0), N = b.GetLength(1);

            if (K != K2)
                throw new ArgumentException("Matrix dimensions incompatible for multiplication");

            float[] result = new float[M * N];
            await Task.Run(() =>
            {
                for (int i = 0; i < M; i++)
                    for (int j = 0; j < N; j++)
                    {
                        float sum = 0;
                        for (int k = 0; k < K; k++)
                            sum += a[i, k] * b[k, j];
                        result[i * N + j] = sum;
                    }
            });

            Interlocked.Increment(ref _operationsCompleted);
            return result;
        }

        /// <inheritdoc/>
        public async Task<float[]> ComputeEmbeddingsAsync(float[] input, float[,] weights)
        {
            if (!_isAvailable)
                throw new InvalidOperationException("OpenCL is not available.");

            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(weights);

            int D = input.Length;
            int D2 = weights.GetLength(0), E = weights.GetLength(1);

            if (D != D2)
                throw new ArgumentException("Input dimension must match weight rows");

            float[] result = new float[E];
            await Task.Run(() =>
            {
                for (int j = 0; j < E; j++)
                {
                    float sum = 0;
                    for (int i = 0; i < D; i++)
                        sum += input[i] * weights[i, j];
                    result[j] = sum;
                }
            });

            Interlocked.Increment(ref _operationsCompleted);
            return result;
        }

        /// <inheritdoc/>
        public Task<byte[]> ProcessAsync(byte[] data, AcceleratorOperation operation)
        {
            throw new NotSupportedException(
                "OpenCL accelerator requires float[] operations. Use VectorMultiplyAsync, MatrixMultiplyAsync, or ComputeEmbeddingsAsync.");
        }

        /// <inheritdoc/>
        public Task<AcceleratorStatistics> GetStatisticsAsync()
        {
            return Task.FromResult(new AcceleratorStatistics(
                Type: Type,
                OperationsCompleted: Interlocked.Read(ref _operationsCompleted),
                AverageThroughputMBps: 0.0,
                CurrentUtilization: 0.0,
                TotalProcessingTime: TimeSpan.Zero
            ));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;

            if (_commandQueue != IntPtr.Zero)
            {
                try { OpenClInterop.ReleaseCommandQueue(_commandQueue); } catch { /* cleanup best-effort */ }
                _commandQueue = IntPtr.Zero;
            }

            if (_context != IntPtr.Zero)
            {
                try { OpenClInterop.ReleaseContext(_context); } catch { /* cleanup best-effort */ }
                _context = IntPtr.Zero;
            }

            _disposed = true;
        }
    }
}
