using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Deduplication;

/// <summary>
/// Sub-file chunk deduplication with reference counting.
/// Combines variable-size chunking with sophisticated reference management.
/// </summary>
/// <remarks>
/// Features:
/// - Sub-file granularity deduplication
/// - Reference counting per chunk
/// - Garbage collection for orphaned chunks
/// - Space reclamation on delete
/// - Cross-file deduplication
/// </remarks>
public sealed class SubFileDeduplicationStrategy : DeduplicationStrategyBase
{
    private readonly ConcurrentDictionary<string, ChunkData> _chunkStore = new();
    private readonly ConcurrentDictionary<string, FileManifest> _fileManifests = new();
    private readonly SemaphoreSlim _gcLock = new(1, 1);
    private readonly Timer _gcTimer;
    private readonly int _chunkSize;
    private readonly bool _autoGc;
    private long _totalStoredBytes;
    private long _totalLogicalBytes;

    /// <summary>
    /// Initializes with default 8KB chunk size and automatic garbage collection.
    /// </summary>
    public SubFileDeduplicationStrategy() : this(8192, true) { }

    /// <summary>
    /// Initializes with specified chunk size and GC setting.
    /// </summary>
    /// <param name="chunkSize">Chunk size in bytes.</param>
    /// <param name="autoGc">Whether to run automatic garbage collection.</param>
    public SubFileDeduplicationStrategy(int chunkSize, bool autoGc)
    {
        if (chunkSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be at least 1KB");

        _chunkSize = chunkSize;
        _autoGc = autoGc;

        if (_autoGc)
        {
            _gcTimer = new Timer(RunGarbageCollection, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
        else
        {
            _gcTimer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <inheritdoc/>
    public override string StrategyId => "dedup.subfile";

    /// <inheritdoc/>
    public override string DisplayName => "Sub-File Deduplication";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 150_000,
        TypicalLatencyMs = 2.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Sub-file chunk deduplication with reference counting and garbage collection. " +
        "Provides fine-grained deduplication with automatic space reclamation when chunks are no longer referenced. " +
        "Ideal for versioned storage and incremental backup systems.";

    /// <inheritdoc/>
    public override string[] Tags => ["deduplication", "sub-file", "reference-counting", "garbage-collection", "chunk"];

    /// <summary>
    /// Gets the total stored bytes (physical).
    /// </summary>
    public long TotalStoredBytes => Interlocked.Read(ref _totalStoredBytes);

    /// <summary>
    /// Gets the total logical bytes across all files.
    /// </summary>
    public long TotalLogicalBytes => Interlocked.Read(ref _totalLogicalBytes);

    /// <summary>
    /// Gets the deduplication ratio.
    /// </summary>
    public double DeduplicationRatio =>
        _totalStoredBytes > 0 ? (double)_totalLogicalBytes / _totalStoredBytes : 1.0;

    /// <summary>
    /// Gets the number of unique chunks.
    /// </summary>
    public int UniqueChunkCount => _chunkStore.Count;

    /// <summary>
    /// Gets the number of files tracked.
    /// </summary>
    public int FileCount => _fileManifests.Count;

    /// <inheritdoc/>
    protected override async Task<DeduplicationResult> DeduplicateCoreAsync(
        Stream data,
        DeduplicationContext context,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Read data
        using var memoryStream = new MemoryStream(65536);
        await data.CopyToAsync(memoryStream, ct);
        var fileBytes = memoryStream.ToArray();
        var totalSize = fileBytes.Length;

        if (totalSize == 0)
        {
            sw.Stop();
            return DeduplicationResult.Unique(Array.Empty<byte>(), 0, 0, 0, 0, sw.Elapsed);
        }

        var effectiveChunkSize = context.FixedBlockSize > 0 ? context.FixedBlockSize : _chunkSize;
        var chunkRefs = new List<ChunkRef>();
        int duplicateChunks = 0;
        long storedSize = 0;
        int offset = 0;

        // Process chunks
        while (offset < fileBytes.Length)
        {
            ct.ThrowIfCancellationRequested();

            var size = Math.Min(effectiveChunkSize, fileBytes.Length - offset);
            var chunkBytes = new byte[size];
            Array.Copy(fileBytes, offset, chunkBytes, 0, size);

            var hash = ComputeHash(chunkBytes);
            var hashString = HashToString(hash);

            if (_chunkStore.TryGetValue(hashString, out var existingChunk))
            {
                // Duplicate chunk - increment reference
                Interlocked.Increment(ref existingChunk.ReferenceCount);
                duplicateChunks++;
            }
            else
            {
                // New chunk - store it
                var newChunk = new ChunkData
                {
                    Hash = hashString,
                    Data = chunkBytes,
                    Size = size,
                    ReferenceCount = 1,
                    CreatedAt = DateTime.UtcNow
                };

                if (_chunkStore.TryAdd(hashString, newChunk))
                {
                    Interlocked.Add(ref _totalStoredBytes, size);
                    storedSize += size;
                }
                else
                {
                    // Concurrent add - increment existing
                    if (_chunkStore.TryGetValue(hashString, out existingChunk))
                    {
                        Interlocked.Increment(ref existingChunk.ReferenceCount);
                        duplicateChunks++;
                    }
                }
            }

            chunkRefs.Add(new ChunkRef
            {
                Hash = hashString,
                Offset = offset,
                Size = size
            });

            offset += size;
        }

        // Create file manifest
        var manifest = new FileManifest
        {
            ObjectId = context.ObjectId,
            Chunks = chunkRefs,
            TotalSize = totalSize,
            ChunkSize = effectiveChunkSize,
            CreatedAt = DateTime.UtcNow
        };

        // If replacing existing file, decrement old chunk references
        if (_fileManifests.TryGetValue(context.ObjectId, out var oldManifest))
        {
            DecrementChunkReferences(oldManifest.Chunks);
            Interlocked.Add(ref _totalLogicalBytes, -oldManifest.TotalSize);
        }

        _fileManifests[context.ObjectId] = manifest;
        Interlocked.Add(ref _totalLogicalBytes, totalSize);

        // Compute file hash
        var fileHash = SHA256.HashData(fileBytes);

        sw.Stop();

        if (chunkRefs.Count == duplicateChunks && chunkRefs.Count > 0)
        {
            return DeduplicationResult.Duplicate(fileHash, FindDuplicateFile(chunkRefs), totalSize, sw.Elapsed);
        }

        return DeduplicationResult.Unique(fileHash, totalSize, storedSize, chunkRefs.Count, duplicateChunks, sw.Elapsed);
    }

    private string FindDuplicateFile(List<ChunkRef> chunks)
    {
        var targetHashes = chunks.Select(c => c.Hash).ToList();

        foreach (var manifest in _fileManifests.Values)
        {
            if (manifest.Chunks.Select(c => c.Hash).SequenceEqual(targetHashes))
            {
                return manifest.ObjectId;
            }
        }

        return chunks.FirstOrDefault()?.Hash ?? string.Empty;
    }

    /// <summary>
    /// Reconstructs file data from its manifest.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>File data stream or null if not found.</returns>
    public async Task<Stream?> ReconstructAsync(string objectId, CancellationToken ct = default)
    {
        if (!_fileManifests.TryGetValue(objectId, out var manifest))
            return null;

        var memoryStream = new MemoryStream((int)manifest.TotalSize);

        foreach (var chunkRef in manifest.Chunks.OrderBy(c => c.Offset))
        {
            ct.ThrowIfCancellationRequested();

            if (!_chunkStore.TryGetValue(chunkRef.Hash, out var chunk))
            {
                // Chunk missing - data corruption
                memoryStream.Dispose();
                return null;
            }

            await memoryStream.WriteAsync(chunk.Data, ct);
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <summary>
    /// Removes a file and decrements chunk references.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>True if removed successfully.</returns>
    public bool RemoveFile(string objectId)
    {
        if (!_fileManifests.TryRemove(objectId, out var manifest))
            return false;

        DecrementChunkReferences(manifest.Chunks);
        Interlocked.Add(ref _totalLogicalBytes, -manifest.TotalSize);

        return true;
    }

    private void DecrementChunkReferences(List<ChunkRef> chunks)
    {
        foreach (var chunkRef in chunks)
        {
            if (_chunkStore.TryGetValue(chunkRef.Hash, out var chunk))
            {
                var newCount = Interlocked.Decrement(ref chunk.ReferenceCount);
                if (newCount <= 0)
                {
                    chunk.MarkedForDeletion = true;
                }
            }
        }
    }

    /// <summary>
    /// Manually triggers garbage collection of orphaned chunks.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of chunks collected.</returns>
    public async Task<int> CollectGarbageAsync(CancellationToken ct = default)
    {
        if (!await _gcLock.WaitAsync(0, ct))
            return 0;

        try
        {
            return CollectGarbageInternal();
        }
        finally
        {
            _gcLock.Release();
        }
    }

    private void RunGarbageCollection(object? state)
    {
        if (!_gcLock.Wait(0))
            return;

        try
        {
            CollectGarbageInternal();
        }
        finally
        {
            _gcLock.Release();
        }
    }

    private int CollectGarbageInternal()
    {
        var collected = 0;
        var toRemove = _chunkStore
            .Where(kv => kv.Value.MarkedForDeletion || kv.Value.ReferenceCount <= 0)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var hash in toRemove)
        {
            if (_chunkStore.TryRemove(hash, out var chunk))
            {
                Interlocked.Add(ref _totalStoredBytes, -chunk.Size);
                collected++;
            }
        }

        return collected;
    }

    /// <summary>
    /// Gets detailed chunk statistics.
    /// </summary>
    /// <returns>Chunk statistics.</returns>
    public ChunkStatistics GetChunkStatistics()
    {
        var chunks = _chunkStore.Values.ToList();

        return new ChunkStatistics
        {
            TotalChunks = chunks.Count,
            TotalSize = chunks.Sum(c => (long)c.Size),
            AverageSize = chunks.Count > 0 ? chunks.Average(c => c.Size) : 0,
            MinSize = chunks.Count > 0 ? chunks.Min(c => c.Size) : 0,
            MaxSize = chunks.Count > 0 ? chunks.Max(c => c.Size) : 0,
            TotalReferences = chunks.Sum(c => (long)c.ReferenceCount),
            AverageReferences = chunks.Count > 0 ? chunks.Average(c => c.ReferenceCount) : 0,
            OrphanedChunks = chunks.Count(c => c.ReferenceCount <= 0 || c.MarkedForDeletion)
        };
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _gcTimer.Dispose();
        _gcLock.Dispose();
        _chunkStore.Clear();
        _fileManifests.Clear();
        return Task.CompletedTask;
    }

    private sealed class ChunkData
    {
        public required string Hash { get; init; }
        public required byte[] Data { get; init; }
        public required int Size { get; init; }
        public int ReferenceCount;
        public bool MarkedForDeletion;
        public required DateTime CreatedAt { get; init; }
    }

    private sealed class ChunkRef
    {
        public required string Hash { get; init; }
        public required int Offset { get; init; }
        public required int Size { get; init; }
    }

    private sealed class FileManifest
    {
        public required string ObjectId { get; init; }
        public required List<ChunkRef> Chunks { get; init; }
        public required long TotalSize { get; init; }
        public required int ChunkSize { get; init; }
        public required DateTime CreatedAt { get; init; }
    }
}

/// <summary>
/// Statistics about chunks in the deduplication store.
/// </summary>
public sealed class ChunkStatistics
{
    /// <summary>
    /// Total number of unique chunks.
    /// </summary>
    public int TotalChunks { get; init; }

    /// <summary>
    /// Total size of all chunks.
    /// </summary>
    public long TotalSize { get; init; }

    /// <summary>
    /// Average chunk size.
    /// </summary>
    public double AverageSize { get; init; }

    /// <summary>
    /// Minimum chunk size.
    /// </summary>
    public int MinSize { get; init; }

    /// <summary>
    /// Maximum chunk size.
    /// </summary>
    public int MaxSize { get; init; }

    /// <summary>
    /// Total reference count across all chunks.
    /// </summary>
    public long TotalReferences { get; init; }

    /// <summary>
    /// Average references per chunk.
    /// </summary>
    public double AverageReferences { get; init; }

    /// <summary>
    /// Number of orphaned chunks (zero references).
    /// </summary>
    public int OrphanedChunks { get; init; }
}
