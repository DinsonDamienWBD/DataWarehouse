using System;

namespace DataWarehouse.Plugins.UltimateConsensus;

/// <summary>
/// Represents a single entry in a Raft group's replicated log.
/// Each entry is uniquely identified by its (Term, Index) pair.
/// </summary>
/// <param name="Term">The Raft term when this entry was created by the leader.</param>
/// <param name="Index">Monotonically increasing position in the log.</param>
/// <param name="Data">Binary payload of the proposed state change.</param>
/// <param name="Timestamp">UTC timestamp when the entry was created.</param>
public sealed record LogEntry(long Term, long Index, byte[] Data, DateTime Timestamp)
{
    /// <summary>
    /// Creates a new log entry with the current UTC timestamp.
    /// </summary>
    /// <param name="term">Raft term.</param>
    /// <param name="index">Log index.</param>
    /// <param name="data">Binary payload.</param>
    /// <returns>A new log entry.</returns>
    public static LogEntry Create(long term, long index, byte[] data)
        => new(term, index, data, DateTime.UtcNow);
}
