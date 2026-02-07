using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Block-level tiering strategy that enables sub-file granularity tiering,
/// allowing different parts of a file to reside on different storage tiers.
/// </summary>
/// <remarks>
/// Features:
/// - Tier individual blocks within files
/// - Hot blocks on fast storage, cold blocks on cheap storage
/// - Transparent block reassembly on read
/// - Block-level access tracking
/// - Intelligent block migration based on access patterns
/// </remarks>
public sealed class BlockLevelTieringStrategy : TieringStrategyBase
{
    private readonly ConcurrentDictionary<string, ObjectBlockMap> _blockMaps = new();
    private readonly ConcurrentDictionary<string, BlockAccessStats> _blockStats = new();
    private BlockTierConfig _config = BlockTierConfig.Default;

    /// <summary>
    /// Maps object blocks to their storage tiers.
    /// </summary>
    private sealed class ObjectBlockMap
    {
        public required string ObjectId { get; init; }
        public long TotalSize { get; init; }
        public int BlockSize { get; init; }
        public int BlockCount { get; init; }
        public StorageTier[] BlockTiers { get; init; } = Array.Empty<StorageTier>();
        public string[] BlockLocations { get; init; } = Array.Empty<string>();
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Block-level access statistics.
    /// </summary>
    private sealed class BlockAccessStats
    {
        public required string ObjectId { get; init; }
        public int[] BlockAccessCounts { get; set; } = Array.Empty<int>();
        public DateTime[] LastAccessTimes { get; set; } = Array.Empty<DateTime>();
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Configuration for block-level tiering.
    /// </summary>
    public sealed class BlockTierConfig
    {
        /// <summary>
        /// Default block size in bytes.
        /// </summary>
        public int DefaultBlockSize { get; init; } = 4 * 1024 * 1024; // 4 MB

        /// <summary>
        /// Minimum object size for block-level tiering.
        /// </summary>
        public long MinObjectSizeForBlocking { get; init; } = 64 * 1024 * 1024; // 64 MB

        /// <summary>
        /// Access count threshold to promote a block to hot tier.
        /// </summary>
        public int HotBlockAccessThreshold { get; init; } = 10;

        /// <summary>
        /// Days without access before demoting a block.
        /// </summary>
        public int ColdBlockInactiveDays { get; init; } = 7;

        /// <summary>
        /// Percentage of hot blocks required to consider promoting entire object.
        /// </summary>
        public double HotBlockPercentageThreshold { get; init; } = 0.8;

        /// <summary>
        /// Gets the default configuration.
        /// </summary>
        public static BlockTierConfig Default => new()
        {
            DefaultBlockSize = 4 * 1024 * 1024,
            MinObjectSizeForBlocking = 64 * 1024 * 1024,
            HotBlockAccessThreshold = 10,
            ColdBlockInactiveDays = 7,
            HotBlockPercentageThreshold = 0.8
        };
    }

    /// <summary>
    /// Represents a block within an object.
    /// </summary>
    public sealed class BlockInfo
    {
        /// <summary>
        /// Block index within the object.
        /// </summary>
        public int BlockIndex { get; init; }

        /// <summary>
        /// Offset in bytes from start of object.
        /// </summary>
        public long Offset { get; init; }

        /// <summary>
        /// Size of the block in bytes.
        /// </summary>
        public int Size { get; init; }

        /// <summary>
        /// Current storage tier of the block.
        /// </summary>
        public StorageTier Tier { get; init; }

        /// <summary>
        /// Number of accesses to this block.
        /// </summary>
        public int AccessCount { get; init; }

        /// <summary>
        /// Last access time.
        /// </summary>
        public DateTime LastAccess { get; init; }

        /// <summary>
        /// Storage location identifier.
        /// </summary>
        public string? Location { get; init; }
    }

    /// <summary>
    /// Result of a block-level tier recommendation.
    /// </summary>
    public sealed class BlockTierRecommendation
    {
        /// <summary>
        /// Object identifier.
        /// </summary>
        public required string ObjectId { get; init; }

        /// <summary>
        /// Blocks recommended for promotion.
        /// </summary>
        public IReadOnlyList<int> BlocksToPromote { get; init; } = Array.Empty<int>();

        /// <summary>
        /// Blocks recommended for demotion.
        /// </summary>
        public IReadOnlyList<int> BlocksToDemote { get; init; } = Array.Empty<int>();

        /// <summary>
        /// Summary of block distribution.
        /// </summary>
        public Dictionary<StorageTier, int> BlockDistribution { get; init; } = new();

        /// <summary>
        /// Whether whole-object tiering is more efficient.
        /// </summary>
        public bool RecommendWholeObjectMove { get; init; }

        /// <summary>
        /// Explanation of the recommendation.
        /// </summary>
        public string Reasoning { get; init; } = string.Empty;
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.block-level";

    /// <inheritdoc/>
    public override string DisplayName => "Block-Level Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 10000,
        TypicalLatencyMs = 2.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Block-level tiering that enables sub-file granularity storage tiering. " +
        "Hot blocks are stored on fast storage while cold blocks are moved to cheaper tiers, " +
        "with transparent reassembly on read.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "block", "sub-file", "granular", "hybrid"];

    /// <summary>
    /// Gets or sets the block tier configuration.
    /// </summary>
    public BlockTierConfig Config
    {
        get => _config;
        set => _config = value ?? BlockTierConfig.Default;
    }

    /// <summary>
    /// Initializes block tracking for an object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="totalSize">Total object size in bytes.</param>
    /// <param name="initialTier">Initial tier for all blocks.</param>
    public void InitializeBlockMap(string objectId, long totalSize, StorageTier initialTier = StorageTier.Hot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        if (totalSize < _config.MinObjectSizeForBlocking)
        {
            return; // Too small for block-level tiering
        }

        var blockCount = (int)Math.Ceiling((double)totalSize / _config.DefaultBlockSize);
        var blockTiers = new StorageTier[blockCount];
        var blockLocations = new string[blockCount];

        for (int i = 0; i < blockCount; i++)
        {
            blockTiers[i] = initialTier;
            blockLocations[i] = $"{initialTier.ToString().ToLower()}/{objectId}/block_{i}";
        }

        _blockMaps[objectId] = new ObjectBlockMap
        {
            ObjectId = objectId,
            TotalSize = totalSize,
            BlockSize = _config.DefaultBlockSize,
            BlockCount = blockCount,
            BlockTiers = blockTiers,
            BlockLocations = blockLocations,
            LastUpdated = DateTime.UtcNow
        };

        _blockStats[objectId] = new BlockAccessStats
        {
            ObjectId = objectId,
            BlockAccessCounts = new int[blockCount],
            LastAccessTimes = Enumerable.Repeat(DateTime.UtcNow, blockCount).ToArray(),
            LastUpdated = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Records an access to specific blocks within an object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="offset">Starting byte offset of the access.</param>
    /// <param name="length">Length of the access in bytes.</param>
    public void RecordBlockAccess(string objectId, long offset, long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_blockStats.TryGetValue(objectId, out var stats))
        {
            return;
        }

        var startBlock = (int)(offset / blockMap.BlockSize);
        var endBlock = (int)((offset + length - 1) / blockMap.BlockSize);

        lock (stats)
        {
            for (int i = startBlock; i <= Math.Min(endBlock, stats.BlockAccessCounts.Length - 1); i++)
            {
                stats.BlockAccessCounts[i]++;
                stats.LastAccessTimes[i] = DateTime.UtcNow;
            }
            stats.LastUpdated = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Gets block information for an object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns>List of block information.</returns>
    public IReadOnlyList<BlockInfo> GetBlockInfo(string objectId)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_blockStats.TryGetValue(objectId, out var stats))
        {
            return Array.Empty<BlockInfo>();
        }

        var blocks = new List<BlockInfo>();

        for (int i = 0; i < blockMap.BlockCount; i++)
        {
            var offset = (long)i * blockMap.BlockSize;
            var size = (int)Math.Min(blockMap.BlockSize, blockMap.TotalSize - offset);

            blocks.Add(new BlockInfo
            {
                BlockIndex = i,
                Offset = offset,
                Size = size,
                Tier = blockMap.BlockTiers[i],
                AccessCount = stats.BlockAccessCounts[i],
                LastAccess = stats.LastAccessTimes[i],
                Location = blockMap.BlockLocations[i]
            });
        }

        return blocks;
    }

    /// <summary>
    /// Gets block-level tier recommendations for an object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns>Block-level tier recommendation.</returns>
    public BlockTierRecommendation GetBlockRecommendations(string objectId)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_blockStats.TryGetValue(objectId, out var stats))
        {
            return new BlockTierRecommendation
            {
                ObjectId = objectId,
                Reasoning = "Object not tracked for block-level tiering"
            };
        }

        var blocksToPromote = new List<int>();
        var blocksToDemote = new List<int>();
        var distribution = new Dictionary<StorageTier, int>();

        var now = DateTime.UtcNow;

        for (int i = 0; i < blockMap.BlockCount; i++)
        {
            var currentTier = blockMap.BlockTiers[i];
            var accessCount = stats.BlockAccessCounts[i];
            var lastAccess = stats.LastAccessTimes[i];
            var daysSinceAccess = (now - lastAccess).TotalDays;

            // Track distribution
            distribution.TryGetValue(currentTier, out var count);
            distribution[currentTier] = count + 1;

            // Check for promotion (cold block with high access)
            if (currentTier > StorageTier.Hot && accessCount >= _config.HotBlockAccessThreshold)
            {
                blocksToPromote.Add(i);
            }
            // Check for demotion (hot/warm block with low access)
            else if (currentTier < StorageTier.Cold && daysSinceAccess > _config.ColdBlockInactiveDays)
            {
                blocksToDemote.Add(i);
            }
        }

        // Check if whole-object move is more efficient
        var hotBlockPercentage = distribution.GetValueOrDefault(StorageTier.Hot, 0) / (double)blockMap.BlockCount;
        var coldBlockPercentage = (distribution.GetValueOrDefault(StorageTier.Cold, 0) +
            distribution.GetValueOrDefault(StorageTier.Archive, 0) +
            distribution.GetValueOrDefault(StorageTier.Glacier, 0)) / (double)blockMap.BlockCount;

        var recommendWholeMove = hotBlockPercentage >= _config.HotBlockPercentageThreshold ||
            coldBlockPercentage >= _config.HotBlockPercentageThreshold;

        var reasoning = BuildBlockReasoning(blocksToPromote.Count, blocksToDemote.Count, distribution, recommendWholeMove);

        return new BlockTierRecommendation
        {
            ObjectId = objectId,
            BlocksToPromote = blocksToPromote,
            BlocksToDemote = blocksToDemote,
            BlockDistribution = distribution,
            RecommendWholeObjectMove = recommendWholeMove,
            Reasoning = reasoning
        };
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Check if object is large enough for block-level tiering
        if (data.SizeBytes < _config.MinObjectSizeForBlocking)
        {
            return Task.FromResult(NoChange(data,
                $"Object too small ({FormatSize(data.SizeBytes)}) for block-level tiering. Minimum: {FormatSize(_config.MinObjectSizeForBlocking)}"));
        }

        // Initialize block map if not exists
        if (!_blockMaps.ContainsKey(data.ObjectId))
        {
            InitializeBlockMap(data.ObjectId, data.SizeBytes, data.CurrentTier);
        }

        // Get block recommendations
        var blockRecs = GetBlockRecommendations(data.ObjectId);

        // If whole-object move is recommended, return standard tier recommendation
        if (blockRecs.RecommendWholeObjectMove)
        {
            var dominantTier = blockRecs.BlockDistribution
                .OrderByDescending(kvp => kvp.Value)
                .First().Key;

            if (dominantTier != data.CurrentTier)
            {
                var reason = $"Block analysis recommends whole-object move. {blockRecs.Reasoning}";

                return Task.FromResult(dominantTier < data.CurrentTier
                    ? Promote(data, dominantTier, reason, 0.85, 0.7)
                    : Demote(data, dominantTier, reason, 0.85, 0.7,
                        EstimateMonthlySavings(data.SizeBytes, data.CurrentTier, dominantTier)));
            }
        }

        // Block-level tiering active - report as no change at object level
        // but provide detailed block info in reason
        var blocksPromoting = blockRecs.BlocksToPromote.Count;
        var blocksDemoting = blockRecs.BlocksToDemote.Count;

        if (blocksPromoting > 0 || blocksDemoting > 0)
        {
            return Task.FromResult(NoChange(data,
                $"Block-level tiering: {blocksPromoting} blocks to promote, {blocksDemoting} blocks to demote. " +
                blockRecs.Reasoning));
        }

        return Task.FromResult(NoChange(data,
            $"Block-level tiering active. Distribution: {string.Join(", ", blockRecs.BlockDistribution.Select(kvp => $"{kvp.Key}:{kvp.Value}"))}"));
    }

    /// <summary>
    /// Moves a specific block to a target tier.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="blockIndex">The block index.</param>
    /// <param name="targetTier">The target storage tier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the move was successful.</returns>
    public Task<bool> MoveBlockAsync(string objectId, int blockIndex, StorageTier targetTier, CancellationToken ct = default)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap))
        {
            return Task.FromResult(false);
        }

        if (blockIndex < 0 || blockIndex >= blockMap.BlockCount)
        {
            return Task.FromResult(false);
        }

        lock (blockMap)
        {
            blockMap.BlockTiers[blockIndex] = targetTier;
            blockMap.BlockLocations[blockIndex] = $"{targetTier.ToString().ToLower()}/{objectId}/block_{blockIndex}";
            blockMap.LastUpdated = DateTime.UtcNow;
        }

        return Task.FromResult(true);
    }

    private static string BuildBlockReasoning(int toPromote, int toDemote, Dictionary<StorageTier, int> distribution, bool wholeMove)
    {
        var parts = new List<string>();

        if (toPromote > 0)
            parts.Add($"{toPromote} hot blocks need promotion");

        if (toDemote > 0)
            parts.Add($"{toDemote} cold blocks need demotion");

        var distStr = string.Join(", ", distribution.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}:{kvp.Value}"));
        parts.Add($"Distribution: {distStr}");

        if (wholeMove)
            parts.Add("Whole-object move recommended for efficiency");

        return string.Join(". ", parts);
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        var size = (double)bytes;

        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:F1} {suffixes[order]}";
    }
}
