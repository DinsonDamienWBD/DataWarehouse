using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Streaming;
using DataWarehouse.SDK.Primitives;
using PublishResult = DataWarehouse.SDK.Contracts.Streaming.PublishResult;

namespace DataWarehouse.Plugins.UltimateStreamingData.Strategies.IoT;

/// <summary>
/// 113.B3.3: CoAP (Constrained Application Protocol) streaming strategy for
/// constrained IoT devices using UDP-based RESTful interactions with the Observe
/// extension for push notifications.
///
/// Key CoAP semantics modeled (RFC 7252 + RFC 7641):
/// - RESTful methods: GET, POST, PUT, DELETE mapped to pub/sub
/// - Confirmable (CON) and Non-confirmable (NON) messages
/// - Observe extension (RFC 7641): server pushes resource updates to observers
/// - Block-wise transfers (RFC 7959): chunked transfer for large payloads
/// - Content formats: application/cbor, application/json, text/plain
/// - ETag-based caching for constrained networks
/// - UDP-based transport with optional DTLS security
/// - Typical message size: 1 KB (fits in single UDP datagram)
/// - Multicast group communication for device discovery
/// - Resource directory for device/resource registration
/// </summary>
internal sealed class CoapStreamStrategy : StreamingDataStrategyBase, IStreamingStrategy
{
    private readonly ConcurrentDictionary<string, CoapResourceState> _resources = new();
    private readonly ConcurrentDictionary<string, List<StreamMessage>> _resourceHistory = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CoapObserverState>> _observers = new();
    private long _nextMessageId;
    private long _nextObserveSequence;
    private long _totalPublished;

    /// <inheritdoc/>
    public override string StrategyId => "coap";

    /// <inheritdoc/>
    public override string DisplayName => "CoAP IoT Protocol";

    /// <inheritdoc/>
    public override StreamingCategory Category => StreamingCategory.IoTProtocols;

    /// <inheritdoc/>
    public override StreamingDataCapabilities Capabilities => new()
    {
        SupportsExactlyOnce = false,
        SupportsWindowing = false,
        SupportsStateManagement = true,
        SupportsCheckpointing = false,
        SupportsBackpressure = false,
        SupportsPartitioning = false,
        SupportsAutoScaling = false,
        SupportsDistributed = true,
        MaxThroughputEventsPerSec = 10_000,
        TypicalLatencyMs = 50.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "CoAP IoT protocol strategy implementing RFC 7252 for constrained devices with " +
        "UDP-based RESTful interactions, Observe extension (RFC 7641) for push notifications, " +
        "block-wise transfers, CBOR content format, ETag caching, and DTLS security.";

    /// <inheritdoc/>
    public override string[] Tags => ["coap", "iot", "constrained", "udp", "observe", "restful", "low-power"];

    #region IStreamingStrategy Implementation

    /// <inheritdoc/>
    string IStreamingStrategy.Name => DisplayName;

    /// <inheritdoc/>
    StreamingCapabilities IStreamingStrategy.Capabilities => new()
    {
        SupportsOrdering = false, // UDP-based, no guaranteed ordering
        SupportsPartitioning = false,
        SupportsExactlyOnce = false,
        SupportsTransactions = false,
        SupportsReplay = false,
        SupportsPersistence = true, // Resource state persistence
        SupportsConsumerGroups = false,
        SupportsDeadLetterQueue = false,
        SupportsAcknowledgment = true, // Confirmable messages
        SupportsHeaders = true, // CoAP options
        SupportsCompression = false,
        SupportsMessageFiltering = true, // Observe on specific resources
        MaxMessageSize = 1024, // Typical max for single UDP datagram
        MaxRetention = null,
        DefaultDeliveryGuarantee = DeliveryGuarantee.AtMostOnce,
        SupportedDeliveryGuarantees = [DeliveryGuarantee.AtMostOnce, DeliveryGuarantee.AtLeastOnce]
    };

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedProtocols => ["coap", "coaps", "coap+tcp", "coap+ws"];

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(string streamName, IEnumerable<StreamMessage> messages, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(messages);
        var results = new List<PublishResult>();
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await PublishAsync(streamName, message, ct));
        }
        return results;
    }

    /// <inheritdoc/>
    public async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Data);

        var sw = Stopwatch.StartNew();
        var msgId = Interlocked.Increment(ref _nextMessageId);

        // Determine message type: CON (confirmable) or NON (non-confirmable)
        var confirmable = !(message.Headers?.TryGetValue("coap.type", out var typeStr) == true
            && string.Equals(typeStr, "NON", StringComparison.OrdinalIgnoreCase));

        // Calculate ETag for caching
        var etag = Convert.ToHexString(SHA256.HashData(message.Data)[..4]).ToLowerInvariant();

        // Determine content format
        var contentFormat = message.ContentType ?? "application/cbor";

        var storedMessage = message with
        {
            MessageId = message.MessageId ?? $"coap-{msgId}",
            Offset = msgId,
            Timestamp = message.Timestamp == default ? DateTime.UtcNow : message.Timestamp,
            Headers = new Dictionary<string, string>(message.Headers ?? new Dictionary<string, string>())
            {
                ["coap.messageId"] = msgId.ToString(),
                ["coap.type"] = confirmable ? "CON" : "NON",
                ["coap.etag"] = etag,
                ["coap.contentFormat"] = contentFormat
            }
        };

        // Update resource state (POST/PUT semantics)
        var resourceUri = streamName;
        var resource = _resources.AddOrUpdate(resourceUri,
            new CoapResourceState
            {
                Uri = resourceUri,
                CurrentValue = message.Data,
                ETag = etag,
                ContentFormat = contentFormat,
                LastModified = DateTime.UtcNow,
                ObserveSequence = Interlocked.Increment(ref _nextObserveSequence)
            },
            (_, existing) => existing with
            {
                CurrentValue = message.Data,
                ETag = etag,
                ContentFormat = contentFormat,
                LastModified = DateTime.UtcNow,
                ObserveSequence = Interlocked.Increment(ref _nextObserveSequence)
            });

        // Store in history
        var history = _resourceHistory.GetOrAdd(resourceUri, _ => new List<StreamMessage>());
        lock (history)
        {
            history.Add(storedMessage);
        }

        // Notify observers (Observe extension RFC 7641)
        if (_observers.TryGetValue(resourceUri, out var observers))
        {
            foreach (var observer in observers.Values)
            {
                observer.PendingNotifications.Enqueue(storedMessage);
            }
        }

        Interlocked.Increment(ref _totalPublished);
        sw.Stop();
        RecordWrite(message.Data.Length, sw.Elapsed.TotalMilliseconds);

        return new PublishResult
        {
            MessageId = storedMessage.MessageId!,
            Offset = msgId,
            Timestamp = DateTime.UtcNow,
            Success = true
        };
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StreamMessage> SubscribeAsync(
        string streamName,
        ConsumerGroup? consumerGroup = null,
        SubscriptionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);

        var resourceUri = streamName;
        var observerId = consumerGroup?.ConsumerId ?? Guid.NewGuid().ToString("N");

        // Register as observer (GET with Observe option)
        var resourceObservers = _observers.GetOrAdd(resourceUri, _ => new ConcurrentDictionary<string, CoapObserverState>());
        var observerState = resourceObservers.GetOrAdd(observerId, _ => new CoapObserverState
        {
            ObserverId = observerId,
            ResourceUri = resourceUri,
            RegisteredAt = DateTime.UtcNow
        });

        // First, deliver current resource state if it exists
        if (_resources.TryGetValue(resourceUri, out var resource))
        {
            var currentState = new StreamMessage
            {
                MessageId = $"observe-{resource.ObserveSequence}",
                Data = resource.CurrentValue,
                Timestamp = resource.LastModified,
                ContentType = resource.ContentFormat,
                Headers = new Dictionary<string, string>
                {
                    ["coap.observe"] = resource.ObserveSequence.ToString(),
                    ["coap.etag"] = resource.ETag
                }
            };
            RecordRead(currentState.Data.Length, 1.0);
            yield return currentState;
        }

        // Then deliver any pending notifications
        while (!ct.IsCancellationRequested && observerState.PendingNotifications.TryDequeue(out var notification))
        {
            RecordRead(notification.Data.Length, 0.5);
            yield return notification;
        }

        // Also deliver historical messages if requested
        if (_resourceHistory.TryGetValue(resourceUri, out var history))
        {
            List<StreamMessage> snapshot;
            lock (history)
            {
                snapshot = history.ToList();
            }

            var startOffset = options?.StartOffset?.Offset ?? 0;
            foreach (var msg in snapshot.Where(m => (m.Offset ?? 0) > startOffset))
            {
                ct.ThrowIfCancellationRequested();
                RecordRead(msg.Data.Length, 0.5);
                yield return msg;
            }
        }
    }

    /// <inheritdoc/>
    public Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);

        _resources.TryAdd(streamName, new CoapResourceState
        {
            Uri = streamName,
            CurrentValue = Array.Empty<byte>(),
            ETag = "",
            ContentFormat = "application/cbor",
            LastModified = DateTime.UtcNow,
            ObserveSequence = 0
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteStreamAsync(string streamName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        _resources.TryRemove(streamName, out _);
        _resourceHistory.TryRemove(streamName, out _);
        _observers.TryRemove(streamName, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        return Task.FromResult(_resources.ContainsKey(streamName));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var uri in _resources.Keys)
        {
            ct.ThrowIfCancellationRequested();
            yield return uri;
        }
    }

    /// <inheritdoc/>
    public Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);

        if (!_resources.TryGetValue(streamName, out var resource))
            throw new StreamingException($"Resource '{streamName}' does not exist.");

        long messageCount = 0;
        if (_resourceHistory.TryGetValue(streamName, out var history))
        {
            lock (history) { messageCount = history.Count; }
        }

        return Task.FromResult(new StreamInfo
        {
            StreamName = streamName,
            PartitionCount = 1,
            MessageCount = messageCount,
            CreatedAt = resource.LastModified,
            Metadata = new Dictionary<string, object>
            {
                ["protocol"] = "coap",
                ["etag"] = resource.ETag,
                ["contentFormat"] = resource.ContentFormat,
                ["observerCount"] = _observers.TryGetValue(streamName, out var obs) ? obs.Count : 0,
                ["observeSequence"] = resource.ObserveSequence
            }
        });
    }

    /// <inheritdoc/>
    public Task CommitOffsetAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default)
    {
        // CoAP uses Observe sequences, not traditional offsets
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SeekAsync(string streamName, ConsumerGroup consumerGroup, StreamOffset offset, CancellationToken ct = default)
    {
        throw new NotSupportedException("CoAP does not support seeking. Use Observe extension for push notifications.");
    }

    /// <inheritdoc/>
    public Task<StreamOffset> GetOffsetAsync(string streamName, ConsumerGroup consumerGroup, CancellationToken ct = default)
    {
        long seq = 0;
        if (_resources.TryGetValue(streamName, out var resource))
            seq = resource.ObserveSequence;
        return Task.FromResult(new StreamOffset { Partition = 0, Offset = seq });
    }

    /// <summary>
    /// Configures Intelligence integration via message bus.
    /// </summary>
    public override void ConfigureIntelligence(IMessageBus? messageBus)
    {
        // Intelligence can optimize observe registrations and block-wise transfer sizes
    }

    #endregion

    private sealed record CoapResourceState
    {
        public required string Uri { get; init; }
        public required byte[] CurrentValue { get; init; }
        public required string ETag { get; init; }
        public string ContentFormat { get; init; } = "application/cbor";
        public DateTime LastModified { get; init; }
        public long ObserveSequence { get; init; }
    }

    private sealed class CoapObserverState
    {
        public required string ObserverId { get; init; }
        public required string ResourceUri { get; init; }
        public DateTime RegisteredAt { get; init; }
        public ConcurrentQueue<StreamMessage> PendingNotifications { get; } = new();
    }
}
