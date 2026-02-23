// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateBlockchain.Scaling;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateBlockchain;

/// <summary>
/// Production-ready blockchain provider for tamper-proof data anchoring.
/// Uses <see cref="SegmentedBlockStore"/> for scalable block storage with 64 MB segments
/// and <see cref="BlockchainScalingManager"/> for bounded caching and concurrency control.
/// All block indices use <see langword="long"/> to support more than 2 billion blocks.
/// </summary>
public class UltimateBlockchainPlugin : BlockchainProviderPluginBase
{
    private readonly ILogger<UltimateBlockchainPlugin> _logger;
    private readonly BoundedCache<Guid, BlockchainAnchor> _anchors;
    private readonly SegmentedBlockStore _blockStore;
    private readonly BlockchainScalingManager _scalingManager;
    private readonly object _blockchainLock = new();

    // In-memory block list for backward-compatible audit chain traversal.
    // The SegmentedBlockStore is the authoritative store; this list shadows it
    // for operations that need to iterate transactions (audit chain, verify).
    private readonly List<InternalBlock> _blockIndex = new();
    private long _nextBlockNumber = 1;

    /// <summary>
    /// Creates a new UltimateBlockchain plugin instance with segmented block storage.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public UltimateBlockchainPlugin(ILogger<UltimateBlockchainPlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Bounded anchor cache replacing BoundedDictionary
        _anchors = new BoundedCache<Guid, BlockchainAnchor>(
            new BoundedCacheOptions<Guid, BlockchainAnchor>
            {
                MaxEntries = 100_000,
                EvictionPolicy = CacheEvictionMode.LRU
            });

        // Set data directory for segmented store and sharded journal
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse",
            "Blockchain");
        Directory.CreateDirectory(dataDir);

        // Create segmented block store (replaces List<Block>)
        _blockStore = new SegmentedBlockStore(
            logger,
            dataDir,
            segmentSizeBytes: SegmentedBlockStore.DefaultSegmentSizeBytes,
            blocksPerJournalShard: SegmentedBlockStore.DefaultBlocksPerJournalShard,
            maxHotSegments: 4,
            maxCacheEntries: SegmentedBlockStore.DefaultMaxCacheEntries);

        // Create scaling manager wiring the block store
        _scalingManager = new BlockchainScalingManager(
            logger,
            _blockStore,
            new ScalingLimits(
                MaxCacheEntries: SegmentedBlockStore.DefaultMaxCacheEntries,
                MaxConcurrentOperations: Environment.ProcessorCount));

        // Restore from segmented journal if blocks exist, otherwise create genesis
        if (_blockStore.BlockCount > 0)
        {
            _logger.LogInformation("Block store contains {Count} blocks, skipping genesis", _blockStore.BlockCount);
            _nextBlockNumber = _blockStore.BlockCount;
        }
        else
        {
            InitializeGenesisBlock();
        }
    }

    /// <summary>
    /// Gets the scaling manager for runtime reconfiguration and metrics.
    /// </summary>
    public BlockchainScalingManager ScalingManager => _scalingManager;

    /// <summary>
    /// Initialize the blockchain with genesis block.
    /// </summary>
    private void InitializeGenesisBlock()
    {
        lock (_blockchainLock)
        {
            var genesisBlock = new InternalBlock
            {
                BlockNumber = 0,
                PreviousHash = "0000000000000000000000000000000000000000000000000000000000000000",
                Timestamp = DateTimeOffset.UtcNow.AddDays(-365),
                Transactions = new List<BlockchainAnchor>(),
                MerkleRoot = "genesis",
                Hash = ComputeHash("genesis")
            };

            _blockIndex.Add(genesisBlock);

            // Persist to segmented store
            var blockData = new BlockData(
                BlockNumber: 0,
                PreviousHash: genesisBlock.PreviousHash,
                Timestamp: genesisBlock.Timestamp,
                TransactionCount: 0,
                MerkleRoot: genesisBlock.MerkleRoot,
                Hash: genesisBlock.Hash,
                TransactionData: System.Text.Json.JsonSerializer.Serialize(genesisBlock.Transactions));

            _ = _scalingManager.AppendBlockAsync(blockData, CancellationToken.None);

            _logger.LogInformation("Blockchain initialized with genesis block");
        }
    }

    /// <summary>
    /// Journal entry format for persistence (legacy compatibility).
    /// </summary>
    private class BlockJournalEntry
    {
        public long BlockNumber { get; set; }
        public string PreviousHash { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
        public List<BlockchainAnchor> Transactions { get; set; } = new();
        public string MerkleRoot { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.ultimateblockchain";

    /// <inheritdoc/>
    public override string Name => "Ultimate Blockchain Provider";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc/>
    public override async Task<BatchAnchorResult> AnchorBatchAsync(
        IEnumerable<AnchorRequest> requests,
        CancellationToken ct = default)
    {
        var requestList = requests.ToList();
        if (requestList.Count == 0)
        {
            return BatchAnchorResult.CreateFailure("No requests provided");
        }

        _logger.LogInformation("Anchoring batch of {Count} objects to blockchain", requestList.Count);

        try
        {
            // Compute Merkle root from all hashes
            var hashes = requestList.Select(r => r.Hash.HashValue).ToList();
            var merkleRoot = ComputeMerkleRoot(hashes);

            // Create blockchain anchors for each request
            var individualAnchors = new Dictionary<Guid, BlockchainAnchor>();

            foreach (var request in requestList)
            {
                var anchor = new BlockchainAnchor
                {
                    AnchorId = Guid.NewGuid().ToString(),
                    ObjectId = request.ObjectId,
                    Version = request.Version,
                    IntegrityHash = request.Hash,
                    AnchoredAt = DateTimeOffset.UtcNow,
                    BlockchainTxId = null,
                    Confirmations = 0
                };

                individualAnchors[request.ObjectId] = anchor;
                _anchors.Put(request.ObjectId, anchor);
            }

            // Create new block with anchors
            long blockNumber;
            lock (_blockchainLock)
            {
                blockNumber = _nextBlockNumber++;

                string previousHash;
                if (_blockIndex.Count > 0)
                {
                    previousHash = _blockIndex[^1].Hash;
                }
                else
                {
                    previousHash = "0000000000000000000000000000000000000000000000000000000000000000";
                }

                var newBlock = new InternalBlock
                {
                    BlockNumber = blockNumber,
                    PreviousHash = previousHash,
                    Timestamp = DateTimeOffset.UtcNow,
                    Transactions = individualAnchors.Values.ToList(),
                    MerkleRoot = merkleRoot,
                    Hash = string.Empty
                };

                // Compute block hash
                var blockDataStr = $"{newBlock.BlockNumber}{newBlock.PreviousHash}{newBlock.Timestamp:O}{newBlock.MerkleRoot}";
                newBlock.Hash = ComputeHash(blockDataStr);

                // Update anchors with block number
                var updatedAnchors = new List<BlockchainAnchor>();
                foreach (var anchor in newBlock.Transactions)
                {
                    var updated = new BlockchainAnchor
                    {
                        AnchorId = anchor.AnchorId,
                        ObjectId = anchor.ObjectId,
                        Version = anchor.Version,
                        IntegrityHash = anchor.IntegrityHash,
                        AnchoredAt = anchor.AnchoredAt,
                        BlockchainTxId = blockNumber.ToString(),
                        Confirmations = 1
                    };
                    updatedAnchors.Add(updated);

                    _anchors.Put(anchor.ObjectId, updated);
                    individualAnchors[anchor.ObjectId] = updated;
                }
                newBlock.Transactions = updatedAnchors;

                // Add to in-memory index
                _blockIndex.Add(newBlock);

                _logger.LogInformation(
                    "Created block {BlockNumber} with {TxCount} transactions, Merkle root: {MerkleRoot}",
                    blockNumber, newBlock.Transactions.Count, merkleRoot);
            }

            // Persist to segmented block store (outside lock for better concurrency)
            var blockData = new BlockData(
                BlockNumber: blockNumber,
                PreviousHash: _blockIndex[(int)Math.Min(blockNumber, _blockIndex.Count - 1)].PreviousHash,
                Timestamp: _blockIndex[(int)Math.Min(blockNumber, _blockIndex.Count - 1)].Timestamp,
                TransactionCount: individualAnchors.Count,
                MerkleRoot: merkleRoot,
                Hash: _blockIndex[(int)Math.Min(blockNumber, _blockIndex.Count - 1)].Hash,
                TransactionData: System.Text.Json.JsonSerializer.Serialize(individualAnchors.Values.ToList()));

            await _scalingManager.AppendBlockAsync(blockData, ct).ConfigureAwait(false);

            return BatchAnchorResult.CreateSuccess(
                blockNumber,
                merkleRoot,
                individualAnchors,
                blockNumber.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to anchor batch to blockchain");
            return BatchAnchorResult.CreateFailure($"Batch anchoring failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public override Task<AnchorVerificationResult> VerifyAnchorAsync(
        Guid objectId,
        IntegrityHash expectedHash,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Verifying blockchain anchor for object {ObjectId}", objectId);

        try
        {
            var anchor = _anchors.GetOrDefault(objectId);
            if (anchor == null)
            {
                return Task.FromResult(AnchorVerificationResult.CreateFailed(
                    "Anchor not found in blockchain",
                    expectedHash,
                    null));
            }

            // Verify hash matches
            var hashMatches = string.Equals(
                anchor.IntegrityHash.HashValue,
                expectedHash.HashValue,
                StringComparison.OrdinalIgnoreCase);

            if (!hashMatches)
            {
                return Task.FromResult(AnchorVerificationResult.CreateFailed(
                    "Hash mismatch",
                    expectedHash,
                    anchor.IntegrityHash));
            }

            // Verify anchor is in a valid block
            if (!long.TryParse(anchor.BlockchainTxId, out var blockNumber))
            {
                return Task.FromResult(AnchorVerificationResult.CreateFailed(
                    "Invalid block number",
                    expectedHash,
                    anchor.IntegrityHash));
            }

            lock (_blockchainLock)
            {
                // Use long comparison -- no (int) cast
                long totalBlocks = _blockIndex.Count;
                if (blockNumber < 0 || blockNumber >= totalBlocks)
                {
                    return Task.FromResult(AnchorVerificationResult.CreateFailed(
                        "Block not found in chain",
                        expectedHash,
                        anchor.IntegrityHash));
                }

                // Safe index: blockNumber is validated to be within int range by the Count check
                var block = _blockIndex[(int)Math.Min(blockNumber, int.MaxValue)];

                // Verify anchor is in block's transactions
                if (!block.Transactions.Any(t => t.AnchorId == anchor.AnchorId))
                {
                    return Task.FromResult(AnchorVerificationResult.CreateFailed(
                        "Anchor not found in block transactions",
                        expectedHash,
                        anchor.IntegrityHash));
                }

                // Calculate confirmations using long arithmetic (no int overflow)
                long confirmations = totalBlocks - blockNumber;

                _logger.LogDebug(
                    "Anchor verified for object {ObjectId}: Block {BlockNumber}, Confirmations {Confirmations}",
                    objectId, blockNumber, confirmations);

                return Task.FromResult(AnchorVerificationResult.CreateValid(
                    anchor,
                    expectedHash,
                    (int)Math.Min(confirmations, int.MaxValue),
                    null));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying anchor for object {ObjectId}", objectId);
            return Task.FromResult(AnchorVerificationResult.CreateFailed(
                $"Verification error: {ex.Message}",
                expectedHash,
                null));
        }
    }

    /// <inheritdoc/>
    public override Task<AuditChain> GetAuditChainAsync(Guid objectId, CancellationToken ct = default)
    {
        _logger.LogDebug("Retrieving audit chain for object {ObjectId}", objectId);

        try
        {
            var entries = new List<AuditChainEntry>();

            lock (_blockchainLock)
            {
                // Find all anchors for this object across all blocks
                foreach (var block in _blockIndex)
                {
                    var matchingAnchors = block.Transactions
                        .Where(t => t.ObjectId == objectId)
                        .ToList();

                    foreach (var anchor in matchingAnchors)
                    {
                        entries.Add(new AuditChainEntry
                        {
                            ObjectId = objectId,
                            Version = anchor.Version,
                            WriteContext = new WriteContextRecord
                            {
                                Author = "blockchain",
                                Comment = $"Anchored to block {block.BlockNumber}",
                                Timestamp = anchor.AnchoredAt,
                                SourceSystem = "ultimate-blockchain"
                            },
                            IntegrityHash = anchor.IntegrityHash,
                            ManifestId = $"block-{block.BlockNumber}",
                            WormRecordId = $"anchor-{anchor.AnchorId}",
                            BlockchainAnchorId = anchor.AnchorId,
                            OriginalSizeBytes = 0,
                            CreatedAt = anchor.AnchoredAt
                        });
                    }
                }
            }

            var auditChain = new AuditChain
            {
                RootObjectId = objectId,
                Entries = entries.OrderBy(e => e.Version).ToList()
            };

            _logger.LogDebug("Retrieved audit chain for object {ObjectId}: {Versions} versions",
                objectId, auditChain.TotalVersions);

            return Task.FromResult(auditChain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve audit chain for object {ObjectId}", objectId);
            throw;
        }
    }

    /// <inheritdoc/>
    public override Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default)
    {
        lock (_blockchainLock)
        {
            if (_blockIndex.Count == 0)
            {
                throw new InvalidOperationException("Blockchain is empty");
            }

            var latestBlock = _blockIndex[^1];
            return Task.FromResult(BlockInfo.Create(
                latestBlock.BlockNumber,
                latestBlock.Hash,
                latestBlock.Timestamp,
                latestBlock.Transactions.Count,
                latestBlock.PreviousHash,
                latestBlock.MerkleRoot));
        }
    }

    /// <inheritdoc/>
    protected override Task<BlockInfo?> GetBlockByNumberAsync(long blockNumber, CancellationToken ct = default)
    {
        lock (_blockchainLock)
        {
            // Long-safe bounds check -- no (int) cast on blockNumber for comparison
            long totalBlocks = _blockIndex.Count;
            if (blockNumber < 0 || blockNumber >= totalBlocks)
            {
                return Task.FromResult<BlockInfo?>(null);
            }

            // Safe: blockNumber is validated within int range by totalBlocks (List count is int)
            var block = _blockIndex[(int)blockNumber];
            return Task.FromResult<BlockInfo?>(BlockInfo.Create(
                block.BlockNumber,
                block.Hash,
                block.Timestamp,
                block.Transactions.Count,
                block.PreviousHash,
                block.MerkleRoot));
        }
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case "blockchain.anchor":
                    await HandleAnchorAsync(message, CancellationToken.None);
                    break;

                case "blockchain.verify":
                    await HandleVerifyAsync(message, CancellationToken.None);
                    break;

                case "blockchain.chain":
                    await HandleGetAuditChainAsync(message, CancellationToken.None);
                    break;

                case "blockchain.latest":
                    await HandleGetLatestBlockAsync(message, CancellationToken.None);
                    break;

                default:
                    _logger.LogDebug("UltimateBlockchain received unknown message type: {Type}", message.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message of type {Type}", message.Type);
            message.Payload["error"] = $"Message handling failed: {ex.Message}";
        }
    }

    private async Task HandleAnchorAsync(PluginMessage message, CancellationToken ct)
    {
        try
        {
            if (!message.Payload.TryGetValue("objectId", out var objIdValue) ||
                objIdValue is not Guid objectId)
            {
                message.Payload["error"] = "Missing or invalid 'objectId' field";
                return;
            }

            if (!message.Payload.TryGetValue("hash", out var hashValue) ||
                hashValue is not IntegrityHash hash)
            {
                message.Payload["error"] = "Missing or invalid 'hash' field";
                return;
            }

            var version = message.Payload.TryGetValue("version", out var verValue) && verValue is int v
                ? v
                : 1;

            var request = new AnchorRequest
            {
                ObjectId = objectId,
                Hash = hash,
                Version = version,
                Timestamp = DateTimeOffset.UtcNow,
                WriteContext = new WriteContextRecord
                {
                    Author = "system",
                    Comment = "Blockchain anchor",
                    Timestamp = DateTimeOffset.UtcNow,
                    SourceSystem = "ultimate-blockchain"
                }
            };

            var anchor = await AnchorAsync(request, ct);

            message.Payload["anchor"] = anchor;
            message.Payload["success"] = true;

            _logger.LogDebug("Anchored object {ObjectId} to blockchain: {AnchorId}",
                objectId, anchor.AnchorId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to anchor object to blockchain");
            message.Payload["error"] = $"Anchor failed: {ex.Message}";
            message.Payload["success"] = false;
        }
    }

    private async Task HandleVerifyAsync(PluginMessage message, CancellationToken ct)
    {
        try
        {
            if (!message.Payload.TryGetValue("objectId", out var objIdValue) ||
                objIdValue is not Guid objectId)
            {
                message.Payload["error"] = "Missing or invalid 'objectId' field";
                return;
            }

            if (!message.Payload.TryGetValue("expectedHash", out var hashValue) ||
                hashValue is not IntegrityHash expectedHash)
            {
                message.Payload["error"] = "Missing or invalid 'expectedHash' field";
                return;
            }

            var result = await VerifyAnchorAsync(objectId, expectedHash, ct);

            message.Payload["result"] = result;
            message.Payload["isValid"] = result.IsValid;

            _logger.LogDebug("Blockchain verification for object {ObjectId}: {Result}",
                objectId, result.IsValid ? "VALID" : "FAILED");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify blockchain anchor");
            message.Payload["error"] = $"Verification failed: {ex.Message}";
            message.Payload["isValid"] = false;
        }
    }

    private async Task HandleGetAuditChainAsync(PluginMessage message, CancellationToken ct)
    {
        try
        {
            if (!message.Payload.TryGetValue("objectId", out var objIdValue) ||
                objIdValue is not Guid objectId)
            {
                message.Payload["error"] = "Missing or invalid 'objectId' field";
                return;
            }

            var auditChain = await GetAuditChainAsync(objectId, ct);

            message.Payload["auditChain"] = auditChain;
            message.Payload["success"] = true;

            _logger.LogDebug("Retrieved audit chain for object {ObjectId}: {Versions} versions",
                objectId, auditChain.TotalVersions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve audit chain");
            message.Payload["error"] = $"Audit chain retrieval failed: {ex.Message}";
            message.Payload["success"] = false;
        }
    }

    private async Task HandleGetLatestBlockAsync(PluginMessage message, CancellationToken ct)
    {
        try
        {
            var latestBlock = await GetLatestBlockAsync(ct);

            message.Payload["blockInfo"] = latestBlock;
            message.Payload["success"] = true;

            _logger.LogDebug("Retrieved latest block: {BlockNumber}", latestBlock.BlockNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve latest block");
            message.Payload["error"] = $"Latest block retrieval failed: {ex.Message}";
            message.Payload["success"] = false;
        }
    }

    /// <summary>
    /// Internal block structure for the in-memory index (not the authoritative store).
    /// The authoritative store is <see cref="SegmentedBlockStore"/>; this is a shadow
    /// index for fast transaction lookups during audit chain and verification operations.
    /// </summary>
    private class InternalBlock
    {
        public required long BlockNumber { get; init; }
        public required string PreviousHash { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public required List<BlockchainAnchor> Transactions { get; set; }
        public required string MerkleRoot { get; init; }
        public required string Hash { get; set; }
    }
}
