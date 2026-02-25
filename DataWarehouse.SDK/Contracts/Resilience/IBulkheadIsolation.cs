using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Resilience
{
    /// <summary>
    /// Contract for bulkhead isolation providing per-plugin resource partitioning (RESIL-02).
    /// Limits the number of concurrent operations a plugin can perform,
    /// preventing a single plugin from exhausting system resources.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public interface IBulkheadIsolation
    {
        /// <summary>
        /// Gets the name of this bulkhead (typically the plugin ID).
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Acquires a slot in the bulkhead. Dispose the returned lease to release the slot.
        /// Throws if no slots are available and the queue is full.
        /// </summary>
        /// <param name="ct">Cancellation token for the acquire operation.</param>
        /// <returns>A bulkhead lease that must be disposed to release the slot.</returns>
        Task<IBulkheadLease> AcquireAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets current bulkhead statistics.
        /// </summary>
        /// <returns>Current bulkhead statistics.</returns>
        BulkheadStatistics GetStatistics();

        /// <summary>
        /// Gets the configuration options for this bulkhead.
        /// </summary>
        BulkheadOptions Options { get; }
    }

    /// <summary>
    /// A lease representing a slot in the bulkhead.
    /// Disposing the lease releases the slot for other operations.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public interface IBulkheadLease : IAsyncDisposable
    {
        /// <summary>
        /// Whether this lease was successfully acquired.
        /// </summary>
        bool IsAcquired { get; }

        /// <summary>
        /// When this lease was acquired.
        /// </summary>
        DateTimeOffset AcquiredAt { get; }
    }

    /// <summary>
    /// Configuration options for a bulkhead.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record BulkheadOptions
    {
        /// <summary>
        /// Maximum number of concurrent operations. Default: 10.
        /// </summary>
        public int MaxConcurrency { get; init; } = 10;

        /// <summary>
        /// Maximum number of operations that can wait in the queue.
        /// 0 means reject immediately when full. Default: 0.
        /// </summary>
        public int MaxQueueSize { get; init; }

        /// <summary>
        /// Maximum time to wait in the queue before being rejected.
        /// Only applicable when MaxQueueSize > 0.
        /// </summary>
        public TimeSpan? QueueTimeout { get; init; }
    }

    /// <summary>
    /// Statistics for a bulkhead.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record BulkheadStatistics
    {
        /// <summary>
        /// The name of the bulkhead.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Current number of concurrent operations.
        /// </summary>
        public required int CurrentConcurrency { get; init; }

        /// <summary>
        /// Maximum concurrent operations allowed.
        /// </summary>
        public required int MaxConcurrency { get; init; }

        /// <summary>
        /// Current number of operations waiting in the queue.
        /// </summary>
        public required int QueueLength { get; init; }

        /// <summary>
        /// Maximum queue size.
        /// </summary>
        public required int MaxQueueSize { get; init; }

        /// <summary>
        /// Total number of operations executed.
        /// </summary>
        public required long TotalExecuted { get; init; }

        /// <summary>
        /// Total number of operations rejected.
        /// </summary>
        public required long TotalRejected { get; init; }
    }
}
