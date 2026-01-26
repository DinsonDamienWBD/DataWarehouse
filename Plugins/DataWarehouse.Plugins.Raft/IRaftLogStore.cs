using System.Collections.Generic;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.Raft
{
    /// <summary>
    /// Interface for persistent Raft log storage.
    /// Implementations must ensure durability guarantees for Raft safety.
    /// </summary>
    public interface IRaftLogStore
    {
        /// <summary>
        /// Gets the index of the last log entry.
        /// Returns 0 if log is empty.
        /// </summary>
        Task<long> GetLastIndexAsync();

        /// <summary>
        /// Gets the term of the last log entry.
        /// Returns 0 if log is empty.
        /// </summary>
        Task<long> GetLastTermAsync();

        /// <summary>
        /// Gets a log entry at the specified index.
        /// Returns null if the entry does not exist.
        /// </summary>
        /// <param name="index">1-based log index</param>
        Task<RaftLogEntry?> GetEntryAsync(long index);

        /// <summary>
        /// Gets all log entries starting from the specified index (inclusive).
        /// </summary>
        /// <param name="startIndex">1-based start index</param>
        Task<IEnumerable<RaftLogEntry>> GetEntriesFromAsync(long startIndex);

        /// <summary>
        /// Appends a new log entry.
        /// Must ensure durability before returning (fsync/flush).
        /// </summary>
        Task AppendAsync(RaftLogEntry entry);

        /// <summary>
        /// Truncates all log entries from the specified index (inclusive) onwards.
        /// Used when follower receives conflicting entries from leader.
        /// </summary>
        Task TruncateFromAsync(long index);

        /// <summary>
        /// Gets the persistent Raft state (currentTerm and votedFor).
        /// </summary>
        Task<(long term, string? votedFor)> GetPersistentStateAsync();

        /// <summary>
        /// Saves the persistent Raft state (currentTerm and votedFor).
        /// Must ensure durability before returning.
        /// </summary>
        Task SavePersistentStateAsync(long term, string? votedFor);

        /// <summary>
        /// Compacts the log by removing entries up to (but not including) the specified index.
        /// Used after snapshotting.
        /// </summary>
        /// <param name="upToIndex">Last index included in snapshot (exclusive)</param>
        Task CompactAsync(long upToIndex);
    }

    /// <summary>
    /// Represents a Raft log entry in persistent storage.
    /// </summary>
    public class RaftLogEntry
    {
        public long Index { get; set; }
        public long Term { get; set; }
        public string Command { get; set; } = string.Empty;
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public string ProposalId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}
