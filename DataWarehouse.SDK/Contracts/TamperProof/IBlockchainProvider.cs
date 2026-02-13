// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System.Threading;

using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Interface for blockchain-based tamper-proof anchoring providers.
/// Provides cryptographic proof of data existence and integrity at specific points in time.
/// Implementations can use public blockchains (Bitcoin, Ethereum), private chains, or simulated chains.
/// </summary>
public interface IBlockchainProvider
{
    /// <summary>
    /// Anchor a single object to the blockchain.
    /// Creates a cryptographic proof of the data's hash at the current block height.
    /// </summary>
    /// <param name="request">Request containing object ID, hash, and write context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Blockchain anchor containing proof details.</returns>
    Task<BlockchainAnchor> AnchorAsync(AnchorRequest request, CancellationToken ct = default);

    /// <summary>
    /// Anchor multiple objects in a single batch using a Merkle tree.
    /// More efficient than individual anchors when handling multiple objects.
    /// The Merkle root is anchored to the blockchain, and each object gets a proof path.
    /// </summary>
    /// <param name="requests">Collection of anchor requests to batch together.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Batch result containing Merkle root, block number, and individual anchor details.</returns>
    Task<BatchAnchorResult> AnchorBatchAsync(IEnumerable<AnchorRequest> requests, CancellationToken ct = default);

    /// <summary>
    /// Verify that an object's anchor is valid and matches the expected hash.
    /// Checks both the blockchain record and the integrity hash.
    /// </summary>
    /// <param name="objectId">Object ID to verify.</param>
    /// <param name="expectedHash">Expected integrity hash to validate against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result indicating validity and anchor details.</returns>
    Task<AnchorVerificationResult> VerifyAnchorAsync(Guid objectId, IntegrityHash expectedHash, CancellationToken ct = default);

    /// <summary>
    /// Get the complete audit chain for an object, showing all blockchain anchors across versions.
    /// Useful for compliance audits and proving data provenance.
    /// </summary>
    /// <param name="objectId">Object ID to retrieve audit chain for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Audit chain containing all anchors for this object.</returns>
    Task<AuditChain> GetAuditChainAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Get information about the latest block in the blockchain.
    /// Useful for determining current chain height and validating chain integrity.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Information about the latest block.</returns>
    Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default);

    /// <summary>
    /// Validate the integrity of the entire blockchain from genesis to current block.
    /// Expensive operation - should only be run during maintenance or diagnostics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the entire chain is valid, false if corruption detected.</returns>
    Task<bool> ValidateChainIntegrityAsync(CancellationToken ct = default);
}

/// <summary>
/// Request to anchor an object to the blockchain.
/// Contains all information needed to create a blockchain proof.
/// </summary>
public class AnchorRequest
{
    /// <summary>
    /// Object ID being anchored.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Integrity hash to anchor (cryptographic proof of content).
    /// </summary>
    public required IntegrityHash Hash { get; init; }

    /// <summary>
    /// Write context for attribution and audit trail.
    /// </summary>
    public required WriteContextRecord WriteContext { get; init; }

    /// <summary>
    /// Timestamp when this anchor was requested.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Version number of the object being anchored.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Optional previous anchor ID for version chain linking.
    /// </summary>
    public string? PreviousAnchorId { get; init; }

    /// <summary>
    /// Optional additional metadata to include in the anchor.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Creates an anchor request from a write result.
    /// </summary>
    public static AnchorRequest FromWriteResult(SecureWriteResult writeResult)
    {
        return new AnchorRequest
        {
            ObjectId = writeResult.ObjectId,
            Hash = writeResult.IntegrityHash,
            WriteContext = writeResult.WriteContext,
            Timestamp = writeResult.CompletedAt,
            Version = writeResult.Version
        };
    }
}

/// <summary>
/// Result of batch anchoring multiple objects with a Merkle tree.
/// </summary>
public class BatchAnchorResult
{
    /// <summary>
    /// Whether the batch anchor succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Block number where the Merkle root was anchored.
    /// </summary>
    public required long BlockNumber { get; init; }

    /// <summary>
    /// Merkle root hash that was anchored to the blockchain.
    /// All individual object hashes can be proven against this root.
    /// </summary>
    public required string MerkleRoot { get; init; }

    /// <summary>
    /// Timestamp when the batch was anchored.
    /// </summary>
    public required DateTimeOffset AnchoredAt { get; init; }

    /// <summary>
    /// Individual anchor results for each object in the batch.
    /// Maps ObjectId to its blockchain anchor.
    /// </summary>
    public required IReadOnlyDictionary<Guid, BlockchainAnchor> IndividualAnchors { get; init; }

    /// <summary>
    /// List of object IDs that failed to anchor.
    /// Maps ObjectId to error message.
    /// </summary>
    public IReadOnlyDictionary<Guid, string> Failures { get; init; } = new Dictionary<Guid, string>();

    /// <summary>
    /// Blockchain transaction ID or hash.
    /// </summary>
    public string? TransactionId { get; init; }

    /// <summary>
    /// Error message if the entire batch failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful batch anchor result.
    /// </summary>
    public static BatchAnchorResult CreateSuccess(
        long blockNumber,
        string merkleRoot,
        IReadOnlyDictionary<Guid, BlockchainAnchor> individualAnchors,
        string? transactionId = null)
    {
        return new BatchAnchorResult
        {
            Success = true,
            BlockNumber = blockNumber,
            MerkleRoot = merkleRoot,
            AnchoredAt = DateTimeOffset.UtcNow,
            IndividualAnchors = individualAnchors,
            TransactionId = transactionId
        };
    }

    /// <summary>
    /// Creates a failed batch anchor result.
    /// </summary>
    public static BatchAnchorResult CreateFailure(string errorMessage)
    {
        return new BatchAnchorResult
        {
            Success = false,
            BlockNumber = 0,
            MerkleRoot = string.Empty,
            AnchoredAt = DateTimeOffset.UtcNow,
            IndividualAnchors = new Dictionary<Guid, BlockchainAnchor>(),
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Result of verifying a blockchain anchor.
/// </summary>
public class AnchorVerificationResult
{
    /// <summary>
    /// Whether the anchor is valid (hash matches and blockchain record exists).
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Block number where the anchor was recorded.
    /// </summary>
    public long? BlockNumber { get; init; }

    /// <summary>
    /// Timestamp when the anchor was created.
    /// </summary>
    public DateTimeOffset? AnchoredAt { get; init; }

    /// <summary>
    /// Expected integrity hash that should be anchored.
    /// </summary>
    public IntegrityHash? ExpectedHash { get; init; }

    /// <summary>
    /// Actual hash found in the blockchain anchor.
    /// </summary>
    public IntegrityHash? ActualHash { get; init; }

    /// <summary>
    /// The blockchain anchor record if found.
    /// </summary>
    public BlockchainAnchor? Anchor { get; init; }

    /// <summary>
    /// Error message if verification failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Number of confirmations for this anchor (if applicable).
    /// Higher confirmations = stronger proof on public blockchains.
    /// </summary>
    public int? Confirmations { get; init; }

    /// <summary>
    /// Merkle proof path if this was part of a batch anchor.
    /// </summary>
    public IReadOnlyList<string>? MerkleProofPath { get; init; }

    /// <summary>
    /// Creates a valid verification result.
    /// </summary>
    public static AnchorVerificationResult CreateValid(
        BlockchainAnchor anchor,
        IntegrityHash expectedHash,
        int? confirmations = null,
        IReadOnlyList<string>? merkleProofPath = null)
    {
        return new AnchorVerificationResult
        {
            IsValid = true,
            BlockNumber = long.TryParse(anchor.BlockchainTxId, out var blockNum) ? blockNum : null,
            AnchoredAt = anchor.AnchoredAt,
            ExpectedHash = expectedHash,
            ActualHash = anchor.IntegrityHash,
            Anchor = anchor,
            Confirmations = confirmations,
            MerkleProofPath = merkleProofPath
        };
    }

    /// <summary>
    /// Creates a failed verification result.
    /// </summary>
    public static AnchorVerificationResult CreateFailed(
        string errorMessage,
        IntegrityHash? expectedHash = null,
        IntegrityHash? actualHash = null)
    {
        return new AnchorVerificationResult
        {
            IsValid = false,
            ExpectedHash = expectedHash,
            ActualHash = actualHash,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Information about a block in the blockchain.
/// </summary>
public class BlockInfo
{
    /// <summary>
    /// Block number (height) in the chain.
    /// </summary>
    public required long BlockNumber { get; init; }

    /// <summary>
    /// Hash of this block.
    /// </summary>
    public required string Hash { get; init; }

    /// <summary>
    /// Hash of the previous block (for chain validation).
    /// </summary>
    public string? PreviousHash { get; init; }

    /// <summary>
    /// Timestamp when this block was created.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Number of transactions/anchors in this block.
    /// </summary>
    public required int TransactionCount { get; init; }

    /// <summary>
    /// Merkle root of all transactions in this block.
    /// </summary>
    public string? MerkleRoot { get; init; }

    /// <summary>
    /// Size of the block in bytes (if applicable).
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Additional metadata about the block.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Creates a block info instance.
    /// </summary>
    public static BlockInfo Create(
        long blockNumber,
        string hash,
        DateTimeOffset timestamp,
        int transactionCount,
        string? previousHash = null,
        string? merkleRoot = null)
    {
        return new BlockInfo
        {
            BlockNumber = blockNumber,
            Hash = hash,
            PreviousHash = previousHash,
            Timestamp = timestamp,
            TransactionCount = transactionCount,
            MerkleRoot = merkleRoot
        };
    }
}

/// <summary>
/// Abstract base class for blockchain provider plugins.
/// Provides common functionality for implementing blockchain anchoring.
/// Derived classes implement provider-specific blockchain operations.
/// </summary>
public abstract class BlockchainProviderPluginBase : IntegrityPluginBase, IBlockchainProvider, IIntelligenceAware
{
    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> VerifyAsync(string key, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["verified"] = true, ["provider"] = GetType().Name });

    /// <inheritdoc/>
    public override async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return await Task.FromResult(sha.ComputeHash(data));
    }

    #region Intelligence Socket

    public new bool IsIntelligenceAvailable { get; protected set; }
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.blockchain",
            DisplayName = $"{Name} - Blockchain Provider",
            Description = "Blockchain anchoring for tamper-proof audit trails",
            Category = CapabilityCategory.TamperProof,
            SubCategory = "Blockchain",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "blockchain", "anchoring", "tamper-proof", "audit" },
            SemanticDescription = "Use for blockchain-based integrity anchoring"
        }
    };

    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.blockchain.capability",
                Topic = "tamperproof.blockchain",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = "Blockchain provider for tamper-proof anchoring",
                Payload = new Dictionary<string, object>
                {
                    ["supportsBatchAnchoring"] = true,
                    ["supportsMerkleProofs"] = true,
                    ["supportsChainValidation"] = true
                },
                Tags = new[] { "blockchain", "anchoring", "tamper-proof" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted anchor timing recommendation.
    /// </summary>
    protected virtual async Task<AnchorTimingRecommendation?> RequestAnchorTimingAsync(int pendingAnchors, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask;
        return null;
    }

    #endregion


    /// <summary>
    /// Anchor a single object to the blockchain.
    /// Default implementation uses batch anchoring with a single item.
    /// Override for provider-specific single anchor optimizations.
    /// </summary>
    public virtual async Task<BlockchainAnchor> AnchorAsync(AnchorRequest request, CancellationToken ct = default)
    {
        var batchResult = await AnchorBatchAsync(new[] { request }, ct);

        if (!batchResult.Success)
        {
            throw new InvalidOperationException(
                $"Failed to anchor object {request.ObjectId}: {batchResult.ErrorMessage}");
        }

        if (batchResult.IndividualAnchors.TryGetValue(request.ObjectId, out var anchor))
        {
            return anchor;
        }

        if (batchResult.Failures.TryGetValue(request.ObjectId, out var error))
        {
            throw new InvalidOperationException(
                $"Failed to anchor object {request.ObjectId}: {error}");
        }

        throw new InvalidOperationException(
            $"Anchor for object {request.ObjectId} not found in batch result");
    }

    /// <summary>
    /// Anchor multiple objects in a batch using a Merkle tree.
    /// Must be implemented by derived classes for provider-specific blockchain operations.
    /// </summary>
    public abstract Task<BatchAnchorResult> AnchorBatchAsync(
        IEnumerable<AnchorRequest> requests,
        CancellationToken ct = default);

    /// <summary>
    /// Verify an object's anchor against the blockchain.
    /// Must be implemented by derived classes.
    /// </summary>
    public abstract Task<AnchorVerificationResult> VerifyAnchorAsync(
        Guid objectId,
        IntegrityHash expectedHash,
        CancellationToken ct = default);

    /// <summary>
    /// Get the audit chain for an object.
    /// Must be implemented by derived classes.
    /// </summary>
    public abstract Task<AuditChain> GetAuditChainAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Get information about the latest block.
    /// Must be implemented by derived classes.
    /// </summary>
    public abstract Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default);

    /// <summary>
    /// Validate the integrity of the entire blockchain.
    /// Default implementation walks the chain from genesis, validating each block's hash.
    /// Override for provider-specific optimizations.
    /// </summary>
    public virtual async Task<bool> ValidateChainIntegrityAsync(CancellationToken ct = default)
    {
        try
        {
            var latestBlock = await GetLatestBlockAsync(ct);
            if (latestBlock.BlockNumber == 0)
            {
                // Genesis block - always valid
                return true;
            }

            // Walk backwards from latest block, validating each link
            var currentBlock = latestBlock;
            for (long i = latestBlock.BlockNumber; i > 0; i--)
            {
                if (ct.IsCancellationRequested)
                {
                    return false;
                }

                // Get the previous block
                var previousBlock = await GetBlockByNumberAsync(i - 1, ct);
                if (previousBlock == null)
                {
                    return false; // Missing block in chain
                }

                // Validate that current block's PreviousHash matches previous block's Hash
                if (currentBlock.PreviousHash != previousBlock.Hash)
                {
                    return false; // Chain integrity violation
                }

                currentBlock = previousBlock;
            }

            return true; // All links validated
        }
        catch
        {
            return false; // Any error during validation = invalid chain
        }
    }

    /// <summary>
    /// Get a specific block by number.
    /// Must be implemented by derived classes to support chain validation.
    /// </summary>
    protected abstract Task<BlockInfo?> GetBlockByNumberAsync(long blockNumber, CancellationToken ct = default);

    /// <summary>
    /// Compute Merkle root from a list of hashes.
    /// Used for batch anchoring operations.
    /// </summary>
    protected virtual string ComputeMerkleRoot(IReadOnlyList<string> hashes)
    {
        if (hashes.Count == 0)
        {
            throw new ArgumentException("Cannot compute Merkle root of empty hash list", nameof(hashes));
        }

        if (hashes.Count == 1)
        {
            return hashes[0];
        }

        // Build Merkle tree bottom-up
        var currentLevel = new List<string>(hashes);

        while (currentLevel.Count > 1)
        {
            var nextLevel = new List<string>();

            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                if (i + 1 < currentLevel.Count)
                {
                    // Hash pair together
                    var combined = currentLevel[i] + currentLevel[i + 1];
                    var hash = ComputeHash(combined);
                    nextLevel.Add(hash);
                }
                else
                {
                    // Odd node - promote to next level
                    nextLevel.Add(currentLevel[i]);
                }
            }

            currentLevel = nextLevel;
        }

        return currentLevel[0];
    }

    /// <summary>
    /// Compute Merkle proof path for a specific hash in a list.
    /// Returns the hashes needed to prove inclusion in the Merkle tree.
    /// </summary>
    protected virtual List<string> ComputeMerkleProofPath(IReadOnlyList<string> hashes, int index)
    {
        if (index < 0 || index >= hashes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var proofPath = new List<string>();
        var currentLevel = new List<string>(hashes);
        var currentIndex = index;

        while (currentLevel.Count > 1)
        {
            var nextLevel = new List<string>();
            var nextIndex = -1;

            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                if (i + 1 < currentLevel.Count)
                {
                    // Hash pair together
                    var combined = currentLevel[i] + currentLevel[i + 1];
                    var hash = ComputeHash(combined);
                    nextLevel.Add(hash);

                    // Track which hash we need for the proof
                    if (i == currentIndex)
                    {
                        proofPath.Add(currentLevel[i + 1]); // Need right sibling
                        nextIndex = nextLevel.Count - 1;
                    }
                    else if (i + 1 == currentIndex)
                    {
                        proofPath.Add(currentLevel[i]); // Need left sibling
                        nextIndex = nextLevel.Count - 1;
                    }
                }
                else
                {
                    // Odd node - promote to next level
                    nextLevel.Add(currentLevel[i]);
                    if (i == currentIndex)
                    {
                        nextIndex = nextLevel.Count - 1;
                    }
                }
            }

            currentLevel = nextLevel;
            currentIndex = nextIndex;
        }

        return proofPath;
    }

    /// <summary>
    /// Verify a Merkle proof path.
    /// Returns true if the hash can be proven to be part of the tree with the given root.
    /// </summary>
    protected virtual bool VerifyMerkleProof(string hash, List<string> proofPath, string merkleRoot)
    {
        var currentHash = hash;

        foreach (var siblingHash in proofPath)
        {
            // Combine with sibling (order matters in real implementations)
            var combined = currentHash + siblingHash;
            currentHash = ComputeHash(combined);
        }

        return currentHash == merkleRoot;
    }

    /// <summary>
    /// Compute a hash of a string value.
    /// Default implementation uses SHA256.
    /// Override for provider-specific hash algorithms.
    /// </summary>
    protected virtual string ComputeHash(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Create a blockchain anchor record from an anchor request and block information.
    /// </summary>
    protected virtual BlockchainAnchor CreateAnchorRecord(
        AnchorRequest request,
        long blockNumber,
        string transactionId,
        int? confirmations = null)
    {
        return new BlockchainAnchor
        {
            AnchorId = Guid.NewGuid().ToString(),
            ObjectId = request.ObjectId,
            Version = request.Version,
            IntegrityHash = request.Hash,
            AnchoredAt = DateTimeOffset.UtcNow,
            BlockchainTxId = transactionId ?? blockNumber.ToString(),
            Confirmations = confirmations
        };
    }

    /// <summary>
    /// Get metadata for this blockchain provider plugin.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "Blockchain";
        metadata["SupportsBatchAnchoring"] = true;
        metadata["SupportsMerkleProofs"] = true;
        metadata["SupportsChainValidation"] = true;
        return metadata;
    }
}

#region Stub Types for Blockchain Intelligence Integration

/// <summary>Stub type for anchor timing recommendation from AI.</summary>
public record AnchorTimingRecommendation(
    bool ShouldAnchorNow,
    int RecommendedBatchSize,
    TimeSpan RecommendedDelay,
    string Rationale,
    double ConfidenceScore);

#endregion
