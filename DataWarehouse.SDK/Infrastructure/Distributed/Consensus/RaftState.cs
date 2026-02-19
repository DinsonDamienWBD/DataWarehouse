using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Raft persistent state that must survive restarts (Raft paper Section 5.2).
    /// Tracks the current term, vote status, and replicated log.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Raft consensus state")]
    internal sealed class RaftPersistentState
    {
        /// <summary>
        /// Latest term server has seen (initialized to 0, increases monotonically).
        /// </summary>
        public long CurrentTerm { get; set; }

        /// <summary>
        /// CandidateId that received vote in current term (null if none).
        /// </summary>
        public string? VotedFor { get; set; }

        /// <summary>
        /// Replicated log entries. Each entry contains command for state machine and term.
        /// </summary>
        public List<RaftLogEntry> Log { get; } = new();
    }

    /// <summary>
    /// Raft volatile state that is reinitialized after restart (Raft paper Section 5.2).
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Raft consensus state")]
    internal sealed class RaftVolatileState
    {
        /// <summary>
        /// Index of highest log entry known to be committed (initialized to 0).
        /// </summary>
        public long CommitIndex { get; set; }

        /// <summary>
        /// Index of highest log entry applied to state machine (initialized to 0).
        /// </summary>
        public long LastApplied { get; set; }

        /// <summary>
        /// Leader-only: For each server, index of the next log entry to send to that server.
        /// Initialized to leader last log index + 1.
        /// </summary>
        public ConcurrentDictionary<string, long> NextIndex { get; } = new();

        /// <summary>
        /// Leader-only: For each server, index of highest log entry known to be replicated on server.
        /// Initialized to 0.
        /// </summary>
        public ConcurrentDictionary<string, long> MatchIndex { get; } = new();
    }

    /// <summary>
    /// Internal Raft node role (separate from ClusterNodeRole to keep Raft internals clean).
    /// </summary>
    internal enum RaftRole
    {
        Follower,
        Candidate,
        Leader
    }

    /// <summary>
    /// Configuration for the Raft consensus engine.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Raft consensus configuration")]
    public sealed record RaftConfiguration
    {
        /// <summary>
        /// Minimum election timeout in milliseconds.
        /// </summary>
        public int ElectionTimeoutMinMs { get; init; } = 150;

        /// <summary>
        /// Maximum election timeout in milliseconds (randomized between min and max).
        /// </summary>
        public int ElectionTimeoutMaxMs { get; init; } = 300;

        /// <summary>
        /// Heartbeat interval in milliseconds (leader sends to maintain authority).
        /// </summary>
        public int HeartbeatIntervalMs { get; init; } = 50;

        /// <summary>
        /// Maximum log entries before compaction (bounded collection).
        /// </summary>
        public int MaxLogEntries { get; init; } = 10_000;
    }

    /// <summary>
    /// Types of Raft RPC messages.
    /// </summary>
    internal enum RaftMessageType
    {
        RequestVote,
        RequestVoteResponse,
        AppendEntries,
        AppendEntriesResponse
    }

    /// <summary>
    /// Raft RPC message, serializable via System.Text.Json for IP2PNetwork transport.
    /// Encodes all four Raft RPCs in a single message type with discriminated fields.
    /// </summary>
    internal sealed class RaftMessage
    {
        [JsonPropertyName("type")]
        public RaftMessageType Type { get; set; }

        [JsonPropertyName("term")]
        public long Term { get; set; }

        [JsonPropertyName("senderId")]
        public string SenderId { get; set; } = string.Empty;

        // RequestVote fields
        [JsonPropertyName("candidateId")]
        public string CandidateId { get; set; } = string.Empty;

        [JsonPropertyName("lastLogIndex")]
        public long LastLogIndex { get; set; }

        [JsonPropertyName("lastLogTerm")]
        public long LastLogTerm { get; set; }

        // RequestVoteResponse fields
        [JsonPropertyName("voteGranted")]
        public bool VoteGranted { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        // AppendEntries fields
        [JsonPropertyName("prevLogIndex")]
        public long PrevLogIndex { get; set; }

        [JsonPropertyName("prevLogTerm")]
        public long PrevLogTerm { get; set; }

        [JsonPropertyName("entries")]
        public List<RaftLogEntry>? Entries { get; set; }

        [JsonPropertyName("leaderCommit")]
        public long LeaderCommit { get; set; }

        // AppendEntriesResponse fields
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("matchIndex")]
        public long MatchIndex { get; set; }

        // Security: HMAC authentication (AUTH-06)
        [JsonPropertyName("hmac")]
        public string? Hmac { get; set; }

        public byte[] Serialize()
        {
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this, RaftJsonContext.Default.RaftMessage);
        }

        public static RaftMessage? Deserialize(byte[] data)
        {
            return System.Text.Json.JsonSerializer.Deserialize(data, RaftJsonContext.Default.RaftMessage);
        }
    }

    /// <summary>
    /// Source-generated JSON context for Raft message serialization.
    /// </summary>
    [JsonSerializable(typeof(RaftMessage))]
    [JsonSerializable(typeof(RaftLogEntry))]
    [JsonSerializable(typeof(List<RaftLogEntry>))]
    internal partial class RaftJsonContext : JsonSerializerContext
    {
    }
}
