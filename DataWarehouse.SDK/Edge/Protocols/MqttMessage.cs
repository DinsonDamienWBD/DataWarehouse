using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge.Protocols
{
    /// <summary>
    /// Represents an MQTT message with topic, payload, quality of service, and metadata.
    /// </summary>
    /// <remarks>
    /// MQTT is a lightweight publish-subscribe messaging protocol designed for constrained
    /// devices and low-bandwidth, high-latency networks. It is the de facto standard for IoT communication.
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: MQTT message record (EDGE-02)")]
    public sealed record MqttMessage
    {
        /// <summary>
        /// Gets the MQTT topic for this message.
        /// </summary>
        /// <remarks>
        /// Topics are hierarchical strings separated by forward slashes (e.g., "home/livingroom/temperature").
        /// Supports wildcards for subscriptions:
        /// - '+' matches a single level (e.g., "home/+/temperature" matches "home/livingroom/temperature")
        /// - '#' matches multiple levels (e.g., "home/#" matches "home/livingroom/temperature" and "home/kitchen/humidity")
        /// </remarks>
        public required string Topic { get; init; }

        /// <summary>
        /// Gets the message payload as raw bytes.
        /// </summary>
        /// <remarks>
        /// MQTT is payload-agnostic. Common encodings: JSON, Protocol Buffers, CBOR, plain text.
        /// </remarks>
        public required byte[] Payload { get; init; }

        /// <summary>
        /// Gets the Quality of Service level for message delivery.
        /// </summary>
        /// <remarks>
        /// - QoS 0 (At Most Once): Fire-and-forget, no acknowledgment, fastest
        /// - QoS 1 (At Least Once): Acknowledged delivery, may duplicate
        /// - QoS 2 (Exactly Once): Guaranteed single delivery, slowest
        /// </remarks>
        public MqttQualityOfServiceLevel QoS { get; init; } = MqttQualityOfServiceLevel.AtMostOnce;

        /// <summary>
        /// Gets whether the broker should retain this message for future subscribers.
        /// </summary>
        /// <remarks>
        /// When true, the broker stores this message and delivers it to new subscribers
        /// to this topic. Useful for "current state" messages (e.g., last known temperature).
        /// </remarks>
        public bool Retain { get; init; } = false;

        /// <summary>
        /// Gets optional user-defined properties (MQTT 5.0 feature).
        /// </summary>
        /// <remarks>
        /// User properties allow custom metadata (key-value pairs) to be attached to messages.
        /// Only available in MQTT 5.0+ brokers. Null for MQTT 3.1.1 compatibility.
        /// </remarks>
        public IReadOnlyDictionary<string, string>? UserProperties { get; init; }
    }

    /// <summary>
    /// MQTT Quality of Service levels for message delivery guarantees.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 36: MQTT QoS enumeration (EDGE-02)")]
    public enum MqttQualityOfServiceLevel
    {
        /// <summary>
        /// QoS 0: At Most Once delivery (fire-and-forget).
        /// </summary>
        /// <remarks>
        /// - Fastest, lowest overhead
        /// - No acknowledgment, no retries
        /// - Message may be lost if network fails
        /// - Use case: Sensor data where occasional loss is acceptable
        /// </remarks>
        AtMostOnce = 0,

        /// <summary>
        /// QoS 1: At Least Once delivery (acknowledged).
        /// </summary>
        /// <remarks>
        /// - Message acknowledged by receiver (PUBACK)
        /// - Retries if acknowledgment not received
        /// - May result in duplicates if network fails during acknowledgment
        /// - Use case: Most IoT scenarios, idempotent consumers
        /// </remarks>
        AtLeastOnce = 1,

        /// <summary>
        /// QoS 2: Exactly Once delivery (guaranteed single delivery).
        /// </summary>
        /// <remarks>
        /// - Four-way handshake (PUBLISH, PUBREC, PUBREL, PUBCOMP)
        /// - Guaranteed no duplicates, no loss
        /// - Highest overhead, slowest
        /// - Use case: Critical commands, financial transactions
        /// </remarks>
        ExactlyOnce = 2
    }
}
