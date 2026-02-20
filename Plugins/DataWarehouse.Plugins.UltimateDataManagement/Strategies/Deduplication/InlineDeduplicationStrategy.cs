using System.Diagnostics;
using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Deduplication;

/// <summary>
/// Real-time inline deduplication strategy that deduplicates data during write operations.
/// Uses a hash lookup table to detect duplicates before storing data.
/// </summary>
/// <remarks>
/// Features:
/// - Synchronous deduplication during write path
/// - SHA-256 hash-based duplicate detection
/// - Zero-copy for duplicates (returns reference)
/// - Thread-safe concurrent access
/// - Low latency optimized for real-time operation
/// </remarks>
public sealed class InlineDeduplicationStrategy : DeduplicationStrategyBase
{
    private readonly BoundedDictionary<string, byte[]> _dataStore = new BoundedDictionary<string, byte[]>(1000);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly int _bufferSize;

    /// <summary>
    /// Initializes a new inline deduplication strategy with default 64KB buffer.
    /// </summary>
    public InlineDeduplicationStrategy() : this(65536) { }

    /// <summary>
    /// Initializes a new inline deduplication strategy with specified buffer size.
    /// </summary>
    /// <param name="bufferSize">Buffer size for reading streams.</param>
    public InlineDeduplicationStrategy(int bufferSize)
    {
        if (bufferSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(bufferSize), "Buffer size must be at least 1KB");

        _bufferSize = bufferSize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "dedup.inline";

    /// <inheritdoc/>
    public override string DisplayName => "Inline Deduplication";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 500_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Real-time inline deduplication that identifies and eliminates duplicate data during write operations. " +
        "Uses SHA-256 hash lookup for instant duplicate detection with zero-copy optimization for duplicates. " +
        "Ideal for write-heavy workloads requiring immediate deduplication.";

    /// <inheritdoc/>
    public override string[] Tags => ["deduplication", "inline", "realtime", "hash", "write-path"];

    /// <inheritdoc/>
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(
        Stream data,
        DeduplicationContext context,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Read all data into memory for hashing
        using var memoryStream = new MemoryStream(65536);
        await data.CopyToAsync(memoryStream, _bufferSize, ct);
        var dataBytes = memoryStream.ToArray();
        var originalSize = dataBytes.Length;

        // Compute hash
        var hash = ComputeHash(dataBytes);
        var hashString = HashToString(hash);

        // Check for duplicate
        if (HashIndex.TryGetValue(hashString, out var existing))
        {
            // Update reference count atomically
            existing.IncrementRef();
            existing.LastAccessedAt = DateTime.UtcNow;

            sw.Stop();
            return DeduplicationResult.Duplicate(hash, existing.ObjectId, originalSize, sw.Elapsed);
        }

        // Not a duplicate - store the data
        await _writeLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (HashIndex.TryGetValue(hashString, out existing))
            {
                existing.IncrementRef();
                existing.LastAccessedAt = DateTime.UtcNow;

                sw.Stop();
                return DeduplicationResult.Duplicate(hash, existing.ObjectId, originalSize, sw.Elapsed);
            }

            // Store data and create hash entry
            _dataStore[context.ObjectId] = dataBytes;
            HashIndex[hashString] = new HashEntry
            {
                ObjectId = context.ObjectId,
                Size = originalSize,
                ReferenceCount = 1
            };

            sw.Stop();
            return DeduplicationResult.Unique(hash, originalSize, originalSize, 1, 0, sw.Elapsed);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Retrieves data by object ID.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns>The data bytes or null if not found.</returns>
    public byte[]? GetData(string objectId)
    {
        return _dataStore.TryGetValue(objectId, out var data) ? data : null;
    }

    /// <summary>
    /// Removes data by object ID and decrements reference counts.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns>True if removed successfully.</returns>
    public bool RemoveData(string objectId)
    {
        if (!_dataStore.TryRemove(objectId, out var data))
            return false;

        var hash = ComputeHash(data);
        var hashString = HashToString(hash);

        if (HashIndex.TryGetValue(hashString, out var entry))
        {
            var newCount = entry.DecrementRef();
            if (newCount <= 0)
            {
                HashIndex.TryRemove(hashString, out _);
            }
        }

        return true;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _dataStore.Clear();
        HashIndex.Clear();
        _writeLock.Dispose();
        return Task.CompletedTask;
    }
}
