using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Addressing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Catalog;

/// <summary>
/// Raft-backed manifest service with in-memory cache for high-performance lookups.
/// </summary>
/// <remarks>
/// <para>
/// RaftBackedManifest implements IManifestService using a Raft consensus engine for
/// linearizable consistency. All write operations (register, update, remove) are proposed
/// via Raft and applied to a state machine. Read operations use an in-memory cache backed
/// by the state machine for O(1) lookup.
/// </para>
/// <para>
/// <strong>Consistency Model:</strong> Writes are linearizable (Raft guarantees all nodes
/// see writes in the same order). Reads are eventually consistent (cache may be slightly
/// stale if Raft replication is in progress).
/// </para>
/// <para>
/// <strong>Performance:</strong>
/// </para>
/// <list type="bullet">
///   <item><description>Single lookup: O(1) via cache or state machine</description></item>
///   <item><description>Batch lookup: 100K+ UUIDs/sec via cache</description></item>
///   <item><description>Time-range query: O(N) scan with UUID v7 timestamp filtering</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Raft-backed manifest service")]
public sealed class RaftBackedManifest : IManifestService
{
    private readonly IConsensusEngine _raft;
    private readonly ManifestStateMachine _stateMachine;
    private readonly ManifestCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="RaftBackedManifest"/> class.
    /// </summary>
    /// <param name="raft">The Raft consensus engine.</param>
    public RaftBackedManifest(IConsensusEngine raft)
    {
        _raft = raft;
        _stateMachine = new ManifestStateMachine();
        _cache = new ManifestCache();

        // Register state machine with Raft via OnCommit callback
        _raft.OnCommit(proposal =>
        {
            if (proposal.Command == "manifest-update")
            {
                _stateMachine.Apply(proposal.Payload);
            }
        });
    }

    /// <inheritdoc />
    public async Task<ObjectLocationEntry?> GetLocationAsync(ObjectIdentity objectId, CancellationToken ct = default)
    {
        // Try cache first
        if (_cache.TryGet(objectId, out var cached))
            return cached;

        // Read from state machine
        var entry = _stateMachine.GetLocation(objectId);
        if (entry != null)
        {
            _cache.Set(entry);
        }

        return entry;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ObjectLocationEntry>> GetLocationsBatchAsync(IEnumerable<ObjectIdentity> objectIds, CancellationToken ct = default)
    {
        var ids = objectIds.ToList();

        // Try cache
        var results = new List<ObjectLocationEntry>();
        var misses = new List<ObjectIdentity>();

        foreach (var id in ids)
        {
            if (_cache.TryGet(id, out var cached))
                results.Add(cached);
            else
                misses.Add(id);
        }

        // Fetch misses from state machine
        if (misses.Any())
        {
            var entries = _stateMachine.GetLocations(misses);
            foreach (var entry in entries)
            {
                _cache.Set(entry);
                results.Add(entry);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ObjectLocationEntry>> QueryByTimeRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        return _stateMachine.QueryByTimeRange(start, end);
    }

    /// <inheritdoc />
    public async Task RegisterObjectAsync(ObjectLocationEntry entry, CancellationToken ct = default)
    {
        var command = new ManifestCommand
        {
            Action = "register",
            Entry = entry
        };

        var proposal = new Proposal
        {
            Command = "manifest-update",
            Payload = JsonSerializer.SerializeToUtf8Bytes(command)
        };

        await _raft.ProposeAsync(proposal).ConfigureAwait(false);

        _cache.Set(entry);
    }

    /// <inheritdoc />
    public async Task UpdateLocationAsync(ObjectIdentity objectId, IReadOnlyList<string> nodeIds, CancellationToken ct = default)
    {
        var command = new ManifestCommand
        {
            Action = "update",
            ObjectId = objectId,
            NodeIds = nodeIds
        };

        var proposal = new Proposal
        {
            Command = "manifest-update",
            Payload = JsonSerializer.SerializeToUtf8Bytes(command)
        };

        await _raft.ProposeAsync(proposal).ConfigureAwait(false);

        _cache.Invalidate(objectId);
    }

    /// <inheritdoc />
    public async Task RemoveObjectAsync(ObjectIdentity objectId, CancellationToken ct = default)
    {
        var command = new ManifestCommand
        {
            Action = "remove",
            ObjectId = objectId
        };

        var proposal = new Proposal
        {
            Command = "manifest-update",
            Payload = JsonSerializer.SerializeToUtf8Bytes(command)
        };

        await _raft.ProposeAsync(proposal).ConfigureAwait(false);

        _cache.Invalidate(objectId);
    }

    /// <inheritdoc />
    public async Task<ManifestStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return _stateMachine.GetStatistics();
    }
}
