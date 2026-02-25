using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Block-level tiering strategy that enables sub-file granularity tiering,
/// allowing different parts of a file to reside on different storage tiers.
/// Implements T81 Liquid Storage Tiers with all sub-tasks.
/// </summary>
/// <remarks>
/// Features (T81 Sub-tasks):
/// - 81.1: Block Access Tracker - Track access frequency per block
/// - 81.2: Heatmap Generator - Visual and queryable block heat distribution
/// - 81.3: Block Splitter - Split files into independently movable blocks
/// - 81.4: Transparent Reassembly - Seamlessly reassemble blocks for file reads
/// - 81.5: Tier Migration Engine - Move individual blocks between storage tiers
/// - 81.6: Predictive Prefetch - Anticipate block access and pre-stage
/// - 81.7: Block Metadata Index - Track which blocks are on which tier
/// - 81.8: Cost Optimizer - Balance performance vs storage cost per block
/// - 81.9: Database Optimization - Special handling for database file patterns
/// - 81.10: Real-time Rebalancing - Continuous optimization as access patterns change
/// </remarks>
public sealed class BlockLevelTieringStrategy : TieringStrategyBase
{
    private readonly BoundedDictionary<string, ObjectBlockMap> _blockMaps = new BoundedDictionary<string, ObjectBlockMap>(1000);
    private readonly BoundedDictionary<string, BlockAccessStats> _blockStats = new BoundedDictionary<string, BlockAccessStats>(1000);
    private readonly BoundedDictionary<string, BlockHeatmap> _heatmaps = new BoundedDictionary<string, BlockHeatmap>(1000);
    private readonly BoundedDictionary<string, PrefetchPrediction> _prefetchPredictions = new BoundedDictionary<string, PrefetchPrediction>(1000);
    private readonly BoundedDictionary<string, DatabaseFileInfo> _databaseFiles = new BoundedDictionary<string, DatabaseFileInfo>(1000);
    private readonly ConcurrentQueue<RebalanceEvent> _rebalanceQueue = new();
    private readonly Timer? _rebalanceTimer;
    private readonly object _rebalanceLock = new();
    private BlockTierConfig _config = BlockTierConfig.Default;
    private volatile bool _rebalanceInProgress;

    /// <summary>
    /// Initializes a new instance of the BlockLevelTieringStrategy.
    /// </summary>
    public BlockLevelTieringStrategy()
    {
        _rebalanceTimer = new Timer(
            RebalanceCallback,
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    #region Inner Classes

    /// <summary>
    /// Maps object blocks to their storage tiers (T81.7 - Block Metadata Index).
    /// </summary>
    private sealed class ObjectBlockMap
    {
        public required string ObjectId { get; init; }
        public string? FilePath { get; init; }
        public long TotalSize { get; init; }
        public int BlockSize { get; init; }
        public int BlockCount { get; init; }
        public StorageTier[] BlockTiers { get; init; } = Array.Empty<StorageTier>();
        public string[] BlockLocations { get; init; } = Array.Empty<string>();
        public byte[][] BlockHashes { get; init; } = Array.Empty<byte[]>();
        public DateTime LastUpdated { get; set; }
        public bool IsDatabaseFile { get; set; }
        public DatabaseFileType DatabaseType { get; set; }
    }

    /// <summary>
    /// Block-level access statistics (T81.1 - Block Access Tracker).
    /// </summary>
    private sealed class BlockAccessStats
    {
        public required string ObjectId { get; init; }
        public int[] BlockAccessCounts { get; set; } = Array.Empty<int>();
        public DateTime[] LastAccessTimes { get; set; } = Array.Empty<DateTime>();
        public int[] AccessesLast24Hours { get; set; } = Array.Empty<int>();
        public int[] AccessesLast7Days { get; set; } = Array.Empty<int>();
        public long[] TotalBytesRead { get; set; } = Array.Empty<long>();
        public double[] AverageLatencyMs { get; set; } = Array.Empty<double>();
        public int[][] AccessSequences { get; set; } = Array.Empty<int[]>();
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Block heat distribution data (T81.2 - Heatmap Generator).
    /// </summary>
    public sealed class BlockHeatmap
    {
        /// <summary>
        /// Object identifier.
        /// </summary>
        public required string ObjectId { get; init; }

        /// <summary>
        /// Heat values per block (0.0 = cold, 1.0 = hot).
        /// </summary>
        public double[] HeatValues { get; set; } = Array.Empty<double>();

        /// <summary>
        /// Tier recommendations per block based on heat.
        /// </summary>
        public StorageTier[] RecommendedTiers { get; set; } = Array.Empty<StorageTier>();

        /// <summary>
        /// Hot regions (contiguous hot blocks).
        /// </summary>
        public List<BlockRange> HotRegions { get; set; } = new();

        /// <summary>
        /// Cold regions (contiguous cold blocks).
        /// </summary>
        public List<BlockRange> ColdRegions { get; set; } = new();

        /// <summary>
        /// Time when heatmap was generated.
        /// </summary>
        public DateTime GeneratedAt { get; set; }

        /// <summary>
        /// Overall heat score (0.0-1.0).
        /// </summary>
        public double OverallHeatScore { get; set; }
    }

    /// <summary>
    /// Represents a contiguous range of blocks.
    /// </summary>
    public sealed class BlockRange
    {
        public int StartBlock { get; init; }
        public int EndBlock { get; init; }
        public double AverageHeat { get; init; }
        public int BlockCount => EndBlock - StartBlock + 1;
    }

    /// <summary>
    /// Prefetch prediction data (T81.6 - Predictive Prefetch).
    /// </summary>
    private sealed class PrefetchPrediction
    {
        public required string ObjectId { get; init; }
        public int[] PredictedNextBlocks { get; set; } = Array.Empty<int>();
        public double[] PredictionConfidence { get; set; } = Array.Empty<double>();
        public Dictionary<int, int[]> SequentialPatterns { get; set; } = new();
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Database file information (T81.9 - Database Optimization).
    /// </summary>
    public sealed class DatabaseFileInfo
    {
        /// <summary>Object identifier.</summary>
        public required string ObjectId { get; init; }
        /// <summary>Database type.</summary>
        public DatabaseFileType Type { get; init; }
        /// <summary>Block indexes containing headers.</summary>
        public int[] HeaderBlocks { get; set; } = Array.Empty<int>();
        /// <summary>Block indexes containing indexes.</summary>
        public int[] IndexBlocks { get; set; } = Array.Empty<int>();
        /// <summary>Block indexes containing data.</summary>
        public int[] DataBlocks { get; set; } = Array.Empty<int>();
        /// <summary>Block indexes containing free space.</summary>
        public int[] FreeSpaceBlocks { get; set; } = Array.Empty<int>();
        /// <summary>Database page size in bytes.</summary>
        public int PageSize { get; init; }
        /// <summary>When the file was last analyzed.</summary>
        public DateTime LastAnalyzed { get; set; }
    }

    /// <summary>
    /// Database file type enumeration.
    /// </summary>
    public enum DatabaseFileType
    {
        Unknown = 0,
        SqlServer,
        Sqlite,
        PostgreSql,
        MySql,
        Oracle,
        LevelDb,
        RocksDb
    }

    /// <summary>
    /// Rebalance event for real-time optimization (T81.10).
    /// </summary>
    private sealed class RebalanceEvent
    {
        public required string ObjectId { get; init; }
        public RebalanceReason Reason { get; init; }
        public int[] AffectedBlocks { get; set; } = Array.Empty<int>();
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public double Priority { get; init; }
    }

    private enum RebalanceReason
    {
        AccessPatternChange,
        CostThresholdExceeded,
        PerformanceDegradation,
        CapacityPressure,
        ScheduledOptimization
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
        /// Heat decay factor per hour (0.0-1.0).
        /// </summary>
        public double HeatDecayFactor { get; init; } = 0.95;

        /// <summary>
        /// Prefetch lookahead blocks.
        /// </summary>
        public int PrefetchLookahead { get; init; } = 4;

        /// <summary>
        /// Minimum confidence for prefetch.
        /// </summary>
        public double PrefetchMinConfidence { get; init; } = 0.7;

        /// <summary>
        /// Cost per GB per month for each tier.
        /// </summary>
        public Dictionary<StorageTier, decimal> TierCosts { get; init; } = new()
        {
            [StorageTier.Hot] = 0.023m,
            [StorageTier.Warm] = 0.0125m,
            [StorageTier.Cold] = 0.004m,
            [StorageTier.Archive] = 0.00099m,
            [StorageTier.Glacier] = 0.0004m
        };

        /// <summary>
        /// Latency penalty multiplier for each tier.
        /// </summary>
        public Dictionary<StorageTier, double> TierLatencyMultipliers { get; init; } = new()
        {
            [StorageTier.Hot] = 1.0,
            [StorageTier.Warm] = 2.0,
            [StorageTier.Cold] = 5.0,
            [StorageTier.Archive] = 50.0,
            [StorageTier.Glacier] = 3600.0 // Hours
        };

        /// <summary>
        /// Rebalance interval in minutes.
        /// </summary>
        public int RebalanceIntervalMinutes { get; init; } = 1;

        /// <summary>
        /// Maximum blocks to migrate per rebalance cycle.
        /// </summary>
        public int MaxBlocksPerRebalance { get; init; } = 100;

        /// <summary>
        /// Gets the default configuration.
        /// </summary>
        public static BlockTierConfig Default => new();
    }

    /// <summary>
    /// Represents a block within an object.
    /// </summary>
    public sealed class BlockInfo
    {
        /// <summary>Block index within the object.</summary>
        public int BlockIndex { get; init; }
        /// <summary>Offset in bytes from start of object.</summary>
        public long Offset { get; init; }
        /// <summary>Size of the block in bytes.</summary>
        public int Size { get; init; }
        /// <summary>Current storage tier of the block.</summary>
        public StorageTier Tier { get; init; }
        /// <summary>Number of accesses to this block.</summary>
        public int AccessCount { get; init; }
        /// <summary>Last access time.</summary>
        public DateTime LastAccess { get; init; }
        /// <summary>Storage location identifier.</summary>
        public string? Location { get; init; }
        /// <summary>Heat value (0.0 = cold, 1.0 = hot).</summary>
        public double HeatValue { get; init; }
        /// <summary>SHA256 hash of block content.</summary>
        public byte[]? ContentHash { get; init; }
    }

    /// <summary>
    /// Result of a block-level tier recommendation.
    /// </summary>
    public sealed class BlockTierRecommendation
    {
        /// <summary>Object identifier.</summary>
        public required string ObjectId { get; init; }
        /// <summary>Blocks recommended for promotion.</summary>
        public IReadOnlyList<int> BlocksToPromote { get; init; } = Array.Empty<int>();
        /// <summary>Blocks recommended for demotion.</summary>
        public IReadOnlyList<int> BlocksToDemote { get; init; } = Array.Empty<int>();
        /// <summary>Summary of block distribution.</summary>
        public Dictionary<StorageTier, int> BlockDistribution { get; init; } = new();
        /// <summary>Whether whole-object tiering is more efficient.</summary>
        public bool RecommendWholeObjectMove { get; init; }
        /// <summary>Explanation of the recommendation.</summary>
        public string Reasoning { get; init; } = string.Empty;
        /// <summary>Estimated monthly savings if recommendations applied.</summary>
        public decimal EstimatedMonthlySavings { get; init; }
        /// <summary>Performance impact score (-1.0 to 1.0).</summary>
        public double PerformanceImpactScore { get; init; }
    }

    /// <summary>
    /// Split result for block splitter (T81.3).
    /// </summary>
    public sealed class BlockSplitResult
    {
        /// <summary>Object identifier.</summary>
        public required string ObjectId { get; init; }
        /// <summary>Total blocks created.</summary>
        public int BlockCount { get; init; }
        /// <summary>Block size used.</summary>
        public int BlockSize { get; init; }
        /// <summary>Block information.</summary>
        public IReadOnlyList<BlockInfo> Blocks { get; init; } = Array.Empty<BlockInfo>();
        /// <summary>Whether split was successful.</summary>
        public bool Success { get; init; }
        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Reassembly result for transparent reassembly (T81.4).
    /// </summary>
    public sealed class BlockReassemblyResult
    {
        /// <summary>Object identifier.</summary>
        public required string ObjectId { get; init; }
        /// <summary>Tiers data was fetched from.</summary>
        public Dictionary<StorageTier, int> TierFetchCounts { get; init; } = new();
        /// <summary>Total bytes reassembled.</summary>
        public long TotalBytes { get; init; }
        /// <summary>Total latency in milliseconds.</summary>
        public double TotalLatencyMs { get; init; }
        /// <summary>Blocks that were prefetched.</summary>
        public int[] PrefetchedBlocks { get; init; } = Array.Empty<int>();
        /// <summary>Whether reassembly was successful.</summary>
        public bool Success { get; init; }
        /// <summary>Error message if failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Cost optimization analysis result (T81.8).
    /// </summary>
    public sealed class CostOptimizationAnalysis
    {
        /// <summary>Object identifier.</summary>
        public required string ObjectId { get; init; }
        /// <summary>Current monthly cost.</summary>
        public decimal CurrentMonthlyCost { get; init; }
        /// <summary>Optimized monthly cost.</summary>
        public decimal OptimizedMonthlyCost { get; init; }
        /// <summary>Monthly savings.</summary>
        public decimal MonthlySavings => CurrentMonthlyCost - OptimizedMonthlyCost;
        /// <summary>Savings percentage.</summary>
        public double SavingsPercentage => CurrentMonthlyCost > 0
            ? (double)(MonthlySavings / CurrentMonthlyCost) * 100 : 0;
        /// <summary>Performance score before (0-100).</summary>
        public double CurrentPerformanceScore { get; init; }
        /// <summary>Performance score after (0-100).</summary>
        public double OptimizedPerformanceScore { get; init; }
        /// <summary>Performance tradeoff percentage.</summary>
        public double PerformanceTradeoffPercent { get; init; }
        /// <summary>Recommended block moves.</summary>
        public List<BlockMoveRecommendation> RecommendedMoves { get; init; } = new();
        /// <summary>Analysis timestamp.</summary>
        public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Block move recommendation.
    /// </summary>
    public sealed class BlockMoveRecommendation
    {
        public int BlockIndex { get; init; }
        public StorageTier CurrentTier { get; init; }
        public StorageTier TargetTier { get; init; }
        public decimal CostSavings { get; init; }
        public double LatencyImpact { get; init; }
        public double Priority { get; init; }
    }

    #endregion

    #region Strategy Properties

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
        "Implements T81 Liquid Storage Tiers with block access tracking, heatmap generation, " +
        "predictive prefetch, cost optimization, database-aware optimization, and real-time rebalancing.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "block", "sub-file", "granular", "hybrid",
        "heatmap", "prefetch", "cost-optimization", "database-aware", "rebalancing"];

    /// <summary>
    /// Gets or sets the block tier configuration.
    /// </summary>
    public BlockTierConfig Config
    {
        get => _config;
        set => _config = value ?? BlockTierConfig.Default;
    }

    #endregion

    #region T81.1 - Block Access Tracker

    /// <summary>
    /// Initializes block tracking for an object (T81.1, T81.7).
    /// </summary>
    public void InitializeBlockMap(string objectId, long totalSize, StorageTier initialTier = StorageTier.Hot,
        string? filePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        if (totalSize < _config.MinObjectSizeForBlocking)
        {
            return; // Too small for block-level tiering
        }

        var blockCount = (int)Math.Ceiling((double)totalSize / _config.DefaultBlockSize);
        var blockTiers = new StorageTier[blockCount];
        var blockLocations = new string[blockCount];
        var blockHashes = new byte[blockCount][];

        for (int i = 0; i < blockCount; i++)
        {
            blockTiers[i] = initialTier;
            blockLocations[i] = $"{initialTier.ToString().ToLower()}/{objectId}/block_{i}";
            blockHashes[i] = Array.Empty<byte>();
        }

        var isDatabaseFile = DetectDatabaseFile(filePath);
        var dbType = isDatabaseFile ? DetectDatabaseType(filePath) : DatabaseFileType.Unknown;

        _blockMaps[objectId] = new ObjectBlockMap
        {
            ObjectId = objectId,
            FilePath = filePath,
            TotalSize = totalSize,
            BlockSize = _config.DefaultBlockSize,
            BlockCount = blockCount,
            BlockTiers = blockTiers,
            BlockLocations = blockLocations,
            BlockHashes = blockHashes,
            LastUpdated = DateTime.UtcNow,
            IsDatabaseFile = isDatabaseFile,
            DatabaseType = dbType
        };

        _blockStats[objectId] = new BlockAccessStats
        {
            ObjectId = objectId,
            BlockAccessCounts = new int[blockCount],
            LastAccessTimes = Enumerable.Repeat(DateTime.UtcNow, blockCount).ToArray(),
            AccessesLast24Hours = new int[blockCount],
            AccessesLast7Days = new int[blockCount],
            TotalBytesRead = new long[blockCount],
            AverageLatencyMs = new double[blockCount],
            AccessSequences = new int[blockCount][],
            LastUpdated = DateTime.UtcNow
        };

        // Initialize access sequences
        for (int i = 0; i < blockCount; i++)
        {
            _blockStats[objectId].AccessSequences[i] = Array.Empty<int>();
        }

        // Initialize heatmap
        _heatmaps[objectId] = new BlockHeatmap
        {
            ObjectId = objectId,
            HeatValues = new double[blockCount],
            RecommendedTiers = Enumerable.Repeat(initialTier, blockCount).ToArray(),
            GeneratedAt = DateTime.UtcNow
        };

        // Initialize prefetch predictions
        _prefetchPredictions[objectId] = new PrefetchPrediction
        {
            ObjectId = objectId,
            PredictedNextBlocks = Array.Empty<int>(),
            PredictionConfidence = Array.Empty<double>(),
            LastUpdated = DateTime.UtcNow
        };

        // Analyze database file if applicable
        if (isDatabaseFile)
        {
            AnalyzeDatabaseFile(objectId, dbType, blockCount);
        }
    }

    /// <summary>
    /// Records an access to specific blocks within an object (T81.1).
    /// </summary>
    public void RecordBlockAccess(string objectId, long offset, long length, double? latencyMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_blockStats.TryGetValue(objectId, out var stats))
        {
            return;
        }

        var startBlock = (int)(offset / blockMap.BlockSize);
        var endBlock = (int)((offset + length - 1) / blockMap.BlockSize);
        var accessedBlocks = new List<int>();

        lock (stats)
        {
            for (int i = startBlock; i <= Math.Min(endBlock, stats.BlockAccessCounts.Length - 1); i++)
            {
                stats.BlockAccessCounts[i]++;
                stats.LastAccessTimes[i] = DateTime.UtcNow;
                stats.AccessesLast24Hours[i]++;
                stats.AccessesLast7Days[i]++;

                var blockBytes = (long)Math.Min(
                    blockMap.BlockSize,
                    length - (i - startBlock) * blockMap.BlockSize);
                stats.TotalBytesRead[i] += blockBytes;

                if (latencyMs.HasValue)
                {
                    // Exponential moving average for latency
                    stats.AverageLatencyMs[i] = stats.AverageLatencyMs[i] * 0.9 + latencyMs.Value * 0.1;
                }

                accessedBlocks.Add(i);
            }
            stats.LastUpdated = DateTime.UtcNow;
        }

        // Update access sequences for pattern detection (for prefetch)
        UpdateAccessSequences(objectId, accessedBlocks.ToArray());

        // Update heatmap
        UpdateHeatmap(objectId, accessedBlocks.ToArray());

        // Check if rebalance needed
        CheckRebalanceNeeded(objectId, accessedBlocks.ToArray());
    }

    private void UpdateAccessSequences(string objectId, int[] accessedBlocks)
    {
        if (!_prefetchPredictions.TryGetValue(objectId, out var prediction) ||
            !_blockStats.TryGetValue(objectId, out var stats))
        {
            return;
        }

        lock (prediction)
        {
            // Record the sequence for pattern detection
            foreach (var block in accessedBlocks)
            {
                // Keep last N access sequences per block
                var current = stats.AccessSequences[block];
                var updated = current.Length >= 10
                    ? current.Skip(1).Concat(accessedBlocks.Where(b => b != block)).Take(10).ToArray()
                    : current.Concat(accessedBlocks.Where(b => b != block)).Take(10).ToArray();
                stats.AccessSequences[block] = updated;
            }

            // Detect sequential patterns
            DetectSequentialPatterns(prediction, stats);
            prediction.LastUpdated = DateTime.UtcNow;
        }
    }

    private void DetectSequentialPatterns(PrefetchPrediction prediction, BlockAccessStats stats)
    {
        prediction.SequentialPatterns.Clear();

        for (int block = 0; block < stats.AccessSequences.Length; block++)
        {
            var sequence = stats.AccessSequences[block];
            if (sequence.Length < 2) continue;

            // Find most common next blocks
            var nextBlockCounts = sequence
                .GroupBy(b => b)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key)
                .ToArray();

            if (nextBlockCounts.Length > 0)
            {
                prediction.SequentialPatterns[block] = nextBlockCounts;
            }
        }
    }

    #endregion

    #region T81.2 - Heatmap Generator

    /// <summary>
    /// Generates or updates the heat map for an object (T81.2).
    /// </summary>
    public BlockHeatmap GenerateHeatmap(string objectId)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_blockStats.TryGetValue(objectId, out var stats))
        {
            return new BlockHeatmap { ObjectId = objectId };
        }

        var heatValues = new double[blockMap.BlockCount];
        var recommendedTiers = new StorageTier[blockMap.BlockCount];
        var now = DateTime.UtcNow;

        // Calculate heat for each block
        for (int i = 0; i < blockMap.BlockCount; i++)
        {
            // Factors: access count, recency, access rate
            var accessCount = stats.BlockAccessCounts[i];
            var hoursSinceAccess = (now - stats.LastAccessTimes[i]).TotalHours;
            var accessRate = stats.AccessesLast24Hours[i];

            // Normalize and combine factors
            var countHeat = Math.Min(1.0, accessCount / 100.0);
            var recencyHeat = Math.Exp(-hoursSinceAccess / 168.0); // Decay over a week
            var rateHeat = Math.Min(1.0, accessRate / 20.0);

            // Weighted combination
            heatValues[i] = (countHeat * 0.3) + (recencyHeat * 0.4) + (rateHeat * 0.3);

            // Apply decay
            if (_heatmaps.TryGetValue(objectId, out var existingHeatmap) &&
                existingHeatmap.HeatValues.Length > i)
            {
                heatValues[i] = Math.Max(heatValues[i],
                    existingHeatmap.HeatValues[i] * _config.HeatDecayFactor);
            }

            // Determine recommended tier
            recommendedTiers[i] = heatValues[i] switch
            {
                >= 0.7 => StorageTier.Hot,
                >= 0.4 => StorageTier.Warm,
                >= 0.2 => StorageTier.Cold,
                >= 0.05 => StorageTier.Archive,
                _ => StorageTier.Glacier
            };
        }

        // Identify hot and cold regions
        var hotRegions = IdentifyRegions(heatValues, 0.6, true);
        var coldRegions = IdentifyRegions(heatValues, 0.2, false);

        var overallHeat = heatValues.Length > 0 ? heatValues.Average() : 0;

        var heatmap = new BlockHeatmap
        {
            ObjectId = objectId,
            HeatValues = heatValues,
            RecommendedTiers = recommendedTiers,
            HotRegions = hotRegions,
            ColdRegions = coldRegions,
            GeneratedAt = DateTime.UtcNow,
            OverallHeatScore = overallHeat
        };

        _heatmaps[objectId] = heatmap;
        return heatmap;
    }

    private void UpdateHeatmap(string objectId, int[] accessedBlocks)
    {
        if (!_heatmaps.TryGetValue(objectId, out var heatmap))
        {
            GenerateHeatmap(objectId);
            return;
        }

        lock (heatmap)
        {
            foreach (var block in accessedBlocks)
            {
                if (block >= 0 && block < heatmap.HeatValues.Length)
                {
                    // Increase heat for accessed block
                    heatmap.HeatValues[block] = Math.Min(1.0, heatmap.HeatValues[block] + 0.1);

                    // Update recommended tier
                    heatmap.RecommendedTiers[block] = heatmap.HeatValues[block] switch
                    {
                        >= 0.7 => StorageTier.Hot,
                        >= 0.4 => StorageTier.Warm,
                        >= 0.2 => StorageTier.Cold,
                        >= 0.05 => StorageTier.Archive,
                        _ => StorageTier.Glacier
                    };
                }
            }
            heatmap.OverallHeatScore = heatmap.HeatValues.Average();
        }
    }

    private static List<BlockRange> IdentifyRegions(double[] heatValues, double threshold, bool above)
    {
        var regions = new List<BlockRange>();
        int? regionStart = null;
        double regionHeatSum = 0;

        for (int i = 0; i < heatValues.Length; i++)
        {
            bool inRegion = above ? heatValues[i] >= threshold : heatValues[i] <= threshold;

            if (inRegion && regionStart == null)
            {
                regionStart = i;
                regionHeatSum = heatValues[i];
            }
            else if (inRegion && regionStart.HasValue)
            {
                regionHeatSum += heatValues[i];
            }
            else if (!inRegion && regionStart.HasValue)
            {
                regions.Add(new BlockRange
                {
                    StartBlock = regionStart.Value,
                    EndBlock = i - 1,
                    AverageHeat = regionHeatSum / (i - regionStart.Value)
                });
                regionStart = null;
                regionHeatSum = 0;
            }
        }

        // Handle region extending to end
        if (regionStart.HasValue)
        {
            regions.Add(new BlockRange
            {
                StartBlock = regionStart.Value,
                EndBlock = heatValues.Length - 1,
                AverageHeat = regionHeatSum / (heatValues.Length - regionStart.Value)
            });
        }

        return regions;
    }

    /// <summary>
    /// Gets the current heatmap for an object (T81.2).
    /// </summary>
    public BlockHeatmap? GetHeatmap(string objectId)
    {
        return _heatmaps.TryGetValue(objectId, out var heatmap) ? heatmap : null;
    }

    /// <summary>
    /// Queries blocks by heat range (T81.2).
    /// </summary>
    public IEnumerable<(int BlockIndex, double Heat)> QueryBlocksByHeat(
        string objectId, double minHeat, double maxHeat)
    {
        if (!_heatmaps.TryGetValue(objectId, out var heatmap))
        {
            yield break;
        }

        for (int i = 0; i < heatmap.HeatValues.Length; i++)
        {
            if (heatmap.HeatValues[i] >= minHeat && heatmap.HeatValues[i] <= maxHeat)
            {
                yield return (i, heatmap.HeatValues[i]);
            }
        }
    }

    #endregion

    #region T81.3 - Block Splitter

    /// <summary>
    /// Splits a file into independently movable blocks (T81.3).
    /// </summary>
    public async Task<BlockSplitResult> SplitIntoBlocksAsync(
        string objectId,
        Stream sourceStream,
        StorageTier initialTier = StorageTier.Hot,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(sourceStream);

        try
        {
            var totalSize = sourceStream.Length;

            if (totalSize < _config.MinObjectSizeForBlocking)
            {
                return new BlockSplitResult
                {
                    ObjectId = objectId,
                    Success = false,
                    ErrorMessage = $"Object too small ({FormatSize(totalSize)}) for block-level tiering"
                };
            }

            var blockCount = (int)Math.Ceiling((double)totalSize / _config.DefaultBlockSize);
            var blocks = new List<BlockInfo>();
            var blockHashes = new byte[blockCount][];

            using var sha256 = SHA256.Create();
            var buffer = new byte[_config.DefaultBlockSize];

            for (int i = 0; i < blockCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var offset = (long)i * _config.DefaultBlockSize;
                sourceStream.Position = offset;

                var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(_config.DefaultBlockSize, totalSize - offset)), ct);

                // Compute hash for deduplication and integrity
                blockHashes[i] = sha256.ComputeHash(buffer, 0, bytesRead);

                blocks.Add(new BlockInfo
                {
                    BlockIndex = i,
                    Offset = offset,
                    Size = bytesRead,
                    Tier = initialTier,
                    AccessCount = 0,
                    LastAccess = DateTime.UtcNow,
                    Location = $"{initialTier.ToString().ToLower()}/{objectId}/block_{i}",
                    HeatValue = 0,
                    ContentHash = blockHashes[i]
                });
            }

            // Initialize tracking structures
            InitializeBlockMap(objectId, totalSize, initialTier);

            // Update hashes
            if (_blockMaps.TryGetValue(objectId, out var blockMap))
            {
                for (int i = 0; i < blockCount; i++)
                {
                    blockMap.BlockHashes[i] = blockHashes[i];
                }
            }

            return new BlockSplitResult
            {
                ObjectId = objectId,
                BlockCount = blockCount,
                BlockSize = _config.DefaultBlockSize,
                Blocks = blocks,
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new BlockSplitResult
            {
                ObjectId = objectId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Splits content by content-defined chunking for better deduplication (T81.3).
    /// Uses Rabin fingerprinting for variable-size blocks.
    /// </summary>
    public async Task<BlockSplitResult> SplitWithContentDefinedChunkingAsync(
        string objectId,
        Stream sourceStream,
        StorageTier initialTier = StorageTier.Hot,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(sourceStream);

        try
        {
            var totalSize = sourceStream.Length;
            var blocks = new List<BlockInfo>();
            var minBlockSize = _config.DefaultBlockSize / 4;
            var maxBlockSize = _config.DefaultBlockSize * 2;
            var targetBlockSize = _config.DefaultBlockSize;

            // Rabin fingerprint parameters
            const ulong prime = 0xbfe6b8a5bf378d83UL;
            const ulong mask = (1UL << 20) - 1; // 1MB average

            using var sha256 = SHA256.Create();
            var buffer = new byte[maxBlockSize];
            long currentOffset = 0;
            int blockIndex = 0;

            while (currentOffset < totalSize)
            {
                ct.ThrowIfCancellationRequested();

                sourceStream.Position = currentOffset;
                var maxRead = (int)Math.Min(maxBlockSize, totalSize - currentOffset);
                var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, maxRead), ct);

                if (bytesRead == 0) break;

                // Find chunk boundary using rolling hash
                var chunkSize = FindChunkBoundary(buffer, bytesRead, minBlockSize,
                    targetBlockSize, prime, mask);

                var hash = sha256.ComputeHash(buffer, 0, chunkSize);

                blocks.Add(new BlockInfo
                {
                    BlockIndex = blockIndex,
                    Offset = currentOffset,
                    Size = chunkSize,
                    Tier = initialTier,
                    AccessCount = 0,
                    LastAccess = DateTime.UtcNow,
                    Location = $"{initialTier.ToString().ToLower()}/{objectId}/block_{blockIndex}",
                    HeatValue = 0,
                    ContentHash = hash
                });

                currentOffset += chunkSize;
                blockIndex++;
            }

            // Initialize with custom block info
            InitializeBlockMapWithVariableBlocks(objectId, totalSize, blocks, initialTier);

            return new BlockSplitResult
            {
                ObjectId = objectId,
                BlockCount = blocks.Count,
                BlockSize = _config.DefaultBlockSize, // Average target
                Blocks = blocks,
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new BlockSplitResult
            {
                ObjectId = objectId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static int FindChunkBoundary(byte[] buffer, int length, int minSize, int targetSize,
        ulong prime, ulong mask)
    {
        if (length <= minSize)
            return length;

        ulong hash = 0;

        for (int i = minSize; i < Math.Min(length, targetSize * 2); i++)
        {
            hash = (hash * prime + buffer[i]) & 0xFFFFFFFFFFFFFFFFUL;

            // Check if we hit a chunk boundary
            if ((hash & mask) == mask || i >= targetSize)
            {
                return i + 1;
            }
        }

        return Math.Min(length, targetSize);
    }

    private void InitializeBlockMapWithVariableBlocks(string objectId, long totalSize,
        List<BlockInfo> blocks, StorageTier initialTier)
    {
        var blockCount = blocks.Count;
        var blockTiers = blocks.Select(_ => initialTier).ToArray();
        var blockLocations = blocks.Select((b, i) =>
            $"{initialTier.ToString().ToLower()}/{objectId}/block_{i}").ToArray();
        var blockHashes = blocks.Select(b => b.ContentHash ?? Array.Empty<byte>()).ToArray();

        _blockMaps[objectId] = new ObjectBlockMap
        {
            ObjectId = objectId,
            TotalSize = totalSize,
            BlockSize = _config.DefaultBlockSize, // Nominal
            BlockCount = blockCount,
            BlockTiers = blockTiers,
            BlockLocations = blockLocations,
            BlockHashes = blockHashes,
            LastUpdated = DateTime.UtcNow
        };

        _blockStats[objectId] = new BlockAccessStats
        {
            ObjectId = objectId,
            BlockAccessCounts = new int[blockCount],
            LastAccessTimes = Enumerable.Repeat(DateTime.UtcNow, blockCount).ToArray(),
            AccessesLast24Hours = new int[blockCount],
            AccessesLast7Days = new int[blockCount],
            TotalBytesRead = new long[blockCount],
            AverageLatencyMs = new double[blockCount],
            AccessSequences = Enumerable.Range(0, blockCount).Select(_ => Array.Empty<int>()).ToArray(),
            LastUpdated = DateTime.UtcNow
        };

        _heatmaps[objectId] = new BlockHeatmap
        {
            ObjectId = objectId,
            HeatValues = new double[blockCount],
            RecommendedTiers = Enumerable.Repeat(initialTier, blockCount).ToArray(),
            GeneratedAt = DateTime.UtcNow
        };

        _prefetchPredictions[objectId] = new PrefetchPrediction
        {
            ObjectId = objectId,
            LastUpdated = DateTime.UtcNow
        };
    }

    #endregion

    #region T81.4 - Transparent Reassembly

    /// <summary>
    /// Reassembles blocks for a read operation (T81.4).
    /// Transparently fetches blocks from different tiers and handles prefetching.
    /// </summary>
    public async Task<BlockReassemblyResult> ReassembleBlocksAsync(
        string objectId,
        long offset,
        long length,
        Func<int, StorageTier, CancellationToken, Task<byte[]>> blockFetcher,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(blockFetcher);

        if (!_blockMaps.TryGetValue(objectId, out var blockMap))
        {
            return new BlockReassemblyResult
            {
                ObjectId = objectId,
                Success = false,
                ErrorMessage = "Object not initialized for block-level tiering"
            };
        }

        try
        {
            var startBlock = (int)(offset / blockMap.BlockSize);
            var endBlock = (int)((offset + length - 1) / blockMap.BlockSize);
            var tierFetchCounts = new Dictionary<StorageTier, int>();
            var totalLatency = 0.0;
            var prefetchedBlocks = new List<int>();

            // Initiate prefetch for predicted blocks
            var prefetchTasks = InitiatePrefetch(objectId, endBlock, blockFetcher, ct);

            // Fetch required blocks
            for (int i = startBlock; i <= Math.Min(endBlock, blockMap.BlockCount - 1); i++)
            {
                ct.ThrowIfCancellationRequested();

                var tier = blockMap.BlockTiers[i];
                tierFetchCounts.TryGetValue(tier, out var count);
                tierFetchCounts[tier] = count + 1;

                var fetchStart = DateTime.UtcNow;
                await blockFetcher(i, tier, ct);
                var fetchLatency = (DateTime.UtcNow - fetchStart).TotalMilliseconds;
                totalLatency += fetchLatency;

                // Record the access
                RecordBlockAccess(objectId, (long)i * blockMap.BlockSize, blockMap.BlockSize, fetchLatency);
            }

            // Wait for prefetch to complete (don't block on failure)
            try
            {
                await Task.WhenAll(prefetchTasks);
                prefetchedBlocks.AddRange(Enumerable.Range(endBlock + 1, prefetchTasks.Count));
            }
            catch
            {

                // Prefetch failures are non-critical
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            return new BlockReassemblyResult
            {
                ObjectId = objectId,
                TierFetchCounts = tierFetchCounts,
                TotalBytes = length,
                TotalLatencyMs = totalLatency,
                PrefetchedBlocks = prefetchedBlocks.ToArray(),
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new BlockReassemblyResult
            {
                ObjectId = objectId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Gets the storage locations for blocks needed to read a range (T81.4).
    /// </summary>
    public IEnumerable<(int BlockIndex, StorageTier Tier, string Location)> GetBlockLocations(
        string objectId, long offset, long length)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap))
        {
            yield break;
        }

        var startBlock = (int)(offset / blockMap.BlockSize);
        var endBlock = (int)((offset + length - 1) / blockMap.BlockSize);

        for (int i = startBlock; i <= Math.Min(endBlock, blockMap.BlockCount - 1); i++)
        {
            yield return (i, blockMap.BlockTiers[i], blockMap.BlockLocations[i]);
        }
    }

    #endregion

    #region T81.5 - Tier Migration Engine

    /// <summary>
    /// Moves a specific block to a target tier (T81.5).
    /// </summary>
    public Task<bool> MoveBlockAsync(string objectId, int blockIndex, StorageTier targetTier,
        CancellationToken ct = default)
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

    /// <summary>
    /// Moves multiple blocks to target tiers (T81.5).
    /// </summary>
    public async Task<Dictionary<int, bool>> MoveBlocksAsync(
        string objectId,
        IEnumerable<(int BlockIndex, StorageTier TargetTier)> moves,
        CancellationToken ct = default)
    {
        var results = new Dictionary<int, bool>();

        foreach (var (blockIndex, targetTier) in moves)
        {
            ct.ThrowIfCancellationRequested();
            results[blockIndex] = await MoveBlockAsync(objectId, blockIndex, targetTier, ct);
        }

        return results;
    }

    /// <summary>
    /// Applies block tier recommendations (T81.5).
    /// </summary>
    public async Task<int> ApplyRecommendationsAsync(string objectId, CancellationToken ct = default)
    {
        var recommendations = GetBlockRecommendations(objectId);
        var moveCount = 0;

        // Promote hot blocks
        foreach (var blockIndex in recommendations.BlocksToPromote)
        {
            ct.ThrowIfCancellationRequested();
            if (await MoveBlockAsync(objectId, blockIndex, StorageTier.Hot, ct))
            {
                moveCount++;
            }
        }

        // Demote cold blocks
        foreach (var blockIndex in recommendations.BlocksToDemote)
        {
            ct.ThrowIfCancellationRequested();
            var heatmap = GetHeatmap(objectId);
            var targetTier = heatmap?.RecommendedTiers[blockIndex] ?? StorageTier.Cold;
            if (await MoveBlockAsync(objectId, blockIndex, targetTier, ct))
            {
                moveCount++;
            }
        }

        return moveCount;
    }

    #endregion

    #region T81.6 - Predictive Prefetch

    /// <summary>
    /// Gets prefetch predictions for an object (T81.6).
    /// </summary>
    public int[] GetPrefetchPredictions(string objectId, int currentBlock)
    {
        if (!_prefetchPredictions.TryGetValue(objectId, out var prediction))
        {
            // Default: predict next N sequential blocks
            return Enumerable.Range(currentBlock + 1, _config.PrefetchLookahead).ToArray();
        }

        // Check for known patterns
        if (prediction.SequentialPatterns.TryGetValue(currentBlock, out var predictedBlocks))
        {
            return predictedBlocks;
        }

        // Fall back to sequential prediction
        if (!_blockMaps.TryGetValue(objectId, out var blockMap))
        {
            return Array.Empty<int>();
        }

        return Enumerable.Range(currentBlock + 1, _config.PrefetchLookahead)
            .Where(b => b < blockMap.BlockCount)
            .ToArray();
    }

    /// <summary>
    /// Pre-stages blocks to a faster tier based on predictions (T81.6).
    /// </summary>
    public async Task<int> PrestageBlocksAsync(
        string objectId,
        int[] blockIndexes,
        CancellationToken ct = default)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap))
        {
            return 0;
        }

        var stagedCount = 0;

        foreach (var blockIndex in blockIndexes)
        {
            ct.ThrowIfCancellationRequested();

            if (blockIndex < 0 || blockIndex >= blockMap.BlockCount)
                continue;

            var currentTier = blockMap.BlockTiers[blockIndex];

            // Only prestage if not already on hot/warm tier
            if (currentTier > StorageTier.Warm)
            {
                if (await MoveBlockAsync(objectId, blockIndex, StorageTier.Warm, ct))
                {
                    stagedCount++;
                }
            }
        }

        return stagedCount;
    }

    private List<Task> InitiatePrefetch(
        string objectId,
        int currentBlock,
        Func<int, StorageTier, CancellationToken, Task<byte[]>> blockFetcher,
        CancellationToken ct)
    {
        var tasks = new List<Task>();
        var predictedBlocks = GetPrefetchPredictions(objectId, currentBlock);

        if (!_blockMaps.TryGetValue(objectId, out var blockMap))
        {
            return tasks;
        }

        foreach (var blockIndex in predictedBlocks.Take(_config.PrefetchLookahead))
        {
            if (blockIndex >= 0 && blockIndex < blockMap.BlockCount)
            {
                var tier = blockMap.BlockTiers[blockIndex];
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await blockFetcher(blockIndex, tier, ct);
                    }
                    catch
                    {

                        // Prefetch failures are non-critical
                        System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                    }
                }, ct));
            }
        }

        return tasks;
    }

    #endregion

    #region T81.7 - Block Metadata Index

    /// <summary>
    /// Gets block information for an object (T81.7).
    /// </summary>
    public IReadOnlyList<BlockInfo> GetBlockInfo(string objectId)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_blockStats.TryGetValue(objectId, out var stats))
        {
            return Array.Empty<BlockInfo>();
        }

        var heatmap = _heatmaps.TryGetValue(objectId, out var h) ? h : null;
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
                Location = blockMap.BlockLocations[i],
                HeatValue = heatmap?.HeatValues[i] ?? 0,
                ContentHash = blockMap.BlockHashes[i]
            });
        }

        return blocks;
    }

    /// <summary>
    /// Gets blocks on a specific tier (T81.7).
    /// </summary>
    public IEnumerable<(string ObjectId, int BlockIndex)> GetBlocksOnTier(StorageTier tier)
    {
        foreach (var kvp in _blockMaps)
        {
            for (int i = 0; i < kvp.Value.BlockTiers.Length; i++)
            {
                if (kvp.Value.BlockTiers[i] == tier)
                {
                    yield return (kvp.Key, i);
                }
            }
        }
    }

    /// <summary>
    /// Gets tier distribution statistics (T81.7).
    /// </summary>
    public Dictionary<StorageTier, (long BlockCount, long TotalBytes)> GetTierDistribution()
    {
        var distribution = new Dictionary<StorageTier, (long BlockCount, long TotalBytes)>();

        foreach (StorageTier tier in Enum.GetValues<StorageTier>())
        {
            distribution[tier] = (0, 0);
        }

        foreach (var blockMap in _blockMaps.Values)
        {
            for (int i = 0; i < blockMap.BlockCount; i++)
            {
                var tier = blockMap.BlockTiers[i];
                var blockSize = (long)Math.Min(blockMap.BlockSize,
                    blockMap.TotalSize - (long)i * blockMap.BlockSize);

                var (count, bytes) = distribution[tier];
                distribution[tier] = (count + 1, bytes + blockSize);
            }
        }

        return distribution;
    }

    #endregion

    #region T81.8 - Cost Optimizer

    /// <summary>
    /// Analyzes cost vs performance tradeoffs for an object (T81.8).
    /// </summary>
    public CostOptimizationAnalysis AnalyzeCostOptimization(string objectId)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_heatmaps.TryGetValue(objectId, out var heatmap))
        {
            return new CostOptimizationAnalysis { ObjectId = objectId };
        }

        var recommendations = new List<BlockMoveRecommendation>();
        decimal currentCost = 0;
        decimal optimizedCost = 0;
        double currentPerformanceScore = 0;
        double optimizedPerformanceScore = 0;

        for (int i = 0; i < blockMap.BlockCount; i++)
        {
            var blockSizeGb = blockMap.BlockSize / (1024.0m * 1024.0m * 1024.0m);
            var currentTier = blockMap.BlockTiers[i];
            var recommendedTier = heatmap.RecommendedTiers[i];
            var heat = heatmap.HeatValues[i];

            // Calculate current cost
            currentCost += blockSizeGb * _config.TierCosts.GetValueOrDefault(currentTier, 0.023m);

            // Calculate optimized cost
            optimizedCost += blockSizeGb * _config.TierCosts.GetValueOrDefault(recommendedTier, 0.023m);

            // Calculate performance scores (based on heat vs tier match)
            var currentLatencyMultiplier = _config.TierLatencyMultipliers.GetValueOrDefault(currentTier, 1.0);
            var optimizedLatencyMultiplier = _config.TierLatencyMultipliers.GetValueOrDefault(recommendedTier, 1.0);

            // Higher heat + lower latency = better performance
            currentPerformanceScore += heat / currentLatencyMultiplier;
            optimizedPerformanceScore += heat / optimizedLatencyMultiplier;

            // Generate move recommendation if different
            if (currentTier != recommendedTier)
            {
                var costSavings = blockSizeGb *
                    (_config.TierCosts.GetValueOrDefault(currentTier, 0.023m) -
                     _config.TierCosts.GetValueOrDefault(recommendedTier, 0.023m));

                var latencyImpact = optimizedLatencyMultiplier - currentLatencyMultiplier;

                // Priority based on cost savings and heat alignment
                var priority = (double)costSavings * 100 +
                    Math.Abs(heat - GetHeatThresholdForTier(recommendedTier));

                recommendations.Add(new BlockMoveRecommendation
                {
                    BlockIndex = i,
                    CurrentTier = currentTier,
                    TargetTier = recommendedTier,
                    CostSavings = costSavings,
                    LatencyImpact = latencyImpact,
                    Priority = priority
                });
            }
        }

        // Normalize performance scores
        if (blockMap.BlockCount > 0)
        {
            currentPerformanceScore = (currentPerformanceScore / blockMap.BlockCount) * 100;
            optimizedPerformanceScore = (optimizedPerformanceScore / blockMap.BlockCount) * 100;
        }

        var performanceTradeoff = currentPerformanceScore > 0
            ? ((optimizedPerformanceScore - currentPerformanceScore) / currentPerformanceScore) * 100
            : 0;

        return new CostOptimizationAnalysis
        {
            ObjectId = objectId,
            CurrentMonthlyCost = currentCost,
            OptimizedMonthlyCost = optimizedCost,
            CurrentPerformanceScore = currentPerformanceScore,
            OptimizedPerformanceScore = optimizedPerformanceScore,
            PerformanceTradeoffPercent = performanceTradeoff,
            RecommendedMoves = recommendations.OrderByDescending(r => r.Priority).ToList()
        };
    }

    private static double GetHeatThresholdForTier(StorageTier tier) => tier switch
    {
        StorageTier.Hot => 0.85,
        StorageTier.Warm => 0.55,
        StorageTier.Cold => 0.30,
        StorageTier.Archive => 0.125,
        StorageTier.Glacier => 0.025,
        _ => 0.5
    };

    /// <summary>
    /// Gets the optimal tier placement given cost and performance constraints (T81.8).
    /// </summary>
    public Dictionary<int, StorageTier> GetOptimalPlacement(
        string objectId,
        decimal maxMonthlyCostDollars,
        double minPerformanceScore)
    {
        var analysis = AnalyzeCostOptimization(objectId);
        var placement = new Dictionary<int, StorageTier>();

        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_heatmaps.TryGetValue(objectId, out var heatmap))
        {
            return placement;
        }

        // Start with recommended placement
        for (int i = 0; i < blockMap.BlockCount; i++)
        {
            placement[i] = heatmap.RecommendedTiers[i];
        }

        // If within constraints, return
        if (analysis.OptimizedMonthlyCost <= maxMonthlyCostDollars &&
            analysis.OptimizedPerformanceScore >= minPerformanceScore)
        {
            return placement;
        }

        // Adjust for cost constraint
        if (analysis.OptimizedMonthlyCost > maxMonthlyCostDollars)
        {
            // Move lower-heat blocks to cheaper tiers
            var blocksByHeat = Enumerable.Range(0, blockMap.BlockCount)
                .OrderBy(i => heatmap.HeatValues[i])
                .ToList();

            foreach (var blockIndex in blocksByHeat)
            {
                var currentTier = placement[blockIndex];
                if (currentTier < StorageTier.Glacier)
                {
                    placement[blockIndex] = currentTier + 1; // Move to colder tier

                    // Recalculate cost
                    var newCost = CalculatePlacementCost(blockMap, placement);
                    if (newCost <= maxMonthlyCostDollars)
                        break;
                }
            }
        }

        // Adjust for performance constraint
        if (CalculatePlacementPerformance(blockMap, heatmap, placement) < minPerformanceScore)
        {
            // Move higher-heat blocks to faster tiers
            var blocksByHeat = Enumerable.Range(0, blockMap.BlockCount)
                .OrderByDescending(i => heatmap.HeatValues[i])
                .ToList();

            foreach (var blockIndex in blocksByHeat)
            {
                var currentTier = placement[blockIndex];
                if (currentTier > StorageTier.Hot)
                {
                    placement[blockIndex] = currentTier - 1; // Move to hotter tier

                    var newPerf = CalculatePlacementPerformance(blockMap, heatmap, placement);
                    if (newPerf >= minPerformanceScore)
                        break;
                }
            }
        }

        return placement;
    }

    private decimal CalculatePlacementCost(ObjectBlockMap blockMap, Dictionary<int, StorageTier> placement)
    {
        decimal cost = 0;
        var blockSizeGb = blockMap.BlockSize / (1024.0m * 1024.0m * 1024.0m);

        foreach (var kvp in placement)
        {
            cost += blockSizeGb * _config.TierCosts.GetValueOrDefault(kvp.Value, 0.023m);
        }

        return cost;
    }

    private double CalculatePlacementPerformance(ObjectBlockMap blockMap, BlockHeatmap heatmap,
        Dictionary<int, StorageTier> placement)
    {
        double score = 0;

        foreach (var kvp in placement)
        {
            var heat = heatmap.HeatValues[kvp.Key];
            var latencyMultiplier = _config.TierLatencyMultipliers.GetValueOrDefault(kvp.Value, 1.0);
            score += heat / latencyMultiplier;
        }

        return (score / placement.Count) * 100;
    }

    #endregion

    #region T81.9 - Database Optimization

    /// <summary>
    /// Detects if a file is a database file (T81.9).
    /// </summary>
    public static bool DetectDatabaseFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mdf" or ".ndf" or ".ldf" => true, // SQL Server
            ".db" or ".sqlite" or ".sqlite3" => true, // SQLite
            ".ibd" or ".frm" or ".myd" or ".myi" => true, // MySQL
            ".dbf" => true, // PostgreSQL / dBase
            ".ora" or ".dbf" => true, // Oracle
            ".ldb" or ".sst" => true, // LevelDB / RocksDB
            _ => false
        };
    }

    /// <summary>
    /// Detects the database type from file extension (T81.9).
    /// </summary>
    public static DatabaseFileType DetectDatabaseType(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return DatabaseFileType.Unknown;

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mdf" or ".ndf" or ".ldf" => DatabaseFileType.SqlServer,
            ".db" or ".sqlite" or ".sqlite3" => DatabaseFileType.Sqlite,
            ".ibd" or ".frm" => DatabaseFileType.MySql,
            ".dbf" => DatabaseFileType.PostgreSql,
            ".ora" => DatabaseFileType.Oracle,
            ".ldb" => DatabaseFileType.LevelDb,
            ".sst" => DatabaseFileType.RocksDb,
            _ => DatabaseFileType.Unknown
        };
    }

    private void AnalyzeDatabaseFile(string objectId, DatabaseFileType dbType, int blockCount)
    {
        // Database-specific block classification
        var headerBlocks = new List<int>();
        var indexBlocks = new List<int>();
        var dataBlocks = new List<int>();
        var freeSpaceBlocks = new List<int>();
        var pageSize = 8192; // Default

        switch (dbType)
        {
            case DatabaseFileType.SqlServer:
                // SQL Server: First few blocks are always header/system pages
                pageSize = 8192;
                headerBlocks.AddRange(Enumerable.Range(0, Math.Min(4, blockCount)));
                // Assume every 8th block might be index-heavy (simplified)
                indexBlocks.AddRange(Enumerable.Range(0, blockCount).Where(i => i % 8 == 1));
                dataBlocks.AddRange(Enumerable.Range(0, blockCount).Where(i => i % 8 > 1 && i >= 4));
                break;

            case DatabaseFileType.Sqlite:
                // SQLite: First block is header
                pageSize = 4096;
                headerBlocks.Add(0);
                // B-tree pages are typically near the beginning
                indexBlocks.AddRange(Enumerable.Range(1, Math.Min(blockCount / 10, blockCount - 1)));
                dataBlocks.AddRange(Enumerable.Range(blockCount / 10, blockCount - blockCount / 10));
                break;

            case DatabaseFileType.PostgreSql:
            case DatabaseFileType.MySql:
            case DatabaseFileType.Oracle:
                // Generic database pattern
                pageSize = 8192;
                headerBlocks.Add(0);
                indexBlocks.AddRange(Enumerable.Range(1, Math.Min(blockCount / 5, blockCount - 1)));
                dataBlocks.AddRange(Enumerable.Range(blockCount / 5, blockCount - blockCount / 5));
                break;

            case DatabaseFileType.LevelDb:
            case DatabaseFileType.RocksDb:
                // LSM-tree based - newer blocks are hotter
                pageSize = 4096;
                headerBlocks.Add(0);
                // Recent blocks are index-like (more frequently accessed)
                var recentCount = Math.Min(blockCount / 4, 20);
                indexBlocks.AddRange(Enumerable.Range(blockCount - recentCount, recentCount));
                dataBlocks.AddRange(Enumerable.Range(1, blockCount - recentCount - 1));
                break;
        }

        _databaseFiles[objectId] = new DatabaseFileInfo
        {
            ObjectId = objectId,
            Type = dbType,
            HeaderBlocks = headerBlocks.ToArray(),
            IndexBlocks = indexBlocks.ToArray(),
            DataBlocks = dataBlocks.ToArray(),
            FreeSpaceBlocks = freeSpaceBlocks.ToArray(),
            PageSize = pageSize,
            LastAnalyzed = DateTime.UtcNow
        };

        // Apply database-aware tier recommendations
        if (_heatmaps.TryGetValue(objectId, out var heatmap))
        {
            // Header blocks should always be hot
            foreach (var block in headerBlocks.Where(b => b < heatmap.RecommendedTiers.Length))
            {
                heatmap.RecommendedTiers[block] = StorageTier.Hot;
                heatmap.HeatValues[block] = 1.0;
            }

            // Index blocks should be at least warm
            foreach (var block in indexBlocks.Where(b => b < heatmap.RecommendedTiers.Length))
            {
                if (heatmap.RecommendedTiers[block] > StorageTier.Warm)
                {
                    heatmap.RecommendedTiers[block] = StorageTier.Warm;
                }
                heatmap.HeatValues[block] = Math.Max(heatmap.HeatValues[block], 0.5);
            }
        }
    }

    /// <summary>
    /// Gets database file analysis (T81.9).
    /// </summary>
    public DatabaseFileInfo? GetDatabaseFileInfo(string objectId)
    {
        return _databaseFiles.TryGetValue(objectId, out var info) ? info : null;
    }

    /// <summary>
    /// Gets tier recommendations with database-aware optimization (T81.9).
    /// </summary>
    public BlockTierRecommendation GetDatabaseAwareRecommendations(string objectId)
    {
        var baseRec = GetBlockRecommendations(objectId);

        if (!_databaseFiles.TryGetValue(objectId, out var dbInfo))
        {
            return baseRec;
        }

        var blocksToPromote = new List<int>(baseRec.BlocksToPromote);
        var blocksToDemote = new List<int>(baseRec.BlocksToDemote);

        // Ensure header blocks are never demoted
        blocksToDemote.RemoveAll(b => dbInfo.HeaderBlocks.Contains(b));
        blocksToPromote.AddRange(dbInfo.HeaderBlocks.Except(blocksToPromote));

        // Ensure index blocks are not demoted below warm
        if (_blockMaps.TryGetValue(objectId, out var blockMap))
        {
            foreach (var block in dbInfo.IndexBlocks)
            {
                if (block < blockMap.BlockCount && blockMap.BlockTiers[block] > StorageTier.Warm)
                {
                    if (!blocksToPromote.Contains(block))
                    {
                        blocksToPromote.Add(block);
                    }
                }
                blocksToDemote.Remove(block);
            }
        }

        return new BlockTierRecommendation
        {
            ObjectId = objectId,
            BlocksToPromote = blocksToPromote.Distinct().ToList(),
            BlocksToDemote = blocksToDemote.Distinct().ToList(),
            BlockDistribution = baseRec.BlockDistribution,
            RecommendWholeObjectMove = baseRec.RecommendWholeObjectMove,
            Reasoning = $"{baseRec.Reasoning} [Database-optimized: {dbInfo.Type}]",
            EstimatedMonthlySavings = baseRec.EstimatedMonthlySavings,
            PerformanceImpactScore = baseRec.PerformanceImpactScore
        };
    }

    #endregion

    #region T81.10 - Real-time Rebalancing

    private void CheckRebalanceNeeded(string objectId, int[] accessedBlocks)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_heatmaps.TryGetValue(objectId, out var heatmap))
        {
            return;
        }

        // Check for significant tier mismatches
        var mismatchedBlocks = new List<int>();

        foreach (var block in accessedBlocks)
        {
            if (block >= blockMap.BlockCount) continue;

            var currentTier = blockMap.BlockTiers[block];
            var recommendedTier = heatmap.RecommendedTiers[block];

            // Check if block is on wrong tier by more than 1 level
            if (Math.Abs((int)currentTier - (int)recommendedTier) > 1)
            {
                mismatchedBlocks.Add(block);
            }
        }

        if (mismatchedBlocks.Count > 0)
        {
            _rebalanceQueue.Enqueue(new RebalanceEvent
            {
                ObjectId = objectId,
                Reason = RebalanceReason.AccessPatternChange,
                AffectedBlocks = mismatchedBlocks.ToArray(),
                Priority = mismatchedBlocks.Count / (double)accessedBlocks.Length
            });
        }
    }

    private void RebalanceCallback(object? state)
    {
        if (_rebalanceInProgress)
            return;

        lock (_rebalanceLock)
        {
            if (_rebalanceInProgress)
                return;
            _rebalanceInProgress = true;
        }

        try
        {
            ExecuteRebalanceCycle();
        }
        finally
        {
            _rebalanceInProgress = false;
        }
    }

    private void ExecuteRebalanceCycle()
    {
        var eventsToProcess = new List<RebalanceEvent>();
        var blocksProcessed = 0;

        // Collect events up to max blocks limit
        while (_rebalanceQueue.TryDequeue(out var evt) &&
               blocksProcessed < _config.MaxBlocksPerRebalance)
        {
            eventsToProcess.Add(evt);
            blocksProcessed += evt.AffectedBlocks.Length;
        }

        // Group by object and process
        foreach (var group in eventsToProcess.GroupBy(e => e.ObjectId))
        {
            var objectId = group.Key;
            var allAffectedBlocks = group.SelectMany(e => e.AffectedBlocks).Distinct().ToArray();

            // Apply rebalancing
            _ = RebalanceBlocksAsync(objectId, allAffectedBlocks, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Forces a rebalance for specific blocks (T81.10).
    /// </summary>
    public async Task<int> RebalanceBlocksAsync(string objectId, int[] blockIndexes,
        CancellationToken ct = default)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap) ||
            !_heatmaps.TryGetValue(objectId, out var heatmap))
        {
            return 0;
        }

        var movedCount = 0;

        foreach (var block in blockIndexes)
        {
            ct.ThrowIfCancellationRequested();

            if (block < 0 || block >= blockMap.BlockCount)
                continue;

            var currentTier = blockMap.BlockTiers[block];
            var recommendedTier = heatmap.RecommendedTiers[block];

            if (currentTier != recommendedTier)
            {
                if (await MoveBlockAsync(objectId, block, recommendedTier, ct))
                {
                    movedCount++;
                }
            }
        }

        return movedCount;
    }

    /// <summary>
    /// Triggers a full rebalance for an object (T81.10).
    /// </summary>
    public async Task<int> FullRebalanceAsync(string objectId, CancellationToken ct = default)
    {
        if (!_blockMaps.TryGetValue(objectId, out var blockMap))
        {
            return 0;
        }

        // Regenerate heatmap first
        GenerateHeatmap(objectId);

        // Rebalance all blocks
        var allBlocks = Enumerable.Range(0, blockMap.BlockCount).ToArray();
        return await RebalanceBlocksAsync(objectId, allBlocks, ct);
    }

    /// <summary>
    /// Gets pending rebalance events (T81.10).
    /// </summary>
    public int GetPendingRebalanceCount() => _rebalanceQueue.Count;

    #endregion

    #region Core Strategy Implementation

    /// <summary>
    /// Gets block-level tier recommendations for an object.
    /// </summary>
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

        // Ensure heatmap is current
        var heatmap = GenerateHeatmap(objectId);

        var blocksToPromote = new List<int>();
        var blocksToDemote = new List<int>();
        var distribution = new Dictionary<StorageTier, int>();
        decimal totalSavings = 0;

        var now = DateTime.UtcNow;

        for (int i = 0; i < blockMap.BlockCount; i++)
        {
            var currentTier = blockMap.BlockTiers[i];
            var accessCount = stats.BlockAccessCounts[i];
            var lastAccess = stats.LastAccessTimes[i];
            var daysSinceAccess = (now - lastAccess).TotalDays;
            var recommendedTier = heatmap.RecommendedTiers[i];

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

                // Calculate savings
                var blockSizeGb = blockMap.BlockSize / (1024.0m * 1024.0m * 1024.0m);
                totalSavings += blockSizeGb *
                    (_config.TierCosts.GetValueOrDefault(currentTier, 0.023m) -
                     _config.TierCosts.GetValueOrDefault(recommendedTier, 0.004m));
            }
        }

        // Check if whole-object move is more efficient
        var hotBlockPercentage = distribution.GetValueOrDefault(StorageTier.Hot, 0) / (double)blockMap.BlockCount;
        var coldBlockPercentage = (distribution.GetValueOrDefault(StorageTier.Cold, 0) +
            distribution.GetValueOrDefault(StorageTier.Archive, 0) +
            distribution.GetValueOrDefault(StorageTier.Glacier, 0)) / (double)blockMap.BlockCount;

        var recommendWholeMove = hotBlockPercentage >= _config.HotBlockPercentageThreshold ||
            coldBlockPercentage >= _config.HotBlockPercentageThreshold;

        var reasoning = BuildBlockReasoning(blocksToPromote.Count, blocksToDemote.Count, distribution,
            recommendWholeMove, _databaseFiles.ContainsKey(objectId));

        // Calculate performance impact
        var perfImpact = CalculatePerformanceImpact(blockMap, heatmap, blocksToPromote, blocksToDemote);

        return new BlockTierRecommendation
        {
            ObjectId = objectId,
            BlocksToPromote = blocksToPromote,
            BlocksToDemote = blocksToDemote,
            BlockDistribution = distribution,
            RecommendWholeObjectMove = recommendWholeMove,
            Reasoning = reasoning,
            EstimatedMonthlySavings = totalSavings,
            PerformanceImpactScore = perfImpact
        };
    }

    private static double CalculatePerformanceImpact(ObjectBlockMap blockMap, BlockHeatmap heatmap,
        List<int> toPromote, List<int> toDemote)
    {
        double impact = 0;

        // Promotions improve performance (positive)
        foreach (var block in toPromote)
        {
            if (block < heatmap.HeatValues.Length)
            {
                impact += heatmap.HeatValues[block] * 0.1;
            }
        }

        // Demotions may decrease performance (negative if hot, neutral if cold)
        foreach (var block in toDemote)
        {
            if (block < heatmap.HeatValues.Length)
            {
                impact -= heatmap.HeatValues[block] * 0.05;
            }
        }

        // Normalize to -1.0 to 1.0 range
        return Math.Clamp(impact, -1.0, 1.0);
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Check if object is large enough for block-level tiering
        if (data.SizeBytes < _config.MinObjectSizeForBlocking)
        {
            return Task.FromResult(NoChange(data,
                $"Object too small ({FormatSize(data.SizeBytes)}) for block-level tiering. " +
                $"Minimum: {FormatSize(_config.MinObjectSizeForBlocking)}"));
        }

        // Initialize block map if not exists
        if (!_blockMaps.ContainsKey(data.ObjectId))
        {
            InitializeBlockMap(data.ObjectId, data.SizeBytes, data.CurrentTier);
        }

        // Get block recommendations (uses database-aware if applicable)
        var blockRecs = _databaseFiles.ContainsKey(data.ObjectId)
            ? GetDatabaseAwareRecommendations(data.ObjectId)
            : GetBlockRecommendations(data.ObjectId);

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

        // Block-level tiering active
        var blocksPromoting = blockRecs.BlocksToPromote.Count;
        var blocksDemoting = blockRecs.BlocksToDemote.Count;

        if (blocksPromoting > 0 || blocksDemoting > 0)
        {
            return Task.FromResult(NoChange(data,
                $"Block-level tiering: {blocksPromoting} blocks to promote, {blocksDemoting} blocks to demote. " +
                $"Est. savings: ${blockRecs.EstimatedMonthlySavings:F2}/mo. {blockRecs.Reasoning}"));
        }

        return Task.FromResult(NoChange(data,
            $"Block-level tiering active. Distribution: {FormatDistribution(blockRecs.BlockDistribution)}"));
    }

    private static string BuildBlockReasoning(int toPromote, int toDemote, Dictionary<StorageTier, int> distribution,
        bool wholeMove, bool isDatabase)
    {
        var parts = new List<string>();

        if (toPromote > 0)
            parts.Add($"{toPromote} hot blocks need promotion");

        if (toDemote > 0)
            parts.Add($"{toDemote} cold blocks need demotion");

        var distStr = FormatDistribution(distribution);
        parts.Add($"Distribution: {distStr}");

        if (wholeMove)
            parts.Add("Whole-object move recommended for efficiency");

        if (isDatabase)
            parts.Add("Database-aware optimization applied");

        return string.Join(". ", parts);
    }

    private static string FormatDistribution(Dictionary<StorageTier, int> distribution)
    {
        return string.Join(", ", distribution
            .Where(kvp => kvp.Value > 0)
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => $"{kvp.Key}:{kvp.Value}"));
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

    #endregion

    #region Disposal

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _rebalanceTimer?.Dispose();
        _blockMaps.Clear();
        _blockStats.Clear();
        _heatmaps.Clear();
        _prefetchPredictions.Clear();
        _databaseFiles.Clear();

        while (_rebalanceQueue.TryDequeue(out _)) { }

        return Task.CompletedTask;
    }

    #endregion
}
