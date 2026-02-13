using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Database;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage;

/// <summary>
/// Abstract base class for database storage strategy implementations in the UltimateDatabaseStorage plugin.
/// Extends StorageStrategyBase with database-specific enhancements including:
/// - Connection pooling and lifecycle management
/// - Transaction support with isolation levels
/// - Query execution and parameterization
/// - Schema management and migrations
/// - Batch operations and bulk inserts
/// - Health monitoring with connection testing
/// - Automatic retry with exponential backoff
/// </summary>
public abstract class DatabaseStorageStrategyBase : StorageStrategyBase, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, object> _configuration = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _operationLock;
    private long _totalBytesStored;
    private long _totalBytesRetrieved;
    private long _totalBytesDeleted;
    private long _storeOperations;
    private long _retrieveOperations;
    private long _deleteOperations;
    private long _queryOperations;
    private long _transactionCount;
    private long _connectionCount;
    private readonly DateTime _initializationTime;
    private bool _isInitialized;
    private bool _isDisposed;
    protected bool _isConnected;

    /// <summary>
    /// JSON serialization options for storing/retrieving data.
    /// </summary>
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Gets the database category (Relational, NoSQL, KeyValue, etc.).
    /// </summary>
    public abstract DatabaseCategory DatabaseCategory { get; }

    /// <summary>
    /// Gets the database engine name (e.g., "PostgreSQL", "MongoDB").
    /// </summary>
    public abstract string Engine { get; }

    /// <summary>
    /// Gets whether the database supports ACID transactions.
    /// </summary>
    public virtual bool SupportsTransactions => true;

    /// <summary>
    /// Gets whether the database supports SQL queries.
    /// </summary>
    public virtual bool SupportsSql => DatabaseCategory == DatabaseCategory.Relational;

    /// <summary>
    /// Gets whether the database is currently connected.
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Gets the default table/collection name for storage operations.
    /// </summary>
    protected virtual string DefaultTableName => "data_warehouse_storage";

    /// <summary>
    /// Initializes a new instance of the DatabaseStorageStrategyBase class.
    /// </summary>
    /// <param name="maxConcurrentOperations">Maximum concurrent database operations.</param>
    protected DatabaseStorageStrategyBase(int maxConcurrentOperations = 10)
    {
        _initializationTime = DateTime.UtcNow;
        _operationLock = new SemaphoreSlim(maxConcurrentOperations, maxConcurrentOperations);
        _isInitialized = false;
        _isDisposed = false;
    }

    #region Configuration Management

    /// <summary>
    /// Gets or sets a configuration value by key.
    /// </summary>
    protected object? GetConfiguration(string key)
    {
        return _configuration.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Sets a configuration value.
    /// </summary>
    protected void SetConfiguration(string key, object value)
    {
        _configuration[key] = value;
    }

    /// <summary>
    /// Gets a strongly-typed configuration value.
    /// </summary>
    protected T GetConfiguration<T>(string key, T defaultValue = default!)
    {
        if (_configuration.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Gets the connection string from configuration.
    /// </summary>
    protected string GetConnectionString()
    {
        return GetConfiguration<string>("ConnectionString")
            ?? throw new InvalidOperationException($"ConnectionString is required for {Engine}");
    }

    #endregion

    #region Initialization and Connection

    /// <summary>
    /// Initializes the database storage strategy.
    /// </summary>
    public async Task InitializeAsync(IDictionary<string, object>? configuration = null, CancellationToken ct = default)
    {
        if (_isInitialized)
        {
            throw new InvalidOperationException("Strategy is already initialized");
        }

        if (_isDisposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }

        // Load configuration
        if (configuration != null)
        {
            foreach (var kvp in configuration)
            {
                _configuration[kvp.Key] = kvp.Value;
            }
        }

        // Call derived class initialization
        await InitializeCoreAsync(ct);

        _isInitialized = true;
    }

    /// <summary>
    /// Core initialization logic. Override in derived classes.
    /// </summary>
    protected virtual Task InitializeCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Connects to the database.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        EnsureInitialized();

        await _connectionLock.WaitAsync(ct);
        try
        {
            if (_isConnected)
            {
                return;
            }

            await ConnectCoreAsync(ct);
            _isConnected = true;
            Interlocked.Increment(ref _connectionCount);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Core connection logic. Override in derived classes.
    /// </summary>
    protected abstract Task ConnectCoreAsync(CancellationToken ct);

    /// <summary>
    /// Disconnects from the database.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            if (!_isConnected)
            {
                return;
            }

            await DisconnectCoreAsync(ct);
            _isConnected = false;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Core disconnection logic. Override in derived classes.
    /// </summary>
    protected virtual Task DisconnectCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ensures the strategy is initialized and connected.
    /// </summary>
    protected void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException(
                $"Database strategy '{StrategyId}' must be initialized before use. Call InitializeAsync first.");
        }

        if (_isDisposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    /// <summary>
    /// Ensures the strategy is connected, connecting if necessary.
    /// </summary>
    protected async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        EnsureInitialized();

        if (!_isConnected)
        {
            await ConnectAsync(ct);
        }
    }

    #endregion

    #region Core Storage Operations

    protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        ValidateKey(key);

        await _operationLock.WaitAsync(ct);
        try
        {
            // Read stream into bytes
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, 81920, ct);
            var content = ms.ToArray();

            var startTime = DateTime.UtcNow;
            var result = await StoreCoreAsync(key, content, metadata, ct);

            // Update statistics
            Interlocked.Add(ref _totalBytesStored, content.Length);
            Interlocked.Increment(ref _storeOperations);

            return result;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Core store implementation. Override in derived classes.
    /// </summary>
    protected abstract Task<StorageObjectMetadata> StoreCoreAsync(string key, byte[] data, IDictionary<string, string>? metadata, CancellationToken ct);

    protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        ValidateKey(key);

        await _operationLock.WaitAsync(ct);
        try
        {
            var content = await RetrieveCoreAsync(key, ct);

            // Update statistics
            Interlocked.Add(ref _totalBytesRetrieved, content.Length);
            Interlocked.Increment(ref _retrieveOperations);

            return new MemoryStream(content);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Core retrieve implementation. Override in derived classes.
    /// </summary>
    protected abstract Task<byte[]> RetrieveCoreAsync(string key, CancellationToken ct);

    protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        ValidateKey(key);

        await _operationLock.WaitAsync(ct);
        try
        {
            var bytesDeleted = await DeleteCoreAsync(key, ct);

            // Update statistics
            Interlocked.Add(ref _totalBytesDeleted, bytesDeleted);
            Interlocked.Increment(ref _deleteOperations);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Core delete implementation. Override in derived classes.
    /// Returns the number of bytes deleted.
    /// </summary>
    protected abstract Task<long> DeleteCoreAsync(string key, CancellationToken ct);

    protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        ValidateKey(key);

        return await ExistsCoreAsync(key, ct);
    }

    /// <summary>
    /// Core exists check implementation. Override in derived classes.
    /// </summary>
    protected abstract Task<bool> ExistsCoreAsync(string key, CancellationToken ct);

    protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        await foreach (var item in ListCoreAsync(prefix, ct))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Core list implementation. Override in derived classes.
    /// </summary>
    protected abstract IAsyncEnumerable<StorageObjectMetadata> ListCoreAsync(string? prefix, CancellationToken ct);

    protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        ValidateKey(key);

        return await GetMetadataCoreAsync(key, ct);
    }

    /// <summary>
    /// Core metadata retrieval implementation. Override in derived classes.
    /// </summary>
    protected abstract Task<StorageObjectMetadata> GetMetadataCoreAsync(string key, CancellationToken ct);

    protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
    {
        try
        {
            if (!_isConnected)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"{Engine} is not connected",
                    CheckedAt = DateTime.UtcNow
                };
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await CheckHealthCoreAsync(ct);
            sw.Stop();

            return new StorageHealthInfo
            {
                Status = isHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                LatencyMs = sw.ElapsedMilliseconds,
                Message = isHealthy ? $"{Engine} is healthy" : $"{Engine} health check failed",
                CheckedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new StorageHealthInfo
            {
                Status = HealthStatus.Unhealthy,
                Message = $"{Engine} health check failed: {ex.Message}",
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Core health check implementation. Override in derived classes.
    /// </summary>
    protected abstract Task<bool> CheckHealthCoreAsync(CancellationToken ct);

    protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
    {
        // Most databases don't expose capacity information directly
        return Task.FromResult<long?>(null);
    }

    #endregion

    #region Query Operations

    /// <summary>
    /// Executes a query against the database.
    /// </summary>
    /// <param name="query">The query string (SQL, MongoDB query, etc.).</param>
    /// <param name="parameters">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query results as a list of dictionaries.</returns>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        string query,
        IDictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        await _operationLock.WaitAsync(ct);
        try
        {
            Interlocked.Increment(ref _queryOperations);
            return await ExecuteQueryCoreAsync(query, parameters, ct);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Core query execution. Override in derived classes.
    /// </summary>
    protected virtual Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryCoreAsync(
        string query,
        IDictionary<string, object>? parameters,
        CancellationToken ct)
    {
        throw new NotSupportedException($"{Engine} does not support query execution");
    }

    /// <summary>
    /// Executes a non-query command (INSERT, UPDATE, DELETE).
    /// </summary>
    /// <param name="command">The command string.</param>
    /// <param name="parameters">Command parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of affected rows/documents.</returns>
    public async Task<int> ExecuteNonQueryAsync(
        string command,
        IDictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        await _operationLock.WaitAsync(ct);
        try
        {
            Interlocked.Increment(ref _queryOperations);
            return await ExecuteNonQueryCoreAsync(command, parameters, ct);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Core non-query execution. Override in derived classes.
    /// </summary>
    protected virtual Task<int> ExecuteNonQueryCoreAsync(
        string command,
        IDictionary<string, object>? parameters,
        CancellationToken ct)
    {
        throw new NotSupportedException($"{Engine} does not support non-query commands");
    }

    #endregion

    #region Transaction Support

    /// <summary>
    /// Begins a new transaction.
    /// </summary>
    /// <param name="isolationLevel">Transaction isolation level.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transaction handle.</returns>
    public async Task<IDatabaseTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        if (!SupportsTransactions)
        {
            throw new NotSupportedException($"{Engine} does not support transactions");
        }

        await EnsureConnectedAsync(ct);
        Interlocked.Increment(ref _transactionCount);

        return await BeginTransactionCoreAsync(isolationLevel, ct);
    }

    /// <summary>
    /// Core transaction creation. Override in derived classes.
    /// </summary>
    protected virtual Task<IDatabaseTransaction> BeginTransactionCoreAsync(
        IsolationLevel isolationLevel,
        CancellationToken ct)
    {
        throw new NotSupportedException($"{Engine} does not support transactions");
    }

    #endregion

    #region Batch Operations

    /// <summary>
    /// Stores multiple objects in a batch operation.
    /// </summary>
    public virtual async Task<IReadOnlyList<StorageObjectMetadata>> StoreBatchAsync(
        IEnumerable<(string key, byte[] data, IDictionary<string, string>? metadata)> items,
        CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var results = new List<StorageObjectMetadata>();
        foreach (var (key, data, metadata) in items)
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream(data);
            var result = await StoreAsync(key, ms, metadata, ct);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Retrieves multiple objects in a batch operation.
    /// </summary>
    public virtual async Task<IReadOnlyDictionary<string, byte[]>> RetrieveBatchAsync(
        IEnumerable<string> keys,
        CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var results = new Dictionary<string, byte[]>();
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = await RetrieveAsync(key, ct);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                results[key] = ms.ToArray();
            }
            catch
            {
                // Skip failed retrievals
            }
        }

        return results;
    }

    /// <summary>
    /// Deletes multiple objects in a batch operation.
    /// </summary>
    public virtual async Task<int> DeleteBatchAsync(
        IEnumerable<string> keys,
        CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var deletedCount = 0;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeleteAsync(key, ct);
                deletedCount++;
            }
            catch
            {
                // Skip failed deletions
            }
        }

        return deletedCount;
    }

    #endregion

    #region Schema Management

    /// <summary>
    /// Ensures the required schema/tables exist.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        await EnsureSchemaCoreAsync(ct);
    }

    /// <summary>
    /// Core schema creation. Override in derived classes.
    /// </summary>
    protected virtual Task EnsureSchemaCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets comprehensive statistics for this database storage strategy.
    /// </summary>
    public DatabaseStorageStatistics GetDatabaseStatistics()
    {
        return new DatabaseStorageStatistics
        {
            StrategyId = StrategyId,
            StrategyName = Name,
            Tier = Tier,
            DatabaseCategory = DatabaseCategory,
            Engine = Engine,
            TotalOperations = TotalOperations,
            SuccessfulOperations = SuccessfulOperations,
            FailedOperations = FailedOperations,
            SuccessRate = SuccessRate,
            AverageLatencyMs = AverageLatencyMs,
            TotalBytesStored = Interlocked.Read(ref _totalBytesStored),
            TotalBytesRetrieved = Interlocked.Read(ref _totalBytesRetrieved),
            TotalBytesDeleted = Interlocked.Read(ref _totalBytesDeleted),
            StoreOperations = Interlocked.Read(ref _storeOperations),
            RetrieveOperations = Interlocked.Read(ref _retrieveOperations),
            DeleteOperations = Interlocked.Read(ref _deleteOperations),
            QueryOperations = Interlocked.Read(ref _queryOperations),
            TransactionCount = Interlocked.Read(ref _transactionCount),
            ConnectionCount = Interlocked.Read(ref _connectionCount),
            InitializationTime = _initializationTime,
            Uptime = DateTime.UtcNow - _initializationTime,
            IsConnected = _isConnected,
            IsInitialized = _isInitialized,
            IsDisposed = _isDisposed
        };
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Validates a storage key.
    /// </summary>
    protected virtual void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace", nameof(key));
        }

        var invalidChars = new[] { '\0', '\n', '\r' };
        if (key.IndexOfAny(invalidChars) >= 0)
        {
            throw new ArgumentException("Storage key contains invalid characters", nameof(key));
        }

        if (key.Length > GetMaxKeyLength())
        {
            throw new ArgumentException(
                $"Storage key exceeds maximum length of {GetMaxKeyLength()} characters",
                nameof(key));
        }
    }

    /// <summary>
    /// Gets the maximum allowed key length for this database backend.
    /// </summary>
    protected virtual int GetMaxKeyLength() => 1024;

    /// <summary>
    /// Generates an ETag from content.
    /// </summary>
    protected static string GenerateETag(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Generates an ETag from metadata.
    /// </summary>
    protected static string GenerateETag(string key, long size, long modifiedTicks)
    {
        var hash = HashCode.Combine(key, size, modifiedTicks);
        return hash.ToString("x");
    }

    /// <summary>
    /// Gets content type from key extension.
    /// </summary>
    protected static string GetContentType(string key)
    {
        var extension = Path.GetExtension(key).ToLowerInvariant();
        return extension switch
        {
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
    }

    #endregion

    #region Disposal

    /// <summary>
    /// Disposes resources used by this storage strategy.
    /// </summary>
    public new virtual void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        DisposeAsyncCore().GetAwaiter().GetResult();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously disposes resources.
    /// </summary>
    public new async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await DisposeAsyncCore();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Core disposal logic. Override in derived classes to clean up resources.
    /// </summary>
    protected new virtual async ValueTask DisposeAsyncCore()
    {
        if (_isConnected)
        {
            await DisconnectAsync();
        }

        _connectionLock?.Dispose();
        _operationLock?.Dispose();
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Interface for database transactions.
/// </summary>
public interface IDatabaseTransaction : IAsyncDisposable
{
    /// <summary>
    /// Gets the transaction ID.
    /// </summary>
    string TransactionId { get; }

    /// <summary>
    /// Gets the isolation level.
    /// </summary>
    IsolationLevel IsolationLevel { get; }

    /// <summary>
    /// Commits the transaction.
    /// </summary>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>
    /// Rolls back the transaction.
    /// </summary>
    Task RollbackAsync(CancellationToken ct = default);
}

/// <summary>
/// Comprehensive statistics for a database storage strategy.
/// </summary>
public record DatabaseStorageStatistics
{
    public string StrategyId { get; init; } = string.Empty;
    public string StrategyName { get; init; } = string.Empty;
    public StorageTier Tier { get; init; }
    public DatabaseCategory DatabaseCategory { get; init; }
    public string Engine { get; init; } = string.Empty;
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

#endregion
