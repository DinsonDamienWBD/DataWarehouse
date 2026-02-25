namespace DataWarehouse.SDK.Primitives.Performance;

/// <summary>
/// Defines the pooling policy for object reuse.
/// </summary>
/// <param name="MinPoolSize">Minimum number of objects to keep in the pool.</param>
/// <param name="MaxPoolSize">Maximum number of objects to keep in the pool.</param>
/// <param name="PrewarmCount">Number of objects to create upfront during initialization.</param>
/// <param name="EvictionTimeoutMs">Time in milliseconds before an idle object is evicted (0 = never).</param>
public record PoolingPolicy(
    int MinPoolSize = 0,
    int MaxPoolSize = 100,
    int PrewarmCount = 0,
    int EvictionTimeoutMs = 0);

/// <summary>
/// Configuration options for batch processing operations.
/// </summary>
/// <param name="MaxBatchSize">Maximum number of items to process in a single batch.</param>
/// <param name="MaxDelayMs">Maximum time in milliseconds to wait before processing a partial batch.</param>
/// <param name="ConcurrentBatches">Maximum number of batches to process concurrently.</param>
/// <param name="RetryOnFailure">If true, retry failed batches.</param>
/// <param name="MaxRetries">Maximum number of retry attempts for failed batches.</param>
public record BatchOptions(
    int MaxBatchSize = 100,
    int MaxDelayMs = 1000,
    int ConcurrentBatches = 1,
    bool RetryOnFailure = false,
    int MaxRetries = 3);

/// <summary>
/// Represents performance metrics for an operation.
/// </summary>
/// <param name="OperationName">Name of the operation being measured.</param>
/// <param name="StartTime">Operation start timestamp.</param>
/// <param name="EndTime">Operation end timestamp.</param>
/// <param name="DurationMs">Total duration in milliseconds.</param>
/// <param name="BytesProcessed">Number of bytes processed (if applicable).</param>
/// <param name="ItemsProcessed">Number of items processed.</param>
/// <param name="ThroughputMBps">Throughput in megabytes per second (if applicable).</param>
/// <param name="AllocatedBytes">Number of bytes allocated during the operation.</param>
/// <param name="GCCollections">Number of garbage collections triggered (by generation).</param>
public record PerformanceMetrics(
    string OperationName,
    DateTime StartTime,
    DateTime EndTime,
    double DurationMs,
    long BytesProcessed = 0,
    long ItemsProcessed = 0,
    double ThroughputMBps = 0,
    long AllocatedBytes = 0,
    Dictionary<int, int>? GCCollections = null);

/// <summary>
/// Represents a latency measurement bucket for histogram tracking.
/// </summary>
/// <param name="LowerBoundMs">Lower bound of the latency bucket in milliseconds.</param>
/// <param name="UpperBoundMs">Upper bound of the latency bucket in milliseconds.</param>
/// <param name="Count">Number of operations that fell into this bucket.</param>
/// <param name="Percentage">Percentage of total operations in this bucket.</param>
public record LatencyBucket(
    double LowerBoundMs,
    double UpperBoundMs,
    long Count,
    double Percentage);

/// <summary>
/// Aggregate latency statistics for performance analysis.
/// </summary>
/// <param name="OperationName">Name of the operation.</param>
/// <param name="TotalOperations">Total number of operations measured.</param>
/// <param name="MinLatencyMs">Minimum latency observed.</param>
/// <param name="MaxLatencyMs">Maximum latency observed.</param>
/// <param name="MeanLatencyMs">Mean (average) latency.</param>
/// <param name="MedianLatencyMs">Median latency (50th percentile).</param>
/// <param name="P95LatencyMs">95th percentile latency.</param>
/// <param name="P99LatencyMs">99th percentile latency.</param>
/// <param name="StandardDeviation">Standard deviation of latencies.</param>
/// <param name="Buckets">Histogram buckets for latency distribution.</param>
public record LatencyStatistics(
    string OperationName,
    long TotalOperations,
    double MinLatencyMs,
    double MaxLatencyMs,
    double MeanLatencyMs,
    double MedianLatencyMs,
    double P95LatencyMs,
    double P99LatencyMs,
    double StandardDeviation,
    IReadOnlyList<LatencyBucket>? Buckets = null);

/// <summary>
/// Configuration for performance monitoring and optimization.
/// </summary>
/// <param name="EnableMetrics">If true, collect detailed performance metrics.</param>
/// <param name="EnableLatencyHistogram">If true, maintain latency histograms.</param>
/// <param name="EnableMemoryTracking">If true, track memory allocations and GC.</param>
/// <param name="SamplingRate">Sampling rate for metrics (1.0 = 100%, 0.1 = 10%).</param>
/// <param name="MetricsFlushIntervalMs">Interval to flush metrics to storage.</param>
public record PerformanceConfig(
    bool EnableMetrics = true,
    bool EnableLatencyHistogram = false,
    bool EnableMemoryTracking = false,
    double SamplingRate = 1.0,
    int MetricsFlushIntervalMs = 60000);

/// <summary>
/// Result of a performance-optimized operation with metrics.
/// </summary>
/// <typeparam name="T">The type of the result data.</typeparam>
/// <param name="Data">The result data.</param>
/// <param name="Metrics">Performance metrics for the operation.</param>
/// <param name="WasOptimized">True if performance optimizations were applied.</param>
/// <param name="OptimizationDetails">Details about optimizations applied.</param>
public record OptimizedResult<T>(
    T Data,
    PerformanceMetrics Metrics,
    bool WasOptimized,
    string? OptimizationDetails = null);
