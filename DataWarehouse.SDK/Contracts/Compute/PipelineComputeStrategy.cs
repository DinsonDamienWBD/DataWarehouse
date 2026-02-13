using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Compute
{
    /// <summary>
    /// Pipeline compute strategy that can be integrated into Write/Read operations.
    /// Enables data processing ON-THE-FLY during ingestion/retrieval with intelligent
    /// fallback to deferred processing when compute cannot keep up with data velocity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This strategy implements adaptive routing between live compute (process immediately)
    /// and deferred compute (queue for background processing) based on real-time capacity monitoring.
    /// </para>
    /// <para>
    /// <b>USE CASE EXAMPLE:</b> Event Horizon Telescope processes 350TB/day from 8 telescopes.
    /// Live compute performs calibration and RFI flagging on-site, reducing data 100x before
    /// network transfer. Deferred compute at central facility performs final correlation.
    /// </para>
    /// </remarks>
    public interface IPipelineComputeStrategy
    {
        /// <summary>
        /// Unique identifier for this compute strategy (e.g., "anomaly-detection", "ml-inference").
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Human-readable display name (e.g., "Anomaly Detection", "ML Inference").
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Capabilities of this compute strategy.
        /// </summary>
        PipelineComputeCapabilities Capabilities { get; }

        /// <summary>
        /// Estimates throughput capacity for this compute strategy given current conditions.
        /// Used by adaptive router to decide between live and deferred processing.
        /// </summary>
        /// <param name="velocity">Current data velocity (throughput).</param>
        /// <param name="available">Available compute resources.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Throughput estimate indicating capacity headroom.</returns>
        Task<ThroughputEstimate> EstimateThroughputAsync(
            DataVelocity velocity,
            ComputeResources available,
            CancellationToken ct = default);

        /// <summary>
        /// Processes data during pipeline execution (live compute).
        /// Called when adaptive router determines sufficient capacity exists.
        /// </summary>
        /// <param name="input">Input data stream.</param>
        /// <param name="context">Pipeline compute context with configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Compute result including processed data and metrics.</returns>
        Task<PipelineComputeResult> ProcessAsync(
            Stream input,
            PipelineComputeContext context,
            CancellationToken ct = default);

        /// <summary>
        /// Processes data from storage (deferred compute).
        /// Called by background processor when capacity becomes available.
        /// </summary>
        /// <param name="objectId">Object identifier for stored data.</param>
        /// <param name="context">Pipeline compute context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Compute result.</returns>
        Task<PipelineComputeResult> ProcessDeferredAsync(
            string objectId,
            PipelineComputeContext context,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Capabilities of a pipeline compute strategy.
    /// </summary>
    public sealed record PipelineComputeCapabilities
    {
        /// <summary>
        /// Strategy can process data in streaming fashion (incremental/chunked).
        /// </summary>
        public bool SupportsStreaming { get; init; }

        /// <summary>
        /// Strategy can be parallelized across multiple threads/cores.
        /// </summary>
        public bool SupportsParallelization { get; init; }

        /// <summary>
        /// Strategy supports GPU acceleration.
        /// </summary>
        public bool SupportsGpuAcceleration { get; init; }

        /// <summary>
        /// Strategy supports distributed processing across multiple nodes.
        /// </summary>
        public bool SupportsDistributed { get; init; }

        /// <summary>
        /// Estimated compute intensity (0.0 = minimal CPU, 1.0 = CPU-intensive).
        /// </summary>
        public double ComputeIntensity { get; init; }

        /// <summary>
        /// Estimated memory usage per GB of input data.
        /// </summary>
        public double MemoryPerGb { get; init; }

        /// <summary>
        /// Whether strategy can operate on compressed data without decompression.
        /// </summary>
        public bool SupportsCompressedInput { get; init; }

        /// <summary>
        /// Whether strategy output can be appended incrementally (for streaming).
        /// </summary>
        public bool SupportsIncrementalOutput { get; init; }
    }

    /// <summary>
    /// Metrics describing data throughput and capacity.
    /// </summary>
    public sealed record ThroughputMetrics
    {
        /// <summary>
        /// Data velocity (bytes per second).
        /// </summary>
        public double Velocity { get; init; }

        /// <summary>
        /// Available compute capacity (operations per second or bytes per second).
        /// </summary>
        public double Capacity { get; init; }

        /// <summary>
        /// Backpressure indicator (0.0 = no pressure, 1.0 = at capacity, >1.0 = overloaded).
        /// </summary>
        public double Backpressure { get; init; }

        /// <summary>
        /// Capacity headroom as fraction (0.0 = no headroom, 1.0 = idle).
        /// </summary>
        public double HeadroomFraction => Capacity > 0 ? Math.Max(0, 1.0 - (Velocity / Capacity)) : 0.0;
    }

    /// <summary>
    /// Configuration for adaptive pipeline compute routing.
    /// </summary>
    public sealed record AdaptiveRouterConfig
    {
        /// <summary>
        /// Minimum headroom fraction required for full live compute (default: 0.8).
        /// </summary>
        public double LiveComputeMinHeadroom { get; init; } = 0.8;

        /// <summary>
        /// Minimum headroom fraction for partial live compute (default: 0.3).
        /// </summary>
        public double PartialComputeMinHeadroom { get; init; } = 0.3;

        /// <summary>
        /// Below this headroom, switch to emergency passthrough mode (default: 0.05).
        /// </summary>
        public double EmergencyPassthroughThreshold { get; init; } = 0.05;

        /// <summary>
        /// Maximum time to wait for deferred compute completion (default: 24 hours).
        /// </summary>
        public TimeSpan MaxDeferralTime { get; init; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Deadline for deferred compute processing (default: 48 hours).
        /// </summary>
        public TimeSpan ProcessingDeadline { get; init; } = TimeSpan.FromHours(48);

        /// <summary>
        /// Whether to enable predictive capacity scaling.
        /// </summary>
        public bool EnablePredictiveScaling { get; init; }

        /// <summary>
        /// Whether to process at edge before network transit (if applicable).
        /// </summary>
        public bool EnableEdgeCompute { get; init; }
    }

    /// <summary>
    /// How to handle compute output relative to raw data.
    /// </summary>
    public enum ComputeOutputMode
    {
        /// <summary>
        /// Replace raw data with processed results only.
        /// </summary>
        Replace,

        /// <summary>
        /// Store processed results alongside raw data (separate objects).
        /// </summary>
        Append,

        /// <summary>
        /// Store both raw and processed in same object (multi-part).
        /// </summary>
        Both,

        /// <summary>
        /// Decide based on compute result (e.g., store raw if anomaly detected).
        /// </summary>
        Conditional
    }

    /// <summary>
    /// Result of a pipeline compute operation.
    /// </summary>
    public sealed record PipelineComputeResult
    {
        /// <summary>
        /// Whether the compute operation succeeded.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Processed data stream (if applicable).
        /// </summary>
        public Stream? ProcessedData { get; init; }

        /// <summary>
        /// Number of bytes processed.
        /// </summary>
        public long BytesProcessed { get; init; }

        /// <summary>
        /// Number of records/items processed.
        /// </summary>
        public long? RecordsProcessed { get; init; }

        /// <summary>
        /// Time spent on compute operation.
        /// </summary>
        public TimeSpan ComputeTime { get; init; }

        /// <summary>
        /// Error message if operation failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Warnings encountered during processing.
        /// </summary>
        public IReadOnlyList<string>? Warnings { get; init; }

        /// <summary>
        /// Metrics from compute operation.
        /// </summary>
        public IDictionary<string, object>? Metrics { get; init; }

        /// <summary>
        /// Metadata extracted/generated during compute.
        /// </summary>
        public IDictionary<string, object>? Metadata { get; init; }

        /// <summary>
        /// Whether raw data should be retained (for Conditional output mode).
        /// </summary>
        public bool ShouldRetainRaw { get; init; }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        public static PipelineComputeResult Ok(Stream? processedData = null, long bytesProcessed = 0) => new()
        {
            Success = true,
            ProcessedData = processedData,
            BytesProcessed = bytesProcessed
        };

        /// <summary>
        /// Creates a failed result with error message.
        /// </summary>
        public static PipelineComputeResult Fail(string errorMessage) => new()
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Interface for deferred compute queue (background processing).
    /// </summary>
    public interface IDeferredComputeQueue
    {
        /// <summary>
        /// Enqueues an object for deferred processing.
        /// </summary>
        /// <param name="objectId">Object identifier.</param>
        /// <param name="strategyId">Compute strategy to apply.</param>
        /// <param name="context">Compute context.</param>
        /// <param name="priority">Processing priority.</param>
        /// <param name="deadline">Processing deadline (optional).</param>
        /// <param name="ct">Cancellation token.</param>
        Task EnqueueAsync(
            string objectId,
            string strategyId,
            PipelineComputeContext context,
            DeferredPriority priority = DeferredPriority.Normal,
            DateTime? deadline = null,
            CancellationToken ct = default);

        /// <summary>
        /// Dequeues the next item for processing.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Deferred compute item, or null if queue is empty.</returns>
        Task<DeferredComputeItem?> DequeueAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets the current queue depth.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Number of items in queue.</returns>
        Task<long> GetQueueDepthAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets queue statistics.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Queue statistics.</returns>
        Task<DeferredQueueStats> GetStatsAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for monitoring compute capacity.
    /// </summary>
    public interface IComputeCapacityMonitor
    {
        /// <summary>
        /// Gets current compute resources availability.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Available compute resources.</returns>
        Task<ComputeResources> GetAvailableResourcesAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets current data velocity.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current data velocity.</returns>
        Task<DataVelocity> GetDataVelocityAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets current throughput metrics.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Throughput metrics.</returns>
        Task<ThroughputMetrics> GetThroughputMetricsAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Adaptive routing decision based on current capacity.
    /// </summary>
    public enum AdaptiveRouteDecision
    {
        /// <summary>
        /// Full live compute - sufficient capacity available.
        /// </summary>
        LiveCompute,

        /// <summary>
        /// Partial live compute - do what we can, queue rest for deferred.
        /// </summary>
        PartialLiveWithDeferred,

        /// <summary>
        /// Store immediately, queue all compute for later processing.
        /// </summary>
        DeferredOnly,

        /// <summary>
        /// Emergency mode - store raw only, no compute queued.
        /// </summary>
        EmergencyPassthrough
    }

    /// <summary>
    /// Data velocity metrics (incoming data rate).
    /// </summary>
    public sealed record DataVelocity
    {
        /// <summary>
        /// Bytes per second.
        /// </summary>
        public double BytesPerSecond { get; init; }

        /// <summary>
        /// Records/items per second (if applicable).
        /// </summary>
        public double? RecordsPerSecond { get; init; }

        /// <summary>
        /// Peak velocity observed in recent window.
        /// </summary>
        public double PeakBytesPerSecond { get; init; }

        /// <summary>
        /// Average velocity over recent window.
        /// </summary>
        public double AverageBytesPerSecond { get; init; }

        /// <summary>
        /// Measurement window duration.
        /// </summary>
        public TimeSpan WindowDuration { get; init; }
    }

    /// <summary>
    /// Compute resources availability.
    /// </summary>
    public sealed record ComputeResources
    {
        /// <summary>
        /// Available CPU cores.
        /// </summary>
        public int AvailableCpuCores { get; init; }

        /// <summary>
        /// Total CPU cores.
        /// </summary>
        public int TotalCpuCores { get; init; }

        /// <summary>
        /// Available memory in bytes.
        /// </summary>
        public long AvailableMemoryBytes { get; init; }

        /// <summary>
        /// Total memory in bytes.
        /// </summary>
        public long TotalMemoryBytes { get; init; }

        /// <summary>
        /// GPU availability (0.0 = unavailable, 1.0 = fully available).
        /// </summary>
        public double? GpuAvailability { get; init; }

        /// <summary>
        /// Network bandwidth availability in bytes per second.
        /// </summary>
        public double? NetworkBandwidthBytesPerSecond { get; init; }

        /// <summary>
        /// Disk I/O bandwidth availability in bytes per second.
        /// </summary>
        public double? DiskBandwidthBytesPerSecond { get; init; }

        /// <summary>
        /// CPU utilization fraction (0.0 = idle, 1.0 = fully utilized).
        /// </summary>
        public double CpuUtilization => TotalCpuCores > 0 ? 1.0 - ((double)AvailableCpuCores / TotalCpuCores) : 1.0;

        /// <summary>
        /// Memory utilization fraction (0.0 = idle, 1.0 = fully utilized).
        /// </summary>
        public double MemoryUtilization => TotalMemoryBytes > 0 ? 1.0 - ((double)AvailableMemoryBytes / TotalMemoryBytes) : 1.0;
    }

    /// <summary>
    /// Throughput estimate for a compute strategy.
    /// </summary>
    public sealed record ThroughputEstimate
    {
        /// <summary>
        /// Estimated processing capacity in bytes per second.
        /// </summary>
        public double EstimatedCapacityBytesPerSecond { get; init; }

        /// <summary>
        /// Confidence in the estimate (0.0 = no confidence, 1.0 = high confidence).
        /// </summary>
        public double Confidence { get; init; }

        /// <summary>
        /// Whether the strategy can keep up with current data velocity.
        /// </summary>
        public bool CanKeepUp { get; init; }

        /// <summary>
        /// Estimated headroom fraction (0.0 = no headroom, 1.0 = idle).
        /// </summary>
        public double HeadroomFraction { get; init; }

        /// <summary>
        /// Recommended routing decision.
        /// </summary>
        public AdaptiveRouteDecision RecommendedDecision { get; init; }
    }

    /// <summary>
    /// Context for pipeline compute operations.
    /// </summary>
    public sealed class PipelineComputeContext
    {
        /// <summary>
        /// Object identifier (for deferred processing).
        /// </summary>
        public string? ObjectId { get; init; }

        /// <summary>
        /// Strategy-specific options.
        /// </summary>
        public IDictionary<string, object>? Options { get; init; }

        /// <summary>
        /// Output mode for compute results.
        /// </summary>
        public ComputeOutputMode OutputMode { get; init; } = ComputeOutputMode.Both;

        /// <summary>
        /// Maximum processing time allowed.
        /// </summary>
        public TimeSpan? MaxProcessingTime { get; init; }

        /// <summary>
        /// User-provided metadata.
        /// </summary>
        public IDictionary<string, object>? Metadata { get; init; }

        /// <summary>
        /// Whether to enable GPU acceleration (if supported).
        /// </summary>
        public bool EnableGpuAcceleration { get; init; }

        /// <summary>
        /// Maximum number of parallel threads to use.
        /// </summary>
        public int? MaxParallelism { get; init; }
    }

    /// <summary>
    /// Priority for deferred compute processing.
    /// </summary>
    public enum DeferredPriority
    {
        /// <summary>
        /// Low priority - process when idle.
        /// </summary>
        Low = 0,

        /// <summary>
        /// Normal priority - standard background processing.
        /// </summary>
        Normal = 1,

        /// <summary>
        /// High priority - process before normal items.
        /// </summary>
        High = 2,

        /// <summary>
        /// Critical priority - process immediately when capacity available.
        /// </summary>
        Critical = 3
    }

    /// <summary>
    /// Item in deferred compute queue.
    /// </summary>
    public sealed record DeferredComputeItem
    {
        /// <summary>
        /// Unique queue item identifier.
        /// </summary>
        public required string QueueItemId { get; init; }

        /// <summary>
        /// Object identifier to process.
        /// </summary>
        public required string ObjectId { get; init; }

        /// <summary>
        /// Compute strategy identifier.
        /// </summary>
        public required string StrategyId { get; init; }

        /// <summary>
        /// Compute context.
        /// </summary>
        public required PipelineComputeContext Context { get; init; }

        /// <summary>
        /// Processing priority.
        /// </summary>
        public DeferredPriority Priority { get; init; }

        /// <summary>
        /// When the item was enqueued.
        /// </summary>
        public DateTime EnqueuedAt { get; init; }

        /// <summary>
        /// Processing deadline (optional).
        /// </summary>
        public DateTime? Deadline { get; init; }

        /// <summary>
        /// Number of processing attempts.
        /// </summary>
        public int AttemptCount { get; init; }
    }

    /// <summary>
    /// Statistics for deferred compute queue.
    /// </summary>
    public sealed record DeferredQueueStats
    {
        /// <summary>
        /// Total items in queue.
        /// </summary>
        public long TotalItems { get; init; }

        /// <summary>
        /// Items by priority level.
        /// </summary>
        public required IDictionary<DeferredPriority, long> ItemsByPriority { get; init; }

        /// <summary>
        /// Average queue wait time.
        /// </summary>
        public TimeSpan AverageWaitTime { get; init; }

        /// <summary>
        /// Items approaching deadline.
        /// </summary>
        public long ItemsNearDeadline { get; init; }

        /// <summary>
        /// Items past deadline.
        /// </summary>
        public long ItemsPastDeadline { get; init; }

        /// <summary>
        /// Processing rate (items per second).
        /// </summary>
        public double ProcessingRate { get; init; }
    }

    /// <summary>
    /// Abstract base class for pipeline compute strategies.
    /// Provides infrastructure for throughput estimation and adaptive routing.
    /// </summary>
    public abstract class PipelineComputeStrategyBase : StrategyBase, IPipelineComputeStrategy
    {
        /// <inheritdoc/>
        public override abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string DisplayName { get; }

        /// <summary>
        /// Bridges DisplayName to the StrategyBase.Name contract.
        /// </summary>
        public override string Name => DisplayName;

        /// <inheritdoc/>
        public abstract PipelineComputeCapabilities Capabilities { get; }

        /// <inheritdoc/>
        public virtual async Task<ThroughputEstimate> EstimateThroughputAsync(
            DataVelocity velocity,
            ComputeResources available,
            CancellationToken ct = default)
        {
            if (velocity == null)
                throw new ArgumentNullException(nameof(velocity));
            if (available == null)
                throw new ArgumentNullException(nameof(available));

            // Default estimation logic - override for more sophisticated strategies
            var estimatedCapacity = EstimateCapacityCoreAsync(available, ct);
            var canKeepUp = estimatedCapacity >= velocity.BytesPerSecond;
            var headroom = estimatedCapacity > 0 ? Math.Max(0, 1.0 - (velocity.BytesPerSecond / estimatedCapacity)) : 0.0;

            var decision = headroom switch
            {
                >= 0.8 => AdaptiveRouteDecision.LiveCompute,
                >= 0.3 => AdaptiveRouteDecision.PartialLiveWithDeferred,
                >= 0.05 => AdaptiveRouteDecision.DeferredOnly,
                _ => AdaptiveRouteDecision.EmergencyPassthrough
            };

            return new ThroughputEstimate
            {
                EstimatedCapacityBytesPerSecond = estimatedCapacity,
                Confidence = 0.8, // Default confidence
                CanKeepUp = canKeepUp,
                HeadroomFraction = headroom,
                RecommendedDecision = decision
            };
        }

        /// <summary>
        /// Estimates processing capacity based on available resources.
        /// Override in derived classes for strategy-specific estimation.
        /// </summary>
        protected virtual double EstimateCapacityCoreAsync(ComputeResources available, CancellationToken ct)
        {
            // Simple estimation based on CPU and memory
            var cpuFactor = available.AvailableCpuCores * (1.0 - Capabilities.ComputeIntensity);
            var memoryFactor = available.AvailableMemoryBytes / (Capabilities.MemoryPerGb * 1024 * 1024 * 1024);

            // Estimate: 100 MB/s per core at low intensity, reduced by compute intensity
            return Math.Min(cpuFactor * 100_000_000, memoryFactor * 100_000_000);
        }

        /// <inheritdoc/>
        public abstract Task<PipelineComputeResult> ProcessAsync(
            Stream input,
            PipelineComputeContext context,
            CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<PipelineComputeResult> ProcessDeferredAsync(
            string objectId,
            PipelineComputeContext context,
            CancellationToken ct = default);
    }
}
