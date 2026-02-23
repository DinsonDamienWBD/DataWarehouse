using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace DataWarehouse.SDK.IO.DeterministicIo;

/// <summary>
/// Worst Case Execution Time annotation for safety-critical I/O operations.
/// Applied to methods that must complete within a bounded time for certification
/// compliance (DO-178C, IEC 61508, ISO 26262).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class WcetAnnotationAttribute : Attribute
{
    /// <summary>
    /// Maximum allowed execution time in milliseconds.
    /// </summary>
    public double MaxExecutionTimeMs { get; set; }

    /// <summary>
    /// Human-readable justification for the WCET bound.
    /// </summary>
    public string Justification { get; set; } = string.Empty;

    /// <summary>
    /// Certification reference (e.g., "DO-178C DAL-A", "IEC 61508 SIL-3").
    /// </summary>
    public string CertificationRef { get; set; } = string.Empty;

    /// <summary>
    /// Computed maximum execution time as a TimeSpan.
    /// </summary>
    public TimeSpan MaxExecutionTime => TimeSpan.FromMilliseconds(MaxExecutionTimeMs);
}

/// <summary>
/// Priority levels for I/O deadline scheduling.
/// </summary>
public enum IoDeadlinePriority
{
    /// <summary>Must complete or system faults. Highest priority.</summary>
    Critical = 0,

    /// <summary>High priority, should complete before normal operations.</summary>
    High = 1,

    /// <summary>Normal priority for standard I/O operations.</summary>
    Normal = 2,

    /// <summary>Best-effort, may be deferred or dropped under load.</summary>
    BestEffort = 3
}

/// <summary>
/// Represents an I/O operation deadline with priority and latency bounds.
/// </summary>
/// <param name="Deadline">Absolute deadline for operation completion.</param>
/// <param name="MaxLatency">Maximum allowed latency from submission to completion.</param>
/// <param name="Priority">Scheduling priority level.</param>
/// <param name="OperationId">Unique identifier for tracing this operation.</param>
public sealed record IoDeadline(
    DateTimeOffset Deadline,
    TimeSpan MaxLatency,
    IoDeadlinePriority Priority,
    string OperationId);

/// <summary>
/// Action to take when a deadline is missed.
/// </summary>
public enum DeadlineMissAction
{
    /// <summary>Log the miss but continue operation.</summary>
    Log = 0,

    /// <summary>Throw a <see cref="DeadlineMissException"/>.</summary>
    Throw = 1,

    /// <summary>Trip a circuit breaker to prevent cascading deadline misses.</summary>
    CircuitBreak = 2
}

/// <summary>
/// Configuration for deterministic I/O mode.
/// All defaults are tuned for general safety-critical use; override for specific certification levels.
/// </summary>
/// <param name="PreAllocatedBufferCount">Number of buffers to pre-allocate (default 1024).</param>
/// <param name="BufferSizeBytes">Size of each buffer in bytes (default 4096, matches VDE block size).</param>
/// <param name="DefaultDeadline">Default deadline for operations without explicit deadline (default 10ms).</param>
/// <param name="EnableWcetEnforcement">Whether to enforce WCET annotations at runtime (default true).</param>
/// <param name="FailOnDeadlineMiss">If true, throw on deadline miss; if false, log only (default false).</param>
/// <param name="DeadlineMissAction">Action to take when a deadline is missed (default Log).</param>
public sealed record DeterministicIoConfig(
    int PreAllocatedBufferCount = 1024,
    int BufferSizeBytes = 4096,
    TimeSpan? DefaultDeadline = null,
    bool EnableWcetEnforcement = true,
    bool FailOnDeadlineMiss = false,
    DeadlineMissAction DeadlineMissAction = DeadlineMissAction.Log)
{
    /// <summary>
    /// Effective default deadline (10ms if not specified).
    /// </summary>
    public TimeSpan EffectiveDefaultDeadline => DefaultDeadline ?? TimeSpan.FromMilliseconds(10);
}

/// <summary>
/// Runtime statistics for deterministic I/O operations.
/// </summary>
/// <param name="TotalOperations">Total number of I/O operations executed.</param>
/// <param name="DeadlineMisses">Number of operations that exceeded their deadline.</param>
/// <param name="P99LatencyMicroseconds">99th percentile latency in microseconds.</param>
/// <param name="MaxLatencyMicroseconds">Maximum observed latency in microseconds.</param>
/// <param name="ActiveBuffersInUse">Number of buffers currently rented.</param>
/// <param name="TotalBuffersAllocated">Total number of buffers in the pool.</param>
public sealed record DeterministicIoStats(
    long TotalOperations,
    long DeadlineMisses,
    double P99LatencyMicroseconds,
    double MaxLatencyMicroseconds,
    int ActiveBuffersInUse,
    int TotalBuffersAllocated);

/// <summary>
/// Safety certification traceability information.
/// Maps certification standards and levels to method-level traceability.
/// </summary>
/// <param name="Standard">Certification standard (e.g., "DO-178C", "IEC 61508", "ISO 26262").</param>
/// <param name="Level">Certification level (e.g., "DAL-A", "SIL-3", "ASIL-D").</param>
/// <param name="TraceabilityMatrix">Maps requirement IDs to method names for audit traceability.</param>
public sealed record SafetyCertificationInfo(
    string Standard,
    string Level,
    IReadOnlyDictionary<string, string> TraceabilityMatrix);

/// <summary>
/// Contract for deterministic bounded-latency I/O with WCET metadata.
/// All operations use pre-allocated buffers and are scheduled through an EDF deadline scheduler.
/// Designed for safety-critical environments requiring DO-178C, IEC 61508, or ISO 26262 compliance.
/// </summary>
public interface IDeterministicIoPath
{
    /// <summary>
    /// Reads a single block using a pre-allocated buffer with deadline enforcement.
    /// </summary>
    /// <param name="blockAddress">Block address to read.</param>
    /// <param name="deadline">Deadline and priority for this operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A pooled buffer containing the block data. Caller must dispose.</returns>
    [WcetAnnotation(MaxExecutionTimeMs = 10, Justification = "Single block read from pre-allocated pool")]
    ValueTask<PooledBuffer> ReadBlockDeterministicAsync(long blockAddress, IoDeadline deadline, CancellationToken ct = default);

    /// <summary>
    /// Writes a single block with deadline enforcement.
    /// </summary>
    /// <param name="blockAddress">Block address to write.</param>
    /// <param name="data">Data to write (must match buffer size).</param>
    /// <param name="deadline">Deadline and priority for this operation.</param>
    /// <param name="ct">Cancellation token.</param>
    [WcetAnnotation(MaxExecutionTimeMs = 10, Justification = "Single block write with pre-allocated buffer")]
    ValueTask WriteBlockDeterministicAsync(long blockAddress, ReadOnlyMemory<byte> data, IoDeadline deadline, CancellationToken ct = default);

    /// <summary>
    /// Reads a batch of contiguous blocks into a caller-provided buffer with deadline enforcement.
    /// </summary>
    /// <param name="startBlock">Starting block address.</param>
    /// <param name="count">Number of blocks to read.</param>
    /// <param name="destination">Destination buffer (must be at least count * block size bytes).</param>
    /// <param name="deadline">Deadline and priority for this operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of blocks actually read.</returns>
    [WcetAnnotation(MaxExecutionTimeMs = 50, Justification = "Batch block read bounded by count and pre-allocated pool")]
    ValueTask<int> ReadBatchDeterministicAsync(long startBlock, int count, Memory<byte> destination, IoDeadline deadline, CancellationToken ct = default);

    /// <summary>
    /// Gets runtime statistics for this deterministic I/O path.
    /// </summary>
    DeterministicIoStats GetStats();
}
