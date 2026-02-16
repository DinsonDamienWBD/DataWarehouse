using DataWarehouse.SDK.Contracts;
using Microsoft.ML.OnnxRuntime;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.Edge.Inference
{
    /// <summary>
    /// ONNX Runtime inference session implementation.
    /// </summary>
    /// <remarks>
    /// Wraps ONNX Runtime InferenceSession for ML model execution.
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: ONNX inference session (EDGE-04)")]
    internal sealed class OnnxInferenceSession : IInferenceSession
    {
        private readonly Microsoft.ML.OnnxRuntime.InferenceSession _session;

        /// <summary>Gets the model file path.</summary>
        public string ModelPath { get; }

        /// <summary>Gets the model input tensor names.</summary>
        public IReadOnlyList<string> InputNames { get; }

        /// <summary>Gets the model output tensor names.</summary>
        public IReadOnlyList<string> OutputNames { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OnnxInferenceSession"/> class.
        /// </summary>
        /// <param name="modelPath">Model file path.</param>
        /// <param name="session">ONNX Runtime inference session.</param>
        internal OnnxInferenceSession(string modelPath, Microsoft.ML.OnnxRuntime.InferenceSession session)
        {
            ModelPath = modelPath;
            _session = session;
            InputNames = session.InputMetadata.Keys.ToList();
            OutputNames = session.OutputMetadata.Keys.ToList();
        }

        /// <summary>
        /// Gets the underlying ONNX Runtime session.
        /// </summary>
        internal Microsoft.ML.OnnxRuntime.InferenceSession UnderlyingSession => _session;

        /// <summary>
        /// Disposes the inference session.
        /// </summary>
        public void Dispose() => _session?.Dispose();
    }
}
