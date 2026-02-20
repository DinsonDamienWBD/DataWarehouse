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
    /// Lightweight sliding-window rate limiter for per-publisher message throttling.
    /// Uses a lock-free design with ConcurrentQueue for high throughput.
    /// </summary>
    internal sealed class SlidingWindowRateLimiter
    {
        private readonly int _maxMessages;
        private readonly TimeSpan _window;
        private readonly ConcurrentQueue<long> _timestamps = new();
        private long _count;

        public SlidingWindowRateLimiter(int maxMessagesPerWindow, TimeSpan window)
        {
            _maxMessages = maxMessagesPerWindow;
            _window = window;
        }

        /// <summary>
        /// Attempts to acquire a permit. Returns true if within rate limit, false if exceeded.
        /// </summary>
        public bool TryAcquire()
        {
            var now = Environment.TickCount64;
            var windowStart = now - (long)_window.TotalMilliseconds;

            // Evict expired timestamps
            while (_timestamps.TryPeek(out var oldest) && oldest < windowStart)
            {
                if (_timestamps.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _count);
                }
            }

            // Check if under limit
            if (Interlocked.Read(ref _count) >= _maxMessages)
            {
                return false;
            }

            // Record this request
            _timestamps.Enqueue(now);
            Interlocked.Increment(ref _count);
            return true;
        }
    }

    /// <summary>
    /// Validates message bus topic names to prevent injection attacks (BUS-06).
    /// </summary>
    internal static partial class TopicValidator
    {
        // Topic names: alphanumeric start, then alphanumeric, dots, hyphens, underscores. Max 256 chars.
        [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._\-]{0,255}$", RegexOptions.Compiled)]
        private static partial Regex TopicNameRegex();

        // Pattern topics allow * and ? for glob matching
        [GeneratedRegex(@"^[a-zA-Z0-9*?][a-zA-Z0-9._\-*?]{0,255}$", RegexOptions.Compiled)]
        private static partial Regex TopicPatternRegex();

        /// <summary>
        /// Validates a topic name. Rejects names with path traversal, control chars, whitespace.
        /// </summary>
        public static void ValidateTopic(string topic)
        {
            ArgumentNullException.ThrowIfNull(topic);

            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("Topic name cannot be empty or whitespace.", nameof(topic));

            if (topic.Contains(".."))
                throw new ArgumentException($"Topic name '{topic}' contains path traversal pattern '..'.", nameof(topic));

            if (topic.Contains('/') || topic.Contains('\\'))
                throw new ArgumentException($"Topic name '{topic}' contains path separator characters.", nameof(topic));

            if (topic.Any(c => char.IsControl(c)))
                throw new ArgumentException($"Topic name '{topic}' contains control characters.", nameof(topic));

            if (!TopicNameRegex().IsMatch(topic))
                throw new ArgumentException(
                    $"Topic name '{topic}' is invalid. Must match pattern: alphanumeric start, " +
                    "then alphanumeric, dots, hyphens, or underscores. Max 256 characters.", nameof(topic));
        }

        /// <summary>
        /// Validates a topic pattern (allows * and ? wildcards).
        /// </summary>
        public static void ValidateTopicPattern(string pattern)
        {
            ArgumentNullException.ThrowIfNull(pattern);

            if (string.IsNullOrWhiteSpace(pattern))
                throw new ArgumentException("Topic pattern cannot be empty or whitespace.", nameof(pattern));

            if (pattern.Contains(".."))
                throw new ArgumentException($"Topic pattern '{pattern}' contains path traversal pattern '..'.", nameof(pattern));

            if (pattern.Contains('/') || pattern.Contains('\\'))
                throw new ArgumentException($"Topic pattern '{pattern}' contains path separator characters.", nameof(pattern));

            if (pattern.Any(c => char.IsControl(c)))
                throw new ArgumentException($"Topic pattern '{pattern}' contains control characters.", nameof(pattern));

            if (!TopicPatternRegex().IsMatch(pattern))
                throw new ArgumentException(
                    $"Topic pattern '{pattern}' is invalid. Must match pattern: alphanumeric or wildcard start, " +
                    "then alphanumeric, dots, hyphens, underscores, or wildcards. Max 256 characters.", nameof(pattern));
        }
    }

    /// <summary>
    /// Default implementation of the message bus for inter-plugin communication.
    ///
    /// Features:
    /// - Pub/sub messaging pattern
    /// - Request/response pattern
    /// - Topic-based and pattern-based routing
    /// - Async-first design
    /// - Non-blocking operations
    /// - Per-publisher rate limiting (BUS-03)
    /// - Topic name validation (BUS-06)
    /// </summary>
    public sealed class DefaultMessageBus(ILogger? logger = null) : IMessageBus
    {
        private readonly BoundedDictionary<string, List<Subscription>> _subscriptions = new BoundedDictionary<string, List<Subscription>>(1000);
        private readonly BoundedDictionary<string, List<ResponseSubscription>> _responseSubscriptions = new BoundedDictionary<string, List<ResponseSubscription>>(1000);
        private readonly BoundedDictionary<string, PatternSubscription> _patternSubscriptions = new BoundedDictionary<string, PatternSubscription>(1000);
        private readonly ILogger? _logger = logger;
        private readonly Lock _subscriptionLock = new();
        private long _subscriptionIdCounter;

        // BUS-03: Per-publisher rate limiting to prevent flooding DoS
        private readonly BoundedDictionary<string, SlidingWindowRateLimiter> _rateLimiters = new BoundedDictionary<string, SlidingWindowRateLimiter>(1000);

        /// <summary>
        /// Maximum messages per second per publisher. Default: 1000.
        /// </summary>
        public int RateLimitPerSecond { get; init; } = 1000;

        /// <summary>
        /// Publish a message to all subscribers (fire and forget).
        /// Validates topic name and enforces per-publisher rate limiting.
        /// </summary>
        public async Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(message);

            // BUS-06: Validate topic name against injection patterns
            TopicValidator.ValidateTopic(topic);

            // BUS-03: Per-publisher rate limiting
            var publisherId = message.Identity?.ActorId ?? message.Source ?? "anonymous";
            var limiter = _rateLimiters.GetOrAdd(publisherId,
                _ => new SlidingWindowRateLimiter(RateLimitPerSecond, TimeSpan.FromSeconds(1)));

            if (!limiter.TryAcquire())
            {
                _logger?.LogWarning("Rate limit exceeded for publisher {Publisher} on topic {Topic}", publisherId, topic);
                throw new InvalidOperationException($"Rate limit exceeded for publisher '{publisherId}'. Max {RateLimitPerSecond} messages/second.");
            }

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
        /// Validates topic name and enforces per-publisher rate limiting.
        /// </summary>
        public async Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(message);

            // BUS-06: Validate topic name against injection patterns
            TopicValidator.ValidateTopic(topic);

            // BUS-03: Per-publisher rate limiting
            var publisherId = message.Identity?.ActorId ?? message.Source ?? "anonymous";
            var limiter = _rateLimiters.GetOrAdd(publisherId,
                _ => new SlidingWindowRateLimiter(RateLimitPerSecond, TimeSpan.FromSeconds(1)));

            if (!limiter.TryAcquire())
            {
                _logger?.LogWarning("Rate limit exceeded for publisher {Publisher} on topic {Topic}", publisherId, topic);
                throw new InvalidOperationException($"Rate limit exceeded for publisher '{publisherId}'. Max {RateLimitPerSecond} messages/second.");
            }

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
        /// Validates topic name and enforces per-publisher rate limiting.
        /// </summary>
        public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(message);

            // BUS-06: Validate topic name
            TopicValidator.ValidateTopic(topic);

            // BUS-03: Per-publisher rate limiting
            var publisherId = message.Identity?.ActorId ?? message.Source ?? "anonymous";
            var limiter = _rateLimiters.GetOrAdd(publisherId,
                _ => new SlidingWindowRateLimiter(RateLimitPerSecond, TimeSpan.FromSeconds(1)));

            if (!limiter.TryAcquire())
            {
                _logger?.LogWarning("Rate limit exceeded for publisher {Publisher} on topic {Topic}", publisherId, topic);
                return MessageResponse.Error($"Rate limit exceeded for publisher '{publisherId}'", "RATE_LIMITED");
            }

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
        /// Validates topic name against injection patterns.
        /// </summary>
        public IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(handler);

            // BUS-06: Validate topic name
            TopicValidator.ValidateTopic(topic);

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
        /// Validates topic name against injection patterns.
        /// </summary>
        public IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
        {
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(handler);

            // BUS-06: Validate topic name
            TopicValidator.ValidateTopic(topic);

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
        /// Validates pattern against injection attacks.
        /// </summary>
        public IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentNullException.ThrowIfNull(handler);

            // BUS-06: Validate topic pattern
            TopicValidator.ValidateTopicPattern(pattern);

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
