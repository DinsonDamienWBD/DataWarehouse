using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.SDK.Contracts.Streaming
{
    #region Streaming Strategy Interface

    /// <summary>
    /// Defines a streaming strategy for real-time data ingestion and delivery.
    /// Supports publish-subscribe patterns, message queuing, and event streaming.
    /// Implementations provide integration with various message brokers (Kafka, RabbitMQ, Pulsar, NATS, Redis Streams, etc).
    /// </summary>
    public interface IStreamingStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this streaming strategy.
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this streaming strategy.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the capabilities supported by this streaming backend.
        /// Describes features like ordering guarantees, partitioning, and delivery semantics.
        /// </summary>
        StreamingCapabilities Capabilities { get; }

        /// <summary>
        /// Gets the supported streaming protocols.
        /// Examples: Kafka, RabbitMQ, AMQP, MQTT, Pulsar, NATS, Redis Streams, Kinesis.
        /// </summary>
        IReadOnlyList<string> SupportedProtocols { get; }

        /// <summary>
        /// Publishes a message to a stream/topic asynchronously.
        /// </summary>
        /// <param name="streamName">The name of the stream/topic to publish to.</param>
        /// <param name="message">The message to publish.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Metadata about the published message including offset/sequence number.</returns>
        /// <exception cref="ArgumentNullException">Thrown when streamName or message is null.</exception>
        /// <exception cref="StreamingException">Thrown when publish operation fails.</exception>
        Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);

        /// <summary>
        /// Publishes multiple messages to a stream/topic in a batch for better performance.
        /// </summary>
        /// <param name="streamName">The name of the stream/topic to publish to.</param>
        /// <param name="messages">The messages to publish.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Results for each published message.</returns>
        /// <exception cref="ArgumentNullException">Thrown when streamName or messages is null.</exception>
        /// <exception cref="StreamingException">Thrown when publish operation fails.</exception>
        Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default);

        /// <summary>
        /// Subscribes to a stream/topic and receives messages asynchronously.
        /// Returns an async enumerable that yields messages as they arrive.
        /// </summary>
        /// <param name="streamName">The name of the stream/topic to subscribe to.</param>
        /// <param name="consumerGroup">Optional consumer group for load balancing and offset tracking.</param>
        /// <param name="options">Optional subscription options.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>An async enumerable of messages.</returns>
        /// <exception cref="ArgumentNullException">Thrown when streamName is null.</exception>
        /// <exception cref="StreamingException">Thrown when subscription fails.</exception>
        IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, CancellationToken ct = default);

        /// <summary>
        /// Creates a new stream/topic with the specified configuration.
        /// </summary>
        /// <param name="streamName">The name of the stream to create.</param>
        /// <param name="config">Configuration for the stream.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="ArgumentNullException">Thrown when streamName is null.</exception>
        /// <exception cref="StreamingException">Thrown when stream creation fails.</exception>
        Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);

        /// <summary>
        /// Deletes an existing stream/topic.
        /// </summary>
        /// <param name="streamName">The name of the stream to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="ArgumentNullException">Thrown when streamName is null.</exception>
        /// <exception cref="StreamingException">Thrown when stream deletion fails.</exception>
        Task DeleteStreamAsync(string streamName, CancellationToken ct = default);

        /// <summary>
        /// Checks if a stream/topic exists.
        /// </summary>
        /// <param name="streamName">The name of the stream to check.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the stream exists, false otherwise.</returns>
        Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);

        /// <summary>
        /// Lists all available streams/topics.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of stream names.</returns>
        IAsyncEnumerable<string> ListStreamsAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets information about a specific stream including partition count, retention, etc.
        /// </summary>
        /// <param name="streamName">The name of the stream.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Stream information.</returns>
        Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);

        /// <summary>
        /// Commits the consumer offset for manual offset management.
        /// Only applicable when using manual offset commit mode.
        /// </summary>
        /// <param name="streamName">The name of the stream.</param>
        /// <param name="consumerGroup">The consumer group.</param>
        /// <param name="offset">The offset to commit.</param>
        /// <param name="ct">Cancellation token.</param>
        Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);

        /// <summary>
        /// Seeks to a specific offset in the stream for consumption.
        /// Allows replaying messages from a specific point.
        /// </summary>
        /// <param name="streamName">The name of the stream.</param>
        /// <param name="consumerGroup">The consumer group.</param>
        /// <param name="offset">The offset to seek to.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default);

        /// <summary>
        /// Gets the current consumer offset for a consumer group.
        /// </summary>
        /// <param name="streamName">The name of the stream.</param>
        /// <param name="consumerGroup">The consumer group.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The current offset.</returns>
        Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default);
    }

    #endregion

    #region Streaming Capabilities

    /// <summary>
    /// Describes the capabilities and features of a streaming backend.
    /// Used to determine what streaming patterns and guarantees are available.
    /// </summary>
    public sealed record StreamingCapabilities
    {
        /// <summary>
        /// Indicates if the backend guarantees message ordering within partitions.
        /// </summary>
        public bool SupportsOrdering { get; init; }

        /// <summary>
        /// Indicates if the backend supports partitioning for parallel processing.
        /// </summary>
        public bool SupportsPartitioning { get; init; }

        /// <summary>
        /// Indicates if the backend supports exactly-once delivery semantics.
        /// </summary>
        public bool SupportsExactlyOnce { get; init; }

        /// <summary>
        /// Indicates if the backend supports transactions across multiple operations.
        /// </summary>
        public bool SupportsTransactions { get; init; }

        /// <summary>
        /// Indicates if the backend supports message replay from historical offsets.
        /// </summary>
        public bool SupportsReplay { get; init; }

        /// <summary>
        /// Indicates if the backend supports message retention/persistence.
        /// </summary>
        public bool SupportsPersistence { get; init; }

        /// <summary>
        /// Indicates if the backend supports consumer groups for load balancing.
        /// </summary>
        public bool SupportsConsumerGroups { get; init; }

        /// <summary>
        /// Indicates if the backend supports dead letter queues for failed messages.
        /// </summary>
        public bool SupportsDeadLetterQueue { get; init; }

        /// <summary>
        /// Indicates if the backend supports message acknowledgment.
        /// </summary>
        public bool SupportsAcknowledgment { get; init; }

        /// <summary>
        /// Indicates if the backend supports message headers/metadata.
        /// </summary>
        public bool SupportsHeaders { get; init; }

        /// <summary>
        /// Indicates if the backend supports message compression.
        /// </summary>
        public bool SupportsCompression { get; init; }

        /// <summary>
        /// Indicates if the backend supports message filtering/routing.
        /// </summary>
        public bool SupportsMessageFiltering { get; init; }

        /// <summary>
        /// Maximum message size in bytes that the backend can handle.
        /// Null indicates no practical limit.
        /// </summary>
        public long? MaxMessageSize { get; init; }

        /// <summary>
        /// Maximum retention period for messages.
        /// Null indicates unlimited or backend-managed retention.
        /// </summary>
        public TimeSpan? MaxRetention { get; init; }

        /// <summary>
        /// Default delivery guarantee provided by this backend.
        /// </summary>
        public DeliveryGuarantee DefaultDeliveryGuarantee { get; init; } = DeliveryGuarantee.AtLeastOnce;

        /// <summary>
        /// Supported delivery guarantees.
        /// </summary>
        public IReadOnlyList<DeliveryGuarantee> SupportedDeliveryGuarantees { get; init; } = new[] { DeliveryGuarantee.AtLeastOnce };

        /// <summary>
        /// Default capabilities for basic streaming.
        /// </summary>
        public static StreamingCapabilities Basic => new()
        {
            SupportsOrdering = true,
            SupportsPersistence = true,
            SupportsAcknowledgment = true,
            DefaultDeliveryGuarantee = DeliveryGuarantee.AtLeastOnce,
            SupportedDeliveryGuarantees = new[] { DeliveryGuarantee.AtMostOnce, DeliveryGuarantee.AtLeastOnce }
        };

        /// <summary>
        /// Full enterprise-grade capabilities.
        /// </summary>
        public static StreamingCapabilities Enterprise => new()
        {
            SupportsOrdering = true,
            SupportsPartitioning = true,
            SupportsExactlyOnce = true,
            SupportsTransactions = true,
            SupportsReplay = true,
            SupportsPersistence = true,
            SupportsConsumerGroups = true,
            SupportsDeadLetterQueue = true,
            SupportsAcknowledgment = true,
            SupportsHeaders = true,
            SupportsCompression = true,
            SupportsMessageFiltering = true,
            DefaultDeliveryGuarantee = DeliveryGuarantee.ExactlyOnce,
            SupportedDeliveryGuarantees = Enum.GetValues<DeliveryGuarantee>()
        };
    }

    #endregion

    #region Streaming Types

    /// <summary>
    /// Represents a message in a stream.
    /// </summary>
    public sealed record StreamMessage
    {
        /// <summary>
        /// Unique identifier for this message.
        /// </summary>
        public string? MessageId { get; init; }

        /// <summary>
        /// The message key for partitioning and ordering.
        /// Messages with the same key are guaranteed to be ordered.
        /// </summary>
        public string? Key { get; init; }

        /// <summary>
        /// The message payload as bytes.
        /// </summary>
        public required byte[] Data { get; init; }

        /// <summary>
        /// Optional message headers/metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Headers { get; init; }

        /// <summary>
        /// Timestamp when the message was produced.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// The partition this message belongs to (if applicable).
        /// </summary>
        public int? Partition { get; init; }

        /// <summary>
        /// The offset/sequence number of this message in the stream.
        /// </summary>
        public long? Offset { get; init; }

        /// <summary>
        /// Content type of the message data (MIME type).
        /// </summary>
        public string? ContentType { get; init; }
    }

    /// <summary>
    /// Result of a publish operation.
    /// </summary>
    public sealed record PublishResult
    {
        /// <summary>
        /// Unique identifier assigned to the published message.
        /// </summary>
        public required string MessageId { get; init; }

        /// <summary>
        /// The partition the message was published to.
        /// </summary>
        public int? Partition { get; init; }

        /// <summary>
        /// The offset/sequence number assigned to the message.
        /// </summary>
        public long? Offset { get; init; }

        /// <summary>
        /// Timestamp when the message was published.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Indicates if the publish was successful.
        /// </summary>
        public bool Success { get; init; } = true;

        /// <summary>
        /// Error message if publish failed.
        /// </summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Represents a partition in a stream.
    /// </summary>
    public sealed record StreamPartition
    {
        /// <summary>
        /// The partition number/identifier.
        /// </summary>
        public required int PartitionId { get; init; }

        /// <summary>
        /// The current offset (end of stream) for this partition.
        /// </summary>
        public long CurrentOffset { get; init; }

        /// <summary>
        /// The starting offset for this partition.
        /// </summary>
        public long StartOffset { get; init; }

        /// <summary>
        /// The leader node/broker for this partition.
        /// </summary>
        public string? Leader { get; init; }

        /// <summary>
        /// Replica nodes/brokers for this partition.
        /// </summary>
        public IReadOnlyList<string>? Replicas { get; init; }
    }

    /// <summary>
    /// Delivery guarantee semantics for message streaming.
    /// </summary>
    public enum DeliveryGuarantee
    {
        /// <summary>
        /// At-most-once delivery - messages may be lost but never duplicated.
        /// Fastest but least reliable. Fire-and-forget.
        /// </summary>
        AtMostOnce,

        /// <summary>
        /// At-least-once delivery - messages are never lost but may be duplicated.
        /// Good balance of performance and reliability. Requires idempotent consumers.
        /// </summary>
        AtLeastOnce,

        /// <summary>
        /// Exactly-once delivery - messages are delivered exactly once with no loss or duplication.
        /// Most reliable but slowest. Requires transactional support.
        /// </summary>
        ExactlyOnce
    }

    /// <summary>
    /// Represents a consumer group for load balancing and offset tracking.
    /// </summary>
    public sealed record ConsumerGroup
    {
        /// <summary>
        /// Unique identifier for the consumer group.
        /// </summary>
        public required string GroupId { get; init; }

        /// <summary>
        /// Optional consumer instance identifier within the group.
        /// </summary>
        public string? ConsumerId { get; init; }

        /// <summary>
        /// Consumer group metadata/configuration.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Represents an offset/position in a stream.
    /// </summary>
    public sealed record StreamOffset
    {
        /// <summary>
        /// The partition identifier.
        /// </summary>
        public int Partition { get; init; }

        /// <summary>
        /// The offset value.
        /// </summary>
        public long Offset { get; init; }

        /// <summary>
        /// Timestamp associated with this offset.
        /// </summary>
        public DateTime? Timestamp { get; init; }

        /// <summary>
        /// Creates an offset representing the beginning of the stream.
        /// </summary>
        public static StreamOffset Beginning(int partition = 0) => new() { Partition = partition, Offset = 0 };

        /// <summary>
        /// Creates an offset representing the end of the stream.
        /// </summary>
        public static StreamOffset End(int partition = 0) => new() { Partition = partition, Offset = long.MaxValue };
    }

    /// <summary>
    /// Configuration for creating a stream.
    /// </summary>
    public sealed record StreamConfiguration
    {
        /// <summary>
        /// Number of partitions for the stream.
        /// Higher values enable more parallelism.
        /// </summary>
        public int? PartitionCount { get; init; }

        /// <summary>
        /// Replication factor for fault tolerance.
        /// Number of replicas to maintain for each partition.
        /// </summary>
        public int? ReplicationFactor { get; init; }

        /// <summary>
        /// Message retention period.
        /// Messages older than this will be deleted.
        /// </summary>
        public TimeSpan? RetentionPeriod { get; init; }

        /// <summary>
        /// Maximum size of the stream before old messages are deleted.
        /// </summary>
        public long? MaxSizeBytes { get; init; }

        /// <summary>
        /// Compression type for messages.
        /// </summary>
        public string? CompressionType { get; init; }

        /// <summary>
        /// Additional backend-specific configuration.
        /// </summary>
        public IReadOnlyDictionary<string, object>? AdditionalSettings { get; init; }
    }

    /// <summary>
    /// Options for subscribing to a stream.
    /// </summary>
    public sealed record SubscriptionOptions
    {
        /// <summary>
        /// Starting offset for consumption.
        /// If null, starts from the latest message.
        /// </summary>
        public StreamOffset? StartOffset { get; init; }

        /// <summary>
        /// Delivery guarantee to use for this subscription.
        /// </summary>
        public DeliveryGuarantee? DeliveryGuarantee { get; init; }

        /// <summary>
        /// Whether to automatically commit offsets after processing.
        /// If false, offsets must be manually committed.
        /// </summary>
        public bool AutoCommit { get; init; } = true;

        /// <summary>
        /// Auto-commit interval if AutoCommit is true.
        /// </summary>
        public TimeSpan? AutoCommitInterval { get; init; }

        /// <summary>
        /// Maximum number of messages to buffer locally.
        /// </summary>
        public int? MaxBufferSize { get; init; }

        /// <summary>
        /// Message filter expression (backend-specific).
        /// </summary>
        public string? FilterExpression { get; init; }

        /// <summary>
        /// Additional backend-specific options.
        /// </summary>
        public IReadOnlyDictionary<string, object>? AdditionalOptions { get; init; }
    }

    /// <summary>
    /// Information about a stream.
    /// </summary>
    public sealed record StreamInfo
    {
        /// <summary>
        /// The name of the stream.
        /// </summary>
        public required string StreamName { get; init; }

        /// <summary>
        /// Number of partitions in the stream.
        /// </summary>
        public int PartitionCount { get; init; }

        /// <summary>
        /// Information about each partition.
        /// </summary>
        public IReadOnlyList<StreamPartition>? Partitions { get; init; }

        /// <summary>
        /// Total number of messages in the stream.
        /// </summary>
        public long? MessageCount { get; init; }

        /// <summary>
        /// Total size of the stream in bytes.
        /// </summary>
        public long? SizeBytes { get; init; }

        /// <summary>
        /// Retention configuration for the stream.
        /// </summary>
        public TimeSpan? RetentionPeriod { get; init; }

        /// <summary>
        /// Timestamp when the stream was created.
        /// </summary>
        public DateTime? CreatedAt { get; init; }

        /// <summary>
        /// Additional stream metadata.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    }

    #endregion

    #region Streaming Exception

    /// <summary>
    /// Exception thrown when streaming operations fail.
    /// </summary>
    public sealed class StreamingException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamingException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        public StreamingException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamingException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        public StreamingException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion

    #region Streaming Strategy Base Class

    /// <summary>
    /// Abstract base class for streaming strategy implementations.
    /// Provides common functionality including subscription management, offset tracking,
    /// and default implementations for optional operations.
    /// Thread-safe for concurrent operations.
    /// </summary>
    public abstract class StreamingStrategyBase : StrategyBase, IStreamingStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this streaming strategy.
        /// </summary>
        public override abstract string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this streaming strategy.
        /// </summary>
        public override abstract string Name { get; }

        /// <summary>
        /// Gets the capabilities supported by this streaming backend.
        /// </summary>
        public abstract StreamingCapabilities Capabilities { get; }

        /// <summary>
        /// Gets the supported streaming protocols.
        /// </summary>
        public abstract IReadOnlyList<string> SupportedProtocols { get; }

        /// <inheritdoc/>
        public abstract Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default);

        /// <inheritdoc/>
        public virtual async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default)
        {
            ValidateStreamName(streamName);
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));

            // Default implementation: publish messages one by one
            // Derived classes should override with optimized batch implementation
            var results = new List<PublishResult>();

            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();
                var result = await PublishAsync(streamName, message, ct);
                results.Add(result);
            }

            return results;
        }

        /// <inheritdoc/>
        public abstract IAsyncEnumerable<StreamMessage> SubscribeAsync(string streamName, ConsumerGroup? consumerGroup = null, SubscriptionOptions? options = null, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task DeleteStreamAsync(string streamName, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract IAsyncEnumerable<string> ListStreamsAsync(CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default);

        /// <inheritdoc/>
        public virtual Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default)
        {
            if (!Capabilities.SupportsAcknowledgment)
                throw new NotSupportedException($"{Name} does not support manual offset commits.");

            return CommitOffsetAsyncCore(streamName, consumerGroup, offset, ct);
        }

        /// <inheritdoc/>
        public virtual Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default)
        {
            if (!Capabilities.SupportsReplay)
                throw new NotSupportedException($"{Name} does not support seeking to offsets.");

            return SeekAsyncCore(streamName, consumerGroup, offset, ct);
        }

        /// <inheritdoc/>
        public virtual Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default)
        {
            if (!Capabilities.SupportsConsumerGroups)
                throw new NotSupportedException($"{Name} does not support consumer groups.");

            return GetOffsetAsyncCore(streamName, consumerGroup, ct);
        }

        /// <summary>
        /// Core implementation of offset commit. Override in derived classes.
        /// </summary>
        protected virtual Task CommitOffsetAsyncCore(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct)
        {
            // Default implementation: no-op for strategies that don't support manual commits
            return Task.CompletedTask;
        }

        /// <summary>
        /// Core implementation of seek. Override in derived classes.
        /// </summary>
        protected virtual Task SeekAsyncCore(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct)
        {
            // Default implementation: no-op for strategies that don't support seeking
            return Task.CompletedTask;
        }

        /// <summary>
        /// Core implementation of get offset. Override in derived classes.
        /// </summary>
        protected virtual Task<StreamOffset> GetOffsetAsyncCore(string streamName, ConsumerGroup consumerGroup, CancellationToken ct)
        {
            // Default implementation: return beginning offset
            return Task.FromResult(StreamOffset.Beginning());
        }

        /// <summary>
        /// Validates a stream name and throws if invalid.
        /// </summary>
        /// <param name="streamName">The stream name to validate.</param>
        /// <exception cref="ArgumentNullException">Thrown when streamName is null or empty.</exception>
        protected static void ValidateStreamName(string streamName)
        {
            if (string.IsNullOrWhiteSpace(streamName))
                throw new ArgumentNullException(nameof(streamName), "Stream name cannot be null or empty.");
        }

        /// <summary>
        /// Validates a message and throws if invalid.
        /// </summary>
        /// <param name="message">The message to validate.</param>
        /// <exception cref="ArgumentNullException">Thrown when message or message data is null.</exception>
        protected virtual void ValidateMessage(StreamMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if (message.Data == null)
                throw new ArgumentNullException(nameof(message.Data), "Message data cannot be null.");

            // Check message size if backend has limits
            if (Capabilities.MaxMessageSize.HasValue && message.Data.Length > Capabilities.MaxMessageSize.Value)
                throw new StreamingException($"Message size ({message.Data.Length} bytes) exceeds maximum allowed size ({Capabilities.MaxMessageSize.Value} bytes).");
        }

        /// <summary>
        /// Helper method to assign a partition for a message based on its key.
        /// Uses consistent hashing to ensure messages with the same key go to the same partition.
        /// </summary>
        /// <param name="key">The message key.</param>
        /// <param name="partitionCount">Total number of partitions.</param>
        /// <returns>The assigned partition number.</returns>
        protected static int AssignPartition(string? key, int partitionCount)
        {
            if (partitionCount <= 0)
                throw new ArgumentException("Partition count must be positive.", nameof(partitionCount));

            if (string.IsNullOrEmpty(key))
            {
                // Random partition if no key
                return Random.Shared.Next(0, partitionCount);
            }

            // Consistent hash based on key
            var hash = key.GetHashCode();
            return Math.Abs(hash % partitionCount);
        }

        /// <summary>
        /// Helper method to track consumer offsets in memory.
        /// Useful for strategies that need local offset management.
        /// </summary>
        protected class OffsetTracker
        {
            private readonly Dictionary<string, long> _offsets = new();
            private readonly object _lock = new();

            /// <summary>
            /// Updates the offset for a stream/partition.
            /// </summary>
            public void UpdateOffset(string key, long offset)
            {
                lock (_lock)
                {
                    _offsets[key] = offset;
                }
            }

            /// <summary>
            /// Gets the current offset for a stream/partition.
            /// </summary>
            public long GetOffset(string key)
            {
                lock (_lock)
                {
                    return _offsets.TryGetValue(key, out var offset) ? offset : 0;
                }
            }

            /// <summary>
            /// Clears all tracked offsets.
            /// </summary>
            public void Clear()
            {
                lock (_lock)
                {
                    _offsets.Clear();
                }
            }
        }
    }

    #endregion
}
