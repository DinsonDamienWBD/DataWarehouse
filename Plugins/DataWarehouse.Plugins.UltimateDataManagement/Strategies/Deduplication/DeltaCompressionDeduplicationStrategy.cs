using System.Diagnostics;
using System.Security.Cryptography;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Deduplication;

/// <summary>
/// Delta compression combined with deduplication for versioned data.
/// Stores only differences between versions while maintaining deduplication.
/// </summary>
/// <remarks>
/// Features:
/// - Delta encoding between versions
/// - Deduplication of base and delta chunks
/// - Version chain management
/// - Efficient storage for frequently modified files
/// - Configurable delta threshold
/// </remarks>
public sealed class DeltaCompressionDeduplicationStrategy : DeduplicationStrategyBase
{
    private readonly BoundedDictionary<string, byte[]> _chunkStore = new BoundedDictionary<string, byte[]>(1000);
    private readonly BoundedDictionary<string, VersionChain> _versionChains = new BoundedDictionary<string, VersionChain>(1000);
    private readonly BoundedDictionary<string, VersionEntry> _versions = new BoundedDictionary<string, VersionEntry>(1000);
    private readonly double _deltaThreshold;
    private readonly int _maxChainLength;
    private readonly int _chunkSize;

    /// <summary>
    /// Initializes with default settings (50% delta threshold, max 10 versions in chain).
    /// </summary>
    public DeltaCompressionDeduplicationStrategy() : this(0.5, 10, 8192) { }

    /// <summary>
    /// Initializes with specified settings.
    /// </summary>
    /// <param name="deltaThreshold">Ratio threshold for delta vs full storage (0-1).</param>
    /// <param name="maxChainLength">Maximum versions before creating new base.</param>
    /// <param name="chunkSize">Chunk size for delta computation.</param>
    public DeltaCompressionDeduplicationStrategy(double deltaThreshold, int maxChainLength, int chunkSize)
    {
        if (deltaThreshold < 0 || deltaThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(deltaThreshold), "Delta threshold must be between 0 and 1");
        if (maxChainLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maxChainLength), "Max chain length must be at least 1");
        if (chunkSize < 512)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be at least 512 bytes");

        _deltaThreshold = deltaThreshold;
        _maxChainLength = maxChainLength;
        _chunkSize = chunkSize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "dedup.delta";

    /// <inheritdoc/>
    public override string DisplayName => "Delta Compression Deduplication";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 5.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Delta compression with deduplication for versioned data. " +
        "Stores only differences between versions while deduplicating common chunks. " +
        "Ideal for version control systems and document management with frequent updates.";

    /// <inheritdoc/>
    public override string[] Tags => ["deduplication", "delta", "versioning", "compression", "incremental"];

    /// <summary>
    /// Gets the delta threshold ratio.
    /// </summary>
    public double DeltaThreshold => _deltaThreshold;

    /// <summary>
    /// Gets the maximum chain length.
    /// </summary>
    public int MaxChainLength => _maxChainLength;

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
        var newData = memoryStream.ToArray();
        var totalSize = newData.Length;

        if (totalSize == 0)
        {
            sw.Stop();
            return DeduplicationResult.Unique(Array.Empty<byte>(), 0, 0, 0, 0, sw.Elapsed);
        }

        var fileHash = ComputeHash(newData);
        var hashString = HashToString(fileHash);

        // Check for exact duplicate
        if (HashIndex.TryGetValue(hashString, out var existingEntry))
        {
            Interlocked.Increment(ref existingEntry.ReferenceCount);
            existingEntry.LastAccessedAt = DateTime.UtcNow;

            sw.Stop();
            return DeduplicationResult.Duplicate(fileHash, existingEntry.ObjectId, totalSize, sw.Elapsed);
        }

        // Get or create version chain for this object
        var chainId = GetChainId(context.ObjectId);
        var chain = _versionChains.GetOrAdd(chainId, _ => new VersionChain
        {
            ChainId = chainId,
            Versions = new List<string>()
        });

        long storedSize;
        int chunks;
        int duplicateChunks;

        // Try to create delta from previous version
        if (chain.Versions.Count > 0 && chain.Versions.Count < _maxChainLength)
        {
            var previousVersionId = chain.Versions[^1];
            if (_versions.TryGetValue(previousVersionId, out var previousVersion))
            {
                var previousData = await ReconstructDataAsync(previousVersion, ct);
                if (previousData != null)
                {
                    var delta = ComputeDelta(previousData, newData);
                    var deltaRatio = (double)delta.Length / totalSize;

                    if (deltaRatio < _deltaThreshold)
                    {
                        // Store as delta
                        var result = StoreDelta(context.ObjectId, delta, previousVersionId, fileHash, totalSize, ct);
                        chain.Versions.Add(context.ObjectId);

                        sw.Stop();
                        return DeduplicationResult.Unique(fileHash, totalSize, result.StoredSize, result.Chunks, result.DuplicateChunks, sw.Elapsed);
                    }
                }
            }
        }

        // Store as new base (full copy or chain too long)
        var baseResult = StoreBase(context.ObjectId, newData, fileHash, totalSize, ct);
        storedSize = baseResult.StoredSize;
        chunks = baseResult.Chunks;
        duplicateChunks = baseResult.DuplicateChunks;

        // Start new chain if needed
        if (chain.Versions.Count >= _maxChainLength)
        {
            chain.Versions.Clear();
        }
        chain.Versions.Add(context.ObjectId);

        sw.Stop();
        return DeduplicationResult.Unique(fileHash, totalSize, storedSize, chunks, duplicateChunks, sw.Elapsed);
    }

    private (long StoredSize, int Chunks, int DuplicateChunks) StoreBase(
        string objectId,
        byte[] data,
        byte[] hash,
        long totalSize,
        CancellationToken ct)
    {
        var chunkRefs = new List<ChunkRef>();
        long storedSize = 0;
        int duplicateChunks = 0;
        int offset = 0;

        while (offset < data.Length)
        {
            ct.ThrowIfCancellationRequested();

            var size = Math.Min(_chunkSize, data.Length - offset);
            var chunkData = new byte[size];
            Array.Copy(data, offset, chunkData, 0, size);

            var chunkHash = ComputeHash(chunkData);
            var chunkHashString = HashToString(chunkHash);

            if (ChunkIndex.TryGetValue(chunkHashString, out var existing))
            {
                Interlocked.Increment(ref existing.ReferenceCount);
                duplicateChunks++;
            }
            else
            {
                _chunkStore[chunkHashString] = chunkData;
                ChunkIndex[chunkHashString] = new ChunkEntry
                {
                    StorageId = chunkHashString,
                    Size = size,
                    ReferenceCount = 1
                };
                storedSize += size;
            }

            chunkRefs.Add(new ChunkRef { Hash = chunkHashString, Offset = offset, Size = size });
            offset += size;
        }

        var version = new VersionEntry
        {
            VersionId = objectId,
            IsBase = true,
            BaseVersionId = null,
            ChunkRefs = chunkRefs,
            DeltaData = null,
            TotalSize = totalSize,
            Hash = HashToString(hash),
            CreatedAt = DateTime.UtcNow
        };

        _versions[objectId] = version;
        HashIndex[version.Hash] = new HashEntry
        {
            ObjectId = objectId,
            Size = totalSize,
            ReferenceCount = 1
        };

        return (storedSize, chunkRefs.Count, duplicateChunks);
    }

    private (long StoredSize, int Chunks, int DuplicateChunks) StoreDelta(
        string objectId,
        byte[] delta,
        string baseVersionId,
        byte[] hash,
        long totalSize,
        CancellationToken ct)
    {
        // Store delta as chunks
        var chunkRefs = new List<ChunkRef>();
        long storedSize = 0;
        int duplicateChunks = 0;
        int offset = 0;

        while (offset < delta.Length)
        {
            ct.ThrowIfCancellationRequested();

            var size = Math.Min(_chunkSize, delta.Length - offset);
            var chunkData = new byte[size];
            Array.Copy(delta, offset, chunkData, 0, size);

            var chunkHash = ComputeHash(chunkData);
            var chunkHashString = HashToString(chunkHash);

            if (ChunkIndex.TryGetValue(chunkHashString, out var existing))
            {
                Interlocked.Increment(ref existing.ReferenceCount);
                duplicateChunks++;
            }
            else
            {
                _chunkStore[chunkHashString] = chunkData;
                ChunkIndex[chunkHashString] = new ChunkEntry
                {
                    StorageId = chunkHashString,
                    Size = size,
                    ReferenceCount = 1
                };
                storedSize += size;
            }

            chunkRefs.Add(new ChunkRef { Hash = chunkHashString, Offset = offset, Size = size });
            offset += size;
        }

        var version = new VersionEntry
        {
            VersionId = objectId,
            IsBase = false,
            BaseVersionId = baseVersionId,
            ChunkRefs = chunkRefs,
            DeltaData = null,
            TotalSize = totalSize,
            Hash = HashToString(hash),
            CreatedAt = DateTime.UtcNow
        };

        _versions[objectId] = version;
        HashIndex[version.Hash] = new HashEntry
        {
            ObjectId = objectId,
            Size = totalSize,
            ReferenceCount = 1
        };

        return (storedSize, chunkRefs.Count, duplicateChunks);
    }

    private const int MinMatchLength = 8;
    private const int HashBlockSize = 4;

    private byte[] ComputeDelta(byte[] oldData, byte[] newData)
    {
        // O(n+m) delta using a 4-byte fingerprint hash table for match discovery.
        // Build a hash table: fingerprint(oldData[i..i+4]) -> list of offsets in oldData.
        // Then for each position in newData, look up potential matches in O(1) amortized.
        using var deltaStream = new MemoryStream(Math.Max(65536, newData.Length / 4));
        using var writer = new BinaryWriter(deltaStream);

        writer.Write(oldData.Length);
        writer.Write(newData.Length);

        if (oldData.Length < HashBlockSize || newData.Length == 0)
        {
            // No matches possible; emit newData as one literal block
            writer.Write((byte)0);
            writer.Write(newData.Length);
            writer.Write(newData, 0, newData.Length);
            return deltaStream.ToArray();
        }

        // Build fingerprint index for oldData
        var hashIndex = new Dictionary<int, List<int>>(oldData.Length);
        for (int i = 0; i <= oldData.Length - HashBlockSize; i++)
        {
            var fp = FourByteFingerprint(oldData, i);
            if (!hashIndex.TryGetValue(fp, out var offsets))
            {
                offsets = new List<int>(4);
                hashIndex[fp] = offsets;
            }
            offsets.Add(i);
        }

        int newPos = 0;
        int literalStart = 0;

        while (newPos <= newData.Length - HashBlockSize)
        {
            var fp = FourByteFingerprint(newData, newPos);
            var bestLen = 0;
            var bestOff = -1;

            if (hashIndex.TryGetValue(fp, out var candidates))
            {
                foreach (var cand in candidates)
                {
                    var len = GetMatchLength(oldData, cand, newData, newPos);
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestOff = cand;
                        if (len >= 256) break; // good enough
                    }
                }
            }

            if (bestLen >= MinMatchLength)
            {
                // Flush preceding literals
                if (newPos > literalStart)
                {
                    var litLen = newPos - literalStart;
                    writer.Write((byte)0);
                    writer.Write(litLen);
                    writer.Write(newData, literalStart, litLen);
                }

                writer.Write((byte)1);
                writer.Write(bestOff);
                writer.Write(bestLen);
                newPos += bestLen;
                literalStart = newPos;
            }
            else
            {
                newPos++;
            }
        }

        // Flush any remaining bytes as literal
        var remaining = newData.Length - literalStart;
        if (remaining > 0)
        {
            writer.Write((byte)0);
            writer.Write(remaining);
            writer.Write(newData, literalStart, remaining);
        }

        return deltaStream.ToArray();
    }

    private static int FourByteFingerprint(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    private static int GetMatchLength(byte[] source, int sourcePos, byte[] target, int targetPos)
    {
        int length = 0;
        while (sourcePos + length < source.Length &&
               targetPos + length < target.Length &&
               source[sourcePos + length] == target[targetPos + length])
        {
            length++;
        }
        return length;
    }

    private async Task<byte[]?> ReconstructDataAsync(VersionEntry version, CancellationToken ct)
    {
        if (version.IsBase)
        {
            return ReconstructFromChunks(version.ChunkRefs);
        }

        // Reconstruct from base + delta chain
        if (string.IsNullOrEmpty(version.BaseVersionId) ||
            !_versions.TryGetValue(version.BaseVersionId, out var baseVersion))
        {
            return null;
        }

        var baseData = await ReconstructDataAsync(baseVersion, ct);
        if (baseData == null)
            return null;

        var deltaData = ReconstructFromChunks(version.ChunkRefs);
        if (deltaData == null)
            return null;

        return ApplyDelta(baseData, deltaData);
    }

    private byte[]? ReconstructFromChunks(List<ChunkRef> chunkRefs)
    {
        using var stream = new MemoryStream(65536);

        foreach (var chunkRef in chunkRefs.OrderBy(c => c.Offset))
        {
            if (!_chunkStore.TryGetValue(chunkRef.Hash, out var chunkData))
                return null;

            stream.Write(chunkData, 0, chunkData.Length);
        }

        return stream.ToArray();
    }

    private byte[] ApplyDelta(byte[] baseData, byte[] deltaData)
    {
        using var reader = new BinaryReader(new MemoryStream(deltaData));
        var oldLength = reader.ReadInt32();
        var newLength = reader.ReadInt32();

        var result = new byte[newLength];
        var resultPos = 0;

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var op = reader.ReadByte();

            if (op == 1) // Copy
            {
                var offset = reader.ReadInt32();
                var length = reader.ReadInt32();
                Array.Copy(baseData, offset, result, resultPos, length);
                resultPos += length;
            }
            else // Literal
            {
                var length = reader.ReadInt32();
                var data = reader.ReadBytes(length);
                Array.Copy(data, 0, result, resultPos, length);
                resultPos += length;
            }
        }

        return result;
    }

    private static string GetChainId(string objectId)
    {
        // Extract base object ID (remove version suffix if present)
        var lastDash = objectId.LastIndexOf('-');
        return lastDash > 0 ? objectId[..lastDash] : objectId;
    }

    /// <summary>
    /// Reconstructs data for a version.
    /// </summary>
    /// <param name="versionId">Version identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reconstructed data or null.</returns>
    public async Task<byte[]?> GetVersionDataAsync(string versionId, CancellationToken ct = default)
    {
        if (!_versions.TryGetValue(versionId, out var version))
            return null;

        return await ReconstructDataAsync(version, ct);
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _chunkStore.Clear();
        _versionChains.Clear();
        _versions.Clear();
        HashIndex.Clear();
        ChunkIndex.Clear();
        return Task.CompletedTask;
    }

    private sealed class ChunkRef
    {
        public required string Hash { get; init; }
        public required int Offset { get; init; }
        public required int Size { get; init; }
    }

    private sealed class VersionEntry
    {
        public required string VersionId { get; init; }
        public required bool IsBase { get; init; }
        public string? BaseVersionId { get; init; }
        public required List<ChunkRef> ChunkRefs { get; init; }
        public byte[]? DeltaData { get; init; }
        public required long TotalSize { get; init; }
        public required string Hash { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    private sealed class VersionChain
    {
        public required string ChainId { get; init; }
        public required List<string> Versions { get; init; }
    }
}
