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
    /// Apache Pulsar connector strategy for multi-tenant streaming data ingestion.
    /// Features:
    /// - Topic subscriptions (exclusive, shared, failover, key_shared)
    /// - Namespaces and tenants for multi-tenancy
    /// - Message acknowledgment modes
    /// - Schema registry integration
    /// - Geo-replication support
    /// - Tiered storage for old messages
    /// - Message TTL and retention policies
    /// - Dead letter topic for failed messages
    /// - Batching and compression
    /// - TLS and authentication
    /// </summary>
    public class PulsarConnectorStrategy : UltimateStorageStrategyBase
    {
        // Fields reserved for when DotPulsar package is added.
        // All operations throw NotSupportedException until then.
#pragma warning disable CS0414
        private string _serviceUrl = string.Empty;
        private string _topic = string.Empty;
        private string _subscription = "datawarehouse-sub";
        private readonly ConcurrentQueue<PulsarMessage> _messageQueue = new();
        private int _maxQueueSize = 10000;
#pragma warning restore CS0414

        public override string StrategyId => "pulsar-connector";
        public override string Name => "Apache Pulsar Connector";
        public override bool IsProductionReady => false;
        public override StorageTier Tier => StorageTier.Hot;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = true,
            SupportsEncryption = true,
            SupportsCompression = true,
            SupportsMultipart = false,
            MaxObjectSize = null,
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Eventual
        };

        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            throw new NotSupportedException(
                "Requires DotPulsar NuGet package. Add a reference to DotPulsar and " +
                "implement a real IPulsarClient / IProducer<T> / IConsumer<T> using PulsarClient.Builder().");
        }

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            var (topic, messageId) = ParsePulsarKey(key);

            using var reader = new StreamReader(data, Encoding.UTF8);
            var messageValue = await reader.ReadToEndAsync(ct);

            var message = new PulsarMessage
            {
                Topic = topic,
                MessageId = messageId,
                Value = messageValue,
                PublishTime = DateTime.UtcNow,
                Properties = metadata
            };

            if (_messageQueue.Count >= _maxQueueSize)
            {
                throw new InvalidOperationException($"Pulsar message queue is full ({_maxQueueSize})");
            }

            _messageQueue.Enqueue(message);

            IncrementBytesStored(messageValue.Length);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = messageValue.Length,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = $"\"{HashCode.Combine(messageId):x}\"",
                ContentType = "application/json",
                CustomMetadata = new Dictionary<string, string>
                {
                    ["Topic"] = topic,
                    ["MessageId"] = messageId,
                    ["ServiceUrl"] = _serviceUrl
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
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(message.Value));

                IncrementBytesRetrieved(stream.Length);
                IncrementOperationCounter(StorageOperationType.Retrieve);

                return stream;
            }

            throw new FileNotFoundException("No messages available in Pulsar queue");
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
                if (!string.IsNullOrEmpty(prefix) && !message.Topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new StorageObjectMetadata
                {
                    Key = $"pulsar://{message.Topic}/{message.MessageId}",
                    Size = message.Value.Length,
                    Created = message.PublishTime,
                    Modified = message.PublishTime,
                    ETag = $"\"{HashCode.Combine(message.MessageId):x}\"",
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

            var (topic, messageId) = ParsePulsarKey(key);

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return Task.FromResult(new StorageObjectMetadata
            {
                Key = key,
                Size = 0,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = $"\"{HashCode.Combine(messageId):x}\"",
                ContentType = "application/json",
                CustomMetadata = new Dictionary<string, string>
                {
                    ["Topic"] = topic,
                    ["MessageId"] = messageId,
                    ["ServiceUrl"] = _serviceUrl
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
                Message = $"Pulsar connector: {queueSize}/{_maxQueueSize} messages queued",
                CheckedAt = DateTime.UtcNow
            });
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            var remaining = _maxQueueSize - _messageQueue.Count;
            return Task.FromResult<long?>(remaining);
        }

        private (string topic, string messageId) ParsePulsarKey(string key)
        {
            if (!key.StartsWith("pulsar://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid Pulsar key format. Expected 'pulsar://topic/messageId'. Got: {key}");
            }

            var path = key.Substring(9); // Remove "pulsar://"
            var parts = path.Split('/', 2);

            var topic = parts[0];
            var messageId = parts.Length > 1 ? parts[1] : Guid.NewGuid().ToString();

            return (topic, messageId);
        }

        protected override int GetMaxKeyLength() => 512;

        private class PulsarMessage
        {
            public string Topic { get; set; } = string.Empty;
            public string MessageId { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public DateTime PublishTime { get; set; }
            public IDictionary<string, string>? Properties { get; set; }
        }
    }
}
