using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Streaming;
using DataWarehouse.SDK.Edge.Protocols;
using PublishResult = DataWarehouse.SDK.Contracts.Streaming.PublishResult;

namespace DataWarehouse.Plugins.UltimateStreamingData.Strategies
{
    /// <summary>
    /// MQTT streaming strategy for edge/IoT messaging using MQTT protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MQTT (Message Queuing Telemetry Transport) is a lightweight publish-subscribe messaging
    /// protocol designed for constrained devices and low-bandwidth, high-latency networks.
    /// It is the de facto standard for IoT communication.
    /// </para>
    /// <para>
    /// <strong>Use Cases:</strong>
    /// - IoT sensor data collection (temperature, humidity, pressure)
    /// - Device command and control (actuators, relays)
    /// - Edge computing message bus
    /// - Real-time telemetry streaming
    /// - Home automation (MQTT is the standard for smart home devices)
    /// </para>
    /// <para>
    /// <strong>Features:</strong>
    /// - Topic-based routing with wildcard subscriptions
    /// - QoS 0/1/2 delivery guarantees
    /// - Auto-reconnect with exponential backoff
    /// - TLS/SSL encryption with mutual authentication
    /// - Retained messages for "last known state"
    /// - Last Will and Testament for offline detection
    /// </para>
    /// <para>
    /// <strong>Configuration:</strong>
    /// Connection settings must be provided via constructor. Settings include broker address,
    /// port, credentials, TLS certificates, and auto-reconnect parameters.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: MQTT streaming strategy (EDGE-02)")]
    public sealed class MqttStreamingStrategy : StreamingStrategyBase
    {
        private readonly IMqttClient _mqttClient;
        private readonly MqttConnectionSettings _connectionSettings;
        private readonly ConcurrentQueue<MqttMessage> _incomingMessages = new();
        private readonly List<string> _subscribedTopics = new();
        private readonly object _topicsLock = new();
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="MqttStreamingStrategy"/> class.
        /// </summary>
        /// <param name="connectionSettings">MQTT broker connection settings.</param>
        /// <exception cref="ArgumentNullException">Thrown when connectionSettings is null.</exception>
        public MqttStreamingStrategy(MqttConnectionSettings connectionSettings)
        {
            _connectionSettings = connectionSettings ?? throw new ArgumentNullException(nameof(connectionSettings));
            _mqttClient = new SDK.Edge.Protocols.MqttClient();

            // Wire up message reception
            _mqttClient.OnMessageReceived += (sender, args) =>
            {
                _incomingMessages.Enqueue(args.Message);
            };
        }

        /// <inheritdoc/>
        public override string StrategyId => "mqtt-streaming";

        /// <inheritdoc/>
        public override string Name => "MQTT Streaming Strategy";

        /// <inheritdoc/>
        public override StreamingCapabilities Capabilities => new()
        {
            SupportsOrdering = true, // MQTT guarantees ordering per topic
            SupportsPartitioning = false, // MQTT doesn't have built-in partitioning
            SupportsExactlyOnce = true, // QoS 2
            SupportsTransactions = false,
            SupportsReplay = false, // No offset-based replay (use retained messages instead)
            SupportsPersistence = true, // Retained messages, persistent sessions
            SupportsConsumerGroups = false, // MQTT doesn't have consumer groups (use shared subscriptions in MQTT 5.0)
            SupportsDeadLetterQueue = false,
            SupportsAcknowledgment = true, // QoS 1/2
            SupportsHeaders = true, // User properties in MQTT 5.0
            SupportsCompression = false, // Application-level concern
            SupportsMessageFiltering = true, // Topic wildcards (+, #)
            MaxMessageSize = 256 * 1024 * 1024, // MQTT default max: 256 MB
            MaxRetention = null, // Broker-dependent
            DefaultDeliveryGuarantee = DeliveryGuarantee.AtLeastOnce, // QoS 1
            SupportedDeliveryGuarantees = new[]
            {
                DeliveryGuarantee.AtMostOnce,   // QoS 0
                DeliveryGuarantee.AtLeastOnce,  // QoS 1
                DeliveryGuarantee.ExactlyOnce   // QoS 2
            }
        };

        /// <inheritdoc/>
        public override IReadOnlyList<string> SupportedProtocols => new[] { "MQTT", "MQTT 3.1.1", "MQTT 5.0" };

        /// <inheritdoc/>
        public override async Task<PublishResult> PublishAsync(string streamName, StreamMessage message, CancellationToken ct = default)
        {
            ValidateStreamName(streamName);
            ValidateMessage(message);

            // Map StreamMessage to MqttMessage
            var qos = message.Headers?.TryGetValue("mqtt.qos", out var qosStr) == true && int.TryParse(qosStr, out var qosValue)
                ? (MqttQualityOfServiceLevel)qosValue
                : MqttQualityOfServiceLevel.AtLeastOnce;

            var retain = message.Headers?.TryGetValue("mqtt.retain", out var retainStr) == true && bool.TryParse(retainStr, out var retainValue)
                ? retainValue
                : false;

            var mqttMessage = new MqttMessage
            {
                Topic = streamName, // Stream name maps to MQTT topic
                Payload = message.Data,
                QoS = qos,
                Retain = retain,
                UserProperties = message.Headers
            };

            await _mqttClient.PublishAsync(mqttMessage, ct);

            return new PublishResult
            {
                MessageId = message.MessageId ?? Guid.NewGuid().ToString(),
                Partition = null, // MQTT doesn't have partitions
                Offset = null, // MQTT doesn't have offsets
                Timestamp = DateTime.UtcNow,
                Success = true
            };
        }

        /// <inheritdoc/>
        public override async IAsyncEnumerable<StreamMessage> SubscribeAsync(
            string streamName,
            ConsumerGroup? consumerGroup = null,
            SubscriptionOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ValidateStreamName(streamName);

            // MQTT subscription
            var qos = options?.DeliveryGuarantee switch
            {
                DeliveryGuarantee.AtMostOnce => MqttQualityOfServiceLevel.AtMostOnce,
                DeliveryGuarantee.ExactlyOnce => MqttQualityOfServiceLevel.ExactlyOnce,
                _ => MqttQualityOfServiceLevel.AtLeastOnce
            };

            await _mqttClient.SubscribeAsync(new[] { streamName }, qos, ct);

            lock (_topicsLock)
            {
                _subscribedTopics.Add(streamName);
            }

            // Consume messages from queue
            while (!ct.IsCancellationRequested)
            {
                if (_incomingMessages.TryDequeue(out var mqttMsg))
                {
                    // Map MqttMessage to StreamMessage
                    var streamMsg = new StreamMessage
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Key = mqttMsg.Topic,
                        Data = mqttMsg.Payload,
                        Headers = mqttMsg.UserProperties,
                        Timestamp = DateTime.UtcNow,
                        ContentType = "application/octet-stream"
                    };

                    yield return streamMsg;
                }
                else
                {
                    // Wait for messages
                    await Task.Delay(10, ct);
                }
            }
        }

        /// <inheritdoc/>
        public override async Task CreateStreamAsync(string streamName, StreamConfiguration? config = null, CancellationToken ct = default)
        {
            ValidateStreamName(streamName);

            // MQTT doesn't require explicit stream creation (topics are created on-demand)
            // No-op for MQTT
            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override async Task DeleteStreamAsync(string streamName, CancellationToken ct = default)
        {
            ValidateStreamName(streamName);

            // MQTT doesn't support topic deletion (broker manages topic lifecycle)
            // Unsubscribe if subscribed
            await _mqttClient.UnsubscribeAsync(new[] { streamName }, ct);

            lock (_topicsLock)
            {
                _subscribedTopics.Remove(streamName);
            }
        }

        /// <inheritdoc/>
        public override Task<bool> StreamExistsAsync(string streamName, CancellationToken ct = default)
        {
            ValidateStreamName(streamName);

            // MQTT topics exist implicitly (no explicit creation required)
            // Return true if we're subscribed to it
            lock (_topicsLock)
            {
                return Task.FromResult(_subscribedTopics.Contains(streamName));
            }
        }

        /// <inheritdoc/>
        public override async IAsyncEnumerable<string> ListStreamsAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            // Return list of subscribed topics (MQTT doesn't support topic enumeration)
            List<string> topics;
            lock (_topicsLock)
            {
                topics = _subscribedTopics.ToList();
            }

            foreach (var topic in topics)
            {
                if (ct.IsCancellationRequested)
                    yield break;

                yield return topic;
            }

            await Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override Task<StreamInfo> GetStreamInfoAsync(string streamName, CancellationToken ct = default)
        {
            ValidateStreamName(streamName);

            // MQTT doesn't provide topic metadata
            return Task.FromResult(new StreamInfo
            {
                StreamName = streamName,
                PartitionCount = 1, // MQTT doesn't have partitions
                MessageCount = null, // Unknown
                SizeBytes = null, // Unknown
                RetentionPeriod = null, // Broker-dependent
                CreatedAt = null, // Unknown
                Metadata = new Dictionary<string, object>
                {
                    ["broker"] = _connectionSettings.BrokerAddress,
                    ["port"] = _connectionSettings.Port,
                    ["protocol"] = "MQTT"
                }
            });
        }

        /// <summary>
        /// Connects to the MQTT broker.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the connection operation.</returns>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await _mqttClient.ConnectAsync(_connectionSettings, ct);
        }

        /// <summary>
        /// Disconnects from the MQTT broker.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the disconnection operation.</returns>
        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            await _mqttClient.DisconnectAsync(ct);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _mqttClient?.DisposeAsync().AsTask().Wait();
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
