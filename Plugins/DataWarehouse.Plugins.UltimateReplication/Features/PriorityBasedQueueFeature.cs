using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Features
{
    /// <summary>
    /// Priority-Based Queue Feature (C4).
    /// Manages replication operations through a multi-level priority queue with
    /// critical, high, normal, low, and background priority levels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queue processing ensures higher-priority items are always processed first.
    /// Background items are processed only when the queue is otherwise empty.
    /// Starvation prevention promotes items that have waited beyond a configurable threshold.
    /// </para>
    /// </remarks>
    public sealed class PriorityBasedQueueFeature : IDisposable
    {
        private readonly ReplicationStrategyRegistry _registry;
        private readonly IMessageBus _messageBus;
        private readonly BoundedDictionary<ReplicationPriority, ConcurrentQueue<QueuedReplication>> _queues;
        private readonly BoundedDictionary<string, QueuedReplication> _itemIndex = new BoundedDictionary<string, QueuedReplication>(1000);
        private readonly TimeSpan _starvationThreshold;
        private readonly int _maxQueueDepth;
        private bool _disposed;
        private IDisposable? _subscription;

        // Topics
        private const string EnqueueTopic = "replication.ultimate.queue.enqueue";
        private const string DequeueTopic = "replication.ultimate.queue.dequeue";
        private const string QueueStatusTopic = "replication.ultimate.queue.status";

        // Statistics
        private long _totalEnqueued;
        private long _totalDequeued;
        private long _totalPromoted;
        private long _totalDropped;

        /// <summary>
        /// Initializes a new instance of the PriorityBasedQueueFeature.
        /// </summary>
        /// <param name="registry">The replication strategy registry.</param>
        /// <param name="messageBus">Message bus for communication.</param>
        /// <param name="starvationThreshold">Time after which lower-priority items are promoted. Defaults to 5 minutes.</param>
        /// <param name="maxQueueDepth">Maximum items per priority level. Defaults to 10000.</param>
        public PriorityBasedQueueFeature(
            ReplicationStrategyRegistry registry,
            IMessageBus messageBus,
            TimeSpan? starvationThreshold = null,
            int maxQueueDepth = 10000)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _starvationThreshold = starvationThreshold ?? TimeSpan.FromMinutes(5);
            _maxQueueDepth = maxQueueDepth;

            _queues = new BoundedDictionary<ReplicationPriority, ConcurrentQueue<QueuedReplication>>(1000);
            foreach (var priority in Enum.GetValues<ReplicationPriority>())
            {
                _queues[priority] = new ConcurrentQueue<QueuedReplication>();
            }

            _subscription = _messageBus.Subscribe(EnqueueTopic, HandleEnqueueMessageAsync);
        }

        /// <summary>Gets total items enqueued.</summary>
        public long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);

        /// <summary>Gets total items dequeued.</summary>
        public long TotalDequeued => Interlocked.Read(ref _totalDequeued);

        /// <summary>Gets total items promoted due to starvation prevention.</summary>
        public long TotalPromoted => Interlocked.Read(ref _totalPromoted);

        /// <summary>
        /// Enqueues a replication operation with the specified priority.
        /// </summary>
        /// <param name="item">The replication item to enqueue.</param>
        /// <returns>True if enqueued, false if queue is full.</returns>
        public bool Enqueue(QueuedReplication item)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (_queues[item.Priority].Count >= _maxQueueDepth)
            {
                Interlocked.Increment(ref _totalDropped);
                return false;
            }

            item.EnqueuedAt = DateTimeOffset.UtcNow;
            _queues[item.Priority].Enqueue(item);
            _itemIndex[item.ItemId] = item;
            Interlocked.Increment(ref _totalEnqueued);
            return true;
        }

        /// <summary>
        /// Dequeues the next highest-priority replication operation.
        /// Applies starvation prevention before dequeuing.
        /// </summary>
        /// <returns>The next item to process, or null if all queues are empty.</returns>
        public QueuedReplication? Dequeue()
        {
            ApplyStarvationPrevention();

            // Dequeue from highest priority first
            foreach (var priority in Enum.GetValues<ReplicationPriority>().OrderBy(p => (int)p))
            {
                if (_queues[priority].TryDequeue(out var item))
                {
                    item.DequeuedAt = DateTimeOffset.UtcNow;
                    _itemIndex.TryRemove(item.ItemId, out _);
                    Interlocked.Increment(ref _totalDequeued);
                    return item;
                }
            }

            return null;
        }

        /// <summary>
        /// Dequeues up to <paramref name="count"/> items, respecting priority ordering.
        /// </summary>
        /// <param name="count">Maximum number of items to dequeue.</param>
        /// <returns>Dequeued items in priority order.</returns>
        public IReadOnlyList<QueuedReplication> DequeueBatch(int count)
        {
            var batch = new List<QueuedReplication>(count);
            for (int i = 0; i < count; i++)
            {
                var item = Dequeue();
                if (item == null) break;
                batch.Add(item);
            }
            return batch;
        }

        /// <summary>
        /// Gets the current queue depth for each priority level.
        /// </summary>
        public IReadOnlyDictionary<ReplicationPriority, int> GetQueueDepths()
        {
            return _queues.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }

        /// <summary>
        /// Gets the total number of items across all queues.
        /// </summary>
        public int TotalQueueDepth => _queues.Values.Sum(q => q.Count);

        /// <summary>
        /// Gets estimated wait time for an item at a given priority.
        /// </summary>
        /// <param name="priority">The priority level.</param>
        /// <returns>Estimated wait time based on current queue depth.</returns>
        public TimeSpan EstimateWaitTime(ReplicationPriority priority)
        {
            var higherPriorityItems = _queues
                .Where(kv => (int)kv.Key < (int)priority)
                .Sum(kv => kv.Value.Count);

            var samePriorityItems = _queues[priority].Count;
            var totalAhead = higherPriorityItems + samePriorityItems;

            // Estimate 100ms per item processing
            return TimeSpan.FromMilliseconds(totalAhead * 100);
        }

        #region Private Methods

        private void ApplyStarvationPrevention()
        {
            var now = DateTimeOffset.UtcNow;
            var promotionCandidates = new List<(ReplicationPriority FromPriority, QueuedReplication Item)>();

            foreach (var priority in Enum.GetValues<ReplicationPriority>().OrderByDescending(p => (int)p))
            {
                if (priority == ReplicationPriority.Critical) continue; // Already highest

                var queue = _queues[priority];
                var tempQueue = new List<QueuedReplication>();

                while (queue.TryDequeue(out var item))
                {
                    if (now - item.EnqueuedAt > _starvationThreshold && item.PromotionCount < 3)
                    {
                        item.PromotionCount++;
                        var newPriority = (ReplicationPriority)Math.Max(0, (int)priority - 1);
                        promotionCandidates.Add((newPriority, item));
                        Interlocked.Increment(ref _totalPromoted);
                    }
                    else
                    {
                        tempQueue.Add(item);
                    }
                }

                foreach (var remaining in tempQueue)
                    queue.Enqueue(remaining);
            }

            foreach (var (toPriority, item) in promotionCandidates)
            {
                item.Priority = toPriority;
                _queues[toPriority].Enqueue(item);
            }
        }

        private Task HandleEnqueueMessageAsync(PluginMessage message)
        {
            var itemId = message.Payload.GetValueOrDefault("itemId")?.ToString() ?? Guid.NewGuid().ToString();
            var priorityStr = message.Payload.GetValueOrDefault("priority")?.ToString() ?? "Normal";
            var sourceNode = message.Payload.GetValueOrDefault("sourceNode")?.ToString() ?? "";
            var targetNode = message.Payload.GetValueOrDefault("targetNode")?.ToString() ?? "";
            var dataSizeStr = message.Payload.GetValueOrDefault("dataSize")?.ToString();

            if (!Enum.TryParse<ReplicationPriority>(priorityStr, true, out var priority))
                priority = ReplicationPriority.Normal;

            var item = new QueuedReplication
            {
                ItemId = itemId,
                Priority = priority,
                SourceNode = sourceNode,
                TargetNode = targetNode,
                DataSizeBytes = long.TryParse(dataSizeStr, out var ds) ? ds : 0,
                StrategyName = message.Payload.GetValueOrDefault("strategy")?.ToString()
            };

            Enqueue(item);
            return Task.CompletedTask;
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _subscription?.Dispose();
        }
    }

    #region Priority Queue Types

    /// <summary>
    /// Replication priority levels for queue ordering.
    /// </summary>
    public enum ReplicationPriority
    {
        /// <summary>Critical: processed immediately, never deferred.</summary>
        Critical = 0,
        /// <summary>High: processed before normal items.</summary>
        High = 1,
        /// <summary>Normal: standard priority.</summary>
        Normal = 2,
        /// <summary>Low: processed after higher-priority items.</summary>
        Low = 3,
        /// <summary>Background: processed only when queue is otherwise empty.</summary>
        Background = 4
    }

    /// <summary>
    /// Queued replication item.
    /// </summary>
    public sealed class QueuedReplication
    {
        /// <summary>Unique item identifier.</summary>
        public required string ItemId { get; init; }
        /// <summary>Priority level (may change due to starvation promotion).</summary>
        public ReplicationPriority Priority { get; set; }
        /// <summary>Source node.</summary>
        public required string SourceNode { get; init; }
        /// <summary>Target node.</summary>
        public required string TargetNode { get; init; }
        /// <summary>Data size in bytes.</summary>
        public required long DataSizeBytes { get; init; }
        /// <summary>Strategy to use (null = use active strategy).</summary>
        public string? StrategyName { get; init; }
        /// <summary>When the item was enqueued.</summary>
        public DateTimeOffset EnqueuedAt { get; set; }
        /// <summary>When the item was dequeued (null if still queued).</summary>
        public DateTimeOffset? DequeuedAt { get; set; }
        /// <summary>Number of times promoted due to starvation prevention.</summary>
        public int PromotionCount { get; set; }
    }

    #endregion
}
