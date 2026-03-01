# Plugin: UltimateDatabaseProtocol
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateDatabaseProtocol

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/UltimateDatabaseProtocolPlugin.cs
```csharp
public sealed class UltimateDatabaseProtocolPlugin : DataWarehouse.SDK.Contracts.Hierarchy.StoragePluginBase, IDisposable
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public IDatabaseProtocolStrategyRegistry Registry;;
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            // Main plugin capability
            new()
            {
                CapabilityId = $"{Id}.protocol",
                DisplayName = "Ultimate Database Protocol",
                Description = SemanticDescription,
                Category = SDK.Contracts.CapabilityCategory.Connector,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = [..SemanticTags]
            }
        };
        // Add strategy-based capabilities
        foreach (var strategy in _registry.GetAllStrategies())
        {
            var tags = new List<string>
            {
                "database-protocol",
                "connector",
                strategy.ProtocolInfo.Family.ToString().ToLowerInvariant()
            };
            if (strategy.ProtocolInfo.Capabilities.SupportsTransactions)
                tags.Add("transactions");
            if (strategy.ProtocolInfo.Capabilities.SupportsSsl)
                tags.Add("ssl");
            if (strategy.ProtocolInfo.Capabilities.SupportsStreaming)
                tags.Add("streaming");
            capabilities.Add(new() { CapabilityId = $"{Id}.{strategy.StrategyId.ToLowerInvariant().Replace(".", "-")}", DisplayName = strategy.StrategyName, Description = $"{strategy.StrategyName} wire protocol for {strategy.ProtocolInfo.ProtocolName}", Category = SDK.Contracts.CapabilityCategory.Connector, SubCategory = strategy.ProtocolInfo.Family.ToString(), PluginId = Id, PluginName = Name, PluginVersion = Version, Tags = [..tags] });
        }

        return capabilities.AsReadOnly();
    }
}
    public UltimateDatabaseProtocolPlugin();
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override Task OnStopCoreAsync();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    public IDatabaseProtocolStrategy? GetStrategy(string strategyId);
    public IReadOnlyCollection<IDatabaseProtocolStrategy> GetStrategiesByFamily(ProtocolFamily family);
    public IReadOnlyCollection<IDatabaseProtocolStrategy> ListStrategies();
    public async Task<IDatabaseProtocolStrategy> CreateConnectionAsync(string strategyId, ConnectionParameters parameters, CancellationToken ct = default);
    protected override void Dispose(bool disposing);
    public override Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);;
    public override Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);;
    public override Task DeleteAsync(string key, CancellationToken ct = default);;
    public override Task<bool> ExistsAsync(string key, CancellationToken ct = default);;
    public override async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public override Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);;
    public override Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/DatabaseProtocolStrategyBase.cs
```csharp
public record ProtocolCapabilities
{
}
    public bool SupportsTransactions { get; init; }
    public bool SupportsPreparedStatements { get; init; }
    public bool SupportsCursors { get; init; }
    public bool SupportsStreaming { get; init; }
    public bool SupportsBatch { get; init; }
    public bool SupportsNotifications { get; init; }
    public bool SupportsSsl { get; init; }
    public bool SupportsCompression { get; init; }
    public bool SupportsMultiplexing { get; init; }
    public bool SupportsServerCursors { get; init; }
    public bool SupportsBulkOperations { get; init; }
    public bool SupportsQueryCancellation { get; init; }
    public AuthenticationMethod[] SupportedAuthMethods { get; init; };
    public static ProtocolCapabilities StandardRelational;;
    public static ProtocolCapabilities StandardNoSql;;
}
```
```csharp
public record ProtocolInfo
{
}
    public string ProtocolName { get; init; };
    public string ProtocolVersion { get; init; };
    public int DefaultPort { get; init; }
    public ProtocolFamily Family { get; init; }
    public ProtocolCapabilities Capabilities { get; init; };
    public int MaxPacketSize { get; init; };
    public IReadOnlyDictionary<string, object> Parameters { get; init; };
}
```
```csharp
public record ConnectionParameters
{
}
    public required string Host { get; init; }
    public int? Port { get; init; }
    public string? Database { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public bool UseSsl { get; init; }
    public X509Certificate2? ClientCertificate { get; init; }
    public int ConnectionTimeoutMs { get; init; };
    public int CommandTimeout { get; init; };
    public int CommandTimeoutMs { get; init; };
    public AuthenticationMethod AuthMethod { get; init; };
    public IReadOnlyDictionary<string, string> AdditionalParameters { get; init; };
    public IReadOnlyDictionary<string, object?>? ExtendedProperties { get; init; }
}
```
```csharp
public record QueryResult
{
}
    public bool Success { get; init; }
    public long RowsAffected { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; };
    public IReadOnlyList<ColumnMetadata> Columns { get; init; };
    public long ExecutionTimeMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; };
}
```
```csharp
public record ColumnMetadata
{
}
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public int Ordinal { get; init; }
    public bool IsNullable { get; init; };
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
}
```
```csharp
public sealed class ProtocolStatistics
{
}
    public long ConnectionsEstablished { get; init; }
    public long ConnectionsFailed { get; init; }
    public long QueriesExecuted { get; init; }
    public long QueriesFailed { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
    public long TransactionsStarted { get; init; }
    public long TransactionsCommitted { get; init; }
    public long TransactionsRolledBack { get; init; }
    public double AverageQueryTimeMs { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime LastUpdateTime { get; init; }
    public static ProtocolStatistics Empty;;
}
```
```csharp
public interface IDatabaseProtocolStrategy
{
}
    string StrategyId { get; }
    string StrategyName { get; }
    ProtocolInfo ProtocolInfo { get; }
    Task<bool> ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default);;
    Task DisconnectAsync(CancellationToken ct = default);;
    Task<QueryResult> ExecuteQueryAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    Task<QueryResult> ExecuteNonQueryAsync(string command, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);;
    Task<string> BeginTransactionAsync(CancellationToken ct = default);;
    Task CommitTransactionAsync(string transactionId, CancellationToken ct = default);;
    Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default);;
    ProtocolConnectionState ConnectionState { get; }
    ProtocolStatistics GetStatistics();;
    void ResetStatistics();;
    Task<bool> PingAsync(CancellationToken ct = default);;
}
```
```csharp
public abstract class DatabaseProtocolStrategyBase : StrategyBase, IDatabaseProtocolStrategy
{
#endregion
}
    protected TcpClient? TcpClient;
    protected NetworkStream? NetworkStream;
    protected SslStream? SslStream;
    protected Stream? ActiveStream;
    protected ConnectionParameters? CurrentParameters;
    protected readonly BoundedDictionary<string, object> ActiveTransactions = new BoundedDictionary<string, object>(1000);
    protected static byte[] RentBuffer(int size);;
    protected static void ReturnBuffer(byte[] buffer);;
    public abstract override string StrategyId { get; }
    public abstract string StrategyName { get; }
    public override string Name;;
    public abstract ProtocolInfo ProtocolInfo { get; }
    public ProtocolConnectionState ConnectionState;;
    protected DatabaseProtocolStrategyBase();
    public virtual async Task<bool> ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default);
    protected virtual async Task EstablishSslAsync(ConnectionParameters parameters, CancellationToken ct);
    protected virtual Task NotifySslUpgradeAsync(CancellationToken ct);;
    protected virtual bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors);
    protected virtual X509Certificate? SelectClientCertificate(object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers);
    protected abstract Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);;
    protected abstract Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);;
    public virtual async Task DisconnectAsync(CancellationToken ct = default);
    protected virtual Task SendDisconnectMessageAsync(CancellationToken ct);;
    protected virtual async Task CleanupConnectionAsync();
    public virtual async Task<QueryResult> ExecuteQueryAsync(string query, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);
    protected abstract Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);;
    public virtual async Task<QueryResult> ExecuteNonQueryAsync(string command, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);
    protected abstract Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);;
    public virtual async Task<string> BeginTransactionAsync(CancellationToken ct = default);
    protected virtual async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    public virtual async Task CommitTransactionAsync(string transactionId, CancellationToken ct = default);
    protected virtual async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    public virtual async Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default);
    protected virtual async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected async Task SendAsync(byte[] data, CancellationToken ct);
    protected async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    protected async Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken ct);
    protected async Task<byte[]> ReceiveExactAsync(int count, CancellationToken ct);
    protected static void WriteInt32BE(Span<byte> buffer, int value);
    protected static int ReadInt32BE(ReadOnlySpan<byte> buffer);
    protected static void WriteInt32LE(Span<byte> buffer, int value);
    protected static int ReadInt32LE(ReadOnlySpan<byte> buffer);
    protected static void WriteInt16BE(Span<byte> buffer, short value);
    protected static short ReadInt16BE(ReadOnlySpan<byte> buffer);
    protected static int WriteNullTerminatedString(Span<byte> buffer, string value);
    protected static string ReadNullTerminatedString(ReadOnlySpan<byte> buffer, out int bytesRead);
    public virtual ProtocolStatistics GetStatistics();
    public virtual void ResetStatistics();
    public virtual async Task<bool> PingAsync(CancellationToken ct = default);
    protected virtual async Task<bool> PingCoreAsync(CancellationToken ct);
    public virtual KnowledgeObject GetStrategyKnowledge();
    protected virtual string GetStrategyDescription();;
    protected virtual Dictionary<string, object> GetKnowledgePayload();;
    protected virtual string[] GetKnowledgeTags();;
    public virtual RegisteredCapability GetStrategyCapability();
    protected void EnsureConnected();
    protected override async ValueTask DisposeAsyncCore();
    protected override void Dispose(bool disposing);
}
```
```csharp
public interface IDatabaseProtocolStrategyRegistry
{
}
    void Register(IDatabaseProtocolStrategy strategy);;
    IDatabaseProtocolStrategy? GetStrategy(string strategyId);;
    IReadOnlyCollection<IDatabaseProtocolStrategy> GetAllStrategies();;
    IReadOnlyCollection<IDatabaseProtocolStrategy> GetStrategiesByFamily(ProtocolFamily family);;
    void DiscoverStrategies(params System.Reflection.Assembly[] assemblies);;
}
```
```csharp
public sealed class DatabaseProtocolStrategyRegistry : IDatabaseProtocolStrategyRegistry
{
}
    public void Register(IDatabaseProtocolStrategy strategy);
    public IDatabaseProtocolStrategy? GetStrategy(string strategyId);
    public IReadOnlyCollection<IDatabaseProtocolStrategy> GetAllStrategies();
    public IReadOnlyCollection<IDatabaseProtocolStrategy> GetStrategiesByFamily(ProtocolFamily family);
    public void DiscoverStrategies(params System.Reflection.Assembly[] assemblies);
}
```
```csharp
public static class ArrayPool<T>
{
}
    public static System.Buffers.ArrayPool<T> Shared;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Infrastructure/ProtocolCompression.cs
```csharp
public sealed record CompressionOptions
{
}
    public CompressionAlgorithm Algorithm { get; init; };
    public ProtocolCompressionLevel Level { get; init; };
    public int MinimumSizeThreshold { get; init; };
    public int MaximumSizeThreshold { get; init; };
    public bool EnableStreaming { get; init; };
    public int StreamingBufferSize { get; init; };
    public bool IncludeHeader { get; init; };
    public static CompressionOptions NoCompression;;
    public static CompressionOptions Fast;;
    public static CompressionOptions Balanced;;
    public static CompressionOptions Best;;
}
```
```csharp
public sealed record CompressionStatistics
{
}
    public long TotalUncompressedBytes { get; init; }
    public long TotalCompressedBytes { get; init; }
    public long CompressionOperations { get; init; }
    public long DecompressionOperations { get; init; }
    public long CompressionTimeMs { get; init; }
    public long DecompressionTimeMs { get; init; }
    public long SkippedOperations { get; init; }
    public long FailedOperations { get; init; }
    public double AverageCompressionRatio;;
    public long BytesSaved;;
    public double CompressionThroughputMBps;;
}
```
```csharp
public readonly record struct CompressionResult
{
}
    public bool WasCompressed { get; init; }
    public int OriginalSize { get; init; }
    public int CompressedSize { get; init; }
    public double CompressionRatio;;
    public CompressionAlgorithm Algorithm { get; init; }
    public long ElapsedMs { get; init; }
}
```
```csharp
public sealed class ProtocolCompressionManager : IDisposable
{
}
    public ProtocolCompressionManager(CompressionOptions? defaultOptions = null);
    public (byte[] Data, CompressionResult Result) Compress(ReadOnlySpan<byte> data, CompressionOptions? options = null);
    public (byte[] Data, CompressionResult Result) Decompress(ReadOnlySpan<byte> data, CompressionAlgorithm algorithm);
    public async Task<CompressionResult> CompressToStreamAsync(Stream input, Stream output, CompressionOptions? options = null, CancellationToken ct = default);
    public async Task<CompressionResult> DecompressFromStreamAsync(Stream input, Stream output, CompressionAlgorithm algorithm, int bufferSize = 81920, CancellationToken ct = default);
    public static CompressionAlgorithm DetectAlgorithm(ReadOnlySpan<byte> data);
    public static CompressionAlgorithm GetBestAlgorithm(string contentType);
    public CompressionStatistics GetStatistics();
    public void ResetStatistics();
    public void Dispose();
}
```
```csharp
internal interface ICompressionProvider
{
}
    byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);;
    byte[] Decompress(ReadOnlySpan<byte> data);;
    Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);;
    Stream CreateDecompressionStream(Stream input, bool leaveOpen);;
}
```
```csharp
internal sealed class NoCompressionProvider : ICompressionProvider
{
}
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);;
    public byte[] Decompress(ReadOnlySpan<byte> data);;
    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);;
    public Stream CreateDecompressionStream(Stream input, bool leaveOpen);;
}
```
```csharp
internal sealed class GZipCompressionProvider : ICompressionProvider
{
}
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);
    public byte[] Decompress(ReadOnlySpan<byte> data);
    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);;
    public Stream CreateDecompressionStream(Stream input, bool leaveOpen);;
}
```
```csharp
internal sealed class DeflateCompressionProvider : ICompressionProvider
{
}
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);
    public byte[] Decompress(ReadOnlySpan<byte> data);
    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);;
    public Stream CreateDecompressionStream(Stream input, bool leaveOpen);;
}
```
```csharp
internal sealed class BrotliCompressionProvider : ICompressionProvider
{
}
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);
    public byte[] Decompress(ReadOnlySpan<byte> data);
    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);;
    public Stream CreateDecompressionStream(Stream input, bool leaveOpen);;
}
```
```csharp
internal sealed class LZ4CompressionProvider : ICompressionProvider
{
}
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);;
    public byte[] Decompress(ReadOnlySpan<byte> data);;
    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);;
    public Stream CreateDecompressionStream(Stream input, bool leaveOpen);;
}
```
```csharp
internal sealed class ZstdCompressionProvider : ICompressionProvider
{
}
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);;
    public byte[] Decompress(ReadOnlySpan<byte> data);;
    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);;
    public Stream CreateDecompressionStream(Stream input, bool leaveOpen);;
}
```
```csharp
internal sealed class SnappyCompressionProvider : ICompressionProvider
{
}
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);;
    public byte[] Decompress(ReadOnlySpan<byte> data);;
    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);;
    public Stream CreateDecompressionStream(Stream input, bool leaveOpen);;
}
```
```csharp
internal sealed class LZOCompressionProvider : ICompressionProvider
{
}
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);;
    public byte[] Decompress(ReadOnlySpan<byte> data);;
    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);;
    public Stream CreateDecompressionStream(Stream input, bool leaveOpen);;
}
```
```csharp
internal sealed class PassthroughStream : Stream
{
}
    public PassthroughStream(Stream inner, bool leaveOpen);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush();;
    public override int Read(byte[] buffer, int offset, int count);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
    protected override void Dispose(bool disposing);
}
```
```csharp
public static class ProtocolCompressionExtensions
{
}
    public static byte[] CompressIfBeneficial(this ProtocolCompressionManager compression, byte[] data, out CompressionAlgorithm usedAlgorithm);
    public static Stream WrapWithCompression(this ProtocolCompressionManager compression, Stream stream, CompressionOptions? options = null);
}
```
```csharp
internal sealed class CountingStream : Stream
{
}
    public CountingStream(Stream inner);;
    public long BytesRead;;
    public long BytesWritten;;
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count);
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
    public override void Write(byte[] buffer, int offset, int count);
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct);
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
    public override void Flush();;
    public override Task FlushAsync(CancellationToken ct);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    protected override void Dispose(bool disposing);
    public override ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Infrastructure/ConnectionPoolManager.cs
```csharp
public sealed record ConnectionPoolOptions
{
}
    public int MinPoolSize { get; init; };
    public int MaxPoolSize { get; init; };
    public int AcquireTimeoutMs { get; init; };
    public int IdleTimeoutMs { get; init; };
    public int MaxLifetimeMs { get; init; };
    public int HealthCheckIntervalMs { get; init; };
    public bool ValidateOnAcquire { get; init; };
    public bool ValidateOnRelease { get; init; };
    public bool EnableWarmUp { get; init; };
    public int WarmUpCount { get; init; };
}
```
```csharp
public sealed record ConnectionPoolStatistics
{
}
    public int AvailableConnections { get; init; }
    public int InUseConnections { get; init; }
    public long TotalConnectionsCreated { get; init; }
    public long TotalConnectionsDestroyed { get; init; }
    public long TotalAcquisitions { get; init; }
    public long FailedAcquisitions { get; init; }
    public long ConnectionsRecycled { get; init; }
    public long IdleConnectionsRemoved { get; init; }
    public long FailedHealthChecks { get; init; }
    public double AverageAcquireTimeMs { get; init; }
    public int PeakInUseConnections { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastUpdated { get; init; }
}
```
```csharp
internal sealed class PooledConnection<TConnection>
    where TConnection : IDatabaseProtocolStrategy, IDisposable
{
}
    public TConnection Connection { get; }
    public DateTime CreatedAt { get; }
    public DateTime LastUsedAt;;
    public int UseCount;;
    public bool IsInUse;;
    public string PoolKey { get; }
    public PooledConnection(TConnection connection, string poolKey);
    public void MarkInUse();
    public void MarkAvailable();
    public bool IsExpired(int maxLifetimeMs);;
    public bool IsIdle(int idleTimeoutMs);;
}
```
```csharp
public sealed class ConnectionPoolManager<TConnection> : IDisposable, IAsyncDisposable where TConnection : IDatabaseProtocolStrategy, IDisposable
{
}
    public ConnectionPoolManager(Func<ConnectionParameters, Task<TConnection>> connectionFactory, ConnectionPoolOptions? defaultOptions = null);
    public ConnectionPool GetOrCreatePool(ConnectionParameters parameters, ConnectionPoolOptions? options = null);
    public async Task<TConnection> AcquireAsync(ConnectionParameters parameters, CancellationToken ct = default);
    public async Task ReleaseAsync(TConnection connection, CancellationToken ct = default);
    public ConnectionPoolStatistics GetStatistics();
    public async Task ClearAllPoolsAsync(CancellationToken ct = default);
    internal void OnConnectionCreated();;
    internal void OnConnectionDestroyed();;
    internal void OnConnectionRecycled();;
    internal void OnIdleConnectionRemoved();;
    internal void OnHealthCheckFailed();;
    public void Dispose();
    public async ValueTask DisposeAsync();
    public sealed class ConnectionPool : IDisposable, IAsyncDisposable;
}
```
```csharp
public sealed class ConnectionPool : IDisposable, IAsyncDisposable
{
}
    internal ConnectionPool(string key, ConnectionParameters parameters, ConnectionPoolOptions options, Func<ConnectionParameters, Task<TConnection>> connectionFactory, ConnectionPoolManager<TConnection> manager);
    public async Task<TConnection> AcquireAsync(CancellationToken ct = default);
    public async Task<bool> TryReleaseAsync(TConnection connection, CancellationToken ct = default);
    public async Task PerformMaintenanceAsync(CancellationToken ct);
    public ConnectionPoolStatistics GetStatistics();
    public async Task ClearAsync(CancellationToken ct = default);
    public void Dispose();
    public async ValueTask DisposeAsync();
}
```
```csharp
public static class ConnectionPoolExtensions
{
}
    public static async Task<PooledConnectionScope<TConnection>> AcquireScopedAsync<TConnection>(this ConnectionPoolManager<TConnection> pool, ConnectionParameters parameters, CancellationToken ct = default)
    where TConnection : IDatabaseProtocolStrategy, IDisposable;
}
```
```csharp
public readonly struct PooledConnectionScope<TConnection> : IAsyncDisposable where TConnection : IDatabaseProtocolStrategy, IDisposable
{
}
    public TConnection Connection { get; }
    internal PooledConnectionScope(ConnectionPoolManager<TConnection> pool, TConnection connection);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Messaging/MessageQueueProtocolStrategies.cs
```csharp
public sealed class KafkaProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```
```csharp
public sealed class RabbitMqProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);
}
```
```csharp
private sealed class AmqpFrame
{
}
    public byte Type { get; init; }
    public ushort Channel { get; init; }
    public byte[] Payload { get; init; };
}
```
```csharp
public sealed class NatsProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class PulsarProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Relational/MySqlProtocolStrategy.cs
```csharp
public sealed class MySqlProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task NotifySslUpgradeAsync(CancellationToken ct);
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Relational/PostgreSqlTypeMapping.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 89: PostgreSQL OID type mapping (ECOS-02)")]
public static class PostgreSqlTypeMapping
{
}
    public static int ToOid(ColumnDataType type);;
    public static ColumnDataType FromOid(int oid);;
    public static string ToPostgresTypeName(ColumnDataType type);;
    public static short GetTypeSize(int oid);;
    public static int GetTypeModifier(int oid);;
    public static short GetFormatCode(int oid);;
    public static byte[] SerializeValue(object? value, ColumnDataType type, bool binaryFormat);
    public static object? DeserializeValue(ReadOnlySpan<byte> data, int oid, bool binaryFormat);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Relational/PostgreSqlWireVerification.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 89: PostgreSQL wire verification (ECOS-01)")]
public sealed class PostgreSqlWireVerification
{
}
    public PostgreSqlProtocolCoverage GetProtocolCoverage();
    public StartupSequenceVerification VerifyStartupSequence(Stream mockStream);
    public ExtendedQuerySequenceVerification VerifyExtendedQuerySequence(Stream mockStream);
    public IReadOnlyDictionary<string, string> GetCompatibilityMatrix();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Relational/PostgreSqlSqlEngineIntegration.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 89: PostgreSQL SQL integration (ECOS-02)")]
public sealed class PostgreSqlSqlEngineIntegration : IDataSourceProvider
{
}
    public char TransactionStatus;;
    public PostgreSqlSqlEngineIntegration(IDataSourceProvider vdeDataSource, PostgreSqlCatalogProvider catalogProvider);
    public async Task<PostgreSqlQueryResult> ExecuteSimpleQueryAsync(string sql, CancellationToken ct);
    public async Task<PostgreSqlQueryResult> ExecuteExtendedQueryAsync(ParsedStatement parsed, BindParameters bind, int maxRows, CancellationToken ct);
    public Task<PreparedStatementDescription> PrepareStatementAsync(string name, string sql, int[] paramOids, CancellationToken ct);
    public ParsedStatement? GetPreparedStatement(string name);
    public void ClosePreparedStatement(string name);
    public Task BeginTransactionAsync();
    public Task CommitTransactionAsync();
    public Task RollbackTransactionAsync();
    public IAsyncEnumerable<ColumnarBatch> GetTableData(string tableName, List<string>? columns, CancellationToken ct);
    public TableStatistics? GetTableStatistics(string tableName);
}
```
```csharp
private sealed class VdeStatisticsProvider : ITableStatisticsProvider
{
}
    public VdeStatisticsProvider(IDataSourceProvider source);;
    public TableStatistics? GetStatistics(string tableName);;
}
```
```csharp
public sealed class PostgreSqlQueryResult
{
}
    public required IReadOnlyList<PostgreSqlFieldDescription> RowDescription { get; init; }
    public required IReadOnlyList<List<byte[]?>> DataRows { get; init; }
    public required string CommandTag { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public bool PortalSuspended { get; init; }
}
```
```csharp
public sealed class BindParameters
{
}
    public IReadOnlyList<object?> Values { get; init; };
    public IReadOnlyList<short> FormatCodes { get; init; };
    public IReadOnlyList<short> ResultFormatCodes { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Relational/PostgreSqlCatalogProvider.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 89: PostgreSQL catalog provider (ECOS-02)")]
public sealed class PostgreSqlCatalogProvider
{
}
    public Func<CancellationToken, Task<IReadOnlyList<string>>>? GetTableNamesAsync { get; set; }
    public Func<string, CancellationToken, Task<IReadOnlyList<CatalogColumnInfo>>>? GetTableColumnsAsync { get; set; }
    public PostgreSqlCatalogProvider(IDataSourceProvider vdeDataSource);
    public bool IsCatalogQuery(string sql);
    public async Task<PostgreSqlQueryResult> HandleCatalogQuery(string sql, CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Relational/OracleProtocolStrategy.cs
```csharp
public sealed class OracleTnsProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```
```csharp
private sealed class TnsPacket
{
}
    public byte Type { get; init; }
    public byte[] Data { get; init; };
}
```
```csharp
public sealed class Db2DrdaProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```
```csharp
private sealed class DrdaObject
{
}
    public ushort CodePoint { get; init; }
    public byte[] Data { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Relational/PostgreSqlProtocolStrategy.cs
```csharp
public sealed class PostgreSqlProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    public char TransactionStatus;;
    public IReadOnlyDictionary<string, string> ServerParameters;;
    public int BackendProcessId;;
    protected override async Task NotifySslUpgradeAsync(CancellationToken ct);
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    public async Task SendCancelRequestAsync(string host, int port, CancellationToken ct);
    public async Task<long> CopyInAsync(string copyCommand, Func<IAsyncEnumerable<byte[]>> dataProvider, CancellationToken ct);
    public async IAsyncEnumerable<byte[]> CopyOutAsync(string copyCommand, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct);
    public bool TryGetNotification(out PostgreSqlNotification? notification);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Relational/TdsProtocolStrategy.cs
```csharp
public sealed class TdsProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task NotifySslUpgradeAsync(CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Graph/AdditionalGraphStrategies.cs
```csharp
public sealed class ArangoDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```
```csharp
public sealed class JanusGraphProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```
```csharp
public sealed class TigerGraphProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Graph/GremlinProtocolStrategy.cs
```csharp
public sealed class GremlinProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
private sealed class GremlinRequest
{
}
    public Guid RequestId { get; set; }
    public string Op { get; set; };
    public string Processor { get; set; };
    public Dictionary<string, object> Args { get; set; };
}
```
```csharp
private sealed class GremlinResponse
{
}
    public Guid RequestId { get; set; }
    public GremlinStatus Status { get; set; };
    public GremlinResult? Result { get; set; }
}
```
```csharp
private sealed class GremlinStatus
{
}
    public int Code { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, object>? Attributes { get; set; }
}
```
```csharp
private sealed class GremlinResult
{
}
    public List<object?>? Data { get; set; }
    public Dictionary<string, object>? Meta { get; set; }
}
```
```csharp
public sealed class NeptuneGremlinProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Graph/Neo4jBoltProtocolStrategy.cs
```csharp
public sealed class Neo4jBoltProtocolStrategy : DatabaseProtocolStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Virtualization/SqlOverObjectProtocolStrategy.cs
```csharp
public sealed class SqlOverObjectProtocolStrategy : DatabaseProtocolStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    public void RegisterTable(string tableName, VirtualTableSchema schema);
    public bool UnregisterTable(string tableName);
    public IReadOnlyList<string> ListTables();
    public void SetFileReader(Func<string, CancellationToken, Task<Stream?>> fileReader);
    public VirtualTableSchema InferCsvSchema(string tableName, string csvHeader, char delimiter = ',');
    public VirtualTableSchema InferJsonSchema(string tableName, string sampleJson);
    public long TotalQueriesExecuted;;
    public long TotalBytesScanned;;
    public long CacheHits;;
    public long CacheMisses;;
    public string SupportedSqlDialect;;
    public IReadOnlyList<string> SupportedFileFormats;;
    public sealed class VirtualTableSchema;
    public sealed class VirtualColumn;
}
```
```csharp
public sealed class VirtualTableSchema
{
}
    public string TableName { get; init; };
    public string Format { get; init; };
    public string? SourcePath { get; init; }
    public List<VirtualColumn> Columns { get; init; };
}
```
```csharp
public sealed class VirtualColumn
{
}
    public string Name { get; init; };
    public string DataType { get; init; };
    public bool IsNullable { get; init; };
    public bool IsPartitionKey { get; init; }
}
```
```csharp
private sealed class CachedTableData
{
}
    public List<Dictionary<string, object?>> Rows { get; init; };
    public DateTime ExpiresAt { get; init; }
}
```
```csharp
private sealed class CachedQueryResult
{
}
    public QueryResult Result { get; init; };
    public DateTime ExpiresAt { get; init; }
}
```
```csharp
private sealed class PartitionMetadata
{
}
    public string TableName { get; init; };
    public List<string> PartitionKeys { get; init; };
    public List<Dictionary<string, string>> Partitions { get; init; };
}
```
```csharp
private sealed class ParsedQuery
{
}
    public string TableName { get; set; };
    public List<string>? SelectedColumns { get; set; }
    public string? WhereClause { get; set; }
    public List<string>? GroupByColumns { get; set; }
    public List<string>? OrderByColumns { get; set; }
    public int Limit { get; set; }
    public List<AggregationFunction>? AggregationFunctions { get; set; }
}
```
```csharp
private sealed class AggregationFunction
{
}
    public string Function { get; init; };
    public string Column { get; init; };
    public string Alias { get; init; };
}
```
```csharp
private sealed class WhereCondition
{
}
    public string Column { get; init; };
    public string Operator { get; init; };
    public string Value { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/CloudDW/CloudDataWarehouseStrategies.cs
```csharp
public sealed class SnowflakeProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class BigQueryProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class RedshiftProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```
```csharp
public sealed class DatabricksProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class SynapseProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Search/ElasticsearchProtocolStrategy.cs
```csharp
public sealed class ElasticsearchProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
private sealed class ElasticsearchOperation
{
}
    public OperationType Type { get; init; }
    public string Endpoint { get; init; };
    public HttpMethod Method { get; init; };
    public string? Body { get; init; }
    public string? ContentType { get; init; }
}
```
```csharp
public sealed class OpenSearchProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Search/AdditionalSearchStrategies.cs
```csharp
public sealed class SolrProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```
```csharp
public sealed class MeiliSearchProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```
```csharp
public sealed class TypesenseProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Specialized/SpecializedProtocolStrategies.cs
```csharp
public sealed class ClickHouseProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```
```csharp
public sealed class HBaseProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class CouchbaseProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class DruidProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class PrestoProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/NoSQL/CassandraCqlProtocolStrategy.cs
```csharp
public sealed class CassandraCqlProtocolStrategy : DatabaseProtocolStrategyBase
{
#endregion
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/NoSQL/RedisRespProtocolStrategy.cs
```csharp
public sealed class RedisRespProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
private sealed class RedisException : Exception
{
}
    public string ErrorType { get; }
    public RedisException(string message) : base(message);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/NoSQL/MongoDbWireProtocolStrategy.cs
```csharp
public sealed class MongoDbWireProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/NoSQL/MemcachedProtocolStrategy.cs
```csharp
public sealed class MemcachedProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
private sealed class BinaryResponse
{
}
    public byte Opcode { get; init; }
    public ushort Status { get; init; }
    public uint Opaque { get; init; }
    public ulong Cas { get; init; }
    public uint Flags { get; init; }
    public string? Key { get; init; }
    public byte[]? Value { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/TimeSeries/TimeSeriesProtocolStrategies.cs
```csharp
public sealed class InfluxDbLineProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);;
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```
```csharp
public sealed class QuestDbIlpProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);;
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);;
    protected override Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);
}
```
```csharp
public sealed class TimescaleDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);;
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);;
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);;
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class PrometheusRemoteWriteStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);;
    protected override Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```
```csharp
public sealed class VictoriaMetricsProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);;
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Embedded/EmbeddedDatabaseStrategies.cs
```csharp
public sealed class SqliteProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class DuckDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class LevelDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```
```csharp
public sealed class RocksDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override bool IsProductionReady;;
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```
```csharp
public sealed class BerkeleyDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);
    protected override Task CleanupConnectionAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/Driver/DriverProtocolStrategies.cs
```csharp
public sealed class AdoNetProviderStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class JdbcBridgeProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
private class JdbcResponse
{
}
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? SessionId { get; set; }
    public string? TransactionId { get; set; }
    public long RowsAffected { get; set; }
}
```
```csharp
private sealed class JdbcQueryResponse : JdbcResponse
{
}
    public List<Dictionary<string, object?>>? Rows { get; set; }
}
```
```csharp
public sealed class OdbcDriverProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateDatabaseProtocol/Strategies/NewSQL/NewSqlProtocolStrategies.cs
```csharp
public sealed class CockroachDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
    protected override async Task CleanupConnectionAsync();
}
```
```csharp
public sealed class TiDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```
```csharp
public sealed class YugabyteDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```
```csharp
public sealed class VoltDbProtocolStrategy : DatabaseProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override ProtocolInfo ProtocolInfo;;
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct);
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(string query, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(string command, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct);
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct);
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct);
    protected override Task SendDisconnectMessageAsync(CancellationToken ct);
    protected override async Task<bool> PingCoreAsync(CancellationToken ct);
}
```
