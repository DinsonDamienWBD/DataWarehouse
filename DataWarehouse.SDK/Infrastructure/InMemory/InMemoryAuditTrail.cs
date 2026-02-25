using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Observability;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AuditEntry = DataWarehouse.SDK.Contracts.Observability.AuditEntry;
using AuditQuery = DataWarehouse.SDK.Contracts.Observability.AuditQuery;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory implementation of <see cref="IAuditTrail"/>.
    /// Provides an immutable, append-only audit trail using a concurrent queue.
    /// Bounded to prevent unbounded memory growth with oldest-first eviction.
    /// Production-ready for single-node deployments.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryAuditTrail : IAuditTrail
    {
        private readonly ConcurrentQueue<AuditEntry> _entries = new();
        private readonly int _maxCapacity;
        private long _count;

        /// <summary>
        /// Initializes a new in-memory audit trail.
        /// </summary>
        /// <param name="maxCapacity">Maximum number of entries. Default: 100,000.</param>
        public InMemoryAuditTrail(int maxCapacity = 100_000)
        {
            _maxCapacity = maxCapacity;
        }

        /// <inheritdoc />
        public event Action<AuditEntry>? OnAuditRecorded;

        /// <inheritdoc />
        public Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Evict oldest if at capacity
            while (Interlocked.Read(ref _count) >= _maxCapacity)
            {
                if (_entries.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _count);
                }
            }

            _entries.Enqueue(entry);
            Interlocked.Increment(ref _count);

            OnAuditRecorded?.Invoke(entry);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var result = _entries.AsEnumerable();

            if (query.Actor != null)
                result = result.Where(e => string.Equals(e.Actor, query.Actor, StringComparison.OrdinalIgnoreCase));
            if (query.Action != null)
                result = result.Where(e => string.Equals(e.Action, query.Action, StringComparison.OrdinalIgnoreCase));
            if (query.ResourceType != null)
                result = result.Where(e => string.Equals(e.ResourceType, query.ResourceType, StringComparison.OrdinalIgnoreCase));
            if (query.ResourceId != null)
                result = result.Where(e => string.Equals(e.ResourceId, query.ResourceId, StringComparison.Ordinal));
            if (query.From.HasValue)
                result = result.Where(e => e.Timestamp >= query.From.Value);
            if (query.To.HasValue)
                result = result.Where(e => e.Timestamp <= query.To.Value);
            if (query.Outcome.HasValue)
                result = result.Where(e => e.Outcome == query.Outcome.Value);

            var list = result.Take(query.MaxResults).ToList();
            return Task.FromResult<IReadOnlyList<AuditEntry>>(list);
        }

        /// <inheritdoc />
        public Task<long> GetCountAsync(AuditQuery? query = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (query == null)
            {
                return Task.FromResult(Interlocked.Read(ref _count));
            }

            var result = _entries.AsEnumerable();

            if (query.Actor != null)
                result = result.Where(e => string.Equals(e.Actor, query.Actor, StringComparison.OrdinalIgnoreCase));
            if (query.Action != null)
                result = result.Where(e => string.Equals(e.Action, query.Action, StringComparison.OrdinalIgnoreCase));
            if (query.ResourceType != null)
                result = result.Where(e => string.Equals(e.ResourceType, query.ResourceType, StringComparison.OrdinalIgnoreCase));
            if (query.ResourceId != null)
                result = result.Where(e => string.Equals(e.ResourceId, query.ResourceId, StringComparison.Ordinal));
            if (query.From.HasValue)
                result = result.Where(e => e.Timestamp >= query.From.Value);
            if (query.To.HasValue)
                result = result.Where(e => e.Timestamp <= query.To.Value);
            if (query.Outcome.HasValue)
                result = result.Where(e => e.Outcome == query.Outcome.Value);

            return Task.FromResult((long)result.Count());
        }
    }
}
