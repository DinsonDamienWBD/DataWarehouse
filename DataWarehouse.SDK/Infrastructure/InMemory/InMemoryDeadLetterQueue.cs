using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Resilience;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory implementation of <see cref="IDeadLetterQueue"/>.
    /// Stores failed messages in a concurrent dictionary with bounded capacity.
    /// Production-ready for single-node deployments.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryDeadLetterQueue : IDeadLetterQueue
    {
        private readonly ConcurrentDictionary<string, DeadLetterMessage> _messages = new();
        private readonly int _maxCapacity;

        /// <summary>
        /// Initializes a new in-memory dead letter queue.
        /// </summary>
        /// <param name="maxCapacity">Maximum number of messages. Default: 10,000.</param>
        public InMemoryDeadLetterQueue(int maxCapacity = 10_000)
        {
            _maxCapacity = maxCapacity;
        }

        /// <inheritdoc />
        public event Action<DeadLetterEvent>? OnDeadLetterEvent;

        /// <inheritdoc />
        public Task EnqueueAsync(DeadLetterMessage message, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Evict oldest if at capacity
            while (_messages.Count >= _maxCapacity)
            {
                var oldest = _messages.OrderBy(kv => kv.Value.FailedAt).FirstOrDefault();
                if (oldest.Key != null)
                {
                    _messages.TryRemove(oldest.Key, out _);
                }
            }

            _messages[message.MessageId] = message;

            OnDeadLetterEvent?.Invoke(new DeadLetterEvent
            {
                EventType = DeadLetterEventType.Enqueued,
                MessageId = message.MessageId,
                OriginalTopic = message.OriginalTopic,
                Timestamp = DateTimeOffset.UtcNow
            });

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<DeadLetterMessage>> PeekAsync(int maxCount, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var result = _messages.Values
                .OrderBy(m => m.FailedAt)
                .Take(maxCount)
                .ToList();
            return Task.FromResult<IReadOnlyList<DeadLetterMessage>>(result);
        }

        /// <inheritdoc />
        public Task<DeadLetterMessage?> DequeueAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var oldest = _messages.OrderBy(kv => kv.Value.FailedAt).FirstOrDefault();
            if (oldest.Key != null && _messages.TryRemove(oldest.Key, out var message))
            {
                return Task.FromResult<DeadLetterMessage?>(message);
            }
            return Task.FromResult<DeadLetterMessage?>(null);
        }

        /// <inheritdoc />
        public Task<bool> RetryAsync(string messageId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_messages.TryRemove(messageId, out var message))
            {
                OnDeadLetterEvent?.Invoke(new DeadLetterEvent
                {
                    EventType = DeadLetterEventType.RetrySucceeded,
                    MessageId = messageId,
                    OriginalTopic = message.OriginalTopic,
                    Timestamp = DateTimeOffset.UtcNow
                });
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <inheritdoc />
        public Task<bool> DiscardAsync(string messageId, string reason, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_messages.TryRemove(messageId, out var message))
            {
                OnDeadLetterEvent?.Invoke(new DeadLetterEvent
                {
                    EventType = DeadLetterEventType.Discarded,
                    MessageId = messageId,
                    OriginalTopic = message.OriginalTopic,
                    Timestamp = DateTimeOffset.UtcNow,
                    Detail = reason
                });
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <inheritdoc />
        public Task<int> GetCountAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_messages.Count);
        }

        /// <inheritdoc />
        public Task PurgeAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _messages.Clear();

            OnDeadLetterEvent?.Invoke(new DeadLetterEvent
            {
                EventType = DeadLetterEventType.Purged,
                MessageId = string.Empty,
                OriginalTopic = string.Empty,
                Timestamp = DateTimeOffset.UtcNow
            });

            return Task.CompletedTask;
        }
    }
}
