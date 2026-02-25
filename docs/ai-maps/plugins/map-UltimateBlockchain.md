# Plugin: UltimateBlockchain
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateBlockchain

### File: Plugins/DataWarehouse.Plugins.UltimateBlockchain/UltimateBlockchainPlugin.cs
```csharp
public class UltimateBlockchainPlugin : BlockchainProviderPluginBase
{
}
    public UltimateBlockchainPlugin(ILogger<UltimateBlockchainPlugin> logger);
    public BlockchainScalingManager ScalingManager;;
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override SDK.Primitives.PluginCategory Category;;
    public override async Task<BatchAnchorResult> AnchorBatchAsync(IEnumerable<AnchorRequest> requests, CancellationToken ct = default);
    public override Task<AnchorVerificationResult> VerifyAnchorAsync(Guid objectId, IntegrityHash expectedHash, CancellationToken ct = default);
    public override Task<AuditChain> GetAuditChainAsync(Guid objectId, CancellationToken ct = default);
    public override Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default);
    protected override Task<BlockInfo?> GetBlockByNumberAsync(long blockNumber, CancellationToken ct = default);
    public override async Task OnMessageAsync(PluginMessage message);
}
```
```csharp
private class BlockJournalEntry
{
}
    public long BlockNumber { get; set; }
    public string PreviousHash { get; set; };
    public DateTimeOffset Timestamp { get; set; }
    public List<BlockchainAnchor> Transactions { get; set; };
    public string MerkleRoot { get; set; };
    public string Hash { get; set; };
}
```
```csharp
private class InternalBlock
{
}
    public required long BlockNumber { get; init; }
    public required string PreviousHash { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required List<BlockchainAnchor> Transactions { get; set; }
    public required string MerkleRoot { get; init; }
    public required string Hash { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateBlockchain/Scaling/BlockchainScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-04: Blockchain scaling manager with IScalableSubsystem")]
public sealed class BlockchainScalingManager : IScalableSubsystem, IDisposable
{
}
    public BlockchainScalingManager(ILogger logger, SegmentedBlockStore blockStore, ScalingLimits? limits = null);
    public SegmentedBlockStore BlockStore;;
    public BoundedCache<string, byte[]> ManifestCache;;
    public BoundedCache<string, bool> ValidationCache;;
    public async Task AppendBlockAsync(BlockData block, CancellationToken ct);
    public byte[]? GetManifest(string key);
    public void PutManifest(string key, byte[] data);
    public bool TryGetValidation(string blockHash, out bool isValid);
    public void PutValidation(string blockHash, bool isValid);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits;;
    public BackpressureState CurrentBackpressureState
{
    get
    {
        long pending = Interlocked.Read(ref _pendingAppends);
        int maxQueue = _currentLimits.MaxQueueDepth;
        if (pending <= 0)
            return BackpressureState.Normal;
        if (pending < maxQueue * 0.5)
            return BackpressureState.Normal;
        if (pending < maxQueue * 0.8)
            return BackpressureState.Warning;
        if (pending < maxQueue)
            return BackpressureState.Critical;
        return BackpressureState.Shedding;
    }
}
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateBlockchain/Scaling/SegmentedBlockStore.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-04: Segmented block store replacing List<Block>")]
public sealed class SegmentedBlockStore : IDisposable
{
}
    public const long DefaultSegmentSizeBytes = 64L * 1024 * 1024;
    public const long DefaultBlocksPerJournalShard = 10_000;
    public const int DefaultMaxCacheEntries = 100_000;
    public SegmentedBlockStore(ILogger logger, string dataDirectory, long segmentSizeBytes = DefaultSegmentSizeBytes, long blocksPerJournalShard = DefaultBlocksPerJournalShard, int maxHotSegments = 4, int maxCacheEntries = DefaultMaxCacheEntries);
    public long BlockCount;;
    public long SegmentCount;;
    public long JournalShardCount;;
    public CacheStatistics CacheStatistics;;
    public async Task AppendBlockAsync(BlockData block, CancellationToken ct);
    public async Task<BlockData?> GetBlockAsync(long blockNumber, CancellationToken ct);
    public async Task<IReadOnlyList<BlockData>> GetBlockRangeAsync(long start, long count, CancellationToken ct);
    public async Task CompactAsync(CancellationToken ct);
    public (long SegmentNumber, long Offset) GetBlockIndex(long blockNumber);
    public IReadOnlyDictionary<string, int> GetTierLockContention();
    public void Dispose();
}
```
