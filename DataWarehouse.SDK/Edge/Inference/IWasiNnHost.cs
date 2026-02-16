using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Edge.Inference
{
    /// <summary>
    /// WASI-NN host interface for on-device ML inference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WASI-NN (WebAssembly System Interface for Neural Networks) provides a standard API
    /// for running ML models in WebAssembly environments. This interface adapts WASI-NN
    /// concepts for native .NET execution.
    /// </para>
    /// <para>
    /// <strong>Workflow</strong>:
    /// <list type="number">
    /// <item><description>Load model from file path -> creates IInferenceSession</description></item>
    /// <item><description>Run inference with input tensors -> returns output tensors</description></item>
    /// <item><description>Session is cached for reuse (avoids reload overhead)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: WASI-NN host interface (EDGE-04)")]
    public interface IWasiNnHost : IDisposable
    {
        /// <summary>
        /// Loads an ML model and creates an inference session.
        /// </summary>
        /// <param name="modelPath">File path to the ONNX model.</param>
        /// <param name="settings">Inference settings (execution provider, batch size, etc.).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Inference session for running predictions.</returns>
        Task<IInferenceSession> LoadModelAsync(string modelPath, InferenceSettings settings, CancellationToken ct = default);

        /// <summary>
        /// Runs inference on a single input tensor.
        /// </summary>
        /// <param name="session">Inference session.</param>
        /// <param name="input">Input tensor (flattened float array).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Output tensor (flattened float array).</returns>
        Task<float[]> InferAsync(IInferenceSession session, float[] input, CancellationToken ct = default);

        /// <summary>
        /// Runs inference on a batch of input tensors.
        /// </summary>
        /// <param name="session">Inference session.</param>
        /// <param name="inputs">Batch of input tensors.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Batch of output tensors.</returns>
        Task<float[][]> InferBatchAsync(IInferenceSession session, float[][] inputs, CancellationToken ct = default);
    }

    /// <summary>
    /// Inference session representing a loaded ML model.
    /// </summary>
    public interface IInferenceSession : IDisposable
    {
        /// <summary>Gets the model file path.</summary>
        string ModelPath { get; }

        /// <summary>Gets the model input tensor names.</summary>
        IReadOnlyList<string> InputNames { get; }

        /// <summary>Gets the model output tensor names.</summary>
        IReadOnlyList<string> OutputNames { get; }
    }
}
