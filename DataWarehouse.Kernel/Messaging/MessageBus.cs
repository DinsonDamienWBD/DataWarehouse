using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Messaging
{
    /// <summary>
    /// Well-known message topics for kernel events.
    /// </summary>
    public static class MessageTopics
    {
        public const string SystemStartup = "system.startup";
        public const string SystemShutdown = "system.shutdown";
        public const string PluginLoaded = "plugin.loaded";
        public const string PluginUnloaded = "plugin.unloaded";
        public const string StorageWrite = "storage.write";
        public const string StorageRead = "storage.read";
        public const string StorageDelete = "storage.delete";
        public const string PipelineStart = "pipeline.start";
        public const string PipelineComplete = "pipeline.complete";
        public const string PipelineError = "pipeline.error";
        public const string ConfigChanged = "config.changed";
    }

    /// <summary>
    /// Response from a message send operation.
    /// </summary>
    public class MessageResponse
    {
        /// <summary>
        /// Whether the message was handled successfully.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Response payload from handler.
        /// </summary>
        public object? Payload { get; init; }

        /// <summary>
        /// Error message if failed.
        /// </summary>
        public string? Error { get; init; }

        /// <summary>
        /// Which plugin handled the message.
        /// </summary>
        public string? HandledBy { get; init; }

        public static MessageResponse Ok(object? payload = null, string? handledBy = null) =>
            new() { Success = true, Payload = payload, HandledBy = handledBy };

        public static MessageResponse Fail(string error) =>
            new() { Success = false, Error = error };

        public static MessageResponse NoHandler() =>
            new() { Success = false, Error = "No handler registered for topic" };
    }

    /// <summary>
    /// Default implementation of the message bus for inter-plugin communication.
    ///
    /// Features:
    /// - Pub/sub messaging pattern
    /// - Request/response pattern
    /// - Topic-based routing
    /// - Async-first design
    /// - Non-blocking operations
    /// </summary>
    public sealed class DefaultMessageBus : IMessageBus
    {
        private readonly ConcurrentDictionary<string, List<Subscription>> _subscriptions = new();
        private readonly ConcurrentDictionary<string, Func<PluginMessage, Task<MessageResponse>>> _requestHandlers = new();
        private readonly ILogger? _logger;
        private readonly object _subscriptionLock = new();
        private long _subscriptionIdCounter;

        public DefaultMessageBus(ILogger? logger = null)
        {
            _logger = logger;
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
                    list = new List<Subscription>();
                    _subscriptions[topic] = list;
                }
                list.Add(subscription);
            }

            _logger?.LogDebug("Subscription {Id} created for topic {Topic}", subscriptionId, topic);

            return new SubscriptionHandle(this, subscription);
        }

        /// <summary>
        /// Register a request handler for a topic.
        /// </summary>
        public IDisposable RegisterHandler(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(handler);

            _requestHandlers[topic] = handler;
            _logger?.LogDebug("Request handler registered for topic {Topic}", topic);

            return new HandlerHandle(this, topic);
        }

        /// <summary>
        /// Publish a message to all subscribers (fire and forget).
        /// </summary>
        public async Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(message);

            List<Subscription>? subscribers;
            lock (_subscriptionLock)
            {
                if (!_subscriptions.TryGetValue(topic, out subscribers))
                {
                    return;
                }
                subscribers = subscribers.ToList(); // Copy for thread safety
            }

            _logger?.LogDebug("Publishing message to {Count} subscribers on topic {Topic}",
                subscribers.Count, topic);

            // Fire all handlers concurrently
            var tasks = subscribers.Select(async sub =>
            {
                try
                {
                    await sub.Handler(message);
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

            if (!_requestHandlers.TryGetValue(topic, out var handler))
            {
                _logger?.LogWarning("No handler registered for topic {Topic}", topic);
                return MessageResponse.NoHandler();
            }

            try
            {
                var response = await handler(message);
                _logger?.LogDebug("Request handled for topic {Topic}", topic);
                return response;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Handler failed for topic {Topic}", topic);
                return MessageResponse.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Get the number of subscribers for a topic.
        /// </summary>
        public int GetSubscriberCount(string topic)
        {
            lock (_subscriptionLock)
            {
                if (_subscriptions.TryGetValue(topic, out var list))
                {
                    return list.Count;
                }
            }
            return 0;
        }

        /// <summary>
        /// Check if a topic has a request handler.
        /// </summary>
        public bool HasHandler(string topic)
        {
            return _requestHandlers.ContainsKey(topic);
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
                {
                    topics.Add(topic);
                }
            }
            foreach (var topic in _requestHandlers.Keys)
            {
                topics.Add(topic);
            }
            return topics;
        }

        private void Unsubscribe(Subscription subscription)
        {
            lock (_subscriptionLock)
            {
                if (_subscriptions.TryGetValue(subscription.Topic, out var list))
                {
                    list.RemoveAll(s => s.Id == subscription.Id);
                    if (list.Count == 0)
                    {
                        _subscriptions.TryRemove(subscription.Topic, out _);
                    }
                }
            }
            _logger?.LogDebug("Subscription {Id} removed from topic {Topic}",
                subscription.Id, subscription.Topic);
        }

        private void UnregisterHandler(string topic)
        {
            _requestHandlers.TryRemove(topic, out _);
            _logger?.LogDebug("Handler removed for topic {Topic}", topic);
        }

        private sealed class Subscription
        {
            public long Id { get; }
            public string Topic { get; }
            public Func<PluginMessage, Task> Handler { get; }

            public Subscription(long id, string topic, Func<PluginMessage, Task> handler)
            {
                Id = id;
                Topic = topic;
                Handler = handler;
            }
        }

        private sealed class SubscriptionHandle : IDisposable
        {
            private readonly DefaultMessageBus _bus;
            private readonly Subscription _subscription;
            private bool _disposed;

            public SubscriptionHandle(DefaultMessageBus bus, Subscription subscription)
            {
                _bus = bus;
                _subscription = subscription;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _bus.Unsubscribe(_subscription);
            }
        }

        private sealed class HandlerHandle : IDisposable
        {
            private readonly DefaultMessageBus _bus;
            private readonly string _topic;
            private bool _disposed;

            public HandlerHandle(DefaultMessageBus bus, string topic)
            {
                _bus = bus;
                _topic = topic;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _bus.UnregisterHandler(_topic);
            }
        }
    }
}
