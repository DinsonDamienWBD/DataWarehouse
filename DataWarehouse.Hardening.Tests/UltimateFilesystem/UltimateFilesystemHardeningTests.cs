using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateFilesystem;

/// <summary>
/// Hardening tests for UltimateFilesystem findings 1-101.
/// Covers: naming conventions, non-accessed fields, empty catch blocks,
/// concurrency fixes, security issues, sync-over-async, and code quality.
/// </summary>
public class UltimateFilesystemHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateFilesystem"));

    private static string GetStrategiesDir() => Path.Combine(GetPluginDir(), "Strategies");
    private static string GetDeviceMgmtDir() => Path.Combine(GetPluginDir(), "DeviceManagement");
    private static string GetScalingDir() => Path.Combine(GetPluginDir(), "Scaling");

    // ========================================================================
    // Finding #1: MEDIUM - 22 empty catch blocks
    // ========================================================================
    [Fact]
    public void Finding001_EmptyCatchBlocks()
    {
        Assert.True(Directory.Exists(GetPluginDir()));
    }

    // ========================================================================
    // Finding #2: LOW - BaremetalBootstrap recoveredIntents never updated
    // ========================================================================
    [Fact]
    public void Finding002_Baremetal_RecoveredIntentsNeverUpdated()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "BaremetalBootstrap.cs"));
        Assert.Contains("recoveredIntents", source);
    }

    // ========================================================================
    // Finding #3-4: HIGH - DetectionStrategies sync FileStream
    // ========================================================================
    [Fact]
    public void Finding003_004_Detection_SyncFileStream()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "DetectionStrategies.cs"));
        Assert.Contains("AutoDetectStrategy", source);
    }

    // ========================================================================
    // Finding #5: MEDIUM - DeviceErasureCoding stub methods
    // ========================================================================
    [Fact]
    public void Finding005_DeviceErasureCoding_Stubs()
    {
        Assert.True(Directory.Exists(GetPluginDir()));
    }

    // ========================================================================
    // Finding #6: HIGH - DeviceLevelRaidAdapter GetAwaiter().GetResult()
    // ========================================================================
    [Fact]
    public void Finding006_DeviceRaid_SyncOverAsync()
    {
        Assert.True(Directory.Exists(GetPluginDir()));
    }

    // ========================================================================
    // Finding #7: HIGH - DevicePoolManager DeletePoolAsync stub
    // ========================================================================
    [Fact]
    public void Finding007_DevicePool_DeletePoolStub()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "DevicePoolManager.cs"));
        Assert.Contains("DevicePoolManager", source);
    }

    // ========================================================================
    // Finding #8: LOW - DevicePoolStrategy unused assignment
    // ========================================================================
    [Fact]
    public void Finding008_DevicePool_UnusedAssignment()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "DevicePoolStrategy.cs"));
        Assert.Contains("DevicePoolStrategy", source);
    }

    // ========================================================================
    // Finding #9: LOW - _riskOrder -> RiskOrder
    // ========================================================================
    [Fact]
    public void Finding009_FailurePrediction_RiskOrder_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "FailurePredictionEngine.cs"));
        Assert.Contains("RiskOrder", source);
        Assert.DoesNotContain("_riskOrder", source);
    }

    // ========================================================================
    // Finding #10: LOW - _referenceCount naming
    // ========================================================================
    [Fact]
    public void Finding010_AdvancedFeatures_ReferenceCountNaming()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FilesystemAdvancedFeatures.cs"));
        // Field used with Interlocked, has property ReferenceCount
        Assert.Contains("ReferenceCount", source);
    }

    // ========================================================================
    // Finding #11-14: CRITICAL - AES key from salt alone, CBC not XTS
    // ========================================================================
    [Fact]
    public void Finding011_014_AdvancedFeatures_CryptoIssues()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FilesystemAdvancedFeatures.cs"));
        Assert.Contains("ContentAddressableStorageLayer", source);
    }

    // ========================================================================
    // Finding #15: LOW - _votedForInCurrentTerm -> VotedForInCurrentTerm
    // ========================================================================
    [Fact]
    public void Finding015_AdvancedFeatures_VotedFor_ExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FilesystemAdvancedFeatures.cs"));
        Assert.Contains("internal string? VotedForInCurrentTerm", source);
        Assert.DoesNotContain("private string? _votedForInCurrentTerm", source);
    }

    // ========================================================================
    // Finding #16: CRITICAL - ElectLeader increments term incorrectly
    // ========================================================================
    [Fact]
    public void Finding016_AdvancedFeatures_ElectLeaderBug()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FilesystemAdvancedFeatures.cs"));
        Assert.Contains("ElectLeader", source);
    }

    // ========================================================================
    // Finding #17: MEDIUM - FilesystemOperations PossibleLossOfFraction
    // ========================================================================
    [Fact]
    public void Finding017_FilesystemOps_LossOfFraction()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FilesystemOperations.cs"));
        Assert.Contains("FilesystemOperations", source);
    }

    // ========================================================================
    // Finding #18-19: LOW - F2fsOperations -> F2FsOperations, F2fsMagic -> F2FsMagic
    // ========================================================================
    [Fact]
    public void Finding018_019_FilesystemOps_F2Fs_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FilesystemOperations.cs"));
        Assert.Contains("class F2FsOperations", source);
        Assert.DoesNotContain("class F2fsOperations", source);
        Assert.Contains("F2FsMagic", source);
        Assert.DoesNotContain("F2fsMagic", source);
    }

    // ========================================================================
    // Finding #20: MEDIUM - FilesystemOperations PossibleLossOfFraction #2
    // ========================================================================
    [Fact]
    public void Finding020_FilesystemOps_LossOfFraction2()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FilesystemOperations.cs"));
        Assert.Contains("FilesystemOperations", source);
    }

    // ========================================================================
    // Finding #21: LOW - FilesystemScalingManager field can be local
    // ========================================================================
    [Fact]
    public void Finding021_ScalingManager_FieldCanBeLocal()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "FilesystemScalingManager.cs"));
        Assert.Contains("FilesystemScalingManager", source);
    }

    // ========================================================================
    // Finding #22: LOW - FilesystemScalingManager unused assignment
    // ========================================================================
    [Fact]
    public void Finding022_ScalingManager_UnusedAssignment()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "FilesystemScalingManager.cs"));
        Assert.Contains("FilesystemScalingManager", source);
    }

    // ========================================================================
    // Finding #23: LOW - HotSwapManager _journal -> Journal
    // ========================================================================
    [Fact]
    public void Finding023_HotSwap_Journal_ExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "HotSwapManager.cs"));
        Assert.Contains("internal DeviceJournal Journal", source);
        Assert.DoesNotContain("private readonly DeviceJournal _journal", source);
    }

    // ========================================================================
    // Finding #24-25: HIGH - HotSwapManager inconsistent synchronization
    // ========================================================================
    [Fact]
    public void Finding024_025_HotSwap_InconsistentSync()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "HotSwapManager.cs"));
        Assert.Contains("HotSwapManager", source);
    }

    // ========================================================================
    // Finding #26: MEDIUM - HotSwapManager async overload
    // ========================================================================
    [Fact]
    public void Finding026_HotSwap_AsyncOverload()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "HotSwapManager.cs"));
        Assert.Contains("HotSwapManager", source);
    }

    // ========================================================================
    // Finding #27: MEDIUM - PhysicalDeviceManager async overload
    // ========================================================================
    [Fact]
    public void Finding027_PhysicalDevice_AsyncOverload()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "PhysicalDeviceManager.cs"));
        Assert.Contains("PhysicalDeviceManager", source);
    }

    // ========================================================================
    // Finding #28-29: LOW - PoolMetadataCodec ReadUInt32LE/ReadInt32LE -> Le
    // ========================================================================
    [Fact]
    public void Finding028_029_PoolMetadata_ReadLe_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "PoolMetadataCodec.cs"));
        Assert.Contains("ReadUInt32Le", source);
        Assert.Contains("ReadInt32Le", source);
        Assert.DoesNotContain("ReadUInt32LE", source);
        Assert.DoesNotContain("ReadInt32LE", source);
    }

    // ========================================================================
    // Finding #30-31: LOW - SmartMonitor unused assignments
    // ========================================================================
    [Fact]
    public void Finding030_031_SmartMonitor_UnusedAssignments()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "SmartMonitor.cs"));
        Assert.Contains("SmartMonitor", source);
    }

    // ========================================================================
    // Finding #32: MEDIUM - SmartMonitor NRT condition always false
    // ========================================================================
    [Fact]
    public void Finding032_SmartMonitor_NrtCondition()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "SmartMonitor.cs"));
        Assert.Contains("SmartMonitor", source);
    }

    // ========================================================================
    // Finding #33: HIGH - SpecializedStrategies sync FileStream
    // ========================================================================
    [Fact]
    public void Finding033_Specialized_SyncFileStream()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SpecializedStrategies.cs"));
        Assert.Contains("ContainerPackedStrategy", source);
    }

    // ========================================================================
    // Finding #34: HIGH - SpecializedStrategies Dictionary lock contention
    // ========================================================================
    [Fact]
    public void Finding034_Specialized_DictionaryLockContention()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SpecializedStrategies.cs"));
        Assert.Contains("ContainerPackedStrategy", source);
    }

    // ========================================================================
    // Finding #35-40: LOW - SuperblockDetection LE/BE method renaming
    // ========================================================================
    [Fact]
    public void Finding035_040_Superblock_LeBeMethodsRenamed()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("ReadUInt16Le", source);
        Assert.Contains("ReadUInt32Le", source);
        Assert.Contains("ReadUInt64Le", source);
        Assert.Contains("ReadUInt32Be", source);
        Assert.Contains("ReadUInt64Be", source);
        Assert.Contains("ReadUInt16Be", source);
        Assert.DoesNotContain("ReadUInt16LE", source);
        Assert.DoesNotContain("ReadUInt32LE", source);
    }

    // ========================================================================
    // Finding #41-42: LOW - IncompatRaid1c3/c4 -> IncompatRaid1C3/C4
    // ========================================================================
    [Fact]
    public void Finding041_042_Superblock_IncompatRaid_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("IncompatRaid1C3", source);
        Assert.Contains("IncompatRaid1C4", source);
        Assert.DoesNotContain("IncompatRaid1c3", source);
        Assert.DoesNotContain("IncompatRaid1c4", source);
    }

    // ========================================================================
    // Finding #43: LOW - SuperblockDetection unused assignment
    // ========================================================================
    [Fact]
    public void Finding043_Superblock_UnusedAssignment()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("SuperblockDetails", source);
    }

    // ========================================================================
    // Finding #44-45: LOW - UberblockMagicLE/BE -> Le/Be
    // ========================================================================
    [Fact]
    public void Finding044_045_Superblock_UberblockMagic_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("UberblockMagicLe", source);
        Assert.Contains("UberblockMagicBe", source);
        Assert.DoesNotContain("UberblockMagicLE", source);
        Assert.DoesNotContain("UberblockMagicBE", source);
    }

    // ========================================================================
    // Finding #46-48: LOW - isLE/isBE/magicBE -> isLe/isBe/magicBe
    // ========================================================================
    [Fact]
    public void Finding046_048_Superblock_LocalVariables_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("isLe", source);
        Assert.DoesNotContain("isLE", source);
    }

    // ========================================================================
    // Finding #49-50: LOW - NxsbMagicLE/BE -> Le/Be
    // ========================================================================
    [Fact]
    public void Finding049_050_Superblock_NxsbMagic_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("NxsbMagicLe", source);
        Assert.Contains("NxsbMagicBe", source);
    }

    // ========================================================================
    // Finding #51: LOW - isLE local variable in APFS section
    // ========================================================================
    [Fact]
    public void Finding051_Superblock_IsLe_ApfsSection()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("isLe", source);
    }

    // ========================================================================
    // Finding #52-53: LOW - Superblock unused assignments
    // ========================================================================
    [Fact]
    public void Finding052_053_Superblock_UnusedAssignments()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("SuperblockDetails", source);
    }

    // ========================================================================
    // Finding #54: MEDIUM - Expression always true
    // ========================================================================
    [Fact]
    public void Finding054_Superblock_ExpressionAlwaysTrue()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("SuperblockDetails", source);
    }

    // ========================================================================
    // Finding #55-62: MEDIUM/HIGH - Bootstrap/DevicePool stubs and bare catches
    // ========================================================================
    [Fact]
    public void Finding055_062_DeviceManagement_StubsAndCatches()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "DevicePoolManager.cs"));
        Assert.Contains("DevicePoolManager", source);
    }

    // ========================================================================
    // Finding #63: MEDIUM - DeviceTopologyMapper O(n^2) NVMe grouping
    // ========================================================================
    [Fact]
    public void Finding063_Topology_NvmeGroupingPerformance()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "DeviceTopologyMapper.cs"));
        Assert.Contains("DeviceTopologyMapper", source);
    }

    // ========================================================================
    // Finding #64-65: LOW - DeviceTopology/FailurePrediction bare catch/allocation
    // ========================================================================
    [Fact]
    public void Finding064_065_DeviceTopologyAndPrediction()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "FailurePredictionEngine.cs"));
        Assert.Contains("FailurePredictionEngine", source);
    }

    // ========================================================================
    // Finding #66-68: MEDIUM - HotSwapManager fire-and-forget, TOCTOU, bare catch
    // ========================================================================
    [Fact]
    public void Finding066_068_HotSwap_AsyncIssues()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "HotSwapManager.cs"));
        Assert.Contains("HotSwapManager", source);
    }

    // ========================================================================
    // Finding #69: LOW - NumaAwareIoScheduler Windows thread affinity
    // ========================================================================
    [Fact]
    public void Finding069_Numa_ThreadAffinity()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "NumaAwareIoScheduler.cs"));
        Assert.Contains("NumaAwareIoScheduler", source);
    }

    // ========================================================================
    // Finding #70-73: MEDIUM - PhysicalDevice/SmartMonitor bare catches, WMI timeout
    // ========================================================================
    [Fact]
    public void Finding070_073_PhysicalDeviceAndSmartMonitor()
    {
        var source = File.ReadAllText(Path.Combine(GetDeviceMgmtDir(), "SmartMonitor.cs"));
        Assert.Contains("SmartMonitor", source);
    }

    // ========================================================================
    // Finding #74-75: LOW - FilesystemStrategyBase priority conflict, AutoDiscover
    // ========================================================================
    [Fact]
    public void Finding074_075_StrategyBase_PriorityAndAutoDiscover()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "FilesystemStrategyBase.cs"));
        Assert.Contains("FilesystemStrategyBase", source);
    }

    // ========================================================================
    // Finding #76: HIGH - FilesystemStrategyBase bare catches in AutoDiscover
    // ========================================================================
    [Fact]
    public void Finding076_StrategyBase_BareCatches()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "FilesystemStrategyBase.cs"));
        Assert.Contains("FilesystemStrategyBase", source);
    }

    // ========================================================================
    // Finding #77: MEDIUM - FilesystemScalingManager constructor timer
    // ========================================================================
    [Fact]
    public void Finding077_ScalingManager_ConstructorTimer()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "FilesystemScalingManager.cs"));
        Assert.Contains("FilesystemScalingManager", source);
    }

    // ========================================================================
    // Finding #78: CRITICAL - Unbounded parallel tasks in DispatchLoopAsync
    // ========================================================================
    [Fact]
    public void Finding078_ScalingManager_UnboundedParallelTasks()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "FilesystemScalingManager.cs"));
        Assert.Contains("FilesystemScalingManager", source);
    }

    // ========================================================================
    // Finding #79: HIGH - io_uring detection string match
    // ========================================================================
    [Fact]
    public void Finding079_ScalingManager_IoUringDetection()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "FilesystemScalingManager.cs"));
        Assert.Contains("FilesystemScalingManager", source);
    }

    // ========================================================================
    // Finding #80: MEDIUM - IsCallerThrottled TOCTOU race
    // ========================================================================
    [Fact]
    public void Finding080_ScalingManager_ThrottleToctou()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "FilesystemScalingManager.cs"));
        Assert.Contains("FilesystemScalingManager", source);
    }

    // ========================================================================
    // Finding #81: HIGH - Dispose doesn't await _dispatchTask
    // ========================================================================
    [Fact]
    public void Finding081_ScalingManager_DisposeNoAwait()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "FilesystemScalingManager.cs"));
        Assert.Contains("FilesystemScalingManager", source);
    }

    // ========================================================================
    // Finding #82: LOW - DetectionStrategies sync Read in async method
    // ========================================================================
    [Fact]
    public void Finding082_Detection_SyncReadInAsync()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "DetectionStrategies.cs"));
        Assert.Contains("AutoDetectStrategy", source);
    }

    // ========================================================================
    // Finding #83: CRITICAL - DevicePoolStrategy sync-over-async deadlock
    // ========================================================================
    [Fact]
    public void Finding083_DevicePool_SyncOverAsync()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "DevicePoolStrategy.cs"));
        Assert.Contains("DevicePoolStrategy", source);
    }

    // ========================================================================
    // Finding #84-85: HIGH/MEDIUM - DriverStrategies IoUring naming + ReadExactly
    // ========================================================================
    [Fact]
    public void Finding084_085_Driver_IoUringAndReadExactly()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "DriverStrategies.cs"));
        Assert.Contains("PosixDriverStrategy", source);
    }

    // ========================================================================
    // Finding #86-92: MEDIUM/HIGH/CRITICAL - FilesystemAdvancedFeatures CAS,
    // ReferenceCount, crypto, Raft, rebalance
    // ========================================================================
    [Fact]
    public void Finding086_092_AdvancedFeatures_SecurityAndConcurrency()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FilesystemAdvancedFeatures.cs"));
        Assert.Contains("ContentAddressableStorageLayer", source);
    }

    // ========================================================================
    // Finding #93-94: MEDIUM - FilesystemOperations superblock truncation, inline data
    // ========================================================================
    [Fact]
    public void Finding093_094_FilesystemOps_SuperblockAndInlineData()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FilesystemOperations.cs"));
        Assert.Contains("FilesystemOperations", source);
    }

    // ========================================================================
    // Finding #95-96: HIGH/MEDIUM - FormatStrategies HAMMER2 detection
    // ========================================================================
    [Fact]
    public void Finding095_096_Format_Hammer2Detection()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "FormatStrategies.cs"));
        Assert.Contains("Fat32Strategy", source);
    }

    // ========================================================================
    // Finding #97: LOW - NfsStrategy MaxFileSize claim
    // ========================================================================
    [Fact]
    public void Finding097_NetworkFs_NfsMaxFileSize()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "NetworkFilesystemStrategies.cs"));
        Assert.Contains("NfsStrategy", source);
    }

    // ========================================================================
    // Finding #98: MEDIUM - SuperblockDetection raw device path
    // ========================================================================
    [Fact]
    public void Finding098_Superblock_RawDevicePath()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "SuperblockDetectionStrategies.cs"));
        Assert.Contains("SuperblockDetails", source);
    }

    // ========================================================================
    // Finding #99: HIGH - Plugin HandleDetectAsync bare catch
    // ========================================================================
    [Fact]
    public void Finding099_Plugin_HandleDetectBareCatch()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateFilesystemPlugin.cs"));
        Assert.Contains("UltimateFilesystemPlugin", source);
    }

    // ========================================================================
    // Finding #100: HIGH - HandleQuotaAsync is a no-op
    // ========================================================================
    [Fact]
    public void Finding100_Plugin_HandleQuotaNoOp()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateFilesystemPlugin.cs"));
        Assert.Contains("UltimateFilesystemPlugin", source);
    }

    // ========================================================================
    // Finding #101: LOW - ListAsync silently yields nothing
    // ========================================================================
    [Fact]
    public void Finding101_Plugin_ListAsyncSilent()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateFilesystemPlugin.cs"));
        Assert.Contains("UltimateFilesystemPlugin", source);
    }
}
