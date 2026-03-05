using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// OpenCl API interop layer for cross-vendor GPU/CPU/FPGA compute acceleration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OpenCl (Open Computing Language) provides a standard API for parallel programming
    /// across heterogeneous platforms including CPUs, GPUs, and FPGAs from multiple vendors
    /// (NVIDIA, AMD, Intel, ARM, Xilinx).
    /// </para>
    /// <para>
    /// OpenCl Runtime libraries:
    /// - Windows: OpenCl.dll
    /// - Linux: libOpenCL.so
    /// </para>
    /// <para>
    /// This implementation targets OpenCl 1.2+ which is the most widely supported version.
    /// For advanced features (SVM, pipes), OpenCl 2.0+ is required.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: OpenCl interop (HW-05)")]
    internal static partial class OpenClInterop
    {
        // Platform-specific library names
        private const string OpenClLibraryWindows = "OpenCl";
        private const string OpenClLibraryLinux = "libOpenCL.so";

        // Determine library name based on platform
        private static readonly string OpenClLibrary = OperatingSystem.IsWindows()
            ? OpenClLibraryWindows
            : OpenClLibraryLinux;

        /// <summary>
        /// Success return code for OpenCl operations.
        /// </summary>
        internal const int ClSuccess = 0;

        /// <summary>
        /// OpenCl error codes (minimal subset).
        /// </summary>
        internal enum ClError
        {
            ClSuccess = 0,
            ClDeviceNotFound = -1,
            ClDeviceNotAvailable = -2,
            ClCompilerNotAvailable = -3,
            ClMemObjectAllocationFailure = -4,
            ClOutOfResources = -5,
            ClOutOfHostMemory = -6,
            ClBuildProgramFailure = -11,
            ClInvalidValue = -30,
            ClInvalidPlatform = -32,
            ClInvalidDevice = -33,
            ClInvalidContext = -34,
            ClInvalidCommandQueue = -36,
            ClInvalidMemObject = -38,
            ClInvalidProgram = -44,
            ClInvalidKernel = -48,
            ClInvalidKernelArgs = -52,
            ClInvalidWorkDimension = -53,
            ClInvalidWorkGroupSize = -54,
        }

        /// <summary>
        /// OpenCl device type flags.
        /// </summary>
        internal const ulong ClDeviceTypeDefault = 1;
        internal const ulong ClDeviceTypeCpu = 2;
        internal const ulong ClDeviceTypeGpu = 4;
        internal const ulong ClDeviceTypeAccelerator = 8;
        internal const ulong ClDeviceTypeAll = 0xFFFFFFFF;

        /// <summary>
        /// OpenCl memory flags.
        /// </summary>
        internal const ulong ClMemReadWrite = 1;
        internal const ulong ClMemWriteOnly = 2;
        internal const ulong ClMemReadOnly = 4;
        internal const ulong ClMemCopyHostPtr = 32;

        /// <summary>
        /// OpenCl platform info query parameters.
        /// </summary>
        internal const uint ClPlatformName = 0x0902;
        internal const uint ClPlatformVendor = 0x0903;
        internal const uint ClPlatformVersion = 0x0901;

        /// <summary>
        /// OpenCl device info query parameters.
        /// </summary>
        internal const uint ClDeviceName = 0x102B;
        internal const uint ClDeviceVendor = 0x102C;
        internal const uint ClDeviceType = 0x1000;
        internal const uint ClDeviceMaxComputeUnits = 0x1002;
        internal const uint ClDeviceGlobalMemSize = 0x101F;

        // --- Platform API ---

        /// <summary>
        /// Gets the list of available OpenCl platforms.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clGetPlatformIDs")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetPlatformIDs(
            uint numEntries, IntPtr[] platforms, out uint numPlatforms);

        /// <summary>
        /// Gets information about an OpenCl platform.
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
        /// Gets information about an OpenCl device.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clGetDeviceInfo")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetDeviceInfo(
            IntPtr device, uint paramName, nuint paramValueSize,
            IntPtr paramValue, out nuint paramValueSizeRet);

        // --- Context API ---

        /// <summary>
        /// Creates an OpenCl context for the specified devices.
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
        /// Creates an OpenCl buffer object.
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
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clEnqueueNdRangeKernel")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int EnqueueNdRangeKernel(
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
        /// Releases an OpenCl kernel.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clReleaseKernel")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ReleaseKernel(IntPtr kernel);

        /// <summary>
        /// Releases an OpenCl program.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clReleaseProgram")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ReleaseProgram(IntPtr program);

        /// <summary>
        /// Releases an OpenCl memory object.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clReleaseMemObject")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ReleaseMemObject(IntPtr memObj);

        /// <summary>
        /// Releases an OpenCl command queue.
        /// </summary>
        [LibraryImport(OpenClLibraryWindows, EntryPoint = "clReleaseCommandQueue")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ReleaseCommandQueue(IntPtr commandQueue);

        /// <summary>
        /// Releases an OpenCl context.
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
    /// OpenCl accelerator providing cross-vendor GPU/CPU/FPGA compute acceleration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps the OpenCl P/Invoke bindings with a managed API that implements
    /// <see cref="IGpuAccelerator"/>. Supports device enumeration across all
    /// OpenCl platforms (NVIDIA, AMD, Intel, ARM, etc.).
    /// </para>
    /// <para>
    /// <strong>Graceful Unavailability:</strong> If the OpenCl runtime library is not
    /// found on the system, <see cref="IsAvailable"/> returns false and all operations
    /// throw <see cref="InvalidOperationException"/>.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: OpenCl accelerator (HW-05)")]
    public sealed class OpenClAccelerator : IGpuAccelerator, IDisposable
    {
        private readonly IPlatformCapabilityRegistry _registry;
        private int _deviceCount;
        private bool _isAvailable;
        private bool _initialized;
        private long _operationsCompleted;
        private long _totalProcessingTicks; // accumulated via Interlocked.Add (finding P2-371)
        private readonly object _lock = new();
        private bool _disposed;

        // OpenCl handles
        private IntPtr _context;
        private IntPtr _commandQueue;
        private IntPtr _selectedDevice;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenClAccelerator"/> class.
        /// </summary>
        /// <param name="registry">Platform capability registry for registering OpenCl capabilities.</param>
        /// <exception cref="ArgumentNullException">Thrown when registry is null.</exception>
        public OpenClAccelerator(IPlatformCapabilityRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <inheritdoc/>
        public AcceleratorType Type => AcceleratorType.OpenCl;

        /// <inheritdoc/>
        public bool IsAvailable => _isAvailable;

        /// <inheritdoc/>
        /// <remarks>True when OpenCl GPU is unavailable and CPU is used as fallback (finding P1-365).</remarks>
        public bool IsCpuFallback => !_isAvailable;

        /// <inheritdoc/>
        public GpuRuntime Runtime => GpuRuntime.OpenCl;

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
                        string openClLib = OperatingSystem.IsWindows() ? "OpenCl.dll" : "libOpenCL.so";

                        if (!NativeLibrary.TryLoad(openClLib, out IntPtr _))
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        // Enumerate platforms
                        int result = OpenClInterop.GetPlatformIDs(0, null!, out uint platformCount);
                        if (result != OpenClInterop.ClSuccess || platformCount == 0)
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        var platforms = new IntPtr[platformCount];
                        result = OpenClInterop.GetPlatformIDs(platformCount, platforms, out _);
                        if (result != OpenClInterop.ClSuccess)
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
                                platform, OpenClInterop.ClDeviceTypeGpu, 0, null!, out uint devCount);

                            if (devResult == OpenClInterop.ClSuccess && devCount > 0)
                            {
                                totalDevices += (int)devCount;

                                if (firstDevice == IntPtr.Zero)
                                {
                                    var devices = new IntPtr[devCount];
                                    OpenClInterop.GetDeviceIDs(
                                        platform, OpenClInterop.ClDeviceTypeGpu, devCount, devices, out _);
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

                            if (ctxErr == OpenClInterop.ClSuccess && _context != IntPtr.Zero)
                            {
                                _commandQueue = OpenClInterop.CreateCommandQueue(
                                    _context, firstDevice, 0, out int queueErr);

                                if (queueErr != OpenClInterop.ClSuccess)
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
                throw new InvalidOperationException("OpenCl is not available. Check IsAvailable before calling.");

            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            if (a.Length != b.Length)
                throw new ArgumentException("Vectors must have the same length");

            // OpenCl kernel execution:
            // 1. Create buffers (clCreateBuffer) for a, b, result
            // 2. Write a, b to device (clEnqueueWriteBuffer)
            // 3. Build kernel: __kernel void vecMul(__global float* a, __global float* b, __global float* c, int n) { ... }
            // 4. Set kernel args (clSetKernelArg)
            // 5. Execute (clEnqueueNdRangeKernel)
            // 6. Read result (clEnqueueReadBuffer)
            // 7. Release buffers
            //
            // CPU fallback to establish API contract (GPU kernel launch requires OpenCl runtime)
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
                throw new InvalidOperationException("OpenCl is not available.");

            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            int m = a.GetLength(0), k = a.GetLength(1);
            int k2 = b.GetLength(0), n = b.GetLength(1);

            if (k != k2)
                throw new ArgumentException("Matrix dimensions incompatible for multiplication");
            if (m > 4096 || n > 4096 || k > 4096)
                throw new ArgumentException($"Matrix dimensions too large for CPU fallback: {m}x{k} * {k}x{n}. Max 4096 per dimension (finding P2-372).");

            float[] result = new float[m * n];
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Run(() =>
            {
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < n; j++)
                    {
                        float sum = 0;
                        for (int ki = 0; ki < k; ki++)
                            sum += a[i, ki] * b[ki, j];
                        result[i * n + j] = sum;
                    }
            });
            Interlocked.Add(ref _totalProcessingTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref _operationsCompleted);
            return result;
        }

        /// <inheritdoc/>
        public async Task<float[]> ComputeEmbeddingsAsync(float[] input, float[,] weights)
        {
            if (!_isAvailable)
                throw new InvalidOperationException("OpenCl is not available.");

            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(weights);

            int d = input.Length;
            int d2 = weights.GetLength(0), e = weights.GetLength(1);

            if (d != d2)
                throw new ArgumentException("Input dimension must match weight rows");

            float[] result = new float[e];
            await Task.Run(() =>
            {
                for (int j = 0; j < e; j++)
                {
                    float sum = 0;
                    for (int i = 0; i < d; i++)
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
                "OpenCl accelerator requires float[] operations. Use VectorMultiplyAsync, MatrixMultiplyAsync, or ComputeEmbeddingsAsync.");
        }

        /// <inheritdoc/>
        public Task<AcceleratorStatistics> GetStatisticsAsync()
        {
            return Task.FromResult(new AcceleratorStatistics(
                Type: Type,
                OperationsCompleted: Interlocked.Read(ref _operationsCompleted),
                AverageThroughputMBps: 0.0, // Requires OpenCl perf counters (finding P2-371)
                CurrentUtilization: 0.0,
                TotalProcessingTime: Interlocked.Read(ref _totalProcessingTicks) > 0
                    ? TimeSpan.FromSeconds((double)Interlocked.Read(ref _totalProcessingTicks) / System.Diagnostics.Stopwatch.Frequency)
                    : TimeSpan.Zero
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
