using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
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
    }

    /// <summary>
    /// Tracks per-member SWIM protocol state with incarnation numbers.
    /// </summary>
    internal sealed class SwimMemberState
    {
        public ClusterNode Node { get; set; } = null!;
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

        public byte[] Serialize()
        {
            return JsonSerializer.SerializeToUtf8Bytes(this, SwimJsonContext.Default.SwimMessage);
        }

        public static SwimMessage? Deserialize(byte[] data)
        {
            return JsonSerializer.Deserialize(data, SwimJsonContext.Default.SwimMessage);
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
