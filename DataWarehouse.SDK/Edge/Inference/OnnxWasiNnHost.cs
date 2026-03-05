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
        private readonly BoundedDictionary<string, OnnxInferenceSession> _sessionCache;
        private readonly int _maxCachedSessions;
        private readonly object _cacheLock = new();
        private volatile bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="OnnxWasiNnHost"/> class.
        /// </summary>
        /// <param name="maxCachedSessions">Maximum number of cached inference sessions.</param>
        public OnnxWasiNnHost(int maxCachedSessions = 5)
        {
            _maxCachedSessions = maxCachedSessions;
            _sessionCache = new BoundedDictionary<string, OnnxInferenceSession>(_maxCachedSessions);
        }

        /// <summary>
        /// Loads an ML model and creates an inference session.
        /// </summary>
        /// <remarks>
        /// Model loading involves disk I/O and CPU-intensive graph initialization.
        /// Work is offloaded to a thread-pool thread via <see cref="Task.Run"/> to avoid
        /// blocking the calling async context (finding P2-277).
        /// </remarks>
        public Task<IInferenceSession> LoadModelAsync(string modelPath, InferenceSettings settings, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(modelPath, nameof(modelPath));
            ArgumentNullException.ThrowIfNull(settings, nameof(settings));

            // Fast cache check before committing to Task.Run
            lock (_cacheLock)
            {
                if (_sessionCache.TryGetValue(modelPath, out var cached))
                    return Task.FromResult<IInferenceSession>(cached);
            }

            return Task.Run<IInferenceSession>(() =>
            {
                ct.ThrowIfCancellationRequested();

                // Create session options
                using var sessionOptions = new SessionOptions();

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

                // Load model (CPU/IO-intensive — runs on thread pool thread)
                var onnxSession = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath, sessionOptions);
                var session = new OnnxInferenceSession(modelPath, onnxSession);

                lock (_cacheLock)
                {
                    // Check again under lock in case another thread loaded the same model
                    if (_sessionCache.TryGetValue(modelPath, out var existing))
                    {
                        session.Dispose();
                        return existing;
                    }

                    // BoundedDictionary handles LRU eviction automatically; register eviction handler for disposal
                    _sessionCache.OnEvicted += (key, evicted) => evicted?.Dispose();

                    _sessionCache[modelPath] = session;
                }

                return session;
            }, ct);
        }

        /// <summary>
        /// Runs inference on a single input tensor.
        /// </summary>
        /// <remarks>
        /// ONNX inference is CPU-intensive synchronous work. It is offloaded to a thread-pool thread
        /// via <see cref="Task.Run"/> to avoid blocking the calling async context (finding P2-277).
        /// </remarks>
        public Task<float[]> InferAsync(IInferenceSession session, float[] input, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(session, nameof(session));
            ArgumentNullException.ThrowIfNull(input, nameof(input));

            if (input.Length == 0)
                throw new ArgumentException("Input array must not be empty.", nameof(input));

            if (session is not OnnxInferenceSession onnxInferenceSession)
                throw new ArgumentException(
                    $"Session must be an OnnxInferenceSession but was {session.GetType().Name}. " +
                    "Ensure the session was created by this OnnxWasiNnHost instance.",
                    nameof(session));

            if (session.InputNames == null || session.InputNames.Count == 0)
                throw new InvalidOperationException(
                    "ONNX model has no input names. The model may be malformed or not loaded correctly.");

            var onnxSession = onnxInferenceSession.UnderlyingSession;
            var inputName = session.InputNames[0];

            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                // Create input tensor (1D or 2D depending on model)
                var inputTensor = new DenseTensor<float>(input, new[] { 1, input.Length });
                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };

                // Run inference (CPU-intensive — runs on thread pool thread)
                using var results = onnxSession.Run(inputs);
                return results.First().AsEnumerable<float>().ToArray();
            }, ct);
        }

        /// <summary>
        /// Runs inference on a batch of input tensors.
        /// </summary>
        public async Task<float[][]> InferBatchAsync(IInferenceSession session, float[][] inputs, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(session, nameof(session));
            ArgumentNullException.ThrowIfNull(inputs, nameof(inputs));

            var results = new List<float[]>(inputs.Length);
            foreach (var input in inputs)
            {
                results.Add(await InferAsync(session, input, ct).ConfigureAwait(false));
            }
            return results.ToArray();
        }

        /// <summary>
        /// Disposes all cached inference sessions.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Take snapshot under lock to avoid concurrent modification
            List<OnnxInferenceSession> sessions;
            lock (_cacheLock)
            {
                sessions = _sessionCache.Values.ToList();
                _sessionCache.Clear();
            }

            foreach (var session in sessions)
                session?.Dispose();
        }
    }
}
