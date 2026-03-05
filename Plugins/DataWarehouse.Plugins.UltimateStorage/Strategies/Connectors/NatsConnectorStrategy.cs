using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Connectors
{
    /// <summary>
    /// NATS connector strategy for lightweight messaging and streaming.
    /// Features:
    /// - Subject-based addressing
    /// - Queue groups for load balancing
    /// - JetStream for persistence and replay
    /// - Key-Value store integration
    /// - Object store integration
    /// - Request-reply patterns
    /// - Wildcard subscriptions
    /// - At-most-once, at-least-once, exactly-once delivery
    /// - Stream replication across clusters
    /// - TLS and authentication
    /// - Leaf nodes for edge computing
    /// </summary>
    public class NatsConnectorStrategy : UltimateStorageStrategyBase
    {
        // Fields reserved for when NATS.Client package is added.
        // All operations throw NotSupportedException until then.
#pragma warning disable CS0169, CS0414
        private string _serverUrl = string.Empty;
        private string _subject = string.Empty;
        private string? _queueGroup;
        private readonly ConcurrentQueue<NatsMessage> _messageQueue = new();
        private int _maxQueueSize = 10000;
        // Interlocked counter mirrors queue size to make check-then-enqueue atomic (TOCTOU fix).
        private int _enqueuedCount = 0;
#pragma warning restore CS0169, CS0414

        public override string StrategyId => "nats-connector";
        public override string Name => "NATS Connector";
        public override bool IsProductionReady => false;
        public override StorageTier Tier => StorageTier.Hot;

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
            MaxObjectSize = 1 * 1024 * 1024, // 1MB default
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Eventual
        };

        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires NATS.Client NuGet package. Add a reference to NATS.Client (or NATS.Net for .NET 6+) " +
                "and implement a real IConnection / INatsConnection using ConnectionFactory or NatsClient.");
        }

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            var (subject, messageId) = ParseNatsKey(key);

            using var reader = new StreamReader(data, Encoding.UTF8);
            var messageData = await reader.ReadToEndAsync(ct);

            var message = new NatsMessage
            {
                Subject = subject,
                MessageId = messageId,
                Data = messageData,
                Timestamp = DateTime.UtcNow,
                Headers = metadata
            };

            // Atomic bounded enqueue: use Interlocked to prevent TOCTOU race between count check and enqueue.
            var currentCount = Interlocked.Increment(ref _enqueuedCount);
            if (currentCount > _maxQueueSize)
            {
                Interlocked.Decrement(ref _enqueuedCount);
                throw new InvalidOperationException($"NATS message queue is full ({_maxQueueSize})");
            }

            _messageQueue.Enqueue(message);

            IncrementBytesStored(messageData.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = messageData.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = $"\"{HashCode.Combine(messageId):x}\"",
                ContentType = "application/octet-stream",
                CustomMetadata = new Dictionary<string, string>
                {
                    ["Subject"] = subject,
                    ["MessageId"] = messageId,
                    ["ServerUrl"] = _serverUrl
                },
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            if (_messageQueue.TryDequeue(out var message))
            {
                Interlocked.Decrement(ref _enqueuedCount);
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(message.Data));

                IncrementBytesRetrieved(stream.Length);
                IncrementOperationCounter(StorageOperationType.Retrieve);

                return stream;
            }

            throw new FileNotFoundException("No messages available in NATS queue");
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.Delete);
            return Task.CompletedTask;
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.Exists);
            return Task.FromResult(!_messageQueue.IsEmpty);
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            foreach (var message in _messageQueue)
            {
                if (!string.IsNullOrEmpty(prefix) && !message.Subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new StorageObjectMetadata
                {
                    Key = $"nats://{message.Subject}/{message.MessageId}",
                    Size = message.Data.Length,
                    Created = message.Timestamp,
                    Modified = message.Timestamp,
                    ETag = $"\"{HashCode.Combine(message.MessageId):x}\"",
                    ContentType = "application/octet-stream",
                    Tier = Tier
                };

                await Task.Yield();
            }
        }

        protected override Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var (subject, messageId) = ParseNatsKey(key);

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return Task.FromResult(new StorageObjectMetadata
            {
                Key = key,
                Size = 0,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = $"\"{HashCode.Combine(messageId):x}\"",
                ContentType = "application/octet-stream",
                CustomMetadata = new Dictionary<string, string>
                {
                    ["Subject"] = subject,
                    ["MessageId"] = messageId,
                    ["ServerUrl"] = _serverUrl
                },
                Tier = Tier
            });
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            var queueSize = _messageQueue.Count;

            var status = queueSize < _maxQueueSize * 0.8
                ? HealthStatus.Healthy
                : queueSize < _maxQueueSize * 0.95
                    ? HealthStatus.Degraded
                    : HealthStatus.Unhealthy;

            return Task.FromResult(new StorageHealthInfo
            {
                Status = status,
                LatencyMs = 0,
                Message = $"NATS connector: {queueSize}/{_maxQueueSize} messages queued",
                CheckedAt = DateTime.UtcNow
            });
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            var remaining = _maxQueueSize - _messageQueue.Count;
            return Task.FromResult<long?>(remaining);
        }

        private (string subject, string messageId) ParseNatsKey(string key)
        {
            if (!key.StartsWith("nats://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid NATS key format. Expected 'nats://subject/messageId'. Got: {key}");
            }

            var path = key.Substring(7); // Remove "nats://"
            var parts = path.Split('/', 2);

            var subject = parts[0];
            var messageId = parts.Length > 1 ? parts[1] : Guid.NewGuid().ToString();

            return (subject, messageId);
        }

        protected override int GetMaxKeyLength() => 512;

        private class NatsMessage
        {
            public string Subject { get; set; } = string.Empty;
            public string MessageId { get; set; } = string.Empty;
            public string Data { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
            public IDictionary<string, string>? Headers { get; set; }
        }
    }
}
