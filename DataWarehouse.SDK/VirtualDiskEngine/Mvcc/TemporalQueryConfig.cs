using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// Configuration parameters for temporal point-in-time queries against a VDE volume.
/// Controls snapshot depth limits, tombstone visibility, and maximum query age enforcement.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-34: Temporal point query engine (VOPT-44+58)")]
public sealed class TemporalQueryConfig
{
    /// <summary>
    /// Maximum number of snapshots the epoch index may bisect through during a single query.
    /// Prevents unbounded scan cost when snapshot history is very deep.
    /// Default: 1024.
    /// </summary>
    public int MaxSnapshotDepth { get; set; } = 1024;

    /// <summary>
    /// When <c>true</c>, inodes that have been tombstoned (deleted) are included in query
    /// results with <see cref="TemporalQueryResult.InodeExisted"/> set to <c>false</c> so
    /// callers can distinguish "never existed" from "existed but was later deleted".
    /// When <c>false</c> (default), tombstoned inodes are treated as never-existing.
    /// </summary>
    public bool IncludeDeletedInodes { get; set; } = false;

    /// <summary>
    /// Maximum age of a temporal query. Queries requesting a point in time older than
    /// <c>UtcNow - MaxQueryAge</c> are rejected with <see cref="ArgumentOutOfRangeException"/>
    /// to prevent unbounded history traversal.
    /// Default: 365 days.
    /// </summary>
    public TimeSpan MaxQueryAge { get; set; } = TimeSpan.FromDays(365);
}
