using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// Interface for persistent Raft log storage in the SDK consensus engine.
    /// Implementations must ensure durability guarantees for Raft safety:
    /// log entries, currentTerm, and votedFor must survive process restarts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two implementations are provided:
    /// <list type="bullet">
    /// <item><description><see cref="InMemoryRaftLogStore"/>: In-memory storage for testing and single-node deployments.</description></item>
    /// <item><description>File-based store in the Raft plugin for production multi-node clusters.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// All mutating operations must ensure durability (fsync) before returning
    /// to maintain Raft's safety guarantees under crash-recovery.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65: Raft log persistence abstraction")]
    public interface IRaftLogStore
    {
        /// <summary>
        /// Appends a log entry to the store. Must be durable before returning.
        /// </summary>
        /// <param name="entry">The log entry to append.</param>
        Task AppendAsync(RaftLogEntry entry);

        /// <summary>
        /// Gets a log entry at the specified 1-based index.
        /// Returns null if the index is out of range.
        /// </summary>
        /// <param name="index">1-based log index.</param>
        Task<RaftLogEntry?> GetAsync(long index);

        /// <summary>
        /// Gets all log entries in the range [fromIndex, toIndex] inclusive (1-based).
        /// </summary>
        /// <param name="fromIndex">Start index (inclusive, 1-based).</param>
        /// <param name="toIndex">End index (inclusive, 1-based).</param>
        Task<IReadOnlyList<RaftLogEntry>> GetRangeAsync(long fromIndex, long toIndex);

        /// <summary>
        /// Truncates all log entries from the specified index (inclusive) onwards.
        /// Used when a follower receives conflicting entries from the leader.
        /// </summary>
        /// <param name="fromIndex">Index from which to truncate (inclusive, 1-based).</param>
        Task TruncateFromAsync(long fromIndex);

        /// <summary>
        /// Gets the index of the last log entry. Returns 0 if empty.
        /// </summary>
        Task<long> GetLastIndexAsync();

        /// <summary>
        /// Gets the term of the last log entry. Returns 0 if empty.
        /// </summary>
        Task<long> GetLastTermAsync();

        /// <summary>
        /// Gets all log entries from the specified index onwards (inclusive).
        /// </summary>
        /// <param name="fromIndex">Start index (inclusive, 1-based).</param>
        Task<IReadOnlyList<RaftLogEntry>> GetFromAsync(long fromIndex);

        /// <summary>
        /// Gets the total count of log entries.
        /// </summary>
        long Count { get; }
    }
}
