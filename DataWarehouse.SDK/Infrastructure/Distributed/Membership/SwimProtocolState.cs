using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Configuration for the SWIM protocol probe cycle and gossip dissemination.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: SWIM gossip membership")]
    public sealed record SwimConfiguration
    {
        /// <summary>
        /// Interval in milliseconds between probe cycles.
        /// </summary>
        public int ProtocolPeriodMs { get; init; } = 1000;

        /// <summary>
        /// Timeout in milliseconds for a direct ping response.
        /// </summary>
        public int PingTimeoutMs { get; init; } = 500;

        /// <summary>
        /// Number of peers to use for indirect probing (ping-req).
        /// </summary>
        public int IndirectPingCount { get; init; } = 3;

        /// <summary>
        /// Time in milliseconds before a Suspected node transitions to Dead.
        /// </summary>
        public int SuspicionTimeoutMs { get; init; } = 5000;

        /// <summary>
        /// Maximum number of membership change entries piggybacked on each SWIM message.
        /// </summary>
        public int MaxGossipPiggybackSize { get; init; } = 10;

        /// <summary>
        /// Shared secret for HMAC-SHA256 authentication of SWIM gossip messages.
        /// When set, all outgoing messages are signed and all incoming messages are verified.
        /// Messages with invalid or missing HMAC are rejected. (DIST-03 mitigation)
        /// </summary>
        public byte[]? ClusterSecret { get; init; }

        /// <summary>
        /// Minimum number of independent Dead reports required before marking a node as dead.
        /// Prevents single-message node eviction attacks. (DIST-03 mitigation)
        /// </summary>
        public int DeadNodeQuorum { get; init; } = 2;

        /// <summary>
        /// Maximum number of membership state changes per node per second.
        /// Rate-limits rapid state transitions to prevent abuse. (DIST-03 mitigation)
        /// </summary>
        public int MaxStateChangesPerNodePerSecond { get; init; } = 1;
    }

    /// <summary>
    /// Tracks per-member SWIM protocol state with incarnation numbers.
    /// </summary>
    internal sealed class SwimMemberState
    {
        public required ClusterNode Node { get; set; }
        public ClusterNodeStatus Status { get; set; }
        public int IncarnationNumber { get; set; }
        public DateTimeOffset LastPingAt { get; set; }
        public DateTimeOffset SuspectedAt { get; set; }
    }

    /// <summary>
    /// SWIM protocol message types for failure detection and membership dissemination.
    /// </summary>
    internal enum SwimMessageType
    {
        Ping,
        PingReq,
        Ack,
        Alive,
        Suspect,
        Dead,
        Join,
        Leave
    }

    /// <summary>
    /// SWIM protocol message serializable via System.Text.Json for IP2PNetwork transport.
    /// </summary>
    internal sealed class SwimMessage
    {
        [JsonPropertyName("type")]
        public SwimMessageType Type { get; set; }

        [JsonPropertyName("sourceNodeId")]
        public string SourceNodeId { get; set; } = string.Empty;

        [JsonPropertyName("targetNodeId")]
        public string TargetNodeId { get; set; } = string.Empty;

        [JsonPropertyName("incarnationNumber")]
        public int IncarnationNumber { get; set; }

        [JsonPropertyName("membershipUpdates")]
        public List<SwimMembershipUpdate> MembershipUpdates { get; set; } = new();

        /// <summary>
        /// HMAC-SHA256 signature of the message payload for authentication (DIST-03 mitigation).
        /// Null when cluster secret is not configured.
        /// </summary>
        [JsonPropertyName("hmac")]
        public byte[]? Hmac { get; set; }

        /// <summary>
        /// Serializes the message to bytes. If a cluster secret is provided, computes and attaches HMAC.
        /// </summary>
        public byte[] Serialize(byte[]? clusterSecret = null)
        {
            Hmac = null; // Clear HMAC before computing (HMAC is over non-HMAC fields)
            var payload = JsonSerializer.SerializeToUtf8Bytes(this, SwimJsonContext.Default.SwimMessage);

            if (clusterSecret != null && clusterSecret.Length > 0)
            {
                using var hmac = new HMACSHA256(clusterSecret);
                Hmac = hmac.ComputeHash(payload);
                // Re-serialize with HMAC included
                payload = JsonSerializer.SerializeToUtf8Bytes(this, SwimJsonContext.Default.SwimMessage);
            }

            return payload;
        }

        /// <summary>
        /// Deserializes a SWIM message from bytes. If a cluster secret is provided, verifies the HMAC.
        /// Returns null if deserialization fails or HMAC verification fails.
        /// </summary>
        public static SwimMessage? Deserialize(byte[] data, byte[]? clusterSecret = null)
        {
            var message = JsonSerializer.Deserialize(data, SwimJsonContext.Default.SwimMessage);
            if (message == null) return null;

            if (clusterSecret != null && clusterSecret.Length > 0)
            {
                if (message.Hmac == null || message.Hmac.Length == 0)
                {
                    return null; // Reject unauthenticated messages when secret is configured
                }

                // Verify HMAC: compute over the message without the HMAC field
                var receivedHmac = message.Hmac;
                message.Hmac = null;
                var payloadWithoutHmac = JsonSerializer.SerializeToUtf8Bytes(message, SwimJsonContext.Default.SwimMessage);

                using var hmac = new HMACSHA256(clusterSecret);
                var expectedHmac = hmac.ComputeHash(payloadWithoutHmac);

                if (!CryptographicOperations.FixedTimeEquals(receivedHmac, expectedHmac))
                {
                    return null; // HMAC mismatch -- reject
                }

                message.Hmac = receivedHmac; // Restore for reference
            }

            return message;
        }
    }

    /// <summary>
    /// Membership change entry piggybacked on SWIM messages for epidemic dissemination.
    /// </summary>
    internal sealed class SwimMembershipUpdate
    {
        [JsonPropertyName("nodeId")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("address")]
        public string Address { get; set; } = string.Empty;

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("status")]
        public ClusterNodeStatus Status { get; set; }

        [JsonPropertyName("incarnationNumber")]
        public int IncarnationNumber { get; set; }
    }

    /// <summary>
    /// Source-generated JSON context for SWIM message serialization (AOT-friendly, zero-allocation).
    /// </summary>
    [JsonSerializable(typeof(SwimMessage))]
    [JsonSerializable(typeof(SwimMembershipUpdate))]
    internal partial class SwimJsonContext : JsonSerializerContext
    {
    }
}
