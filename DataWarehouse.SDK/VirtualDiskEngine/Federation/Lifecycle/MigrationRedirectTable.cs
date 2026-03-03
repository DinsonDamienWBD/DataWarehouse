using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;

/// <summary>
/// Thread-safe table that tracks active shard migrations and provides transparent read redirection.
/// During a migration, reads for the source shard are redirected to the destination shard once
/// the migration enters the <see cref="MigrationPhase.CatchingUp"/> or <see cref="MigrationPhase.Switching"/>
/// phase (data is sufficiently replicated). During <see cref="MigrationPhase.CopyingBlocks"/>, reads
/// continue from the source (data not yet fully available at destination).
/// </summary>
/// <remarks>
/// Bounded to <see cref="MaxConcurrentMigrations"/> entries to prevent unbounded resource consumption
/// in runaway rebalancing scenarios.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 93: VDE 2.0B Shard Lifecycle (VSHL-04)")]
public sealed class MigrationRedirectTable : IDisposable
{
    /// <summary>
    /// Maximum number of concurrent migrations the redirect table can track.
    /// </summary>
    public const int MaxConcurrentMigrations = 1024;

    private readonly BoundedDictionary<Guid, ShardMigrationState> _activeMigrations;

    /// <summary>
    /// Creates a new migration redirect table.
    /// </summary>
    public MigrationRedirectTable()
    {
        _activeMigrations = new BoundedDictionary<Guid, ShardMigrationState>(MaxConcurrentMigrations);
    }

    /// <summary>
    /// Attempts to find a redirect destination for the given source shard.
    /// Returns true and sets <paramref name="destinationShardId"/> only when the migration
    /// is in <see cref="MigrationPhase.CatchingUp"/> or <see cref="MigrationPhase.Switching"/>
    /// phase (destination has enough data to serve reads).
    /// </summary>
    /// <param name="sourceShardId">The source shard VDE ID to look up.</param>
    /// <param name="destinationShardId">
    /// When this method returns true, contains the destination shard VDE ID to redirect reads to.
    /// </param>
    /// <returns>
    /// True if reads should be redirected to the destination; false if the source should still serve reads
    /// (either no active migration, or migration is still in the bulk copy phase).
    /// </returns>
    public bool TryGetRedirect(Guid sourceShardId, out Guid destinationShardId)
    {
        destinationShardId = Guid.Empty;

        if (!_activeMigrations.TryGetValue(sourceShardId, out ShardMigrationState? state))
            return false;

        // Only redirect during CatchingUp or Switching when destination has the data
        MigrationPhase phase = state.Phase;
        if (phase is MigrationPhase.CatchingUp or MigrationPhase.Switching)
        {
            destinationShardId = state.DestinationShardId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Registers a migration in the redirect table, keyed by source shard ID.
    /// </summary>
    /// <param name="state">The migration state to register.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="state"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the table is at capacity and cannot accept new migrations.
    /// </exception>
    public void RegisterMigration(ShardMigrationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!_activeMigrations.TryAdd(state.SourceShardId, state))
        {
            // If already present with same source, update it; otherwise capacity exceeded
            if (_activeMigrations.TryGetValue(state.SourceShardId, out _))
            {
                // Replace existing migration for this source shard
                _activeMigrations.TryRemove(state.SourceShardId, out _);
                if (!_activeMigrations.TryAdd(state.SourceShardId, state))
                {
                    throw new InvalidOperationException(
                        $"Failed to register migration for shard {state.SourceShardId}: redirect table is at capacity ({MaxConcurrentMigrations}).");
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"Cannot register migration for shard {state.SourceShardId}: redirect table is at capacity ({MaxConcurrentMigrations}).");
            }
        }
    }

    /// <summary>
    /// Removes a migration from the redirect table.
    /// </summary>
    /// <param name="sourceShardId">The source shard VDE ID to unregister.</param>
    public void UnregisterMigration(Guid sourceShardId)
    {
        _activeMigrations.TryRemove(sourceShardId, out _);
    }

    /// <summary>
    /// Returns all non-terminal migrations currently tracked.
    /// </summary>
    /// <returns>List of active (non-terminal) migration states.</returns>
    public IReadOnlyList<ShardMigrationState> GetActiveMigrations()
    {
        var result = new List<ShardMigrationState>();
        foreach (KeyValuePair<Guid, ShardMigrationState> kvp in _activeMigrations)
        {
            if (!kvp.Value.IsTerminal)
                result.Add(kvp.Value);
        }
        return result;
    }

    /// <summary>
    /// Looks up a migration state by source or destination shard ID.
    /// </summary>
    /// <param name="shardId">The shard VDE ID to search for (checked as both source and destination).</param>
    /// <returns>The matching migration state, or null if no active migration involves this shard.</returns>
    public ShardMigrationState? GetMigrationForShard(Guid shardId)
    {
        // Check as source first (direct key lookup)
        if (_activeMigrations.TryGetValue(shardId, out ShardMigrationState? state))
            return state;

        // Check as destination (linear scan)
        foreach (KeyValuePair<Guid, ShardMigrationState> kvp in _activeMigrations)
        {
            if (kvp.Value.DestinationShardId == shardId)
                return kvp.Value;
        }

        return null;
    }

    /// <summary>
    /// Disposes the underlying bounded dictionary.
    /// </summary>
    public void Dispose()
    {
        _activeMigrations.Dispose();
    }
}
