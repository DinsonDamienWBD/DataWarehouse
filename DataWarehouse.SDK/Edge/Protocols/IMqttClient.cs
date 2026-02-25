using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Protocols
{
    /// <summary>
    /// MQTT client interface for publish-subscribe messaging with edge and IoT devices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MQTT (Message Queuing Telemetry Transport) is a lightweight publish-subscribe messaging
    /// protocol designed for constrained devices and networks. It is the standard protocol for IoT.
    /// </para>
    /// <para>
    /// <strong>Key Features:</strong>
    /// - Topic-based routing with hierarchical namespaces (e.g., "home/livingroom/temperature")
    /// - Wildcard subscriptions: '+' (single level), '#' (multiple levels)
    /// - Quality of Service levels: QoS 0 (at most once), QoS 1 (at least once), QoS 2 (exactly once)
    /// - Retained messages: Broker stores last message per topic for new subscribers
    /// - Last Will and Testament: Automatic notification when client disconnects unexpectedly
    /// - Auto-reconnect with exponential backoff for network resilience
    /// </para>
    /// <para>
    /// <strong>Thread Safety:</strong>
    /// All methods are thread-safe and can be called concurrently.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: MQTT client interface (EDGE-02)")]
    public interface IMqttClient : IAsyncDisposable
    {
        /// <summary>
        /// Connects to the MQTT broker.
        /// </summary>
        /// <param name="settings">Connection configuration including broker address, port, credentials, TLS settings.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the connection operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when settings is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when connection fails (broker unreachable, auth failure, etc.).</exception>
        Task ConnectAsync(MqttConnectionSettings settings, CancellationToken ct = default);

        /// <summary>
        /// Disconnects from the broker gracefully.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the disconnection operation.</returns>
        /// <remarks>
        /// Sends proper DISCONNECT packet to broker (will message is NOT sent).
        /// Stops auto-reconnect if enabled.
        /// </remarks>
        Task DisconnectAsync(CancellationToken ct = default);

        /// <summary>
        /// Publishes a message to a topic.
        /// </summary>
        /// <param name="message">Message to publish including topic, payload, QoS, and retain flag.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the publish operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when message is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when not connected or publish fails.</exception>
        /// <remarks>
        /// QoS 0: Returns immediately after sending (no acknowledgment).
        /// QoS 1/2: Returns after broker acknowledges receipt.
        /// </remarks>
        Task PublishAsync(MqttMessage message, CancellationToken ct = default);

        /// <summary>
        /// Subscribes to one or more topics (supports wildcards).
        /// </summary>
        /// <param name="topics">Topic filters to subscribe to. Supports wildcards: '+' (single level), '#' (multiple levels).</param>
        /// <param name="qos">Quality of Service level for subscription.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the subscription operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when topics is null or empty.</exception>
        /// <exception cref="InvalidOperationException">Thrown when not connected or subscription fails.</exception>
        /// <remarks>
        /// Wildcard examples:
        /// - "sensor/+/temperature" matches "sensor/living_room/temperature", "sensor/bedroom/temperature"
        /// - "home/#" matches "home/living_room/temperature", "home/bedroom/humidity/sensor1"
        /// </remarks>
        Task SubscribeAsync(IEnumerable<string> topics, MqttQualityOfServiceLevel qos, CancellationToken ct = default);

        /// <summary>
        /// Unsubscribes from topics.
        /// </summary>
        /// <param name="topics">Topics to unsubscribe from.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task representing the unsubscription operation.</returns>
        Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken ct = default);

        /// <summary>
        /// Event fired when a message is received on subscribed topics.
        /// </summary>
        /// <remarks>
        /// Event handlers should be fast (< 100ms). For slow processing, queue messages for background processing.
        /// Messages are delivered in arrival order per topic.
        /// </remarks>
        event EventHandler<MqttMessageReceivedEventArgs>? OnMessageReceived;

        /// <summary>
        /// Event fired when connection to broker is lost unexpectedly.
        /// </summary>
        /// <remarks>
        /// This event is NOT fired for intentional disconnects via <see cref="DisconnectAsync"/>.
        /// If AutoReconnect is enabled, reconnection attempts start automatically.
        /// </remarks>
        event EventHandler<MqttConnectionLostEventArgs>? OnConnectionLost;

        /// <summary>
        /// Event fired when automatic reconnection succeeds.
        /// </summary>
        /// <remarks>
        /// Subscriptions are automatically restored after reconnection.
        /// </remarks>
        event EventHandler? OnReconnected;

        /// <summary>
        /// Gets whether the client is currently connected to the broker.
        /// </summary>
        bool IsConnected { get; }
    }

    /// <summary>
    /// Event arguments for message reception.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: MQTT message received event (EDGE-02)")]
    public sealed record MqttMessageReceivedEventArgs(MqttMessage Message);

    /// <summary>
    /// Event arguments for connection loss.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: MQTT connection lost event (EDGE-02)")]
    public sealed record MqttConnectionLostEventArgs(string Reason, Exception? Exception);
}
