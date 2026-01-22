using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Microsoft.Data.Sqlite;

namespace DataWarehouse.Plugins.MetadataStorage;

/// <summary>
/// Production-ready Metadata Storage plugin for the DataWarehouse write fan-out pattern.
/// Supports SQLite (ACID-compliant) and file-based storage backends.
/// Provides comprehensive metadata management with full-text search, filtering, and pagination.
/// </summary>
public class MetadataStoragePlugin : WriteDestinationPluginBase, IDisposable
{
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private MetadataStorageConfiguration _configuration = new();
    private IMetadataStorageBackend? _backend;
    private bool _disposed;
    private volatile bool _isRunning;

    public override string Id => "datawarehouse.plugins.metadata-storage";
    public override string Name => "Metadata Storage Plugin";
    public override string Version => "1.0.0";
    public override WriteDestinationType DestinationType => WriteDestinationType.MetadataStorage;
    public override bool IsRequired => false;
    public override int Priority => 90;

    /// <summary>
    /// Indicates whether the plugin is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    public MetadataStoragePlugin()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// Configures the plugin with the specified options.
    /// </summary>
    public void Configure(MetadataStorageConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Starts the metadata storage plugin.
    /// </summary>
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return;

        lock (_lock)
        {
            if (_isRunning) return;

            _backend = _configuration.BackendType switch
            {
                MetadataBackendType.SQLite => new SqliteMetadataBackend(_configuration, _jsonOptions),
                MetadataBackendType.FileBased => new FileBasedMetadataBackend(_configuration, _jsonOptions),
                _ => throw new InvalidOperationException($"Unsupported backend type: {_configuration.BackendType}")
            };

            _isRunning = true;
        }

        await _backend.InitializeAsync(ct);
    }

    /// <summary>
    /// Stops the metadata storage plugin.
    /// </summary>
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
        }

        if (_backend is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_backend is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _backend = null;
    }

    /// <summary>
    /// Writes metadata to storage as part of the fan-out write pattern.
    /// </summary>
    public override async Task<WriteDestinationResult> WriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        EnsureRunning();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var record = MetadataRecord.FromIndexableContent(objectId, content);
            await _backend!.UpsertAsync(record, ct);
            sw.Stop();
            return SuccessResult(sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailureResult($"Failed to write metadata: {ex.Message}", sw.Elapsed);
        }
    }

    /// <summary>
    /// Retrieves metadata for a specific object by its ID.
    /// </summary>
    public async Task<MetadataRecord?> GetMetadataAsync(string objectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("Object ID cannot be empty", nameof(objectId));

        EnsureRunning();
        return await _backend!.GetAsync(objectId, ct);
    }

    /// <summary>
    /// Queries metadata with flexible filtering options.
    /// </summary>
    public async Task<MetadataQueryResult> QueryMetadataAsync(
        MetadataQuery query,
        CancellationToken ct = default)
    {
        if (query == null)
            throw new ArgumentNullException(nameof(query));

        EnsureRunning();
        return await _backend!.QueryAsync(query, ct);
    }

    /// <summary>
    /// Updates existing metadata for an object.
    /// </summary>
    public async Task<bool> UpdateMetadataAsync(
        string objectId,
        MetadataUpdateRequest updateRequest,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("Object ID cannot be empty", nameof(objectId));
        if (updateRequest == null)
            throw new ArgumentNullException(nameof(updateRequest));

        EnsureRunning();
        return await _backend!.UpdateAsync(objectId, updateRequest, ct);
    }

    /// <summary>
    /// Deletes metadata for a specific object.
    /// </summary>
    public async Task<bool> DeleteMetadataAsync(string objectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentException("Object ID cannot be empty", nameof(objectId));

        EnsureRunning();
        return await _backend!.DeleteAsync(objectId, ct);
    }

    /// <summary>
    /// Lists objects with pagination support.
    /// </summary>
    public async Task<MetadataListResult> ListObjectsAsync(
        MetadataListOptions options,
        CancellationToken ct = default)
    {
        options ??= new MetadataListOptions();
        EnsureRunning();
        return await _backend!.ListAsync(options, ct);
    }

    /// <summary>
    /// Gets storage statistics.
    /// </summary>
    public async Task<MetadataStorageStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        EnsureRunning();
        return await _backend!.GetStatisticsAsync(ct);
    }

    /// <summary>
    /// Performs a bulk write operation.
    /// </summary>
    public async Task<BulkWriteResult> BulkWriteAsync(
        IEnumerable<MetadataRecord> records,
        CancellationToken ct = default)
    {
        if (records == null)
            throw new ArgumentNullException(nameof(records));

        EnsureRunning();
        return await _backend!.BulkUpsertAsync(records, ct);
    }

    /// <summary>
    /// Performs a bulk delete operation.
    /// </summary>
    public async Task<int> BulkDeleteAsync(
        IEnumerable<string> objectIds,
        CancellationToken ct = default)
    {
        if (objectIds == null)
            throw new ArgumentNullException(nameof(objectIds));

        EnsureRunning();
        return await _backend!.BulkDeleteAsync(objectIds, ct);
    }

    private void EnsureRunning()
    {
        if (!_isRunning || _backend == null)
            throw new InvalidOperationException("Metadata storage plugin is not running. Call StartAsync first.");
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["BackendType"] = _configuration.BackendType.ToString();
        metadata["SupportsFullTextSearch"] = true;
        metadata["SupportsRangeQueries"] = true;
        metadata["SupportsTagFiltering"] = true;
        metadata["SupportsPagination"] = true;
        metadata["ACIDCompliant"] = _configuration.BackendType == MetadataBackendType.SQLite;
        return metadata;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_backend is IDisposable disposable)
        {
            disposable.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

#region Configuration

/// <summary>
/// Configuration options for the Metadata Storage plugin.
/// </summary>
public class MetadataStorageConfiguration
{
    /// <summary>
    /// The backend storage type.
    /// </summary>
    public MetadataBackendType BackendType { get; set; } = MetadataBackendType.SQLite;

    /// <summary>
    /// Base path for storage (database file or directory for file-based).
    /// </summary>
    public string StoragePath { get; set; } = "./metadata";

    /// <summary>
    /// Database name for SQLite backend.
    /// </summary>
    public string DatabaseName { get; set; } = "metadata.db";

    /// <summary>
    /// Connection pool size for SQLite.
    /// </summary>
    public int PoolSize { get; set; } = 10;

    /// <summary>
    /// Enable Write-Ahead Logging for SQLite (better concurrency).
    /// </summary>
    public bool EnableWAL { get; set; } = true;

    /// <summary>
    /// Enable automatic vacuuming.
    /// </summary>
    public bool AutoVacuum { get; set; } = true;

    /// <summary>
    /// Cache size in pages for SQLite.
    /// </summary>
    public int CacheSizePages { get; set; } = 10000;

    /// <summary>
    /// Maximum metadata JSON size in bytes.
    /// </summary>
    public int MaxMetadataSize { get; set; } = 1024 * 1024; // 1MB

    /// <summary>
    /// Enable compression for file-based backend.
    /// </summary>
    public bool EnableCompression { get; set; } = false;
}

/// <summary>
/// Backend storage types.
/// </summary>
public enum MetadataBackendType
{
    /// <summary>
    /// SQLite embedded database (ACID-compliant, recommended for production).
    /// </summary>
    SQLite,

    /// <summary>
    /// File-based storage using JSON files.
    /// </summary>
    FileBased
}

#endregion

#region Data Models

/// <summary>
/// Represents a metadata record in storage.
/// </summary>
public class MetadataRecord
{
    /// <summary>
    /// Unique object identifier (primary key).
    /// </summary>
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>
    /// Original filename.
    /// </summary>
    public string? Filename { get; set; }

    /// <summary>
    /// MIME content type.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Creation timestamp (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last modification timestamp (UTC).
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last access timestamp (UTC).
    /// </summary>
    public DateTime? LastAccessedAt { get; set; }

    /// <summary>
    /// Custom metadata as key-value pairs.
    /// </summary>
    public Dictionary<string, object> CustomMetadata { get; set; } = new();

    /// <summary>
    /// Tags for categorization.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Labels as key-value pairs.
    /// </summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>
    /// Version number (incremented on each update).
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Content hash (e.g., SHA-256).
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// MD5 hash for compatibility.
    /// </summary>
    public string? MD5Hash { get; set; }

    /// <summary>
    /// Parent container or bucket ID.
    /// </summary>
    public string? ContainerId { get; set; }

    /// <summary>
    /// Owner ID.
    /// </summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// Text content for full-text search.
    /// </summary>
    public string? TextContent { get; set; }

    /// <summary>
    /// AI-generated summary.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Storage tier (Hot, Warm, Cold, Archive).
    /// </summary>
    public string StorageTier { get; set; } = "Hot";

    /// <summary>
    /// Whether the object is marked as deleted (soft delete).
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Deletion timestamp if soft-deleted.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// ETag for optimistic concurrency.
    /// </summary>
    public string ETag { get; set; } = GenerateETag();

    /// <summary>
    /// Creates a MetadataRecord from IndexableContent.
    /// </summary>
    public static MetadataRecord FromIndexableContent(string objectId, IndexableContent content)
    {
        var record = new MetadataRecord
        {
            ObjectId = objectId,
            Filename = content.Filename,
            ContentType = content.ContentType,
            Size = content.Size ?? 0,
            TextContent = content.TextContent,
            Summary = content.Summary,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        if (content.Metadata != null)
        {
            foreach (var kvp in content.Metadata)
            {
                record.CustomMetadata[kvp.Key] = kvp.Value;
            }
        }

        return record;
    }

    private static string GenerateETag()
    {
        return Convert.ToBase64String(Guid.NewGuid().ToByteArray())[..22];
    }

    /// <summary>
    /// Updates the ETag for concurrency control.
    /// </summary>
    public void UpdateETag()
    {
        ETag = GenerateETag();
        Version++;
        ModifiedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Query options for metadata search.
/// </summary>
public class MetadataQuery
{
    /// <summary>
    /// Full-text search query on filename and text content.
    /// </summary>
    public string? TextSearch { get; set; }

    /// <summary>
    /// Filter by content type (supports wildcards like "image/*").
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Filter by tags (AND logic - all tags must match).
    /// </summary>
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Filter by tags (OR logic - any tag matches).
    /// </summary>
    public List<string>? AnyTags { get; set; }

    /// <summary>
    /// Filter by labels.
    /// </summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>
    /// Minimum size in bytes.
    /// </summary>
    public long? MinSize { get; set; }

    /// <summary>
    /// Maximum size in bytes.
    /// </summary>
    public long? MaxSize { get; set; }

    /// <summary>
    /// Created after this date.
    /// </summary>
    public DateTime? CreatedAfter { get; set; }

    /// <summary>
    /// Created before this date.
    /// </summary>
    public DateTime? CreatedBefore { get; set; }

    /// <summary>
    /// Modified after this date.
    /// </summary>
    public DateTime? ModifiedAfter { get; set; }

    /// <summary>
    /// Modified before this date.
    /// </summary>
    public DateTime? ModifiedBefore { get; set; }

    /// <summary>
    /// Filter by container ID.
    /// </summary>
    public string? ContainerId { get; set; }

    /// <summary>
    /// Filter by owner ID.
    /// </summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// Filter by storage tier.
    /// </summary>
    public string? StorageTier { get; set; }

    /// <summary>
    /// Include soft-deleted items.
    /// </summary>
    public bool IncludeDeleted { get; set; }

    /// <summary>
    /// Custom metadata filters (supports equality).
    /// </summary>
    public Dictionary<string, object>? CustomFilters { get; set; }

    /// <summary>
    /// Field to sort by.
    /// </summary>
    public string SortBy { get; set; } = "createdAt";

    /// <summary>
    /// Sort direction.
    /// </summary>
    public SortDirection SortDirection { get; set; } = SortDirection.Descending;

    /// <summary>
    /// Page number (1-based).
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Items per page.
    /// </summary>
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// Sort direction for queries.
/// </summary>
public enum SortDirection
{
    Ascending,
    Descending
}

/// <summary>
/// Result of a metadata query.
/// </summary>
public class MetadataQueryResult
{
    /// <summary>
    /// The matching records.
    /// </summary>
    public List<MetadataRecord> Records { get; set; } = new();

    /// <summary>
    /// Total count of matching records (before pagination).
    /// </summary>
    public long TotalCount { get; set; }

    /// <summary>
    /// Current page number.
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// Items per page.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// Total number of pages.
    /// </summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;

    /// <summary>
    /// Whether there are more pages.
    /// </summary>
    public bool HasMore => Page < TotalPages;

    /// <summary>
    /// Query execution time.
    /// </summary>
    public TimeSpan ExecutionTime { get; set; }
}

/// <summary>
/// Update request for metadata.
/// </summary>
public class MetadataUpdateRequest
{
    /// <summary>
    /// New filename (null = no change).
    /// </summary>
    public string? Filename { get; set; }

    /// <summary>
    /// New content type (null = no change).
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Custom metadata to merge (existing keys are overwritten).
    /// </summary>
    public Dictionary<string, object>? CustomMetadata { get; set; }

    /// <summary>
    /// Tags to add.
    /// </summary>
    public List<string>? AddTags { get; set; }

    /// <summary>
    /// Tags to remove.
    /// </summary>
    public List<string>? RemoveTags { get; set; }

    /// <summary>
    /// Replace all tags.
    /// </summary>
    public List<string>? ReplaceTags { get; set; }

    /// <summary>
    /// Labels to merge.
    /// </summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>
    /// Labels to remove.
    /// </summary>
    public List<string>? RemoveLabels { get; set; }

    /// <summary>
    /// New storage tier.
    /// </summary>
    public string? StorageTier { get; set; }

    /// <summary>
    /// New content hash.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// New size.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Expected ETag for optimistic concurrency (optional).
    /// </summary>
    public string? ExpectedETag { get; set; }
}

/// <summary>
/// Options for listing objects.
/// </summary>
public class MetadataListOptions
{
    /// <summary>
    /// Prefix filter for object IDs or filenames.
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Container ID filter.
    /// </summary>
    public string? ContainerId { get; set; }

    /// <summary>
    /// Continuation token for pagination.
    /// </summary>
    public string? ContinuationToken { get; set; }

    /// <summary>
    /// Maximum items to return.
    /// </summary>
    public int MaxItems { get; set; } = 100;

    /// <summary>
    /// Include soft-deleted items.
    /// </summary>
    public bool IncludeDeleted { get; set; }

    /// <summary>
    /// Field to sort by.
    /// </summary>
    public string SortBy { get; set; } = "createdAt";

    /// <summary>
    /// Sort direction.
    /// </summary>
    public SortDirection SortDirection { get; set; } = SortDirection.Descending;
}

/// <summary>
/// Result of a list operation.
/// </summary>
public class MetadataListResult
{
    /// <summary>
    /// The listed records.
    /// </summary>
    public List<MetadataRecord> Records { get; set; } = new();

    /// <summary>
    /// Continuation token for next page.
    /// </summary>
    public string? ContinuationToken { get; set; }

    /// <summary>
    /// Whether there are more items.
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// Total count of matching items (if available).
    /// </summary>
    public long? TotalCount { get; set; }
}

/// <summary>
/// Storage statistics.
/// </summary>
public class MetadataStorageStatistics
{
    /// <summary>
    /// Total number of objects.
    /// </summary>
    public long TotalObjects { get; set; }

    /// <summary>
    /// Total size of all objects in bytes.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Number of deleted objects (soft deletes).
    /// </summary>
    public long DeletedObjects { get; set; }

    /// <summary>
    /// Objects by content type.
    /// </summary>
    public Dictionary<string, long> ObjectsByContentType { get; set; } = new();

    /// <summary>
    /// Objects by storage tier.
    /// </summary>
    public Dictionary<string, long> ObjectsByStorageTier { get; set; } = new();

    /// <summary>
    /// Objects by container.
    /// </summary>
    public Dictionary<string, long> ObjectsByContainer { get; set; } = new();

    /// <summary>
    /// Average object size in bytes.
    /// </summary>
    public double AverageObjectSize { get; set; }

    /// <summary>
    /// Largest object size in bytes.
    /// </summary>
    public long LargestObjectSize { get; set; }

    /// <summary>
    /// Smallest object size in bytes.
    /// </summary>
    public long SmallestObjectSize { get; set; }

    /// <summary>
    /// Oldest object creation date.
    /// </summary>
    public DateTime? OldestObject { get; set; }

    /// <summary>
    /// Newest object creation date.
    /// </summary>
    public DateTime? NewestObject { get; set; }

    /// <summary>
    /// Database/storage file size.
    /// </summary>
    public long StorageSizeBytes { get; set; }

    /// <summary>
    /// Index size (if applicable).
    /// </summary>
    public long IndexSizeBytes { get; set; }

    /// <summary>
    /// Backend type.
    /// </summary>
    public string BackendType { get; set; } = string.Empty;

    /// <summary>
    /// Statistics generation timestamp.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result of a bulk write operation.
/// </summary>
public class BulkWriteResult
{
    /// <summary>
    /// Number of successfully written records.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed records.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Failed object IDs with error messages.
    /// </summary>
    public Dictionary<string, string> Failures { get; set; } = new();

    /// <summary>
    /// Total operation time.
    /// </summary>
    public TimeSpan Duration { get; set; }
}

#endregion

#region Backend Interface

/// <summary>
/// Interface for metadata storage backends.
/// </summary>
internal interface IMetadataStorageBackend : IDisposable
{
    Task InitializeAsync(CancellationToken ct);
    Task<MetadataRecord?> GetAsync(string objectId, CancellationToken ct);
    Task UpsertAsync(MetadataRecord record, CancellationToken ct);
    Task<bool> UpdateAsync(string objectId, MetadataUpdateRequest request, CancellationToken ct);
    Task<bool> DeleteAsync(string objectId, CancellationToken ct);
    Task<MetadataQueryResult> QueryAsync(MetadataQuery query, CancellationToken ct);
    Task<MetadataListResult> ListAsync(MetadataListOptions options, CancellationToken ct);
    Task<MetadataStorageStatistics> GetStatisticsAsync(CancellationToken ct);
    Task<BulkWriteResult> BulkUpsertAsync(IEnumerable<MetadataRecord> records, CancellationToken ct);
    Task<int> BulkDeleteAsync(IEnumerable<string> objectIds, CancellationToken ct);
}

#endregion

#region SQLite Backend

/// <summary>
/// SQLite-based metadata storage backend with ACID compliance.
/// </summary>
internal sealed class SqliteMetadataBackend : IMetadataStorageBackend, IAsyncDisposable
{
    private readonly MetadataStorageConfiguration _config;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _connectionSemaphore;
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteMetadataBackend(MetadataStorageConfiguration config, JsonSerializerOptions jsonOptions)
    {
        _config = config;
        _jsonOptions = jsonOptions;
        _connectionSemaphore = new SemaphoreSlim(config.PoolSize, config.PoolSize);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_config.StoragePath);
        var dbPath = Path.Combine(_config.StoragePath, _config.DatabaseName);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync(ct);

        // Configure SQLite for performance
        await ExecuteNonQueryAsync($"PRAGMA journal_mode = {(_config.EnableWAL ? "WAL" : "DELETE")};", ct);
        await ExecuteNonQueryAsync($"PRAGMA synchronous = NORMAL;", ct);
        await ExecuteNonQueryAsync($"PRAGMA cache_size = {_config.CacheSizePages};", ct);
        await ExecuteNonQueryAsync($"PRAGMA auto_vacuum = {(_config.AutoVacuum ? "INCREMENTAL" : "NONE")};", ct);
        await ExecuteNonQueryAsync("PRAGMA foreign_keys = ON;", ct);
        await ExecuteNonQueryAsync("PRAGMA temp_store = MEMORY;", ct);

        // Create tables
        await CreateTablesAsync(ct);
    }

    private async Task CreateTablesAsync(CancellationToken ct)
    {
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS metadata (
                object_id TEXT PRIMARY KEY NOT NULL,
                filename TEXT,
                content_type TEXT,
                size INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                last_accessed_at TEXT,
                custom_metadata TEXT,
                tags TEXT,
                labels TEXT,
                version INTEGER NOT NULL DEFAULT 1,
                content_hash TEXT,
                md5_hash TEXT,
                container_id TEXT,
                owner_id TEXT,
                text_content TEXT,
                summary TEXT,
                storage_tier TEXT NOT NULL DEFAULT 'Hot',
                is_deleted INTEGER NOT NULL DEFAULT 0,
                deleted_at TEXT,
                etag TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_metadata_filename ON metadata(filename);
            CREATE INDEX IF NOT EXISTS idx_metadata_content_type ON metadata(content_type);
            CREATE INDEX IF NOT EXISTS idx_metadata_created_at ON metadata(created_at);
            CREATE INDEX IF NOT EXISTS idx_metadata_modified_at ON metadata(modified_at);
            CREATE INDEX IF NOT EXISTS idx_metadata_container_id ON metadata(container_id);
            CREATE INDEX IF NOT EXISTS idx_metadata_owner_id ON metadata(owner_id);
            CREATE INDEX IF NOT EXISTS idx_metadata_storage_tier ON metadata(storage_tier);
            CREATE INDEX IF NOT EXISTS idx_metadata_is_deleted ON metadata(is_deleted);
            CREATE INDEX IF NOT EXISTS idx_metadata_size ON metadata(size);

            CREATE VIRTUAL TABLE IF NOT EXISTS metadata_fts USING fts5(
                object_id,
                filename,
                text_content,
                summary,
                content='metadata',
                content_rowid='rowid'
            );

            CREATE TRIGGER IF NOT EXISTS metadata_ai AFTER INSERT ON metadata BEGIN
                INSERT INTO metadata_fts(rowid, object_id, filename, text_content, summary)
                VALUES (NEW.rowid, NEW.object_id, NEW.filename, NEW.text_content, NEW.summary);
            END;

            CREATE TRIGGER IF NOT EXISTS metadata_ad AFTER DELETE ON metadata BEGIN
                INSERT INTO metadata_fts(metadata_fts, rowid, object_id, filename, text_content, summary)
                VALUES ('delete', OLD.rowid, OLD.object_id, OLD.filename, OLD.text_content, OLD.summary);
            END;

            CREATE TRIGGER IF NOT EXISTS metadata_au AFTER UPDATE ON metadata BEGIN
                INSERT INTO metadata_fts(metadata_fts, rowid, object_id, filename, text_content, summary)
                VALUES ('delete', OLD.rowid, OLD.object_id, OLD.filename, OLD.text_content, OLD.summary);
                INSERT INTO metadata_fts(rowid, object_id, filename, text_content, summary)
                VALUES (NEW.rowid, NEW.object_id, NEW.filename, NEW.text_content, NEW.summary);
            END;
        ";

        await ExecuteNonQueryAsync(createTableSql, ct);
    }

    public async Task<MetadataRecord?> GetAsync(string objectId, CancellationToken ct)
    {
        const string sql = @"
            SELECT object_id, filename, content_type, size, created_at, modified_at, last_accessed_at,
                   custom_metadata, tags, labels, version, content_hash, md5_hash, container_id,
                   owner_id, text_content, summary, storage_tier, is_deleted, deleted_at, etag
            FROM metadata WHERE object_id = @objectId";

        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            using var cmd = new SqliteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@objectId", objectId);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return ReadRecord(reader);
            }
            return null;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    public async Task UpsertAsync(MetadataRecord record, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO metadata (object_id, filename, content_type, size, created_at, modified_at,
                last_accessed_at, custom_metadata, tags, labels, version, content_hash, md5_hash,
                container_id, owner_id, text_content, summary, storage_tier, is_deleted, deleted_at, etag)
            VALUES (@objectId, @filename, @contentType, @size, @createdAt, @modifiedAt,
                @lastAccessedAt, @customMetadata, @tags, @labels, @version, @contentHash, @md5Hash,
                @containerId, @ownerId, @textContent, @summary, @storageTier, @isDeleted, @deletedAt, @etag)
            ON CONFLICT(object_id) DO UPDATE SET
                filename = excluded.filename,
                content_type = excluded.content_type,
                size = excluded.size,
                modified_at = excluded.modified_at,
                last_accessed_at = excluded.last_accessed_at,
                custom_metadata = excluded.custom_metadata,
                tags = excluded.tags,
                labels = excluded.labels,
                version = metadata.version + 1,
                content_hash = excluded.content_hash,
                md5_hash = excluded.md5_hash,
                container_id = excluded.container_id,
                owner_id = excluded.owner_id,
                text_content = excluded.text_content,
                summary = excluded.summary,
                storage_tier = excluded.storage_tier,
                is_deleted = excluded.is_deleted,
                deleted_at = excluded.deleted_at,
                etag = excluded.etag";

        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            using var cmd = new SqliteCommand(sql, _connection);
            AddRecordParameters(cmd, record);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    public async Task<bool> UpdateAsync(string objectId, MetadataUpdateRequest request, CancellationToken ct)
    {
        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            // Get existing record
            var existing = await GetAsyncInternal(objectId, ct);
            if (existing == null) return false;

            // Check ETag if provided
            if (!string.IsNullOrEmpty(request.ExpectedETag) && existing.ETag != request.ExpectedETag)
            {
                throw new ConcurrencyException($"ETag mismatch. Expected: {request.ExpectedETag}, Actual: {existing.ETag}");
            }

            // Apply updates
            if (request.Filename != null) existing.Filename = request.Filename;
            if (request.ContentType != null) existing.ContentType = request.ContentType;
            if (request.Size.HasValue) existing.Size = request.Size.Value;
            if (request.ContentHash != null) existing.ContentHash = request.ContentHash;
            if (request.StorageTier != null) existing.StorageTier = request.StorageTier;

            // Handle custom metadata
            if (request.CustomMetadata != null)
            {
                foreach (var kvp in request.CustomMetadata)
                {
                    existing.CustomMetadata[kvp.Key] = kvp.Value;
                }
            }

            // Handle tags
            if (request.ReplaceTags != null)
            {
                existing.Tags = request.ReplaceTags;
            }
            else
            {
                if (request.AddTags != null)
                {
                    existing.Tags.AddRange(request.AddTags.Where(t => !existing.Tags.Contains(t)));
                }
                if (request.RemoveTags != null)
                {
                    existing.Tags.RemoveAll(t => request.RemoveTags.Contains(t));
                }
            }

            // Handle labels
            if (request.Labels != null)
            {
                foreach (var kvp in request.Labels)
                {
                    existing.Labels[kvp.Key] = kvp.Value;
                }
            }
            if (request.RemoveLabels != null)
            {
                foreach (var key in request.RemoveLabels)
                {
                    existing.Labels.Remove(key);
                }
            }

            existing.UpdateETag();

            // Save changes
            await UpsertInternalAsync(existing, ct);
            return true;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private async Task<MetadataRecord?> GetAsyncInternal(string objectId, CancellationToken ct)
    {
        const string sql = @"
            SELECT object_id, filename, content_type, size, created_at, modified_at, last_accessed_at,
                   custom_metadata, tags, labels, version, content_hash, md5_hash, container_id,
                   owner_id, text_content, summary, storage_tier, is_deleted, deleted_at, etag
            FROM metadata WHERE object_id = @objectId";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@objectId", objectId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadRecord(reader);
        }
        return null;
    }

    private async Task UpsertInternalAsync(MetadataRecord record, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO metadata (object_id, filename, content_type, size, created_at, modified_at,
                last_accessed_at, custom_metadata, tags, labels, version, content_hash, md5_hash,
                container_id, owner_id, text_content, summary, storage_tier, is_deleted, deleted_at, etag)
            VALUES (@objectId, @filename, @contentType, @size, @createdAt, @modifiedAt,
                @lastAccessedAt, @customMetadata, @tags, @labels, @version, @contentHash, @md5Hash,
                @containerId, @ownerId, @textContent, @summary, @storageTier, @isDeleted, @deletedAt, @etag)
            ON CONFLICT(object_id) DO UPDATE SET
                filename = excluded.filename,
                content_type = excluded.content_type,
                size = excluded.size,
                modified_at = excluded.modified_at,
                last_accessed_at = excluded.last_accessed_at,
                custom_metadata = excluded.custom_metadata,
                tags = excluded.tags,
                labels = excluded.labels,
                version = excluded.version,
                content_hash = excluded.content_hash,
                md5_hash = excluded.md5_hash,
                container_id = excluded.container_id,
                owner_id = excluded.owner_id,
                text_content = excluded.text_content,
                summary = excluded.summary,
                storage_tier = excluded.storage_tier,
                is_deleted = excluded.is_deleted,
                deleted_at = excluded.deleted_at,
                etag = excluded.etag";

        using var cmd = new SqliteCommand(sql, _connection);
        AddRecordParameters(cmd, record);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string objectId, CancellationToken ct)
    {
        const string sql = @"
            UPDATE metadata SET
                is_deleted = 1,
                deleted_at = @deletedAt,
                modified_at = @modifiedAt,
                etag = @etag,
                version = version + 1
            WHERE object_id = @objectId AND is_deleted = 0";

        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            using var cmd = new SqliteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@objectId", objectId);
            cmd.Parameters.AddWithValue("@deletedAt", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@modifiedAt", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@etag", Convert.ToBase64String(Guid.NewGuid().ToByteArray())[..22]);

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    public async Task<MetadataQueryResult> QueryAsync(MetadataQuery query, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            var (whereClauses, parameters) = BuildWhereClause(query);
            var whereString = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

            // Check if we need FTS
            var usesFts = !string.IsNullOrEmpty(query.TextSearch);
            var fromClause = usesFts
                ? "FROM metadata m INNER JOIN metadata_fts f ON m.object_id = f.object_id"
                : "FROM metadata m";

            if (usesFts && !string.IsNullOrEmpty(query.TextSearch))
            {
                whereClauses.Add("metadata_fts MATCH @textSearch");
                parameters["@textSearch"] = EscapeFtsQuery(query.TextSearch);
                whereString = "WHERE " + string.Join(" AND ", whereClauses);
            }

            // Count query
            var countSql = $"SELECT COUNT(*) {fromClause} {whereString}";
            using var countCmd = new SqliteCommand(countSql, _connection);
            foreach (var p in parameters)
            {
                countCmd.Parameters.AddWithValue(p.Key, p.Value);
            }
            var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

            // Data query with pagination
            var sortColumn = GetSortColumn(query.SortBy);
            var sortDir = query.SortDirection == SortDirection.Ascending ? "ASC" : "DESC";
            var offset = (query.Page - 1) * query.PageSize;

            var dataSql = $@"
                SELECT m.object_id, m.filename, m.content_type, m.size, m.created_at, m.modified_at,
                       m.last_accessed_at, m.custom_metadata, m.tags, m.labels, m.version, m.content_hash,
                       m.md5_hash, m.container_id, m.owner_id, m.text_content, m.summary, m.storage_tier,
                       m.is_deleted, m.deleted_at, m.etag
                {fromClause} {whereString}
                ORDER BY m.{sortColumn} {sortDir}
                LIMIT @limit OFFSET @offset";

            using var dataCmd = new SqliteCommand(dataSql, _connection);
            foreach (var p in parameters)
            {
                dataCmd.Parameters.AddWithValue(p.Key, p.Value);
            }
            dataCmd.Parameters.AddWithValue("@limit", query.PageSize);
            dataCmd.Parameters.AddWithValue("@offset", offset);

            var records = new List<MetadataRecord>();
            using var reader = await dataCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                records.Add(ReadRecord(reader));
            }

            sw.Stop();
            return new MetadataQueryResult
            {
                Records = records,
                TotalCount = totalCount,
                Page = query.Page,
                PageSize = query.PageSize,
                ExecutionTime = sw.Elapsed
            };
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    public async Task<MetadataListResult> ListAsync(MetadataListOptions options, CancellationToken ct)
    {
        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            var whereClauses = new List<string>();
            var parameters = new Dictionary<string, object>();

            if (!options.IncludeDeleted)
            {
                whereClauses.Add("is_deleted = 0");
            }

            if (!string.IsNullOrEmpty(options.Prefix))
            {
                whereClauses.Add("(object_id LIKE @prefix OR filename LIKE @prefix)");
                parameters["@prefix"] = options.Prefix + "%";
            }

            if (!string.IsNullOrEmpty(options.ContainerId))
            {
                whereClauses.Add("container_id = @containerId");
                parameters["@containerId"] = options.ContainerId;
            }

            var whereString = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
            var sortColumn = GetSortColumn(options.SortBy);
            var sortDir = options.SortDirection == SortDirection.Ascending ? "ASC" : "DESC";

            // Parse continuation token for offset
            var offset = 0;
            if (!string.IsNullOrEmpty(options.ContinuationToken))
            {
                var tokenBytes = Convert.FromBase64String(options.ContinuationToken);
                offset = int.Parse(Encoding.UTF8.GetString(tokenBytes));
            }

            // Count query
            var countSql = $"SELECT COUNT(*) FROM metadata {whereString}";
            using var countCmd = new SqliteCommand(countSql, _connection);
            foreach (var p in parameters)
            {
                countCmd.Parameters.AddWithValue(p.Key, p.Value);
            }
            var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

            // Data query
            var dataSql = $@"
                SELECT object_id, filename, content_type, size, created_at, modified_at, last_accessed_at,
                       custom_metadata, tags, labels, version, content_hash, md5_hash, container_id,
                       owner_id, text_content, summary, storage_tier, is_deleted, deleted_at, etag
                FROM metadata {whereString}
                ORDER BY {sortColumn} {sortDir}
                LIMIT @limit OFFSET @offset";

            using var dataCmd = new SqliteCommand(dataSql, _connection);
            foreach (var p in parameters)
            {
                dataCmd.Parameters.AddWithValue(p.Key, p.Value);
            }
            dataCmd.Parameters.AddWithValue("@limit", options.MaxItems + 1); // Get one extra to check hasMore
            dataCmd.Parameters.AddWithValue("@offset", offset);

            var records = new List<MetadataRecord>();
            using var reader = await dataCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                records.Add(ReadRecord(reader));
            }

            var hasMore = records.Count > options.MaxItems;
            if (hasMore)
            {
                records = records.Take(options.MaxItems).ToList();
            }

            string? continuationToken = null;
            if (hasMore)
            {
                var nextOffset = offset + options.MaxItems;
                continuationToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(nextOffset.ToString()));
            }

            return new MetadataListResult
            {
                Records = records,
                ContinuationToken = continuationToken,
                HasMore = hasMore,
                TotalCount = totalCount
            };
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    public async Task<MetadataStorageStatistics> GetStatisticsAsync(CancellationToken ct)
    {
        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            var stats = new MetadataStorageStatistics
            {
                BackendType = "SQLite"
            };

            // Basic counts
            using (var cmd = new SqliteCommand("SELECT COUNT(*), SUM(size), AVG(size), MAX(size), MIN(size) FROM metadata WHERE is_deleted = 0", _connection))
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    stats.TotalObjects = reader.GetInt64(0);
                    stats.TotalSizeBytes = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    stats.AverageObjectSize = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
                    stats.LargestObjectSize = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    stats.SmallestObjectSize = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                }
            }

            // Deleted count
            using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM metadata WHERE is_deleted = 1", _connection))
            {
                stats.DeletedObjects = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }

            // By content type
            using (var cmd = new SqliteCommand("SELECT content_type, COUNT(*) FROM metadata WHERE is_deleted = 0 GROUP BY content_type", _connection))
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var contentType = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                    stats.ObjectsByContentType[contentType] = reader.GetInt64(1);
                }
            }

            // By storage tier
            using (var cmd = new SqliteCommand("SELECT storage_tier, COUNT(*) FROM metadata WHERE is_deleted = 0 GROUP BY storage_tier", _connection))
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    stats.ObjectsByStorageTier[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            // By container
            using (var cmd = new SqliteCommand("SELECT container_id, COUNT(*) FROM metadata WHERE is_deleted = 0 AND container_id IS NOT NULL GROUP BY container_id", _connection))
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    stats.ObjectsByContainer[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            // Date ranges
            using (var cmd = new SqliteCommand("SELECT MIN(created_at), MAX(created_at) FROM metadata WHERE is_deleted = 0", _connection))
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    if (!reader.IsDBNull(0))
                        stats.OldestObject = DateTime.Parse(reader.GetString(0));
                    if (!reader.IsDBNull(1))
                        stats.NewestObject = DateTime.Parse(reader.GetString(1));
                }
            }

            // Database file size
            var dbPath = Path.Combine(_config.StoragePath, _config.DatabaseName);
            if (File.Exists(dbPath))
            {
                stats.StorageSizeBytes = new FileInfo(dbPath).Length;
            }

            return stats;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    public async Task<BulkWriteResult> BulkUpsertAsync(IEnumerable<MetadataRecord> records, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new BulkWriteResult();

        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            using var transaction = _connection!.BeginTransaction();

            foreach (var record in records)
            {
                try
                {
                    await UpsertInternalAsync(record, ct);
                    result.SuccessCount++;
                }
                catch (Exception ex)
                {
                    result.FailureCount++;
                    result.Failures[record.ObjectId] = ex.Message;
                }
            }

            transaction.Commit();
        }
        finally
        {
            _connectionSemaphore.Release();
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        return result;
    }

    public async Task<int> BulkDeleteAsync(IEnumerable<string> objectIds, CancellationToken ct)
    {
        var idList = objectIds.ToList();
        if (idList.Count == 0) return 0;

        await _connectionSemaphore.WaitAsync(ct);
        try
        {
            var placeholders = string.Join(",", idList.Select((_, i) => $"@id{i}"));
            var sql = $@"
                UPDATE metadata SET
                    is_deleted = 1,
                    deleted_at = @deletedAt,
                    modified_at = @modifiedAt,
                    version = version + 1
                WHERE object_id IN ({placeholders}) AND is_deleted = 0";

            using var cmd = new SqliteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@deletedAt", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@modifiedAt", DateTime.UtcNow.ToString("O"));

            for (int i = 0; i < idList.Count; i++)
            {
                cmd.Parameters.AddWithValue($"@id{i}", idList[i]);
            }

            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private (List<string> clauses, Dictionary<string, object> parameters) BuildWhereClause(MetadataQuery query)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object>();

        if (!query.IncludeDeleted)
        {
            clauses.Add("m.is_deleted = 0");
        }

        if (!string.IsNullOrEmpty(query.ContentType))
        {
            if (query.ContentType.EndsWith("/*"))
            {
                clauses.Add("m.content_type LIKE @contentType");
                parameters["@contentType"] = query.ContentType.Replace("/*", "/%");
            }
            else
            {
                clauses.Add("m.content_type = @contentType");
                parameters["@contentType"] = query.ContentType;
            }
        }

        if (!string.IsNullOrEmpty(query.ContainerId))
        {
            clauses.Add("m.container_id = @containerId");
            parameters["@containerId"] = query.ContainerId;
        }

        if (!string.IsNullOrEmpty(query.OwnerId))
        {
            clauses.Add("m.owner_id = @ownerId");
            parameters["@ownerId"] = query.OwnerId;
        }

        if (!string.IsNullOrEmpty(query.StorageTier))
        {
            clauses.Add("m.storage_tier = @storageTier");
            parameters["@storageTier"] = query.StorageTier;
        }

        if (query.MinSize.HasValue)
        {
            clauses.Add("m.size >= @minSize");
            parameters["@minSize"] = query.MinSize.Value;
        }

        if (query.MaxSize.HasValue)
        {
            clauses.Add("m.size <= @maxSize");
            parameters["@maxSize"] = query.MaxSize.Value;
        }

        if (query.CreatedAfter.HasValue)
        {
            clauses.Add("m.created_at >= @createdAfter");
            parameters["@createdAfter"] = query.CreatedAfter.Value.ToString("O");
        }

        if (query.CreatedBefore.HasValue)
        {
            clauses.Add("m.created_at <= @createdBefore");
            parameters["@createdBefore"] = query.CreatedBefore.Value.ToString("O");
        }

        if (query.ModifiedAfter.HasValue)
        {
            clauses.Add("m.modified_at >= @modifiedAfter");
            parameters["@modifiedAfter"] = query.ModifiedAfter.Value.ToString("O");
        }

        if (query.ModifiedBefore.HasValue)
        {
            clauses.Add("m.modified_at <= @modifiedBefore");
            parameters["@modifiedBefore"] = query.ModifiedBefore.Value.ToString("O");
        }

        // Tag filtering (stored as JSON array)
        if (query.Tags != null && query.Tags.Count > 0)
        {
            foreach (var tag in query.Tags)
            {
                var paramName = $"@tag_{Guid.NewGuid():N}";
                clauses.Add($"m.tags LIKE {paramName}");
                parameters[paramName] = $"%\"{tag}\"%";
            }
        }

        if (query.AnyTags != null && query.AnyTags.Count > 0)
        {
            var tagClauses = query.AnyTags.Select((tag, i) =>
            {
                var paramName = $"@anyTag_{i}";
                parameters[paramName] = $"%\"{tag}\"%";
                return $"m.tags LIKE {paramName}";
            });
            clauses.Add($"({string.Join(" OR ", tagClauses)})");
        }

        // Label filtering
        if (query.Labels != null && query.Labels.Count > 0)
        {
            foreach (var kvp in query.Labels)
            {
                var paramName = $"@label_{Guid.NewGuid():N}";
                clauses.Add($"json_extract(m.labels, '$.{kvp.Key}') = {paramName}");
                parameters[paramName] = kvp.Value;
            }
        }

        return (clauses, parameters);
    }

    private string GetSortColumn(string sortBy)
    {
        return sortBy.ToLowerInvariant() switch
        {
            "createdat" or "created_at" => "created_at",
            "modifiedat" or "modified_at" => "modified_at",
            "size" => "size",
            "filename" => "filename",
            "contenttype" or "content_type" => "content_type",
            "objectid" or "object_id" => "object_id",
            _ => "created_at"
        };
    }

    private string EscapeFtsQuery(string query)
    {
        // Escape special FTS5 characters and wrap in quotes for phrase search
        var escaped = query.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private MetadataRecord ReadRecord(SqliteDataReader reader)
    {
        var record = new MetadataRecord
        {
            ObjectId = reader.GetString(0),
            Filename = reader.IsDBNull(1) ? null : reader.GetString(1),
            ContentType = reader.IsDBNull(2) ? null : reader.GetString(2),
            Size = reader.GetInt64(3),
            CreatedAt = DateTime.Parse(reader.GetString(4)),
            ModifiedAt = DateTime.Parse(reader.GetString(5)),
            LastAccessedAt = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
            Version = reader.GetInt32(10),
            ContentHash = reader.IsDBNull(11) ? null : reader.GetString(11),
            MD5Hash = reader.IsDBNull(12) ? null : reader.GetString(12),
            ContainerId = reader.IsDBNull(13) ? null : reader.GetString(13),
            OwnerId = reader.IsDBNull(14) ? null : reader.GetString(14),
            TextContent = reader.IsDBNull(15) ? null : reader.GetString(15),
            Summary = reader.IsDBNull(16) ? null : reader.GetString(16),
            StorageTier = reader.GetString(17),
            IsDeleted = reader.GetInt32(18) != 0,
            DeletedAt = reader.IsDBNull(19) ? null : DateTime.Parse(reader.GetString(19)),
            ETag = reader.GetString(20)
        };

        // Deserialize custom metadata
        if (!reader.IsDBNull(7))
        {
            var json = reader.GetString(7);
            record.CustomMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOptions)
                                    ?? new Dictionary<string, object>();
        }

        // Deserialize tags
        if (!reader.IsDBNull(8))
        {
            var json = reader.GetString(8);
            record.Tags = JsonSerializer.Deserialize<List<string>>(json, _jsonOptions) ?? new List<string>();
        }

        // Deserialize labels
        if (!reader.IsDBNull(9))
        {
            var json = reader.GetString(9);
            record.Labels = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions)
                            ?? new Dictionary<string, string>();
        }

        return record;
    }

    private void AddRecordParameters(SqliteCommand cmd, MetadataRecord record)
    {
        cmd.Parameters.AddWithValue("@objectId", record.ObjectId);
        cmd.Parameters.AddWithValue("@filename", (object?)record.Filename ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@contentType", (object?)record.ContentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size", record.Size);
        cmd.Parameters.AddWithValue("@createdAt", record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@modifiedAt", record.ModifiedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@lastAccessedAt", record.LastAccessedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@customMetadata", JsonSerializer.Serialize(record.CustomMetadata, _jsonOptions));
        cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(record.Tags, _jsonOptions));
        cmd.Parameters.AddWithValue("@labels", JsonSerializer.Serialize(record.Labels, _jsonOptions));
        cmd.Parameters.AddWithValue("@version", record.Version);
        cmd.Parameters.AddWithValue("@contentHash", (object?)record.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@md5Hash", (object?)record.MD5Hash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@containerId", (object?)record.ContainerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ownerId", (object?)record.OwnerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@textContent", (object?)record.TextContent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@summary", (object?)record.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@storageTier", record.StorageTier);
        cmd.Parameters.AddWithValue("@isDeleted", record.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@deletedAt", record.DeletedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@etag", record.ETag);
    }

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken ct)
    {
        using var cmd = new SqliteCommand(sql, _connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
        _connectionSemaphore.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }
        _connectionSemaphore.Dispose();
    }
}

#endregion

#region File-Based Backend

/// <summary>
/// File-based metadata storage backend using JSON files.
/// </summary>
internal sealed class FileBasedMetadataBackend : IMetadataStorageBackend
{
    private readonly MetadataStorageConfiguration _config;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<string, MetadataRecord> _cache;
    private readonly SemaphoreSlim _fileLock;
    private readonly string _dataDirectory;
    private readonly string _indexPath;
    private bool _disposed;

    public FileBasedMetadataBackend(MetadataStorageConfiguration config, JsonSerializerOptions jsonOptions)
    {
        _config = config;
        _jsonOptions = jsonOptions;
        _cache = new ConcurrentDictionary<string, MetadataRecord>();
        _fileLock = new SemaphoreSlim(1, 1);
        _dataDirectory = Path.Combine(config.StoragePath, "data");
        _indexPath = Path.Combine(config.StoragePath, "index.json");
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_dataDirectory);

        // Load index if exists
        if (File.Exists(_indexPath))
        {
            var indexJson = await File.ReadAllTextAsync(_indexPath, ct);
            var index = JsonSerializer.Deserialize<Dictionary<string, string>>(indexJson, _jsonOptions);
            if (index != null)
            {
                foreach (var kvp in index)
                {
                    var recordPath = Path.Combine(_dataDirectory, kvp.Value);
                    if (File.Exists(recordPath))
                    {
                        var recordJson = await File.ReadAllTextAsync(recordPath, ct);
                        var record = JsonSerializer.Deserialize<MetadataRecord>(recordJson, _jsonOptions);
                        if (record != null)
                        {
                            _cache[kvp.Key] = record;
                        }
                    }
                }
            }
        }
    }

    public Task<MetadataRecord?> GetAsync(string objectId, CancellationToken ct)
    {
        _cache.TryGetValue(objectId, out var record);
        return Task.FromResult(record);
    }

    public async Task UpsertAsync(MetadataRecord record, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            // Check if updating existing
            if (_cache.TryGetValue(record.ObjectId, out var existing))
            {
                record.Version = existing.Version + 1;
            }
            record.ModifiedAt = DateTime.UtcNow;
            record.UpdateETag();

            _cache[record.ObjectId] = record;

            // Write record file
            var filename = GetRecordFilename(record.ObjectId);
            var recordPath = Path.Combine(_dataDirectory, filename);
            var json = JsonSerializer.Serialize(record, _jsonOptions);
            await File.WriteAllTextAsync(recordPath, json, ct);

            // Update index
            await SaveIndexAsync(ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<bool> UpdateAsync(string objectId, MetadataUpdateRequest request, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(objectId, out var existing))
            {
                return false;
            }

            // Check ETag
            if (!string.IsNullOrEmpty(request.ExpectedETag) && existing.ETag != request.ExpectedETag)
            {
                throw new ConcurrencyException($"ETag mismatch. Expected: {request.ExpectedETag}, Actual: {existing.ETag}");
            }

            // Apply updates
            if (request.Filename != null) existing.Filename = request.Filename;
            if (request.ContentType != null) existing.ContentType = request.ContentType;
            if (request.Size.HasValue) existing.Size = request.Size.Value;
            if (request.ContentHash != null) existing.ContentHash = request.ContentHash;
            if (request.StorageTier != null) existing.StorageTier = request.StorageTier;

            if (request.CustomMetadata != null)
            {
                foreach (var kvp in request.CustomMetadata)
                {
                    existing.CustomMetadata[kvp.Key] = kvp.Value;
                }
            }

            if (request.ReplaceTags != null)
            {
                existing.Tags = request.ReplaceTags;
            }
            else
            {
                if (request.AddTags != null)
                {
                    existing.Tags.AddRange(request.AddTags.Where(t => !existing.Tags.Contains(t)));
                }
                if (request.RemoveTags != null)
                {
                    existing.Tags.RemoveAll(t => request.RemoveTags.Contains(t));
                }
            }

            if (request.Labels != null)
            {
                foreach (var kvp in request.Labels)
                {
                    existing.Labels[kvp.Key] = kvp.Value;
                }
            }
            if (request.RemoveLabels != null)
            {
                foreach (var key in request.RemoveLabels)
                {
                    existing.Labels.Remove(key);
                }
            }

            existing.UpdateETag();

            // Save
            var filename = GetRecordFilename(objectId);
            var recordPath = Path.Combine(_dataDirectory, filename);
            var json = JsonSerializer.Serialize(existing, _jsonOptions);
            await File.WriteAllTextAsync(recordPath, json, ct);

            return true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string objectId, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(objectId, out var existing) || existing.IsDeleted)
            {
                return false;
            }

            existing.IsDeleted = true;
            existing.DeletedAt = DateTime.UtcNow;
            existing.UpdateETag();

            // Save
            var filename = GetRecordFilename(objectId);
            var recordPath = Path.Combine(_dataDirectory, filename);
            var json = JsonSerializer.Serialize(existing, _jsonOptions);
            await File.WriteAllTextAsync(recordPath, json, ct);

            return true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public Task<MetadataQueryResult> QueryAsync(MetadataQuery query, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var results = _cache.Values.AsEnumerable();

        // Apply filters
        if (!query.IncludeDeleted)
        {
            results = results.Where(r => !r.IsDeleted);
        }

        if (!string.IsNullOrEmpty(query.TextSearch))
        {
            var search = query.TextSearch.ToLowerInvariant();
            results = results.Where(r =>
                (r.Filename?.ToLowerInvariant().Contains(search) ?? false) ||
                (r.TextContent?.ToLowerInvariant().Contains(search) ?? false) ||
                (r.Summary?.ToLowerInvariant().Contains(search) ?? false));
        }

        if (!string.IsNullOrEmpty(query.ContentType))
        {
            if (query.ContentType.EndsWith("/*"))
            {
                var prefix = query.ContentType[..^2];
                results = results.Where(r => r.ContentType?.StartsWith(prefix) ?? false);
            }
            else
            {
                results = results.Where(r => r.ContentType == query.ContentType);
            }
        }

        if (!string.IsNullOrEmpty(query.ContainerId))
        {
            results = results.Where(r => r.ContainerId == query.ContainerId);
        }

        if (!string.IsNullOrEmpty(query.OwnerId))
        {
            results = results.Where(r => r.OwnerId == query.OwnerId);
        }

        if (!string.IsNullOrEmpty(query.StorageTier))
        {
            results = results.Where(r => r.StorageTier == query.StorageTier);
        }

        if (query.MinSize.HasValue)
        {
            results = results.Where(r => r.Size >= query.MinSize.Value);
        }

        if (query.MaxSize.HasValue)
        {
            results = results.Where(r => r.Size <= query.MaxSize.Value);
        }

        if (query.CreatedAfter.HasValue)
        {
            results = results.Where(r => r.CreatedAt >= query.CreatedAfter.Value);
        }

        if (query.CreatedBefore.HasValue)
        {
            results = results.Where(r => r.CreatedAt <= query.CreatedBefore.Value);
        }

        if (query.ModifiedAfter.HasValue)
        {
            results = results.Where(r => r.ModifiedAt >= query.ModifiedAfter.Value);
        }

        if (query.ModifiedBefore.HasValue)
        {
            results = results.Where(r => r.ModifiedAt <= query.ModifiedBefore.Value);
        }

        if (query.Tags != null && query.Tags.Count > 0)
        {
            results = results.Where(r => query.Tags.All(t => r.Tags.Contains(t)));
        }

        if (query.AnyTags != null && query.AnyTags.Count > 0)
        {
            results = results.Where(r => query.AnyTags.Any(t => r.Tags.Contains(t)));
        }

        if (query.Labels != null && query.Labels.Count > 0)
        {
            results = results.Where(r => query.Labels.All(l =>
                r.Labels.TryGetValue(l.Key, out var v) && v == l.Value));
        }

        // Sort
        results = query.SortBy.ToLowerInvariant() switch
        {
            "size" => query.SortDirection == SortDirection.Ascending
                ? results.OrderBy(r => r.Size)
                : results.OrderByDescending(r => r.Size),
            "filename" => query.SortDirection == SortDirection.Ascending
                ? results.OrderBy(r => r.Filename)
                : results.OrderByDescending(r => r.Filename),
            "modifiedat" or "modified_at" => query.SortDirection == SortDirection.Ascending
                ? results.OrderBy(r => r.ModifiedAt)
                : results.OrderByDescending(r => r.ModifiedAt),
            _ => query.SortDirection == SortDirection.Ascending
                ? results.OrderBy(r => r.CreatedAt)
                : results.OrderByDescending(r => r.CreatedAt)
        };

        var totalCount = results.Count();
        var offset = (query.Page - 1) * query.PageSize;
        var records = results.Skip(offset).Take(query.PageSize).ToList();

        sw.Stop();
        return Task.FromResult(new MetadataQueryResult
        {
            Records = records,
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize,
            ExecutionTime = sw.Elapsed
        });
    }

    public Task<MetadataListResult> ListAsync(MetadataListOptions options, CancellationToken ct)
    {
        var results = _cache.Values.AsEnumerable();

        if (!options.IncludeDeleted)
        {
            results = results.Where(r => !r.IsDeleted);
        }

        if (!string.IsNullOrEmpty(options.Prefix))
        {
            results = results.Where(r =>
                r.ObjectId.StartsWith(options.Prefix) ||
                (r.Filename?.StartsWith(options.Prefix) ?? false));
        }

        if (!string.IsNullOrEmpty(options.ContainerId))
        {
            results = results.Where(r => r.ContainerId == options.ContainerId);
        }

        // Sort
        results = options.SortBy.ToLowerInvariant() switch
        {
            "size" => options.SortDirection == SortDirection.Ascending
                ? results.OrderBy(r => r.Size)
                : results.OrderByDescending(r => r.Size),
            "filename" => options.SortDirection == SortDirection.Ascending
                ? results.OrderBy(r => r.Filename)
                : results.OrderByDescending(r => r.Filename),
            "modifiedat" or "modified_at" => options.SortDirection == SortDirection.Ascending
                ? results.OrderBy(r => r.ModifiedAt)
                : results.OrderByDescending(r => r.ModifiedAt),
            _ => options.SortDirection == SortDirection.Ascending
                ? results.OrderBy(r => r.CreatedAt)
                : results.OrderByDescending(r => r.CreatedAt)
        };

        var totalCount = results.Count();

        // Parse continuation token
        var offset = 0;
        if (!string.IsNullOrEmpty(options.ContinuationToken))
        {
            var tokenBytes = Convert.FromBase64String(options.ContinuationToken);
            offset = int.Parse(Encoding.UTF8.GetString(tokenBytes));
        }

        var records = results.Skip(offset).Take(options.MaxItems + 1).ToList();
        var hasMore = records.Count > options.MaxItems;
        if (hasMore)
        {
            records = records.Take(options.MaxItems).ToList();
        }

        string? continuationToken = null;
        if (hasMore)
        {
            var nextOffset = offset + options.MaxItems;
            continuationToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(nextOffset.ToString()));
        }

        return Task.FromResult(new MetadataListResult
        {
            Records = records,
            ContinuationToken = continuationToken,
            HasMore = hasMore,
            TotalCount = totalCount
        });
    }

    public Task<MetadataStorageStatistics> GetStatisticsAsync(CancellationToken ct)
    {
        var activeRecords = _cache.Values.Where(r => !r.IsDeleted).ToList();
        var deletedRecords = _cache.Values.Where(r => r.IsDeleted).ToList();

        var stats = new MetadataStorageStatistics
        {
            BackendType = "FileBased",
            TotalObjects = activeRecords.Count,
            DeletedObjects = deletedRecords.Count,
            TotalSizeBytes = activeRecords.Sum(r => r.Size),
            AverageObjectSize = activeRecords.Count > 0 ? activeRecords.Average(r => (double)r.Size) : 0,
            LargestObjectSize = activeRecords.Count > 0 ? activeRecords.Max(r => r.Size) : 0,
            SmallestObjectSize = activeRecords.Count > 0 ? activeRecords.Min(r => r.Size) : 0,
            OldestObject = activeRecords.Count > 0 ? activeRecords.Min(r => r.CreatedAt) : null,
            NewestObject = activeRecords.Count > 0 ? activeRecords.Max(r => r.CreatedAt) : null,
            ObjectsByContentType = activeRecords
                .GroupBy(r => r.ContentType ?? "unknown")
                .ToDictionary(g => g.Key, g => (long)g.Count()),
            ObjectsByStorageTier = activeRecords
                .GroupBy(r => r.StorageTier)
                .ToDictionary(g => g.Key, g => (long)g.Count()),
            ObjectsByContainer = activeRecords
                .Where(r => !string.IsNullOrEmpty(r.ContainerId))
                .GroupBy(r => r.ContainerId!)
                .ToDictionary(g => g.Key, g => (long)g.Count())
        };

        // Calculate storage size
        if (Directory.Exists(_dataDirectory))
        {
            var dirInfo = new DirectoryInfo(_dataDirectory);
            stats.StorageSizeBytes = dirInfo.EnumerateFiles("*.json", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }

        return Task.FromResult(stats);
    }

    public async Task<BulkWriteResult> BulkUpsertAsync(IEnumerable<MetadataRecord> records, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new BulkWriteResult();

        await _fileLock.WaitAsync(ct);
        try
        {
            foreach (var record in records)
            {
                try
                {
                    if (_cache.TryGetValue(record.ObjectId, out var existing))
                    {
                        record.Version = existing.Version + 1;
                    }
                    record.ModifiedAt = DateTime.UtcNow;
                    record.UpdateETag();

                    _cache[record.ObjectId] = record;

                    var filename = GetRecordFilename(record.ObjectId);
                    var recordPath = Path.Combine(_dataDirectory, filename);
                    var json = JsonSerializer.Serialize(record, _jsonOptions);
                    await File.WriteAllTextAsync(recordPath, json, ct);

                    result.SuccessCount++;
                }
                catch (Exception ex)
                {
                    result.FailureCount++;
                    result.Failures[record.ObjectId] = ex.Message;
                }
            }

            await SaveIndexAsync(ct);
        }
        finally
        {
            _fileLock.Release();
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        return result;
    }

    public async Task<int> BulkDeleteAsync(IEnumerable<string> objectIds, CancellationToken ct)
    {
        var count = 0;

        await _fileLock.WaitAsync(ct);
        try
        {
            foreach (var objectId in objectIds)
            {
                if (_cache.TryGetValue(objectId, out var existing) && !existing.IsDeleted)
                {
                    existing.IsDeleted = true;
                    existing.DeletedAt = DateTime.UtcNow;
                    existing.UpdateETag();

                    var filename = GetRecordFilename(objectId);
                    var recordPath = Path.Combine(_dataDirectory, filename);
                    var json = JsonSerializer.Serialize(existing, _jsonOptions);
                    await File.WriteAllTextAsync(recordPath, json, ct);

                    count++;
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }

        return count;
    }

    private string GetRecordFilename(string objectId)
    {
        // Use SHA256 hash to avoid filesystem issues with special characters
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(objectId));
        return Convert.ToHexString(hash).ToLowerInvariant() + ".json";
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        var index = _cache.ToDictionary(
            kvp => kvp.Key,
            kvp => GetRecordFilename(kvp.Key));

        var json = JsonSerializer.Serialize(index, _jsonOptions);
        await File.WriteAllTextAsync(_indexPath, json, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fileLock.Dispose();
    }
}

#endregion

#region Exceptions

/// <summary>
/// Exception thrown when there's a concurrency conflict (ETag mismatch).
/// </summary>
public class ConcurrencyException : Exception
{
    public ConcurrencyException(string message) : base(message) { }
    public ConcurrencyException(string message, Exception inner) : base(message, inner) { }
}

#endregion
