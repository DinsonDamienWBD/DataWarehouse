# Plugin: UltimateConsensus
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateConsensus

### File: Plugins/DataWarehouse.Plugins.UltimateConsensus/IRaftStrategy.cs
```csharp
public interface IRaftStrategy
{
}
    string AlgorithmName { get; }
    Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct);;
}
```
```csharp
public sealed class RaftStrategy : IRaftStrategy
{
}
    public string AlgorithmName;;
    public RaftStrategy(UltimateConsensusPlugin plugin);
    public async Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct);
}
```
```csharp
public sealed class PaxosStrategy : IRaftStrategy
{
}
    public PaxosStrategy(int acceptorCount = 3);
    public string AlgorithmName;;
    public bool IsStableLeader;;
    public long TotalCommittedSlots;;
    public Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct);
    public bool Reconfigure(int groupId, int newAcceptorCount);
    public IReadOnlyDictionary<long, PaxosLogEntry>? GetCommittedLog(int groupId);
    public sealed class PaxosLogEntry;
}
```
```csharp
public sealed class PaxosLogEntry
{
}
    public long Slot { get; init; }
    public long ProposalNumber { get; init; }
    public byte[] Value { get; init; };
    public bool IsNoOp { get; init; }
    public DateTimeOffset CommittedAt { get; init; }
}
```
```csharp
private sealed class AcceptorState
{
}
    public long HighestPromised { get; set; }
    public long AcceptedProposal { get; set; }
    public byte[]? AcceptedValue { get; set; }
}
```
```csharp
private sealed class PaxosGroupState
{
}
    public long NextSlot { get; set; }
    public int ConfiguredAcceptorCount { get; set; };
    public BoundedDictionary<long, PaxosLogEntry> CommittedLog { get; };
    public AcceptorState GetAcceptor(int id);;
}
```
```csharp
public sealed class PbftStrategy : IRaftStrategy
{
}
    public int CheckpointInterval { get; set; };
    public PbftStrategy(int faultTolerance = 1);
    public string AlgorithmName;;
    public int ViewNumber;;
    public int GetPrimary(int view);;
    public int FaultTolerance;;
    public int TotalNodes;;
    public Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct);
    public int RequestViewChange(int groupId);
    public IReadOnlyList<PbftCheckpoint> GetCheckpoints(int groupId);
    public enum PbftMessageType;
    public sealed class PbftMessage;
    public sealed class PbftCommittedEntry;
    public sealed class PbftCheckpoint;
    public sealed class PbftViewChange;
}
```
```csharp
public sealed class PbftMessage
{
}
    public PbftMessageType Type { get; init; }
    public int View { get; init; }
    public int Sequence { get; init; }
    public byte[] Digest { get; init; };
    public byte[] Hmac { get; init; };
    public int SenderId { get; init; }
    public byte[]? Data { get; init; }
}
```
```csharp
public sealed class PbftCommittedEntry
{
}
    public int Sequence { get; init; }
    public int View { get; init; }
    public byte[] Data { get; init; };
    public byte[] Digest { get; init; };
    public DateTimeOffset CommittedAt { get; init; }
}
```
```csharp
public sealed class PbftCheckpoint
{
}
    public int Sequence { get; init; }
    public byte[] StateDigest { get; init; };
    public DateTimeOffset CreatedAt { get; init; }
    public int ProofCount { get; init; }
}
```
```csharp
public sealed class PbftViewChange
{
}
    public int OldView { get; init; }
    public int NewView { get; init; }
    public int NewPrimary { get; init; }
    public string Reason { get; init; };
    public DateTimeOffset ChangedAt { get; init; }
}
```
```csharp
private sealed class PbftGroupState
{
}
    public int LowWatermark { get; set; }
    public int HighWatermark { get; set; }
    public long LastApplied { get; set; }
    public HashSet<int> PreparedSet { get; };
    public BoundedDictionary<int, PbftMessage> PrePrepareLog { get; };
    public BoundedDictionary<int, ConcurrentBag<int>> PrepareMessages { get; };
    public BoundedDictionary<int, ConcurrentBag<int>> CommitMessages { get; };
    public BoundedDictionary<int, PbftCommittedEntry> CommittedEntries { get; };
    public List<PbftCheckpoint> Checkpoints { get; };
    public List<PbftViewChange> ViewChangeHistory { get; };
    public PbftGroupState(int totalNodes);
}
```
```csharp
public sealed class ZabStrategy : IRaftStrategy
{
}
    public ZabStrategy(int followerCount = 2);
    public string AlgorithmName;;
    public ZabPhase CurrentPhase;;
    public long CurrentEpoch;;
    public bool IsLeader;;
    public Task<ConsensusResult> ProposeAsync(byte[] data, int groupId, CancellationToken ct);
    public long ForceLeaderRecovery(int groupId);
    public IReadOnlyList<ZabTransaction> GetCommittedLog(int groupId);
    public enum ZabPhase;
    public enum ZabSyncMode;
    public sealed class ZabTransaction;
}
```
```csharp
public sealed class ZabTransaction
{
}
    public long Zxid { get; init; }
    public byte[]? Data { get; init; }
    public long Epoch { get; init; }
    public long Counter { get; init; }
    public DateTimeOffset ProposedAt { get; init; }
    public DateTimeOffset? CommittedAt { get; set; }
}
```
```csharp
private sealed class FollowerState
{
}
    public long LastAckedZxid { get; set; }
    public long LastCommittedZxid { get; set; }
    public ZabSyncMode LastSyncMode { get; set; }
}
```
```csharp
private sealed class ZabGroupState
{
}
    public long LastCommittedZxid { get; set; }
    public List<ZabTransaction> CommittedLog { get; };
    public FollowerState GetFollower(int id);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConsensus/UltimateConsensusPlugin.cs
```csharp
public sealed class UltimateConsensusPlugin : ConsensusPluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override bool IsLeader
{
    get
    {
        if (_multiRaft == null)
            return false;
        // Leader in ANY group counts
        return _multiRaft.GetGroupStatuses().Values.Any(s => s.IsLeader);
    }
}
    public int GroupCount;;
    public UltimateConsensusPlugin(int groupCount = 3, int votersPerGroup = 3);
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    public override async Task<bool> ProposeAsync(Proposal proposal, CancellationToken cancellationToken = default);
    public override IDisposable OnCommit(Action<Proposal> handler);
    public override Task<ClusterState> GetClusterStateAsync();
    public override async Task<ConsensusResult> ProposeAsync(byte[] data, CancellationToken ct);
    public async Task<ConsensusResult> ProposeToGroupAsync(byte[] data, int groupId, CancellationToken ct);
    public override Task<bool> IsLeaderAsync(CancellationToken ct);
    public override Task<ConsensusState> GetStateAsync(CancellationToken ct);
    public override Task<ClusterHealthInfo> GetClusterHealthAsync(CancellationToken ct);
    public IRaftStrategy? CreateStrategy(string strategyName);
    public bool SetActiveStrategy(string strategyName);
    public override async Task OnMessageAsync(PluginMessage message);
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnBeforeStatePersistAsync(CancellationToken ct);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    protected override void Dispose(bool disposing);
}
```
```csharp
private sealed class CommitHandlerRegistration : IDisposable
{
}
    public CommitHandlerRegistration(List<Action<Proposal>> handlers, object handlerLock, Action<Proposal> handler);
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConsensus/ConsistentHash.cs
```csharp
public sealed class ConsistentHash
{
}
    public ConsistentHash(int initialBuckets);
    public static int GetBucket(string key, int numBuckets);
    public int Route(string key);
    public int Route(byte[] data);
    public void AddNode(int nodeId);
    public void RemoveNode(int nodeId);
    public int BucketCount
{
    get
    {
        lock (_lock)
        {
            return _bucketCount;
        }
    }
}
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConsensus/Scaling/SegmentedRaftLog.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-05: Segmented Raft log store with mmap'd hot segments")]
public sealed class SegmentedRaftLog : IRaftLogStore, IDisposable
{
#endregion
}
    public int EntriesPerSegment { get; }
    public int HotSegmentCount { get; }
    public SegmentedRaftLog(string dataDir, string groupId, int entriesPerSegment = 10_000, int hotSegmentCount = 3);
    public async Task InitializeAsync();
    public long Count
{
    get
    {
        EnsureInitialized();
        return _entries.Count;
    }
}
    public Task<long> GetLastIndexAsync();
    public Task<long> GetLastTermAsync();
    public Task<RaftLogEntry?> GetAsync(long index);
    public Task<IReadOnlyList<RaftLogEntry>> GetRangeAsync(long fromIndex, long toIndex);
    public Task<IReadOnlyList<RaftLogEntry>> GetFromAsync(long fromIndex);
    public async Task AppendAsync(RaftLogEntry entry);
    public async Task TruncateFromAsync(long fromIndex);
    public Task<(long term, string? votedFor)> GetPersistentStateAsync();
    public async Task SavePersistentStateAsync(long term, string? votedFor);
    public async Task CompactAsync(long upToIndex);
    public int SegmentCount
{
    get
    {
        EnsureInitialized();
        return _segments.Count;
    }
}
    public int ActiveHotSegments;;
    public void Dispose();
}
```
```csharp
private sealed class MappedSegment : IDisposable
{
}
    public MemoryMappedFile File { get; }
    public MemoryMappedViewAccessor Accessor { get; }
    public long Length { get; }
    public MappedSegment(MemoryMappedFile file, MemoryMappedViewAccessor accessor, long length);
    public void Dispose();
}
```
```csharp
private sealed class PersistentStateDto
{
}
    public long Term { get; set; }
    public string VotedFor { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateConsensus/Scaling/ConsensusScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-05: Consensus scaling manager with multi-Raft, connection pooling, adaptive timeouts")]
public sealed class ConsensusScalingManager : IScalableSubsystem, IDisposable
{
#endregion
}
    public int MaxNodesPerGroup;;
    public int MaxConnectionsPerPeer;;
    public int MinElectionTimeoutMs;;
    public int ElectionTimeoutMultiplier;;
    public TimeSpan IdleConnectionTimeout;;
    public int ActiveGroupCount;;
    public ConsensusScalingManager(int maxNodesPerGroup = 100, int maxConnectionsPerPeer = 4, int minElectionTimeoutMs = 150, int electionTimeoutMultiplier = 10, int idleConnectionTimeoutMinutes = 5);
    public bool RegisterGroup(string groupId, int nodeCount);
    public bool UpdateGroupNodeCount(string groupId, int nodeCount);
    public void UpdateGroupLogSize(string groupId, long logSize);
    public void RecordElection(string groupId);
    public bool RemoveGroup(string groupId);
    public string? RouteKey(string key);
    public ConnectionPool GetOrCreatePool(string peerId);
    public int ActivePoolCount;;
    public void RecordRtt(string peerId, double rttMs);
    public int ComputeElectionTimeout();
    public double GetP50Rtt();
    public double GetP99Rtt();
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits;;
    public BackpressureState CurrentBackpressureState
{
    get
    {
        // Compute backpressure from group utilization
        int totalNodes = _groups.Values.Sum(g => g.NodeCount);
        int maxCapacity = _groups.Count * _maxNodesPerGroup;
        if (maxCapacity == 0)
            return BackpressureState.Normal;
        double utilization = (double)totalNodes / maxCapacity;
        return utilization switch
        {
            >= 0.95 => BackpressureState.Critical,
            >= 0.80 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }
}
    public void Dispose();
    [SdkCompatibility("6.0.0", Notes = "Phase 88-05: RTT sliding window tracker for adaptive timeouts")];
    [SdkCompatibility("6.0.0", Notes = "Phase 88-05: Per-peer connection pool for Raft RPC")];
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-05: RTT sliding window tracker for adaptive timeouts")]
public sealed class RttTracker
{
}
    public RttTracker(int windowSize);
    public void Record(double rttMs);
    public double GetP50();
    public double GetP99();
    public double GetPercentile(double percentile);
    public int SampleCount
{
    get
    {
        lock (_lock)
        {
            return _count;
        }
    }
}
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-05: Per-peer connection pool for Raft RPC")]
public sealed class ConnectionPool
{
}
    public string PeerId;;
    public int MaxConnections { get; }
    public TimeSpan IdleTimeout { get; }
    public int ActiveConnections;;
    public int AvailableSlots;;
    public DateTime CreatedAt;;
    public ConnectionPool(string peerId, int maxConnections, TimeSpan idleTimeout);
    public bool TryAcquire();
    public void Release();
    public double Utilization;;
}
```
