// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Buffers;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using System.Threading;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Interface for integrity hash computation and verification providers.
/// Supports multiple hash algorithms and efficient streaming operations.
/// </summary>
public interface IIntegrityProvider
{
    /// <summary>
    /// Gets the list of hash algorithms supported by this provider.
    /// </summary>
    IReadOnlyList<HashAlgorithmType> SupportedAlgorithms { get; }

    /// <summary>
    /// Computes an integrity hash from a stream of data.
    /// Stream is read completely and position is reset to beginning if seekable.
    /// </summary>
    /// <param name="data">Stream containing the data to hash.</param>
    /// <param name="algorithm">Hash algorithm to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Computed integrity hash.</returns>
    /// <exception cref="NotSupportedException">If the specified algorithm is not supported.</exception>
    Task<IntegrityHash> ComputeHashAsync(Stream data, HashAlgorithmType algorithm, CancellationToken ct = default);

    /// <summary>
    /// Computes an integrity hash from a byte array.
    /// </summary>
    /// <param name="data">Byte array containing the data to hash.</param>
    /// <param name="algorithm">Hash algorithm to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Computed integrity hash.</returns>
    /// <exception cref="NotSupportedException">If the specified algorithm is not supported.</exception>
    Task<IntegrityHash> ComputeHashAsync(byte[] data, HashAlgorithmType algorithm, CancellationToken ct = default);

    /// <summary>
    /// Verifies data integrity by comparing computed hash against expected hash.
    /// Stream is read completely and position is reset to beginning if seekable.
    /// </summary>
    /// <param name="data">Stream containing the data to verify.</param>
    /// <param name="expectedHash">Expected integrity hash from a trusted source.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result indicating success or failure with details.</returns>
    Task<IntegrityVerificationResult> VerifyAsync(Stream data, IntegrityHash expectedHash, CancellationToken ct = default);

    /// <summary>
    /// Computes a hash for a single RAID shard.
    /// Includes shard index and object ID in the hash context for tamper detection.
    /// </summary>
    /// <param name="shardData">Raw bytes of the shard.</param>
    /// <param name="shardIndex">Zero-based index of this shard.</param>
    /// <param name="objectId">Object ID this shard belongs to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Shard hash with metadata.</returns>
    Task<ShardHash> ComputeShardHashAsync(byte[] shardData, int shardIndex, Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Computes hashes for multiple shards in batch.
    /// More efficient than calling ComputeShardHashAsync repeatedly.
    /// </summary>
    /// <param name="shards">Collection of shard byte arrays.</param>
    /// <param name="objectId">Object ID all shards belong to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of shard hashes in the same order as input shards.</returns>
    Task<IReadOnlyList<ShardHash>> ComputeShardHashesAsync(IReadOnlyList<byte[]> shards, Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a specific hash algorithm is supported by this provider.
    /// </summary>
    /// <param name="algorithm">Algorithm to check.</param>
    /// <returns>True if supported, false otherwise.</returns>
    bool IsAlgorithmSupported(HashAlgorithmType algorithm);
}

/// <summary>
/// Abstract base class for integrity provider plugins.
/// Provides thread-safe, streaming-capable hash computation with efficient buffer management.
/// Derived classes only need to implement the core hash algorithm.
/// </summary>
public abstract class IntegrityProviderPluginBase : FeaturePluginBase, IIntegrityProvider, IIntelligenceAware
{
    #region Intelligence Socket

    public bool IsIntelligenceAvailable { get; protected set; }
    public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.integrity",
            DisplayName = $"{Name} - Integrity Provider",
            Description = $"Hash computation with {string.Join(", ", SupportedAlgorithms)}",
            Category = CapabilityCategory.TamperProof,
            SubCategory = "Integrity",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "integrity", "hash", "verification", "tamper-proof" },
            SemanticDescription = "Use for integrity hash computation and verification"
        }
    };

    protected virtual IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.integrity.capability",
                Topic = "tamperproof.integrity",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Integrity provider, Algorithms: {string.Join(", ", SupportedAlgorithms)}",
                Payload = new Dictionary<string, object>
                {
                    ["supportedAlgorithms"] = SupportedAlgorithms.Select(a => a.ToString()).ToArray(),
                    ["defaultAlgorithm"] = DefaultAlgorithm.ToString(),
                    ["supportsStreaming"] = true,
                    ["supportsShardHashing"] = true
                },
                Tags = new[] { "integrity", "hash", "tamper-proof" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted integrity verification confidence.
    /// </summary>
    protected virtual async Task<IntegrityVerificationConfidence?> RequestVerificationConfidenceAsync(IntegrityHash expectedHash, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask;
        return null;
    }

    #endregion


    /// <summary>
    /// Default buffer size for streaming operations (64KB).
    /// Can be overridden by derived classes for optimal performance.
    /// </summary>
    protected virtual int StreamBufferSize => 65536;

    /// <summary>
    /// Default hash algorithm used when none is specified.
    /// </summary>
    protected virtual HashAlgorithmType DefaultAlgorithm => HashAlgorithmType.SHA256;

    /// <summary>
    /// Gets the list of supported hash algorithms.
    /// Must be implemented by derived classes.
    /// </summary>
    public abstract IReadOnlyList<HashAlgorithmType> SupportedAlgorithms { get; }

    /// <summary>
    /// Core hash computation method that must be implemented by derived classes.
    /// This method should be thread-safe and perform the actual hashing.
    /// </summary>
    /// <param name="data">Read-only span of data to hash.</param>
    /// <param name="algorithm">Hash algorithm to use.</param>
    /// <returns>Raw hash bytes.</returns>
    protected abstract byte[] ComputeHashCore(ReadOnlySpan<byte> data, HashAlgorithmType algorithm);

    /// <summary>
    /// Checks if a hash algorithm is supported.
    /// </summary>
    public virtual bool IsAlgorithmSupported(HashAlgorithmType algorithm)
    {
        return SupportedAlgorithms.Contains(algorithm);
    }

    /// <summary>
    /// Computes hash from a stream with efficient chunked reading.
    /// Automatically handles non-seekable streams and resets seekable streams.
    /// </summary>
    public virtual async Task<IntegrityHash> ComputeHashAsync(Stream data, HashAlgorithmType algorithm, CancellationToken ct = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (!IsAlgorithmSupported(algorithm))
            throw new NotSupportedException($"Hash algorithm {algorithm} is not supported by this provider.");

        var originalPosition = data.CanSeek ? data.Position : -1;

        try
        {
            // For small streams or seekable streams, read all at once for better performance
            if (data.CanSeek && data.Length < StreamBufferSize * 2)
            {
                var buffer = new byte[data.Length];
                var totalRead = 0;

                while (totalRead < buffer.Length)
                {
                    var read = await data.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
                    if (read == 0) break;
                    totalRead += read;
                }

                var hashBytes = ComputeHashCore(buffer.AsSpan(0, totalRead), algorithm);
                var hashHex = Convert.ToHexString(hashBytes);

                return IntegrityHash.Create(algorithm, hashHex);
            }
            else
            {
                // For large or non-seekable streams, use chunked reading
                using var memoryOwner = MemoryPool<byte>.Shared.Rent(StreamBufferSize);
                var buffer = memoryOwner.Memory;
                using var ms = new MemoryStream();

                int bytesRead;
                while ((bytesRead = await data.ReadAsync(buffer, ct)) > 0)
                {
                    await ms.WriteAsync(buffer.Slice(0, bytesRead), ct);
                }

                var allBytes = ms.ToArray();
                var hashBytes = ComputeHashCore(allBytes, algorithm);
                var hashHex = Convert.ToHexString(hashBytes);

                return IntegrityHash.Create(algorithm, hashHex);
            }
        }
        finally
        {
            // Reset stream position if possible
            if (originalPosition >= 0 && data.CanSeek)
            {
                try
                {
                    data.Position = originalPosition;
                }
                catch
                {
                    // Ignore errors when resetting position
                }
            }
        }
    }

    /// <summary>
    /// Computes hash from a byte array.
    /// More efficient than stream-based version for in-memory data.
    /// </summary>
    public virtual Task<IntegrityHash> ComputeHashAsync(byte[] data, HashAlgorithmType algorithm, CancellationToken ct = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (!IsAlgorithmSupported(algorithm))
            throw new NotSupportedException($"Hash algorithm {algorithm} is not supported by this provider.");

        ct.ThrowIfCancellationRequested();

        var hashBytes = ComputeHashCore(data.AsSpan(), algorithm);
        var hashHex = Convert.ToHexString(hashBytes);

        return Task.FromResult(IntegrityHash.Create(algorithm, hashHex));
    }

    /// <summary>
    /// Verifies data integrity against an expected hash.
    /// </summary>
    public virtual async Task<IntegrityVerificationResult> VerifyAsync(Stream data, IntegrityHash expectedHash, CancellationToken ct = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (expectedHash == null)
            throw new ArgumentNullException(nameof(expectedHash));

        try
        {
            var actualHash = await ComputeHashAsync(data, expectedHash.Algorithm, ct);

            var isValid = string.Equals(
                actualHash.HashValue,
                expectedHash.HashValue,
                StringComparison.OrdinalIgnoreCase);

            if (isValid)
            {
                return IntegrityVerificationResult.CreateValid(expectedHash, actualHash);
            }
            else
            {
                return IntegrityVerificationResult.CreateFailed(
                    $"Integrity verification failed. Expected: {expectedHash.HashValue}, Actual: {actualHash.HashValue}",
                    expectedHash,
                    actualHash);
            }
        }
        catch (Exception ex)
        {
            return IntegrityVerificationResult.CreateFailed(
                $"Integrity verification error: {ex.Message}",
                expectedHash,
                null);
        }
    }

    /// <summary>
    /// Computes hash for a single shard with contextual information.
    /// Includes shard index and object ID to detect shard swapping attacks.
    /// </summary>
    public virtual async Task<ShardHash> ComputeShardHashAsync(byte[] shardData, int shardIndex, Guid objectId, CancellationToken ct = default)
    {
        if (shardData == null)
            throw new ArgumentNullException(nameof(shardData));

        if (shardIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(shardIndex), "Shard index must be non-negative.");

        ct.ThrowIfCancellationRequested();

        // Create contextual data: objectId + shardIndex + shardData
        // This prevents shard swapping attacks
        var objectIdBytes = objectId.ToByteArray();
        var indexBytes = BitConverter.GetBytes(shardIndex);

        var contextualData = new byte[objectIdBytes.Length + indexBytes.Length + shardData.Length];
        Buffer.BlockCopy(objectIdBytes, 0, contextualData, 0, objectIdBytes.Length);
        Buffer.BlockCopy(indexBytes, 0, contextualData, objectIdBytes.Length, indexBytes.Length);
        Buffer.BlockCopy(shardData, 0, contextualData, objectIdBytes.Length + indexBytes.Length, shardData.Length);

        var hash = await ComputeHashAsync(contextualData, DefaultAlgorithm, ct);

        return ShardHash.Create(
            shardIndex: shardIndex,
            hash: hash,
            sizeBytes: shardData.Length,
            isParity: false); // Caller should set this based on RAID configuration
    }

    /// <summary>
    /// Computes hashes for multiple shards efficiently in parallel.
    /// Uses configured degree of parallelism for optimal throughput.
    /// </summary>
    public virtual async Task<IReadOnlyList<ShardHash>> ComputeShardHashesAsync(IReadOnlyList<byte[]> shards, Guid objectId, CancellationToken ct = default)
    {
        if (shards == null)
            throw new ArgumentNullException(nameof(shards));

        if (shards.Count == 0)
            return Array.Empty<ShardHash>();

        // Compute shard hashes in parallel for better performance
        var degreeOfParallelism = Math.Min(Environment.ProcessorCount, shards.Count);
        var tasks = new Task<ShardHash>[shards.Count];

        for (int i = 0; i < shards.Count; i++)
        {
            var shardIndex = i;
            var shardData = shards[i];
            tasks[i] = Task.Run(async () => await ComputeShardHashAsync(shardData, shardIndex, objectId, ct), ct);
        }

        var results = await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Plugin category for integrity providers.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "IntegrityProvider";
        metadata["SupportedAlgorithms"] = SupportedAlgorithms.Select(a => a.ToString()).ToArray();
        metadata["DefaultAlgorithm"] = DefaultAlgorithm.ToString();
        metadata["SupportsStreaming"] = true;
        metadata["ThreadSafe"] = true;
        metadata["SupportsBatchOperations"] = true;
        metadata["SupportsShardHashing"] = true;
        return metadata;
    }
}

#region Stub Types for Integrity Intelligence Integration

/// <summary>Stub type for integrity verification confidence from AI.</summary>
public record IntegrityVerificationConfidence(
    bool IsLikelyValid,
    double ConfidenceScore,
    string[] RiskFactors,
    string Recommendation);

#endregion
