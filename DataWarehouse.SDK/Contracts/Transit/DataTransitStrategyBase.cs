using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts.Transit
{
    /// <summary>
    /// Statistics tracking for data transit operations.
    /// Used for monitoring, auditing, and performance analysis.
    /// </summary>
    public sealed record TransitStatistics
    {
        /// <summary>
        /// Total number of completed transfer operations.
        /// </summary>
        public long TransferCount { get; init; }

        /// <summary>
        /// Total bytes transferred across all operations.
        /// </summary>
        public long BytesTransferred { get; init; }

        /// <summary>
        /// Number of failed transfer operations.
        /// </summary>
        public long Failures { get; init; }

        /// <summary>
        /// Number of currently active transfers.
        /// </summary>
        public long ActiveTransfers { get; init; }

        /// <summary>
        /// Timestamp when statistics tracking started.
        /// </summary>
        public DateTime StartTime { get; init; }

        /// <summary>
        /// Timestamp of the last statistics update.
        /// </summary>
        public DateTime LastUpdateTime { get; init; }

        /// <summary>
        /// Creates an empty statistics snapshot.
        /// </summary>
        public static TransitStatistics Empty => new()
        {
            StartTime = DateTime.UtcNow,
            LastUpdateTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Abstract base class for data transit strategies.
    /// Provides common functionality for transfer ID generation, statistics tracking,
    /// progress reporting, cancellation management, and Intelligence integration.
    /// </summary>
    /// <remarks>
    /// All concrete transit strategies should inherit from this base class to get
    /// consistent behavior for transfer tracking, cancellation, and AI-enhanced features.
    /// Thread-safe for concurrent transfer operations.
    /// </remarks>
    public abstract class DataTransitStrategyBase : StrategyBase, IDataTransitStrategy
    {
        private long _transferCount;
        private long _bytesTransferred;
        private long _failures;
        private long _activeTransfers;
        private readonly DateTime _startTime;
        private long _lastUpdateTimeTicks; // Stored as UTC ticks for Interlocked thread-safety

        /// <summary>
        /// Thread-safe dictionary tracking active transfer cancellation tokens.
        /// Keyed by transfer ID.
        /// </summary>
        protected readonly BoundedDictionary<string, CancellationTokenSource> ActiveTransferCancellations = new BoundedDictionary<string, CancellationTokenSource>(1000);

        /// <summary>
        /// Initializes a new instance of the <see cref="DataTransitStrategyBase"/> class.
        /// </summary>
        protected DataTransitStrategyBase()
        {
            _startTime = DateTime.UtcNow;
            Interlocked.Exchange(ref _lastUpdateTimeTicks, DateTime.UtcNow.Ticks);
        }

        /// <inheritdoc/>
        public override abstract string StrategyId { get; }

        /// <inheritdoc/>
        public override abstract string Name { get; }

        /// <inheritdoc/>
        public abstract TransitCapabilities Capabilities { get; }

        /// <inheritdoc/>
        public abstract Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);

        /// <inheritdoc/>
        public virtual Task<TransitResult> ResumeTransferAsync(string transferId, IProgress<TransitProgress>? progress = null, CancellationToken ct = default)
        {
            if (!Capabilities.SupportsResumable)
            {
                throw new NotSupportedException($"Strategy '{StrategyId}' does not support resumable transfers.");
            }

            throw new NotSupportedException($"Strategy '{StrategyId}' has not implemented resume logic.");
        }

        /// <inheritdoc/>
        public virtual Task CancelTransferAsync(string transferId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transferId);

            if (ActiveTransferCancellations.TryRemove(transferId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public virtual Task<TransitHealthStatus> GetHealthAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new TransitHealthStatus
            {
                StrategyId = StrategyId,
                IsHealthy = true,
                LastCheckTime = DateTime.UtcNow,
                ActiveTransfers = (int)Interlocked.Read(ref _activeTransfers)
            });
        }

        /// <summary>
        /// Generates a unique transfer ID using the strategy ID and a GUID.
        /// Ensures no collision across strategies per research pitfall 6.
        /// </summary>
        /// <returns>A unique transfer identifier in the format "{StrategyId}-{guid}".</returns>
        protected string GenerateTransferId()
        {
            return $"{StrategyId}-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Records a successful transfer in the statistics.
        /// </summary>
        /// <param name="bytesTransferred">Number of bytes transferred.</param>
        protected void RecordTransferSuccess(long bytesTransferred)
        {
            Interlocked.Increment(ref _transferCount);
            Interlocked.Add(ref _bytesTransferred, bytesTransferred);
            Interlocked.Exchange(ref _lastUpdateTimeTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Records a failed transfer in the statistics.
        /// </summary>
        protected void RecordTransferFailure()
        {
            Interlocked.Increment(ref _failures);
            Interlocked.Exchange(ref _lastUpdateTimeTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Increments the active transfer count. Call when a transfer starts.
        /// </summary>
        protected void IncrementActiveTransfers()
        {
            Interlocked.Increment(ref _activeTransfers);
        }

        /// <summary>
        /// Decrements the active transfer count. Call when a transfer completes or fails.
        /// </summary>
        protected void DecrementActiveTransfers()
        {
            Interlocked.Decrement(ref _activeTransfers);
        }

        /// <summary>
        /// Gets the current transit statistics snapshot.
        /// </summary>
        /// <returns>A snapshot of the current statistics.</returns>
        public TransitStatistics GetStatistics()
        {
            return new TransitStatistics
            {
                TransferCount = Interlocked.Read(ref _transferCount),
                BytesTransferred = Interlocked.Read(ref _bytesTransferred),
                Failures = Interlocked.Read(ref _failures),
                ActiveTransfers = Interlocked.Read(ref _activeTransfers),
                StartTime = _startTime,
                LastUpdateTime = new DateTime(Interlocked.Read(ref _lastUpdateTimeTicks), DateTimeKind.Utc)
            };
        }

        /// <summary>
        /// Resets all statistics counters.
        /// </summary>
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _transferCount, 0);
            Interlocked.Exchange(ref _bytesTransferred, 0);
            Interlocked.Exchange(ref _failures, 0);
            Interlocked.Exchange(ref _activeTransfers, 0);
            Interlocked.Exchange(ref _lastUpdateTimeTicks, DateTime.UtcNow.Ticks);
        }
    }
}
