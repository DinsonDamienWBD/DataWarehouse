# Plugin: UltimateDataTransit
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDataTransit

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/TransitMessageTopics.cs
```csharp
internal static class TransitMessageTopics
{
}
    public const string TransferStarted = "transit.transfer.started";
    public const string TransferProgress = "transit.transfer.progress";
    public const string TransferCompleted = "transit.transfer.completed";
    public const string TransferFailed = "transit.transfer.failed";
    public const string TransferResumed = "transit.transfer.resumed";
    public const string TransferCancelled = "transit.transfer.cancelled";
    public const string StrategyRegistered = "transit.strategy.registered";
    public const string StrategyHealth = "transit.strategy.health";
    public const string QoSChanged = "transit.qos.changed";
    public const string CostRouteChanged = "transit.cost.route.changed";
    public const string AuditEntry = "transit.audit.entry";
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/UltimateDataTransitPlugin.cs
```csharp
public sealed class UltimateDataTransitPlugin : DataTransitPluginBase, ITransitOrchestrator, IDisposable
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public UltimateDataTransitPlugin();
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override Task OnStopCoreAsync();
    public async Task<IDataTransitStrategy> SelectStrategyAsync(TransitRequest request, CancellationToken ct = default);
    public async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    public IReadOnlyCollection<IDataTransitStrategy> GetRegisteredStrategies();
    public async Task<IReadOnlyCollection<TransitHealthStatus>> GetHealthAsync(CancellationToken ct = default);
    protected override void Dispose(bool disposing);
    public override Task<Dictionary<string, object>> TransferAsync(string key, Dictionary<string, object> target, CancellationToken ct = default);
    public override Task<Dictionary<string, object>> GetTransferStatusAsync(string transferId, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Audit/TransitAuditService.cs
```csharp
internal sealed class TransitAuditService
{
}
    public TransitAuditService(IMessageBus messageBus);
    public void LogEvent(TransitAuditEntry entry);
    public IReadOnlyList<TransitAuditEntry> GetAuditTrail(string transferId);
    public IReadOnlyList<TransitAuditEntry> GetRecentEntries(int count);
    public long GetTotalEntries();
    public void PurgeOlderThan(DateTime cutoff);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Layers/CompressionInTransitLayer.cs
```csharp
internal sealed class CompressionInTransitLayer : IDataTransitStrategy
{
}
    public CompressionInTransitLayer(IDataTransitStrategy inner, IMessageBus messageBus);
    public string StrategyId;;
    public string Name;;
    public TransitCapabilities Capabilities;;
    public async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    public Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public Task<TransitResult> ResumeTransferAsync(string transferId, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    public Task CancelTransferAsync(string transferId, CancellationToken ct = default);
    public Task<TransitHealthStatus> GetHealthAsync(CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Layers/EncryptionInTransitLayer.cs
```csharp
internal sealed class EncryptionInTransitLayer : IDataTransitStrategy
{
}
    public EncryptionInTransitLayer(IDataTransitStrategy inner, IMessageBus messageBus);
    public string StrategyId;;
    public string Name;;
    public TransitCapabilities Capabilities;;
    public async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    public Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public Task<TransitResult> ResumeTransferAsync(string transferId, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    public Task CancelTransferAsync(string transferId, CancellationToken ct = default);
    public Task<TransitHealthStatus> GetHealthAsync(CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/QoS/CostAwareRouter.cs
```csharp
internal sealed record TransitRoute
{
}
    public string RouteId { get; init; };
    public string StrategyId { get; init; };
    public TransitEndpoint? Endpoint { get; init; }
    public TransitCostProfile CostProfile { get; init; };
    public double EstimatedLatencyMs { get; init; }
    public double EstimatedThroughputBytesPerSec { get; init; }
}
```
```csharp
internal sealed class CostAwareRouter
{
}
    public CostAwareRouter(RoutingPolicy defaultPolicy = RoutingPolicy.Balanced);
    public void RegisterCostProfile(string strategyId, TransitCostProfile profile);
    public TransitCostProfile? GetCostProfile(string strategyId);
    public TransitRoute SelectRoute(IReadOnlyList<TransitRoute> candidates, TransitRequest request, RoutingPolicy? overridePolicy = null);
    public decimal EstimateTransferCost(string strategyId, long sizeBytes);
    public IReadOnlyList<(TransitRoute Route, decimal Cost)> RankRoutesByCost(IReadOnlyList<TransitRoute> routes, long sizeBytes);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/QoS/QoSThrottlingManager.cs
```csharp
internal sealed record QoSConfiguration
{
}
    public long TotalBandwidthBytesPerSecond { get; init; }
    public Dictionary<TransitPriority, PriorityConfig> PriorityConfigs { get; init; };
}
```
```csharp
internal sealed record PriorityConfig
{
}
    public double WeightPercent { get; init; }
    public long MinBandwidthBytesPerSecond { get; init; }
    public long MaxBandwidthBytesPerSecond { get; init; }
}
```
```csharp
internal sealed class QoSThrottlingManager : IDisposable
{
}
    public QoSThrottlingManager(QoSConfiguration configuration);
    public Task<Stream> CreateThrottledStreamAsync(Stream inner, TransitPriority priority, CancellationToken ct = default);
    public void UpdateConfiguration(QoSConfiguration config);
    public QoSConfiguration Configuration
{
    get
    {
        lock (_configLock)
        {
            return _configuration;
        }
    }
}
    public long GetAvailableTokens(TransitPriority priority);
    public void Dispose();
}
```
```csharp
internal sealed class TokenBucket : IDisposable
{
}
    public TokenBucket(long maxTokens, long tokensPerSecond);
    public long AvailableTokens;;
    public async Task<bool> TryConsumeAsync(long tokens, CancellationToken ct);
    public async Task ConsumeAsync(long tokens, CancellationToken ct);
    public void Dispose();
}
```
```csharp
internal sealed class ThrottledStream : Stream
{
}
    public ThrottledStream(Stream inner, TokenBucket bucket);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override bool CanTimeout;;
    public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
    public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
    public override void Flush();;
    public override Task FlushAsync(CancellationToken cancellationToken);;
    public override int Read(byte[] buffer, int offset, int count);
    public override void Write(byte[] buffer, int offset, int count);
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
    public override async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Scaling/TransitScalingMigration.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-13: DataTransit plugin BoundedCache migration")]
public sealed class TransitScalingMigration : IDisposable
{
}
    public TransitScalingMigration(ScalingLimits? limits = null);
    public BoundedCache<string, byte[]> TransferState;;
    public BoundedCache<string, byte[]> RouteCache;;
    public BoundedCache<string, byte[]> QosState;;
    public BoundedCache<string, byte[]> CostRoutingTable;;
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Chunked/ChunkedResumableStrategy.cs
```csharp
internal sealed class ChunkedResumableStrategy : DataTransitStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public ChunkedResumableStrategy();
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    public override async Task<TransitResult> ResumeTransferAsync(string transferId, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    internal sealed class ChunkManifest;
    internal sealed class ChunkInfo;
}
```
```csharp
internal sealed class ChunkManifest
{
}
    public required string TransferId { get; init; }
    public required long TotalSize { get; init; }
    public required int ChunkSizeBytes { get; init; }
    public required List<ChunkInfo> Chunks { get; init; }
    public TransitRequest? Request { get; set; }
}
```
```csharp
internal sealed class ChunkInfo
{
}
    public required int Index { get; init; }
    public required long Offset { get; init; }
    public required int Size { get; init; }
    public string? Sha256Hash { get; set; }
    public bool Completed { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Chunked/DeltaDifferentialStrategy.cs
```csharp
internal sealed class DeltaDifferentialStrategy : DataTransitStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public DeltaDifferentialStrategy();
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    internal sealed class RollingHashComputer;
    internal sealed record BlockSignature(int Index, long Offset, int Size, uint WeakHash, byte[] StrongHash);;
}
```
```csharp
internal sealed class RollingHashComputer
{
}
    public uint ComputeAdler32(ReadOnlySpan<byte> data);
    public byte[] ComputeStrongHash(ReadOnlySpan<byte> data);
    public uint RollHash(uint currentHash, byte removedByte, byte addedByte, int blockSize);
}
```
```csharp
private sealed class BlockSignatureDto
{
}
    public int Index { get; set; }
    public long Offset { get; set; }
    public int Size { get; set; }
    public uint WeakHash { get; set; }
    public string StrongHash { get; set; };
}
```
```csharp
private sealed class DeltaInstruction
{
}
    public required DeltaInstructionType Type { get; init; }
    public int DestBlockIndex { get; init; }
    public required int SourceOffset { get; init; }
    public required int Length { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Direct/FtpTransitStrategy.cs
```csharp
internal sealed class FtpTransitStrategy : DataTransitStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
}
```
```csharp
private sealed class FtpHashingStream : Stream
{
}
    public FtpHashingStream(Stream inner, IncrementalHash hash, IProgress<TransitProgress>? progress, string transferId, long totalBytes, Action<long> updateBytesRead);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count);
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    public override void Flush();;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Direct/GrpcStreamingTransitStrategy.cs
```csharp
internal sealed class GrpcStreamingTransitStrategy : DataTransitStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public GrpcStreamingTransitStrategy();
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Direct/Http2TransitStrategy.cs
```csharp
internal sealed class Http2TransitStrategy : DataTransitStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public Http2TransitStrategy();
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
}
```
```csharp
private sealed class ByteTracker
{
}
    public long Value { get => Interlocked.Read(ref _value); set => Interlocked.Exchange(ref _value, value); }
}
```
```csharp
private sealed class HashingProgressStream : Stream
{
}
    public HashingProgressStream(Stream inner, IncrementalHash hash, IProgress<TransitProgress>? progress, string transferId, long totalBytes, ByteTracker tracker);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count);
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    public override void Flush();;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Direct/Http3TransitStrategy.cs
```csharp
internal sealed class Http3TransitStrategy : DataTransitStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public Http3TransitStrategy();
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Direct/ScpRsyncTransitStrategy.cs
```csharp
internal sealed class ScpRsyncTransitStrategy : DataTransitStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Direct/SftpTransitStrategy.cs
```csharp
internal sealed class SftpTransitStrategy : DataTransitStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Distributed/MultiPathParallelStrategy.cs
```csharp
internal sealed class MultiPathParallelStrategy : DataTransitStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public MultiPathParallelStrategy();
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    public override async Task<TransitResult> ResumeTransferAsync(string transferId, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    internal sealed class PathInfo;
    internal sealed class DataSegment;
    internal sealed class TransferState;
}
```
```csharp
internal sealed class PathInfo
{
}
    public required string PathId { get; init; }
    public required string EndpointUri { get; init; }
    public double LatencyMs { get; set; }
    public double ThroughputBytesPerSec { get; set; }
    public double ErrorRate { get; set; }
    public double Score { get; set; }
    public long BytesAssigned { get; set; }
    public long BytesCompleted { get; set; }
    public bool IsHealthy { get; set; }
    public int Attempts { get; set; }
    public int Failures { get; set; }
    public object Lock { get; init; };
}
```
```csharp
internal sealed class DataSegment
{
}
    public required int Index { get; init; }
    public required long Offset { get; init; }
    public required long Size { get; init; }
    public required string AssignedPathId { get; set; }
    public bool Completed { get; set; }
}
```
```csharp
internal sealed class TransferState
{
}
    public required string TransferId { get; init; }
    public required long TotalSize { get; init; }
    public required List<PathInfo> Paths { get; set; }
    public required List<DataSegment> Segments { get; init; }
    public long BytesTransferred;
    public TransitRequest? Request { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Distributed/P2PSwarmStrategy.cs
```csharp
internal sealed class P2PSwarmStrategy : DataTransitStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public P2PSwarmStrategy();
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    public override async Task<TransitResult> ResumeTransferAsync(string transferId, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
    internal sealed record SwarmPeer(string PeerId, string EndpointUri, bool[] PieceBitmap, int ActiveDownloads, long BytesDownloaded, DateTime LastSeen);;
    internal sealed record PieceInfo(int Index, long Offset, int Size, byte[] Sha256Hash, bool Downloaded);;
    internal sealed class SwarmState;
}
```
```csharp
internal sealed class SwarmState
{
}
    public required string TransferId { get; init; }
    public required long TotalSize { get; init; }
    public required int PieceSize { get; init; }
    public required PieceInfo[] Pieces { get; init; }
    public required BoundedDictionary<string, SwarmPeer> Peers { get; init; }
    public required Channel<int> PieceQueue { get; set; }
    public required int ConcurrentDownloadLimit { get; init; }
    public TransitRequest? Request { get; init; }
}
```
```csharp
private sealed class TrackerResponse
{
}
    public List<TrackerPeer>? Peers { get; set; }
}
```
```csharp
private sealed class TrackerPeer
{
}
    public string PeerId { get; set; };
    public string EndpointUri { get; set; };
    public bool[]? PieceBitmap { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Offline/AirGapTransferStrategy.cs
```csharp
public sealed class AirGapTransferStrategy
{
#endregion
}
    public AirGapTransferStrategy(byte[] encryptionKey, string instanceId);
    public async Task<AirGapPackage> CreatePackageAsync(IEnumerable<AirGapBlobData> blobs, string? targetInstanceId = null, bool autoIngest = false, IEnumerable<string>? tags = null, CancellationToken ct = default);
    public bool VerifyPackageSignature(AirGapPackage package);
    public IReadOnlyList<AirGapBlobData> UnpackShards(AirGapPackage package);
    public async Task<AirGapSecureWipeResult> SecureWipePackageAsync(string packagePath, int passes = 3, CancellationToken ct = default);
}
```
```csharp
public sealed class AirGapPackage
{
}
    public int Version { get; init; };
    public required string PackageId { get; init; }
    public required string SourceInstanceId { get; init; }
    public string? TargetInstanceId { get; init; }
    public DateTimeOffset CreatedAt { get; init; };
    public required AirGapPackageManifest Manifest { get; init; }
    public List<AirGapEncryptedShard> Shards { get; init; };
    public string? Signature { get; set; }
    public byte[] ToBytes();;
    public static AirGapPackage FromBytes(byte[] data);;
}
```
```csharp
public sealed class AirGapPackageManifest
{
}
    public int ShardCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public string EncryptionAlgorithm { get; init; };
    public string KeyDerivation { get; init; };
    public string? MerkleRoot { get; init; }
    public List<string> BlobUris { get; init; };
    public List<string> Tags { get; init; };
    public bool AutoIngest { get; init; }
}
```
```csharp
public sealed class AirGapEncryptedShard
{
}
    public int Index { get; init; }
    public required string BlobUri { get; init; }
    public required byte[] Data { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Tag { get; init; }
    public required string Hash { get; init; }
    public long OriginalSize { get; init; }
}
```
```csharp
public sealed class AirGapBlobData
{
}
    public required string Uri { get; init; }
    public required byte[] Data { get; init; }
    public Dictionary<string, string> Metadata { get; init; };
}
```
```csharp
public sealed class AirGapSecureWipeResult
{
}
    public bool Success { get; init; }
    public long BytesWiped { get; init; }
    public int WipePasses { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Verified { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Offline/StoreAndForwardStrategy.cs
```csharp
internal sealed class StoreAndForwardStrategy : DataTransitStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override TransitCapabilities Capabilities;;
    public override Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);
}
```
```csharp
internal sealed record ForwardPackage
{
}
    public string PackageId { get; init; };
    public string TransferId { get; init; };
    public DateTime CreatedAt { get; init; }
    public string SourceDescription { get; init; };
    public string DestinationDescription { get; init; };
    public long TotalSizeBytes { get; init; }
    public List<PackageEntry> Entries { get; init; };
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? ManifestHash { get; init; }
}
```
```csharp
internal sealed record PackageEntry
{
}
    public string RelativePath { get; init; };
    public long SizeBytes { get; init; }
    public string Sha256Hash { get; init; };
    public DateTime ModifiedAt { get; init; }
}
```
