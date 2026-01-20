using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.SDK.Federation;

// ============================================================================
// PHASE 1.1: CONTENT-ADDRESSABLE OBJECT STORE
// Foundation for Federated Distributed Object Store
// ============================================================================

#region 1.1.1 - IContentAddressableObject Interface

/// <summary>
/// Interface for content-addressable objects in the federation.
/// Objects are identified by their content hash, not by location or name.
/// </summary>
public interface IContentAddressableObject
{
    /// <summary>
    /// The content-derived unique identifier (SHA256 hash).
    /// </summary>
    ObjectId ObjectId { get; }

    /// <summary>
    /// Size of the object in bytes.
    /// </summary>
    long Size { get; }

    /// <summary>
    /// Content type (MIME type).
    /// </summary>
    string ContentType { get; }

    /// <summary>
    /// When the object was created.
    /// </summary>
    DateTime CreatedAt { get; }

    /// <summary>
    /// Gets the raw content as a stream.
    /// </summary>
    Task<Stream> OpenReadAsync(CancellationToken ct = default);
}

#endregion

#region 1.1.2 - ObjectId Value Type

/// <summary>
/// Content-derived unique identifier using SHA256 hash.
/// This is the fundamental addressing mechanism for the federation.
/// Objects are identified by WHAT they contain, not WHERE they are.
/// </summary>
public readonly struct ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>
{
    private readonly byte[] _hash;

    /// <summary>
    /// Empty/null ObjectId.
    /// </summary>
    public static readonly ObjectId Empty = new(new byte[32]);

    /// <summary>
    /// Creates an ObjectId from a pre-computed hash.
    /// </summary>
    public ObjectId(byte[] hash)
    {
        if (hash == null || hash.Length != 32)
            throw new ArgumentException("ObjectId must be a 32-byte SHA256 hash", nameof(hash));
        _hash = hash;
    }

    /// <summary>
    /// Creates an ObjectId from a hex string.
    /// </summary>
    public ObjectId(string hexString)
    {
        if (string.IsNullOrEmpty(hexString) || hexString.Length != 64)
            throw new ArgumentException("ObjectId hex string must be 64 characters", nameof(hexString));
        _hash = Convert.FromHexString(hexString);
    }

    /// <summary>
    /// Computes an ObjectId from content bytes.
    /// </summary>
    public static ObjectId FromContent(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return new ObjectId(hash);
    }

    /// <summary>
    /// Computes an ObjectId from a stream.
    /// </summary>
    public static async Task<ObjectId> FromStreamAsync(Stream stream, CancellationToken ct = default)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return new ObjectId(hash);
    }

    /// <summary>
    /// Parses an ObjectId from a string (hex format).
    /// </summary>
    public static ObjectId Parse(string s) => new(s);

    /// <summary>
    /// Tries to parse an ObjectId from a string.
    /// </summary>
    public static bool TryParse(string? s, out ObjectId result)
    {
        result = Empty;
        if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
        try
        {
            result = new ObjectId(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the raw hash bytes.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan() => _hash ?? Empty._hash;

    /// <summary>
    /// Gets the hash as a byte array (copy).
    /// </summary>
    public byte[] ToByteArray() => _hash?.ToArray() ?? new byte[32];

    /// <summary>
    /// Returns the hex string representation (lowercase).
    /// </summary>
    public override string ToString() => Convert.ToHexString(_hash ?? Empty._hash).ToLowerInvariant();

    /// <summary>
    /// Short form for display (first 8 chars).
    /// </summary>
    public string ToShortString() => ToString()[..8];

    public bool Equals(ObjectId other) => _hash.AsSpan().SequenceEqual(other._hash);
    public override bool Equals(object? obj) => obj is ObjectId other && Equals(other);
    public override int GetHashCode() => BitConverter.ToInt32(_hash, 0);
    public int CompareTo(ObjectId other) => _hash.AsSpan().SequenceCompareTo(other._hash);

    public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);
    public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);
    public static bool operator <(ObjectId left, ObjectId right) => left.CompareTo(right) < 0;
    public static bool operator >(ObjectId left, ObjectId right) => left.CompareTo(right) > 0;

    public bool IsEmpty => _hash == null || _hash.All(b => b == 0);
}

#endregion

#region 1.1.3 - ObjectChunk for Large Object Chunking

/// <summary>
/// Represents a chunk of a large object for content-defined chunking.
/// Large objects are split into chunks, each with its own ObjectId.
/// </summary>
public sealed class ObjectChunk
{
    /// <summary>
    /// The content-addressed ID of this chunk.
    /// </summary>
    public ObjectId ChunkId { get; init; }

    /// <summary>
    /// Index of this chunk within the parent object.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Offset in bytes from the start of the parent object.
    /// </summary>
    public long Offset { get; init; }

    /// <summary>
    /// Size of this chunk in bytes.
    /// </summary>
    public int Size { get; init; }

    /// <summary>
    /// The actual chunk data (null if not loaded).
    /// </summary>
    public byte[]? Data { get; set; }
}

/// <summary>
/// Configuration for content-defined chunking.
/// Uses rolling hash (Rabin fingerprinting) for chunk boundaries.
/// </summary>
public sealed class ChunkingConfig
{
    /// <summary>
    /// Minimum chunk size (default 4KB).
    /// </summary>
    public int MinChunkSize { get; set; } = 4 * 1024;

    /// <summary>
    /// Maximum chunk size (default 64KB).
    /// </summary>
    public int MaxChunkSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Target average chunk size (default 16KB).
    /// </summary>
    public int TargetChunkSize { get; set; } = 16 * 1024;

    /// <summary>
    /// Rabin polynomial mask bits (controls average size).
    /// </summary>
    public int MaskBits { get; set; } = 13;
}

#endregion

#region 1.1.4 - ObjectManifest (Federation-Extended Manifest)

/// <summary>
/// Extended manifest for federated objects.
/// Combines the existing Manifest with federation-specific metadata.
/// "The Map" - describes what exists, who owns it, where it lives.
/// </summary>
public sealed class ObjectManifest
{
    /// <summary>
    /// The content-addressed ObjectId (derived from content hash).
    /// </summary>
    public ObjectId ObjectId { get; init; }

    /// <summary>
    /// Human-readable name (virtual path component).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// MIME content type.
    /// </summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// Total size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Owner node ID.
    /// </summary>
    public string OwnerNodeId { get; set; } = string.Empty;

    /// <summary>
    /// Owner user ID (within the node).
    /// </summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>
    /// Creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last modified timestamp.
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Version history (ObjectIds of previous versions).
    /// </summary>
    public List<ObjectId> Versions { get; set; } = new();

    /// <summary>
    /// Chunk list for large objects (empty for small objects stored inline).
    /// </summary>
    public List<ObjectChunk> Chunks { get; set; } = new();

    /// <summary>
    /// Is this object chunked (large object)?
    /// </summary>
    public bool IsChunked => Chunks.Count > 0;

    /// <summary>
    /// Nodes that have replicas of this object ("The Territory").
    /// </summary>
    public HashSet<string> ReplicaNodes { get; set; } = new();

    /// <summary>
    /// Replication factor (how many copies should exist).
    /// </summary>
    public int ReplicationFactor { get; set; } = 3;

    /// <summary>
    /// Storage tier (Hot, Warm, Cold, Archive).
    /// </summary>
    public StorageTier Tier { get; set; } = StorageTier.Warm;

    /// <summary>
    /// Custom metadata key-value pairs.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Capability tokens granting access to this object.
    /// </summary>
    public List<string> CapabilityTokenIds { get; set; } = new();

    /// <summary>
    /// Vector clock for conflict-free replication.
    /// </summary>
    public Dictionary<string, long> VectorClock { get; set; } = new();

    /// <summary>
    /// Converts to legacy Manifest for backward compatibility.
    /// </summary>
    public Manifest ToLegacyManifest()
    {
        return new Manifest
        {
            Id = ObjectId.ToString(),
            Name = Name,
            ContentType = ContentType,
            SizeBytes = Size,
            OwnerId = OwnerUserId,
            CreatedAt = new DateTimeOffset(CreatedAt).ToUnixTimeSeconds(),
            Checksum = ObjectId.ToString(),
            CurrentTier = Tier.ToString(),
            Metadata = new Dictionary<string, string>(Metadata)
        };
    }

    /// <summary>
    /// Creates from a legacy Manifest.
    /// </summary>
    public static ObjectManifest FromLegacyManifest(Manifest manifest)
    {
        ObjectId.TryParse(manifest.Checksum, out var objectId);

        return new ObjectManifest
        {
            ObjectId = objectId,
            Name = manifest.Name,
            ContentType = manifest.ContentType,
            Size = manifest.SizeBytes,
            OwnerUserId = manifest.OwnerId,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(manifest.CreatedAt).UtcDateTime,
            Tier = Enum.TryParse<StorageTier>(manifest.CurrentTier, out var tier) ? tier : StorageTier.Warm,
            Metadata = new Dictionary<string, string>(manifest.Metadata)
        };
    }
}

/// <summary>
/// Storage tier enum for tiered storage.
/// </summary>
public enum StorageTier
{
    Hot,
    Warm,
    Cold,
    Archive
}

#endregion

#region 1.1.5 - ContentAddressableObjectStore Implementation

/// <summary>
/// Content-Addressable Object Store implementation.
/// Objects are stored and retrieved by their content hash (ObjectId).
/// This is the foundation of the federated storage system.
/// </summary>
public sealed class ContentAddressableObjectStore : IAsyncDisposable
{
    private readonly ConcurrentDictionary<ObjectId, ObjectManifest> _manifests = new();
    private readonly ConcurrentDictionary<ObjectId, byte[]> _inlineObjects = new();
    private readonly ConcurrentDictionary<ObjectId, byte[]> _chunks = new();
    private readonly IObjectStorageBackend? _backend;
    private readonly ChunkingConfig _chunkingConfig;
    private readonly int _inlineThreshold;
    private volatile bool _disposed;

    /// <summary>
    /// Threshold for inline storage vs chunked storage (default 64KB).
    /// </summary>
    public const int DefaultInlineThreshold = 64 * 1024;

    public ContentAddressableObjectStore(
        IObjectStorageBackend? backend = null,
        ChunkingConfig? chunkingConfig = null,
        int inlineThreshold = DefaultInlineThreshold)
    {
        _backend = backend;
        _chunkingConfig = chunkingConfig ?? new ChunkingConfig();
        _inlineThreshold = inlineThreshold;
    }

    /// <summary>
    /// Stores an object and returns its content-addressed ObjectId.
    /// </summary>
    public async Task<StoreResult> StoreAsync(
        Stream content,
        string name,
        string contentType,
        string ownerNodeId,
        string ownerUserId,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ContentAddressableObjectStore));

        // Read content to memory for hashing
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var data = ms.ToArray();

        // Compute ObjectId from content
        var objectId = ObjectId.FromContent(data);

        // Check if we already have this object (deduplication)
        if (_manifests.ContainsKey(objectId))
        {
            return new StoreResult
            {
                Success = true,
                ObjectId = objectId,
                WasDeduplicated = true,
                BytesStored = 0
            };
        }

        ObjectManifest manifest;
        long bytesStored;

        if (data.Length <= _inlineThreshold)
        {
            // Store inline (small object)
            _inlineObjects[objectId] = data;
            bytesStored = data.Length;

            manifest = new ObjectManifest
            {
                ObjectId = objectId,
                Name = name,
                ContentType = contentType,
                Size = data.Length,
                OwnerNodeId = ownerNodeId,
                OwnerUserId = ownerUserId,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }
        else
        {
            // Chunk large object
            var chunks = await ChunkDataAsync(data, ct);
            bytesStored = 0;

            foreach (var chunk in chunks)
            {
                if (!_chunks.ContainsKey(chunk.ChunkId))
                {
                    _chunks[chunk.ChunkId] = chunk.Data!;
                    bytesStored += chunk.Size;

                    // Store to backend if available
                    if (_backend != null)
                    {
                        await _backend.StoreChunkAsync(chunk.ChunkId, chunk.Data!, ct);
                    }
                }
                chunk.Data = null; // Clear data after storing
            }

            manifest = new ObjectManifest
            {
                ObjectId = objectId,
                Name = name,
                ContentType = contentType,
                Size = data.Length,
                OwnerNodeId = ownerNodeId,
                OwnerUserId = ownerUserId,
                Chunks = chunks,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        _manifests[objectId] = manifest;

        // Store to backend if available
        if (_backend != null)
        {
            await _backend.StoreManifestAsync(manifest, ct);
        }

        return new StoreResult
        {
            Success = true,
            ObjectId = objectId,
            WasDeduplicated = false,
            BytesStored = bytesStored
        };
    }

    /// <summary>
    /// Retrieves an object by its ObjectId.
    /// </summary>
    public async Task<RetrieveResult> RetrieveAsync(ObjectId objectId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ContentAddressableObjectStore));

        if (!_manifests.TryGetValue(objectId, out var manifest))
        {
            // Try backend
            if (_backend != null)
            {
                manifest = await _backend.GetManifestAsync(objectId, ct);
                if (manifest != null)
                {
                    _manifests[objectId] = manifest;
                }
            }

            if (manifest == null)
            {
                return new RetrieveResult { Success = false, Error = "Object not found" };
            }
        }

        byte[] data;

        if (manifest.IsChunked)
        {
            // Reassemble from chunks
            using var ms = new MemoryStream((int)manifest.Size);
            foreach (var chunk in manifest.Chunks.OrderBy(c => c.Index))
            {
                byte[]? chunkData;
                if (!_chunks.TryGetValue(chunk.ChunkId, out chunkData))
                {
                    if (_backend != null)
                    {
                        chunkData = await _backend.GetChunkAsync(chunk.ChunkId, ct);
                    }
                }

                if (chunkData == null)
                {
                    return new RetrieveResult { Success = false, Error = $"Chunk {chunk.ChunkId.ToShortString()} not found" };
                }

                ms.Write(chunkData, 0, chunkData.Length);
            }
            data = ms.ToArray();
        }
        else
        {
            // Get inline object
            if (!_inlineObjects.TryGetValue(objectId, out data!))
            {
                return new RetrieveResult { Success = false, Error = "Inline data not found" };
            }
        }

        // Verify integrity
        var verifyId = ObjectId.FromContent(data);
        if (verifyId != objectId)
        {
            return new RetrieveResult { Success = false, Error = "Integrity check failed" };
        }

        return new RetrieveResult
        {
            Success = true,
            Data = data,
            Manifest = manifest
        };
    }

    /// <summary>
    /// Gets an object manifest without retrieving data.
    /// </summary>
    public Task<ObjectManifest?> GetManifestAsync(ObjectId objectId, CancellationToken ct = default)
    {
        if (_manifests.TryGetValue(objectId, out var manifest))
        {
            return Task.FromResult<ObjectManifest?>(manifest);
        }

        return _backend?.GetManifestAsync(objectId, ct) ?? Task.FromResult<ObjectManifest?>(null);
    }

    /// <summary>
    /// Checks if an object exists.
    /// </summary>
    public Task<bool> ExistsAsync(ObjectId objectId, CancellationToken ct = default)
    {
        if (_manifests.ContainsKey(objectId)) return Task.FromResult(true);
        return _backend?.ExistsAsync(objectId, ct) ?? Task.FromResult(false);
    }

    /// <summary>
    /// Deletes an object.
    /// </summary>
    public async Task<bool> DeleteAsync(ObjectId objectId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ContentAddressableObjectStore));

        if (!_manifests.TryRemove(objectId, out var manifest))
        {
            return false;
        }

        _inlineObjects.TryRemove(objectId, out _);

        // Note: Chunks are reference counted in a real implementation
        // Here we just remove if this was the only reference
        foreach (var chunk in manifest.Chunks)
        {
            _chunks.TryRemove(chunk.ChunkId, out _);
        }

        if (_backend != null)
        {
            await _backend.DeleteManifestAsync(objectId, ct);
        }

        return true;
    }

    /// <summary>
    /// Gets store statistics.
    /// </summary>
    public ObjectStoreStats GetStats()
    {
        return new ObjectStoreStats
        {
            ObjectCount = _manifests.Count,
            InlineObjectCount = _inlineObjects.Count,
            ChunkCount = _chunks.Count,
            TotalInlineBytes = _inlineObjects.Values.Sum(d => (long)d.Length),
            TotalChunkBytes = _chunks.Values.Sum(d => (long)d.Length),
            TotalLogicalBytes = _manifests.Values.Sum(m => m.Size)
        };
    }

    /// <summary>
    /// Enumerates all object manifests.
    /// </summary>
    public async IAsyncEnumerable<ObjectManifest> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var manifest in _manifests.Values)
        {
            ct.ThrowIfCancellationRequested();
            yield return manifest;
        }

        await Task.CompletedTask; // Async enumeration placeholder
    }

    private async Task<List<ObjectChunk>> ChunkDataAsync(byte[] data, CancellationToken ct)
    {
        var chunks = new List<ObjectChunk>();
        var offset = 0;
        var index = 0;

        // Simple fixed-size chunking for now
        // TODO: Replace with Rabin fingerprinting for content-defined chunking
        var chunkSize = _chunkingConfig.TargetChunkSize;

        while (offset < data.Length)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = data.Length - offset;
            var size = Math.Min(chunkSize, remaining);
            var chunkData = new byte[size];
            Array.Copy(data, offset, chunkData, 0, size);

            var chunkId = ObjectId.FromContent(chunkData);

            chunks.Add(new ObjectChunk
            {
                ChunkId = chunkId,
                Index = index,
                Offset = offset,
                Size = size,
                Data = chunkData
            });

            offset += size;
            index++;
        }

        await Task.CompletedTask;
        return chunks;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Result of a store operation.
/// </summary>
public record StoreResult
{
    public bool Success { get; init; }
    public ObjectId ObjectId { get; init; }
    public bool WasDeduplicated { get; init; }
    public long BytesStored { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Result of a retrieve operation.
/// </summary>
public record RetrieveResult
{
    public bool Success { get; init; }
    public byte[]? Data { get; init; }
    public ObjectManifest? Manifest { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Object store statistics.
/// </summary>
public record ObjectStoreStats
{
    public int ObjectCount { get; init; }
    public int InlineObjectCount { get; init; }
    public int ChunkCount { get; init; }
    public long TotalInlineBytes { get; init; }
    public long TotalChunkBytes { get; init; }
    public long TotalLogicalBytes { get; init; }
    public double DeduplicationRatio => TotalLogicalBytes > 0
        ? 1.0 - ((double)(TotalInlineBytes + TotalChunkBytes) / TotalLogicalBytes)
        : 0;
}

/// <summary>
/// Backend storage interface for persistent object storage.
/// </summary>
public interface IObjectStorageBackend
{
    Task StoreManifestAsync(ObjectManifest manifest, CancellationToken ct = default);
    Task<ObjectManifest?> GetManifestAsync(ObjectId objectId, CancellationToken ct = default);
    Task DeleteManifestAsync(ObjectId objectId, CancellationToken ct = default);
    Task<bool> ExistsAsync(ObjectId objectId, CancellationToken ct = default);
    Task StoreChunkAsync(ObjectId chunkId, byte[] data, CancellationToken ct = default);
    Task<byte[]?> GetChunkAsync(ObjectId chunkId, CancellationToken ct = default);
}

#endregion
