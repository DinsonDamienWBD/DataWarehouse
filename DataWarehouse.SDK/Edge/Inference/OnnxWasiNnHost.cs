using DataWarehouse.SDK.Contracts;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Edge.Inference
{
    /// <summary>
    /// WASI-NN host implementation using ONNX Runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps Microsoft.ML.OnnxRuntime for on-device ML inference with:
    /// <list type="bullet">
    /// <item><description>Model loading from file paths</description></item>
    /// <item><description>Session caching (avoids reload overhead)</description></item>
    /// <item><description>Execution provider selection (CPU, CUDA, DirectML, TensorRT)</description></item>
    /// <item><description>Single and batch inference</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Execution Providers</strong>:
    /// <list type="bullet">
    /// <item><description><strong>CPU</strong>: Always available. Portable across all platforms.</description></item>
    /// <item><description><strong>CUDA</strong>: NVIDIA GPU acceleration. Requires CUDA toolkit.</description></item>
    /// <item><description><strong>DirectML</strong>: Windows GPU acceleration (NVIDIA/AMD/Intel). Requires Windows 10+.</description></item>
    /// <item><description><strong>TensorRT</strong>: NVIDIA GPU optimization. Requires TensorRT SDK.</description></item>
    /// </list>
    /// If a GPU provider is requested but unavailable, ONNX Runtime falls back to CPU.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: ONNX Runtime WASI-NN host (EDGE-04)")]
    public sealed class OnnxWasiNnHost : IWasiNnHost
    {
        private readonly BoundedDictionary<string, OnnxInferenceSession> _sessionCache = new BoundedDictionary<string, OnnxInferenceSession>(1000);
        private readonly int _maxCachedSessions;

        /// <summary>
        /// Initializes a new instance of the <see cref="OnnxWasiNnHost"/> class.
        /// </summary>
        /// <param name="maxCachedSessions">Maximum number of cached inference sessions.</param>
        public OnnxWasiNnHost(int maxCachedSessions = 5)
        {
            _maxCachedSessions = maxCachedSessions;
        }

        /// <summary>
        /// Loads an ML model and creates an inference session.
        /// </summary>
        public async Task<IInferenceSession> LoadModelAsync(string modelPath, InferenceSettings settings, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(modelPath, nameof(modelPath));
            ArgumentNullException.ThrowIfNull(settings, nameof(settings));

            // Check cache
            if (_sessionCache.TryGetValue(modelPath, out var cached))
                return cached;

            // Create session options
            var sessionOptions = new SessionOptions();

            // Configure execution provider
            switch (settings.Provider)
            {
                case ExecutionProvider.CUDA:
                    sessionOptions.AppendExecutionProvider_CUDA();
                    break;
                case ExecutionProvider.DirectML:
                    sessionOptions.AppendExecutionProvider_DML();
                    break;
                case ExecutionProvider.TensorRT:
                    sessionOptions.AppendExecutionProvider_Tensorrt();
                    break;
                default:
                    sessionOptions.AppendExecutionProvider_CPU();
                    break;
            }

            // Load model (wrap in try/catch to prevent session leak on failure)
            Microsoft.ML.OnnxRuntime.InferenceSession onnxSession;
            try
            {
                onnxSession = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath, sessionOptions);
            }
            catch
            {
                sessionOptions.Dispose();
                throw;
            }
            var session = new OnnxInferenceSession(modelPath, onnxSession);

            // Evict oldest session if cache is full
            if (_sessionCache.Count >= _maxCachedSessions)
            {
                var oldest = _sessionCache.First();
                _sessionCache.TryRemove(oldest.Key, out var removed);
                removed?.Dispose();
            }

            // Cache session
            _sessionCache[modelPath] = session;
            return await Task.FromResult<IInferenceSession>(session);
        }

        /// <summary>
        /// Runs inference on a single input tensor.
        /// </summary>
        public async Task<float[]> InferAsync(IInferenceSession session, float[] input, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(session, nameof(session));
            ArgumentNullException.ThrowIfNull(input, nameof(input));

            var onnxSession = ((OnnxInferenceSession)session).UnderlyingSession;
            var inputName = session.InputNames[0];

            // Create input tensor (1D or 2D depending on model)
            var inputTensor = new DenseTensor<float>(input, new[] { 1, input.Length });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };

            // Run inference
            using var results = onnxSession.Run(inputs);
            var output = results.First().AsEnumerable<float>().ToArray();

            return await Task.FromResult(output);
        }

        /// <summary>
        /// Runs inference on a batch of input tensors.
        /// </summary>
        public async Task<float[][]> InferBatchAsync(IInferenceSession session, float[][] inputs, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(session, nameof(session));
            ArgumentNullException.ThrowIfNull(inputs, nameof(inputs));

            // Simplified batch implementation: run each input sequentially
            // Production optimization: use ONNX Runtime's native batch support
            var results = new List<float[]>();
            foreach (var input in inputs)
            {
                results.Add(await InferAsync(session, input, ct));
            }
            return results.ToArray();
        }

        /// <summary>
        /// Disposes all cached inference sessions.
        /// </summary>
        public void Dispose()
        {
            foreach (var session in _sessionCache.Values)
                session?.Dispose();
            _sessionCache.Clear();
        }
    }
}
