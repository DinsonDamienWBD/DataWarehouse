using System.Diagnostics;
using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Deduplication;

/// <summary>
/// Whole-file deduplication strategy using SHA-256 hashing.
/// Provides simple and efficient deduplication at the file level.
/// </summary>
/// <remarks>
/// Features:
/// - SHA-256 whole-file hashing
/// - Fast duplicate detection
/// - Reference counting for shared files
/// - Zero storage for duplicates
/// - Content-addressable storage pattern
/// </remarks>
public sealed class FileLevelDeduplicationStrategy : DeduplicationStrategyBase
{
    private readonly BoundedDictionary<string, byte[]> _fileStore = new BoundedDictionary<string, byte[]>(1000);
    private readonly BoundedDictionary<string, string> _objectToHash = new BoundedDictionary<string, string>(1000);
    private readonly bool _storeInMemory;

    /// <summary>
    /// Initializes with in-memory storage.
    /// </summary>
    public FileLevelDeduplicationStrategy() : this(true) { }

    /// <summary>
    /// Initializes with specified storage mode.
    /// </summary>
    /// <param name="storeInMemory">Whether to store file data in memory.</param>
    public FileLevelDeduplicationStrategy(bool storeInMemory)
    {
        _storeInMemory = storeInMemory;
    }

    /// <inheritdoc/>
    public override string StrategyId => "dedup.filelevel";

    /// <inheritdoc/>
    public override string DisplayName => "File-Level Deduplication";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 500_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Whole-file deduplication using SHA-256 cryptographic hashing. " +
        "Provides content-addressable storage with zero-copy for duplicate files. " +
        "Ideal for file servers and backup systems with many identical files.";

    /// <inheritdoc/>
    public override string[] Tags => ["deduplication", "file-level", "whole-file", "sha256", "content-addressable"];

    /// <summary>
    /// Gets the number of unique files stored.
    /// </summary>
    public int UniqueFileCount => HashIndex.Count;

    /// <summary>
    /// Gets the total number of object references.
    /// </summary>
    public int TotalObjectCount => _objectToHash.Count;

    /// <inheritdoc/>
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(
        Stream data,
        DeduplicationContext context,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Read entire file
        using var memoryStream = new MemoryStream(65536);
        await data.CopyToAsync(memoryStream, ct);
        var fileBytes = memoryStream.ToArray();
        var fileSize = fileBytes.Length;

        // Compute SHA-256 hash
        var hash = ComputeHash(fileBytes);
        var hashString = HashToString(hash);

        // Check for duplicate
        if (HashIndex.TryGetValue(hashString, out var existing))
        {
            // File already exists - just add reference
            Interlocked.Increment(ref existing.ReferenceCount);
            existing.LastAccessedAt = DateTime.UtcNow;

            // Map object to hash
            _objectToHash[context.ObjectId] = hashString;

            sw.Stop();
            return DeduplicationResult.Duplicate(hash, existing.ObjectId, fileSize, sw.Elapsed);
        }

        // New unique file
        HashIndex[hashString] = new HashEntry
        {
            ObjectId = context.ObjectId,
            Size = fileSize,
            ReferenceCount = 1
        };

        _objectToHash[context.ObjectId] = hashString;

        // Store file data if configured
        if (_storeInMemory)
        {
            _fileStore[hashString] = fileBytes;
        }

        sw.Stop();
        return DeduplicationResult.Unique(hash, fileSize, fileSize, 1, 0, sw.Elapsed);
    }

    /// <summary>
    /// Retrieves file data by hash.
    /// </summary>
    /// <param name="hash">SHA-256 hash of the file.</param>
    /// <returns>File data or null if not found.</returns>
    public byte[]? GetByHash(byte[] hash)
    {
        var hashString = HashToString(hash);
        return _fileStore.TryGetValue(hashString, out var data) ? data : null;
    }

    /// <summary>
    /// Retrieves file data by object ID.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>File data or null if not found.</returns>
    public byte[]? GetByObjectId(string objectId)
    {
        if (!_objectToHash.TryGetValue(objectId, out var hashString))
            return null;

        return _fileStore.TryGetValue(hashString, out var data) ? data : null;
    }

    /// <summary>
    /// Gets the hash for an object ID.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>Hash bytes or null if not found.</returns>
    public byte[]? GetHashForObject(string objectId)
    {
        if (!_objectToHash.TryGetValue(objectId, out var hashString))
            return null;

        return StringToHash(hashString);
    }

    /// <summary>
    /// Removes an object reference and decrements the reference count.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool RemoveObject(string objectId)
    {
        if (!_objectToHash.TryRemove(objectId, out var hashString))
            return false;

        if (HashIndex.TryGetValue(hashString, out var entry))
        {
            var newCount = Interlocked.Decrement(ref entry.ReferenceCount);
            if (newCount <= 0)
            {
                // No more references - remove file
                HashIndex.TryRemove(hashString, out _);
                _fileStore.TryRemove(hashString, out _);
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if an object exists.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>True if the object exists.</returns>
    public bool ObjectExists(string objectId)
    {
        return _objectToHash.ContainsKey(objectId);
    }

    /// <summary>
    /// Gets the reference count for a hash.
    /// </summary>
    /// <param name="hash">File hash.</param>
    /// <returns>Reference count or 0 if not found.</returns>
    public int GetReferenceCount(byte[] hash)
    {
        var hashString = HashToString(hash);
        return HashIndex.TryGetValue(hashString, out var entry) ? entry.ReferenceCount : 0;
    }

    /// <summary>
    /// Lists all objects referencing a specific hash.
    /// </summary>
    /// <param name="hash">File hash.</param>
    /// <returns>List of object IDs.</returns>
    public IReadOnlyList<string> GetObjectsForHash(byte[] hash)
    {
        var hashString = HashToString(hash);
        return _objectToHash
            .Where(kv => kv.Value == hashString)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _fileStore.Clear();
        _objectToHash.Clear();
        HashIndex.Clear();
        return Task.CompletedTask;
    }
}
