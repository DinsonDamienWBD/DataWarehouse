using DataWarehouse.SDK.Contracts;
using System;
using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// An entry in the Raft replicated log.
    /// Each entry contains a command and its associated term and index for ordering and consistency.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 29: Raft consensus log entry")]
    public sealed class RaftLogEntry
    {
        /// <summary>
        /// Position of this entry in the log (1-indexed).
        /// </summary>
        [JsonPropertyName("index")]
        public long Index { get; set; }

        /// <summary>
        /// Term when this entry was received by the leader.
        /// </summary>
        [JsonPropertyName("term")]
        public long Term { get; set; }

        /// <summary>
        /// The command name to apply to the state machine.
        /// </summary>
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// The command data payload.
        /// </summary>
        [JsonPropertyName("payload")]
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Timestamp when this entry was created.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }
    }
}
