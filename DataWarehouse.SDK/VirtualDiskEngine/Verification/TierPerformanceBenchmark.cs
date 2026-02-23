using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Regions;

namespace DataWarehouse.SDK.VirtualDiskEngine.Verification;

/// <summary>
/// Captures the performance benchmark result for a single feature across all three tiers.
/// Nanosecond-per-operation measurements enable direct comparison of tier performance.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Tier benchmark result record (TIER-05)")]
public sealed record BenchmarkResult
{
    /// <summary>The module this benchmark measures.</summary>
    public required ModuleId Module { get; init; }

    /// <summary>Human-readable feature name being benchmarked.</summary>
    public required string FeatureName { get; init; }

    /// <summary>Nanoseconds per operation at Tier 1 (VDE-integrated binary region access).</summary>
    public required long Tier1NanosPerOp { get; init; }

    /// <summary>Nanoseconds per operation at Tier 2 (plugin pipeline with serialization overhead).</summary>
    public required long Tier2NanosPerOp { get; init; }

    /// <summary>Nanoseconds per operation at Tier 3 (basic fallback, in-memory or no-op).</summary>
    public required long Tier3NanosPerOp { get; init; }

    /// <summary>
    /// Ratio of Tier 2 to Tier 1 nanoseconds. Values greater than 1.0 mean Tier 1 is faster.
    /// </summary>
    public required double Tier1vsTier2Ratio { get; init; }

    /// <summary>
    /// Ratio of Tier 3 to Tier 1 nanoseconds. Values greater than 1.0 mean Tier 1 is faster.
    /// Note: Tier 3 may be faster for pure reads (in-memory) but loses persistence/correctness.
    /// </summary>
    public required double Tier1vsTier3Ratio { get; init; }

    /// <summary>Human-readable analysis of the benchmark results.</summary>
    public required string Analysis { get; init; }
}

/// <summary>
/// Performance benchmarks comparing Tier 1, Tier 2, and Tier 3 for 5 representative VDE
/// module features. Uses <see cref="System.Diagnostics.Stopwatch"/> with warmup and measured
/// iterations to produce nanosecond-per-operation measurements.
/// </summary>
/// <remarks>
/// <para>Benchmarked features:</para>
/// <list type="number">
/// <item>Security (PolicyVault): policy serialize/deserialize round-trip</item>
/// <item>Integrity (IntegrityTree): Merkle hash verification</item>
/// <item>Compression (DictionaryRegion): dictionary entry add/lookup</item>
/// <item>Query (BTreeIndexForest): B-tree insert/search</item>
/// <item>Snapshot (SnapshotTable): snapshot entry create/lookup</item>
/// </list>
/// <para>
/// Tier 1 uses actual region Serialize/Deserialize. Tier 2 adds simulated plugin indirection
/// (extra dictionary lookup + byte[] allocation for serialization boundary). Tier 3 uses the
/// simplest possible operation (ConcurrentDictionary get, no serialization).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 80: Tier performance benchmarks (TIER-05)")]
public static class TierPerformanceBenchmark
{
    /// <summary>Number of warmup iterations to stabilize JIT and CPU caches.</summary>
    private const int WarmupIterations = 100;

    /// <summary>Number of measured iterations for timing.</summary>
    private const int MeasuredIterations = 1000;

    /// <summary>Block size used for region serialization benchmarks.</summary>
    private const int BenchmarkBlockSize = 4096;

    /// <summary>
    /// Runs all 5 feature benchmarks and returns nanosecond-per-operation measurements
    /// with tier comparison ratios and analysis.
    /// </summary>
    /// <returns>List of 5 benchmark results, one per representative feature.</returns>
    public static IReadOnlyList<BenchmarkResult> RunBenchmarks()
    {
        return new[]
        {
            BenchmarkSecurity(),
            BenchmarkIntegrity(),
            BenchmarkCompression(),
            BenchmarkQuery(),
            BenchmarkSnapshot(),
        };
    }

    // ========================================================================
    //  Feature 1: Security (PolicyVault)
    // ========================================================================

    private static BenchmarkResult BenchmarkSecurity()
    {
        // Setup
        var hmacKey = new byte[32];
        RandomNumberGenerator.Fill(hmacKey);
        var policyData = Encoding.UTF8.GetBytes("benchmark-policy-data-payload");
        var policyId = Guid.NewGuid();
        var policy = new PolicyDefinition(policyId, 0x0074, 1,
            DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks, policyData);

        // Tier 1: PolicyVaultRegion serialize + deserialize
        long tier1Nanos = MeasureNanosPerOp(() =>
        {
            var vault = new PolicyVaultRegion(hmacKey);
            vault.AddPolicy(policy);
            vault.Generation = 1;
            var buffer = new byte[BenchmarkBlockSize * PolicyVaultRegion.BlockCount];
            vault.Serialize(buffer, BenchmarkBlockSize);
            PolicyVaultRegion.Deserialize(buffer, BenchmarkBlockSize, hmacKey);
        });

        // Tier 2: Plugin-mediated with extra dictionary indirection + JSON overhead simulation
        var pluginDict = new ConcurrentDictionary<Guid, byte[]>();
        long tier2Nanos = MeasureNanosPerOp(() =>
        {
            // Simulate plugin serialization boundary: serialize to bytes, store in dictionary, retrieve
            var vault = new PolicyVaultRegion(hmacKey);
            vault.AddPolicy(policy);
            vault.Generation = 1;
            var buffer = new byte[BenchmarkBlockSize * PolicyVaultRegion.BlockCount];
            vault.Serialize(buffer, BenchmarkBlockSize);

            // Extra indirection: store/retrieve through dictionary (plugin lookup overhead)
            pluginDict[policyId] = buffer;
            var retrieved = pluginDict[policyId];
            PolicyVaultRegion.Deserialize(retrieved, BenchmarkBlockSize, hmacKey);
        });

        // Tier 3: Plain ConcurrentDictionary get (no serialization, no persistence)
        var memoryStore = new ConcurrentDictionary<Guid, PolicyDefinition>();
        memoryStore[policyId] = policy;
        long tier3Nanos = MeasureNanosPerOp(() =>
        {
            memoryStore.TryGetValue(policyId, out _);
        });

        return BuildResult(ModuleId.Security, "Security (PolicyVault)", tier1Nanos, tier2Nanos, tier3Nanos);
    }

    // ========================================================================
    //  Feature 2: Integrity (IntegrityTree)
    // ========================================================================

    private static BenchmarkResult BenchmarkIntegrity()
    {
        // Setup
        var blockData = new byte[64];
        RandomNumberGenerator.Fill(blockData);
        var leafHash = SHA256.HashData(blockData);

        // Tier 1: IntegrityTreeRegion serialize + deserialize + root hash verification
        long tier1Nanos = MeasureNanosPerOp(() =>
        {
            var region = new IntegrityTreeRegion(leafCount: 4);
            region.SetLeafHash(0, leafHash);
            region.Generation = 1;
            int blocks = region.RequiredBlocks(BenchmarkBlockSize);
            var buffer = new byte[BenchmarkBlockSize * blocks];
            region.Serialize(buffer, BenchmarkBlockSize);
            var restored = IntegrityTreeRegion.Deserialize(buffer, BenchmarkBlockSize, blocks);
            restored.GetRootHash();
        });

        // Tier 2: Plugin-computed hash tree with extra allocation + dictionary storage
        var hashStore = new ConcurrentDictionary<int, byte[]>();
        long tier2Nanos = MeasureNanosPerOp(() =>
        {
            var region = new IntegrityTreeRegion(leafCount: 4);
            region.SetLeafHash(0, leafHash);
            region.Generation = 1;
            int blocks = region.RequiredBlocks(BenchmarkBlockSize);
            var buffer = new byte[BenchmarkBlockSize * blocks];
            region.Serialize(buffer, BenchmarkBlockSize);

            // Extra indirection: store serialized bytes in plugin dictionary
            hashStore[0] = buffer;
            var retrieved = hashStore[0];
            var restored = IntegrityTreeRegion.Deserialize(retrieved, BenchmarkBlockSize, blocks);
            restored.GetRootHash();
        });

        // Tier 3: No verification (instant but unsafe — just compute hash, no tree)
        long tier3Nanos = MeasureNanosPerOp(() =>
        {
            SHA256.HashData(blockData);
        });

        return BuildResult(ModuleId.Integrity, "Integrity (MerkleTree)", tier1Nanos, tier2Nanos, tier3Nanos);
    }

    // ========================================================================
    //  Feature 3: Compression (DictionaryRegion)
    // ========================================================================

    private static BenchmarkResult BenchmarkCompression()
    {
        // Setup
        var contentHash = SHA256.HashData(Encoding.UTF8.GetBytes("benchmark-dictionary"));
        var entry = new CompressionDictEntry
        {
            DictId = 1,
            AlgorithmId = 0,
            BlockOffset = 200,
            BlockCount = 3,
            DictionarySizeBytes = 12000,
            TrainedUtcTicks = DateTime.UtcNow.Ticks,
            SampleCount = 1000,
            ContentHash = contentHash,
        };

        // Tier 1: CompressionDictionaryRegion round-trip
        long tier1Nanos = MeasureNanosPerOp(() =>
        {
            var region = new CompressionDictionaryRegion();
            region.RegisterDictionary(entry);
            region.Generation = 1;
            int blocks = region.RequiredBlocks(BenchmarkBlockSize);
            var buffer = new byte[BenchmarkBlockSize * blocks];
            region.Serialize(buffer, BenchmarkBlockSize);
            CompressionDictionaryRegion.Deserialize(buffer, BenchmarkBlockSize, blocks);
        });

        // Tier 2: Plugin-managed dictionary with extra lookup layer
        var pluginCache = new ConcurrentDictionary<byte, byte[]>();
        long tier2Nanos = MeasureNanosPerOp(() =>
        {
            var region = new CompressionDictionaryRegion();
            region.RegisterDictionary(entry);
            region.Generation = 1;
            int blocks = region.RequiredBlocks(BenchmarkBlockSize);
            var buffer = new byte[BenchmarkBlockSize * blocks];
            region.Serialize(buffer, BenchmarkBlockSize);

            // Plugin-layer indirection
            pluginCache[1] = buffer;
            var retrieved = pluginCache[1];
            CompressionDictionaryRegion.Deserialize(retrieved, BenchmarkBlockSize, blocks);
        });

        // Tier 3: No compression (raw copy — just dictionary lookup in ConcurrentDictionary)
        var rawStore = new ConcurrentDictionary<byte, CompressionDictEntry>();
        rawStore[1] = entry;
        long tier3Nanos = MeasureNanosPerOp(() =>
        {
            rawStore.TryGetValue(1, out _);
        });

        return BuildResult(ModuleId.Compression, "Compression (Dictionary)", tier1Nanos, tier2Nanos, tier3Nanos);
    }

    // ========================================================================
    //  Feature 4: Query (BTreeIndexForest via TagIndexRegion)
    // ========================================================================

    private static BenchmarkResult BenchmarkQuery()
    {
        // Setup
        var key = Encoding.UTF8.GetBytes("idx:benchmark-field");
        var value = Encoding.UTF8.GetBytes("asc");

        // Tier 1: TagIndexRegion insert + serialize + deserialize + lookup
        long tier1Nanos = MeasureNanosPerOp(() =>
        {
            var region = new TagIndexRegion(order: 8);
            region.Insert(new TagEntry(key, value, 1, 50));
            region.Generation = 1;
            int blocks = region.RequiredBlocks(BenchmarkBlockSize);
            var buffer = new byte[BenchmarkBlockSize * blocks];
            region.Serialize(buffer, BenchmarkBlockSize);
            var restored = TagIndexRegion.Deserialize(buffer, BenchmarkBlockSize, blocks);
            restored.Lookup(key);
        });

        // Tier 2: Plugin-managed B-tree with serialization overhead
        var pluginIndex = new ConcurrentDictionary<string, byte[]>();
        long tier2Nanos = MeasureNanosPerOp(() =>
        {
            var region = new TagIndexRegion(order: 8);
            region.Insert(new TagEntry(key, value, 1, 50));
            region.Generation = 1;
            int blocks = region.RequiredBlocks(BenchmarkBlockSize);
            var buffer = new byte[BenchmarkBlockSize * blocks];
            region.Serialize(buffer, BenchmarkBlockSize);

            // Plugin indirection
            pluginIndex["idx:benchmark-field"] = buffer;
            var retrieved = pluginIndex["idx:benchmark-field"];
            var restored = TagIndexRegion.Deserialize(retrieved, BenchmarkBlockSize, blocks);
            restored.Lookup(key);
        });

        // Tier 3: Linear scan (ConcurrentDictionary get — O(1) but no persistence)
        var linearStore = new ConcurrentDictionary<string, long>();
        linearStore["idx:benchmark-field"] = 1;
        long tier3Nanos = MeasureNanosPerOp(() =>
        {
            linearStore.TryGetValue("idx:benchmark-field", out _);
        });

        return BuildResult(ModuleId.Query, "Query (BTreeIndex)", tier1Nanos, tier2Nanos, tier3Nanos);
    }

    // ========================================================================
    //  Feature 5: Snapshot (SnapshotTable)
    // ========================================================================

    private static BenchmarkResult BenchmarkSnapshot()
    {
        // Setup
        var snapshotId = Guid.NewGuid();
        var entry = new SnapshotEntry
        {
            SnapshotId = snapshotId,
            ParentSnapshotId = Guid.Empty,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            InodeTableBlockOffset = 100,
            InodeTableBlockCount = 10,
            DataBlockCount = 5000,
            Flags = SnapshotEntry.FlagBaseSnapshot,
            Label = Encoding.UTF8.GetBytes("v1.0"),
        };

        // Tier 1: SnapshotTableRegion create + serialize + deserialize + lookup
        long tier1Nanos = MeasureNanosPerOp(() =>
        {
            var region = new SnapshotTableRegion();
            region.CreateSnapshot(entry);
            region.Generation = 1;
            int blocks = region.RequiredBlocks(BenchmarkBlockSize);
            var buffer = new byte[BenchmarkBlockSize * blocks];
            region.Serialize(buffer, BenchmarkBlockSize);
            var restored = SnapshotTableRegion.Deserialize(buffer, BenchmarkBlockSize, blocks);
            restored.GetSnapshot(snapshotId);
        });

        // Tier 2: Plugin snapshot management with extra serialization boundary
        var pluginSnapshots = new ConcurrentDictionary<Guid, byte[]>();
        long tier2Nanos = MeasureNanosPerOp(() =>
        {
            var region = new SnapshotTableRegion();
            region.CreateSnapshot(entry);
            region.Generation = 1;
            int blocks = region.RequiredBlocks(BenchmarkBlockSize);
            var buffer = new byte[BenchmarkBlockSize * blocks];
            region.Serialize(buffer, BenchmarkBlockSize);

            // Plugin indirection
            pluginSnapshots[snapshotId] = buffer;
            var retrieved = pluginSnapshots[snapshotId];
            var restored = SnapshotTableRegion.Deserialize(retrieved, BenchmarkBlockSize, blocks);
            restored.GetSnapshot(snapshotId);
        });

        // Tier 3: In-memory snapshot metadata (ConcurrentDictionary, no persistence)
        var memorySnapshots = new ConcurrentDictionary<Guid, SnapshotEntry>();
        memorySnapshots[snapshotId] = entry;
        long tier3Nanos = MeasureNanosPerOp(() =>
        {
            memorySnapshots.TryGetValue(snapshotId, out _);
        });

        return BuildResult(ModuleId.Snapshot, "Snapshot (SnapshotTable)", tier1Nanos, tier2Nanos, tier3Nanos);
    }

    // ========================================================================
    //  Measurement utilities
    // ========================================================================

    /// <summary>
    /// Measures nanoseconds per operation using warmup + measured iterations with Stopwatch.
    /// </summary>
    private static long MeasureNanosPerOp(Action operation)
    {
        // Warmup: stabilize JIT, CPU caches, branch prediction
        for (int i = 0; i < WarmupIterations; i++)
        {
            operation();
        }

        // Measured iterations
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < MeasuredIterations; i++)
        {
            operation();
        }
        sw.Stop();

        // Convert to nanoseconds per operation
        // Stopwatch.Frequency is ticks per second
        long elapsedTicks = sw.ElapsedTicks;
        double nanoseconds = (double)elapsedTicks / Stopwatch.Frequency * 1_000_000_000.0;
        return (long)(nanoseconds / MeasuredIterations);
    }

    /// <summary>
    /// Builds a <see cref="BenchmarkResult"/> from measured nanosecond values,
    /// computing ratios and analysis string.
    /// </summary>
    private static BenchmarkResult BuildResult(
        ModuleId module, string featureName,
        long tier1Nanos, long tier2Nanos, long tier3Nanos)
    {
        double tier1vs2 = tier1Nanos > 0 ? (double)tier2Nanos / tier1Nanos : 0.0;
        double tier1vs3 = tier1Nanos > 0 ? (double)tier3Nanos / tier1Nanos : 0.0;

        var analysis = new StringBuilder();
        analysis.Append($"Tier1={tier1Nanos}ns, Tier2={tier2Nanos}ns, Tier3={tier3Nanos}ns. ");

        if (tier1vs2 > 1.0)
            analysis.Append($"Tier 1 is {tier1vs2:F1}x faster than Tier 2. ");
        else
            analysis.Append($"Tier 2 is {1.0 / tier1vs2:F1}x faster than Tier 1. ");

        if (tier1vs3 > 1.0)
            analysis.Append($"Tier 1 is {tier1vs3:F1}x faster than Tier 3 (Tier 3 loses persistence). ");
        else
            analysis.Append($"Tier 3 is {1.0 / tier1vs3:F1}x faster than Tier 1 (Tier 3 trades persistence for speed). ");

        return new BenchmarkResult
        {
            Module = module,
            FeatureName = featureName,
            Tier1NanosPerOp = tier1Nanos,
            Tier2NanosPerOp = tier2Nanos,
            Tier3NanosPerOp = tier3Nanos,
            Tier1vsTier2Ratio = Math.Round(tier1vs2, 2),
            Tier1vsTier3Ratio = Math.Round(tier1vs3, 2),
            Analysis = analysis.ToString(),
        };
    }
}
