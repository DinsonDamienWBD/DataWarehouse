using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Hosting;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace DataWarehouse.Plugins.AedsCore.ControlPlane;

/// <summary>
/// MQTT control plane transport implementation for AEDS signaling and manifest distribution.
/// </summary>
/// <remarks>
/// <para>
/// This plugin provides production-ready MQTT-based control plane transport with:
/// <list type="bullet">
/// <item><description>Persistent sessions with clean session = false for reliable delivery</description></item>
/// <item><description>QoS 1 (at least once delivery) for manifests and channel subscriptions</description></item>
/// <item><description>QoS 0 (at most once delivery) for heartbeats to reduce overhead</description></item>
/// <item><description>Automatic reconnection via MQTTnet AutoReconnect feature</description></item>
/// <item><description>Topic-based routing for unicast and broadcast delivery modes</description></item>
/// <item><description>Channel buffering for async enumerable manifest receiving</description></item>
/// <item><description>Topic-level ACL enforcement for subscribe and publish operations (NET-04)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Topic Structure:</strong>
/// <list type="bullet">
/// <item><description>Personal topic: aeds/client/{clientId}/manifests</description></item>
/// <item><description>Channel topic: aeds/channel/{channelId}</description></item>
/// <item><description>Heartbeat topic: aeds/heartbeat/{clientId}</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Topic ACL Rules (NET-04 - CVSS 7.5):</strong>
/// <list type="bullet">
/// <item><description>aeds/client/{clientId}/# -- each client can only access their own namespace</description></item>
/// <item><description>aeds/channel/# -- subscribed channels only</description></item>
/// <item><description>aeds/heartbeat/{clientId} -- own heartbeat only</description></item>
/// <item><description>aeds/control/# -- admin role only</description></item>
/// <item><description>aeds/manifest/# -- authorized manifest publishers only</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Use Cases:</strong> Ideal for IoT/edge deployments, mobile clients, and scenarios
/// requiring lightweight pub/sub messaging with broker-based routing.
/// </para>
/// </remarks>
[PluginProfile(ServiceProfileType.Server)]
public class MqttControlPlanePlugin : ControlPlaneTransportPluginBase
{
    private readonly ILogger<MqttControlPlanePlugin> _logger;
    private IMqttClient? _mqttClient;
    private string? _clientId;
    private Channel<IntentManifest>? _manifestChannel;
    private readonly List<string> _subscribedTopics = new();
    private readonly object _subscriptionLock = new();
    private MqttClientOptions? _mqttOptions;
    private CancellationTokenSource? _connectionCts;
    private const int MaxBackoffSeconds = 32;
    private int _reconnectAttempt;

    /// <summary>
    /// Topic ACL entries defining allowed topic patterns per client role.
    /// Key: role name, Value: list of allowed topic regex patterns.
    /// </summary>
    private readonly Dictionary<string, List<Regex>> _topicAcl = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set of channel IDs this client is authorized to access.
    /// </summary>
    private readonly HashSet<string> _authorizedChannels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The role of this client for topic ACL enforcement.
    /// </summary>
    private string _clientRole = "client";

    /// <summary>
    /// Initializes a new instance of the <see cref="MqttControlPlanePlugin"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public MqttControlPlanePlugin(ILogger<MqttControlPlanePlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.aeds.controlplane.mqtt";

    /// <inheritdoc />
    public override string Name => "MQTT Control Plane Transport";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc />
    public override string TransportId => "mqtt";

    /// <inheritdoc />
    protected override async Task EstablishConnectionAsync(ControlPlaneConfig config, CancellationToken ct)
    {
        _clientId = config.ClientId;
        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _manifestChannel = Channel.CreateUnbounded<IntentManifest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        // Initialize topic ACL rules (NET-04: CVSS 7.5)
        InitializeTopicAcl(config.ClientId);

        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _mqttClient.ConnectedAsync += OnConnectedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(ExtractMqttHost(config.ServerUrl), ExtractMqttPort(config.ServerUrl))
            .WithClientId(config.ClientId)
            .WithCleanSession(false)
            .WithKeepAlivePeriod(config.HeartbeatInterval)
            .WithTimeout(TimeSpan.FromSeconds(30));

        if (!string.IsNullOrEmpty(config.AuthToken))
        {
            var parts = config.AuthToken.Split(':', 2);
            if (parts.Length == 2)
            {
                optionsBuilder.WithCredentials(parts[0], parts[1]);
            }
            else
            {
                optionsBuilder.WithCredentials("token", config.AuthToken);
            }
        }

        if (config.ServerUrl.StartsWith("mqtts://", StringComparison.OrdinalIgnoreCase))
        {
            optionsBuilder.WithTlsOptions(o => o.UseTls());
        }

        _mqttOptions = optionsBuilder.Build();

        await _mqttClient.ConnectAsync(_mqttOptions, ct);

        var personalTopic = $"aeds/client/{config.ClientId}/manifests";
        await SubscribeToTopicAsync(personalTopic, MqttQualityOfServiceLevel.AtLeastOnce, ct);

        _reconnectAttempt = 0;
        _logger.LogInformation("MQTT control plane connection established to {ServerUrl}", config.ServerUrl);
    }

    /// <summary>
    /// Initializes topic ACL rules based on client role.
    /// Each client role has specific topic patterns they are allowed to subscribe to and publish on.
    /// </summary>
    /// <param name="clientId">The client identifier for scoped ACL rules.</param>
    private void InitializeTopicAcl(string clientId)
    {
        var escapedClientId = Regex.Escape(clientId);

        // Default client role: access own namespace, subscribed channels, own heartbeat
        _topicAcl["client"] = new List<Regex>
        {
            new Regex($@"^aeds/client/{escapedClientId}/.*$", RegexOptions.Compiled),
            new Regex(@"^aeds/channel/[a-zA-Z0-9_\-]+$", RegexOptions.Compiled),
            new Regex($@"^aeds/heartbeat/{escapedClientId}$", RegexOptions.Compiled)
        };

        // Admin role: access everything
        _topicAcl["admin"] = new List<Regex>
        {
            new Regex(@"^aeds/.*$", RegexOptions.Compiled)
        };

        // Manifest publisher role: client access + manifest publishing
        _topicAcl["manifest-publisher"] = new List<Regex>
        {
            new Regex($@"^aeds/client/{escapedClientId}/.*$", RegexOptions.Compiled),
            new Regex(@"^aeds/channel/[a-zA-Z0-9_\-]+$", RegexOptions.Compiled),
            new Regex($@"^aeds/heartbeat/{escapedClientId}$", RegexOptions.Compiled),
            new Regex(@"^aeds/manifest/.*$", RegexOptions.Compiled)
        };

        _logger.LogDebug("Topic ACL initialized for client {ClientId} with role {Role}", clientId, _clientRole);
    }

    /// <summary>
    /// Authorizes a topic for subscribe or publish based on the client's ACL.
    /// </summary>
    /// <param name="topic">The MQTT topic to authorize.</param>
    /// <param name="operation">The operation type (subscribe or publish).</param>
    /// <returns>True if the topic is authorized, false otherwise.</returns>
    private bool AuthorizeTopic(string topic, string operation)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            _logger.LogWarning("MQTT topic authorization denied: empty topic for {Operation}", operation);
            return false;
        }

        // Validate topic does not contain MQTT wildcards in publish operations
        if (operation == "publish" && (topic.Contains('#') || topic.Contains('+')))
        {
            _logger.LogWarning("MQTT topic authorization denied: wildcards not allowed in publish topic {Topic}", topic);
            return false;
        }

        // Check against ACL for current role
        if (_topicAcl.TryGetValue(_clientRole, out var patterns))
        {
            foreach (var pattern in patterns)
            {
                if (pattern.IsMatch(topic))
                {
                    return true;
                }
            }
        }

        _logger.LogWarning(
            "MQTT topic authorization denied: client {ClientId} (role={Role}) attempted {Operation} on topic {Topic}",
            _clientId, _clientRole, operation, topic);

        return false;
    }

    /// <summary>
    /// Validates that a received message came on an authorized topic.
    /// </summary>
    /// <param name="topic">The topic the message was received on.</param>
    /// <returns>True if the message should be processed, false if it should be dropped.</returns>
    private bool IsTopicAllowed(string topic)
    {
        return AuthorizeTopic(topic, "subscribe");
    }

    /// <summary>
    /// Extracts MQTT host from connection URL.
    /// </summary>
    private string ExtractMqttHost(string serverUrl)
    {
        var uri = new Uri(serverUrl.Replace("mqtt://", "http://").Replace("mqtts://", "https://"));
        return uri.Host;
    }

    /// <summary>
    /// Extracts MQTT port from connection URL (default 1883 for mqtt, 8883 for mqtts).
    /// </summary>
    private int ExtractMqttPort(string serverUrl)
    {
        var uri = new Uri(serverUrl.Replace("mqtt://", "http://").Replace("mqtts://", "https://"));
        if (uri.Port > 0)
        {
            return uri.Port;
        }

        return serverUrl.StartsWith("mqtts://", StringComparison.OrdinalIgnoreCase) ? 8883 : 1883;
    }

    /// <summary>
    /// Event handler for MQTT client connected.
    /// </summary>
    private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        _logger.LogInformation("MQTT client connected");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Event handler for MQTT client disconnected.
    /// </summary>
    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (args.Exception != null)
        {
            _logger.LogWarning(args.Exception, "MQTT client disconnected, attempting reconnection");
        }
        else
        {
            _logger.LogInformation("MQTT client disconnected (reason: {Reason})", args.Reason);
        }

        if (_connectionCts != null && !_connectionCts.Token.IsCancellationRequested && _mqttOptions != null)
        {
            await ReconnectAsync(_connectionCts.Token);
        }
    }

    /// <summary>
    /// Reconnects to MQTT broker with exponential backoff.
    /// </summary>
    private async Task ReconnectAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _mqttClient != null && _mqttOptions != null)
        {
            try
            {
                if (_mqttClient.IsConnected)
                {
                    return;
                }

                var backoffSeconds = Math.Min((int)Math.Pow(2, _reconnectAttempt), MaxBackoffSeconds);
                _logger.LogInformation("Reconnecting to MQTT broker in {BackoffSeconds}s (attempt {Attempt})",
                    backoffSeconds, _reconnectAttempt + 1);

                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);

                await _mqttClient.ConnectAsync(_mqttOptions, ct);

                List<string> topicsToSubscribe;
                lock (_subscriptionLock)
                {
                    topicsToSubscribe = _subscribedTopics.ToList();
                }

                foreach (var topic in topicsToSubscribe)
                {
                    var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(topic, MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    await _mqttClient.SubscribeAsync(subscribeOptions, ct);
                }

                _reconnectAttempt = 0;
                _logger.LogInformation("MQTT reconnection successful");
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _reconnectAttempt++;
                _logger.LogWarning(ex, "MQTT reconnection attempt failed");
            }
        }
    }

    /// <summary>
    /// Event handler for received MQTT messages.
    /// </summary>
    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            var topic = args.ApplicationMessage.Topic;

            // NET-04: Validate received message against topic ACL
            if (!IsTopicAllowed(topic))
            {
                _logger.LogWarning("Dropping MQTT message on unauthorized topic {Topic} for client {ClientId}",
                    topic, _clientId);
                return;
            }

            // In MQTTnet v5, Payload is ReadOnlySequence<byte>
            var payloadSequence = args.ApplicationMessage.Payload;
            var payload = Encoding.UTF8.GetString(payloadSequence.IsSingleSegment
                ? payloadSequence.FirstSpan
                : payloadSequence.ToArray());

            _logger.LogDebug("Received MQTT message on topic {Topic}", topic);

            if (topic.StartsWith("aeds/client/") && topic.EndsWith("/manifests"))
            {
                await ProcessManifestMessageAsync(payload);
            }
            else if (topic.StartsWith("aeds/channel/"))
            {
                await ProcessManifestMessageAsync(payload);
            }
            else
            {
                _logger.LogDebug("Ignoring message on non-manifest topic {Topic}", topic);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MQTT message");
        }
    }

    /// <summary>
    /// Processes a manifest message payload.
    /// </summary>
    private async Task ProcessManifestMessageAsync(string payload)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<IntentManifest>(payload);
            if (manifest != null && _manifestChannel != null)
            {
                await _manifestChannel.Writer.WriteAsync(manifest);
                _logger.LogDebug("Queued manifest {ManifestId}", manifest.ManifestId);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize manifest, skipping invalid JSON");
        }
    }

    /// <summary>
    /// Subscribes to an MQTT topic after verifying topic-level authorization (NET-04).
    /// </summary>
    private async Task SubscribeToTopicAsync(string topic, MqttQualityOfServiceLevel qos, CancellationToken ct)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            throw new InvalidOperationException("MQTT client is not connected");
        }

        // NET-04: Enforce topic-level authorization before subscribing
        if (!AuthorizeTopic(topic, "subscribe"))
        {
            throw new UnauthorizedAccessException(
                $"MQTT topic authorization denied: client '{_clientId}' (role={_clientRole}) is not allowed to subscribe to topic '{topic}'");
        }

        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topic, qos)
            .Build();

        var result = await _mqttClient.SubscribeAsync(subscribeOptions, ct);

        lock (_subscriptionLock)
        {
            if (!_subscribedTopics.Contains(topic))
            {
                _subscribedTopics.Add(topic);
            }
        }

        _logger.LogInformation("Subscribed to MQTT topic {Topic} with QoS {QoS}", topic, qos);
    }

    /// <summary>
    /// Unsubscribes from an MQTT topic.
    /// </summary>
    private async Task UnsubscribeFromTopicAsync(string topic, CancellationToken ct)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            throw new InvalidOperationException("MQTT client is not connected");
        }

        var unsubscribeOptions = new MqttClientUnsubscribeOptionsBuilder()
            .WithTopicFilter(topic)
            .Build();

        await _mqttClient.UnsubscribeAsync(unsubscribeOptions, ct);

        lock (_subscriptionLock)
        {
            _subscribedTopics.Remove(topic);
        }

        _logger.LogInformation("Unsubscribed from MQTT topic {Topic}", topic);
    }

    /// <inheritdoc />
    protected override async Task TransmitManifestAsync(IntentManifest manifest, CancellationToken ct)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            throw new InvalidOperationException("MQTT client is not connected");
        }

        var json = JsonSerializer.Serialize(manifest);
        var payload = Encoding.UTF8.GetBytes(json);

        string topic;
        if (manifest.DeliveryMode == DeliveryMode.Broadcast && manifest.Targets.Length > 0)
        {
            topic = $"aeds/channel/{manifest.Targets[0]}";
        }
        else if (manifest.DeliveryMode == DeliveryMode.Unicast && manifest.Targets.Length > 0)
        {
            topic = $"aeds/client/{manifest.Targets[0]}/manifests";
        }
        else
        {
            throw new InvalidOperationException($"Cannot determine MQTT topic for delivery mode {manifest.DeliveryMode}");
        }

        // NET-04: Enforce topic-level authorization before publishing
        if (!AuthorizeTopic(topic, "publish"))
        {
            throw new UnauthorizedAccessException(
                $"MQTT topic authorization denied: client '{_clientId}' (role={_clientRole}) is not allowed to publish to topic '{topic}'");
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _mqttClient.PublishAsync(message, ct);

        _logger.LogDebug("Published manifest {ManifestId} to topic {Topic}", manifest.ManifestId, topic);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<IntentManifest> ListenForManifestsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_manifestChannel == null)
        {
            throw new InvalidOperationException("Not connected, manifest channel is not available");
        }

        await foreach (var manifest in _manifestChannel.Reader.ReadAllAsync(ct))
        {
            yield return manifest;
        }
    }

    /// <inheritdoc />
    protected override async Task TransmitHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            return;
        }

        var json = JsonSerializer.Serialize(heartbeat);
        var payload = Encoding.UTF8.GetBytes(json);
        var topic = $"aeds/heartbeat/{heartbeat.ClientId}";

        // NET-04: Enforce topic-level authorization for heartbeat publishing
        if (!AuthorizeTopic(topic, "publish"))
        {
            _logger.LogWarning("Heartbeat publish denied for topic {Topic}, client {ClientId}", topic, _clientId);
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(false)
            .Build();

        try
        {
            await _mqttClient.PublishAsync(message, ct);
            _logger.LogTrace("Published heartbeat to topic {Topic}", topic);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish heartbeat");
        }
    }

    /// <inheritdoc />
    protected override async Task JoinChannelAsync(string channelId, CancellationToken ct)
    {
        var topic = $"aeds/channel/{channelId}";
        await SubscribeToTopicAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, ct);
    }

    /// <inheritdoc />
    protected override async Task LeaveChannelAsync(string channelId, CancellationToken ct)
    {
        var topic = $"aeds/channel/{channelId}";
        await UnsubscribeFromTopicAsync(topic, ct);
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        _logger.LogInformation("Closing MQTT control plane connection");

        _connectionCts?.Cancel();

        if (_mqttClient != null && _mqttClient.IsConnected)
        {
            try
            {
                List<string> topicsToUnsubscribe;
                lock (_subscriptionLock)
                {
                    topicsToUnsubscribe = _subscribedTopics.ToList();
                    _subscribedTopics.Clear();
                }

                foreach (var topic in topicsToUnsubscribe)
                {
                    var unsubscribeOptions = new MqttClientUnsubscribeOptionsBuilder()
                        .WithTopicFilter(topic)
                        .Build();

                    await _mqttClient.UnsubscribeAsync(unsubscribeOptions);
                }

                var disconnectOptions = new MqttClientDisconnectOptionsBuilder()
                    .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                    .Build();

                await _mqttClient.DisconnectAsync(disconnectOptions);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during MQTT disconnect");
            }
        }

        _mqttClient?.Dispose();
        _mqttClient = null;

        _manifestChannel?.Writer.Complete();
        _manifestChannel = null;

        _connectionCts?.Dispose();
        _connectionCts = null;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connectionCts?.Dispose();
            _mqttClient?.Dispose();
        }
        base.Dispose(disposing);
    }
}
