# Plugin: UltimateDatabaseStorage
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDatabaseStorage

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/DatabaseStorageStrategyBase.cs
```csharp
public abstract class DatabaseStorageStrategyBase : StorageStrategyBase, IAsyncDisposable
{
#endregion
}
    protected bool _isConnected;
    protected static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};
    public abstract DatabaseCategory DatabaseCategory { get; }
    public abstract string Engine { get; }
    public virtual bool SupportsTransactions;;
    public virtual bool SupportsSql;;
    public bool IsConnected;;
    protected virtual string DefaultTableName;;
    protected DatabaseStorageStrategyBase(int maxConcurrentOperations = 10);
    protected object? GetConfiguration(string key);
    protected void SetConfiguration(string key, object value);
    protected T GetConfiguration<T>(string key, T defaultValue = default !);
    protected string GetConnectionString();
    public async Task InitializeAsync(IDictionary<string, object>? configuration = null, CancellationToken ct = default);
    protected virtual Task InitializeCoreAsync(CancellationToken ct);
    public async Task ConnectAsync(CancellationToken ct = default);
    protected abstract Task ConnectCoreAsync(CancellationToken ct);;
    public async Task DisconnectAsync(CancellationToken ct = default);
    protected virtual Task DisconnectCoreAsync(CancellationToken ct);
    protected void EnsureInitialized();
    protected async Task EnsureConnectedAsync(CancellationToken ct = default);
    protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected abstract Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);;
    protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct);
    protected abstract Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);;
    protected override async Task DeleteAsyncCore(string key, CancellationToken ct);
    protected abstract Task<long> DeleteCoreAsync(string key, CancellationToken ct);;
    protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct);
    protected abstract Task<bool> ExistsCoreAsync(string key, CancellationToken ct);;
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected abstract IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, CancellationToken ct);;
    protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct);
    protected abstract Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);;
    protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct);
    protected abstract Task<bool> CheckHealthCoreAsync(CancellationToken ct);;
    protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct);
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(string query, IDictionary<string, object>? parameters = null, CancellationToken ct = default);
    protected virtual Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(string query, IDictionary<string, object>? parameters, CancellationToken ct);
    public async Task<int> ExecuteNonQueryAsync(string command, IDictionary<string, object>? parameters = null, CancellationToken ct = default);
    protected virtual Task<int> ExecuteNonQueryCoreAsync(string command, IDictionary<string, object>? parameters, CancellationToken ct);
    public async Task<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default);
    protected virtual Task<IDatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken ct);
    public virtual async Task<IReadOnlyList<StorageObjectMetadata>> StoreBatchAsync(IEnumerable<(string key, byte[] data, IDictionary<string, string>? metadata)> items, CancellationToken ct = default);
    public virtual async Task<IReadOnlyDictionary<string, byte[]>> RetrieveBatchAsync(IEnumerable<string> keys, CancellationToken ct = default);
    public virtual async Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default);
    public async Task EnsureSchemaAsync(CancellationToken ct = default);
    protected virtual Task EnsureSchemaCoreAsync(CancellationToken ct);
    public DatabaseStorageStatistics GetDatabaseStatistics();
    protected static void ValidateSqlIdentifier(string identifier, string parameterName = "identifier");
    protected virtual void ValidateKey(string key);
    protected virtual int GetMaxKeyLength();;
    protected static string GenerateETag(byte[] content);
    protected static string GenerateETag(string key, long size, long modifiedTicks);
    protected static string GetContentType(string key);
    public new virtual void Dispose();
    public new async ValueTask DisposeAsync();
    protected new virtual async ValueTask DisposeAsyncCore();
}
```
```csharp
public interface IDatabaseTransaction : IAsyncDisposable
{
}
    string TransactionId { get; }
    IsolationLevel IsolationLevel { get; }
    Task CommitAsync(CancellationToken ct = default);;
    Task RollbackAsync(CancellationToken ct = default);;
}
```
```csharp
public record DatabaseStorageStatistics
{
}
    public string StrategyId { get; init; };
    public string StrategyName { get; init; };
    public StorageTier Tier { get; init; }
    public DatabaseCategory DatabaseCategory { get; init; }
    public string Engine { get; init; };
    public long TotalOperations { get; init; }
    public long SuccessfulOperations { get; init; }
    public long FailedOperations { get; init; }
    public double SuccessRate { get; init; }
    public double AverageLatencyMs { get; init; }
    public long TotalBytesStored { get; init; }
    public long TotalBytesRetrieved { get; init; }
    public long TotalBytesDeleted { get; init; }
    public long StoreOperations { get; init; }
    public long RetrieveOperations { get; init; }
    public long DeleteOperations { get; init; }
    public long QueryOperations { get; init; }
    public long TransactionCount { get; init; }
    public long ConnectionCount { get; init; }
    public DateTime InitializationTime { get; init; }
    public TimeSpan Uptime { get; init; }
    public bool IsConnected { get; init; }
    public bool IsInitialized { get; init; }
    public bool IsDisposed { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/UltimateDatabaseStoragePlugin.cs
```csharp
[PluginProfile(ServiceProfileType.Server)]
public sealed class UltimateDatabaseStoragePlugin : DataWarehouse.SDK.Contracts.Hierarchy.StoragePluginBase, IAsyncDisposable
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public int StrategyCount;;
    public UltimateDatabaseStoragePlugin();
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    public override async Task OnMessageAsync(PluginMessage message);
    protected override Dictionary<string, object> GetMetadata();
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override async ValueTask DisposeAsyncCore();
    public override Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);;
    public override Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);;
    public override Task DeleteAsync(string key, CancellationToken ct = default);;
    public override Task<bool> ExistsAsync(string key, CancellationToken ct = default);;
    public override async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public override Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);;
    public override Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/DatabaseStorageOptimization.cs
```csharp
public sealed class DatabaseIndexManager
{
}
    public IndexDefinition CreateIndex(string collection, string indexName, IndexType type, IReadOnlyList<string> fields, bool unique = false);
    public bool DropIndex(string collection, string indexName);
    public void RecordUsage(string collection, string indexName, double queryTimeMs);
    public IndexRecommendation Analyze(string collection);
    public IReadOnlyList<IndexDefinition> GetIndexes(string collection);;
}
```
```csharp
public sealed record IndexDefinition
{
}
    public required string Collection { get; init; }
    public required string IndexName { get; init; }
    public IndexType Type { get; init; }
    public List<string> Fields { get; init; };
    public bool IsUnique { get; init; }
    public DateTime CreatedAt { get; init; }
    public IndexStatus Status { get; init; }
    public long SizeBytes { get; init; }
}
```
```csharp
public sealed class IndexUsageStats
{
}
    public required string IndexKey { get; init; }
    public long TotalScans { get; set; }
    public double TotalTimeMs { get; set; }
    public DateTime? LastUsed { get; set; }
}
```
```csharp
public sealed record IndexRecommendation
{
}
    public required string Collection { get; init; }
    public List<string> UnusedIndexes { get; init; };
    public List<string> HeavilyUsedIndexes { get; init; };
    public int TotalIndexes { get; init; }
    public long TotalIndexSizeBytes { get; init; }
}
```
```csharp
public sealed class CompactionPolicyManager
{
}
    public void SetPolicy(string collection, CompactionStrategy strategy, int maxLevels = 7, double sizeRatio = 10, TimeSpan? timeWindow = null);
    public CompactionDecision EvaluateCompaction(string collection, int sstableCount, long totalSizeBytes, int level = 0);
    public void RecordCompaction(string collection, long bytesRead, long bytesWritten, int filesRemoved, int filesCreated, TimeSpan duration);
    public IReadOnlyList<CompactionHistory> GetHistory(string collection, int limit = 50);
}
```
```csharp
public sealed record CompactionPolicy
{
}
    public required string Collection { get; init; }
    public CompactionStrategy Strategy { get; init; }
    public int MaxLevels { get; init; }
    public double SizeRatio { get; init; }
    public TimeSpan TimeWindow { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record CompactionDecision
{
}
    public bool ShouldCompact { get; init; }
    public CompactionStrategy Strategy { get; init; }
    public TimeSpan EstimatedDuration { get; init; }
    public CompactionPriority Priority { get; init; }
}
```
```csharp
public sealed record CompactionHistory
{
}
    public required string Collection { get; init; }
    public long BytesRead { get; init; }
    public long BytesWritten { get; init; }
    public int FilesRemoved { get; init; }
    public int FilesCreated { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime CompletedAt { get; init; }
}
```
```csharp
public sealed class QueryOptimizer
{
}
    public void UpdateStatistics(string collection, long rowCount, long sizeBytes, Dictionary<string, ColumnStatistics>? columnStats = null);
    public QueryPlan GetPlan(string queryHash, string collection, QueryType type, IReadOnlyList<string>? filterFields = null);
    public int InvalidateCache(string collection);
    public OptimizerMetrics GetMetrics();;
}
```
```csharp
public sealed record QueryPlan
{
}
    public required string QueryHash { get; init; }
    public required string Collection { get; init; }
    public QueryType Type { get; init; }
    public AccessMethod AccessMethod { get; init; }
    public long EstimatedRows { get; init; }
    public double EstimatedCost { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record TableStatistics
{
}
    public required string Collection { get; init; }
    public long RowCount { get; init; }
    public long SizeBytes { get; init; }
    public Dictionary<string, ColumnStatistics> ColumnStats { get; init; };
    public DateTime UpdatedAt { get; init; }
}
```
```csharp
public sealed record ColumnStatistics
{
}
    public required string ColumnName { get; init; }
    public long DistinctValues { get; init; }
    public object? MinValue { get; init; }
    public object? MaxValue { get; init; }
    public double NullFraction { get; init; }
    public double AverageWidth { get; init; }
}
```
```csharp
public sealed record OptimizerMetrics
{
}
    public long PlansGenerated { get; init; }
    public long CacheHits { get; init; }
    public int CachedPlans { get; init; }
    public int TablesWithStatistics { get; init; }
}
```
```csharp
public sealed class StorageCacheIntegration
{
}
    public StorageCacheIntegration(int maxEntries = 10_000);
    public async Task<byte[]?> GetAsync(string key, Func<string, Task<byte[]?>> fallback, CancellationToken ct = default);
    public void Put(string key, byte[] data, TimeSpan? ttl = null);
    public bool Invalidate(string key);;
    public CacheMetrics GetMetrics();
}
```
```csharp
public sealed class CacheEntry
{
}
    public required string Key { get; init; }
    public required byte[] Data { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastAccessed { get; set; }
    public long AccessCount { get; set; }
    public DateTime? ExpiresAt { get; init; }
}
```
```csharp
public sealed record CacheMetrics
{
}
    public long Hits { get; init; }
    public long Misses { get; init; }
    public double HitRate { get; init; }
    public int EntryCount { get; init; }
    public long TotalSizeBytes { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Scaling/DatabaseStorageScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Database storage scaling with streaming retrieval and pagination")]
public sealed class DatabaseStorageScalingManager : IScalableSubsystem, IDisposable
{
}
    public DatabaseStorageScalingManager(IPersistentBackingStore? backingStore = null, ScalingLimits? initialLimits = null);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits;;
    public BackpressureState CurrentBackpressureState;;
    public async Task<Stream> CreateStreamingRetrievalAsync(Func<CancellationToken, Task<Stream>> dataSource, CancellationToken ct = default);
    public async IAsyncEnumerable<byte[]> StreamChunkedAsync(Func<CancellationToken, Task<Stream>> dataSource, int? chunkSize = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public async Task<PagedQueryResult<T>> ExecutePaginatedQueryAsync<T>(Func<int, int, CancellationToken, Task<(IReadOnlyList<T> Items, long TotalCount)>> queryExecutor, int offset = 0, int? limit = null, CancellationToken ct = default);
    public async IAsyncEnumerable<T> ExecuteStreamingQueryAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>> streamingQueryExecutor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public async Task<IReadOnlyDictionary<string, bool>> CheckStrategyHealthAsync(IReadOnlyDictionary<string, Func<CancellationToken, Task<bool>>> strategyHealthChecks, CancellationToken ct = default);
    internal void RecordBytesStreamed(long bytes);
    public void Dispose();
    internal sealed record StrategyHealthInfo;
}
```
```csharp
internal sealed record StrategyHealthInfo
{
}
    public required string StrategyName { get; init; }
    public required bool IsHealthy { get; init; }
    public required DateTime LastCheckedUtc { get; init; }
    public required int ConsecutiveFailures { get; init; }
}
```
```csharp
private sealed class BoundedBufferStream : Stream
{
}
    public BoundedBufferStream(Stream source, int chunkSize, long maxBufferBytes, DatabaseStorageScalingManager manager);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _source.Position; set => _source.Position = value; }
    public override int Read(byte[] buffer, int offset, int count);
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
    public override void Flush();;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
    protected override void Dispose(bool disposing);
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Paginated query result for database storage")]
public sealed class PagedQueryResult<T>
{
}
    public required IReadOnlyList<T> Items { get; init; }
    public required long TotalCount { get; init; }
    public required bool HasMore { get; init; }
    public string? ContinuationToken { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Spatial/PostGisStorageStrategy.cs
```csharp
public sealed class PostGisStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Relational/OracleStorageStrategy.cs
```csharp
public sealed class OracleStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken ct);
}
```
```csharp
private sealed class OracleDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public IsolationLevel IsolationLevel;;
    public OracleDbTransaction(OracleConnection connection, OracleTransaction transaction);
    public Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Relational/PostgreSqlStorageStrategy.cs
```csharp
public sealed class PostgreSqlStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(string query, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<int> ExecuteNonQueryCoreAsync(string command, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class PostgreSqlTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public IsolationLevel IsolationLevel;;
    public PostgreSqlTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction);
    public async Task CommitAsync(CancellationToken ct = default);
    public async Task RollbackAsync(CancellationToken ct = default);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Relational/SqliteStorageStrategy.cs
```csharp
public sealed class SqliteStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(string query, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<int> ExecuteNonQueryCoreAsync(string command, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken ct);
    public async Task VacuumAsync(CancellationToken ct = default);
    public async Task AnalyzeAsync(CancellationToken ct = default);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class SqliteDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public IsolationLevel IsolationLevel;;
    public SqliteDbTransaction(SqliteConnection connection, SqliteTransaction transaction, SemaphoreSlim writeLock);
    public Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Relational/SqlServerStorageStrategy.cs
```csharp
public sealed class SqlServerStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(string query, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<int> ExecuteNonQueryCoreAsync(string command, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken ct);
}
```
```csharp
private sealed class SqlServerTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public IsolationLevel IsolationLevel;;
    public SqlServerTransaction(SqlConnection connection, SqlTransaction transaction);
    public Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Relational/MySqlStorageStrategy.cs
```csharp
public sealed class MySqlStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(string query, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<int> ExecuteNonQueryCoreAsync(string command, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken ct);
}
```
```csharp
private sealed class MySqlDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public IsolationLevel IsolationLevel;;
    public MySqlDbTransaction(MySqlConnection connection, MySqlTransaction transaction);
    public async Task CommitAsync(CancellationToken ct = default);
    public async Task RollbackAsync(CancellationToken ct = default);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Graph/GraphPartitioningStrategies.cs
```csharp
public abstract class GraphPartitioningStrategyBase
{
}
    public abstract string StrategyId { get; }
    public abstract string Name { get; }
    public int PartitionCount { get; set; };
    public abstract int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null);;
    public abstract IEnumerable<int> AssignEdgePartitions(string edgeId, string sourceVertexId, string targetVertexId, IDictionary<string, object>? properties = null);;
    public virtual IEnumerable<int> GetQueryPartitions(string vertexId);
    public IEnumerable<int> GetAllPartitions();
}
```
```csharp
public sealed class HashPartitioningStrategy : GraphPartitioningStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public HashPartitionAlgorithm Algorithm { get; set; };
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null);
    public override IEnumerable<int> AssignEdgePartitions(string edgeId, string sourceVertexId, string targetVertexId, IDictionary<string, object>? properties = null);
}
```
```csharp
public sealed class RangePartitioningStrategy : GraphPartitioningStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public void SetRangeBoundaries(IEnumerable<string> boundaries);
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null);
    public override IEnumerable<int> AssignEdgePartitions(string edgeId, string sourceVertexId, string targetVertexId, IDictionary<string, object>? properties = null);
}
```
```csharp
public sealed class EdgeCutPartitioningStrategy : GraphPartitioningStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public double BalanceFactor { get; set; };
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null);
    public override IEnumerable<int> AssignEdgePartitions(string edgeId, string sourceVertexId, string targetVertexId, IDictionary<string, object>? properties = null);
    public Dictionary<int, long> GetPartitionSizes();
    public double GetImbalanceRatio();
}
```
```csharp
public sealed class VertexCutPartitioningStrategy : GraphPartitioningStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public int ReplicationThreshold { get; set; };
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null);
    public override IEnumerable<int> AssignEdgePartitions(string edgeId, string sourceVertexId, string targetVertexId, IDictionary<string, object>? properties = null);
    public override IEnumerable<int> GetQueryPartitions(string vertexId);
    public int GetVertexReplicationFactor(string vertexId);
    public double GetAverageReplicationFactor();
}
```
```csharp
public sealed class CommunityPartitioningStrategy : GraphPartitioningStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public CommunityDetectionAlgorithm Algorithm { get; set; };
    public void AssignCommunity(string vertexId, int communityId);
    public void SetCommunityAssignments(IDictionary<string, int> assignments);
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null);
    public override IEnumerable<int> AssignEdgePartitions(string edgeId, string sourceVertexId, string targetVertexId, IDictionary<string, object>? properties = null);
    public int GetCommunity(string vertexId);
}
```
```csharp
public sealed class GridPartitioningStrategy : GraphPartitioningStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public int GridColumns { get; set; };
    public int GridRows { get; set; };
    public double MinLatitude { get; set; };
    public double MaxLatitude { get; set; };
    public double MinLongitude { get; set; };
    public double MaxLongitude { get; set; };
    public GridPartitioningStrategy();
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null);
    public override IEnumerable<int> AssignEdgePartitions(string edgeId, string sourceVertexId, string targetVertexId, IDictionary<string, object>? properties = null);
    public IEnumerable<int> GetAdjacentPartitions(int partition);
}
```
```csharp
public sealed class GraphPartitioningStrategyRegistry
{
}
    public void Register(GraphPartitioningStrategyBase strategy);
    public GraphPartitioningStrategyBase? Get(string strategyId);
    public IEnumerable<GraphPartitioningStrategyBase> GetAll();
    public static GraphPartitioningStrategyRegistry CreateDefault();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Graph/GraphVisualizationExport.cs
```csharp
public static class GraphVisualizationExport
{
#endregion
}
    public static string ExportToD3Json(IEnumerable<GraphVisualizationNode> nodes, IEnumerable<GraphVisualizationEdge> edges, D3ExportOptions? options = null);
    public static string ExportToCytoscapeJson(IEnumerable<GraphVisualizationNode> nodes, IEnumerable<GraphVisualizationEdge> edges, CytoscapeExportOptions? options = null);
    public static string ExportToGraphML(IEnumerable<GraphVisualizationNode> nodes, IEnumerable<GraphVisualizationEdge> edges, GraphMLExportOptions? options = null);
    public static string ExportToDot(IEnumerable<GraphVisualizationNode> nodes, IEnumerable<GraphVisualizationEdge> edges, DotExportOptions? options = null);
    public static string ExportToGexf(IEnumerable<GraphVisualizationNode> nodes, IEnumerable<GraphVisualizationEdge> edges, GexfExportOptions? options = null);
    public static string ExportToJgf(IEnumerable<GraphVisualizationNode> nodes, IEnumerable<GraphVisualizationEdge> edges, JgfExportOptions? options = null);
    public static AdjacencyMatrixResult ExportToAdjacencyMatrix(IEnumerable<GraphVisualizationNode> nodes, IEnumerable<GraphVisualizationEdge> edges, AdjacencyMatrixOptions? options = null);
    public static string ExportMatrixToCsv(AdjacencyMatrixResult matrix);
}
```
```csharp
public sealed class GraphVisualizationNode
{
}
    public required string Id { get; set; }
    public string? Label { get; set; }
    public string? Group { get; set; }
    public double? Size { get; set; }
    public string? Color { get; set; }
    public string? Shape { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public Dictionary<string, object>? Properties { get; set; }
}
```
```csharp
public sealed class GraphVisualizationEdge
{
}
    public string? Id { get; set; }
    public required string SourceId { get; set; }
    public required string TargetId { get; set; }
    public string? Label { get; set; }
    public double? Weight { get; set; }
    public string? Color { get; set; }
    public Dictionary<string, object>? Properties { get; set; }
}
```
```csharp
public sealed class D3ExportOptions
{
}
    public bool UseIndices { get; set; };
    public bool IncludeProperties { get; set; };
    public double DefaultNodeSize { get; set; };
    public string DefaultNodeColor { get; set; };
    public string DefaultEdgeColor { get; set; };
}
```
```csharp
public sealed class CytoscapeExportOptions
{
}
    public bool IncludeProperties { get; set; };
    public bool IncludePositions { get; set; };
}
```
```csharp
public sealed class GraphMLExportOptions
{
}
    public string GraphId { get; set; };
    public bool Directed { get; set; };
}
```
```csharp
public sealed class DotExportOptions
{
}
    public string GraphName { get; set; };
    public bool Directed { get; set; };
    public string? RankDir { get; set; }
    public string? Layout { get; set; }
    public string DefaultNodeShape { get; set; };
}
```
```csharp
public sealed class GexfExportOptions
{
}
    public bool Directed { get; set; };
    public bool Dynamic { get; set; };
    public bool IncludeAttributes { get; set; };
    public string? Creator { get; set; }
    public string? Description { get; set; }
}
```
```csharp
public sealed class JgfExportOptions
{
}
    public bool Directed { get; set; };
    public bool IncludeMetadata { get; set; };
    public string? Type { get; set; }
    public string? Label { get; set; }
}
```
```csharp
public sealed class AdjacencyMatrixOptions
{
}
    public bool Directed { get; set; };
    public double DefaultValue { get; set; };
}
```
```csharp
public sealed class AdjacencyMatrixResult
{
}
    public required List<string> NodeIds { get; init; }
    public required double[, ] Matrix { get; init; }
}
```
```csharp
internal sealed class D3Graph
{
}
    [JsonPropertyName("nodes")]
public required List<D3Node> Nodes { get; init; }
    [JsonPropertyName("links")]
public required List<D3Link> Links { get; init; }
}
```
```csharp
internal sealed class D3Node
{
}
    [JsonPropertyName("id")]
public required string Id { get; init; }
    [JsonPropertyName("label")]
public string? Label { get; init; }
    [JsonPropertyName("group")]
public string? Group { get; init; }
    [JsonPropertyName("size")]
public double Size { get; init; }
    [JsonPropertyName("color")]
public string? Color { get; init; }
    [JsonPropertyName("properties")]
public Dictionary<string, object>? Properties { get; init; }
}
```
```csharp
internal sealed class D3Link
{
}
    [JsonPropertyName("source")]
public required object Source { get; init; }
    [JsonPropertyName("target")]
public required object Target { get; init; }
    [JsonPropertyName("value")]
public double Value { get; init; }
    [JsonPropertyName("label")]
public string? Label { get; init; }
    [JsonPropertyName("color")]
public string? Color { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Graph/ArangoDbStorageStrategy.cs
```csharp
public sealed class ArangoDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class AuthResult
{
}
    [JsonPropertyName("jwt")]
public string? Jwt { get; set; }
}
```
```csharp
private sealed class ArangoDocument
{
}
    [JsonPropertyName("_key")]
public string Key { get; set; };
    [JsonPropertyName("_rev")]
public string? Rev { get; set; }
    [JsonPropertyName("data")]
public string Data { get; set; };
    [JsonPropertyName("size")]
public long Size { get; set; }
    [JsonPropertyName("contentType")]
public string? ContentType { get; set; }
    [JsonPropertyName("etag")]
public string? ETag { get; set; }
    [JsonPropertyName("metadata")]
public Dictionary<string, string>? Metadata { get; set; }
    [JsonPropertyName("createdAt")]
public DateTime CreatedAt { get; set; }
    [JsonPropertyName("modifiedAt")]
public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class ArangoDocumentResult
{
}
    [JsonPropertyName("_rev")]
public string? Rev { get; set; }
}
```
```csharp
private sealed class ArangoCursorResult
{
}
    [JsonPropertyName("result")]
public ArangoListDocument[]? Result { get; set; }
}
```
```csharp
private sealed class ArangoListDocument
{
}
    [JsonPropertyName("key")]
public string Key { get; set; };
    [JsonPropertyName("size")]
public long Size { get; set; }
    [JsonPropertyName("contentType")]
public string? ContentType { get; set; }
    [JsonPropertyName("etag")]
public string? ETag { get; set; }
    [JsonPropertyName("metadata")]
public Dictionary<string, string>? Metadata { get; set; }
    [JsonPropertyName("createdAt")]
public DateTime CreatedAt { get; set; }
    [JsonPropertyName("modifiedAt")]
public DateTime ModifiedAt { get; set; }
    [JsonPropertyName("rev")]
public string? Rev { get; set; }
}
```
```csharp
private sealed class ArangoTransactionResult
{
}
    [JsonPropertyName("result")]
public ArangoTransactionInfo? Result { get; set; }
}
```
```csharp
private sealed class ArangoTransactionInfo
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
}
```
```csharp
private sealed class ArangoTransaction : IDatabaseTransaction
{
}
    public string TransactionId;;
    public System.Data.IsolationLevel IsolationLevel;;
    public ArangoTransaction(HttpClient client, string database, string transactionId);
    public async Task CommitAsync(CancellationToken ct = default);
    public async Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Graph/DistributedGraphProcessing.cs
```csharp
public sealed class PregelGraphProcessor<TVertex, TMessage, TEdge>
    where TVertex : class where TMessage : class
{
}
    public int CurrentSuperstep;;
    public bool IsActive;;
    public event EventHandler<SuperstepEventArgs>? SuperstepStarted;
    public event EventHandler<SuperstepEventArgs>? SuperstepCompleted;
    public PregelGraphProcessor(GraphPartitioningStrategyBase? partitioner = null, PregelConfiguration? config = null);
    public void AddVertex(string vertexId, TVertex value);
    public void AddEdge(string sourceId, string targetId, TEdge value);
    public async Task<PregelResult> ExecuteAsync(IVertexProgram<TVertex, TMessage, TEdge> vertexProgram, IMessageCombiner<TMessage>? messageCombiner = null, CancellationToken ct = default);
    public TVertex? GetVertexValue(string vertexId);
    public IReadOnlyDictionary<string, TVertex> GetAllVertexValues();
}
```
```csharp
internal sealed class VertexState<TVertex, TMessage>
    where TVertex : class where TMessage : class
{
}
    public required string VertexId { get; init; }
    public required TVertex Value { get; set; }
    public bool IsActive { get; set; }
    public int Partition { get; init; }
    public List<TMessage>? IncomingMessages { get; set; }
}
```
```csharp
public sealed class Edge<TEdge>
{
}
    public required string TargetId { get; init; }
    public required TEdge Value { get; init; }
}
```
```csharp
public sealed class VertexContext<TVertex, TMessage, TEdge>
    where TVertex : class where TMessage : class
{
}
    public required string VertexId { get; init; }
    public int Superstep { get; init; }
    public required TVertex Value { get; init; }
    public required List<TMessage> IncomingMessages { get; init; }
    public required IReadOnlyList<Edge<TEdge>> Edges { get; init; }
    public required Action<string, TMessage> SendMessage { get; init; }
    public required Action VoteToHalt { get; init; }
    public void SendToAllNeighbors(TMessage message);
}
```
```csharp
public interface IVertexProgram<TVertex, TMessage, TEdge>
    where TVertex : class where TMessage : class
{
}
    Task<TVertex> ComputeAsync(VertexContext<TVertex, TMessage, TEdge> context, CancellationToken ct);;
}
```
```csharp
public interface IMessageCombiner<TMessage>
{
}
    TMessage Combine(IEnumerable<TMessage> messages);;
}
```
```csharp
public sealed class PregelConfiguration
{
}
    public int MaxSupersteps { get; set; };
    public int Parallelism { get; set; };
    public bool EnableCheckpointing { get; set; };
    public int CheckpointInterval { get; set; };
}
```
```csharp
public sealed class PregelResult
{
}
    public bool Success { get; init; }
    public int Supersteps { get; init; }
    public long TotalMessages { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, object?>? VertexResults { get; init; }
}
```
```csharp
internal sealed class SuperstepResult
{
}
    public int ActiveVertices { get; init; }
    public long MessagesSent { get; init; }
}
```
```csharp
public sealed class SuperstepEventArgs : EventArgs
{
}
    public int Superstep { get; init; }
    public int ActiveVertices { get; init; }
    public long MessagesSent { get; init; }
}
```
```csharp
public sealed class PageRankValue
{
}
    public double Rank { get; set; };
    public int OutDegree { get; set; }
}
```
```csharp
public sealed class PageRankMessage
{
}
    public double Contribution { get; set; }
}
```
```csharp
public sealed class PregelPageRank : IVertexProgram<PageRankValue, PageRankMessage, double>
{
}
    public double DampingFactor { get; set; };
    public double Tolerance { get; set; };
    public Task<PageRankValue> ComputeAsync(VertexContext<PageRankValue, PageRankMessage, double> context, CancellationToken ct);
}
```
```csharp
public sealed class SsspValue
{
}
    public double Distance { get; set; };
    public string? Predecessor { get; set; }
}
```
```csharp
public sealed class SsspMessage
{
}
    public double Distance { get; set; }
    public required string SenderId { get; init; }
}
```
```csharp
public sealed class PregelSssp : IVertexProgram<SsspValue, SsspMessage, double>
{
}
    public required string SourceVertexId { get; init; }
    public Task<SsspValue> ComputeAsync(VertexContext<SsspValue, SsspMessage, double> context, CancellationToken ct);
}
```
```csharp
public sealed class ComponentValue
{
}
    public string ComponentId { get; set; };
}
```
```csharp
public sealed class ComponentMessage
{
}
    public required string ComponentId { get; init; }
}
```
```csharp
public sealed class PregelConnectedComponents : IVertexProgram<ComponentValue, ComponentMessage, object>
{
}
    public Task<ComponentValue> ComputeAsync(VertexContext<ComponentValue, ComponentMessage, object> context, CancellationToken ct);
}
```
```csharp
public sealed class SumCombiner : IMessageCombiner<PageRankMessage>
{
}
    public PageRankMessage Combine(IEnumerable<PageRankMessage> messages);
}
```
```csharp
public sealed class MinDistanceCombiner : IMessageCombiner<SsspMessage>
{
}
    public SsspMessage Combine(IEnumerable<SsspMessage> messages);
}
```
```csharp
public sealed class MinComponentCombiner : IMessageCombiner<ComponentMessage>
{
}
    public ComponentMessage Combine(IEnumerable<ComponentMessage> messages);
}
```
```csharp
public sealed class DistributedGraphCoordinator
{
}
    public DistributedGraphCoordinator(GraphPartitioningStrategyBase partitioner);
    public void RegisterWorker(IGraphWorker worker);
    public async Task<DistributedComputationResult> ExecuteAsync(CancellationToken ct = default);
}
```
```csharp
public interface IGraphWorker
{
}
    int PartitionId { get; }
    Task<WorkerSuperstepResult> ExecuteSuperstepAsync(int superstep, CancellationToken ct);;
    Task<IEnumerable<(int TargetPartition, object Message)>> GetOutgoingMessagesAsync(CancellationToken ct);;
    Task ReceiveMessagesAsync(IEnumerable<object> messages, CancellationToken ct);;
}
```
```csharp
public sealed class WorkerSuperstepResult
{
}
    public bool HasActiveVertices { get; init; }
    public bool HasPendingMessages { get; init; }
    public long MessagesSent { get; init; }
}
```
```csharp
public sealed class DistributedComputationResult
{
}
    public int Supersteps { get; init; }
    public long TotalMessages { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Graph/GraphAnalyticsStrategies.cs
```csharp
public abstract class GraphAnalyticsAlgorithmBase
{
}
    public abstract string AlgorithmId { get; }
    public abstract string Name { get; }
    public abstract GraphAnalyticsCategory Category { get; }
    public abstract bool IsIterative { get; }
}
```
```csharp
public sealed class PageRankAlgorithm : GraphAnalyticsAlgorithmBase
{
}
    public override string AlgorithmId;;
    public override string Name;;
    public override GraphAnalyticsCategory Category;;
    public override bool IsIterative;;
    public double DampingFactor { get; set; };
    public double Tolerance { get; set; };
    public int MaxIterations { get; set; };
    public PageRankResult Compute(IDictionary<string, IList<string>> adjacency, IDictionary<string, double>? personalizationVector = null);
}
```
```csharp
public sealed class PageRankResult
{
}
    public required Dictionary<string, double> Scores { get; init; }
    public int Iterations { get; init; }
    public bool Converged { get; init; }
    public double FinalDifference { get; init; }
    public IEnumerable<(string Vertex, double Score)> GetTopN(int n);
}
```
```csharp
public sealed class BetweennessCentralityAlgorithm : GraphAnalyticsAlgorithmBase
{
}
    public override string AlgorithmId;;
    public override string Name;;
    public override GraphAnalyticsCategory Category;;
    public override bool IsIterative;;
    public bool Normalize { get; set; };
    public Dictionary<string, double> Compute(IDictionary<string, IList<string>> adjacency);
}
```
```csharp
public sealed class ClosenessCentralityAlgorithm : GraphAnalyticsAlgorithmBase
{
}
    public override string AlgorithmId;;
    public override string Name;;
    public override GraphAnalyticsCategory Category;;
    public override bool IsIterative;;
    public bool UseHarmonic { get; set; };
    public Dictionary<string, double> Compute(IDictionary<string, IList<string>> adjacency);
}
```
```csharp
public sealed class DegreeCentralityAlgorithm : GraphAnalyticsAlgorithmBase
{
}
    public override string AlgorithmId;;
    public override string Name;;
    public override GraphAnalyticsCategory Category;;
    public override bool IsIterative;;
    public DegreeType DegreeType { get; set; };
    public bool Normalize { get; set; };
    public Dictionary<string, double> Compute(IDictionary<string, IList<string>> adjacency, IDictionary<string, IList<string>>? reverseAdjacency = null);
}
```
```csharp
public sealed class LouvainCommunityDetection : GraphAnalyticsAlgorithmBase
{
}
    public override string AlgorithmId;;
    public override string Name;;
    public override GraphAnalyticsCategory Category;;
    public override bool IsIterative;;
    public double MinModularityGain { get; set; };
    public int MaxPasses { get; set; };
    public CommunityDetectionResult Compute(IList<(string Source, string Target, double Weight)> edges);
}
```
```csharp
public sealed class CommunityDetectionResult
{
}
    public required Dictionary<string, int> Communities { get; init; }
    public int NumCommunities { get; init; }
    public double Modularity { get; init; }
    public int Iterations { get; init; }
    public IEnumerable<string> GetCommunityMembers(int communityId);
    public Dictionary<int, int> GetCommunitySizes();
}
```
```csharp
public sealed class LabelPropagationCommunityDetection : GraphAnalyticsAlgorithmBase
{
}
    public override string AlgorithmId;;
    public override string Name;;
    public override GraphAnalyticsCategory Category;;
    public override bool IsIterative;;
    public int MaxIterations { get; set; };
    public CommunityDetectionResult Compute(IDictionary<string, IList<string>> adjacency);
}
```
```csharp
public sealed class TriangleCountingAlgorithm : GraphAnalyticsAlgorithmBase
{
}
    public override string AlgorithmId;;
    public override string Name;;
    public override GraphAnalyticsCategory Category;;
    public override bool IsIterative;;
    public TriangleCountResult Compute(IDictionary<string, IList<string>> adjacency);
}
```
```csharp
public sealed class TriangleCountResult
{
}
    public required Dictionary<string, int> VertexTriangleCounts { get; init; }
    public long TotalTriangles { get; init; }
    public Dictionary<string, double> ComputeClusteringCoefficients(IDictionary<string, IList<string>> adjacency);
}
```
```csharp
public sealed class ConnectedComponentsAlgorithm : GraphAnalyticsAlgorithmBase
{
}
    public override string AlgorithmId;;
    public override string Name;;
    public override GraphAnalyticsCategory Category;;
    public override bool IsIterative;;
    public ConnectedComponentsResult Compute(IDictionary<string, IList<string>> adjacency);
}
```
```csharp
public sealed class ConnectedComponentsResult
{
}
    public required Dictionary<string, int> Components { get; init; }
    public int NumComponents { get; init; }
    public IEnumerable<string> GetComponentMembers(int componentId);
    public int GetLargestComponent();
}
```
```csharp
public sealed class GraphAnalyticsRegistry
{
}
    public void Register(GraphAnalyticsAlgorithmBase algorithm);
    public GraphAnalyticsAlgorithmBase? Get(string algorithmId);
    public IEnumerable<GraphAnalyticsAlgorithmBase> GetAll();
    public IEnumerable<GraphAnalyticsAlgorithmBase> GetByCategory(GraphAnalyticsCategory category);
    public static GraphAnalyticsRegistry CreateDefault();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Graph/JanusGraphStorageStrategy.cs
```csharp
public sealed class JanusGraphStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Graph/Neo4jStorageStrategy.cs
```csharp
public sealed class Neo4jStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class Neo4jTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public Neo4jTransaction(IAsyncSession session, IAsyncTransaction transaction);
    public async Task CommitAsync(CancellationToken ct = default);
    public async Task RollbackAsync(CancellationToken ct = default);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Streaming/KafkaStorageStrategy.cs
```csharp
public sealed class KafkaStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class MetadataDocument
{
}
    public string Key { get; set; };
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? CustomMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Streaming/PulsarStorageStrategy.cs
```csharp
public sealed class PulsarStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class PulsarMessage
{
}
    [JsonPropertyName("payload")]
public string? Payload { get; set; }
    [JsonPropertyName("properties")]
public Dictionary<string, string>? Properties { get; set; }
}
```
```csharp
private sealed class PulsarProduceResult
{
}
    [JsonPropertyName("messageId")]
public string? MessageId { get; set; }
}
```
```csharp
private sealed class MetadataDocument
{
}
    public string Key { get; set; };
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? CustomMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Search/ElasticsearchStorageStrategy.cs
```csharp
public sealed class ElasticsearchStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    public async Task<IEnumerable<StorageObjectMetadata>> SearchAsync(string query, int maxResults = 100, CancellationToken ct = default);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class StorageDocument
{
}
    public string Key { get; set; };
    public string Data { get; set; };
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Search/MeilisearchStorageStrategy.cs
```csharp
public sealed class MeilisearchStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    public async Task<IEnumerable<StorageObjectMetadata>> SearchAsync(string query, int limit = 100, CancellationToken ct = default);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class StorageDocument
{
}
    public string Key { get; set; };
    public string Data { get; set; };
    public long Size { get; set; }
    public string ContentType { get; set; };
    public string? ETag { get; set; }
    public string? Metadata { get; set; }
    public long CreatedAt { get; set; }
    public long ModifiedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Search/TypesenseStorageStrategy.cs
```csharp
public sealed class TypesenseStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    public async Task<IEnumerable<StorageObjectMetadata>> SearchAsync(string query, int limit = 100, CancellationToken ct = default);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class TypesenseDocument
{
}
    [JsonPropertyName("id")]
public string Id { get; set; };
    [JsonPropertyName("key")]
public string Key { get; set; };
    [JsonPropertyName("data")]
public string Data { get; set; };
    [JsonPropertyName("size")]
public long Size { get; set; }
    [JsonPropertyName("contentType")]
public string ContentType { get; set; };
    [JsonPropertyName("etag")]
public string? ETag { get; set; }
    [JsonPropertyName("metadata")]
public string? Metadata { get; set; }
    [JsonPropertyName("createdAt")]
public long CreatedAt { get; set; }
    [JsonPropertyName("modifiedAt")]
public long ModifiedAt { get; set; }
}
```
```csharp
private sealed class TypesenseSearchResult
{
}
    [JsonPropertyName("hits")]
public TypesenseHit[]? Hits { get; set; }
}
```
```csharp
private sealed class TypesenseHit
{
}
    [JsonPropertyName("document")]
public TypesenseDocument? Document { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Search/OpenSearchStorageStrategy.cs
```csharp
public sealed class OpenSearchStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class StorageDocument
{
}
    public string Key { get; set; };
    public string Data { get; set; };
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/CloudNative/SpannerStorageStrategy.cs
```csharp
public sealed class SpannerStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(string query, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<int> ExecuteNonQueryCoreAsync(string command, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class SpannerDatabaseTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public IsolationLevel IsolationLevel;;
    public SpannerDatabaseTransaction(Google.Cloud.Spanner.Data.SpannerTransaction transaction);
    public async Task CommitAsync(CancellationToken ct = default);
    public async Task RollbackAsync(CancellationToken ct = default);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/CloudNative/CosmosDbStorageStrategy.cs
```csharp
public sealed class CosmosDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(string query, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class StorageDocument
{
}
    public string Id { get; set; };
    public string PartitionKey { get; set; };
    public string OriginalKey { get; set; };
    public string Data { get; set; };
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class CosmosDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public CosmosDbTransaction(Container container);
    public async Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Analytics/ClickHouseStorageStrategy.cs
```csharp
public sealed class ClickHouseStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Analytics/PrestoStorageStrategy.cs
```csharp
public sealed class PrestoStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class PrestoQueryResult
{
}
    [JsonPropertyName("nextUri")]
public string? NextUri { get; set; }
    [JsonPropertyName("data")]
public List<List<object?>>? Data { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Analytics/DruidStorageStrategy.cs
```csharp
public sealed class DruidStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class DruidScanResult
{
}
    [JsonPropertyName("events")]
public Dictionary<string, object>[]? Events { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/KeyValue/ConsulKvStorageStrategy.cs
```csharp
public sealed class ConsulKvStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    public async Task<IDistributedLock> AcquireLockAsync(string lockKey, CancellationToken ct = default);
    protected override async ValueTask DisposeAsyncCore();
    public interface IDistributedLock : IAsyncDisposable;
}
```
```csharp
private sealed class MetadataDocument
{
}
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? CustomMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class ConsulTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public ConsulTransaction(ConsulClient client);
    public async Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```
```csharp
public interface IDistributedLock : IAsyncDisposable
{
}
    Task ReleaseAsync(CancellationToken ct = default);;
}
```
```csharp
private sealed class ConsulLock : IDistributedLock
{
}
    public ConsulLock(Consul.IDistributedLock lockHandle);
    public async Task ReleaseAsync(CancellationToken ct = default);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/KeyValue/MemcachedStorageStrategy.cs
```csharp
public sealed class MemcachedStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class MetadataDocument
{
}
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? CustomMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/KeyValue/RedisStorageStrategy.cs
```csharp
public sealed class RedisStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    public async Task SetExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default);
    public async Task<TimeSpan?> GetExpirationAsync(string key, CancellationToken ct = default);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class RedisTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public RedisTransaction(IDatabase database);
    public async Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/KeyValue/RocksDbStorageStrategy.cs
```csharp
public sealed class RocksDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override Task ConnectCoreAsync(CancellationToken ct);
    protected override Task DisconnectCoreAsync(CancellationToken ct);
    protected override Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    public void Compact();
    public string GetStatistics();
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class MetadataDocument
{
}
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? CustomMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class RocksDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public RocksDbTransaction(RocksDb db, ColumnFamilyHandle dataHandle, ColumnFamilyHandle metadataHandle);
    public Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/KeyValue/EtcdStorageStrategy.cs
```csharp
public sealed class EtcdStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class MetadataDocument
{
}
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? CustomMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class EtcdTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public EtcdTransaction(EtcdClient client);
    public async Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/KeyValue/FoundationDbStorageStrategy.cs
```csharp
public sealed class FoundationDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class MetadataDocument
{
}
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? CustomMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class FdbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public FdbTransaction(IFdbTransaction transaction);
    public async Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/KeyValue/LevelDbStorageStrategy.cs
```csharp
public sealed class LevelDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override Task ConnectCoreAsync(CancellationToken ct);
    protected override Task DisconnectCoreAsync(CancellationToken ct);
    protected override Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    public void Compact();
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class MetadataDocument
{
}
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? CustomMetadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class LevelDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public LevelDbTransaction(RocksDb db);
    public Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/NoSQL/CouchDbStorageStrategy.cs
```csharp
public sealed class CouchDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class CouchDocument
{
}
    [JsonPropertyName("_id")]
public string? Id { get; set; }
    [JsonPropertyName("_rev")]
public string? Rev { get; set; }
    [JsonPropertyName("data")]
public string Data { get; set; };
    [JsonPropertyName("size")]
public long Size { get; set; }
    [JsonPropertyName("contentType")]
public string? ContentType { get; set; }
    [JsonPropertyName("etag")]
public string? ETag { get; set; }
    [JsonPropertyName("metadata")]
public Dictionary<string, string>? Metadata { get; set; }
    [JsonPropertyName("createdAt")]
public DateTime CreatedAt { get; set; }
    [JsonPropertyName("modifiedAt")]
public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class CouchPutResult
{
}
    [JsonPropertyName("ok")]
public bool Ok { get; set; }
    [JsonPropertyName("id")]
public string? Id { get; set; }
    [JsonPropertyName("rev")]
public string? Rev { get; set; }
}
```
```csharp
private sealed class CouchAllDocsResult
{
}
    [JsonPropertyName("rows")]
public CouchAllDocsRow[]? Rows { get; set; }
}
```
```csharp
private sealed class CouchAllDocsRow
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
    [JsonPropertyName("doc")]
public CouchDocument? Doc { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/NoSQL/DynamoDbStorageStrategy.cs
```csharp
public sealed class DynamoDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class DynamoDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public DynamoDbTransaction(AmazonDynamoDBClient client, string tableName);
    public async Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/NoSQL/DocumentDbStorageStrategy.cs
```csharp
public sealed class DocumentDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class StorageDocument
{
}
    [System.Text.Json.Serialization.JsonPropertyName("id")]
public string Id { get; set; };
    [System.Text.Json.Serialization.JsonPropertyName("partitionKey")]
public string PartitionKey { get; set; };
    [System.Text.Json.Serialization.JsonPropertyName("data")]
public string Data { get; set; };
    [System.Text.Json.Serialization.JsonPropertyName("size")]
public long Size { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("contentType")]
public string? ContentType { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("eTag")]
public string? ETag { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("metadata")]
public Dictionary<string, string>? Metadata { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
public DateTime CreatedAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("modifiedAt")]
public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class CosmosTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public CosmosTransaction(Container container);
    public async Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/NoSQL/RavenDbStorageStrategy.cs
```csharp
public sealed class RavenDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class StorageDocument
{
}
    public string Id { get; set; };
    public string Key { get; set; };
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
```
```csharp
private sealed class RavenDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public RavenDbTransaction(IAsyncDocumentSession session);
    public async Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/NoSQL/MongoDbStorageStrategy.cs
```csharp
public sealed class MongoDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(string query, IDictionary<string, object>? parameters, CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
[BsonIgnoreExtraElements]
private sealed class StorageDocument
{
}
    [BsonId]
[BsonElement("_id")]
public string Key { get; set; };
    [BsonElement("data")]
public byte[]? Data { get; set; }
    [BsonElement("size")]
public long Size { get; set; }
    [BsonElement("contentType")]
public string? ContentType { get; set; }
    [BsonElement("etag")]
public string? ETag { get; set; }
    [BsonElement("metadata")]
public Dictionary<string, string>? Metadata { get; set; }
    [BsonElement("createdAt")]
public DateTime CreatedAt { get; set; }
    [BsonElement("modifiedAt")]
public DateTime ModifiedAt { get; set; }
    [BsonElement("isGridFs")]
public bool IsGridFs { get; set; }
    [BsonElement("gridFsFileId")]
public ObjectId? GridFsFileId { get; set; }
}
```
```csharp
private sealed class MongoDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public MongoDbTransaction(IClientSessionHandle session);
    public async Task CommitAsync(CancellationToken ct = default);
    public async Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/TimeSeries/QuestDbStorageStrategy.cs
```csharp
public sealed class QuestDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/TimeSeries/TimescaleDbStorageStrategy.cs
```csharp
public sealed class TimescaleDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    public async Task EnableCompressionAsync(string olderThan = "7 days", CancellationToken ct = default);
    public async Task SetRetentionPolicyAsync(string retentionPeriod = "90 days", CancellationToken ct = default);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class NpgsqlDatabaseTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public NpgsqlDatabaseTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction);
    public async Task CommitAsync(CancellationToken ct = default);
    public async Task RollbackAsync(CancellationToken ct = default);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/TimeSeries/VictoriaMetricsStorageStrategy.cs
```csharp
public sealed class VictoriaMetricsStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class VmQueryResult
{
}
    [JsonPropertyName("data")]
public VmData? Data { get; set; }
}
```
```csharp
private sealed class VmData
{
}
    [JsonPropertyName("result")]
public VmResult[]? Result { get; set; }
}
```
```csharp
private sealed class VmResult
{
}
    [JsonPropertyName("metric")]
public Dictionary<string, string>? Metric { get; set; }
    [JsonPropertyName("value")]
public object[]? Value { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/TimeSeries/InfluxDbStorageStrategy.cs
```csharp
public sealed class InfluxDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Embedded/DuckDbStorageStrategy.cs
```csharp
public sealed class DuckDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override Task ConnectCoreAsync(CancellationToken ct);
    protected override Task DisconnectCoreAsync(CancellationToken ct);
    protected override Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    public async Task ExportToParquetAsync(string filePath, CancellationToken ct = default);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class DuckDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public DuckDbTransaction(DuckDBTransaction transaction);
    public Task CommitAsync(CancellationToken ct = default);
    public Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Embedded/DerbyStorageStrategy.cs
```csharp
public sealed class DerbyStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Embedded/LiteDbStorageStrategy.cs
```csharp
public sealed class LiteDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async Task<IDatabaseTransaction> BeginTransactionCoreAsync(System.Data.IsolationLevel isolationLevel, CancellationToken ct);
    public async Task<StorageObjectMetadata> StoreLargeFileAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);
    public async Task<Stream> RetrieveLargeFileAsync(string key, CancellationToken ct = default);
    public async Task CompactAsync(CancellationToken ct = default);
    public async Task CheckpointAsync(CancellationToken ct = default);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class StorageDocument
{
}
    public ObjectId Id { get; set; };
    public string Key { get; set; };
    public byte[]? Data { get; set; }
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool IsLargeFile { get; set; }
}
```
```csharp
private sealed class LiteDbTransaction : IDatabaseTransaction
{
}
    public string TransactionId { get; };
    public System.Data.IsolationLevel IsolationLevel;;
    public LiteDbTransaction(LiteDatabaseAsync database, SemaphoreSlim writeLock);
    public async Task CommitAsync(CancellationToken ct = default);
    public async Task RollbackAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Embedded/H2StorageStrategy.cs
```csharp
public sealed class H2StorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override bool IsProductionReady;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/Embedded/HsqlDbStorageStrategy.cs
```csharp
public sealed class HsqlDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/WideColumn/ScyllaDbStorageStrategy.cs
```csharp
public sealed class ScyllaDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/WideColumn/HBaseStorageStrategy.cs
```csharp
public sealed class HBaseStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
```csharp
private sealed class HBaseRowResponse
{
}
    public HBaseRow[]? Row { get; set; }
}
```
```csharp
private sealed class HBaseRow
{
}
    public string key { get; set; };
    public HBaseCell[]? Cell { get; set; }
}
```
```csharp
private sealed class HBaseCell
{
}
    public string column { get; set; };
    public long timestamp { get; set; }
    public string value { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/WideColumn/BigtableStorageStrategy.cs
```csharp
public sealed class BigtableStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/WideColumn/CassandraStorageStrategy.cs
```csharp
public sealed class CassandraStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/NewSQL/CockroachDbStorageStrategy.cs
```csharp
public sealed class CockroachDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/NewSQL/TiDbStorageStrategy.cs
```csharp
public sealed class TiDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/NewSQL/VitessStorageStrategy.cs
```csharp
public sealed class VitessStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseStorage/Strategies/NewSQL/YugabyteDbStorageStrategy.cs
```csharp
public sealed class YugabyteDbStorageStrategy : DatabaseStorageStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override StorageTier Tier;;
    public override DatabaseCategory DatabaseCategory;;
    public override string Engine;;
    public override StorageCapabilities Capabilities;;
    public override bool SupportsTransactions;;
    public override bool SupportsSql;;
    protected override async Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task ConnectCoreAsync(CancellationToken ct);
    protected override async Task DisconnectCoreAsync(CancellationToken ct);
    protected override async Task EnsureSchemaCoreAsync(CancellationToken ct);
    protected override async Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);
    protected override async Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);
    protected override async Task<long> DeleteCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct);
    protected override async IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct);
    protected override async Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);
    protected override async Task<bool> CheckHealthCoreAsync(CancellationToken ct);
    protected override async ValueTask DisposeAsyncCore();
}
```
