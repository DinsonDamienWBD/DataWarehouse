using System.Diagnostics;
using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Deduplication;

/// <summary>
/// Result of a deduplication operation.
/// </summary>
public sealed class DeduplicationResult
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Whether the data was identified as a duplicate.
    /// </summary>
    public bool IsDuplicate { get; init; }

    /// <summary>
    /// The hash of the deduplicated data.
    /// </summary>
    public byte[]? Hash { get; init; }

    /// <summary>
    /// Reference ID if this is a duplicate (points to original).
    /// </summary>
    public string? ReferenceId { get; init; }

    /// <summary>
    /// Original size before deduplication.
    /// </summary>
    public long OriginalSize { get; init; }

    /// <summary>
    /// Size after deduplication (0 if duplicate).
    /// </summary>
    public long StoredSize { get; init; }

    /// <summary>
    /// Number of chunks processed.
    /// </summary>
    public int ChunksProcessed { get; init; }

    /// <summary>
    /// Number of duplicate chunks found.
    /// </summary>
    public int DuplicateChunks { get; init; }

    /// <summary>
    /// Duration of the operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful result for unique data.
    /// </summary>
    public static DeduplicationResult Unique(byte[] hash, long originalSize, long storedSize, int chunks, int duplicateChunks, TimeSpan duration) =>
        new()
        {
            Success = true,
            IsDuplicate = false,
            Hash = hash,
            OriginalSize = originalSize,
            StoredSize = storedSize,
            ChunksProcessed = chunks,
            DuplicateChunks = duplicateChunks,
            Duration = duration
        };

    /// <summary>
    /// Creates a successful result for duplicate data.
    /// </summary>
    public static DeduplicationResult Duplicate(byte[] hash, string referenceId, long originalSize, TimeSpan duration) =>
        new()
        {
            Success = true,
            IsDuplicate = true,
            Hash = hash,
            ReferenceId = referenceId,
            OriginalSize = originalSize,
            StoredSize = 0,
            Duration = duration
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static DeduplicationResult Failed(string error) =>
        new() { Success = false, ErrorMessage = error };
}

/// <summary>
/// Context for deduplication operations.
/// </summary>
public sealed class DeduplicationContext
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public string ObjectId { get; init; } = string.Empty;

    /// <summary>
    /// Content type (MIME type).
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Tenant ID for multi-tenant deduplication.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Whether to perform global deduplication across tenants.
    /// </summary>
    public bool GlobalDedup { get; init; } = true;

    /// <summary>
    /// Minimum chunk size in bytes (for variable chunking).
    /// </summary>
    public int MinChunkSize { get; init; } = 4096;

    /// <summary>
    /// Maximum chunk size in bytes (for variable chunking).
    /// </summary>
    public int MaxChunkSize { get; init; } = 65536;

    /// <summary>
    /// Average chunk size in bytes (for variable chunking).
    /// </summary>
    public int AverageChunkSize { get; init; } = 8192;

    /// <summary>
    /// Fixed block size (for fixed-block deduplication).
    /// </summary>
    public int FixedBlockSize { get; init; } = 8192;

    /// <summary>
    /// Additional metadata for context-aware deduplication.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Statistics for deduplication operations.
/// </summary>
public sealed class DeduplicationStats
{
    /// <summary>
    /// Total bytes processed.
    /// </summary>
    public long TotalBytesProcessed { get; set; }

    /// <summary>
    /// Total bytes saved through deduplication.
    /// </summary>
    public long BytesSaved { get; set; }

    /// <summary>
    /// Total unique chunks stored.
    /// </summary>
    public long UniqueChunks { get; set; }

    /// <summary>
    /// Total duplicate chunks found.
    /// </summary>
    public long DuplicateChunks { get; set; }

    /// <summary>
    /// Total unique objects.
    /// </summary>
    public long UniqueObjects { get; set; }

    /// <summary>
    /// Total duplicate objects (file-level).
    /// </summary>
    public long DuplicateObjects { get; set; }

    /// <summary>
    /// Gets the deduplication ratio (1.0 = no dedup, higher = more savings).
    /// </summary>
    public double DeduplicationRatio =>
        TotalBytesProcessed > 0 ? (double)TotalBytesProcessed / (TotalBytesProcessed - BytesSaved) : 1.0;

    /// <summary>
    /// Gets the space savings percentage.
    /// </summary>
    public double SpaceSavingsPercent =>
        TotalBytesProcessed > 0 ? (double)BytesSaved / TotalBytesProcessed * 100 : 0;
}

/// <summary>
/// A chunk reference with metadata.
/// </summary>
public sealed class ChunkReference
{
    /// <summary>
    /// Hash of the chunk.
    /// </summary>
    public required byte[] Hash { get; init; }

    /// <summary>
    /// Size of the chunk.
    /// </summary>
    public required int Size { get; init; }

    /// <summary>
    /// Offset in the original data.
    /// </summary>
    public required long Offset { get; init; }

    /// <summary>
    /// Reference count for this chunk.
    /// </summary>
    public int ReferenceCount { get; set; } = 1;

    /// <summary>
    /// Storage location identifier.
    /// </summary>
    public string? StorageId { get; init; }
}

/// <summary>
/// Interface for deduplication strategies.
/// </summary>
public interface IDeduplicationStrategy : IDataManagementStrategy
{
    /// <summary>
    /// Deduplicates data from a stream.
    /// </summary>
    /// <param name="data">The data stream to deduplicate.</param>
    /// <param name="context">Deduplication context with settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing deduplication outcome.</returns>
    Task<DeduplicationResult> DeduplicateAsync(Stream data, DeduplicationContext context, CancellationToken ct);

    /// <summary>
    /// Checks if a hash exists in the deduplication index.
    /// </summary>
    /// <param name="hash">The hash to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the hash exists (duplicate).</returns>
    Task<bool> IsDuplicateAsync(byte[] hash, CancellationToken ct);

    /// <summary>
    /// Gets deduplication statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current deduplication statistics.</returns>
    Task<DeduplicationStats> GetStatsAsync(CancellationToken ct);
}

/// <summary>
/// Abstract base class for deduplication strategies.
/// Provides common functionality for hash-based deduplication.
/// </summary>
public abstract class DeduplicationStrategyBase : DataManagementStrategyBase, IDeduplicationStrategy
{
    /// <summary>
    /// Thread-safe hash index mapping hash to object references.
    /// </summary>
    protected readonly BoundedDictionary<string, HashEntry> HashIndex = new BoundedDictionary<string, HashEntry>(1000);

    /// <summary>
    /// Thread-safe chunk index for sub-file deduplication.
    /// </summary>
    protected readonly BoundedDictionary<string, ChunkEntry> ChunkIndex = new BoundedDictionary<string, ChunkEntry>(1000);

    /// <summary>
    /// Lock object for statistics updates.
    /// </summary>
    private readonly object _statsLock = new();

    /// <summary>
    /// Current deduplication statistics.
    /// </summary>
    private readonly DeduplicationStats _stats = new();

    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Lifecycle;

    /// <inheritdoc/>
    public async Task<DeduplicationResult> DeduplicateAsync(Stream data, DeduplicationContext context, CancellationToken ct)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(context);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await DeduplicateCoreAsync(data, context, ct);
            sw.Stop();

            UpdateStats(result);
            RecordWrite(result.OriginalSize, sw.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordFailure();
            return DeduplicationResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsDuplicateAsync(byte[] hash, CancellationToken ct)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(hash);

        return await IsDuplicateCoreAsync(hash, ct);
    }

    /// <inheritdoc/>
    public Task<DeduplicationStats> GetStatsAsync(CancellationToken ct)
    {
        ThrowIfNotInitialized();

        lock (_statsLock)
        {
            return Task.FromResult(new DeduplicationStats
            {
                TotalBytesProcessed = _stats.TotalBytesProcessed,
                BytesSaved = _stats.BytesSaved,
                UniqueChunks = _stats.UniqueChunks,
                DuplicateChunks = _stats.DuplicateChunks,
                UniqueObjects = _stats.UniqueObjects,
                DuplicateObjects = _stats.DuplicateObjects
            });
        }
    }

    /// <summary>
    /// Core deduplication implementation. Must be overridden.
    /// </summary>
    protected abstract Task<DeduplicationResult> DeduplicateCoreAsync(
        Stream data,
        DeduplicationContext context,
        CancellationToken ct);

    /// <summary>
    /// Core duplicate check implementation.
    /// </summary>
    protected virtual Task<bool> IsDuplicateCoreAsync(byte[] hash, CancellationToken ct)
    {
        var hashString = Convert.ToHexString(hash);
        return Task.FromResult(HashIndex.ContainsKey(hashString) || ChunkIndex.ContainsKey(hashString));
    }

    /// <summary>
    /// Updates statistics after a deduplication operation.
    /// </summary>
    protected void UpdateStats(DeduplicationResult result)
    {
        lock (_statsLock)
        {
            _stats.TotalBytesProcessed += result.OriginalSize;
            _stats.BytesSaved += result.OriginalSize - result.StoredSize;

            if (result.IsDuplicate)
            {
                _stats.DuplicateObjects++;
            }
            else
            {
                _stats.UniqueObjects++;
            }

            _stats.DuplicateChunks += result.DuplicateChunks;
            _stats.UniqueChunks += result.ChunksProcessed - result.DuplicateChunks;
        }
    }

    /// <summary>
    /// Computes SHA-256 hash of data.
    /// </summary>
    protected static byte[] ComputeHash(byte[] data)
    {
        return SHA256.HashData(data);
    }

    /// <summary>
    /// Computes SHA-256 hash of a stream.
    /// </summary>
    protected static async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        return await sha256.ComputeHashAsync(data, ct);
    }

    /// <summary>
    /// Converts hash bytes to hex string for indexing.
    /// </summary>
    protected static string HashToString(byte[] hash) => Convert.ToHexString(hash);

    /// <summary>
    /// Converts hex string back to hash bytes.
    /// </summary>
    protected static byte[] StringToHash(string hashString) => Convert.FromHexString(hashString);

    /// <summary>
    /// Entry in the hash index.
    /// </summary>
    protected sealed class HashEntry
    {
        /// <summary>
        /// Object ID that stores this hash.
        /// </summary>
        public required string ObjectId { get; init; }

        /// <summary>
        /// Size of the data.
        /// </summary>
        public required long Size { get; init; }

        /// <summary>
        /// Reference count (field for Interlocked operations).
        /// </summary>
        public int ReferenceCount = 1;

        /// <summary>
        /// When the entry was created.
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Last access time.
        /// </summary>
        public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Increments reference count atomically.
        /// </summary>
        public void IncrementRef() => Interlocked.Increment(ref ReferenceCount);

        /// <summary>
        /// Decrements reference count atomically.
        /// </summary>
        /// <returns>New reference count.</returns>
        public int DecrementRef() => Interlocked.Decrement(ref ReferenceCount);
    }

    /// <summary>
    /// Entry in the chunk index.
    /// </summary>
    protected sealed class ChunkEntry
    {
        /// <summary>
        /// Storage identifier for the chunk.
        /// </summary>
        public required string StorageId { get; init; }

        /// <summary>
        /// Chunk size.
        /// </summary>
        public required int Size { get; init; }

        /// <summary>
        /// Reference count (field for Interlocked operations).
        /// </summary>
        public int ReferenceCount = 1;

        /// <summary>
        /// When the chunk was stored.
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Increments reference count atomically.
        /// </summary>
        public void IncrementRef() => Interlocked.Increment(ref ReferenceCount);

        /// <summary>
        /// Decrements reference count atomically.
        /// </summary>
        /// <returns>New reference count.</returns>
        public int DecrementRef() => Interlocked.Decrement(ref ReferenceCount);
    }
}
