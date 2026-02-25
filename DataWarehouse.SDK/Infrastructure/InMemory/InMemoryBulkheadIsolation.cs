using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Resilience;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory implementation of <see cref="IBulkheadIsolation"/> using SemaphoreSlim.
    /// Limits the number of concurrent operations to prevent resource exhaustion.
    /// This is production-ready for single-node deployments.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryBulkheadIsolation : IBulkheadIsolation
    {
        private readonly SemaphoreSlim _semaphore;
        private long _totalExecuted;
        private long _totalRejected;

        /// <summary>
        /// Initializes a new bulkhead isolation instance.
        /// </summary>
        /// <param name="name">The name of this bulkhead (typically plugin ID).</param>
        /// <param name="options">Bulkhead options. Uses defaults if null.</param>
        public InMemoryBulkheadIsolation(string name, BulkheadOptions? options = null)
        {
            Name = name;
            Options = options ?? new BulkheadOptions();
            _semaphore = new SemaphoreSlim(Options.MaxConcurrency, Options.MaxConcurrency);
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public BulkheadOptions Options { get; }

        /// <inheritdoc />
        public async Task<IBulkheadLease> AcquireAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            bool acquired;
            if (Options.QueueTimeout.HasValue)
            {
                acquired = await _semaphore.WaitAsync(Options.QueueTimeout.Value, ct);
            }
            else if (Options.MaxQueueSize == 0)
            {
                acquired = _semaphore.Wait(0);
            }
            else
            {
                await _semaphore.WaitAsync(ct);
                acquired = true;
            }

            if (!acquired)
            {
                Interlocked.Increment(ref _totalRejected);
                throw new BulkheadRejectedException(Name);
            }

            Interlocked.Increment(ref _totalExecuted);
            return new InMemoryBulkheadLease(_semaphore);
        }

        /// <inheritdoc />
        public BulkheadStatistics GetStatistics() => new()
        {
            Name = Name,
            CurrentConcurrency = Options.MaxConcurrency - _semaphore.CurrentCount,
            MaxConcurrency = Options.MaxConcurrency,
            QueueLength = 0,
            MaxQueueSize = Options.MaxQueueSize,
            TotalExecuted = Interlocked.Read(ref _totalExecuted),
            TotalRejected = Interlocked.Read(ref _totalRejected)
        };

        private sealed class InMemoryBulkheadLease : IBulkheadLease
        {
            private readonly SemaphoreSlim _semaphore;
            private int _disposed;

            public InMemoryBulkheadLease(SemaphoreSlim semaphore)
            {
                _semaphore = semaphore;
                IsAcquired = true;
                AcquiredAt = DateTimeOffset.UtcNow;
            }

            public bool IsAcquired { get; }
            public DateTimeOffset AcquiredAt { get; }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
                {
                    _semaphore.Release();
                }
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Exception thrown when a bulkhead rejects a request due to full capacity.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class BulkheadRejectedException : InvalidOperationException
    {
        /// <summary>
        /// Gets the name of the bulkhead that rejected the request.
        /// </summary>
        public string BulkheadName { get; }

        /// <summary>
        /// Initializes a new bulkhead rejected exception.
        /// </summary>
        /// <param name="bulkheadName">The name of the rejecting bulkhead.</param>
        public BulkheadRejectedException(string bulkheadName)
            : base($"Bulkhead '{bulkheadName}' is at capacity and rejecting requests.")
        {
            BulkheadName = bulkheadName;
        }
    }
}
