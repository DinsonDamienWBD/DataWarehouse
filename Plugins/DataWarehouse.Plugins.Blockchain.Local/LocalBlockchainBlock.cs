// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts.TamperProof;

namespace DataWarehouse.Plugins.Blockchain.Local;

/// <summary>
/// Represents a block in the local file-based blockchain.
/// Contains block header, transactions (anchors), and cryptographic proof.
/// </summary>
public record LocalBlockchainBlock
{
    /// <summary>
    /// Block number (height) in the chain. Genesis block is 0.
    /// </summary>
    [JsonPropertyName("blockNumber")]
    public required long BlockNumber { get; init; }

    /// <summary>
    /// Hash of this block (computed from block contents).
    /// </summary>
    [JsonPropertyName("hash")]
    public required string Hash { get; init; }

    /// <summary>
    /// Hash of the previous block in the chain.
    /// Genesis block (0) has null previous hash.
    /// </summary>
    [JsonPropertyName("previousHash")]
    public string? PreviousHash { get; init; }

    /// <summary>
    /// Timestamp when this block was created.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Merkle root of all transactions in this block.
    /// Provides efficient verification of transaction inclusion.
    /// </summary>
    [JsonPropertyName("merkleRoot")]
    public required string MerkleRoot { get; init; }

    /// <summary>
    /// List of transactions (anchors) in this block.
    /// Each transaction represents an anchored object with its integrity hash.
    /// </summary>
    [JsonPropertyName("transactions")]
    public required List<BlockchainTransaction> Transactions { get; init; }

    /// <summary>
    /// Version of the block format for future compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Optional nonce for proof-of-work (currently unused, reserved for future).
    /// </summary>
    [JsonPropertyName("nonce")]
    public long? Nonce { get; init; }

    /// <summary>
    /// Optional additional metadata for the block.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Computes the hash of this block based on its contents.
    /// Uses SHA256 of concatenated block header fields.
    /// </summary>
    /// <returns>Computed hash as hex string.</returns>
    public string ComputeHash()
    {
        var data = $"{BlockNumber}|{PreviousHash ?? ""}|{Timestamp:O}|{MerkleRoot}|{Version}|{Nonce ?? 0}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Validates this block's integrity.
    /// Checks hash correctness and previous block linkage.
    /// </summary>
    /// <returns>True if block is valid, false otherwise.</returns>
    public bool IsValid()
    {
        // Verify block hash matches computed hash
        var computedHash = ComputeHash();
        if (Hash != computedHash)
        {
            return false;
        }

        // Genesis block has no previous hash
        if (BlockNumber == 0)
        {
            return PreviousHash == null;
        }

        // Non-genesis blocks must have previous hash
        if (string.IsNullOrEmpty(PreviousHash))
        {
            return false;
        }

        // Verify Merkle root matches transactions
        if (Transactions.Count > 0)
        {
            var transactionHashes = Transactions.Select(t => t.Hash).ToList();
            var computedMerkleRoot = ComputeMerkleRoot(transactionHashes);
            if (MerkleRoot != computedMerkleRoot)
            {
                return false;
            }
        }
        else
        {
            // Empty block should have empty Merkle root
            if (MerkleRoot != string.Empty)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes Merkle root from transaction hashes.
    /// </summary>
    private static string ComputeMerkleRoot(IReadOnlyList<string> hashes)
    {
        if (hashes.Count == 0)
        {
            return string.Empty;
        }

        if (hashes.Count == 1)
        {
            return hashes[0];
        }

        var currentLevel = new List<string>(hashes);

        while (currentLevel.Count > 1)
        {
            var nextLevel = new List<string>();

            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                if (i + 1 < currentLevel.Count)
                {
                    var combined = currentLevel[i] + currentLevel[i + 1];
                    var hash = ComputeSimpleHash(combined);
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
    /// Simple hash computation helper.
    /// </summary>
    private static string ComputeSimpleHash(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Converts this block to BlockInfo for SDK compatibility.
    /// </summary>
    public BlockInfo ToBlockInfo()
    {
        return BlockInfo.Create(
            blockNumber: BlockNumber,
            hash: Hash,
            timestamp: Timestamp,
            transactionCount: Transactions.Count,
            previousHash: PreviousHash,
            merkleRoot: MerkleRoot
        );
    }
}

/// <summary>
/// Represents a transaction (anchor) in the blockchain.
/// Each transaction records the integrity hash of an object at a specific version.
/// </summary>
public record BlockchainTransaction
{
    /// <summary>
    /// Unique transaction ID (anchor ID).
    /// </summary>
    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    /// <summary>
    /// Object ID being anchored.
    /// </summary>
    [JsonPropertyName("objectId")]
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Version of the object being anchored.
    /// </summary>
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    /// <summary>
    /// Integrity hash being anchored.
    /// </summary>
    [JsonPropertyName("integrityHash")]
    public required string IntegrityHash { get; init; }

    /// <summary>
    /// Hash algorithm used for integrity hash.
    /// </summary>
    [JsonPropertyName("hashAlgorithm")]
    public required string HashAlgorithm { get; init; }

    /// <summary>
    /// Timestamp when this transaction was created.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Write context for attribution.
    /// </summary>
    [JsonPropertyName("writeContext")]
    public required WriteContextRecord WriteContext { get; init; }

    /// <summary>
    /// Hash of this transaction (computed).
    /// </summary>
    [JsonPropertyName("hash")]
    public required string Hash { get; init; }

    /// <summary>
    /// Optional previous transaction ID for version chain linking.
    /// </summary>
    [JsonPropertyName("previousTransactionId")]
    public string? PreviousTransactionId { get; init; }

    /// <summary>
    /// Optional additional metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Computes the hash of this transaction.
    /// </summary>
    public string ComputeHash()
    {
        var data = $"{TransactionId}|{ObjectId}|{Version}|{IntegrityHash}|{HashAlgorithm}|{Timestamp:O}|{WriteContext.ComputeHash()}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Creates a transaction from an anchor request.
    /// </summary>
    public static BlockchainTransaction FromAnchorRequest(AnchorRequest request, string transactionId)
    {
        var transaction = new BlockchainTransaction
        {
            TransactionId = transactionId,
            ObjectId = request.ObjectId,
            Version = request.Version,
            IntegrityHash = request.Hash.HashValue,
            HashAlgorithm = request.Hash.Algorithm.ToString(),
            Timestamp = request.Timestamp,
            WriteContext = request.WriteContext,
            Hash = string.Empty, // Will be computed
            PreviousTransactionId = request.PreviousAnchorId,
            Metadata = request.Metadata
        };

        // Compute and set hash
        return transaction with { Hash = transaction.ComputeHash() };
    }
}

/// <summary>
/// Index for fast lookup of transactions by object ID.
/// Stored separately from blocks for efficient queries.
/// </summary>
public record BlockchainIndex
{
    /// <summary>
    /// Map of ObjectId to list of anchor entries.
    /// </summary>
    [JsonPropertyName("objectAnchors")]
    public required Dictionary<string, List<AnchorIndexEntry>> ObjectAnchors { get; init; }

    /// <summary>
    /// Last updated timestamp.
    /// </summary>
    [JsonPropertyName("lastUpdated")]
    public required DateTimeOffset LastUpdated { get; init; }

    /// <summary>
    /// Total number of anchors in the index.
    /// </summary>
    [JsonPropertyName("totalAnchors")]
    public int TotalAnchors { get; init; }

    /// <summary>
    /// Creates an empty index.
    /// </summary>
    public static BlockchainIndex CreateEmpty()
    {
        return new BlockchainIndex
        {
            ObjectAnchors = new Dictionary<string, List<AnchorIndexEntry>>(),
            LastUpdated = DateTimeOffset.UtcNow,
            TotalAnchors = 0
        };
    }
}

/// <summary>
/// Index entry for a specific anchor.
/// </summary>
public class AnchorIndexEntry
{
    /// <summary>
    /// Block number where this anchor is stored.
    /// </summary>
    [JsonPropertyName("blockNumber")]
    public required long BlockNumber { get; init; }

    /// <summary>
    /// Transaction ID (anchor ID).
    /// </summary>
    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    /// <summary>
    /// Version of the object.
    /// </summary>
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    /// <summary>
    /// Timestamp when anchored.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Integrity hash that was anchored.
    /// </summary>
    [JsonPropertyName("integrityHash")]
    public required string IntegrityHash { get; init; }
}
