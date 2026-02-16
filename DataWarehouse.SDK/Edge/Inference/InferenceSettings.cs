using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Edge.Inference
{
    /// <summary>
    /// Inference settings for ML model execution.
    /// </summary>
    /// <remarks>
    /// Configures model loading, execution provider (CPU/GPU), batch size, and session caching.
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: Inference settings (EDGE-04)")]
    public sealed record InferenceSettings
    {
        /// <summary>Gets or sets the file path to the ONNX model.</summary>
        public required string ModelPath { get; init; }

        /// <summary>Gets or sets the execution provider (CPU, CUDA, DirectML, TensorRT).</summary>
        public ExecutionProvider Provider { get; init; } = ExecutionProvider.CPU;

        /// <summary>Gets or sets the batch size for inference.</summary>
        public int BatchSize { get; init; } = 1;

        /// <summary>Gets or sets the maximum number of cached inference sessions.</summary>
        public int MaxCachedSessions { get; init; } = 5;
    }

    /// <summary>
    /// Execution provider for ML inference.
    /// </summary>
    public enum ExecutionProvider
    {
        /// <summary>CPU execution (portable, no special hardware required).</summary>
        CPU,

        /// <summary>NVIDIA CUDA GPU acceleration.</summary>
        CUDA,

        /// <summary>DirectML GPU acceleration (Windows, supports NVIDIA/AMD/Intel).</summary>
        DirectML,

        /// <summary>NVIDIA TensorRT optimized execution.</summary>
        TensorRT
    }
}
