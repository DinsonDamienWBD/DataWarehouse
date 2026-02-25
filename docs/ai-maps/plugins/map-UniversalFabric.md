# Plugin: UniversalFabric
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UniversalFabric

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/AddressRouter.cs
```csharp
public sealed class AddressRouter
{
}
    public IStorageStrategy? Resolve(StorageAddress address, IBackendRegistry registry);
    public void MapBucket(string bucket, string backendId);
    public void MapNode(string nodeId, string backendId);
    public void MapCluster(string clusterName, string backendId);
    public void SetDefaultBackend(string backendId);
    public string? DefaultBackendId;;
    public (string? backendId, string objectKey) ExtractRoutingKey(StorageAddress address, IBackendRegistry registry);
    public bool UnmapBucket(string bucket);;
    public bool UnmapNode(string nodeId);;
    public bool UnmapCluster(string clusterName);;
    public int TotalMappings;;
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/BackendRegistryImpl.cs
```csharp
public sealed class BackendRegistryImpl : IBackendRegistry
{
}
    public event Action<BackendDescriptor, bool>? BackendChanged;
    public void Register(BackendDescriptor descriptor, IStorageStrategy strategy);
    public bool Unregister(string backendId);
    public IReadOnlyList<BackendDescriptor> FindByTag(string tag);
    public IReadOnlyList<BackendDescriptor> FindByTier(StorageTier tier);
    public IReadOnlyList<BackendDescriptor> FindByCapabilities(StorageCapabilities required);
    public BackendDescriptor? GetById(string backendId);
    public IStorageStrategy? GetStrategy(string backendId);
    public IReadOnlyList<BackendDescriptor> All;;
    public int Count;;
    public bool Contains(string backendId);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/UniversalFabricPlugin.cs
```csharp
public sealed class UniversalFabricPlugin : StoragePluginBase, IStorageFabric, IDisposable
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public IBackendRegistry Registry;;
    public override async Task InitializeAsync(CancellationToken ct = default);
    public async Task<StorageObjectMetadata> StoreAsync(StorageAddress address, Stream data, StoragePlacementHints? hints, IDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async Task<StorageObjectMetadata> CopyAsync(StorageAddress source, StorageAddress destination, CancellationToken ct = default);
    public async Task<StorageObjectMetadata> MoveAsync(StorageAddress source, StorageAddress destination, CancellationToken ct = default);
    public IStorageStrategy? ResolveBackend(StorageAddress address);
    public async Task<FabricHealthReport> GetFabricHealthAsync(CancellationToken ct = default);
    public override async Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public override async Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);
    public override async Task DeleteAsync(string key, CancellationToken ct = default);
    public override async Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    public override async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct = default);
    public override async Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);
    public override async Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);
    public async Task<long?> GetAvailableCapacityAsync(CancellationToken ct = default);
    protected override Dictionary<string, object> GetMetadata();
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Migration/LiveMigrationEngine.cs
```csharp
public class LiveMigrationEngine
{
}
    public LiveMigrationEngine(IStorageFabric fabric);
    public Task<MigrationJob> StartMigrationAsync(MigrationJob job, CancellationToken ct = default);
    public MigrationProgress? GetProgress(string jobId);
    public void PauseJob(string jobId);
    public void ResumeJob(string jobId);
    public void CancelJob(string jobId);
    public IReadOnlyList<MigrationJob> ListJobs();;
    public MigrationJob? GetJob(string jobId);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Migration/MigrationJob.cs
```csharp
public class MigrationJob
{
}
    public string JobId { get; };
    public required string SourceBackendId { get; init; }
    public required string DestinationBackendId { get; init; }
    public string? SourcePrefix { get; init; }
    public MigrationMode Mode { get; init; };
    public MigrationJobStatus Status { get; private set; };
    public DateTime CreatedAt { get; };
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? ErrorMessage { get; private set; }
    public long TotalObjects;;
    public long MigratedObjects;;
    public long FailedObjects;;
    public long SkippedObjects;;
    public long TotalBytes;;
    public long MigratedBytes;;
    public int MaxConcurrency { get; init; };
    public int MaxRetries { get; init; };
    public bool VerifyAfterCopy { get; init; };
    public bool DeleteSourceAfterVerify { get; init; }
    public bool SkipExisting { get; init; };
    public IReadOnlyList<MigrationFailure> Failures;;
    public void Start();
    public void Pause();
    public void Resume();
    public void Complete();
    public void Fail(string error);
    public void Cancel();
    public void RecordMigrated(long bytes);
    public void RecordFailed(string key, string error);
    public void RecordSkipped();
    public void SetTotal(long objects, long bytes);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Migration/MigrationProgress.cs
```csharp
public record MigrationProgress
{
}
    public required string JobId { get; init; }
    public required MigrationJobStatus Status { get; init; }
    public required long TotalObjects { get; init; }
    public required long MigratedObjects { get; init; }
    public required long FailedObjects { get; init; }
    public required long SkippedObjects { get; init; }
    public required long TotalBytes { get; init; }
    public required long MigratedBytes { get; init; }
    public double PercentComplete;;
    public TimeSpan Elapsed { get; init; }
    public double BytesPerSecond;;
    public TimeSpan? EstimatedRemaining;;
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Placement/PlacementContext.cs
```csharp
public record PlacementContext
{
}
    public string? ContentType { get; init; }
    public long? ObjectSize { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
    public string? BucketName { get; init; }
    public string? ObjectKey { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Placement/PlacementOptimizer.cs
```csharp
public class PlacementOptimizer
{
}
    public PlacementOptimizer(IBackendRegistry registry, PlacementScorer? scorer = null);
    public IReadOnlyList<PlacementRule> Rules
{
    get
    {
        _rulesLock.EnterReadLock();
        try
        {
            return _rules.OrderBy(r => r.Priority).ToList().AsReadOnly();
        }
        finally
        {
            _rulesLock.ExitReadLock();
        }
    }
}
    public void AddRule(PlacementRule rule);
    public bool RemoveRule(string ruleName);
    public async Task<PlacementResult> SelectBackendAsync(StoragePlacementHints hints, PlacementContext? context = null, CancellationToken ct = default);
    public async Task<IReadOnlyList<PlacementResult>> SelectBackendsAsync(StoragePlacementHints hints, int count, CancellationToken ct = default);
}
```
```csharp
public record PlacementResult
{
}
    public required BackendDescriptor Backend { get; init; }
    public required double Score { get; init; }
    public required IReadOnlyDictionary<string, double> ScoreBreakdown { get; init; }
    public IReadOnlyList<BackendDescriptor>? Alternatives { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Placement/PlacementRule.cs
```csharp
public record PlacementCondition
{
}
    public string? ContentTypePattern { get; init; }
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }
    public IReadOnlySet<string>? MetadataKeys { get; init; }
    public string? BucketPattern { get; init; }
    public bool Matches(PlacementContext? context);
}
```
```csharp
public record PlacementRule
{
}
    public required string Name { get; init; }
    public PlacementRuleType Type { get; init; }
    public PlacementCondition? Condition { get; init; }
    public string? TargetBackendId { get; init; }
    public IReadOnlySet<string>? RequiredTags { get; init; }
    public IReadOnlySet<string>? ExcludedTags { get; init; }
    public StorageTier? RequiredTier { get; init; }
    public string? RequiredRegion { get; init; }
    public int Priority { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Placement/PlacementScorer.cs
```csharp
public record PlacementWeights
{
}
    public double TierWeight { get; init; };
    public double TagWeight { get; init; };
    public double RegionWeight { get; init; };
    public double CapacityWeight { get; init; };
    public double PriorityWeight { get; init; };
    public double HealthWeight { get; init; };
    public double CapabilityWeight { get; init; };
}
```
```csharp
public class PlacementScorer
{
}
    public PlacementScorer(PlacementWeights? weights = null);
    public PlacementWeights Weights;;
    public double Score(BackendDescriptor backend, StoragePlacementHints hints, StorageHealthInfo? health);
    public ScoreBreakdown ScoreWithBreakdown(BackendDescriptor backend, StoragePlacementHints hints, StorageHealthInfo? health);
}
```
```csharp
public record ScoreBreakdown
{
}
    public double TierScore { get; init; }
    public double TagScore { get; init; }
    public double RegionScore { get; init; }
    public double CapacityScore { get; init; }
    public double PriorityScore { get; init; }
    public double HealthScore { get; init; }
    public double CapabilityScore { get; init; }
    public double TotalScore { get; init; }
    public IReadOnlyDictionary<string, double> ToDictionary();;
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Resilience/BackendAbstractionLayer.cs
```csharp
public class BackendAbstractionLayer : IStorageStrategy
{
#endregion
}
    public BackendAbstractionLayer(IStorageStrategy inner, string backendId, ErrorNormalizer normalizer, FallbackChain? fallbackChain = null, BackendAbstractionOptions? options = null);
    public string StrategyId;;
    public string Name;;
    public StorageTier Tier;;
    public StorageCapabilities Capabilities;;
    public async Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);
    public async Task DeleteAsync(string key, CancellationToken ct = default);
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    public async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default);
    public async Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);
    public async Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);
    public async Task<long?> GetAvailableCapacityAsync(CancellationToken ct = default);
    public int ConsecutiveFailures
{
    get
    {
        lock (_circuitLock)
        {
            return _consecutiveFailures;
        }
    }
}
    public bool IsCircuitOpen
{
    get
    {
        lock (_circuitLock)
        {
            return _consecutiveFailures >= _options.CircuitBreakerThreshold && DateTime.UtcNow < _circuitOpenUntil;
        }
    }
}
}
```
```csharp
public record BackendAbstractionOptions
{
}
    public TimeSpan OperationTimeout { get; init; };
    public int CircuitBreakerThreshold { get; init; };
    public TimeSpan CircuitBreakerCooldown { get; init; };
    public bool EnableMetrics { get; init; };
    public static BackendAbstractionOptions Default { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Resilience/ErrorNormalizer.cs
```csharp
public class ErrorNormalizer
{
}
    public Exception Normalize(Exception ex, string backendId, string operation, string? key = null);
    public bool IsRetryable(Exception ex);;
    public bool ShouldFallback(Exception ex);;
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Resilience/FallbackChain.cs
```csharp
public class FallbackChain
{
}
    public FallbackChain(IBackendRegistry registry, ErrorNormalizer? normalizer = null);
    public async Task<T> ExecuteWithFallbackAsync<T>(string primaryBackendId, Func<IStorageStrategy, Task<T>> operation, FallbackOptions? options = null, CancellationToken ct = default);
    public async Task ExecuteWithFallbackAsync(string primaryBackendId, Func<IStorageStrategy, Task> operation, FallbackOptions? options = null, CancellationToken ct = default);
}
```
```csharp
public record FallbackOptions
{
}
    public int MaxFallbackAttempts { get; init; };
    public bool AllowCrossTierFallback { get; init; }
    public bool AllowCrossRegionFallback { get; init; }
    public static FallbackOptions Default { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/S3Server/S3BucketManager.cs
```csharp
public sealed class S3BucketManager
{
}
    public S3BucketManager(IBackendRegistry registry, string storagePath);
    public S3BucketInfo CreateBucket(string name, string? backendId = null, string? region = null);
    public void DeleteBucket(string name);
    public bool BucketExists(string name);
    public IReadOnlyList<S3BucketInfo> ListBuckets();
    public S3BucketInfo? GetBucket(string name);
    public string? GetBackendId(string bucketName);
    public void UpdateBucketStats(string bucketName, long objectCountDelta, long sizeDelta);
    public void SetVersioning(string bucketName, bool enabled);
    public int Count;;
    public static bool IsValidBucketName(string name);
    internal sealed class BucketEntry;
}
```
```csharp
internal sealed class BucketEntry
{
}
    public required string Name { get; set; }
    public DateTime CreationDate { get; set; }
    public string? BackendId { get; set; }
    public string? Region { get; set; }
    public bool VersioningEnabled { get; set; }
    public long ObjectCount { get; set; }
    public long TotalSizeBytes { get; set; }
}
```
```csharp
public record S3BucketInfo
{
}
    public required string Name { get; init; }
    public required DateTime CreationDate { get; init; }
    public string? BackendId { get; init; }
    public string? Region { get; init; }
    public bool VersioningEnabled { get; init; }
    public long ObjectCount { get; init; }
    public long TotalSizeBytes { get; init; }
}
```
```csharp
[JsonSerializable(typeof(List<S3BucketManager.BucketEntry>))]
internal partial class BucketJsonContext : JsonSerializerContext
{
}
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/S3Server/S3CredentialStore.cs
```csharp
public sealed class S3CredentialStore
{
}
    public S3CredentialStore(string storagePath);
    public S3Credentials CreateCredentials(string? userId = null, IReadOnlySet<string>? allowedBuckets = null, bool isAdmin = false);
    public S3Credentials? GetCredentials(string accessKeyId);
    public bool DeleteCredentials(string accessKeyId);
    public IReadOnlyList<S3Credentials> ListCredentials();
    public int Count;;
    internal sealed class CredentialEntry;
}
```
```csharp
internal sealed class CredentialEntry
{
}
    public required string AccessKeyId { get; set; }
    public required string SecretAccessKey { get; set; }
    public string? UserId { get; set; }
    public List<string>? AllowedBuckets { get; set; }
    public bool IsAdmin { get; set; }
}
```
```csharp
[JsonSerializable(typeof(List<S3CredentialStore.CredentialEntry>))]
internal partial class CredentialJsonContext : JsonSerializerContext
{
}
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/S3Server/S3HttpServer.cs
```csharp
internal sealed class MultipartUploadState
{
}
    public required string UploadId { get; init; }
    public required string BucketName { get; init; }
    public required string Key { get; init; }
    public string? ContentType { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
    public DateTime Initiated { get; init; };
    public BoundedDictionary<int, PartInfo> Parts { get; };
}
```
```csharp
internal sealed class PartInfo
{
}
    public required int PartNumber { get; init; }
    public required string ETag { get; init; }
    public required byte[] Data { get; init; }
}
```
```csharp
public sealed class S3HttpServer : IS3CompatibleServer
{
#endregion
}
    public S3HttpServer(IStorageFabric fabric);
    public bool IsRunning;;
    public string? ListenUrl { get; private set; }
    public async Task StartAsync(S3ServerOptions options, CancellationToken ct = default);
    public async Task StopAsync(CancellationToken ct = default);
    public Task<S3ListBucketsResponse> ListBucketsAsync(S3ListBucketsRequest request, CancellationToken ct = default);
    public Task<S3CreateBucketResponse> CreateBucketAsync(S3CreateBucketRequest request, CancellationToken ct = default);
    public Task DeleteBucketAsync(string bucketName, CancellationToken ct = default);
    public Task<bool> BucketExistsAsync(string bucketName, CancellationToken ct = default);
    public async Task<S3GetObjectResponse> GetObjectAsync(S3GetObjectRequest request, CancellationToken ct = default);
    public async Task<S3PutObjectResponse> PutObjectAsync(S3PutObjectRequest request, CancellationToken ct = default);
    public async Task DeleteObjectAsync(string bucketName, string key, CancellationToken ct = default);
    public async Task<S3HeadObjectResponse> HeadObjectAsync(string bucketName, string key, CancellationToken ct = default);
    public async Task<S3ListObjectsResponse> ListObjectsV2Async(S3ListObjectsRequest request, CancellationToken ct = default);
    public Task<S3InitiateMultipartResponse> InitiateMultipartUploadAsync(S3InitiateMultipartRequest request, CancellationToken ct = default);
    public async Task<S3UploadPartResponse> UploadPartAsync(S3UploadPartRequest request, CancellationToken ct = default);
    public async Task<S3CompleteMultipartResponse> CompleteMultipartUploadAsync(S3CompleteMultipartRequest request, CancellationToken ct = default);
    public Task AbortMultipartUploadAsync(string bucketName, string key, string uploadId, CancellationToken ct = default);
    public Task<string> GeneratePresignedUrlAsync(S3PresignedUrlRequest request, CancellationToken ct = default);
    public async Task<S3CopyObjectResponse> CopyObjectAsync(S3CopyObjectRequest request, CancellationToken ct = default);
    public void Dispose();
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/S3Server/S3RequestParser.cs
```csharp
public sealed class S3RequestParser
{
#endregion
}
    public S3Operation ParseOperation(HttpListenerRequest request);
    public (string? Bucket, string? Key) ExtractBucketAndKey(HttpListenerRequest request);
    public S3GetObjectRequest ParseGetObject(HttpListenerRequest req, string bucket, string key);
    public S3PutObjectRequest ParsePutObject(HttpListenerRequest req, string bucket, string key);
    public S3ListObjectsRequest ParseListObjects(HttpListenerRequest req, string bucket);
    public S3InitiateMultipartRequest ParseInitiateMultipart(HttpListenerRequest req, string bucket, string key);
    public S3UploadPartRequest ParseUploadPart(HttpListenerRequest req, string bucket, string key);
    public async Task<S3CompleteMultipartRequest> ParseCompleteMultipartAsync(HttpListenerRequest req, string bucket, string key);
    public S3CopyObjectRequest ParseCopyObject(HttpListenerRequest req, string destBucket, string destKey);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/S3Server/S3ResponseWriter.cs
```csharp
public sealed class S3ResponseWriter
{
#endregion
}
    public void WriteListBucketsResponse(HttpListenerResponse resp, S3ListBucketsResponse data);
    public void WriteListObjectsResponse(HttpListenerResponse resp, S3ListObjectsResponse data, string bucketName);
    public void WriteInitiateMultipartResponse(HttpListenerResponse resp, S3InitiateMultipartResponse data);
    public void WriteCompleteMultipartResponse(HttpListenerResponse resp, S3CompleteMultipartResponse data);
    public void WriteCopyObjectResponse(HttpListenerResponse resp, S3CopyObjectResponse data);
    public void WriteCreateBucketResponse(HttpListenerResponse resp, S3CreateBucketResponse data);
    public void WriteErrorResponse(HttpListenerResponse resp, int statusCode, string errorCode, string message, string? resource = null);
    public void SetObjectHeaders(HttpListenerResponse resp, S3HeadObjectResponse metadata);
    public void SetGetObjectHeaders(HttpListenerResponse resp, S3GetObjectResponse data);
    public void WriteNoContentResponse(HttpListenerResponse resp, int statusCode = 204);
    public void WritePutObjectResponse(HttpListenerResponse resp, S3PutObjectResponse data);
    public void WriteUploadPartResponse(HttpListenerResponse resp, S3UploadPartResponse data);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/S3Server/S3SignatureV4.cs
```csharp
public sealed class S3SignatureV4 : IS3AuthProvider
{
}
    public S3SignatureV4(S3CredentialStore credentialStore);
    public Task<S3AuthResult> AuthenticateAsync(S3AuthContext context, CancellationToken ct = default);
    public Task<S3Credentials> GetCredentialsAsync(string accessKeyId, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UniversalFabric/Scaling/FabricScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Fabric scaling with dynamic topology and health-based node management")]
public sealed class FabricScalingManager : IScalableSubsystem, IDisposable
{
}
    public event Action<TopologySwitchRecommendation>? OnTopologySwitchRecommended;
    public event Action<string, NodeHealth>? OnNodeRemoved;
    public event Action<string>? OnNodeDiscovered;
    public FabricScalingManager(FabricTopology initialTopology = FabricTopology.Star, ScalingLimits? initialLimits = null, bool autoSwitchEnabled = true, int heartbeatIntervalMs = DefaultHeartbeatIntervalMs, int missedHeartbeatsThreshold = DefaultMissedHeartbeatsThreshold);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits;;
    public BackpressureState CurrentBackpressureState;;
    public int GetMaxNodesForTopology(FabricTopology topology);
    public void SetMaxNodesForTopology(FabricTopology topology, int maxNodes);
    public FabricTopology CurrentTopology;;
    public void ConfigureTopologySwitching(int starToMeshThreshold = DefaultStarToMeshThreshold, int meshToFederatedThreshold = DefaultMeshToFederatedThreshold, bool autoSwitch = true);
    public void SwitchTopology(FabricTopology targetTopology);
    public void RecordHeartbeat(string nodeId, IReadOnlyDictionary<string, string>? metadata = null);
    public NodeHealth? GetNodeHealth(string nodeId);
    public int ActiveNodeCount;;
    public void ConfigureHeartbeat(int intervalMs, int missedThreshold);
    public void Dispose();
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Node health tracking for fabric topology")]
public sealed record NodeHealth
{
}
    public required string NodeId { get; init; }
    public required bool IsHealthy { get; init; }
    public required DateTime LastHeartbeatUtc { get; init; }
    public required int MissedHeartbeats { get; init; }
    public required DateTime JoinedUtc { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; };
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Topology switch recommendation")]
public sealed record TopologySwitchRecommendation
{
}
    public required FabricTopology FromTopology { get; init; }
    public required FabricTopology ToTopology { get; init; }
    public required string Reason { get; init; }
    public required int NodeCount { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required bool WasAutomatic { get; init; }
}
```
