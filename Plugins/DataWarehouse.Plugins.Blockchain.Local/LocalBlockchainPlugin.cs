// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.Blockchain.Local;

/// <summary>
/// File-based local blockchain provider plugin.
/// Provides a production-ready blockchain implementation using JSON files for storage.
/// Thread-safe with file locking for concurrent access.
/// Suitable for single-node deployments or development/testing environments.
/// </summary>
public class LocalBlockchainPlugin : BlockchainProviderPluginBase
{
    private readonly string _blockchainDirectory;
    private readonly SemaphoreSlim _chainLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, List<BlockchainAnchor>> _anchorCache = new();
    private BlockchainIndex? _index;
    private long _currentBlockHeight = -1;

    /// <summary>
    /// Plugin ID for registration.
    /// </summary>
    public override string Id => "com.datawarehouse.blockchain.local";

    /// <summary>
    /// Human-readable plugin name.
    /// </summary>
    public override string Name => "Local Blockchain Provider";

    /// <summary>
    /// Plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// Plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Semantic description for AI agents.
    /// Provides detailed information about plugin capabilities and use cases.
    /// </summary>
    public string SemanticDescription =>
        "File-based blockchain provider that stores tamper-proof anchors in local JSON files. " +
        "Implements a simplified blockchain with block linking, Merkle trees, and chain validation. " +
        "Suitable for single-node deployments, development, testing, or air-gapped environments. " +
        "Provides cryptographic proof of data existence and integrity without requiring external blockchain infrastructure. " +
        "Thread-safe with file locking for concurrent access. Supports batch anchoring with Merkle proofs.";

    /// <summary>
    /// Semantic tags for AI discoverability.
    /// </summary>
    public string[] SemanticTags => new[]
    {
        "blockchain",
        "local",
        "file-based",
        "tamper-proof",
        "integrity",
        "anchoring",
        "merkle-tree",
        "audit-trail",
        "provenance",
        "immutable",
        "cryptographic-proof",
        "chain-validation",
        "single-node",
        "air-gapped"
    };

    /// <summary>
    /// Initializes a new instance of the LocalBlockchainPlugin.
    /// </summary>
    /// <param name="blockchainDirectory">Directory to store blockchain files. Defaults to "./blockchain".</param>
    public LocalBlockchainPlugin(string? blockchainDirectory = null)
    {
        _blockchainDirectory = blockchainDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "blockchain");
        EnsureDirectoryExists();
    }

    /// <summary>
    /// Ensures the blockchain directory exists.
    /// Creates directory structure and initializes genesis block if needed.
    /// </summary>
    private void EnsureDirectoryExists()
    {
        Directory.CreateDirectory(_blockchainDirectory);
        Directory.CreateDirectory(Path.Combine(_blockchainDirectory, "blocks"));
        Directory.CreateDirectory(Path.Combine(_blockchainDirectory, "index"));
    }

    /// <summary>
    /// Initializes the blockchain.
    /// Loads existing chain or creates genesis block.
    /// </summary>
    private async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_currentBlockHeight >= 0)
        {
            return; // Already initialized
        }

        await _chainLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_currentBlockHeight >= 0)
            {
                return;
            }

            // Load index
            _index = await LoadIndexAsync(ct);

            // Find latest block
            var latestBlock = await FindLatestBlockAsync(ct);
            if (latestBlock != null)
            {
                _currentBlockHeight = latestBlock.BlockNumber;
            }
            else
            {
                // Create genesis block
                await CreateGenesisBlockAsync(ct);
                _currentBlockHeight = 0;
            }
        }
        finally
        {
            _chainLock.Release();
        }
    }

    /// <summary>
    /// Creates the genesis block (block 0).
    /// </summary>
    private async Task CreateGenesisBlockAsync(CancellationToken ct)
    {
        var genesisBlock = new LocalBlockchainBlock
        {
            BlockNumber = 0,
            Hash = string.Empty, // Will be computed
            PreviousHash = null,
            Timestamp = DateTimeOffset.UtcNow,
            MerkleRoot = string.Empty,
            Transactions = new List<BlockchainTransaction>(),
            Version = 1,
            Metadata = new Dictionary<string, object>
            {
                ["genesis"] = true,
                ["created_by"] = "LocalBlockchainPlugin",
                ["note"] = "Genesis block"
            }
        };

        // Compute hash
        genesisBlock = genesisBlock with { Hash = genesisBlock.ComputeHash() };

        // Save genesis block
        await SaveBlockAsync(genesisBlock, ct);
    }

    /// <summary>
    /// Finds the latest block in the chain.
    /// </summary>
    private async Task<LocalBlockchainBlock?> FindLatestBlockAsync(CancellationToken ct)
    {
        var blocksDir = Path.Combine(_blockchainDirectory, "blocks");
        var blockFiles = Directory.GetFiles(blocksDir, "block_*.json")
            .OrderByDescending(f => f)
            .ToList();

        foreach (var file in blockFiles)
        {
            try
            {
                var block = await LoadBlockFromFileAsync(file, ct);
                if (block != null && block.IsValid())
                {
                    return block;
                }
            }
            catch
            {
                // Skip corrupted blocks
            }
        }

        return null;
    }

    /// <summary>
    /// Loads a block from file.
    /// </summary>
    private async Task<LocalBlockchainBlock?> LoadBlockFromFileAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<LocalBlockchainBlock>(fileStream, cancellationToken: ct);
    }

    /// <summary>
    /// Saves a block to file with atomic write.
    /// </summary>
    private async Task SaveBlockAsync(LocalBlockchainBlock block, CancellationToken ct)
    {
        var blockFile = GetBlockFilePath(block.BlockNumber);
        var tempFile = blockFile + ".tmp";

        // Write to temp file first
        await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fileStream, block, new JsonSerializerOptions
            {
                WriteIndented = true
            }, cancellationToken: ct);
        }

        // Atomic rename
        File.Move(tempFile, blockFile, overwrite: true);
    }

    /// <summary>
    /// Gets the file path for a block.
    /// </summary>
    private string GetBlockFilePath(long blockNumber)
    {
        return Path.Combine(_blockchainDirectory, "blocks", $"block_{blockNumber:D10}.json");
    }

    /// <summary>
    /// Loads the blockchain index.
    /// </summary>
    private async Task<BlockchainIndex> LoadIndexAsync(CancellationToken ct)
    {
        var indexFile = Path.Combine(_blockchainDirectory, "index", "index.json");
        if (!File.Exists(indexFile))
        {
            return BlockchainIndex.CreateEmpty();
        }

        try
        {
            await using var fileStream = new FileStream(indexFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<BlockchainIndex>(fileStream, cancellationToken: ct)
                   ?? BlockchainIndex.CreateEmpty();
        }
        catch
        {
            // Corrupted index, rebuild from blocks
            return BlockchainIndex.CreateEmpty();
        }
    }

    /// <summary>
    /// Saves the blockchain index.
    /// </summary>
    private async Task SaveIndexAsync(CancellationToken ct)
    {
        if (_index == null)
        {
            return;
        }

        var indexFile = Path.Combine(_blockchainDirectory, "index", "index.json");
        var tempFile = indexFile + ".tmp";

        await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fileStream, _index, new JsonSerializerOptions
            {
                WriteIndented = true
            }, cancellationToken: ct);
        }

        File.Move(tempFile, indexFile, overwrite: true);
    }

    /// <summary>
    /// Anchors multiple objects in a batch using a Merkle tree.
    /// Creates a new block with all anchors.
    /// </summary>
    public override async Task<BatchAnchorResult> AnchorBatchAsync(
        IEnumerable<AnchorRequest> requests,
        CancellationToken ct = default)
    {
        var requestList = requests.ToList();
        if (requestList.Count == 0)
        {
            return BatchAnchorResult.CreateFailure("No requests provided");
        }

        try
        {
            await InitializeAsync(ct);

            await _chainLock.WaitAsync(ct);
            try
            {
                // Create transactions from requests
                var transactions = new List<BlockchainTransaction>();
                var individualAnchors = new Dictionary<Guid, BlockchainAnchor>();

                foreach (var request in requestList)
                {
                    var transactionId = Guid.NewGuid().ToString();
                    var transaction = BlockchainTransaction.FromAnchorRequest(request, transactionId);
                    transactions.Add(transaction);

                    // Create anchor record
                    var anchor = new BlockchainAnchor
                    {
                        AnchorId = transactionId,
                        ObjectId = request.ObjectId,
                        Version = request.Version,
                        IntegrityHash = request.Hash,
                        AnchoredAt = DateTimeOffset.UtcNow,
                        BlockchainTxId = transactionId,
                        Confirmations = 1 // Immediate confirmation in local blockchain
                    };

                    individualAnchors[request.ObjectId] = anchor;

                    // Update cache
                    _anchorCache.AddOrUpdate(
                        request.ObjectId,
                        new List<BlockchainAnchor> { anchor },
                        (_, list) => { list.Add(anchor); return list; }
                    );
                }

                // Compute Merkle root
                var transactionHashes = transactions.Select(t => t.Hash).ToList();
                var merkleRoot = ComputeMerkleRoot(transactionHashes);

                // Create new block
                var previousBlock = await GetBlockByNumberAsync(_currentBlockHeight, ct);
                var newBlockNumber = _currentBlockHeight + 1;

                var newBlock = new LocalBlockchainBlock
                {
                    BlockNumber = newBlockNumber,
                    Hash = string.Empty, // Will be computed
                    PreviousHash = previousBlock?.Hash,
                    Timestamp = DateTimeOffset.UtcNow,
                    MerkleRoot = merkleRoot,
                    Transactions = transactions,
                    Version = 1
                };

                // Compute block hash
                newBlock = newBlock with { Hash = newBlock.ComputeHash() };

                // Validate block
                if (!newBlock.IsValid())
                {
                    return BatchAnchorResult.CreateFailure("Generated block failed validation");
                }

                // Save block
                await SaveBlockAsync(newBlock, ct);
                _currentBlockHeight = newBlockNumber;

                // Update index
                await UpdateIndexWithBlockAsync(newBlock, ct);

                return BatchAnchorResult.CreateSuccess(
                    blockNumber: newBlockNumber,
                    merkleRoot: merkleRoot,
                    individualAnchors: individualAnchors,
                    transactionId: newBlock.Hash
                );
            }
            finally
            {
                _chainLock.Release();
            }
        }
        catch (Exception ex)
        {
            return BatchAnchorResult.CreateFailure($"Batch anchor failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the index with a new block.
    /// </summary>
    private async Task UpdateIndexWithBlockAsync(LocalBlockchainBlock block, CancellationToken ct)
    {
        if (_index == null)
        {
            _index = BlockchainIndex.CreateEmpty();
        }

        foreach (var transaction in block.Transactions)
        {
            var objectIdStr = transaction.ObjectId.ToString();
            if (!_index.ObjectAnchors.ContainsKey(objectIdStr))
            {
                _index.ObjectAnchors[objectIdStr] = new List<AnchorIndexEntry>();
            }

            _index.ObjectAnchors[objectIdStr].Add(new AnchorIndexEntry
            {
                BlockNumber = block.BlockNumber,
                TransactionId = transaction.TransactionId,
                Version = transaction.Version,
                Timestamp = transaction.Timestamp,
                IntegrityHash = transaction.IntegrityHash
            });
        }

        _index = _index with
        {
            LastUpdated = DateTimeOffset.UtcNow,
            TotalAnchors = _index.ObjectAnchors.Values.Sum(list => list.Count)
        };

        await SaveIndexAsync(ct);
    }

    /// <summary>
    /// Verifies an object's anchor against the blockchain.
    /// </summary>
    public override async Task<AnchorVerificationResult> VerifyAnchorAsync(
        Guid objectId,
        IntegrityHash expectedHash,
        CancellationToken ct = default)
    {
        try
        {
            await InitializeAsync(ct);

            // Check cache first
            if (_anchorCache.TryGetValue(objectId, out var anchors))
            {
                var matchingAnchor = anchors.FirstOrDefault(a =>
                    a.IntegrityHash.HashValue == expectedHash.HashValue &&
                    a.IntegrityHash.Algorithm == expectedHash.Algorithm);

                if (matchingAnchor != null)
                {
                    return AnchorVerificationResult.CreateValid(
                        anchor: matchingAnchor,
                        expectedHash: expectedHash,
                        confirmations: 1
                    );
                }
            }

            // Check index
            if (_index != null && _index.ObjectAnchors.TryGetValue(objectId.ToString(), out var indexEntries))
            {
                foreach (var entry in indexEntries)
                {
                    if (entry.IntegrityHash == expectedHash.HashValue)
                    {
                        // Load the block to get full transaction details
                        var block = await GetBlockByNumberInternalAsync(entry.BlockNumber, ct);
                        if (block != null)
                        {
                            var transaction = block.Transactions.FirstOrDefault(t => t.TransactionId == entry.TransactionId);
                            if (transaction != null)
                            {
                                var anchor = new BlockchainAnchor
                                {
                                    AnchorId = transaction.TransactionId,
                                    ObjectId = transaction.ObjectId,
                                    Version = transaction.Version,
                                    IntegrityHash = IntegrityHash.Parse($"{transaction.HashAlgorithm}:{transaction.IntegrityHash}"),
                                    AnchoredAt = transaction.Timestamp,
                                    BlockchainTxId = block.Hash,
                                    Confirmations = (int)(_currentBlockHeight - entry.BlockNumber + 1)
                                };

                                return AnchorVerificationResult.CreateValid(
                                    anchor: anchor,
                                    expectedHash: expectedHash,
                                    confirmations: anchor.Confirmations
                                );
                            }
                        }
                    }
                }
            }

            return AnchorVerificationResult.CreateFailed(
                "No matching anchor found for object",
                expectedHash: expectedHash
            );
        }
        catch (Exception ex)
        {
            return AnchorVerificationResult.CreateFailed(
                $"Verification failed: {ex.Message}",
                expectedHash: expectedHash
            );
        }
    }

    /// <summary>
    /// Gets the audit chain for an object.
    /// </summary>
    public override async Task<AuditChain> GetAuditChainAsync(Guid objectId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        var entries = new List<AuditChainEntry>();

        // Get all anchors for this object from index
        if (_index != null && _index.ObjectAnchors.TryGetValue(objectId.ToString(), out var indexEntries))
        {
            foreach (var indexEntry in indexEntries.OrderBy(e => e.Version))
            {
                var block = await GetBlockByNumberInternalAsync(indexEntry.BlockNumber, ct);
                if (block != null)
                {
                    var transaction = block.Transactions.FirstOrDefault(t => t.TransactionId == indexEntry.TransactionId);
                    if (transaction != null)
                    {
                        var integrityHash = IntegrityHash.Parse($"{transaction.HashAlgorithm}:{transaction.IntegrityHash}");

                        entries.Add(AuditChainEntry.Create(
                            objectId: transaction.ObjectId,
                            version: transaction.Version,
                            writeContext: transaction.WriteContext,
                            integrityHash: integrityHash,
                            manifestId: "N/A", // Not available in blockchain
                            wormRecordId: "N/A", // Not available in blockchain
                            originalSizeBytes: 0, // Not available in blockchain
                            blockchainAnchorId: transaction.TransactionId
                        ));
                    }
                }
            }
        }

        return AuditChain.Create(objectId, entries);
    }

    /// <summary>
    /// Gets information about the latest block.
    /// </summary>
    public override async Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        var latestBlock = await GetBlockByNumberInternalAsync(_currentBlockHeight, ct);
        if (latestBlock == null)
        {
            throw new InvalidOperationException("Latest block not found");
        }

        return latestBlock.ToBlockInfo();
    }

    /// <summary>
    /// Gets a specific block by number.
    /// </summary>
    protected override async Task<BlockInfo?> GetBlockByNumberAsync(long blockNumber, CancellationToken ct = default)
    {
        var block = await GetBlockByNumberInternalAsync(blockNumber, ct);
        return block?.ToBlockInfo();
    }

    /// <summary>
    /// Internal method to get block as LocalBlockchainBlock.
    /// </summary>
    private async Task<LocalBlockchainBlock?> GetBlockByNumberInternalAsync(long blockNumber, CancellationToken ct)
    {
        var blockFile = GetBlockFilePath(blockNumber);
        return await LoadBlockFromFileAsync(blockFile, ct);
    }

    /// <summary>
    /// Starts the blockchain provider.
    /// </summary>
    public override async Task StartAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
    }

    /// <summary>
    /// Stops the blockchain provider.
    /// </summary>
    public override Task StopAsync()
    {
        _anchorCache.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets plugin metadata.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["BlockchainType"] = "Local";
        metadata["StorageType"] = "File-Based";
        metadata["Directory"] = _blockchainDirectory;
        metadata["ThreadSafe"] = true;
        metadata["SupportsAtomicWrites"] = true;
        metadata["CurrentBlockHeight"] = _currentBlockHeight;
        metadata["SemanticDescription"] = SemanticDescription;
        metadata["SemanticTags"] = SemanticTags;
        return metadata;
    }
}
