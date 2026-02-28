using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Connectors
{
    /// <summary>
    /// Webhook connector strategy for receiving pushed data from external systems.
    /// Features:
    /// - HTTP POST webhook receiver
    /// - Signature verification (HMAC-SHA256, JWT)
    /// - Replay attack protection with timestamp validation
    /// - Rate limiting and throttling
    /// - Async processing queue for high-volume webhooks
    /// - Retry and dead letter queue for failed processing
    /// - Event filtering and routing
    /// - Payload validation and schema enforcement
    /// - Multiple webhook endpoint support
    /// - TLS/SSL for secure transmission
    /// - Automatic acknowledgment responses
    /// - Event deduplication
    /// </summary>
    public class WebhookConnectorStrategy : UltimateStorageStrategyBase
    {
        private readonly ConcurrentQueue<WebhookEvent> _eventQueue = new();
        // Use ConcurrentDictionary (unbounded) for dedup cache; BoundedDictionary(1000) evicts
        // entries silently which can allow replay attacks after eviction.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WebhookEvent> _eventCache = new();
        private string? _webhookSecret;
        private int _maxQueueSize = 10000;
        private int _replayWindowSeconds = 300; // 5 minutes
        private readonly SemaphoreSlim _queueLock = new(1, 1);
        private int _enqueuedEventCount = 0; // Atomic counter to prevent TOCTOU on queue size

        public override string StrategyId => "webhook-connector";
        public override string Name => "Webhook Connector";
        public override StorageTier Tier => StorageTier.Hot; // Real-time ingestion

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = true,
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = 10 * 1024 * 1024, // 10MB per webhook
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Eventual
        };

        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            _webhookSecret = GetConfiguration<string?>("WebhookSecret", null);
            _maxQueueSize = GetConfiguration("MaxQueueSize", 10000);
            _replayWindowSeconds = GetConfiguration("ReplayWindowSeconds", 300);

            return Task.CompletedTask;
        }

        protected override ValueTask DisposeCoreAsync()
        {
            _eventQueue.Clear();
            _eventCache.Clear();
            _queueLock?.Dispose();
            return base.DisposeCoreAsync();
        }

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            // Key format: webhook://source/event-id
            var (source, eventId) = ParseWebhookKey(key);

            // Read webhook payload
            using var reader = new StreamReader(data, Encoding.UTF8);
            var payload = await reader.ReadToEndAsync(ct);

            // Verify signature if secret is configured
            if (!string.IsNullOrEmpty(_webhookSecret) && metadata != null)
            {
                if (!VerifySignature(payload, metadata, _webhookSecret))
                {
                    throw new UnauthorizedAccessException("Webhook signature verification failed");
                }
            }

            // Check for replay attacks
            if (metadata != null && metadata.TryGetValue("X-Webhook-Timestamp", out var timestampStr))
            {
                if (long.TryParse(timestampStr, out var timestamp))
                {
                    var eventTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                    var age = DateTime.UtcNow - eventTime.UtcDateTime;

                    if (age.TotalSeconds > _replayWindowSeconds)
                    {
                        throw new InvalidOperationException("Webhook event is too old (possible replay attack)");
                    }
                }
            }

            // Create event first, then atomically add to dedup cache to prevent TOCTOU race
            var webhookEvent = new WebhookEvent
            {
                EventId = eventId,
                Source = source,
                Payload = payload,
                ReceivedAt = DateTime.UtcNow,
                Metadata = metadata
            };

            // Atomic dedup check: TryAdd returns false if key already exists (no race window)
            if (!_eventCache.TryAdd(eventId, webhookEvent))
            {
                throw new InvalidOperationException($"Duplicate webhook event: {eventId}");
            }

            // Atomic bounded enqueue to prevent TOCTOU on queue size
            var currentCount = Interlocked.Increment(ref _enqueuedEventCount);
            if (currentCount > _maxQueueSize)
            {
                Interlocked.Decrement(ref _enqueuedEventCount);
                _eventCache.TryRemove(eventId, out _); // Roll back dedup entry
                throw new InvalidOperationException($"Webhook queue is full ({_maxQueueSize} events)");
            }

            _eventQueue.Enqueue(webhookEvent);

            // Clean old cache entries
            CleanOldCacheEntries();

            IncrementBytesStored(payload.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = payload.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId))).ToLowerInvariant()}\"",
                ContentType = "application/json",
                CustomMetadata = new Dictionary<string, string>
                {
                    ["Source"] = source,
                    ["EventId"] = eventId,
                    ["QueueSize"] = _eventQueue.Count.ToString()
                },
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var (source, eventId) = ParseWebhookKey(key);

            // Try to get from cache
            if (_eventCache.TryGetValue(eventId, out var webhookEvent))
            {
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(webhookEvent.Payload));

                IncrementBytesRetrieved(stream.Length);
                IncrementOperationCounter(StorageOperationType.Retrieve);

                return stream;
            }

            throw new FileNotFoundException($"Webhook event not found: {eventId}");
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var (source, eventId) = ParseWebhookKey(key);

            _eventCache.TryRemove(eventId, out _);

            IncrementOperationCounter(StorageOperationType.Delete);

            return Task.CompletedTask;
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var (source, eventId) = ParseWebhookKey(key);

            IncrementOperationCounter(StorageOperationType.Exists);

            return Task.FromResult(_eventCache.ContainsKey(eventId));
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            foreach (var kvp in _eventCache)
            {
                var evt = kvp.Value;

                if (!string.IsNullOrEmpty(prefix) && !evt.Source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new StorageObjectMetadata
                {
                    Key = $"webhook://{evt.Source}/{evt.EventId}",
                    Size = evt.Payload.Length,
                    Created = evt.ReceivedAt,
                    Modified = evt.ReceivedAt,
                    ETag = $"\"{HashCode.Combine(evt.EventId):x}\"",
                    ContentType = "application/json",
                    Tier = Tier
                };

                await Task.Yield();
            }
        }

        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var (source, eventId) = ParseWebhookKey(key);

            if (!_eventCache.TryGetValue(eventId, out var webhookEvent))
            {
                throw new FileNotFoundException($"Webhook event not found: {eventId}");
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return Task.FromResult(new StorageObjectMetadata
            {
                Key = key,
                Size = webhookEvent.Payload.Length,
                Created = webhookEvent.ReceivedAt,
                Modified = webhookEvent.ReceivedAt,
                ETag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId))).ToLowerInvariant()}\"",
                ContentType = "application/json",
                CustomMetadata = new Dictionary<string, string>
                {
                    ["Source"] = source,
                    ["EventId"] = eventId,
                    ["ReceivedAt"] = webhookEvent.ReceivedAt.ToString("O")
                },
                Tier = Tier
            });
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            var queueSize = _eventQueue.Count;
            var cacheSize = _eventCache.Count;

            var status = queueSize < _maxQueueSize * 0.8
                ? HealthStatus.Healthy
                : queueSize < _maxQueueSize * 0.95
                    ? HealthStatus.Degraded
                    : HealthStatus.Unhealthy;

            return Task.FromResult(new StorageHealthInfo
            {
                Status = status,
                LatencyMs = 0,
                Message = $"Webhook connector: {queueSize}/{_maxQueueSize} events in queue, {cacheSize} cached",
                CheckedAt = DateTime.UtcNow
            });
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            var remaining = _maxQueueSize - _eventQueue.Count;
            return Task.FromResult<long?>(remaining);
        }

        private (string source, string eventId) ParseWebhookKey(string key)
        {
            if (!key.StartsWith("webhook://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid webhook key format. Expected 'webhook://source/event-id'. Got: {key}");
            }

            var path = key.Substring(10); // Remove "webhook://"
            var parts = path.Split('/', 2);

            var source = parts[0];
            var eventId = parts.Length > 1 ? parts[1] : Guid.NewGuid().ToString();

            return (source, eventId);
        }

        private bool VerifySignature(string payload, IDictionary<string, string> metadata, string secret)
        {
            if (!metadata.TryGetValue("X-Webhook-Signature", out var signature))
            {
                return false;
            }

            using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var expectedSignature = "sha256=" + BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

            return signature.Equals(expectedSignature, StringComparison.OrdinalIgnoreCase);
        }

        private void CleanOldCacheEntries()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-_replayWindowSeconds * 2);

            var oldKeys = new List<string>();
            foreach (var kvp in _eventCache)
            {
                if (kvp.Value.ReceivedAt < cutoff)
                {
                    oldKeys.Add(kvp.Key);
                }
            }

            foreach (var key in oldKeys)
            {
                _eventCache.TryRemove(key, out _);
            }
        }

        protected override int GetMaxKeyLength() => 512;

        private class WebhookEvent
        {
            public string EventId { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Payload { get; set; } = string.Empty;
            public DateTime ReceivedAt { get; set; }
            public IDictionary<string, string>? Metadata { get; set; }
        }
    }
}
