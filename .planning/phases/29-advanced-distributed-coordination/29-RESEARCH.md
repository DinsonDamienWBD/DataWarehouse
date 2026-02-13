# Phase 29: Advanced Distributed Coordination - Research

**Researched:** 2026-02-14
**Domain:** SWIM gossip membership, Raft consensus, CRDT conflict resolution, consistent hashing, resource-aware load balancing
**Confidence:** HIGH

## Summary

Phase 29 implements production-ready distributed coordination algorithms against the contracts defined in Phase 26. The SDK already has all necessary interfaces (`IClusterMembership`, `ILoadBalancerStrategy`, `IP2PNetwork`/`IGossipProtocol`, `IReplicationSync`, `IConsistentHashRing`) and in-memory single-node implementations. Phase 29 replaces these stubs with real multi-node algorithms: SWIM gossip for cluster membership, Raft for leader election, CRDT-based multi-master replication, and consistent hashing with resource-aware load balancing.

The codebase already has significant distributed infrastructure from earlier phases: `VectorClock` (SDK Replication namespace), `EnhancedVectorClock` (Replication plugin), CRDT types (`GCounterCrdt`, `PNCounterCrdt`, `ORSetCrdt<T>`, `LWWRegisterCrdt<T>` in the Replication plugin), `ConsistentHashShardingStrategy` (DataManagement plugin), `IConsensusEngine` with `GeoRaftState`/`GeoRaftNode` types, and `FederatedMessageBusBase` with `IConsistentHashRing`. The primary gap is that none of these are wired up as SDK-level implementations of the Phase 26 contracts -- they exist as plugin-internal code or stub SDK types.

All implementations MUST be SDK-level classes in `DataWarehouse.SDK/Infrastructure/` (not plugins), implementing the Phase 26 contracts. They must follow Rule 13 (production-ready, no mocks/stubs), use bounded collections, be thread-safe via `ConcurrentDictionary`/`SemaphoreSlim`, and respect `CancellationToken` on all async paths.

**Primary recommendation:** Organize into 4 plans as defined in ROADMAP. Each plan produces self-contained SDK implementations that can run independently. Plan 29-01 (SWIM + P2P gossip) is foundational -- 29-02 (Raft) and 29-04 (load balancing) depend on cluster membership being available. Plan 29-03 (CRDT replication) depends on P2P networking.

## Existing Infrastructure Inventory

### Phase 26 Contracts (targets for implementation)

| Contract | File | Key Methods | In-Memory Stub |
|----------|------|-------------|----------------|
| `IClusterMembership` | `SDK/Contracts/Distributed/IClusterMembership.cs` | JoinAsync, LeaveAsync, GetMembers, GetLeader, IsHealthyAsync | `InMemoryClusterMembership` (always self as leader) |
| `ILoadBalancerStrategy` | `SDK/Contracts/Distributed/ILoadBalancerStrategy.cs` | SelectNode, SelectNodeAsync, ReportNodeHealth | `InMemoryLoadBalancerStrategy` (always first node) |
| `IP2PNetwork` | `SDK/Contracts/Distributed/IP2PNetwork.cs` | DiscoverPeersAsync, SendToPeerAsync, BroadcastAsync, RequestFromPeerAsync | `InMemoryP2PNetwork` (no peers) |
| `IGossipProtocol` | `SDK/Contracts/Distributed/IP2PNetwork.cs` | SpreadAsync, GetPendingAsync, OnGossipReceived | `InMemoryP2PNetwork` (implements both) |
| `IReplicationSync` | `SDK/Contracts/Distributed/IReplicationSync.cs` | SyncAsync, GetSyncStatusAsync, ResolveConflictAsync | `InMemoryReplicationSync` (local-only, LocalWins) |
| `IConsistentHashRing` | `SDK/Contracts/Distributed/IFederatedMessageBus.cs` | GetNode, GetNodes, AddNode, RemoveNode, VirtualNodeCount | None (interface only, no in-memory) |
| `FederatedMessageBusBase` | `SDK/Contracts/Distributed/FederatedMessageBusBase.cs` | PublishAsync (with routing), SendToRemoteNodeAsync (abstract) | `InMemoryFederatedMessageBus` (local-only) |

### Pre-Existing Distributed Primitives in SDK

| Type | Location | Status |
|------|----------|--------|
| `VectorClock` (record) | `SDK/Replication/IMultiMasterReplication.cs` | Production-ready -- Increment, HappensBefore, Merge |
| `IConsensusEngine` | `SDK/Contracts/IConsensusEngine.cs` | Interface + GeoRaft types (GeoRaftState, GeoRaftNode, RequestVoteMessage, etc.) |
| `IMultiMasterReplication` | `SDK/Replication/IMultiMasterReplication.cs` | Full interface with WriteAsync, ReadAsync, RegisterConflictResolver, SubscribeAsync |
| `MultiMasterReplicationPluginBase` | `SDK/Contracts/MultiMasterPluginBases.cs` | Abstract base with CRDT resolver factory (CreateCrdtResolver) |
| `ConsistencyLevel` enum | `SDK/Replication/IMultiMasterReplication.cs` | Eventual, ReadYourWrites, CausalConsistency, BoundedStaleness, Strong |
| `ConflictResolution` enum | `SDK/Replication/IMultiMasterReplication.cs` | LastWriterWins, VectorClock, CRDT, CustomResolver, ManualResolution |

### Pre-Existing Distributed Primitives in Plugins (reference only -- NOT for SDK use)

| Type | Plugin | Notes |
|------|--------|-------|
| `EnhancedVectorClock` | UltimateReplication | Thread-safe, ConcurrentDictionary-based -- pattern to follow |
| `GCounterCrdt`, `PNCounterCrdt` | UltimateReplication/Core | Working CRDT implementations -- SDK versions should match semantics |
| `ORSetCrdt<T>` | UltimateReplication/Core | Observed-Remove Set with causal context tags |
| `LWWRegisterCrdt<T>` | UltimateReplication/Core | Last-Writer-Wins Register with timestamp ordering |
| `ConsistentHashShardingStrategy` | UltimateDataManagement | Virtual-node ring with ReaderWriterLockSlim -- pattern to follow |
| `ConsistentHashingLoadBalancingStrategy` | UltimateResilience | Similar pattern, different domain |
| `GossipCoordinationStrategy` | UltimateWorkflow | Gossip-style coordination (reference) |
| `HotHotStrategy` (Active-Active) | UltimateReplication | Active-active with EnhancedVectorClock (reference) |

### SDK Contract Supporting Types

| Type | Contract File | Purpose |
|------|---------------|---------|
| `ClusterNode` | IClusterMembership.cs | NodeId, Address, Port, Role, Status, JoinedAt, Metadata |
| `ClusterNodeRole` | IClusterMembership.cs | Leader, Follower, Observer, Candidate |
| `ClusterNodeStatus` | IClusterMembership.cs | Active, Joining, Leaving, Suspected, Dead |
| `ClusterMembershipEvent` | IClusterMembership.cs | EventType, Node, Timestamp, Reason |
| `LoadBalancerContext` | ILoadBalancerStrategy.cs | RequestKey, AvailableNodes, Metadata |
| `NodeHealthReport` | ILoadBalancerStrategy.cs | CpuUsage, MemoryUsage, ActiveConnections, AverageLatency |
| `PeerInfo` | IP2PNetwork.cs | PeerId, Address, Port, LastSeen, Metadata |
| `GossipMessage` | IP2PNetwork.cs | MessageId, OriginNodeId, Payload, Generation, Timestamp |
| `SyncConflict` | IReplicationSync.cs | Key, LocalValue, RemoteValue, LocalTimestamp, RemoteTimestamp |
| `ConflictResolutionResult` | IReplicationSync.cs | Key, Strategy, ResolvedValue |
| `ConflictResolutionStrategy` enum | IReplicationSync.cs | LocalWins, RemoteWins, LatestWins, Merge, Custom |
| `MessageRoutingTarget` enum | IFederatedMessageBus.cs | Local, Remote, Broadcast, ConsistentHash |

## Architecture Patterns

### Recommended Project Structure

All Phase 29 implementations go in `DataWarehouse.SDK/Infrastructure/`:

```
DataWarehouse.SDK/Infrastructure/
├── InMemory/                          # Phase 26 stubs (existing, unchanged)
│   ├── InMemoryClusterMembership.cs
│   ├── InMemoryLoadBalancerStrategy.cs
│   ├── InMemoryP2PNetwork.cs
│   ├── InMemoryReplicationSync.cs
│   └── InMemoryFederatedMessageBus.cs
├── Distributed/                       # Phase 29 implementations (NEW)
│   ├── Membership/
│   │   ├── SwimClusterMembership.cs       # SWIM gossip membership (DIST-12)
│   │   └── SwimProtocolState.cs           # SWIM protocol state machine
│   ├── Consensus/
│   │   ├── RaftConsensusEngine.cs         # Raft leader election (DIST-13)
│   │   ├── RaftState.cs                   # Persistent state (term, votedFor, log)
│   │   └── RaftLogEntry.cs                # Replicated log entries
│   ├── Replication/
│   │   ├── CrdtReplicationSync.cs         # CRDT-based replication (DIST-14)
│   │   ├── CrdtRegistry.cs               # CRDT type registry + merge
│   │   ├── GossipReplicator.cs            # P2P gossip replication (DIST-15)
│   │   └── ConsistentHashRing.cs          # IConsistentHashRing implementation
│   └── LoadBalancing/
│       ├── ConsistentHashLoadBalancer.cs   # Consistent hashing LB (DIST-16)
│       └── ResourceAwareLoadBalancer.cs    # Resource-aware LB (DIST-17)
```

### Pattern 1: SWIM Gossip Membership Protocol

**What:** Decentralized cluster membership with failure detection via random probing.
**Algorithm summary:**
1. Each node periodically selects a random peer and sends a `Ping`
2. If `Ack` received, peer is alive
3. If no `Ack` within timeout, send `PingReq` to k random peers asking them to probe the target
4. If no indirect `Ack`, mark target as `Suspected`
5. If Suspected node does not respond within suspicion timeout, mark as `Dead`
6. Membership changes (join/leave/suspect/dead) are disseminated by piggybacking on ping messages

**Contract mapping:**
- Implements `IClusterMembership` (JoinAsync, LeaveAsync, GetMembers, GetLeader, IsHealthyAsync)
- Uses `IP2PNetwork` for actual network communication (SendToPeerAsync, BroadcastAsync)
- Uses `IGossipProtocol` for disseminating membership changes
- Fires `OnMembershipChanged` events on state transitions
- Uses `ClusterNodeStatus` states: `Active` <-> `Suspected` -> `Dead`

**Key implementation details:**
```csharp
// SWIM state per known node
internal sealed class SwimMemberState
{
    public ClusterNode Node { get; set; }
    public ClusterNodeStatus Status { get; set; }
    public int IncarnationNumber { get; set; }
    public DateTimeOffset LastPingAt { get; set; }
    public DateTimeOffset SuspectedAt { get; set; }
}
```

**Configuration parameters:**
- `ProtocolPeriodMs` (default: 1000ms) -- how often to probe a random peer
- `PingTimeoutMs` (default: 500ms) -- time to wait for direct Ack
- `IndirectPingCount` (default: 3) -- number of peers for indirect probing
- `SuspicionTimeoutMs` (default: 5000ms) -- time before Suspected -> Dead
- `MaxGossipSize` (default: 10) -- max membership changes piggybacked per message

### Pattern 2: Raft Consensus for Leader Election

**What:** Leader election with log replication for consistent cluster state.
**Algorithm summary:**
1. Nodes start as Followers with randomized election timeout
2. On timeout without heartbeat, become Candidate, increment term, request votes
3. Candidate receiving majority becomes Leader
4. Leader sends periodic heartbeats (AppendEntries with empty log)
5. Leader replicates log entries to followers for state consistency
6. Committed entries are applied to state machine

**Contract mapping:**
- Implements `IClusterMembership.GetLeader()` -- returns current leader
- Uses `ClusterNodeRole`: Follower, Candidate, Leader (matches existing enum)
- Integrates with SWIM membership: Raft operates over the SWIM-discovered cluster
- Fires `ClusterMembershipEventType.LeaderChanged` on leader transitions

**Key implementation details:**
```csharp
// Raft persistent state (per node)
internal sealed class RaftPersistentState
{
    public long CurrentTerm { get; set; }
    public string? VotedFor { get; set; }
    public List<RaftLogEntry> Log { get; } = new();
}

// Raft volatile state
internal sealed class RaftVolatileState
{
    public long CommitIndex { get; set; }
    public long LastApplied { get; set; }
    // Leader-only state
    public ConcurrentDictionary<string, long> NextIndex { get; } = new();
    public ConcurrentDictionary<string, long> MatchIndex { get; } = new();
}
```

**Existing SDK types to reuse:**
- `GeoRaftState` (SDK/Contracts/IConsensusEngine.cs) -- has Role, CurrentTerm, VotedFor, CommitIndex, LastApplied
- `GeoRaftNode` (SDK/Contracts/IConsensusEngine.cs) -- has NodeId, Address, NextIndex, MatchIndex
- `RequestVoteMessage` / `RequestVoteResponse` -- exactly what Raft needs
- `LogReplicator` -- stub class that needs real implementation

**IMPORTANT:** Do NOT depend on or import from the existing `IConsensusEngine` or `GeoRaft*` types directly. These are v1.0 legacy types with different semantics. Create clean Phase 29 types in the new namespace. The existing types serve as reference for the shape of the data.

### Pattern 3: CRDT Conflict Resolution for Multi-Master Replication

**What:** Conflict-free replicated data types that merge concurrent writes deterministically.
**Algorithm summary:**
1. Each write is tagged with the originating node's vector clock
2. When two writes conflict (concurrent vector clocks), CRDT merge function is applied
3. Merge is commutative, associative, and idempotent -- order-independent convergence
4. SDK provides 4 CRDT types: GCounter, PNCounter, LWWRegister, ORSet

**Contract mapping:**
- Implements `IReplicationSync` (SyncAsync, ResolveConflictAsync, GetSyncStatusAsync)
- Uses `ConflictResolutionStrategy.Merge` for CRDT resolution
- Uses existing `VectorClock` (SDK/Replication) for causality tracking
- Uses `IGossipProtocol.SpreadAsync` for epidemic data propagation
- Fires `SyncEvent` on sync start/complete/conflict

**CRDT types to implement at SDK level:**
```csharp
// SDK-level CRDT abstraction (in SDK/Infrastructure/Distributed/Replication/)
public interface ICrdtType<TSelf> where TSelf : ICrdtType<TSelf>
{
    byte[] Serialize();
    static abstract TSelf Deserialize(byte[] data);
    TSelf Merge(TSelf other);
}

public sealed class GCounterCrdt : ICrdtType<GCounterCrdt> { ... }
public sealed class PNCounterCrdt : ICrdtType<PNCounterCrdt> { ... }
public sealed class LWWRegisterCrdt : ICrdtType<LWWRegisterCrdt> { ... }
public sealed class ORSetCrdt : ICrdtType<ORSetCrdt> { ... }
```

**Existing primitives to reuse:**
- `VectorClock` record in `SDK.Replication` -- Increment, HappensBefore, Merge already implemented
- `ConflictResolutionStrategy` enum -- already has `Merge` value
- `SyncConflict` record -- already has Key, LocalValue, RemoteValue, timestamps
- `ConflictResolutionResult` record -- already has Key, Strategy, ResolvedValue

### Pattern 4: Consistent Hashing with Virtual Nodes

**What:** Distribute requests across cluster nodes using a hash ring with virtual nodes for uniform distribution.
**Algorithm summary:**
1. Each physical node gets N virtual nodes placed on a 32-bit hash ring
2. Request key is hashed; walk ring clockwise to find first virtual node
3. That virtual node maps to the physical node that handles the request
4. When nodes join/leave, only keys between the node and its predecessor are affected

**Contract mapping:**
- Implements `IConsistentHashRing` (GetNode, GetNodes, AddNode, RemoveNode)
- Implements `ILoadBalancerStrategy` (SelectNode, SelectNodeAsync, ReportNodeHealth) with AlgorithmName = "ConsistentHash"
- Uses `LoadBalancerContext.RequestKey` as the hash key
- Uses `System.IO.Hashing` (already a PackageReference in SDK) for hashing

**Existing pattern to follow:**
- `ConsistentHashShardingStrategy` in UltimateDataManagement plugin uses `SortedDictionary<uint, string>` ring with `ReaderWriterLockSlim` -- same approach for SDK version
- 150 virtual nodes per physical shard is the plugin's default -- use similar range (100-200)

### Pattern 5: Resource-Aware Load Balancer

**What:** Route requests to nodes based on CPU/memory/connection metrics, avoiding overloaded nodes.
**Algorithm summary:**
1. Each node periodically reports `NodeHealthReport` (CpuUsage, MemoryUsage, ActiveConnections, AverageLatency)
2. Load balancer computes a weighted score per node: lower load = higher weight
3. Node selection uses weighted random or weighted round-robin
4. Nodes above configurable thresholds (e.g., 90% CPU) are excluded from selection

**Contract mapping:**
- Implements `ILoadBalancerStrategy` with AlgorithmName = "ResourceAware"
- Uses `NodeHealthReport` (already defined in SDK) for CPU/memory/connections/latency
- Uses `LoadBalancerContext.AvailableNodes` filtered by health thresholds

### Anti-Patterns to Avoid

- **Real network I/O in SDK:** All implementations must be "network-agnostic" -- they communicate through `IP2PNetwork` and `IGossipProtocol` abstractions, not raw sockets or HTTP. The actual transport is injected.
- **External NuGet dependencies:** SDK has only 4 PackageReferences. Phase 29 MUST NOT add any new ones. Use `System.IO.Hashing` (already present) for hashing. Use `System.Threading.Channels` (built into .NET) for async message queues.
- **Modifying existing InMemory implementations:** The Phase 26 in-memory stubs remain unchanged. Phase 29 creates NEW implementations alongside them. Consumer code chooses which to use at configuration time.
- **Plugin-level coupling:** All Phase 29 code goes in SDK only. No plugin references, no plugin imports. Plugins use these implementations through the Phase 26 contracts.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Vector clocks | New VectorClock | Existing `SDK.Replication.VectorClock` | Already has Increment, HappensBefore, Merge -- proven correct |
| Hash function | Custom hash | `System.IO.Hashing.XxHash32/64` | Already an SDK dependency, fast and well-distributed |
| Consistent hash ring data structure | Custom ring | `SortedDictionary<uint, string>` + binary search | Same pattern used by ConsistentHashShardingStrategy plugin, proven |
| Thread-safe collections | Manual locking | `ConcurrentDictionary` + `ReaderWriterLockSlim` | .NET BCL, matches existing SDK patterns |
| Async messaging queues | Custom queue | `System.Threading.Channels.Channel<T>` | Built into .NET, backpressure-aware, bounded |
| Timer-based periodic tasks | Custom Thread/Timer | `PeriodicTimer` (.NET 6+) | Clean async pattern, respects CancellationToken |
| Serialization | Custom binary format | `System.Text.Json` | SDK decision (STATE.md): STJ is sole serializer |
| CRDT semantics | Custom merge logic | Follow proven CRDT formulations (GCounter, PNCounter, ORSet, LWWRegister) | Mathematically proven to converge |

## Common Pitfalls

### Pitfall 1: Raft Election Timer Not Randomized
**What goes wrong:** Multiple nodes timeout simultaneously, causing split votes indefinitely.
**Why it happens:** Using a fixed election timeout across all nodes.
**How to avoid:** Election timeout MUST be randomized per node (e.g., 150-300ms range). Use `RandomNumberGenerator` (not `System.Random`) for CSPRNG compliance with SDK crypto standards.
**Warning signs:** Leader election never converges in tests with 3+ nodes.

### Pitfall 2: SWIM Suspicion Without Incarnation Numbers
**What goes wrong:** A slow node gets permanently stuck as Suspected because old "suspect" messages keep circulating.
**Why it happens:** Not implementing incarnation numbers to supersede old state.
**How to avoid:** Each node has an `IncarnationNumber` that it increments when it hears it has been suspected. An Alive message with a higher incarnation number overrides Suspect messages.
**Warning signs:** Nodes that recover from temporary slowness remain Suspected forever.

### Pitfall 3: CRDT Merge Not Idempotent
**What goes wrong:** Applying the same update twice produces incorrect state.
**Why it happens:** Merge function uses addition instead of max for counters, or doesn't deduplicate operations.
**How to avoid:** GCounter merge uses `Math.Max` per node, not sum. ORSet merge uses set union for adds, tombstone tracking for removes. LWWRegister merge picks higher timestamp.
**Warning signs:** Counter values drift higher than expected after re-sync.

### Pitfall 4: Consistent Hash Ring Empty Checks
**What goes wrong:** `SelectNode` throws `InvalidOperationException` on empty ring.
**Why it happens:** Not handling the case where all nodes have been removed (cluster drain).
**How to avoid:** Always check `AvailableNodes.Count > 0` before ring operations. The existing `InMemoryLoadBalancerStrategy` already throws on empty -- match this behavior.
**Warning signs:** Unhandled exceptions during cluster maintenance windows.

### Pitfall 5: Unbounded Gossip Message Accumulation
**What goes wrong:** Memory grows unboundedly as gossip messages queue up.
**Why it happens:** Not bounding the pending gossip message queue.
**How to avoid:** Use bounded `Channel<GossipMessage>` with `BoundedChannelFullMode.DropOldest`. SDK Rule: all collections must be bounded.
**Warning signs:** Memory pressure after extended operation.

### Pitfall 6: Type Ambiguity Between SDK Namespaces
**What goes wrong:** `SyncResult`, `ConflictResolutionResult` collide between `SDK.Contracts.Distributed` and `SDK.Replication` namespaces.
**Why it happens:** Both namespaces define similar types for different contract generations.
**How to avoid:** Use `using` aliases as established in Phase 26. The `InMemoryReplicationSync` already demonstrates this pattern:
```csharp
using SyncResult = DataWarehouse.SDK.Contracts.Distributed.SyncResult;
using ConflictResolutionResult = DataWarehouse.SDK.Contracts.Distributed.ConflictResolutionResult;
```
**Warning signs:** CS0104 ambiguous reference errors during build.

### Pitfall 7: Not Respecting CancellationToken in Long-Running Loops
**What goes wrong:** SWIM probe loop or Raft heartbeat loop cannot be stopped, causing graceful shutdown to hang.
**Why it happens:** Forgetting to pass `ct` to inner async calls and loop conditions.
**How to avoid:** All background loops must check `ct.IsCancellationRequested` AND pass `ct` to awaited calls. Use `PeriodicTimer` which natively supports cancellation.
**Warning signs:** Process hangs on shutdown, tests timeout.

## Implementation Complexity Assessment

| Algorithm | Lines (est.) | Complexity | Key Challenge |
|-----------|-------------|------------|---------------|
| SWIM Membership | 300-400 | HIGH | Correct suspicion mechanism with incarnation numbers |
| P2P Gossip Replication | 200-300 | MEDIUM | Bounded epidemic propagation, generation tracking |
| Raft Leader Election | 400-500 | HIGH | Correct term/vote handling, election timeout randomization |
| CRDT Replication Sync | 300-400 | HIGH | Correct merge semantics for 4 CRDT types |
| Consistent Hash Ring | 150-200 | MEDIUM | Virtual node management, ring rebalancing on membership change |
| Consistent Hash Load Balancer | 100-150 | LOW | Wraps ConsistentHashRing with ILoadBalancerStrategy |
| Resource-Aware Load Balancer | 150-200 | MEDIUM | Health report aggregation, weighted selection |

**Total estimated new code:** 1,600-2,150 lines across ~10-12 files.

## Code Examples

### SWIM Probe Cycle (core algorithm)

```csharp
// Source: SWIM paper (Das et al., 2002) adapted to SDK contracts
private async Task RunProbeLoopAsync(CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.ProtocolPeriodMs));
    while (await timer.WaitForNextTickAsync(ct))
    {
        var target = SelectRandomMember();
        if (target == null) continue;

        var acked = await TryDirectPingAsync(target, ct);
        if (!acked)
        {
            // Indirect probing via k random members
            var probers = SelectRandomMembers(_config.IndirectPingCount, exclude: target);
            acked = await TryIndirectPingAsync(target, probers, ct);
        }

        if (!acked)
        {
            MarkSuspected(target);
        }
    }
}
```

### Raft Vote Request Handling

```csharp
// Source: Raft paper (Ongaro & Ousterhout, 2014) adapted to SDK types
internal RequestVoteResponse HandleVoteRequest(RequestVoteMessage request)
{
    // Rule: If request term > currentTerm, update and step down
    if (request.Term > _state.CurrentTerm)
    {
        _state.CurrentTerm = request.Term;
        _state.VotedFor = null;
        TransitionToFollower();
    }

    // Grant vote if: same or newer term, haven't voted yet (or voted for this candidate),
    // and candidate's log is at least as up-to-date
    bool grantVote = request.Term >= _state.CurrentTerm
        && (_state.VotedFor == null || _state.VotedFor == request.CandidateId)
        && IsLogUpToDate(request.LastLogIndex, request.LastLogTerm);

    if (grantVote)
    {
        _state.VotedFor = request.CandidateId;
        ResetElectionTimer();
    }

    return new RequestVoteResponse
    {
        Term = _state.CurrentTerm,
        VoteGranted = grantVote,
        Reason = grantVote ? "Vote granted" : "Vote denied"
    };
}
```

### CRDT GCounter Merge (SDK-level)

```csharp
// Source: Shapiro et al., "A comprehensive study of CRDTs" (INRIA, 2011)
public sealed class SdkGCounter : ICrdtType
{
    private readonly ConcurrentDictionary<string, long> _counts = new();

    public long Value => _counts.Values.Sum();

    public void Increment(string nodeId, long amount = 1)
    {
        _counts.AddOrUpdate(nodeId, amount, (_, v) => v + amount);
    }

    public SdkGCounter Merge(SdkGCounter other)
    {
        var result = new SdkGCounter();
        foreach (var (key, value) in _counts)
            result._counts[key] = value;
        foreach (var (key, value) in other._counts)
            result._counts.AddOrUpdate(key, value, (_, existing) => Math.Max(existing, value));
        return result;
    }

    public byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(_counts.ToDictionary(kv => kv.Key, kv => kv.Value));
    }
}
```

### Consistent Hash Ring

```csharp
// Source: Karger et al., "Consistent Hashing and Random Trees" (1997), adapted from existing ConsistentHashShardingStrategy
public sealed class ConsistentHashRing : IConsistentHashRing
{
    private readonly SortedDictionary<uint, string> _ring = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private uint[]? _sortedKeys;

    public int VirtualNodeCount { get; }

    public ConsistentHashRing(int virtualNodeCount = 150)
    {
        VirtualNodeCount = virtualNodeCount;
    }

    public string GetNode(string key)
    {
        _lock.EnterReadLock();
        try
        {
            if (_ring.Count == 0)
                throw new InvalidOperationException("Hash ring is empty.");

            var hash = ComputeHash(key);
            var keys = _sortedKeys ?? _ring.Keys.ToArray();
            var idx = Array.BinarySearch(keys, hash);
            if (idx < 0) idx = ~idx;
            if (idx >= keys.Length) idx = 0;
            return _ring[keys[idx]];
        }
        finally { _lock.ExitReadLock(); }
    }

    private static uint ComputeHash(string key)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        return System.IO.Hashing.XxHash32.HashToUInt32(bytes);
    }
}
```

## Plan Structure Recommendation

### Plan 29-01: SWIM Gossip Cluster Membership and P2P Gossip Replication
**Requirements:** DIST-12, DIST-15
**Scope:**
- `SwimClusterMembership` implementing `IClusterMembership`
- `GossipReplicator` implementing `IGossipProtocol` for P2P data replication
- SWIM protocol state machine (probe, suspect, dead lifecycle)
- Gossip message dissemination piggybacked on SWIM probes
- Bounded gossip message queues using `Channel<T>`
**Dependencies:** Phase 26 contracts (available), `IP2PNetwork` (used for transport)
**Files:** 3-4 new files in `SDK/Infrastructure/Distributed/Membership/`
**Risk:** HIGH -- SWIM correctness requires careful incarnation number handling
**Verification:** Unit test with 3-5 simulated nodes, verify join/leave/failure detection

### Plan 29-02: Raft Consensus for Leader Election
**Requirements:** DIST-13
**Scope:**
- `RaftConsensusEngine` integrating with `IClusterMembership` for leader election
- Term management, vote handling, election timer with randomized timeout
- Leader heartbeat loop via `IP2PNetwork`
- Log replication for state machine (AppendEntries)
- Integration point: `IClusterMembership.GetLeader()` returns Raft-elected leader
**Dependencies:** Plan 29-01 (needs SWIM membership for node discovery)
**Files:** 3-4 new files in `SDK/Infrastructure/Distributed/Consensus/`
**Risk:** HIGH -- Raft correctness is subtle (split brain, stale reads)
**Verification:** Unit test with leader failure and re-election scenario

### Plan 29-03: Multi-Master Replication with CRDT Conflict Resolution
**Requirements:** DIST-14
**Scope:**
- `CrdtReplicationSync` implementing `IReplicationSync` with CRDT merge
- SDK-level CRDT types: `SdkGCounter`, `SdkPNCounter`, `SdkLWWRegister`, `SdkORSet`
- CRDT registry mapping data keys to CRDT types
- Vector clock integration for causality tracking (reuse existing `VectorClock`)
- Uses `IGossipProtocol` for epidemic state propagation
**Dependencies:** Plan 29-01 (needs gossip protocol for propagation)
**Files:** 4-5 new files in `SDK/Infrastructure/Distributed/Replication/`
**Risk:** HIGH -- CRDT merge correctness, serialization round-trip fidelity
**Verification:** Unit test with concurrent writes from 3 nodes, verify deterministic convergence

### Plan 29-04: Consistent Hashing and Resource-Aware Load Balancing
**Requirements:** DIST-16, DIST-17
**Scope:**
- `ConsistentHashRing` implementing `IConsistentHashRing`
- `ConsistentHashLoadBalancer` implementing `ILoadBalancerStrategy` (AlgorithmName = "ConsistentHash")
- `ResourceAwareLoadBalancer` implementing `ILoadBalancerStrategy` (AlgorithmName = "ResourceAware")
- Virtual node management for uniform distribution
- Health-based node exclusion and weighted selection
**Dependencies:** Phase 26 contracts only (independent of SWIM/Raft)
**Files:** 3 new files in `SDK/Infrastructure/Distributed/LoadBalancing/`
**Risk:** MEDIUM -- straightforward algorithms, well-understood patterns
**Verification:** Unit test hash distribution uniformity, verify health-based exclusion

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `InMemoryClusterMembership` (single-node stub) | `SwimClusterMembership` (SWIM gossip) | Phase 29 | Real multi-node membership |
| `InMemoryLoadBalancerStrategy` (always first) | `ConsistentHashLoadBalancer` + `ResourceAwareLoadBalancer` | Phase 29 | Cache-friendly distribution + health-aware routing |
| `InMemoryP2PNetwork` (no peers) | Used as transport by SWIM + Raft + Gossip replicator | Phase 29 | Real P2P communication |
| `InMemoryReplicationSync` (local-only) | `CrdtReplicationSync` (CRDT merge) | Phase 29 | Real conflict-free distributed writes |
| No `IConsistentHashRing` impl | `ConsistentHashRing` (virtual nodes) | Phase 29 | Real hash ring for routing |
| `GeoRaftState` + `GeoRaftNode` (v1.0 types) | Clean Phase 29 Raft implementation | Phase 29 | Production Raft separate from legacy types |

**Note:** All Phase 26 in-memory implementations remain available as the default for single-node deployments. Phase 29 implementations are alternatives activated in cluster mode.

## Open Questions

1. **IP2PNetwork transport implementation**
   - What we know: Phase 29 algorithms need `IP2PNetwork` for inter-node communication. The existing `InMemoryP2PNetwork` throws on SendToPeer.
   - What's unclear: Do we need a real TCP/gRPC transport in Phase 29, or do the algorithms work against the `IP2PNetwork` abstraction?
   - Recommendation: Algorithms implement against the abstraction only. A real transport (`TcpP2PNetwork` or similar) is a separate concern. For Phase 29 testing, create a `SimulatedP2PNetwork` that routes between in-process instances (NOT a mock -- it actually delivers messages between node objects in the same process).

2. **Raft integration with IClusterMembership**
   - What we know: SWIM provides membership, Raft provides leader election. Both affect `IClusterMembership`.
   - What's unclear: Should we have one `IClusterMembership` implementation combining both, or compose them?
   - Recommendation: `SwimClusterMembership` is the primary `IClusterMembership` implementation. Raft runs as an internal component that feeds leader information back into the membership. SWIM owns the member list; Raft owns the leader election. `SwimClusterMembership.GetLeader()` delegates to the Raft engine.

3. **CRDT type selection per data key**
   - What we know: Different data needs different CRDT types (counters vs registers vs sets).
   - What's unclear: How does the caller specify which CRDT type to use for a given key?
   - Recommendation: `CrdtReplicationSync` accepts a `CrdtTypeRegistry` in its constructor that maps key patterns to CRDT types. Default is `LWWRegister` (most general). This follows the existing `MultiMasterReplicationPluginBase.CreateCrdtResolver<T>` pattern.

4. **Relationship to existing IConsensusEngine**
   - What we know: `IConsensusEngine` exists in `SDK/Contracts/IConsensusEngine.cs` with `ProposeAsync`, `OnCommit`, `IsLeader`.
   - What's unclear: Should the new Raft implementation also implement `IConsensusEngine`?
   - Recommendation: YES -- the Raft engine should implement `IConsensusEngine` in addition to being used internally by SWIM membership. This preserves backward compatibility and allows plugins that already use `IConsensusEngine` to benefit from the new implementation.

## Sources

### Primary (HIGH confidence)
- SDK source: `DataWarehouse.SDK/Contracts/Distributed/IClusterMembership.cs` -- full contract with ClusterNode, ClusterNodeRole, ClusterNodeStatus
- SDK source: `DataWarehouse.SDK/Contracts/Distributed/ILoadBalancerStrategy.cs` -- full contract with LoadBalancerContext, NodeHealthReport
- SDK source: `DataWarehouse.SDK/Contracts/Distributed/IP2PNetwork.cs` -- full contract with IGossipProtocol, PeerInfo, GossipMessage
- SDK source: `DataWarehouse.SDK/Contracts/Distributed/IReplicationSync.cs` -- full contract with SyncConflict, ConflictResolutionResult, ConflictResolutionStrategy
- SDK source: `DataWarehouse.SDK/Contracts/Distributed/IFederatedMessageBus.cs` -- IConsistentHashRing, FederatedMessageBusBase
- SDK source: `DataWarehouse.SDK/Replication/IMultiMasterReplication.cs` -- VectorClock, ConsistencyLevel, ConflictResolution
- SDK source: `DataWarehouse.SDK/Contracts/IConsensusEngine.cs` -- IConsensusEngine, GeoRaftState, RequestVoteMessage
- SDK source: 13 InMemory implementations in `SDK/Infrastructure/InMemory/`
- Plugin source: `UltimateReplication/Strategies/Core/CoreReplicationStrategies.cs` -- GCounterCrdt, PNCounterCrdt, ORSetCrdt, LWWRegisterCrdt
- Plugin source: `UltimateDataManagement/Strategies/Sharding/ConsistentHashShardingStrategy.cs` -- consistent hash ring reference implementation

### Secondary (MEDIUM confidence)
- [SWIM paper (Das et al., 2002)](https://www.cs.cornell.edu/projects/Quicksilver/public_pdfs/SWIM.pdf) -- original SWIM protocol specification
- [Raft Consensus Algorithm](https://raft.github.io/) -- official Raft specification and visualization
- [DotNext Raft for .NET](https://dotnet.github.io/dotNext/features/cluster/raft.html) -- .NET Raft reference implementation
- [Raft.NET](https://github.com/hhblaze/Raft.Net) -- TCP-based .NET Raft implementation
- [SWIM Protocol explained](https://www.brianstorti.com/swim/) -- clear SWIM protocol walkthrough

### Tertiary (LOW confidence)
- Line count and complexity estimates are based on similar implementations and experience -- actual counts may vary by 30-50%

## Metadata

**Confidence breakdown:**
- Contract understanding: HIGH -- direct source code inspection of all 9 SDK contract files and 13 in-memory implementations
- Algorithm specifications: HIGH -- SWIM and Raft are well-documented, mature algorithms with decades of production use
- Existing primitives inventory: HIGH -- grep + direct read of all relevant SDK and plugin files
- Plan structure: HIGH -- follows ROADMAP-defined 4-plan structure, aligned with dependency graph
- Complexity estimates: MEDIUM -- based on algorithm specifications and reference implementations, not actual coding

**Research date:** 2026-02-14
**Valid until:** 2026-03-14 (stable -- all target contracts are finalized in Phase 26, no moving targets)
