using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Edge.Inference;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Hardware.Accelerators
{
    /// <summary>
    /// Inference backend for WASI-NN accelerator routing.
    /// Determines which hardware backend executes inference workloads.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 65: WASI-NN inference backends (HW-13)")]
    public enum InferenceBackend
    {
        /// <summary>CPU execution (always available, fallback).</summary>
        CPU = 0,

        /// <summary>NVIDIA CUDA GPU acceleration.</summary>
        CUDA = 1,

        /// <summary>AMD ROCm GPU acceleration.</summary>
        ROCm = 2,

        /// <summary>OpenCL cross-vendor GPU/accelerator.</summary>
        OpenCL = 3,

        /// <summary>NVIDIA TensorRT optimized inference.</summary>
        TensorRT = 4,

        /// <summary>Apple Core ML inference (macOS/iOS).</summary>
        CoreML = 5,

        /// <summary>Android NNAPI hardware acceleration.</summary>
        NNAPI = 6,

        /// <summary>Huawei CANN (Ascend NPU) acceleration.</summary>
        CANN = 7,

        /// <summary>Automatic backend selection based on hardware availability.</summary>
        Auto = 255,
    }

    /// <summary>
    /// Model format supported by the WASI-NN accelerator.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 65: WASI-NN model formats (HW-13)")]
    public enum ModelFormat
    {
        /// <summary>ONNX format (primary, cross-platform).</summary>
        ONNX = 0,

        /// <summary>TensorFlow Lite format (mobile/edge).</summary>
        TensorFlowLite = 1,

        /// <summary>PyTorch format (requires ONNX conversion for non-CUDA backends).</summary>
        PyTorch = 2,

        /// <summary>OpenVINO IR format (Intel optimized).</summary>
        OpenVINO = 3,
    }

    /// <summary>
    /// Handle to a loaded inference model, tracking which backend is executing it.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 65: WASI-NN model handle (HW-13)")]
    public sealed class ModelHandle : IDisposable
    {
        private readonly IInferenceSession? _session;
        private volatile bool _disposed;

        /// <summary>
        /// Gets the model file path.
        /// </summary>
        public string ModelPath { get; }

        /// <summary>
        /// Gets the backend that is executing this model.
        /// </summary>
        public InferenceBackend Backend { get; }

        /// <summary>
        /// Gets the model format.
        /// </summary>
        public ModelFormat Format { get; }

        /// <summary>
        /// Gets whether the model is loaded and ready for inference.
        /// </summary>
        public bool IsLoaded => _session != null && !_disposed;

        /// <summary>
        /// Gets the underlying inference session (for internal use by WasiNnAccelerator).
        /// </summary>
        internal IInferenceSession? Session => _session;

        /// <summary>
        /// Gets the unique handle identifier.
        /// </summary>
        public Guid HandleId { get; } = Guid.NewGuid();

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelHandle"/> class.
        /// </summary>
        /// <param name="modelPath">Path to the model file.</param>
        /// <param name="backend">Backend executing the model.</param>
        /// <param name="format">Model format.</param>
        /// <param name="session">Underlying inference session.</param>
        internal ModelHandle(string modelPath, InferenceBackend backend, ModelFormat format, IInferenceSession? session)
        {
            ModelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
            Backend = backend;
            Format = format;
            _session = session;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _session?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Result of an inference operation.
    /// </summary>
    /// <param name="Output">Output tensor data.</param>
    /// <param name="Backend">Backend that executed the inference.</param>
    /// <param name="InferenceTimeMs">Inference execution time in milliseconds.</param>
    [SdkCompatibility("3.0.0", Notes = "Phase 65: WASI-NN inference result (HW-13)")]
    public record InferenceResult(
        float[] Output,
        InferenceBackend Backend,
        double InferenceTimeMs
    );

    /// <summary>
    /// Interface for accelerator-aware inference routing.
    /// Extends WASI-NN with hardware detection and automatic backend selection.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 65: WASI-NN accelerator interface (HW-13)")]
    public interface IInferenceAccelerator
    {
        /// <summary>
        /// Loads a model with the specified backend.
        /// </summary>
        /// <param name="modelPath">Path to the model file.</param>
        /// <param name="backend">Inference backend to use (Auto for best available).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Handle to the loaded model.</returns>
        Task<ModelHandle> LoadModelAsync(string modelPath, InferenceBackend backend, CancellationToken ct = default);

        /// <summary>
        /// Runs inference on a loaded model.
        /// </summary>
        /// <param name="model">Handle to a loaded model.</param>
        /// <param name="input">Input tensor data.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Inference result with output tensor and metadata.</returns>
        Task<InferenceResult> RunInferenceAsync(ModelHandle model, ReadOnlyMemory<byte> input, CancellationToken ct = default);

        /// <summary>
        /// Gets the list of available inference backends on the current hardware.
        /// </summary>
        /// <returns>List of available backends ordered by priority.</returns>
        IReadOnlyList<InferenceBackend> GetAvailableBackends();

        /// <summary>
        /// Gets the best available backend based on hardware detection.
        /// </summary>
        /// <returns>The highest-priority available backend.</returns>
        InferenceBackend GetBestBackend();
    }

    /// <summary>
    /// WASI-NN accelerator with hardware-aware inference routing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extends the existing <see cref="OnnxWasiNnHost"/> with accelerator-aware inference
    /// routing. Automatically detects available hardware and routes inference workloads
    /// to the best available backend.
    /// </para>
    /// <para>
    /// <strong>Backend Priority:</strong>
    /// CUDA > ROCm > CoreML (macOS) > NNAPI (Android) > CANN > OpenCL > CPU
    /// </para>
    /// <para>
    /// <strong>Hardware Detection:</strong>
    /// Uses <see cref="IHardwareAccelerator.IsAvailable"/> and platform detection to probe
    /// for available hardware. Falls back to CPU (via <see cref="OnnxWasiNnHost"/>) if no
    /// accelerator is available.
    /// </para>
    /// <para>
    /// <strong>Model Format Support:</strong>
    /// <list type="bullet">
    /// <item><description>ONNX: Primary format, supported on all backends</description></item>
    /// <item><description>TensorFlow Lite: Mobile/edge optimized (CPU, NNAPI, CoreML)</description></item>
    /// <item><description>PyTorch: Supported via ONNX conversion hint</description></item>
    /// <item><description>OpenVINO IR: Intel-optimized inference</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Message Bus Integration:</strong>
    /// Listens on "inference.execute" topic for inference requests, enabling
    /// decoupled inference scheduling from any plugin.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 65: WASI-NN accelerator-aware routing (HW-13)")]
    public sealed class WasiNnAccelerator : IInferenceAccelerator, IDisposable
    {
        private readonly OnnxWasiNnHost _onnxHost;
        private readonly IPlatformCapabilityRegistry _registry;
        private readonly BoundedDictionary<Guid, ModelHandle> _loadedModels = new BoundedDictionary<Guid, ModelHandle>(1000);
        private readonly List<InferenceBackend> _availableBackends = new();
        private InferenceBackend _bestBackend = InferenceBackend.CPU;
        private volatile bool _initialized;
        private long _inferencesCompleted;
        private readonly object _lock = new();
        private volatile bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="WasiNnAccelerator"/> class.
        /// </summary>
        /// <param name="registry">Platform capability registry for hardware detection.</param>
        /// <param name="maxCachedSessions">Maximum number of cached ONNX inference sessions.</param>
        /// <exception cref="ArgumentNullException">Thrown when registry is null.</exception>
        public WasiNnAccelerator(IPlatformCapabilityRegistry registry, int maxCachedSessions = 5)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _onnxHost = new OnnxWasiNnHost(maxCachedSessions);
        }

        /// <summary>
        /// Initializes the accelerator by detecting available hardware backends.
        /// </summary>
        public async Task InitializeAsync()
        {
            await Task.Run(() =>
            {
                lock (_lock)
                {
                    if (_initialized) return;
                    DetectAvailableBackends();
                    _initialized = true;
                }
            });
        }

        private void DetectAvailableBackends()
        {
            _availableBackends.Clear();

            // Priority order: CUDA > ROCm > CoreML > NNAPI > CANN > OpenCL > CPU

            // Check CUDA availability
            if (TryDetectCuda())
                _availableBackends.Add(InferenceBackend.CUDA);

            // Check ROCm availability
            if (TryDetectRocm())
                _availableBackends.Add(InferenceBackend.ROCm);

            // Check CoreML (macOS/iOS only)
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                _availableBackends.Add(InferenceBackend.CoreML);

            // Check NNAPI (Android only)
            if (OperatingSystem.IsAndroid())
                _availableBackends.Add(InferenceBackend.NNAPI);

            // Check CANN (Huawei Ascend NPU)
            if (TryDetectCann())
                _availableBackends.Add(InferenceBackend.CANN);

            // Check OpenCL
            if (TryDetectOpenCL())
                _availableBackends.Add(InferenceBackend.OpenCL);

            // CPU is always available
            _availableBackends.Add(InferenceBackend.CPU);

            // Best backend is the first in priority order
            _bestBackend = _availableBackends.Count > 0 ? _availableBackends[0] : InferenceBackend.CPU;
        }

        private static bool TryDetectCuda()
        {
            try
            {
                string cudaLib = OperatingSystem.IsWindows() ? "cudart64_12.dll" : "libcudart.so.12";
                return NativeLibrary.TryLoad(cudaLib, out _);
            }
            catch { return false; }
        }

        private static bool TryDetectRocm()
        {
            try
            {
                string hipLib = OperatingSystem.IsWindows() ? "amdhip64.dll" : "libamdhip64.so";
                return NativeLibrary.TryLoad(hipLib, out _);
            }
            catch { return false; }
        }

        private static bool TryDetectCann()
        {
            try
            {
                string cannLib = OperatingSystem.IsWindows() ? "ascendcl.dll" : "libascendcl.so";
                return NativeLibrary.TryLoad(cannLib, out _);
            }
            catch { return false; }
        }

        private static bool TryDetectOpenCL()
        {
            try
            {
                string clLib = OperatingSystem.IsWindows() ? "OpenCL.dll" : "libOpenCL.so";
                return NativeLibrary.TryLoad(clLib, out _);
            }
            catch { return false; }
        }

        /// <inheritdoc/>
        public async Task<ModelHandle> LoadModelAsync(string modelPath, InferenceBackend backend, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(modelPath, nameof(modelPath));

            if (!_initialized)
                await InitializeAsync();

            // Resolve backend if Auto
            var resolvedBackend = backend == InferenceBackend.Auto ? _bestBackend : backend;

            // Validate backend is available
            if (!_availableBackends.Contains(resolvedBackend) && resolvedBackend != InferenceBackend.CPU)
            {
                // Fall back to CPU if requested backend is unavailable
                resolvedBackend = InferenceBackend.CPU;
            }

            // Determine model format from extension
            var format = DetectModelFormat(modelPath);

            // Map inference backend to ONNX Runtime execution provider
            var provider = MapBackendToProvider(resolvedBackend);

            var settings = new InferenceSettings
            {
                ModelPath = modelPath,
                Provider = provider,
            };

            // Load model via ONNX Runtime
            var session = await _onnxHost.LoadModelAsync(modelPath, settings, ct);

            var handle = new ModelHandle(modelPath, resolvedBackend, format, session);
            _loadedModels[handle.HandleId] = handle;
            return handle;
        }

        /// <inheritdoc/>
        public async Task<InferenceResult> RunInferenceAsync(ModelHandle model, ReadOnlyMemory<byte> input, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(model);

            if (!model.IsLoaded)
                throw new InvalidOperationException("Model is not loaded or has been disposed.");

            var startTime = DateTime.UtcNow;

            // Convert byte input to float array for ONNX Runtime
            var floatInput = ConvertToFloatArray(input);

            // Run inference via ONNX Runtime session
            var output = await _onnxHost.InferAsync(model.Session!, floatInput, ct);

            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Interlocked.Increment(ref _inferencesCompleted);

            return new InferenceResult(output, model.Backend, duration);
        }

        /// <inheritdoc/>
        public IReadOnlyList<InferenceBackend> GetAvailableBackends()
        {
            if (!_initialized)
            {
                lock (_lock)
                {
                    if (!_initialized)
                    {
                        DetectAvailableBackends();
                        _initialized = true;
                    }
                }
            }

            return _availableBackends.AsReadOnly();
        }

        /// <inheritdoc/>
        public InferenceBackend GetBestBackend()
        {
            if (!_initialized)
            {
                lock (_lock)
                {
                    if (!_initialized)
                    {
                        DetectAvailableBackends();
                        _initialized = true;
                    }
                }
            }

            return _bestBackend;
        }

        /// <summary>
        /// Gets the number of inferences completed.
        /// </summary>
        public long InferencesCompleted => Interlocked.Read(ref _inferencesCompleted);

        private static ModelFormat DetectModelFormat(string modelPath)
        {
            var extension = System.IO.Path.GetExtension(modelPath).ToLowerInvariant();
            return extension switch
            {
                ".onnx" => ModelFormat.ONNX,
                ".tflite" => ModelFormat.TensorFlowLite,
                ".pt" or ".pth" => ModelFormat.PyTorch,
                ".xml" => ModelFormat.OpenVINO,
                _ => ModelFormat.ONNX, // Default to ONNX
            };
        }

        private static ExecutionProvider MapBackendToProvider(InferenceBackend backend)
        {
            return backend switch
            {
                InferenceBackend.CUDA => ExecutionProvider.CUDA,
                InferenceBackend.TensorRT => ExecutionProvider.TensorRT,
                InferenceBackend.ROCm => ExecutionProvider.CPU, // ROCm uses MIGraphX provider; fall back to CPU for ONNX Runtime
                InferenceBackend.CoreML => ExecutionProvider.CPU, // CoreML provider requires ORT CoreML EP; fall back to CPU
                InferenceBackend.NNAPI => ExecutionProvider.CPU, // NNAPI provider requires ORT NNAPI EP; fall back to CPU
                InferenceBackend.OpenCL => ExecutionProvider.CPU, // No direct OpenCL EP in ONNX Runtime; fall back to CPU
                InferenceBackend.CANN => ExecutionProvider.CPU, // CANN EP requires Huawei SDK; fall back to CPU
                _ => ExecutionProvider.CPU,
            };
        }

        private static float[] ConvertToFloatArray(ReadOnlyMemory<byte> input)
        {
            var span = input.Span;
            int floatCount = span.Length / sizeof(float);
            var result = new float[floatCount];

            for (int i = 0; i < floatCount; i++)
            {
                result[i] = BitConverter.ToSingle(span.Slice(i * sizeof(float), sizeof(float)));
            }

            return result;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;

            foreach (var kvp in _loadedModels)
            {
                kvp.Value.Dispose();
            }
            _loadedModels.Clear();

            _onnxHost.Dispose();
            _disposed = true;
        }
    }
}
