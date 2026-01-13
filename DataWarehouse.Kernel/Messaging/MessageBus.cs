using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DataWarehouse.Kernel.Messaging
{
    // NOTE: MessageTopics and MessageResponse are defined in SDK (DataWarehouse.SDK.Contracts)
    // Import with: using static DataWarehouse.SDK.Contracts.MessageTopics;

    /// <summary>
    /// Default implementation of the message bus for inter-plugin communication.
    ///
    /// Features:
    /// - Pub/sub messaging pattern
    /// - Request/response pattern
    /// - Topic-based and pattern-based routing
    /// - Async-first design
    /// - Non-blocking operations
    /// </summary>
    public sealed class DefaultMessageBus(ILogger? logger = null) : IMessageBus
    {
        private readonly ConcurrentDictionary<string, List<Subscription>> _subscriptions = new();
        private readonly ConcurrentDictionary<string, List<ResponseSubscription>> _responseSubscriptions = new();
        private readonly ConcurrentDictionary<string, PatternSubscription> _patternSubscriptions = new();
        private readonly ILogger? _logger = logger;
        private readonly Lock _subscriptionLock = new();
        private long _subscriptionIdCounter;

        /// <summary>
        /// Publish a message to all subscribers (fire and forget).
        /// </summary>
        public async Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(message);

            var handlers = GetHandlersForTopic(topic);
            if (handlers.Count == 0) return;

            _logger?.LogDebug("Publishing message to {Count} subscribers on topic {Topic}", handlers.Count, topic);

            // Fire all handlers concurrently without waiting
            foreach (var handler in handlers)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler(message);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Subscriber failed for topic {Topic}", topic);
                    }
                }, ct);
            }
        }

        /// <summary>
        /// Publish a message and wait for all handlers to complete.
        /// </summary>
        public async Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(message);

            var handlers = GetHandlersForTopic(topic);
            if (handlers.Count == 0) return;

            _logger?.LogDebug("Publishing (wait) message to {Count} subscribers on topic {Topic}", handlers.Count, topic);

            var tasks = handlers.Select(async handler =>
            {
                try
                {
                    await handler(message);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Subscriber failed for topic {Topic}", topic);
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Send a message and wait for a response.
        /// </summary>
        public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(message);

            // Find a response handler
            List<ResponseSubscription>? responseHandlers;
            lock (_subscriptionLock)
            {
                _responseSubscriptions.TryGetValue(topic, out responseHandlers);
                responseHandlers = responseHandlers?.ToList();
            }

            if (responseHandlers == null || responseHandlers.Count == 0)
            {
                _logger?.LogWarning("No handler registered for topic {Topic}", topic);
                return MessageResponse.Error("No handler registered for topic", "NO_HANDLER");
            }

            try
            {
                // Use first handler (could implement round-robin or load balancing later)
                var response = await responseHandlers[0].Handler(message);
                _logger?.LogDebug("Request handled for topic {Topic}", topic);
                return response;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Handler failed for topic {Topic}", topic);
                return MessageResponse.Error(ex.Message, "HANDLER_ERROR");
            }
        }

        /// <summary>
        /// Send a message and wait for a response with timeout.
        /// </summary>
        public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                return await SendAsync(topic, message, linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return MessageResponse.Error($"Request timed out after {timeout.TotalMilliseconds}ms", "TIMEOUT");
            }
        }

        /// <summary>
        /// Subscribe to messages on a topic.
        /// </summary>
        public IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(handler);

            var subscriptionId = Interlocked.Increment(ref _subscriptionIdCounter);
            var subscription = new Subscription(subscriptionId, topic, handler);

            lock (_subscriptionLock)
            {
                if (!_subscriptions.TryGetValue(topic, out var list))
                {
                    list = [];
                    _subscriptions[topic] = list;
                }
                list.Add(subscription);
            }

            _logger?.LogDebug("Subscription {Id} created for topic {Topic}", subscriptionId, topic);

            return new SubscriptionHandle(() =>
            {
                lock (_subscriptionLock)
                {
                    if (_subscriptions.TryGetValue(topic, out var list))
                    {
                        list.RemoveAll(s => s.Id == subscriptionId);
                        if (list.Count == 0)
                            _subscriptions.TryRemove(topic, out _);
                    }
                }
                _logger?.LogDebug("Subscription {Id} removed from topic {Topic}", subscriptionId, topic);
            });
        }

        /// <summary>
        /// Subscribe to messages on a topic with response capability.
        /// </summary>
        public IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(handler);

            var subscriptionId = Interlocked.Increment(ref _subscriptionIdCounter);
            var subscription = new ResponseSubscription(subscriptionId, topic, handler);

            lock (_subscriptionLock)
            {
                if (!_responseSubscriptions.TryGetValue(topic, out var list))
                {
                    list = [];
                    _responseSubscriptions[topic] = list;
                }
                list.Add(subscription);
            }

            _logger?.LogDebug("Response subscription {Id} created for topic {Topic}", subscriptionId, topic);

            return new SubscriptionHandle(() =>
            {
                lock (_subscriptionLock)
                {
                    if (_responseSubscriptions.TryGetValue(topic, out var list))
                    {
                        list.RemoveAll(s => s.Id == subscriptionId);
                        if (list.Count == 0)
                            _responseSubscriptions.TryRemove(topic, out _);
                    }
                }
                _logger?.LogDebug("Response subscription {Id} removed from topic {Topic}", subscriptionId, topic);
            });
        }

        /// <summary>
        /// Subscribe to messages matching a pattern (e.g., "storage.*", "*.error").
        /// </summary>
        public IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentNullException.ThrowIfNull(handler);

            var subscriptionId = Interlocked.Increment(ref _subscriptionIdCounter);

            // Convert glob pattern to regex
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            var regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var subscription = new PatternSubscription(subscriptionId, pattern, regex, handler);
            _patternSubscriptions[subscriptionId.ToString()] = subscription;

            _logger?.LogDebug("Pattern subscription {Id} created for pattern {Pattern}", subscriptionId, pattern);

            return new SubscriptionHandle(() =>
            {
                _patternSubscriptions.TryRemove(subscriptionId.ToString(), out _);
                _logger?.LogDebug("Pattern subscription {Id} removed", subscriptionId);
            });
        }

        /// <summary>
        /// Unsubscribe all handlers for a topic.
        /// </summary>
        public void Unsubscribe(string topic)
        {
            ArgumentNullException.ThrowIfNull(topic);

            lock (_subscriptionLock)
            {
                _subscriptions.TryRemove(topic, out _);
                _responseSubscriptions.TryRemove(topic, out _);
            }

            _logger?.LogDebug("All subscriptions removed for topic {Topic}", topic);
        }

        /// <summary>
        /// Get all active topics.
        /// </summary>
        public IEnumerable<string> GetActiveTopics()
        {
            var topics = new HashSet<string>();
            lock (_subscriptionLock)
            {
                foreach (var topic in _subscriptions.Keys)
                    topics.Add(topic);
                foreach (var topic in _responseSubscriptions.Keys)
                    topics.Add(topic);
            }
            return topics;
        }

        /// <summary>
        /// Get the number of subscribers for a topic.
        /// </summary>
        public int GetSubscriberCount(string topic)
        {
            lock (_subscriptionLock)
            {
                var count = 0;
                if (_subscriptions.TryGetValue(topic, out var list))
                    count += list.Count;
                if (_responseSubscriptions.TryGetValue(topic, out var rlist))
                    count += rlist.Count;
                return count;
            }
        }

        private List<Func<PluginMessage, Task>> GetHandlersForTopic(string topic)
        {
            var handlers = new List<Func<PluginMessage, Task>>();

            lock (_subscriptionLock)
            {
                // Direct subscriptions
                if (_subscriptions.TryGetValue(topic, out var list))
                {
                    handlers.AddRange(list.Select(s => s.Handler));
                }

                // Response subscriptions (call but ignore response)
                if (_responseSubscriptions.TryGetValue(topic, out var rlist))
                {
                    handlers.AddRange(rlist.Select(s => new Func<PluginMessage, Task>(async msg =>
                    {
                        await s.Handler(msg);
                    })));
                }
            }

            // Pattern subscriptions
            foreach (var ps in _patternSubscriptions.Values)
            {
                if (ps.Regex.IsMatch(topic))
                {
                    handlers.Add(ps.Handler);
                }
            }

            return handlers;
        }

        private sealed record Subscription(long Id, string Topic, Func<PluginMessage, Task> Handler);
        private sealed record ResponseSubscription(long Id, string Topic, Func<PluginMessage, Task<MessageResponse>> Handler);
        private sealed record PatternSubscription(long Id, string Pattern, Regex Regex, Func<PluginMessage, Task> Handler);

        private sealed class SubscriptionHandle(Action unsubscribe) : IDisposable
        {
            private readonly Action _unsubscribe = unsubscribe;
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _unsubscribe();
            }
        }
    }
}
