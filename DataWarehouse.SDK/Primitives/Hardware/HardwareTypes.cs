namespace DataWarehouse.SDK.Primitives.Hardware;

/// <summary>
/// Represents the type of hardware accelerator available.
/// </summary>
public enum AcceleratorType
{
    /// <summary>No hardware acceleration available (fallback to software).</summary>
    None = 0,

    /// <summary>CPU SIMD instructions (SSE, SSE2, SSE3, SSE4).</summary>
    SIMD = 1,

    /// <summary>Intel AVX2 (Advanced Vector Extensions 2) instructions.</summary>
    AVX2 = 2,

    /// <summary>Intel AVX-512 instructions for high-performance vectorization.</summary>
    AVX512 = 3,

    /// <summary>ARM NEON SIMD instructions.</summary>
    NEON = 4,

    /// <summary>NVIDIA CUDA-capable GPU acceleration.</summary>
    GPU_CUDA = 100,

    /// <summary>OpenCL-compatible GPU or accelerator.</summary>
    GPU_OpenCL = 101,

    /// <summary>Vulkan Compute API for GPU acceleration.</summary>
    GPU_Vulkan = 102,

    /// <summary>DirectCompute (DirectX compute shaders) for GPU acceleration.</summary>
    GPU_DirectCompute = 103,

    /// <summary>Apple Metal GPU acceleration.</summary>
    GPU_Metal = 104,

    /// <summary>FPGA (Field-Programmable Gate Array) accelerator.</summary>
    FPGA = 200,

    /// <summary>TPU (Tensor Processing Unit) for machine learning workloads.</summary>
    TPU = 201,

    /// <summary>Custom or vendor-specific accelerator.</summary>
    Custom = 1000
}

/// <summary>
/// Describes the capabilities of a hardware accelerator.
/// </summary>
/// <param name="Type">The type of accelerator.</param>
/// <param name="Name">Human-readable name of the accelerator (e.g., "NVIDIA GeForce RTX 3080").</param>
/// <param name="ComputeUnits">Number of compute units (cores, SMs, CUs, etc.).</param>
/// <param name="MemoryBytes">Available memory in bytes for acceleration operations.</param>
/// <param name="MaxBatchSize">Maximum batch size that can be processed in a single call.</param>
/// <param name="SupportsAsync">True if the accelerator supports asynchronous operations.</param>
/// <param name="SupportsDoublePrecision">True if the accelerator supports 64-bit floating point.</param>
/// <param name="VendorInfo">Optional vendor-specific information.</param>
public record HardwareCapabilities(
    AcceleratorType Type,
    string Name,
    int ComputeUnits,
    long MemoryBytes,
    int MaxBatchSize,
    bool SupportsAsync,
    bool SupportsDoublePrecision,
    Dictionary<string, string>? VendorInfo = null);

/// <summary>
/// Provides hints to the hardware accelerator about the nature of the operation.
/// </summary>
/// <remarks>
/// These hints help the accelerator choose the most efficient execution strategy.
/// The accelerator may ignore hints if they are not applicable.
/// </remarks>
public enum AccelerationHint
{
    /// <summary>Automatically determine the best acceleration strategy.</summary>
    Auto = 0,

    /// <summary>The operation is compute-intensive (e.g., mathematical calculations).</summary>
    ComputeBound = 1,

    /// <summary>The operation is memory-intensive (e.g., large data transfers).</summary>
    MemoryBound = 2,

    /// <summary>The operation involves sequential data access patterns.</summary>
    Sequential = 3,

    /// <summary>The operation involves random data access patterns.</summary>
    Random = 4,

    /// <summary>The operation is suitable for vectorization (SIMD-friendly).</summary>
    Vectorizable = 5,

    /// <summary>The operation requires high precision (use double precision if available).</summary>
    HighPrecision = 6,

    /// <summary>The operation can tolerate lower precision for better performance.</summary>
    LowPrecision = 7,

    /// <summary>The operation involves cryptographic calculations.</summary>
    Cryptographic = 8,

    /// <summary>The operation involves compression or decompression.</summary>
    Compression = 9,

    /// <summary>The operation involves image or video processing.</summary>
    ImageProcessing = 10,

    /// <summary>The operation involves machine learning inference.</summary>
    MachineLearning = 11
}

/// <summary>
/// Represents the result of a hardware acceleration operation with performance metrics.
/// </summary>
/// <typeparam name="T">The type of the result data.</typeparam>
/// <param name="Data">The result data.</param>
/// <param name="AcceleratorUsed">The type of accelerator that was used.</param>
/// <param name="ExecutionTimeMs">Execution time in milliseconds.</param>
/// <param name="WasFallback">True if the operation fell back to software execution.</param>
public record AccelerationResult<T>(
    T Data,
    AcceleratorType AcceleratorUsed,
    double ExecutionTimeMs,
    bool WasFallback);

/// <summary>
/// Configuration options for hardware acceleration.
/// </summary>
/// <param name="PreferredAccelerator">Preferred accelerator type (null for auto-select).</param>
/// <param name="AllowFallback">If true, fall back to software if hardware acceleration fails.</param>
/// <param name="MaxMemoryBytes">Maximum memory to use for acceleration (null for no limit).</param>
/// <param name="EnableProfiling">If true, collect detailed performance metrics.</param>
public record AccelerationConfig(
    AcceleratorType? PreferredAccelerator = null,
    bool AllowFallback = true,
    long? MaxMemoryBytes = null,
    bool EnableProfiling = false);
