using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Protocols
{
    /// <summary>
    /// Configuration settings for connecting to an MQTT broker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supports both MQTT 3.1.1 and MQTT 5.0 protocols with configurable authentication,
    /// TLS/SSL encryption, and connection parameters.
    /// </para>
    /// <para>
    /// <strong>Security:</strong>
    /// - Use TLS (UseTls = true) for production deployments
    /// - Mutual TLS (client certificates) recommended for device authentication
    /// - AllowUntrustedCertificates should ONLY be true in development/testing
    /// </para>
    /// <para>
    /// <strong>Reliability:</strong>
    /// - AutoReconnect with exponential backoff handles network interruptions
    /// - CleanSession = false preserves subscriptions and queued messages across reconnects
    /// - KeepAlive prevents idle connection timeouts
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: MQTT connection settings (EDGE-02)")]
    public sealed record MqttConnectionSettings
    {
        /// <summary>
        /// Gets the MQTT broker address (hostname or IP).
        /// </summary>
        /// <remarks>
        /// Examples: "mqtt.eclipse.org", "192.168.1.100", "broker.hivemq.com"
        /// </remarks>
        public required string BrokerAddress { get; init; }

        /// <summary>
        /// Gets the broker port.
        /// </summary>
        /// <remarks>
        /// Standard ports:
        /// - 1883: MQTT (unencrypted)
        /// - 8883: MQTT over TLS
        /// - 8080/80: MQTT over WebSockets
        /// </remarks>
        public int Port { get; init; } = 1883;

        /// <summary>
        /// Gets the client identifier (auto-generated if null).
        /// </summary>
        /// <remarks>
        /// Must be unique per broker. If null, a GUID is generated.
        /// Persistent sessions require fixed ClientId.
        /// </remarks>
        public string? ClientId { get; init; }

        /// <summary>
        /// Gets the username for broker authentication.
        /// </summary>
        public string? Username { get; init; }

        /// <summary>
        /// Gets the password for broker authentication.
        /// </summary>
        public string? Password { get; init; }

        /// <summary>
        /// Gets whether to use TLS/SSL encryption.
        /// </summary>
        /// <remarks>
        /// Always use TLS in production. Unencrypted MQTT sends credentials in plaintext.
        /// </remarks>
        public bool UseTls { get; init; } = false;

        /// <summary>
        /// Gets the path to the client certificate for mutual TLS authentication.
        /// </summary>
        /// <remarks>
        /// Used for X.509 certificate-based authentication. Supports PFX/PKCS#12 format.
        /// </remarks>
        public string? TlsCertificatePath { get; init; }

        /// <summary>
        /// Gets the password for the client certificate (if encrypted).
        /// </summary>
        public string? TlsCertificatePassword { get; init; }

        /// <summary>
        /// Gets whether to allow untrusted/self-signed server certificates.
        /// </summary>
        /// <remarks>
        /// <strong>WARNING:</strong> Only use true in development/testing.
        /// In production, validate server certificates to prevent MITM attacks.
        /// </remarks>
        public bool AllowUntrustedCertificates { get; init; } = false;

        /// <summary>
        /// Gets the keep-alive interval for detecting stale connections.
        /// </summary>
        /// <remarks>
        /// Broker closes connection if no message received within 1.5x KeepAlive period.
        /// Typical values: 30-120 seconds.
        /// </remarks>
        public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets whether to start a clean session (discarding previous state).
        /// </summary>
        /// <remarks>
        /// - true: Broker discards previous session state (subscriptions, queued messages)
        /// - false: Broker restores session state (persistent sessions, QoS 1/2 delivery guarantees)
        /// </remarks>
        public bool CleanSession { get; init; } = true;

        /// <summary>
        /// Gets the Last Will and Testament message sent by broker when client disconnects unexpectedly.
        /// </summary>
        /// <remarks>
        /// Will message notifies other clients when this client goes offline (e.g., "sensor/status" -> "offline").
        /// Broker sends will message if client disconnects without proper DISCONNECT packet.
        /// </remarks>
        public MqttMessage? WillMessage { get; init; }

        /// <summary>
        /// Gets the connection timeout duration.
        /// </summary>
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets whether to automatically reconnect when connection is lost.
        /// </summary>
        /// <remarks>
        /// When true, MqttClient automatically attempts reconnection with exponential backoff.
        /// </remarks>
        public bool AutoReconnect { get; init; } = true;

        /// <summary>
        /// Gets the initial delay before first reconnect attempt.
        /// </summary>
        /// <remarks>
        /// Subsequent attempts use exponential backoff: delay * 2^(attempt-1), capped at 60 seconds.
        /// </remarks>
        public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets the maximum number of reconnect attempts (0 = infinite).
        /// </summary>
        public int MaxReconnectAttempts { get; init; } = 10;
    }
}
