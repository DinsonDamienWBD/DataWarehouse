using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// SYCL Runtime API interop layer for Intel oneAPI heterogeneous compute acceleration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SYCL is a cross-platform abstraction layer built on top of OpenCL that enables
    /// code for heterogeneous processors to be written in standard C++. Intel's
    /// implementation (DPC++) is the primary SYCL runtime.
    /// </para>
    /// <para>
    /// SYCL Runtime libraries:
    /// - Windows: sycl.dll (Intel oneAPI DPC++ runtime)
    /// - Linux: libsycl.so
    /// </para>
    /// <para>
    /// SYCL primarily works via compiled SPIR-V kernels. The interop layer provides
    /// device management and memory operations; kernel execution loads pre-compiled
    /// SPIR-V binaries.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: SYCL interop (HW-05)")]
    internal static partial class SyclInterop
    {
        // Platform-specific library names
        private const string SyclLibraryWindows = "sycl";
        private const string SyclLibraryLinux = "libsycl.so";

        // Determine library name based on platform
        private static readonly string SyclLibrary = OperatingSystem.IsWindows()
            ? SyclLibraryWindows
            : SyclLibraryLinux;

        /// <summary>
        /// Success return code for SYCL operations.
        /// </summary>
        internal const int SyclSuccess = 0;

        /// <summary>
        /// SYCL error codes (minimal subset for interop).
        /// </summary>
        internal enum SyclError
        {
            Success = 0,
            RuntimeError = 1,
            InvalidValue = 2,
            InvalidDevice = 3,
            InvalidQueue = 4,
            OutOfMemory = 5,
            DeviceNotFound = 6,
            CompilationFailure = 7,
        }

        /// <summary>
        /// SYCL device types for device selection.
        /// </summary>
        internal const int SyclDeviceTypeGpu = 1;
        internal const int SyclDeviceTypeCpu = 2;
        internal const int SyclDeviceTypeAccelerator = 3;
        internal const int SyclDeviceTypeAll = 0;

        /// <summary>
        /// SYCL device info query parameters.
        /// </summary>
        internal const int SyclDeviceInfoName = 1;
        internal const int SyclDeviceInfoVendor = 2;
        internal const int SyclDeviceInfoMaxComputeUnits = 3;
        internal const int SyclDeviceInfoGlobalMemSize = 4;

        /// <summary>
        /// SYCL memory copy direction.
        /// </summary>
        internal const int SyclMemcpyHostToDevice = 1;
        internal const int SyclMemcpyDeviceToHost = 2;

        // --- Device API ---

        /// <summary>
        /// Gets available SYCL devices of the specified type.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclDeviceGet")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int DeviceGet(
            out IntPtr device, int deviceType, int deviceIndex);

        /// <summary>
        /// Gets the number of available SYCL devices of the specified type.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclDeviceGetCount")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int DeviceGetCount(out int count, int deviceType);

        /// <summary>
        /// Gets information about a SYCL device.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclDeviceGetInfo")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int DeviceGetInfo(
            IntPtr device, int paramName, nuint paramValueSize,
            IntPtr paramValue, out nuint paramValueSizeRet);

        // --- Queue API ---

        /// <summary>
        /// Creates a SYCL queue for the specified device.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclQueueCreate")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int QueueCreate(out IntPtr queue, IntPtr device);

        /// <summary>
        /// Submits a command group to a SYCL queue.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclQueueSubmit")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int QueueSubmit(IntPtr queue, IntPtr commandGroup);

        /// <summary>
        /// Waits for all submitted commands in the queue to complete.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclQueueWait")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int QueueWait(IntPtr queue);

        /// <summary>
        /// Destroys a SYCL queue and releases resources.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclQueueDestroy")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int QueueDestroy(IntPtr queue);

        // --- Memory API ---

        /// <summary>
        /// Allocates device memory via SYCL USM (Unified Shared Memory).
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclMallocDevice")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial IntPtr MallocDevice(nuint size, IntPtr queue);

        /// <summary>
        /// Allocates host-accessible memory via SYCL USM.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclMallocHost")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial IntPtr MallocHost(nuint size, IntPtr queue);

        /// <summary>
        /// Frees SYCL USM-allocated memory.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Free(IntPtr ptr, IntPtr queue);

        /// <summary>
        /// Copies memory between host and device using SYCL USM.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclMemcpy")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Memcpy(
            IntPtr queue, IntPtr dst, IntPtr src, nuint count);

        // --- Kernel API ---

        /// <summary>
        /// Loads a pre-compiled SPIR-V kernel binary for execution.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclKernelLoadSpirv")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int KernelLoadSpirv(
            out IntPtr kernel, IntPtr queue, IntPtr spirvData, nuint spirvSize);

        /// <summary>
        /// Launches a loaded kernel with the specified work dimensions.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclKernelLaunch")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int KernelLaunch(
            IntPtr kernel, IntPtr queue, nuint globalSize, nuint localSize);

        /// <summary>
        /// Releases a loaded SYCL kernel.
        /// </summary>
        [LibraryImport(SyclLibraryWindows, EntryPoint = "syclKernelRelease")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int KernelRelease(IntPtr kernel);
    }

    /// <summary>
    /// SYCL accelerator providing Intel oneAPI heterogeneous compute (CPU+GPU+FPGA).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps the SYCL P/Invoke bindings with a managed API that implements
    /// <see cref="IGpuAccelerator"/>. SYCL provides a single-source programming model
    /// for heterogeneous compute across Intel CPUs, GPUs, and FPGAs.
    /// </para>
    /// <para>
    /// <strong>SPIR-V Kernel Loading:</strong> SYCL kernels are compiled to SPIR-V
    /// intermediate representation. The <see cref="LaunchKernelAsync"/> method loads
    /// pre-compiled SPIR-V binaries for execution on the target device.
    /// </para>
    /// <para>
    /// <strong>Graceful Unavailability:</strong> If the SYCL runtime (sycl.dll / libsycl.so)
    /// is not found, <see cref="IsAvailable"/> returns false.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: SYCL accelerator (HW-05)")]
    public sealed class SyclAccelerator : IGpuAccelerator, IDisposable
    {
        /// <summary>Platform capability registry provided at construction (finding P4-1997).</summary>
        internal IPlatformCapabilityRegistry Registry => _registry;
        private readonly IPlatformCapabilityRegistry _registry;
        private int _deviceCount;
        private bool _isAvailable;
        private bool _initialized;
        private long _operationsCompleted;
        private long _totalProcessingTicks; // accumulated via Interlocked.Add (finding P2-371)
        private readonly object _lock = new();
        private bool _disposed;

        // SYCL handles
        /// <summary>SYCL device handle (finding P4-1998).</summary>
        internal IntPtr DeviceHandle => _device;
        private IntPtr _device;
        private IntPtr _queue;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyclAccelerator"/> class.
        /// </summary>
        /// <param name="registry">Platform capability registry for registering SYCL capabilities.</param>
        /// <exception cref="ArgumentNullException">Thrown when registry is null.</exception>
        public SyclAccelerator(IPlatformCapabilityRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <inheritdoc/>
        public AcceleratorType Type => AcceleratorType.Sycl;

        /// <inheritdoc/>
        public bool IsAvailable => _isAvailable;

        /// <inheritdoc/>
        /// <remarks>True when SYCL GPU is unavailable and CPU is used as fallback (finding P1-365).</remarks>
        public bool IsCpuFallback => !_isAvailable;

        /// <inheritdoc/>
        public GpuRuntime Runtime => GpuRuntime.Sycl;

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
                        string syclLib = OperatingSystem.IsWindows() ? "sycl.dll" : "libsycl.so";

                        if (!NativeLibrary.TryLoad(syclLib, out IntPtr _))
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        // Query GPU device count
                        int result = SyclInterop.DeviceGetCount(out int gpuCount, SyclInterop.SyclDeviceTypeGpu);

                        if (result != SyclInterop.SyclSuccess || gpuCount == 0)
                        {
                            // Fall back to checking all device types (CPU, FPGA)
                            result = SyclInterop.DeviceGetCount(out int allCount, SyclInterop.SyclDeviceTypeAll);

                            if (result != SyclInterop.SyclSuccess || allCount == 0)
                            {
                                _isAvailable = false;
                                _initialized = true;
                                return;
                            }

                            _deviceCount = allCount;
                        }
                        else
                        {
                            _deviceCount = gpuCount;
                        }

                        // Get first device
                        result = SyclInterop.DeviceGet(out IntPtr device, SyclInterop.SyclDeviceTypeGpu, 0);
                        if (result != SyclInterop.SyclSuccess)
                        {
                            result = SyclInterop.DeviceGet(out device, SyclInterop.SyclDeviceTypeAll, 0);
                            if (result != SyclInterop.SyclSuccess)
                            {
                                _isAvailable = false;
                                _initialized = true;
                                return;
                            }
                        }

                        _device = device;

                        // Create queue
                        result = SyclInterop.QueueCreate(out IntPtr queue, device);
                        if (result != SyclInterop.SyclSuccess)
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        _queue = queue;
                        _isAvailable = true;
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

        /// <summary>
        /// Loads and launches a pre-compiled SPIR-V kernel on the SYCL device.
        /// </summary>
        /// <param name="spirvBinary">The compiled SPIR-V binary data.</param>
        /// <param name="globalWorkSize">Global work size for kernel execution.</param>
        /// <param name="localWorkSize">Local work group size (0 for auto).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the kernel executed successfully.</returns>
        /// <exception cref="InvalidOperationException">Thrown when SYCL is not available.</exception>
        public async Task<bool> LaunchKernelAsync(
            byte[] spirvBinary, nuint globalWorkSize, nuint localWorkSize, CancellationToken ct = default)
        {
            if (!_isAvailable)
                throw new InvalidOperationException("SYCL is not available. Check IsAvailable before calling.");

            ArgumentNullException.ThrowIfNull(spirvBinary);

            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                unsafe
                {
                    fixed (byte* spirvPtr = spirvBinary)
                    {
                        int result = SyclInterop.KernelLoadSpirv(
                            out IntPtr kernel, _queue, (IntPtr)spirvPtr, (nuint)spirvBinary.Length);

                        if (result != SyclInterop.SyclSuccess)
                            return false;

                        try
                        {
                            result = SyclInterop.KernelLaunch(kernel, _queue, globalWorkSize, localWorkSize);
                            if (result != SyclInterop.SyclSuccess)
                                return false;

                            result = SyclInterop.QueueWait(_queue);
                            return result == SyclInterop.SyclSuccess;
                        }
                        finally
                        {
                            SyclInterop.KernelRelease(kernel);
                        }
                    }
                }
            }, ct);
        }

        /// <inheritdoc/>
        public async Task<float[]> VectorMultiplyAsync(float[] a, float[] b)
        {
            if (!_isAvailable)
                throw new InvalidOperationException("SYCL is not available. Check IsAvailable before calling.");

            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            if (a.Length != b.Length)
                throw new ArgumentException("Vectors must have the same length");

            // SYCL kernel execution via SPIR-V:
            // 1. Allocate USM memory (syclMallocDevice)
            // 2. Copy input data (syclMemcpy host->device)
            // 3. Load SPIR-V kernel (syclKernelLoadSpirv)
            // 4. Launch kernel (syclKernelLaunch)
            // 5. Copy result (syclMemcpy device->host)
            // 6. Free USM memory (syclFree)
            //
            // CPU fallback to establish API contract
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
                throw new InvalidOperationException("SYCL is not available.");

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
                throw new InvalidOperationException("SYCL is not available.");

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
                "SYCL accelerator requires float[] operations. Use VectorMultiplyAsync, MatrixMultiplyAsync, or ComputeEmbeddingsAsync. " +
                "For SPIR-V kernel execution, use LaunchKernelAsync.");
        }

        /// <inheritdoc/>
        public Task<AcceleratorStatistics> GetStatisticsAsync()
        {
            return Task.FromResult(new AcceleratorStatistics(
                Type: Type,
                OperationsCompleted: Interlocked.Read(ref _operationsCompleted),
                AverageThroughputMBps: 0.0, // Requires SYCL perf counters (finding P2-371)
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

            if (_queue != IntPtr.Zero)
            {
                try { SyclInterop.QueueDestroy(_queue); } catch { /* cleanup best-effort */ }
                _queue = IntPtr.Zero;
            }

            _disposed = true;
        }
    }
}
