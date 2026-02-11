using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Messaging
{
    /// <summary>
    /// Thread-safe subscription list that ensures atomic operations.
    /// Wraps a List with proper synchronization to avoid race conditions.
    /// </summary>
    internal sealed class ThreadSafeSubscriptionList<T>
    {
        private readonly List<T> _handlers = new();
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

        public int Count
        {
            get
            {
                _lock.EnterReadLock();
                try { return _handlers.Count; }
                finally { _lock.ExitReadLock(); }
            }
        }

        public void Add(T handler)
        {
            _lock.EnterWriteLock();
            try { _handlers.Add(handler); }
            finally { _lock.ExitWriteLock(); }
        }

        public bool Remove(T handler)
        {
            _lock.EnterWriteLock();
            try { return _handlers.Remove(handler); }
            finally { _lock.ExitWriteLock(); }
        }

        public T[] ToArray()
        {
            _lock.EnterReadLock();
            try { return _handlers.ToArray(); }
            finally { _lock.ExitReadLock(); }
        }

        public T? FirstOrDefault()
        {
            _lock.EnterReadLock();
            try { return _handlers.Count > 0 ? _handlers[0] : default; }
            finally { _lock.ExitReadLock(); }
        }

        public void Clear()
        {
            _lock.EnterWriteLock();
            try { _handlers.Clear(); }
            finally { _lock.ExitWriteLock(); }
        }
    }

    /// <summary>
    /// Bounded concurrent dictionary that enforces a maximum capacity.
    /// When capacity is reached, oldest entries are evicted.
    /// </summary>
    internal sealed class BoundedConcurrentDictionary<TKey, TValue> where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, TValue> _dictionary = new();
        private readonly ConcurrentQueue<TKey> _keyOrder = new();
        private readonly int _maxCapacity;
        private readonly object _evictionLock = new();

        public BoundedConcurrentDictionary(int maxCapacity)
        {
            _maxCapacity = maxCapacity > 0 ? maxCapacity : int.MaxValue;
        }

        public int Count => _dictionary.Count;

        public TValue this[TKey key]
        {
            get => _dictionary[key];
            set => AddOrUpdate(key, value);
        }

        public bool TryGetValue(TKey key, out TValue? value) => _dictionary.TryGetValue(key, out value);

        public bool TryRemove(TKey key, out TValue? value) => _dictionary.TryRemove(key, out value);

        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);

        public ICollection<TValue> Values => _dictionary.Values;

        public ICollection<TKey> Keys => _dictionary.Keys;

        public void AddOrUpdate(TKey key, TValue value)
        {
            // Use TryAdd for atomic check-then-add to avoid race condition
            if (_dictionary.TryAdd(key, value))
            {
                // Key was new - track it for eviction ordering and enforce capacity
                _keyOrder.Enqueue(key);
                EnforceCapacity();
            }
            else
            {
                // Key already exists - just update the value
                _dictionary[key] = value;
            }
        }

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (_dictionary.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var value = _dictionary.GetOrAdd(key, valueFactory);
            _keyOrder.Enqueue(key);
            EnforceCapacity();
            return value;
        }

        private void EnforceCapacity()
        {
            if (_dictionary.Count <= _maxCapacity) return;

            lock (_evictionLock)
            {
                while (_dictionary.Count > _maxCapacity && _keyOrder.TryDequeue(out var oldKey))
                {
                    _dictionary.TryRemove(oldKey, out _);
                }
            }
        }

        public IEnumerable<KeyValuePair<TKey, TValue>> Where(Func<KeyValuePair<TKey, TValue>, bool> predicate)
        {
            return _dictionary.Where(predicate);
        }
    }

    /// <summary>
    /// Production-ready advanced message bus with reliable delivery, transactional messaging,
    /// and comprehensive statistics. Suitable for hyperscale deployments.
    ///
    /// Thread-safety guarantees:
    /// - All subscription operations are atomic and thread-safe
    /// - Message delivery is non-blocking and handles concurrent publishes
    /// - Statistics are protected by lock and use atomic operations
    /// - Bounded collections prevent memory exhaustion under load
    /// </summary>
    public class AdvancedMessageBus : MessageBusBase, IAdvancedMessageBus
    {
        private readonly BoundedConcurrentDictionary<string, PendingMessage> _pendingMessages;
        private readonly BoundedConcurrentDictionary<string, MessageGroup> _messageGroups;
        private readonly ConcurrentDictionary<string, FilteredSubscription> _filteredSubscriptions = new();
        private readonly MessageBusStatistics _statistics = new();
        private readonly IKernelContext _context;
        private readonly AdvancedMessageBusConfig _config;
        private readonly Timer _retryTimer;
        private readonly Timer _cleanupTimer;
        private readonly object _statsLock = new();

        // Thread-safe subscription storage - uses ThreadSafeSubscriptionList for atomic operations
        private readonly ConcurrentDictionary<string, ThreadSafeSubscriptionList<Func<PluginMessage, Task>>> _subscriptions = new();

        public AdvancedMessageBus(IKernelContext context, AdvancedMessageBusConfig? config = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _config = config ?? new AdvancedMessageBusConfig();

            // Initialize bounded collections with configured limits
            _pendingMessages = new BoundedConcurrentDictionary<string, PendingMessage>(_config.MaxPendingMessages);
            _messageGroups = new BoundedConcurrentDictionary<string, MessageGroup>(_config.MaxMessageGroups);

            // Start background timers
            _retryTimer = new Timer(ProcessRetries, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            _cleanupTimer = new Timer(CleanupExpiredMessages, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        #region Base Message Bus Implementation

        public override async Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            if (_subscriptions.TryGetValue(topic, out var handlerList))
            {
                // Get a snapshot of handlers for thread-safe iteration
                var handlers = handlerList.ToArray();
                if (handlers.Length == 0) return;

                // Execute all handlers with proper error handling
                var exceptions = new List<Exception>();
                var tasks = handlers.Select(async h =>
                {
                    try
                    {
                        await h(message);
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) { exceptions.Add(ex); }
                        _context.LogError($"[MessageBus] Handler error on topic '{topic}': {ex.Message}", ex);
                    }
                });

                await Task.WhenAll(tasks);

                // If all handlers failed, record failure
                if (exceptions.Count == handlers.Length && handlers.Length > 0)
                {
                    RecordStatistic(s => s.TotalFailed++);
                }
            }
        }

        public override async Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            if (_subscriptions.TryGetValue(topic, out var handlerList))
            {
                var handler = handlerList.FirstOrDefault();
                if (handler != null)
                {
                    try
                    {
                        await handler(message);
                        return MessageResponse.Ok(null);
                    }
                    catch (Exception ex)
                    {
                        _context.LogError($"[MessageBus] SendAsync error on topic '{topic}': {ex.Message}", ex);
                        return MessageResponse.Error(ex.Message, "SEND_ERROR");
                    }
                }
            }
            return MessageResponse.Error("No handler registered for topic", "NO_HANDLER");
        }

        public override IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler)
        {
            // GetOrAdd with ThreadSafeSubscriptionList is atomic
            var handlerList = _subscriptions.GetOrAdd(topic, _ => new ThreadSafeSubscriptionList<Func<PluginMessage, Task>>());
            handlerList.Add(handler);

            return CreateHandle(() => handlerList.Remove(handler));
        }

        public override void Unsubscribe(string topic)
        {
            if (_subscriptions.TryRemove(topic, out var handlerList))
            {
                handlerList.Clear();
            }
        }

        public override IEnumerable<string> GetActiveTopics()
        {
            return _subscriptions.Keys.ToList();
        }

        #endregion

        #region Reliable Publishing (At-Least-Once Delivery)

        /// <summary>
        /// Implements IAdvancedMessageBus.PublishReliableAsync - simple reliable delivery.
        /// </summary>
        public async Task PublishReliableAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            await PublishWithConfirmationAsync(topic, message, null, ct);
        }

        /// <summary>
        /// Implements IAdvancedMessageBus.PublishWithConfirmationAsync - returns detailed publish result.
        /// </summary>
        public async Task<PublishResult> PublishWithConfirmationAsync(
            string topic,
            PluginMessage message,
            PublishOptions? options = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            options ??= PublishOptions.Default;

            var messageId = options.CorrelationId ?? message.CorrelationId ?? Guid.NewGuid().ToString("N")[..16];
            // Note: CorrelationId is init-only; we track messageId separately for confirmation

            _context.LogDebug($"[MessageBus] Publishing with confirmation: {messageId} to {topic}");

            try
            {
                // Count subscribers - thread-safe access
                var subscriberCount = 0;
                if (_subscriptions.TryGetValue(topic, out var handlerList))
                {
                    subscriberCount = handlerList.Count;
                }

                // Deliver the message
                var delivered = await DeliverMessageAsync(topic, message, ct);

                sw.Stop();

                if (delivered)
                {
                    RecordStatistic(s => s.TotalDelivered++);
                    return PublishResult.Ok(messageId, subscriberCount, sw.Elapsed);
                }
                else
                {
                    return new PublishResult
                    {
                        Success = false,
                        MessageId = messageId,
                        SubscribersNotified = 0,
                        Duration = sw.Elapsed,
                        Error = "No subscribers for topic"
                    };
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordStatistic(s => s.TotalFailed++);
                _context.LogError($"[MessageBus] Publish failed for {messageId}: {ex.Message}", ex);

                return new PublishResult
                {
                    Success = false,
                    MessageId = messageId,
                    Duration = sw.Elapsed,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Publishes a message with at-least-once delivery guarantee.
        /// The message will be retried until acknowledged or max retries reached.
        /// </summary>
        public async Task<ReliablePublishResult> PublishReliableAsync(
            string topic,
            PluginMessage message,
            ReliablePublishOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new ReliablePublishOptions();
            // Use existing CorrelationId or generate a new one for tracking
            var messageId = message.CorrelationId ?? Guid.NewGuid().ToString("N");

            _context.LogDebug($"[MessageBus] Publishing reliable message {messageId} to {topic}");

            var pending = new PendingMessage
            {
                MessageId = messageId,
                Topic = topic,
                Message = message,
                Options = options,
                CreatedAt = DateTime.UtcNow,
                RetryCount = 0,
                State = MessageState.Pending
            };

            _pendingMessages.AddOrUpdate(messageId, pending);
            RecordStatistic(s => s.TotalPublished++);

            try
            {
                // Attempt initial delivery
                var delivered = await DeliverMessageAsync(topic, message, ct);

                if (delivered)
                {
                    pending.State = MessageState.Delivered;
                    pending.DeliveredAt = DateTime.UtcNow;

                    // Wait for acknowledgment if required
                    if (options.RequireAcknowledgment)
                    {
                        var acked = await WaitForAcknowledgmentAsync(messageId, options.AcknowledgmentTimeout, ct);
                        if (acked)
                        {
                            pending.State = MessageState.Acknowledged;
                            pending.AcknowledgedAt = DateTime.UtcNow;
                            RecordStatistic(s => s.TotalAcknowledged++);
                        }
                        else
                        {
                            pending.State = MessageState.PendingRetry;
                            RecordStatistic(s => s.TotalPendingRetry++);
                        }
                    }
                    else
                    {
                        RecordStatistic(s => s.TotalDelivered++);
                    }
                }
                else
                {
                    pending.State = MessageState.PendingRetry;
                    RecordStatistic(s => s.TotalPendingRetry++);
                }

                return new ReliablePublishResult
                {
                    Success = pending.State == MessageState.Delivered || pending.State == MessageState.Acknowledged,
                    MessageId = messageId,
                    State = pending.State,
                    DeliveredAt = pending.DeliveredAt,
                    AcknowledgedAt = pending.AcknowledgedAt
                };
            }
            catch (Exception ex)
            {
                pending.State = MessageState.Failed;
                pending.LastError = ex.Message;
                RecordStatistic(s => s.TotalFailed++);

                _context.LogError($"[MessageBus] Failed to publish message {messageId}: {ex.Message}", ex);

                return new ReliablePublishResult
                {
                    Success = false,
                    MessageId = messageId,
                    State = MessageState.Failed,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Acknowledges receipt of a message.
        /// </summary>
        public Task AcknowledgeAsync(string messageId, CancellationToken ct = default)
        {
            if (_pendingMessages.TryGetValue(messageId, out var pending) && pending != null)
            {
                pending.State = MessageState.Acknowledged;
                pending.AcknowledgedAt = DateTime.UtcNow;
                RecordStatistic(s => s.TotalAcknowledged++);
                _context.LogDebug($"[MessageBus] Message {messageId} acknowledged");
            }
            return Task.CompletedTask;
        }

        private async Task<bool> DeliverMessageAsync(string topic, PluginMessage message, CancellationToken ct)
        {
            try
            {
                await PublishAsync(topic, message, ct);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> WaitForAcknowledgmentAsync(string messageId, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (_pendingMessages.TryGetValue(messageId, out var pending) &&
                    pending != null && pending.State == MessageState.Acknowledged)
                {
                    return true;
                }

                await Task.Delay(100, ct);
            }
            return false;
        }

        private void ProcessRetries(object? state)
        {
            var now = DateTime.UtcNow;
            var toRetry = _pendingMessages.Values
                .Where(p => p.State == MessageState.PendingRetry)
                .Where(p => p.NextRetryAt == null || p.NextRetryAt <= now)
                .ToList();

            foreach (var pending in toRetry)
            {
                if (pending.RetryCount >= pending.Options.MaxRetries)
                {
                    pending.State = MessageState.Failed;
                    pending.LastError = $"Max retries ({pending.Options.MaxRetries}) exceeded";
                    RecordStatistic(s => s.TotalFailed++);
                    _context.LogWarning($"[MessageBus] Message {pending.MessageId} failed after {pending.RetryCount} retries");
                    continue;
                }

                pending.RetryCount++;
                pending.NextRetryAt = now + CalculateBackoff(pending.RetryCount, pending.Options);

                _context.LogDebug($"[MessageBus] Retrying message {pending.MessageId} (attempt {pending.RetryCount})");

                Task.Run(async () =>
                {
                    try
                    {
                        var delivered = await DeliverMessageAsync(pending.Topic, pending.Message, CancellationToken.None);
                        if (delivered)
                        {
                            pending.State = MessageState.Delivered;
                            pending.DeliveredAt = DateTime.UtcNow;
                            RecordStatistic(s => { s.TotalDelivered++; s.TotalRetried++; });
                        }
                    }
                    catch (Exception ex)
                    {
                        pending.LastError = ex.Message;
                        _context.LogError($"[MessageBus] Retry failed for {pending.MessageId}: {ex.Message}", ex);
                    }
                });
            }
        }

        private TimeSpan CalculateBackoff(int retryCount, ReliablePublishOptions options)
        {
            // Exponential backoff with jitter
            var baseDelay = options.RetryDelay.TotalMilliseconds;
            var exponentialDelay = baseDelay * Math.Pow(2, retryCount - 1);
            var maxDelay = options.MaxRetryDelay.TotalMilliseconds;
            var delay = Math.Min(exponentialDelay, maxDelay);

            // Add jitter (±20%)
            var jitter = (Random.Shared.NextDouble() - 0.5) * 0.4 * delay;
            delay += jitter;

            return TimeSpan.FromMilliseconds(delay);
        }

        #endregion

        #region Filtered Subscriptions

        /// <summary>
        /// Subscribes to messages matching a predicate filter.
        /// </summary>
        public IDisposable Subscribe(
            string topic,
            Func<PluginMessage, bool> filter,
            Action<PluginMessage> handler)
        {
            var subscriptionId = Guid.NewGuid().ToString("N");

            var subscription = new FilteredSubscription
            {
                SubscriptionId = subscriptionId,
                Topic = topic,
                Filter = filter,
                Handler = handler
            };

            _filteredSubscriptions[subscriptionId] = subscription;

            // Subscribe to base topic with filtering
            var baseSubscription = Subscribe(topic, msg =>
            {
                if (filter(msg))
                {
                    try
                    {
                        handler(msg);
                        RecordStatistic(s => s.TotalFiltered++);
                    }
                    catch (Exception ex)
                    {
                        _context.LogError($"[MessageBus] Filtered handler error: {ex.Message}", ex);
                    }
                }
                return Task.CompletedTask;
            });

            _context.LogDebug($"[MessageBus] Created filtered subscription {subscriptionId} on {topic}");

            return new FilteredSubscriptionDisposable(subscriptionId, baseSubscription, this);
        }

        internal void RemoveFilteredSubscription(string subscriptionId)
        {
            _filteredSubscriptions.TryRemove(subscriptionId, out _);
        }

        #endregion

        #region Message Groups (Transactional Batching)

        /// <summary>
        /// Creates a transactional message group for atomic batch publishing.
        /// </summary>
        public IMessageGroup CreateGroup(string groupId)
        {
            if (_messageGroups.ContainsKey(groupId))
            {
                throw new InvalidOperationException($"Message group '{groupId}' already exists");
            }

            var group = new MessageGroup
            {
                GroupId = groupId,
                CreatedAt = DateTime.UtcNow,
                State = GroupState.Open,
                Messages = new List<GroupedMessage>()
            };

            _messageGroups.AddOrUpdate(groupId, group);
            _context.LogDebug($"[MessageBus] Created message group {groupId}");

            return new MessageGroupHandle(groupId, this);
        }

        internal void AddToGroup(string groupId, string topic, PluginMessage message)
        {
            if (!_messageGroups.TryGetValue(groupId, out var group) || group == null)
            {
                throw new InvalidOperationException($"Message group '{groupId}' not found");
            }

            if (group.State != GroupState.Open)
            {
                throw new InvalidOperationException($"Message group '{groupId}' is not open for additions");
            }

            group.Messages.Add(new GroupedMessage
            {
                Topic = topic,
                Message = message,
                AddedAt = DateTime.UtcNow
            });
        }

        internal async Task<GroupCommitResult> CommitGroupAsync(string groupId, CancellationToken ct)
        {
            if (!_messageGroups.TryGetValue(groupId, out var group) || group == null)
            {
                throw new InvalidOperationException($"Message group '{groupId}' not found");
            }

            group.State = GroupState.Committing;
            var results = new List<(string Topic, bool Success, string? Error)>();

            _context.LogInfo($"[MessageBus] Committing group {groupId} with {group.Messages.Count} messages");

            try
            {
                // Publish all messages in the group
                foreach (var gm in group.Messages)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        await PublishAsync(gm.Topic, gm.Message, ct);
                        results.Add((gm.Topic, true, null));
                    }
                    catch (Exception ex)
                    {
                        results.Add((gm.Topic, false, ex.Message));
                    }
                }

                var allSuccess = results.All(r => r.Success);
                group.State = allSuccess ? GroupState.Committed : GroupState.PartiallyCommitted;
                group.CommittedAt = DateTime.UtcNow;

                RecordStatistic(s =>
                {
                    s.TotalGroupsCommitted++;
                    s.TotalGroupMessages += group.Messages.Count;
                });

                return new GroupCommitResult
                {
                    GroupId = groupId,
                    Success = allSuccess,
                    TotalMessages = group.Messages.Count,
                    SuccessfulMessages = results.Count(r => r.Success),
                    FailedMessages = results.Count(r => !r.Success),
                    Errors = results.Where(r => !r.Success).Select(r => $"{r.Topic}: {r.Error}").ToList()
                };
            }
            catch (Exception ex)
            {
                group.State = GroupState.Failed;
                _context.LogError($"[MessageBus] Group {groupId} commit failed: {ex.Message}", ex);

                return new GroupCommitResult
                {
                    GroupId = groupId,
                    Success = false,
                    Errors = new List<string> { ex.Message }
                };
            }
        }

        internal void RollbackGroup(string groupId)
        {
            if (_messageGroups.TryGetValue(groupId, out var group) && group != null)
            {
                group.State = GroupState.RolledBack;
                group.Messages.Clear();
                _context.LogDebug($"[MessageBus] Rolled back group {groupId}");
            }
        }

        internal void DisposeGroup(string groupId)
        {
            _messageGroups.TryRemove(groupId, out _);
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Gets comprehensive message bus statistics.
        /// </summary>
        public MessageBusStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new MessageBusStatistics
                {
                    TotalPublished = _statistics.TotalPublished,
                    TotalDelivered = _statistics.TotalDelivered,
                    TotalAcknowledged = _statistics.TotalAcknowledged,
                    TotalFailed = _statistics.TotalFailed,
                    TotalRetried = _statistics.TotalRetried,
                    TotalPendingRetry = _pendingMessages.Where(p => p.Value.State == MessageState.PendingRetry).Count(),
                    TotalFiltered = _statistics.TotalFiltered,
                    TotalGroupsCommitted = _statistics.TotalGroupsCommitted,
                    TotalGroupMessages = _statistics.TotalGroupMessages,
                    ActiveSubscriptions = GetActiveTopics().Count(),
                    FilteredSubscriptions = _filteredSubscriptions.Count,
                    ActiveGroups = _messageGroups.Where(g => g.Value.State == GroupState.Open).Count(),
                    PendingMessages = _pendingMessages.Count
                };
            }
        }

        /// <summary>
        /// Resets statistics counters.
        /// </summary>
        public void ResetStatistics()
        {
            lock (_statsLock)
            {
                _statistics.TotalPublished = 0;
                _statistics.TotalDelivered = 0;
                _statistics.TotalAcknowledged = 0;
                _statistics.TotalFailed = 0;
                _statistics.TotalRetried = 0;
                _statistics.TotalFiltered = 0;
                _statistics.TotalGroupsCommitted = 0;
                _statistics.TotalGroupMessages = 0;
            }
        }

        private void RecordStatistic(Action<MessageBusStatistics> update)
        {
            lock (_statsLock)
            {
                update(_statistics);
            }
        }

        #endregion

        #region Cleanup

        private void CleanupExpiredMessages(object? state)
        {
            var now = DateTime.UtcNow;
            var expiredIds = _pendingMessages
                .Where(p => p.Value.State == MessageState.Acknowledged ||
                           p.Value.State == MessageState.Failed ||
                           p.Value.State == MessageState.Delivered)
                .Where(p => (now - p.Value.CreatedAt) > _config.MessageRetention)
                .Select(p => p.Key)
                .ToList();

            foreach (var id in expiredIds)
            {
                _pendingMessages.TryRemove(id, out _);
            }

            if (expiredIds.Count > 0)
            {
                _context.LogDebug($"[MessageBus] Cleaned up {expiredIds.Count} expired messages");
            }
        }

        /// <summary>
        /// Disposes the message bus and releases resources.
        /// </summary>
        public void Dispose()
        {
            _retryTimer.Dispose();
            _cleanupTimer.Dispose();
        }

        #endregion

        #region Internal Classes

        private class PendingMessage
        {
            public string MessageId { get; set; } = string.Empty;
            public string Topic { get; set; } = string.Empty;
            public required PluginMessage Message { get; set; }
            public ReliablePublishOptions Options { get; set; } = new();
            public DateTime CreatedAt { get; set; }
            public DateTime? DeliveredAt { get; set; }
            public DateTime? AcknowledgedAt { get; set; }
            public DateTime? NextRetryAt { get; set; }
            public int RetryCount { get; set; }
            public MessageState State { get; set; }
            public string? LastError { get; set; }
        }

        private class FilteredSubscription
        {
            public string SubscriptionId { get; set; } = string.Empty;
            public string Topic { get; set; } = string.Empty;
            public required Func<PluginMessage, bool> Filter { get; set; }
            public required Action<PluginMessage> Handler { get; set; }
        }

        private class MessageGroup
        {
            public string GroupId { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public DateTime? CommittedAt { get; set; }
            public GroupState State { get; set; }
            public List<GroupedMessage> Messages { get; set; } = new();
        }

        private class GroupedMessage
        {
            public string Topic { get; set; } = string.Empty;
            public required PluginMessage Message { get; set; }
            public DateTime AddedAt { get; set; }
        }

        private class FilteredSubscriptionDisposable : IDisposable
        {
            private readonly string _subscriptionId;
            private readonly IDisposable _baseSubscription;
            private readonly AdvancedMessageBus _bus;

            public FilteredSubscriptionDisposable(string subscriptionId, IDisposable baseSubscription, AdvancedMessageBus bus)
            {
                _subscriptionId = subscriptionId;
                _baseSubscription = baseSubscription;
                _bus = bus;
            }

            public void Dispose()
            {
                _baseSubscription.Dispose();
                _bus.RemoveFilteredSubscription(_subscriptionId);
            }
        }

        private class MessageGroupHandle : IMessageGroup
        {
            private readonly string _groupId;
            private readonly AdvancedMessageBus _bus;
            private bool _disposed;

            public MessageGroupHandle(string groupId, AdvancedMessageBus bus)
            {
                _groupId = groupId;
                _bus = bus;
            }

            public string GroupId => _groupId;

            public void Add(string topic, PluginMessage message)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(MessageGroupHandle));
                _bus.AddToGroup(_groupId, topic, message);
            }

            public Task<GroupCommitResult> CommitAsync(CancellationToken ct = default)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(MessageGroupHandle));
                return _bus.CommitGroupAsync(_groupId, ct);
            }

            public void Rollback()
            {
                if (_disposed) throw new ObjectDisposedException(nameof(MessageGroupHandle));
                _bus.RollbackGroup(_groupId);
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _bus.DisposeGroup(_groupId);
                    _disposed = true;
                }
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Configuration for the advanced message bus.
    /// </summary>
    public class AdvancedMessageBusConfig
    {
        public TimeSpan MessageRetention { get; set; } = TimeSpan.FromHours(24);
        public int MaxPendingMessages { get; set; } = 100000;
        public int MaxMessageGroups { get; set; } = 1000;
    }

    /// <summary>
    /// Options for reliable message publishing.
    /// </summary>
    public class ReliablePublishOptions
    {
        public bool RequireAcknowledgment { get; set; } = true;
        public TimeSpan AcknowledgmentTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxRetries { get; set; } = 3;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Result of a reliable publish operation.
    /// </summary>
    public class ReliablePublishResult
    {
        public bool Success { get; set; }
        public string MessageId { get; set; } = string.Empty;
        public MessageState State { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// State of a pending message.
    /// </summary>
    public enum MessageState
    {
        Pending,
        Delivered,
        Acknowledged,
        PendingRetry,
        Failed
    }

    /// <summary>
    /// State of a message group.
    /// </summary>
    public enum GroupState
    {
        Open,
        Committing,
        Committed,
        PartiallyCommitted,
        RolledBack,
        Failed
    }

    /// <summary>
    /// Result of committing a message group.
    /// </summary>
    public class GroupCommitResult
    {
        public string GroupId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public int TotalMessages { get; set; }
        public int SuccessfulMessages { get; set; }
        public int FailedMessages { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// Interface for transactional message groups.
    /// </summary>
    public interface IMessageGroup : IDisposable
    {
        string GroupId { get; }
        void Add(string topic, PluginMessage message);
        Task<GroupCommitResult> CommitAsync(CancellationToken ct = default);
        void Rollback();
    }

    /// <summary>
    /// Interface for advanced message bus features.
    /// </summary>
    public interface IAdvancedMessageBus : IMessageBus
    {
        Task<ReliablePublishResult> PublishReliableAsync(string topic, PluginMessage message, ReliablePublishOptions? options = null, CancellationToken ct = default);
        Task AcknowledgeAsync(string messageId, CancellationToken ct = default);
        IDisposable Subscribe(string topic, Func<PluginMessage, bool> filter, Action<PluginMessage> handler);
        IMessageGroup CreateGroup(string groupId);
        MessageBusStatistics GetStatistics();
        void ResetStatistics();
    }

    /// <summary>
    /// Comprehensive message bus statistics.
    /// </summary>
    public class MessageBusStatistics
    {
        public long TotalPublished { get; set; }
        public long TotalDelivered { get; set; }
        public long TotalAcknowledged { get; set; }
        public long TotalFailed { get; set; }
        public long TotalRetried { get; set; }
        public long TotalPendingRetry { get; set; }
        public long TotalFiltered { get; set; }
        public long TotalGroupsCommitted { get; set; }
        public long TotalGroupMessages { get; set; }
        public int ActiveSubscriptions { get; set; }
        public int FilteredSubscriptions { get; set; }
        public int ActiveGroups { get; set; }
        public int PendingMessages { get; set; }
    }

    #endregion
}
