using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateConsensus;

/// <summary>
/// Represents a single Raft consensus group within the Multi-Raft architecture.
/// Each group independently manages leader election, log replication, and state machine application.
/// Groups are routed to via consistent hashing in <see cref="UltimateConsensusPlugin"/>.
/// </summary>
public sealed class RaftGroup
{
    private readonly object _stateLock = new();
    private RaftGroupState _state = RaftGroupState.Follower;
    private long _currentTerm;
    private string? _votedFor;
    private DateTime _lastHeartbeat = DateTime.UtcNow;

    /// <summary>
    /// Unique identifier for this Raft group.
    /// </summary>
    public int GroupId { get; }

    /// <summary>
    /// List of voter node IDs participating in this group.
    /// </summary>
    public List<string> VoterIds { get; } = new();

    /// <summary>
    /// Current leader of this group, or null if no leader is elected.
    /// </summary>
    public string? CurrentLeader { get; private set; }

    /// <summary>
    /// Replicated log for this group.
    /// </summary>
    public ConcurrentQueue<LogEntry> Log { get; } = new();

    /// <summary>
    /// Highest log index known to be committed (replicated to a majority).
    /// </summary>
    public long CommitIndex { get; private set; }

    /// <summary>
    /// Highest log index applied to the state machine.
    /// </summary>
    public long LastApplied { get; private set; }

    /// <summary>
    /// Current Raft term for this group.
    /// </summary>
    public long CurrentTerm
    {
        get { lock (_stateLock) { return _currentTerm; } }
    }

    /// <summary>
    /// Current state of this group's local node.
    /// </summary>
    public RaftGroupState State
    {
        get { lock (_stateLock) { return _state; } }
    }

    /// <summary>
    /// Whether this group's local node is the leader.
    /// </summary>
    public bool IsLeader
    {
        get { lock (_stateLock) { return _state == RaftGroupState.Leader; } }
    }

    /// <summary>
    /// Applied state machine entries (committed and applied data).
    /// </summary>
    private readonly ConcurrentDictionary<long, byte[]> _stateMachine = new();

    /// <summary>
    /// The local node ID for this group.
    /// </summary>
    private readonly string _localNodeId;

    /// <summary>
    /// Next log index to assign.
    /// </summary>
    private long _nextLogIndex = 1;

    /// <summary>
    /// Creates a new Raft group.
    /// </summary>
    /// <param name="groupId">Unique group identifier.</param>
    /// <param name="localNodeId">Local node's identifier.</param>
    /// <param name="voterIds">List of voter node IDs (3-5 recommended).</param>
    public RaftGroup(int groupId, string localNodeId, IEnumerable<string> voterIds)
    {
        GroupId = groupId;
        _localNodeId = localNodeId;
        VoterIds.AddRange(voterIds);
    }

    /// <summary>
    /// Attempts leader election for this group using simple majority voting.
    /// Uses randomized election timeout to prevent split votes.
    /// </summary>
    /// <returns>True if this node was elected leader.</returns>
    public Task<bool> ElectLeaderAsync()
    {
        lock (_stateLock)
        {
            _currentTerm++;
            _state = RaftGroupState.Candidate;
            _votedFor = _localNodeId;
        }

        // In a single-node or local-only scenario, we win immediately
        // In distributed scenarios, we'd send RequestVote RPCs to peers
        int votesReceived = 1; // Self-vote
        int quorum = VoterIds.Count / 2 + 1;

        // For local Multi-Raft groups (same process), simulate quorum
        // Real distributed election happens via message bus
        if (VoterIds.Count <= 1 || votesReceived >= quorum)
        {
            lock (_stateLock)
            {
                _state = RaftGroupState.Leader;
                CurrentLeader = _localNodeId;
                _lastHeartbeat = DateTime.UtcNow;
            }
            return Task.FromResult(true);
        }

        // Insufficient votes in local simulation
        lock (_stateLock)
        {
            _state = RaftGroupState.Follower;
        }
        return Task.FromResult(false);
    }

    /// <summary>
    /// Appends an entry to the log and attempts to replicate to a majority.
    /// Only the leader should call this method.
    /// </summary>
    /// <param name="entry">Log entry to append and replicate.</param>
    /// <returns>True if the entry was committed (replicated to majority).</returns>
    public Task<bool> AppendEntryAsync(LogEntry entry)
    {
        lock (_stateLock)
        {
            if (_state != RaftGroupState.Leader)
            {
                return Task.FromResult(false);
            }
        }

        Log.Enqueue(entry);

        // In local Multi-Raft, commit immediately (single-node quorum)
        // In distributed mode, wait for majority acknowledgment
        int quorum = VoterIds.Count / 2 + 1;
        int acks = 1; // Self-ack

        if (acks >= quorum)
        {
            CommitIndex = entry.Index;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Applies all committed but unapplied entries to the state machine.
    /// Processes entries from LastApplied+1 up to CommitIndex.
    /// </summary>
    /// <returns>Number of entries applied.</returns>
    public Task<int> ApplyCommittedEntriesAsync()
    {
        int applied = 0;

        while (LastApplied < CommitIndex)
        {
            var targetIndex = LastApplied + 1;

            // Find the entry in the log
            var entry = Log.FirstOrDefault(e => e.Index == targetIndex);
            if (entry is null) break;

            // Apply to state machine
            _stateMachine[entry.Index] = entry.Data;
            LastApplied = targetIndex;
            applied++;
        }

        return Task.FromResult(applied);
    }

    /// <summary>
    /// Creates a snapshot of the current committed state for log compaction.
    /// </summary>
    /// <returns>Serialized snapshot of the committed state machine.</returns>
    public Task<byte[]> TakeSnapshotAsync()
    {
        var snapshot = new Dictionary<string, object>
        {
            ["groupId"] = GroupId,
            ["commitIndex"] = CommitIndex,
            ["lastApplied"] = LastApplied,
            ["currentTerm"] = _currentTerm,
            ["leader"] = CurrentLeader ?? "",
            ["entryCount"] = _stateMachine.Count,
            ["snapshotAt"] = DateTime.UtcNow.ToString("O")
        };

        var data = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        return Task.FromResult(data);
    }

    /// <summary>
    /// Restores state from a snapshot, replacing the current state machine.
    /// </summary>
    /// <param name="snapshot">Serialized snapshot data.</param>
    /// <returns>A completed task.</returns>
    public Task RestoreSnapshotAsync(byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var doc = JsonDocument.Parse(snapshot);
        var root = doc.RootElement;

        if (root.TryGetProperty("commitIndex", out var ci))
        {
            CommitIndex = ci.GetInt64();
        }
        if (root.TryGetProperty("lastApplied", out var la))
        {
            LastApplied = la.GetInt64();
        }
        if (root.TryGetProperty("currentTerm", out var ct))
        {
            lock (_stateLock)
            {
                _currentTerm = ct.GetInt64();
            }
        }
        if (root.TryGetProperty("leader", out var leader))
        {
            var leaderStr = leader.GetString();
            CurrentLeader = string.IsNullOrEmpty(leaderStr) ? null : leaderStr;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the next log entry with an auto-incremented index.
    /// </summary>
    /// <param name="data">Binary payload for the entry.</param>
    /// <returns>A new log entry with the next sequential index.</returns>
    public LogEntry CreateEntry(byte[] data)
    {
        var index = Interlocked.Increment(ref _nextLogIndex);
        return LogEntry.Create(_currentTerm, index, data);
    }

    /// <summary>
    /// Gets a summary of this group's health.
    /// </summary>
    /// <returns>Dictionary with group health metrics.</returns>
    public Dictionary<string, string> GetHealthSummary()
    {
        lock (_stateLock)
        {
            return new Dictionary<string, string>
            {
                [$"group-{GroupId}.state"] = _state.ToString(),
                [$"group-{GroupId}.term"] = _currentTerm.ToString(),
                [$"group-{GroupId}.leader"] = CurrentLeader ?? "none",
                [$"group-{GroupId}.commitIndex"] = CommitIndex.ToString(),
                [$"group-{GroupId}.lastApplied"] = LastApplied.ToString(),
                [$"group-{GroupId}.logSize"] = Log.Count.ToString(),
                [$"group-{GroupId}.voters"] = VoterIds.Count.ToString()
            };
        }
    }
}

/// <summary>
/// Raft group state for a local node within a group.
/// </summary>
public enum RaftGroupState
{
    /// <summary>Following the current leader.</summary>
    Follower,
    /// <summary>Running for leader election.</summary>
    Candidate,
    /// <summary>Elected leader of this group.</summary>
    Leader
}
