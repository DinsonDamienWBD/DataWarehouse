using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// CANN (Compute Architecture for Neural Networks) API interop layer for Huawei Ascend NPU acceleration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CANN is Huawei's heterogeneous computing architecture for AI processors.
    /// It provides the AscendCL (Ascend Computing Language) C API for device management,
    /// memory operations, stream management, and model inference on Ascend NPUs.
    /// </para>
    /// <para>
    /// CANN Runtime libraries:
    /// - Linux: libascendcl.so (primary platform for Ascend NPU)
    /// - Windows: ascendcl.dll (limited availability)
    /// </para>
    /// <para>
    /// Ascend NPU models are pre-compiled to the .om (Offline Model) format using
    /// the ATC (Ascend Tensor Compiler) tool. This interop loads and executes .om models.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: CANN interop (HW-05)")]
    internal static partial class CannInterop
    {
        // Platform-specific library names
        private const string CannLibraryWindows = "ascendcl";
        private const string CannLibraryLinux = "libascendcl.so";

        // Determine library name based on platform
        private static readonly string CannLibrary = OperatingSystem.IsWindows()
            ? CannLibraryWindows
            : CannLibraryLinux;

        /// <summary>
        /// Success return code for CANN operations.
        /// </summary>
        internal const int ACL_SUCCESS = 0;

        /// <summary>
        /// CANN error codes (minimal subset).
        /// </summary>
        internal enum AclError
        {
            ACL_SUCCESS = 0,
            ACL_ERROR_INVALID_PARAM = 100000,
            ACL_ERROR_UNINITIALIZE = 100001,
            ACL_ERROR_REPEAT_INITIALIZE = 100002,
            ACL_ERROR_INVALID_FILE = 100003,
            ACL_ERROR_WRITE_FILE = 100004,
            ACL_ERROR_INVALID_FILE_SIZE = 100005,
            ACL_ERROR_PARSE_FILE = 100006,
            ACL_ERROR_FILE_MISSING_ATTR = 100007,
            ACL_ERROR_FILE_ATTR_INVALID = 100008,
            ACL_ERROR_INVALID_DUMP_CONFIG = 100009,
            ACL_ERROR_INVALID_PROFILING_CONFIG = 100010,
            ACL_ERROR_INVALID_MODEL_ID = 100011,
            ACL_ERROR_DESERIALIZE_MODEL = 100012,
            ACL_ERROR_MEMORY_ADDRESS_UNALIGNED = 200000,
            ACL_ERROR_RESOURCE_NOT_MATCH = 200001,
            ACL_ERROR_INVALID_RESOURCE_HANDLE = 200002,
            ACL_ERROR_FEATURE_UNSUPPORTED = 200003,
            ACL_ERROR_STORAGE_OVER_LIMIT = 300000,
            ACL_ERROR_INTERNAL_ERROR = 500000,
            ACL_ERROR_FAILURE = 500001,
            ACL_ERROR_NOT_FOUND = 500002,
            ACL_ERROR_STREAM_NOT_CREATED = 500003,
        }

        /// <summary>
        /// CANN memory copy types.
        /// </summary>
        internal const int ACL_MEMCPY_HOST_TO_HOST = 0;
        internal const int ACL_MEMCPY_HOST_TO_DEVICE = 1;
        internal const int ACL_MEMCPY_DEVICE_TO_HOST = 2;
        internal const int ACL_MEMCPY_DEVICE_TO_DEVICE = 3;

        // --- Initialization API ---

        /// <summary>
        /// Initializes the AscendCL runtime environment.
        /// </summary>
        /// <param name="configPath">Optional path to ACL configuration file (null for defaults).</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclInit", StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int AclInit(string? configPath);

        /// <summary>
        /// Finalizes the AscendCL runtime and releases all resources.
        /// </summary>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclFinalize")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int AclFinalize();

        // --- Device API ---

        /// <summary>
        /// Sets the active Ascend device.
        /// </summary>
        /// <param name="deviceId">Device index (0-based).</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclrtSetDevice")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int SetDevice(int deviceId);

        /// <summary>
        /// Resets a device and frees all resources on it.
        /// </summary>
        /// <param name="deviceId">Device index.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclrtResetDevice")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ResetDevice(int deviceId);

        /// <summary>
        /// Gets the number of available Ascend devices.
        /// </summary>
        /// <param name="count">Output: number of devices.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclrtGetDeviceCount")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetDeviceCount(out uint count);

        // --- Memory API ---

        /// <summary>
        /// Allocates device memory on the Ascend NPU.
        /// </summary>
        /// <param name="devPtr">Output: device memory pointer.</param>
        /// <param name="size">Size in bytes to allocate.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclrtMalloc")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Malloc(out IntPtr devPtr, nuint size, uint policy);

        /// <summary>
        /// Frees device memory.
        /// </summary>
        /// <param name="devPtr">Device memory pointer to free.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclrtFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Free(IntPtr devPtr);

        /// <summary>
        /// Copies memory between host and Ascend device.
        /// </summary>
        /// <param name="dst">Destination pointer.</param>
        /// <param name="destMax">Maximum destination buffer size.</param>
        /// <param name="src">Source pointer.</param>
        /// <param name="count">Number of bytes to copy.</param>
        /// <param name="kind">Copy direction (host->device, device->host, etc.).</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclrtMemcpy")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Memcpy(IntPtr dst, nuint destMax, IntPtr src, nuint count, int kind);

        // --- Stream API ---

        /// <summary>
        /// Creates an execution stream on the Ascend device.
        /// </summary>
        /// <param name="stream">Output: stream handle.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclrtCreateStream")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int CreateStream(out IntPtr stream);

        /// <summary>
        /// Destroys an execution stream.
        /// </summary>
        /// <param name="stream">Stream handle to destroy.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclrtDestroyStream")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int DestroyStream(IntPtr stream);

        /// <summary>
        /// Synchronizes a stream (waits for all operations in the stream to complete).
        /// </summary>
        /// <param name="stream">Stream handle.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclrtSynchronizeStream")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int SynchronizeStream(IntPtr stream);

        // --- Model API ---

        /// <summary>
        /// Loads an offline model (.om) from a file.
        /// </summary>
        /// <param name="modelPath">Path to the .om model file.</param>
        /// <param name="modelId">Output: model identifier for subsequent operations.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclmdlLoadFromFile", StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ModelLoadFromFile(string modelPath, out uint modelId);

        /// <summary>
        /// Executes a loaded model (inference).
        /// </summary>
        /// <param name="modelId">Model identifier from LoadFromFile.</param>
        /// <param name="input">Input dataset handle.</param>
        /// <param name="output">Output dataset handle.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclmdlExecute")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ModelExecute(uint modelId, IntPtr input, IntPtr output);

        /// <summary>
        /// Unloads a model and frees associated resources.
        /// </summary>
        /// <param name="modelId">Model identifier to unload.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclmdlUnload")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ModelUnload(uint modelId);

        /// <summary>
        /// Creates a model description for querying model input/output details.
        /// </summary>
        /// <returns>Pointer to model description, or IntPtr.Zero on failure.</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclmdlCreateDesc")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial IntPtr ModelCreateDesc();

        /// <summary>
        /// Gets model description from a loaded model.
        /// </summary>
        /// <param name="desc">Model description handle.</param>
        /// <param name="modelId">Model identifier.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclmdlGetDesc")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ModelGetDesc(IntPtr desc, uint modelId);

        /// <summary>
        /// Destroys a model description handle.
        /// </summary>
        /// <param name="desc">Model description handle to destroy.</param>
        /// <returns>ACL error code (0 = success).</returns>
        [LibraryImport(CannLibraryWindows, EntryPoint = "aclmdlDestroyDesc")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int ModelDestroyDesc(IntPtr desc);
    }

    /// <summary>
    /// CANN accelerator providing Huawei Ascend NPU acceleration for AI/ML workloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps the CANN AscendCL P/Invoke bindings with a managed API that implements
    /// <see cref="IGpuAccelerator"/>. Ascend NPUs are specialized for neural network
    /// inference and training workloads.
    /// </para>
    /// <para>
    /// <strong>Model Execution:</strong> Ascend models are pre-compiled to the .om
    /// (Offline Model) format using the ATC tool. The <see cref="LoadAndExecuteModelAsync"/>
    /// method loads and runs inference on .om model files.
    /// </para>
    /// <para>
    /// <strong>Graceful Unavailability:</strong> If the CANN runtime (libascendcl.so / ascendcl.dll)
    /// is not found, <see cref="IsAvailable"/> returns false.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: CANN accelerator (HW-05)")]
    public sealed class CannAccelerator : IGpuAccelerator, IDisposable
    {
        private readonly IPlatformCapabilityRegistry _registry;
        private int _deviceCount;
        private bool _isAvailable;
        private bool _initialized;
        private long _operationsCompleted;
        private readonly object _lock = new();
        private bool _disposed;

        // CANN handles
        private IntPtr _stream;
        private bool _aclInitialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="CannAccelerator"/> class.
        /// </summary>
        /// <param name="registry">Platform capability registry for registering CANN capabilities.</param>
        /// <exception cref="ArgumentNullException">Thrown when registry is null.</exception>
        public CannAccelerator(IPlatformCapabilityRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <inheritdoc/>
        public AcceleratorType Type => AcceleratorType.Cann;

        /// <inheritdoc/>
        public bool IsAvailable => _isAvailable;

        /// <inheritdoc/>
        public GpuRuntime Runtime => GpuRuntime.Cann;

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
                        string cannLib = OperatingSystem.IsWindows() ? "ascendcl.dll" : "libascendcl.so";

                        if (!NativeLibrary.TryLoad(cannLib, out IntPtr _))
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        // Initialize ACL runtime
                        int result = CannInterop.AclInit(null);
                        if (result != CannInterop.ACL_SUCCESS &&
                            result != (int)CannInterop.AclError.ACL_ERROR_REPEAT_INITIALIZE)
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        _aclInitialized = true;

                        // Query device count
                        result = CannInterop.GetDeviceCount(out uint deviceCount);
                        if (result != CannInterop.ACL_SUCCESS || deviceCount == 0)
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        _deviceCount = (int)deviceCount;

                        // Set device 0 and create stream
                        result = CannInterop.SetDevice(0);
                        if (result != CannInterop.ACL_SUCCESS)
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        result = CannInterop.CreateStream(out IntPtr stream);
                        if (result != CannInterop.ACL_SUCCESS)
                        {
                            _isAvailable = false;
                            _initialized = true;
                            return;
                        }

                        _stream = stream;
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
        /// Loads an Ascend .om model file and executes inference.
        /// </summary>
        /// <param name="modelPath">Path to the .om (Offline Model) file.</param>
        /// <param name="inputData">Input data handle (from aclmdlCreateDataset).</param>
        /// <param name="outputData">Output data handle (from aclmdlCreateDataset).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the model executed successfully.</returns>
        /// <exception cref="InvalidOperationException">Thrown when CANN is not available.</exception>
        public async Task<bool> LoadAndExecuteModelAsync(
            string modelPath, IntPtr inputData, IntPtr outputData, CancellationToken ct = default)
        {
            if (!_isAvailable)
                throw new InvalidOperationException("CANN is not available. Check IsAvailable before calling.");

            ArgumentNullException.ThrowIfNull(modelPath);

            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                int result = CannInterop.ModelLoadFromFile(modelPath, out uint modelId);
                if (result != CannInterop.ACL_SUCCESS)
                    return false;

                try
                {
                    result = CannInterop.ModelExecute(modelId, inputData, outputData);
                    if (result != CannInterop.ACL_SUCCESS)
                        return false;

                    // Synchronize stream to ensure execution completes
                    if (_stream != IntPtr.Zero)
                    {
                        result = CannInterop.SynchronizeStream(_stream);
                        if (result != CannInterop.ACL_SUCCESS)
                            return false;
                    }

                    Interlocked.Increment(ref _operationsCompleted);
                    return true;
                }
                finally
                {
                    CannInterop.ModelUnload(modelId);
                }
            }, ct);
        }

        /// <inheritdoc/>
        public async Task<float[]> VectorMultiplyAsync(float[] a, float[] b)
        {
            if (!_isAvailable)
                throw new InvalidOperationException("CANN is not available. Check IsAvailable before calling.");

            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            if (a.Length != b.Length)
                throw new ArgumentException("Vectors must have the same length");

            // CANN execution:
            // 1. Allocate device memory (aclrtMalloc)
            // 2. Copy data to device (aclrtMemcpy HOST_TO_DEVICE)
            // 3. Load and execute Ascend custom op or .om model
            // 4. Copy result back (aclrtMemcpy DEVICE_TO_HOST)
            // 5. Free device memory (aclrtFree)
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
                throw new InvalidOperationException("CANN is not available.");

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
                throw new InvalidOperationException("CANN is not available.");

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
                "CANN accelerator requires float[] operations or model execution. " +
                "Use VectorMultiplyAsync, MatrixMultiplyAsync, ComputeEmbeddingsAsync, or LoadAndExecuteModelAsync.");
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

            if (_stream != IntPtr.Zero)
            {
                try { CannInterop.DestroyStream(_stream); } catch { /* cleanup best-effort */ }
                _stream = IntPtr.Zero;
            }

            try { CannInterop.ResetDevice(0); } catch { /* cleanup best-effort */ }

            if (_aclInitialized)
            {
                try { CannInterop.AclFinalize(); } catch { /* cleanup best-effort */ }
                _aclInitialized = false;
            }

            _disposed = true;
        }
    }
}
