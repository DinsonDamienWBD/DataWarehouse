using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// Triton GPU kernel compilation interop for ML workloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Triton (OpenAI) is a Python-based GPU kernel compiler that generates optimized
    /// PTX/CUBIN kernels from high-level Python definitions. This interop layer supports
    /// two integration modes:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <strong>Pre-compiled:</strong> Load Triton-compiled PTX/CUBIN kernels via CUDA driver API.
    /// </description></item>
    /// <item><description>
    /// <strong>Runtime compilation:</strong> Shell out to <c>triton-compile</c> CLI (if available)
    /// then load the compiled result.
    /// </description></item>
    /// </list>
    /// <para>
    /// Triton kernels ultimately execute on CUDA or ROCm backends, so this interop
    /// layer delegates actual GPU execution to the underlying CUDA/ROCm runtime.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Triton interop (HW-05)")]
    internal static partial class TritonInterop
    {
        /// <summary>
        /// CUDA Driver API library for loading compiled Triton kernels (PTX/CUBIN).
        /// </summary>
        private const string CudaDriverLibraryWindows = "nvcuda";
        private const string CudaDriverLibraryLinux = "libcuda.so";

        private static readonly string CudaDriverLibrary = OperatingSystem.IsWindows()
            ? CudaDriverLibraryWindows
            : CudaDriverLibraryLinux;

        /// <summary>
        /// Success return code for CUDA Driver API operations.
        /// </summary>
        internal const int CUDA_SUCCESS = 0;

        // --- CUDA Driver API for module/kernel loading ---

        /// <summary>
        /// Initializes the CUDA driver API.
        /// </summary>
        [LibraryImport(CudaDriverLibraryWindows, EntryPoint = "cuInit")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int CuInit(uint flags);

        /// <summary>
        /// Loads a CUDA module from a file (PTX or CUBIN).
        /// </summary>
        [LibraryImport(CudaDriverLibraryWindows, EntryPoint = "cuModuleLoad", StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int CuModuleLoad(out IntPtr module, string fileName);

        /// <summary>
        /// Loads a CUDA module from in-memory data (PTX or CUBIN).
        /// </summary>
        [LibraryImport(CudaDriverLibraryWindows, EntryPoint = "cuModuleLoadData")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int CuModuleLoadData(out IntPtr module, IntPtr image);

        /// <summary>
        /// Gets a function handle (kernel) from a loaded module.
        /// </summary>
        [LibraryImport(CudaDriverLibraryWindows, EntryPoint = "cuModuleGetFunction", StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int CuModuleGetFunction(out IntPtr function, IntPtr module, string name);

        /// <summary>
        /// Launches a CUDA kernel via the driver API.
        /// </summary>
        [LibraryImport(CudaDriverLibraryWindows, EntryPoint = "cuLaunchKernel")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int CuLaunchKernel(
            IntPtr function,
            uint gridDimX, uint gridDimY, uint gridDimZ,
            uint blockDimX, uint blockDimY, uint blockDimZ,
            uint sharedMemBytes, IntPtr hStream,
            IntPtr[] kernelParams, IntPtr[] extra);

        /// <summary>
        /// Unloads a CUDA module.
        /// </summary>
        [LibraryImport(CudaDriverLibraryWindows, EntryPoint = "cuModuleUnload")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int CuModuleUnload(IntPtr module);

        /// <summary>
        /// Synchronizes the current CUDA context (waits for all operations).
        /// </summary>
        [LibraryImport(CudaDriverLibraryWindows, EntryPoint = "cuCtxSynchronize")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int CuCtxSynchronize();
    }

    /// <summary>
    /// Represents a compiled Triton kernel ready for GPU execution.
    /// </summary>
    /// <param name="BinaryData">The compiled PTX or CUBIN binary data.</param>
    /// <param name="EntryPoint">The kernel entry point function name.</param>
    /// <param name="SharedMemorySize">Required shared memory size in bytes.</param>
    /// <param name="SourcePath">Original Triton source file path (if available).</param>
    public record CompiledTritonKernel(
        byte[] BinaryData,
        string EntryPoint,
        int SharedMemorySize,
        string? SourcePath = null);

    /// <summary>
    /// Loads and manages Triton-compiled GPU kernels for ML workloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supports two modes of operation:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Loading pre-compiled PTX/CUBIN kernel binaries directly</description></item>
    /// <item><description>Runtime compilation via the <c>triton-compile</c> CLI tool</description></item>
    /// </list>
    /// <para>
    /// If <c>triton-compile</c> is not found on the system PATH, runtime compilation
    /// is unavailable but pre-compiled kernels can still be loaded.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Triton kernel loader (HW-05)")]
    public sealed class TritonKernelLoader
    {
        private readonly bool _tritonCompilerAvailable;
        private readonly string? _tritonCompilerPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="TritonKernelLoader"/> class.
        /// Detects whether the triton-compile CLI tool is available on the system.
        /// </summary>
        public TritonKernelLoader()
        {
            _tritonCompilerPath = FindTritonCompiler();
            _tritonCompilerAvailable = _tritonCompilerPath != null;
        }

        /// <summary>
        /// Gets whether the Triton compiler CLI is available for runtime compilation.
        /// </summary>
        public bool IsCompilerAvailable => _tritonCompilerAvailable;

        /// <summary>
        /// Loads a pre-compiled Triton kernel from a binary file (PTX or CUBIN).
        /// </summary>
        /// <param name="kernelPath">Path to the compiled kernel binary file.</param>
        /// <param name="entryPoint">Kernel entry point function name.</param>
        /// <param name="sharedMemorySize">Required shared memory size in bytes.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="CompiledTritonKernel"/> ready for execution.</returns>
        /// <exception cref="FileNotFoundException">Thrown when the kernel file is not found.</exception>
        public async Task<CompiledTritonKernel> LoadKernelAsync(
            string kernelPath, string entryPoint, int sharedMemorySize = 0, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(kernelPath);
            ArgumentNullException.ThrowIfNull(entryPoint);

            if (!File.Exists(kernelPath))
                throw new FileNotFoundException($"Compiled Triton kernel not found: {kernelPath}", kernelPath);

            byte[] binaryData = await File.ReadAllBytesAsync(kernelPath, ct);

            return new CompiledTritonKernel(
                BinaryData: binaryData,
                EntryPoint: entryPoint,
                SharedMemorySize: sharedMemorySize,
                SourcePath: kernelPath);
        }

        /// <summary>
        /// Compiles a Triton source file using the triton-compile CLI and returns the compiled kernel.
        /// </summary>
        /// <param name="tritonSourcePath">Path to the Triton Python source file.</param>
        /// <param name="entryPoint">Kernel entry point function name.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="CompiledTritonKernel"/> compiled from the source.</returns>
        /// <exception cref="InvalidOperationException">Thrown when triton-compile is not available.</exception>
        /// <exception cref="FileNotFoundException">Thrown when the source file is not found.</exception>
        public async Task<CompiledTritonKernel> CompileKernelAsync(
            string tritonSourcePath, string entryPoint, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(tritonSourcePath);
            ArgumentNullException.ThrowIfNull(entryPoint);

            if (!_tritonCompilerAvailable)
                throw new InvalidOperationException(
                    "Triton compiler (triton-compile) is not available. Install OpenAI Triton: pip install triton");

            if (!File.Exists(tritonSourcePath))
                throw new FileNotFoundException($"Triton source file not found: {tritonSourcePath}", tritonSourcePath);

            string outputPath = Path.Combine(
                Path.GetTempPath(),
                $"triton_{Path.GetFileNameWithoutExtension(tritonSourcePath)}_{Guid.NewGuid():N}.cubin");

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = _tritonCompilerPath!,
                    Arguments = $"\"{tritonSourcePath}\" --kernel-name {entryPoint} -o \"{outputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                process.Start();

                // Read output/error streams
                string stdout = await process.StandardOutput.ReadToEndAsync(ct);
                string stderr = await process.StandardError.ReadToEndAsync(ct);

                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Triton compilation failed (exit code {process.ExitCode}): {stderr}");
                }

                if (!File.Exists(outputPath))
                {
                    throw new InvalidOperationException(
                        $"Triton compilation produced no output. stdout: {stdout}, stderr: {stderr}");
                }

                byte[] binaryData = await File.ReadAllBytesAsync(outputPath, ct);

                return new CompiledTritonKernel(
                    BinaryData: binaryData,
                    EntryPoint: entryPoint,
                    SharedMemorySize: 0,
                    SourcePath: tritonSourcePath);
            }
            finally
            {
                // Cleanup temporary output file
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best-effort cleanup */ }
            }
        }

        private static string? FindTritonCompiler()
        {
            string[] candidates = OperatingSystem.IsWindows()
                ? new[] { "triton-compile.exe", "triton-compile" }
                : new[] { "triton-compile" };

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (string dir in pathDirs)
            {
                foreach (string candidate in candidates)
                {
                    string fullPath = Path.Combine(dir, candidate);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Triton accelerator for GPU kernel compilation and execution of ML workloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Triton kernels are compiled to PTX/CUBIN and execute on the underlying CUDA or
    /// ROCm runtime. This accelerator wraps the <see cref="TritonKernelLoader"/> for
    /// kernel management and delegates GPU execution to the CUDA driver API.
    /// </para>
    /// <para>
    /// <strong>Graceful Unavailability:</strong> If neither the CUDA driver library nor
    /// the triton-compile CLI are available, <see cref="IsAvailable"/> returns false.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Triton accelerator (HW-05)")]
    public sealed class TritonAccelerator : IGpuAccelerator, IDisposable
    {
        private readonly IPlatformCapabilityRegistry _registry;
        private readonly TritonKernelLoader _kernelLoader;
        private int _deviceCount;
        private bool _isAvailable;
        private bool _initialized;
        private long _operationsCompleted;
        private readonly object _lock = new();
        private bool _disposed;
        private bool _cudaDriverAvailable;

        /// <summary>
        /// Initializes a new instance of the <see cref="TritonAccelerator"/> class.
        /// </summary>
        /// <param name="registry">Platform capability registry for registering Triton capabilities.</param>
        /// <exception cref="ArgumentNullException">Thrown when registry is null.</exception>
        public TritonAccelerator(IPlatformCapabilityRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _kernelLoader = new TritonKernelLoader();
        }

        /// <inheritdoc/>
        public AcceleratorType Type => AcceleratorType.Triton;

        /// <inheritdoc/>
        public bool IsAvailable => _isAvailable;

        /// <inheritdoc/>
        public GpuRuntime Runtime => GpuRuntime.Triton;

        /// <inheritdoc/>
        public int DeviceCount => _deviceCount;

        /// <summary>
        /// Gets the kernel loader for compiling and loading Triton kernels.
        /// </summary>
        public TritonKernelLoader KernelLoader => _kernelLoader;

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
                        // Triton requires CUDA driver API for kernel execution
                        string cudaDriverLib = OperatingSystem.IsWindows() ? "nvcuda.dll" : "libcuda.so";

                        if (NativeLibrary.TryLoad(cudaDriverLib, out IntPtr _))
                        {
                            int result = TritonInterop.CuInit(0);
                            if (result == TritonInterop.CUDA_SUCCESS)
                            {
                                _cudaDriverAvailable = true;

                                // Use CUDA runtime to get device count (driver API requires more setup)
                                try
                                {
                                    int rtResult = CudaInterop.GetDeviceCount(out int count);
                                    if (rtResult == CudaInterop.CUDA_SUCCESS && count > 0)
                                    {
                                        _deviceCount = count;
                                    }
                                }
                                catch
                                {
                                    _deviceCount = 1; // CUDA driver loaded, assume at least 1 device
                                }
                            }
                        }

                        // Triton is available if we have CUDA driver OR the compiler
                        _isAvailable = _cudaDriverAvailable || _kernelLoader.IsCompilerAvailable;

                        if (!_isAvailable)
                        {
                            // Log that Triton is unavailable
                            // Neither CUDA driver (for execution) nor triton-compile (for compilation) found
                        }

                        _initialized = true;
                    }
                    catch (DllNotFoundException)
                    {
                        _isAvailable = _kernelLoader.IsCompilerAvailable;
                        _initialized = true;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        _isAvailable = _kernelLoader.IsCompilerAvailable;
                        _initialized = true;
                    }
                }
            });
        }

        /// <summary>
        /// Loads and executes a compiled Triton kernel on the GPU.
        /// </summary>
        /// <param name="kernel">The compiled Triton kernel to execute.</param>
        /// <param name="gridDim">Grid dimensions (blocks).</param>
        /// <param name="blockDim">Block dimensions (threads per block).</param>
        /// <param name="kernelParams">Kernel parameter pointers.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if kernel executed successfully.</returns>
        /// <exception cref="InvalidOperationException">Thrown when CUDA driver is not available.</exception>
        public async Task<bool> ExecuteKernelAsync(
            CompiledTritonKernel kernel,
            (uint x, uint y, uint z) gridDim,
            (uint x, uint y, uint z) blockDim,
            IntPtr[] kernelParams,
            CancellationToken ct = default)
        {
            if (!_cudaDriverAvailable)
                throw new InvalidOperationException(
                    "CUDA driver is not available. Triton kernel execution requires CUDA.");

            ArgumentNullException.ThrowIfNull(kernel);

            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                unsafe
                {
                    fixed (byte* dataPtr = kernel.BinaryData)
                    {
                        int result = TritonInterop.CuModuleLoadData(out IntPtr module, (IntPtr)dataPtr);
                        if (result != TritonInterop.CUDA_SUCCESS)
                            return false;

                        try
                        {
                            result = TritonInterop.CuModuleGetFunction(out IntPtr function, module, kernel.EntryPoint);
                            if (result != TritonInterop.CUDA_SUCCESS)
                                return false;

                            result = TritonInterop.CuLaunchKernel(
                                function,
                                gridDim.x, gridDim.y, gridDim.z,
                                blockDim.x, blockDim.y, blockDim.z,
                                (uint)kernel.SharedMemorySize, IntPtr.Zero,
                                kernelParams, null!);

                            if (result != TritonInterop.CUDA_SUCCESS)
                                return false;

                            result = TritonInterop.CuCtxSynchronize();
                            return result == TritonInterop.CUDA_SUCCESS;
                        }
                        finally
                        {
                            TritonInterop.CuModuleUnload(module);
                        }
                    }
                }
            }, ct);
        }

        /// <inheritdoc/>
        public async Task<float[]> VectorMultiplyAsync(float[] a, float[] b)
        {
            if (!_isAvailable)
                throw new InvalidOperationException("Triton is not available. Check IsAvailable before calling.");

            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            if (a.Length != b.Length)
                throw new ArgumentException("Vectors must have the same length");

            // Triton kernel execution:
            // 1. Compile or load Triton vector multiply kernel
            // 2. Allocate GPU memory via CUDA Malloc
            // 3. Copy data to GPU
            // 4. Launch compiled kernel via CuLaunchKernel
            // 5. Copy result back
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
                throw new InvalidOperationException("Triton is not available.");

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
                throw new InvalidOperationException("Triton is not available.");

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
                "Triton accelerator requires float[] operations or direct kernel execution. " +
                "Use VectorMultiplyAsync, MatrixMultiplyAsync, ComputeEmbeddingsAsync, or ExecuteKernelAsync.");
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
            _disposed = true;
        }
    }
}
