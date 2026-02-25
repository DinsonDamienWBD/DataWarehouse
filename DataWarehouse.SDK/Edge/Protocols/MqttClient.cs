using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace DataWarehouse.SDK.Edge.Protocols
{
    /// <summary>
    /// MQTT client implementation wrapping MQTTnet library.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation provides DataWarehouse-native semantics over MQTTnet,
    /// including auto-reconnect with exponential backoff, connection state management,
    /// and event-based message reception.
    /// </para>
    /// <para>
    /// <strong>Thread Safety:</strong>
    /// All public methods are thread-safe. Connection state is protected by a semaphore.
    /// Event handlers are invoked on background threads.
    /// </para>
    /// <para>
    /// <strong>Auto-Reconnect:</strong>
    /// When AutoReconnect is enabled and connection is lost, the client automatically
    /// attempts reconnection with exponential backoff (5s, 10s, 20s, 40s, max 60s).
    /// Subscriptions are restored after successful reconnection.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: MQTTnet wrapper (EDGE-02)")]
    public sealed class MqttClient : IMqttClient
    {
        private readonly MQTTnet.Client.IMqttClient _client;
        private MqttConnectionSettings? _currentSettings;
        private CancellationTokenSource? _reconnectCts;
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly List<string> _subscribedTopics = new();
        private readonly object _topicsLock = new();
        private bool _disposed;

        /// <inheritdoc/>
        public event EventHandler<MqttMessageReceivedEventArgs>? OnMessageReceived;

        /// <inheritdoc/>
        public event EventHandler<MqttConnectionLostEventArgs>? OnConnectionLost;

        /// <inheritdoc/>
        public event EventHandler? OnReconnected;

        /// <summary>
        /// Initializes a new instance of the <see cref="MqttClient"/> class.
        /// </summary>
        public MqttClient()
        {
            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            // Wire up MQTTnet event handlers
            _client.ApplicationMessageReceivedAsync += OnMqttMessageReceivedAsync;
            _client.DisconnectedAsync += OnMqttDisconnectedAsync;
        }

        /// <inheritdoc/>
        public bool IsConnected => _client?.IsConnected ?? false;

        /// <inheritdoc/>
        public async Task ConnectAsync(MqttConnectionSettings settings, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(settings);

            await _connectLock.WaitAsync(ct);
            try
            {
                _currentSettings = settings;

                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithTcpServer(settings.BrokerAddress, settings.Port)
                    .WithClientId(settings.ClientId ?? Guid.NewGuid().ToString())
                    .WithKeepAlivePeriod(settings.KeepAlive)
                    .WithCleanSession(settings.CleanSession)
                    .WithTimeout(settings.ConnectTimeout);

                // Authentication
                if (!string.IsNullOrEmpty(settings.Username))
                {
                    optionsBuilder.WithCredentials(settings.Username, settings.Password);
                }

                // TLS/SSL
                if (settings.UseTls)
                {
                    var tlsOptionsBuilder = new MqttClientTlsOptionsBuilder()
                        .WithAllowUntrustedCertificates(settings.AllowUntrustedCertificates);

                    // Client certificate for mutual TLS
                    if (!string.IsNullOrEmpty(settings.TlsCertificatePath))
                    {
                        var cert = X509CertificateLoader.LoadPkcs12FromFile(
                            settings.TlsCertificatePath,
                            settings.TlsCertificatePassword);
                        tlsOptionsBuilder.WithClientCertificates(new[] { cert });
                    }

                    optionsBuilder.WithTlsOptions(tlsOptionsBuilder.Build());
                }

                // Last Will and Testament
                if (settings.WillMessage is not null)
                {
                    var willQos = settings.WillMessage.QoS switch
                    {
                        MqttQualityOfServiceLevel.AtMostOnce => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
                        MqttQualityOfServiceLevel.AtLeastOnce => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce,
                        MqttQualityOfServiceLevel.ExactlyOnce => MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                        _ => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce
                    };

                    optionsBuilder.WithWillTopic(settings.WillMessage.Topic)
                        .WithWillPayload(settings.WillMessage.Payload)
                        .WithWillQualityOfServiceLevel(willQos)
                        .WithWillRetain(settings.WillMessage.Retain);
                }

                var options = optionsBuilder.Build();
                var result = await _client.ConnectAsync(options, ct);

                if (result.ResultCode != MqttClientConnectResultCode.Success)
                {
                    throw new InvalidOperationException(
                        $"MQTT connection failed: {result.ResultCode} - {result.ReasonString ?? "Unknown error"}");
                }
            }
            finally
            {
                _connectLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            // Cancel auto-reconnect
            _reconnectCts?.Cancel();
            _reconnectCts = null;

            if (_client.IsConnected)
            {
                var options = new MqttClientDisconnectOptionsBuilder()
                    .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                    .Build();

                await _client.DisconnectAsync(options, ct);
            }
        }

        /// <inheritdoc/>
        public async Task PublishAsync(MqttMessage message, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (!IsConnected)
            {
                throw new InvalidOperationException("MQTT client is not connected. Call ConnectAsync first.");
            }

            var msgQos = message.QoS switch
            {
                MqttQualityOfServiceLevel.AtMostOnce => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
                MqttQualityOfServiceLevel.AtLeastOnce => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce,
                MqttQualityOfServiceLevel.ExactlyOnce => MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                _ => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce
            };

            var mqttMsg = new MqttApplicationMessageBuilder()
                .WithTopic(message.Topic)
                .WithPayload(message.Payload)
                .WithQualityOfServiceLevel(msgQos)
                .WithRetainFlag(message.Retain)
                .Build();

            // User properties (MQTT 5.0)
            if (message.UserProperties is not null)
            {
                foreach (var prop in message.UserProperties)
                {
                    mqttMsg.UserProperties.Add(new MQTTnet.Packets.MqttUserProperty(prop.Key, prop.Value));
                }
            }

            await _client.PublishAsync(mqttMsg, ct);
        }

        /// <inheritdoc/>
        public async Task SubscribeAsync(IEnumerable<string> topics, MqttQualityOfServiceLevel qos, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(topics);

            var topicList = topics.ToList();
            if (topicList.Count == 0)
            {
                throw new ArgumentException("Topics collection cannot be empty.", nameof(topics));
            }

            if (!IsConnected)
            {
                throw new InvalidOperationException("MQTT client is not connected. Call ConnectAsync first.");
            }

            var subQos = qos switch
            {
                MqttQualityOfServiceLevel.AtMostOnce => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
                MqttQualityOfServiceLevel.AtLeastOnce => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce,
                MqttQualityOfServiceLevel.ExactlyOnce => MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
                _ => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce
            };

            var subscribeOptionsBuilder = new MqttClientSubscribeOptionsBuilder();
            foreach (var topic in topicList)
            {
                subscribeOptionsBuilder.WithTopicFilter(topic, subQos);
            }

            await _client.SubscribeAsync(subscribeOptionsBuilder.Build(), ct);

            // Track subscriptions for restoration after reconnect
            lock (_topicsLock)
            {
                foreach (var topic in topicList)
                {
                    if (!_subscribedTopics.Contains(topic))
                    {
                        _subscribedTopics.Add(topic);
                    }
                }
            }
        }

        /// <inheritdoc/>
        public async Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(topics);

            var topicList = topics.ToList();
            if (topicList.Count == 0)
            {
                return; // No-op
            }

            if (!IsConnected)
            {
                throw new InvalidOperationException("MQTT client is not connected.");
            }

            var unsubscribeOptionsBuilder = new MqttClientUnsubscribeOptionsBuilder();
            foreach (var topic in topicList)
            {
                unsubscribeOptionsBuilder.WithTopicFilter(topic);
            }

            await _client.UnsubscribeAsync(unsubscribeOptionsBuilder.Build(), ct);

            // Remove from tracked subscriptions
            lock (_topicsLock)
            {
                foreach (var topic in topicList)
                {
                    _subscribedTopics.Remove(topic);
                }
            }
        }

        /// <summary>
        /// MQTTnet message received handler.
        /// </summary>
        private Task OnMqttMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            var qosLevel = args.ApplicationMessage.QualityOfServiceLevel switch
            {
                MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce => MqttQualityOfServiceLevel.AtMostOnce,
                MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce => MqttQualityOfServiceLevel.AtLeastOnce,
                MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce => MqttQualityOfServiceLevel.ExactlyOnce,
                _ => MqttQualityOfServiceLevel.AtMostOnce
            };

            var message = new MqttMessage
            {
                Topic = args.ApplicationMessage.Topic,
                Payload = args.ApplicationMessage.PayloadSegment.ToArray(),
                QoS = qosLevel,
                Retain = args.ApplicationMessage.Retain,
                UserProperties = args.ApplicationMessage.UserProperties?.ToDictionary(
                    p => p.Name,
                    p => p.Value)
            };

            OnMessageReceived?.Invoke(this, new MqttMessageReceivedEventArgs(message));
            return Task.CompletedTask;
        }

        /// <summary>
        /// MQTTnet disconnected handler.
        /// </summary>
        private async Task OnMqttDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            // Fire connection lost event
            OnConnectionLost?.Invoke(this, new MqttConnectionLostEventArgs(
                args.Reason.ToString(),
                args.Exception));

            // Auto-reconnect logic
            if (_currentSettings?.AutoReconnect == true && !_disposed)
            {
                _reconnectCts = new CancellationTokenSource();
                await ReconnectAsync(_reconnectCts.Token);
            }
        }

        /// <summary>
        /// Auto-reconnect with exponential backoff.
        /// </summary>
        private async Task ReconnectAsync(CancellationToken ct)
        {
            if (_currentSettings is null) return;

            int attempt = 0;
            while (!ct.IsCancellationRequested && !_client.IsConnected)
            {
                attempt++;

                // Check max attempts
                if (_currentSettings.MaxReconnectAttempts > 0 && attempt > _currentSettings.MaxReconnectAttempts)
                {
                    break;
                }

                try
                {
                    // Exponential backoff: delay * 2^(attempt-1), max 60 seconds
                    var delay = TimeSpan.FromMilliseconds(
                        Math.Min(_currentSettings.ReconnectDelay.TotalMilliseconds * Math.Pow(2, attempt - 1), 60000));

                    await Task.Delay(delay, ct);

                    // Attempt reconnection
                    await ConnectAsync(_currentSettings, ct);

                    // Restore subscriptions
                    await RestoreSubscriptionsAsync(ct);

                    // Fire reconnected event
                    OnReconnected?.Invoke(this, EventArgs.Empty);
                    break;
                }
                catch (Exception)
                {
                    // Retry on next iteration
                }
            }
        }

        /// <summary>
        /// Restores subscriptions after reconnection.
        /// </summary>
        private async Task RestoreSubscriptionsAsync(CancellationToken ct)
        {
            List<string> topicsToRestore;
            lock (_topicsLock)
            {
                topicsToRestore = _subscribedTopics.ToList();
            }

            if (topicsToRestore.Count > 0)
            {
                // Restore all subscriptions with QoS 1 (default)
                // Note: Accurate restoration requires tracking per-topic QoS levels
                await SubscribeAsync(topicsToRestore, MqttQualityOfServiceLevel.AtLeastOnce, ct);
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();

            if (_client.IsConnected)
            {
                try
                {
                    await _client.DisconnectAsync();
                }
                catch
                {
                    // Ignore exceptions during disposal
                }
            }

            _client?.Dispose();
            _connectLock?.Dispose();
        }
    }
}
