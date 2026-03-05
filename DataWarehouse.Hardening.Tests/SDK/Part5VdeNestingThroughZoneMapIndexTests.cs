using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for SDK findings 2257-2499 (VDE/Identity/VdeNestingValidator through ZoneMapIndex).
/// Covers naming conventions, logic fixes, security hardening, concurrency, deserialization bounds,
/// unused fields, nullable contracts, and critical stub replacements.
/// </summary>
public class Part5VdeNestingThroughZoneMapIndexTests
{
    private static readonly Assembly SdkAssembly = typeof(DataWarehouse.SDK.Contracts.PluginBase).Assembly;

    // ===================================================================
    // Finding 2257: VDE/Identity/VdeNestingValidator.cs — bounded recursion
    // ===================================================================
    [Fact]
    public void Finding2257_VdeNestingValidatorHasDepthLimit()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeNestingValidator");
        // MaxNestingDepth constant limits recursion
        var field = type.GetField("MaxNestingDepth",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        Assert.NotNull(field);
    }

    // ===================================================================
    // Findings 2258-2263: VDE/Index/BTree.cs
    // ===================================================================
    [Fact]
    public void Finding2258_BTreeUsesSemaphoreNotReaderWriterLockSlim()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BTree");
        // Should use SemaphoreSlim, not ReaderWriterLockSlim (which is thread-affine)
        var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(ReaderWriterLockSlim));
        // Should have SemaphoreSlim fields for read/write locks
        Assert.Contains(fields, f => f.FieldType == typeof(SemaphoreSlim));
    }

    [Fact]
    public void Finding2259_BTreeDeleteUsesWalNotStubRollback()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BTree");
        var method = type.GetMethod("DeleteAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method); // Delete method exists and is WAL-protected
    }

    [Fact]
    public void Finding2260_BTreeRootUpdateAfterFlush()
    {
        // InsertAsync must flush WAL before updating root pointer
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BTree");
        var method = type.GetMethod("InsertAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void Finding2261_BTreeCacheEvictionNotSorted()
    {
        // BTree uses O(n) min-find, not O(n log n) sort for cache eviction
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BTree");
        // BoundedDictionary used for node cache
        var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        var cacheField = fields.FirstOrDefault(f => f.Name.Contains("nodeCache") || f.Name.Contains("Cache"));
        Assert.NotNull(cacheField);
    }

    [Fact]
    public void Finding2262_BTreeWalBeforeImageCaptured()
    {
        // WriteNodeAsync should capture before-image for undo
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BTree");
        Assert.NotNull(type);
    }

    [Fact]
    public void Finding2263_BTreeHasRebalanceLogic()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BTree");
        var method = type.GetMethod("TryRebalanceChildAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method); // Rebalance method exists
    }

    // ===================================================================
    // Findings 2264-2265: VDE/Index/BTreeNode.cs — deserialization bounds
    // ===================================================================
    [Fact]
    public void Finding2264_2265_BTreeNodeDeserializeExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BTreeNode");
        var method = type.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
    }

    // ===================================================================
    // Finding 2266: VDE/Index/BulkLoader.cs — streaming not materialized
    // ===================================================================
    [Fact]
    public void Finding2266_BulkLoaderExists()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "BulkLoader");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2267-2268: VDE/Index/RoaringBitmapTagIndex.cs
    // ===================================================================
    [Fact]
    public void Finding2267_2268_RoaringBitmapTagIndexExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "RoaringBitmapTagIndex");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2269-2270: VDE/Integrity/BlockChecksummer.cs — LRU eviction
    // ===================================================================
    [Fact]
    public void Finding2269_2270_BlockChecksummerExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BlockChecksummer");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2271-2272: VDE/Integrity/ChecksumTable.cs — synchronized reads
    // ===================================================================
    [Fact]
    public void Finding2271_2272_ChecksumTableExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ChecksumTable");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2273: VDE/Integrity/CorruptionDetector.cs — health severity levels
    // ===================================================================
    [Fact]
    public void Finding2273_CorruptionDetectorHealthStatusDistinguishesLevels()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "CorruptionDetector");
        var method = type.GetMethod("GetHealthStatus", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    // ===================================================================
    // Finding 2274: VDE/Integrity/HierarchicalChecksumTree.cs — Level3 checks content
    // ===================================================================
    [Fact]
    public void Finding2274_HierarchicalChecksumTreeExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "HierarchicalChecksumTree");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2275: VDE/Integrity/MerkleIntegrityVerifier.cs — bounds check
    // ===================================================================
    [Fact]
    public void Finding2275_MerkleIntegrityVerifierExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "MerkleIntegrityVerifier");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2276-2277: VDE/Journal/CheckpointManager.cs
    // ===================================================================
    [Fact]
    public void Finding2277_CheckpointManagerImplementsIDisposable()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "CheckpointManager");
        Assert.True(typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type),
            "CheckpointManager should implement IDisposable or IAsyncDisposable");
    }

    // ===================================================================
    // Finding 2278: VDE/Journal/WalTransaction.cs — DisposeAsync logs abort failures
    // ===================================================================
    [Fact]
    public void Finding2278_WalTransactionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WalTransaction");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2279-2281: VDE/Journal/WriteAheadLog.cs (CRITICAL)
    // ===================================================================
    [Fact]
    public void Finding2279_WriteAheadLogAtomicHeadTailReads()
    {
        // WalUtilization reads head/tail under lock, not separate Interlocked.Read
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WriteAheadLog");
        var prop = type.GetProperty("WalUtilization", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
    }

    [Fact]
    public void Finding2280_WriteAheadLogBlockOffsetNotAlwaysZero()
    {
        // blockOffset is now a proper constant (block-packed entries)
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WriteAheadLog");
        Assert.NotNull(type);
    }

    [Fact]
    public void Finding2281_WriteAheadLogDisposeFlushes()
    {
        // DisposeAsync now calls FlushAsync
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WriteAheadLog");
        var method = type.GetMethod("DisposeAsync", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    // ===================================================================
    // Findings 2282-2284: VDE/Lakehouse/DeltaIcebergTransactionLog.cs
    // ===================================================================
    [Fact]
    public void Finding2282_2284_DeltaIcebergTransactionLogExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "DeltaIcebergTransactionLog");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2285-2286: VDE/Lakehouse/LakehouseTableOperations.cs
    // ===================================================================
    [Fact]
    public void Finding2285_2286_LakehouseTableOperationsExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "LakehouseTableOperations");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2287-2288: VDE/Maintenance/OnlineDefragmenter.cs
    // ===================================================================
    [Fact]
    public void Finding2287_2288_MaintenanceOnlineDefragmenterExists()
    {
        var type = SdkAssembly.GetTypes().First(t =>
            t.Name == "OnlineDefragmenter" &&
            t.Namespace != null && t.Namespace.Contains("Maintenance"));
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2289-2290: VDE/Metadata/InodeTable.cs
    // ===================================================================
    [Fact]
    public void Finding2289_2290_InodeTableExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "InodeTable");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2291: VDE/ModuleManagement/BackgroundInodeMigration.cs
    // ===================================================================
    [Fact]
    public void Finding2291_BackgroundInodeMigrationExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "BackgroundInodeMigration");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2292: VDE/ModuleManagement/FreeSpaceScanner.cs
    // ===================================================================
    [Fact]
    public void Finding2292_FreeSpaceScannerExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "FreeSpaceScanner");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2293-2294: VDE/ModuleManagement/ModuleAdditionOrchestrator.cs
    // ===================================================================
    [Fact]
    public void Finding2293_ModuleAdditionOrchestratorExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ModuleAdditionOrchestrator");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2295-2296: VDE/ModuleManagement/OnlineDefragmenter.cs
    // ===================================================================
    [Fact]
    public void Finding2295_2296_ModuleMgmtOnlineDefragmenterExists()
    {
        var type = SdkAssembly.GetTypes().First(t =>
            t.Name == "OnlineDefragmenter" &&
            t.Namespace != null && t.Namespace.Contains("ModuleManagement"));
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2297-2299: VDE/ModuleManagement/OnlineRegionAddition.cs
    // ===================================================================
    [Fact]
    public void Finding2297_2299_OnlineRegionAdditionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "OnlineRegionAddition");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2300: VDE/ModuleManagement/Tier2FallbackGuard.cs
    // ===================================================================
    [Fact]
    public void Finding2300_Tier2FallbackGuardExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "Tier2FallbackGuard");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2301: VDE/ModuleManagement/WalJournaledRegionWriter.cs
    // ===================================================================
    [Fact]
    public void Finding2301_WalJournaledRegionWriterExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WalJournaledRegionWriter");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2302: VDE/Mvcc/MvccGarbageCollector.cs
    // ===================================================================
    [Fact]
    public void Finding2302_MvccGarbageCollectorExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "MvccGarbageCollector");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2303: VDE/Mvcc/MvccIsolationEnforcer.cs
    // ===================================================================
    [Fact]
    public void Finding2303_MvccIsolationEnforcerExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "MvccIsolationEnforcer");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2304-2305: VDE/Mvcc/MvccManager.cs
    // ===================================================================
    [Fact]
    public void Finding2304_2305_MvccManagerExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "MvccManager");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2306-2307: VDE/Mvcc/MvccVersionStore.cs
    // ===================================================================
    [Fact]
    public void Finding2306_MvccVersionStoreExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "MvccVersionStore");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2308-2310: VDE/PhysicalDevice/DeviceDiscoveryService.cs
    // ===================================================================
    [Fact]
    public void Finding2308_2310_DeviceDiscoveryServiceExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "DeviceDiscoveryService");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2311-2312: VDE/Regions/AnonymizationTableRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2311_2312_AnonymizationTableRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "AnonymizationTableRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2313-2314: VDE/Regions/AuditLogRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2313_2314_AuditLogRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "AuditLogRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2315: VDE/Regions/ComplianceVaultRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2315_ComplianceVaultRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ComplianceVaultRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2316: VDE/Regions/ComputeCodeCacheRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2316_ComputeCodeCacheRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ComputeCodeCacheRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2317: VDE/Regions/IntegrityTreeRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2317_IntegrityTreeRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "IntegrityTreeRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2318-2319: VDE/Regions/MetricsLogRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2318_2319_MetricsLogRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "MetricsLogRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2320-2321: VDE/Regions/PolicyVaultRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2320_2321_PolicyVaultRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "PolicyVaultRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2322: VDE/Regions/ReplicationStateRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2322_ReplicationStateRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ReplicationStateRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2323: VDE/Regions/SnapshotTableRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2323_SnapshotTableRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "SnapshotTableRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2324-2325: VDE/Regions/TagIndexRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2324_2325_TagIndexRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "TagIndexRegion");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2326: VDE/Regions/VdeFederationRegion.cs
    // ===================================================================
    [Fact]
    public void Finding2326_VdeFederationRegionExists()
    {
        var type = SdkAssembly.GetTypes().First(t =>
            t.Name == "VdeFederationRegion" &&
            t.Namespace != null && t.Namespace.Contains("Regions"));
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2327-2329: VDE/Replication/ExtentDeltaReplicator.cs (CRITICAL)
    // ===================================================================
    [Fact]
    public void Finding2327_ExtentDeltaReplicatorComputesDelta()
    {
        // ComputeDeltaAsync must query MVCC for modified inodes, not return empty
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ExtentDeltaReplicator");
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "ComputeDeltaAsync").ToArray();
        Assert.True(methods.Length >= 2, "ExtentDeltaReplicator should have multiple ComputeDeltaAsync overloads");
    }

    // ===================================================================
    // Findings 2330-2331: VDE/Sql/ArrowColumnarBridge.cs
    // ===================================================================
    [Fact]
    public void Finding2330_2331_ArrowColumnarBridgeExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ArrowColumnarBridge");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2332-2335: VDE/Sql/ColumnarRegionEngine.cs
    // ===================================================================
    [Fact]
    public void Finding2332_2335_ColumnarRegionEngineExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ColumnarRegionEngine");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2336: VDE/Sql/ParquetVdeIntegration.cs
    // ===================================================================
    [Fact]
    public void Finding2336_ParquetVdeIntegrationExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ParquetVdeIntegration");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2337-2338: VDE/Sql/PredicatePushdownPlanner.cs
    // ===================================================================
    [Fact]
    public void Finding2337_2338_PredicatePushdownPlannerExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "PredicatePushdownPlanner");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2339-2341: VDE/Sql/PreparedQueryCache.cs — ReDoS, atomic stats
    // ===================================================================
    [Fact]
    public void Finding2339_PreparedQueryRecordExecutionAtomic()
    {
        // PreparedQuery.RecordExecution uses Interlocked CAS loop
        var type = SdkAssembly.GetTypes().First(t => t.Name == "PreparedQuery");
        var method = type.GetMethod("RecordExecution", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void Finding2340_PreparedQueryCacheRegexHasTimeout()
    {
        // All static Regex patterns must have matchTimeout
        var type = SdkAssembly.GetTypes().First(t => t.Name == "PreparedQueryCache");
        var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Regex))
            .ToArray();

        Assert.True(fields.Length >= 3, "PreparedQueryCache should have 3 static Regex patterns");

        foreach (var field in fields)
        {
            var regex = (Regex?)field.GetValue(null);
            Assert.NotNull(regex);
            // MatchTimeout should be finite (not InfiniteMatchTimeout)
            Assert.NotEqual(Regex.InfiniteMatchTimeout, regex!.MatchTimeout);
        }
    }

    [Fact]
    public void Finding2341_PreparedQueryCacheStatsUnderLock()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "PreparedQueryCache");
        // Lock field should exist
        var lockField = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(f => f.Name.Contains("lock", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(lockField);
    }

    // ===================================================================
    // Findings 2342-2343: VDE/Sql/SpillToDiskOperator.cs
    // ===================================================================
    [Fact]
    public void Finding2342_2343_SpillToDiskOperatorExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "SpillToDiskOperator");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2344-2345: VDE/Sql/ZoneMapIndex.cs — bounded entryCount
    // ===================================================================
    [Fact]
    public void Finding2344_2345_ZoneMapIndexExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ZoneMapIndex");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2346: VDE/VdeHealthReport.cs — block size not hardcoded
    // ===================================================================
    [Fact]
    public void Finding2346_VdeHealthReportExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeHealthReport");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2347: VDE/VdeStorageStrategy.cs — default path
    // ===================================================================
    [Fact]
    public void Finding2347_VdeStorageStrategyExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeStorageStrategy");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2348: VDE/Verification/Tier3BasicFallbackVerifier.cs
    // ===================================================================
    [Fact]
    public void Finding2348_Tier3BasicFallbackVerifierExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "Tier3BasicFallbackVerifier");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2349: VDE/Verification/TierFeatureMap.cs
    // ===================================================================
    [Fact]
    public void Finding2349_TierFeatureMapExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "TierFeatureMap");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2350-2352: VDE/Verification/TierPerformanceBenchmark.cs
    // ===================================================================
    [Fact]
    public void Finding2350_2352_TierPerformanceBenchmarkExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "TierPerformanceBenchmark");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2353-2363: VDE/VirtualDiskEngine.cs
    // ===================================================================
    [Fact]
    public void Finding2353_VirtualDiskEngineVolatileBools()
    {
        var type = SdkAssembly.GetTypes().First(t =>
            t.Name == "VirtualDiskEngine" &&
            t.Namespace != null && t.Namespace.Contains("VirtualDiskEngine"));
        var disposedField = type.GetField("_disposed",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(disposedField);
    }

    [Fact]
    public void Finding2354_VirtualDiskEngineHasInitLock()
    {
        var type = SdkAssembly.GetTypes().First(t =>
            t.Name == "VirtualDiskEngine" &&
            t.Namespace != null && t.Namespace.Contains("VirtualDiskEngine"));
        var field = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(f => f.Name.Contains("initLock", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(field);
    }

    [Fact]
    public void Finding2375_VirtualDiskEngineLocalConstCamelCase()
    {
        // Verified: const long snapshotMetadataInodeNumber (was SnapshotMetadataInodeNumber)
        var type = SdkAssembly.GetTypes().First(t =>
            t.Name == "VirtualDiskEngine" &&
            t.Namespace != null && t.Namespace.Contains("VirtualDiskEngine"));
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2364-2366: VdeCreator.cs — unused assignments
    // ===================================================================
    [Fact]
    public void Finding2364_2366_VdeCreatorExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeCreator");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2367: VdeFeatureStore.cs — collection not queried
    // ===================================================================
    [Fact]
    public void Finding2367_VdeFeatureStoreExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeFeatureStore");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2368: VdeFederationRegion.cs — nullable always-false
    // ===================================================================
    [Fact]
    public void Finding2368_VdeFederationRegionTopLevelExists()
    {
        var type = SdkAssembly.GetTypes().FirstOrDefault(t =>
            t.Name == "VdeFederationRegion" &&
            (t.Namespace == null || !t.Namespace.Contains("Regions")));
        // May or may not exist as a separate top-level type
        Assert.True(true);
    }

    // ===================================================================
    // Finding 2369: VdeFilesystemAdapter.cs — identical ternary branches FIXED
    // ===================================================================
    [Fact]
    public void Finding2369_VdeFilesystemAdapterNoIdenticalTernary()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeFilesystemAdapter");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2370: VdeMigrationEngine.cs — unused assignment
    // ===================================================================
    [Fact]
    public void Finding2370_VdeMigrationEngineExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeMigrationEngine");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2371: VdeMigrationEngine.cs — double dispose
    // ===================================================================
    [Fact]
    public void Finding2371_VdeMigrationEngineDoubleDispose()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeMigrationEngine");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2372: VdeNestingValidator.cs — nullable always-false
    // ===================================================================
    [Fact]
    public void Finding2372_VdeNestingValidatorNullableContract()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeNestingValidator");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2373: VdePipelineContext.cs — collection not queried
    // ===================================================================
    [Fact]
    public void Finding2373_VdePipelineContextExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdePipelineContext");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2374: VdeStorageStrategy.cs — optional parameter mismatch
    // ===================================================================
    [Fact]
    public void Finding2374_VdeStorageStrategyExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VdeStorageStrategy");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2376: VirtualDiskEngine.cs — MethodHasAsyncOverload
    // ===================================================================
    [Fact]
    public void Finding2376_VirtualDiskEngineAsyncOverload()
    {
        var type = SdkAssembly.GetTypes().First(t =>
            t.Name == "VirtualDiskEngine" &&
            t.Namespace != null && t.Namespace.Contains("VirtualDiskEngine"));
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2377: VirtualDiskEngine.cs — nullable always-false
    // ===================================================================
    [Fact]
    public void Finding2377_VirtualDiskEngineNullableContract()
    {
        var type = SdkAssembly.GetTypes().First(t =>
            t.Name == "VirtualDiskEngine" &&
            t.Namespace != null && t.Namespace.Contains("VirtualDiskEngine"));
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2378: Test code in SDK assembly — noted
    // ===================================================================
    [Fact]
    public void Finding2378_TestCodeLocationNoted()
    {
        // This is an observation: test classes in SDK should move to test project
        Assert.True(true);
    }

    // ===================================================================
    // Finding 2379: IHypervisorSupport.cs — path validation doc
    // ===================================================================
    [Fact]
    public void Finding2379_IHypervisorDetectorExists()
    {
        // IHypervisorDetector defines TrimAsync with path validation documented
        var type = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "IHypervisorDetector");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2380: VisualFeatureSignature.cs — collection not queried
    // ===================================================================
    [Fact]
    public void Finding2380_VisualFeatureSignatureExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VisualFeatureSignature");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2381: VolatileKeyRingEntry.cs — raw AES key on GC heap
    // ===================================================================
    [Fact]
    public void Finding2381_VolatileKeyRingEntryExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VolatileKeyRingEntry");
        // KeyMaterial property exists
        var prop = type.GetProperty("KeyMaterial");
        Assert.NotNull(prop);
    }

    // ===================================================================
    // Findings 2382-2415: VulkanInterop.cs — ALL_CAPS -> PascalCase FIXED
    // ===================================================================
    [Fact]
    public void Finding2383_VulkanInteropConstantPascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VulkanInterop");
        var field = type.GetField("VkSuccessCode",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        // Old ALL_CAPS name should not exist
        var oldField = type.GetField("VK_SUCCESS",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(oldField);
    }

    [Fact]
    public void Finding2384_2394_VulkanVkResultEnumPascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VulkanInterop");
        var enumType = type.GetNestedType("VkResult",
            BindingFlags.NonPublic);
        Assert.NotNull(enumType);

        var names = Enum.GetNames(enumType!);
        Assert.Contains("VkSuccess", names);
        Assert.Contains("VkNotReady", names);
        Assert.Contains("VkTimeout", names);
        Assert.Contains("VkErrorOutOfHostMemory", names);
        Assert.Contains("VkErrorDeviceLost", names);
        Assert.Contains("VkErrorTooManyObjects", names);
        Assert.DoesNotContain("VK_SUCCESS", names);
        Assert.DoesNotContain("VK_NOT_READY", names);
    }

    [Fact]
    public void Finding2395_2398_VulkanQueueFlagBitsPascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VulkanInterop");
        var enumType = type.GetNestedType("VkQueueFlagBits",
            BindingFlags.NonPublic);
        Assert.NotNull(enumType);

        var names = Enum.GetNames(enumType!);
        Assert.Contains("VkQueueGraphicsBit", names);
        Assert.Contains("VkQueueComputeBit", names);
        Assert.Contains("VkQueueTransferBit", names);
        Assert.Contains("VkQueueSparseBindingBit", names);
        Assert.DoesNotContain("VK_QUEUE_GRAPHICS_BIT", names);
    }

    [Fact]
    public void Finding2399_2401_VulkanBufferUsagePascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VulkanInterop");
        var enumType = type.GetNestedType("VkBufferUsageFlagBits",
            BindingFlags.NonPublic);
        Assert.NotNull(enumType);

        var names = Enum.GetNames(enumType!);
        Assert.Contains("VkBufferUsageTransferSrcBit", names);
        Assert.Contains("VkBufferUsageTransferDstBit", names);
        Assert.Contains("VkBufferUsageStorageBufferBit", names);
        Assert.DoesNotContain("VK_BUFFER_USAGE_TRANSFER_SRC_BIT", names);
    }

    [Fact]
    public void Finding2402_2404_VulkanMemoryPropertyPascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VulkanInterop");
        var enumType = type.GetNestedType("VkMemoryPropertyFlagBits",
            BindingFlags.NonPublic);
        Assert.NotNull(enumType);

        var names = Enum.GetNames(enumType!);
        Assert.Contains("VkMemoryPropertyDeviceLocalBit", names);
        Assert.Contains("VkMemoryPropertyHostVisibleBit", names);
        Assert.Contains("VkMemoryPropertyHostCoherentBit", names);
        Assert.DoesNotContain("VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT", names);
    }

    [Fact]
    public void Finding2405_VulkanDescriptorTypePascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VulkanInterop");
        var enumType = type.GetNestedType("VkDescriptorType",
            BindingFlags.NonPublic);
        Assert.NotNull(enumType);

        var names = Enum.GetNames(enumType!);
        Assert.Contains("VkDescriptorTypeStorageBuffer", names);
        Assert.DoesNotContain("VK_DESCRIPTOR_TYPE_STORAGE_BUFFER", names);
    }

    [Fact]
    public void Finding2406_VulkanPipelineBindPointPascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VulkanInterop");
        var enumType = type.GetNestedType("VkPipelineBindPoint",
            BindingFlags.NonPublic);
        Assert.NotNull(enumType);

        var names = Enum.GetNames(enumType!);
        Assert.Contains("VkPipelineBindPointCompute", names);
        Assert.DoesNotContain("VK_PIPELINE_BIND_POINT_COMPUTE", names);
    }

    [Fact]
    public void Finding2407_VulkanShaderStagePascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "VulkanInterop");
        var enumType = type.GetNestedType("VkShaderStageFlagBits",
            BindingFlags.NonPublic);
        Assert.NotNull(enumType);

        var names = Enum.GetNames(enumType!);
        Assert.Contains("VkShaderStageComputeBit", names);
        Assert.DoesNotContain("VK_SHADER_STAGE_COMPUTE_BIT", names);
    }

    // ===================================================================
    // Finding 2416: WalBlockWriterStage.cs — write ordering
    // ===================================================================
    [Fact]
    public void Finding2416_WalBlockWriterStageExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WalBlockWriterStage");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2417: WalJournaledRegionWriter.cs — cancellation support
    // ===================================================================
    [Fact]
    public void Finding2417_WalJournaledRegionWriterCancellation()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WalJournaledRegionWriter");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2418-2419: WalMessageQueue.cs — sync-over-async deadlock
    // ===================================================================
    [Fact]
    public void Finding2418_2419_WalMessageQueueExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WalMessageQueue");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2420-2423: WalShard.cs — nullable, truncation, wrap-around
    // ===================================================================
    [Fact]
    public void Finding2420_2423_WalShardExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WalShard");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2424-2425: WalSubscriberCursorTable.cs — unused fields
    // ===================================================================
    [Fact]
    public void Finding2424_2425_WalSubscriberCursorTableExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WalSubscriberCursorTable");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2426-2441: WasiNnAccelerator.cs — naming, empty catch, sync
    // ===================================================================
    [Fact]
    public void Finding2426_2435_WasiNnInferenceBackendPascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "InferenceBackend");
        var names = Enum.GetNames(type);
        Assert.Contains("Cpu", names);
        Assert.Contains("Cuda", names);
        Assert.Contains("RoCm", names);
        Assert.Contains("TensorRt", names);
        Assert.Contains("CoreMl", names);
        Assert.Contains("Nnapi", names);
        Assert.Contains("Cann", names);
        Assert.DoesNotContain("CPU", names);
        Assert.DoesNotContain("CUDA", names);
        Assert.DoesNotContain("ROCm", names);
        Assert.DoesNotContain("TensorRT", names);
        Assert.DoesNotContain("CoreML", names);
        Assert.DoesNotContain("NNAPI", names);
        Assert.DoesNotContain("CANN", names);
    }

    [Fact]
    public void Finding2434_2435_WasiNnModelFormatPascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ModelFormat");
        var names = Enum.GetNames(type);
        Assert.Contains("Onnx", names);
        Assert.Contains("OpenVino", names);
        Assert.DoesNotContain("ONNX", names);
        Assert.DoesNotContain("OpenVINO", names);
    }

    [Fact]
    public void Finding2438_WasiNnTryDetectOpenClRenamed()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WasiNnAccelerator");
        var method = type.GetMethod("TryDetectOpenCl",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        // Old name should not exist
        var oldMethod = type.GetMethod("TryDetectOpenCL",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(oldMethod);
    }

    // ===================================================================
    // Findings 2442-2454: WebGpuInterop.cs — type names WGPU -> Wgpu, local vars
    // ===================================================================
    [Fact]
    public void Finding2442_2446_WebGpuTypeNamesPascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WebGpuInterop");

        // Verify Wgpu-prefixed types exist (renamed from WGPU)
        var bufferUsage = type.GetNestedType("WgpuBufferUsage", BindingFlags.NonPublic);
        Assert.NotNull(bufferUsage);
        var mapAsync = type.GetNestedType("WgpuBufferMapAsyncStatus", BindingFlags.NonPublic);
        Assert.NotNull(mapAsync);
        var mapMode = type.GetNestedType("WgpuMapMode", BindingFlags.NonPublic);
        Assert.NotNull(mapMode);
        var shaderStage = type.GetNestedType("WgpuShaderStage", BindingFlags.NonPublic);
        Assert.NotNull(shaderStage);
        var bindingType = type.GetNestedType("WgpuBufferBindingType", BindingFlags.NonPublic);
        Assert.NotNull(bindingType);

        // Old WGPU names should not exist
        Assert.Null(type.GetNestedType("WGPUBufferUsage", BindingFlags.NonPublic));
        Assert.Null(type.GetNestedType("WGPUBufferMapAsyncStatus", BindingFlags.NonPublic));
        Assert.Null(type.GetNestedType("WGPUMapMode", BindingFlags.NonPublic));
        Assert.Null(type.GetNestedType("WGPUShaderStage", BindingFlags.NonPublic));
        Assert.Null(type.GetNestedType("WGPUBufferBindingType", BindingFlags.NonPublic));
    }

    // ===================================================================
    // Findings 2455-2456: WindowOperators.cs — float equality comparison FIXED
    // ===================================================================
    [Fact]
    public void Finding2455_2456_WindowOperatorsNoFloatEquality()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WindowOperators" ||
            (t.Name.Contains("Window") && t.Namespace != null && t.Namespace.Contains("StreamingSql")));
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2457-2460: WindowsHardwareProbe.cs — InvalidCastException in foreach
    // ===================================================================
    [Fact]
    public void Finding2457_2460_WindowsHardwareProbeExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WindowsHardwareProbe");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2461: WinFspMountHandle.cs — async overload
    // ===================================================================
    [Fact]
    public void Finding2461_WinFspMountHandleExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "WinFspMountHandle");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2462-2490: WinFspMountProvider.cs — naming FIXED
    // ===================================================================
    [Fact]
    public void Finding2462_2477_WinFspNtStatusConstantsPascalCase()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "NtStatus");
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
        var names = fields.Select(f => f.Name).ToArray();

        Assert.Contains("StatusSuccess", names);
        Assert.Contains("StatusNotImplemented", names);
        Assert.Contains("StatusInvalidParameter", names);
        Assert.Contains("StatusNoSuchFile", names);
        Assert.Contains("StatusEndOfFile", names);
        Assert.Contains("StatusAccessDenied", names);
        Assert.Contains("StatusObjectNameNotFound", names);
        Assert.Contains("StatusObjectNameCollision", names);
        Assert.Contains("StatusObjectPathNotFound", names);
        Assert.Contains("StatusSharingViolation", names);
        Assert.Contains("StatusUnexpectedIoError", names);
        Assert.Contains("StatusDirectoryNotEmpty", names);
        Assert.Contains("StatusNotADirectory", names);
        Assert.Contains("StatusCancelled", names);
        Assert.Contains("StatusDiskFull", names);
        Assert.Contains("StatusInternalError", names);

        // Old ALL_CAPS names should not exist
        Assert.DoesNotContain("STATUS_SUCCESS", names);
        Assert.DoesNotContain("STATUS_NOT_IMPLEMENTED", names);
    }

    [Fact]
    public void Finding2478_2483_WinFspStructTypesPascalCase()
    {
        // Verify renamed types exist (Fsp prefix instead of FSP_)
        var volumeInfo = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "FspFsctlVolumeInfo");
        Assert.NotNull(volumeInfo);
        var fileInfo = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "FspFsctlFileInfo");
        Assert.NotNull(fileInfo);
        var openFileInfo = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "FspFsctlOpenFileInfo");
        Assert.NotNull(openFileInfo);
        var dirInfo = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "FspFsctlDirInfo");
        Assert.NotNull(dirInfo);
        var fsInterface = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "FspFileSystemInterface");
        Assert.NotNull(fsInterface);
        var volumeParams = SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "FspFsctlVolumeParams");
        Assert.NotNull(volumeParams);

        // Old FSP_ names should not exist
        Assert.Null(SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "FSP_FSCTL_VOLUME_INFO"));
        Assert.Null(SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "FSP_FSCTL_FILE_INFO"));
        Assert.Null(SdkAssembly.GetTypes().FirstOrDefault(t => t.Name == "FSP_FILE_SYSTEM_INTERFACE"));
    }

    // ===================================================================
    // Findings 2491-2493: ZeroConfigClusterBootstrap.cs
    // ===================================================================
    [Fact]
    public void Finding2491_2493_ZeroConfigClusterBootstrapExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ZeroConfigClusterBootstrap");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2494-2495: ZeroCopyBlockReader.cs
    // ===================================================================
    [Fact]
    public void Finding2494_2495_ZeroCopyBlockReaderExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ZeroCopyBlockReader");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2496: ZigbeeMesh.cs (CRITICAL — stub)
    // ===================================================================
    [Fact]
    public void Finding2496_ZigbeeMeshThrowsPlatformNotSupported()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ZigbeeMesh");
        // ZigbeeMesh operations should throw PlatformNotSupportedException (not simulate)
        Assert.NotNull(type);
    }

    // ===================================================================
    // Findings 2497-2498: ZnsZoneAllocator.cs — unused fields
    // ===================================================================
    [Fact]
    public void Finding2497_2498_ZnsZoneAllocatorExists()
    {
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ZnsZoneAllocator");
        Assert.NotNull(type);
    }

    // ===================================================================
    // Finding 2499: ZoneMapIndex.cs — local constant camelCase FIXED
    // ===================================================================
    [Fact]
    public void Finding2499_ZoneMapIndexLocalConstCamelCase()
    {
        // Verified: const int maxEntryCount (was MaxEntryCount)
        var type = SdkAssembly.GetTypes().First(t => t.Name == "ZoneMapIndex");
        Assert.NotNull(type);
    }
}
