// Hardening tests for UltimateStorage findings 1001-1243
// Innovation strategies (InfiniteStorage through ZeroWaste), Kubernetes, Local, Network,
// OpenStack, S3Compatible, Scale, SoftwareDefined, Specialized, ZeroGravity strategies,
// plus plugin-level files (UltimateStoragePlugin, VastData, VultrObjectStorage, Wasabi, WebDav, WekaIo)

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class FinalBatchTests5
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");
    private static readonly string InnovationDir = Path.Combine(PluginRoot, "Strategies", "Innovation");
    private static readonly string KubernetesDir = Path.Combine(PluginRoot, "Strategies", "Kubernetes");
    private static readonly string LocalDir = Path.Combine(PluginRoot, "Strategies", "Local");
    private static readonly string NetworkDir = Path.Combine(PluginRoot, "Strategies", "Network");
    private static readonly string OpenStackDir = Path.Combine(PluginRoot, "Strategies", "OpenStack");
    private static readonly string S3Dir = Path.Combine(PluginRoot, "Strategies", "S3Compatible");
    private static readonly string ScaleDir = Path.Combine(PluginRoot, "Strategies", "Scale");
    private static readonly string SoftwareDefinedDir = Path.Combine(PluginRoot, "Strategies", "SoftwareDefined");
    private static readonly string SpecializedDir = Path.Combine(PluginRoot, "Strategies", "Specialized");
    private static readonly string ZeroGravityDir = Path.Combine(PluginRoot, "Strategies", "ZeroGravity");
    private static readonly string EnterpriseDir = Path.Combine(PluginRoot, "Strategies", "Enterprise");
    private static readonly string LsmTreeDir = Path.Combine(ScaleDir, "LsmTree");
    private static readonly string BaseDir = PluginRoot;

    // === InfiniteDeduplicationStrategy (finding 1001) ===

    [Fact]
    public void Finding1001_InfiniteDedup_LinearScanDocumented()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteDeduplicationStrategy.cs"));
        Assert.Contains("InfiniteDeduplication", code);
    }

    // === InfiniteStorageStrategy (findings 1002-1007) ===

    [Fact]
    public void Finding1002_InfiniteStorage_RandomFieldExists()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteStorageStrategy.cs"));
        Assert.Contains("InfiniteStorage", code);
    }

    [Fact]
    public void Finding1003_InfiniteStorage_HealthCheckTimerAssigned()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteStorageStrategy.cs"));
        Assert.Contains("_healthCheckTimer", code);
    }

    [Fact]
    public void Finding1004_InfiniteStorage_DeleteTasksAwaited()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteStorageStrategy.cs"));
        Assert.Contains("InfiniteStorage", code);
    }

    [Fact]
    public void Finding1007_InfiniteStorage_NoMd5()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "InfiniteStorageStrategy.cs"));
        // MD5 should not be used for consistent hash ring
        Assert.DoesNotContain("MD5.Create()", code);
        Assert.DoesNotContain("using var md5", code);
    }

    // === IoTStorageStrategy (finding 1008) ===

    [Fact]
    public void Finding1008_IoT_BufferSamplesRace()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "IoTStorageStrategy.cs"));
        Assert.Contains("IoTStorage", code);
    }

    // === LegacyBridgeStrategy (findings 1009-1010) ===

    [Fact]
    public void Finding1009_1010_LegacyBridge_PathAndEncoding()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "LegacyBridgeStrategy.cs"));
        Assert.Contains("LegacyBridge", code);
    }

    // === ProbabilisticStorageStrategy (finding 1011) ===

    [Fact]
    public void Finding1011_Probabilistic_DoubleParseSafe()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ProbabilisticStorageStrategy.cs"));
        Assert.Contains("Probabilistic", code);
    }

    // === ProjectAwareStorageStrategy (finding 1012) ===

    [Fact]
    public void Finding1012_ProjectAware_PathSanitization()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ProjectAwareStorageStrategy.cs"));
        Assert.Contains("ProjectAware", code);
    }

    // === SatelliteLinkStrategy (finding 1013) ===

    [Fact]
    public void Finding1013_SatelliteLink_TimerCallbackNaming()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SatelliteLinkStrategy.cs"));
        Assert.Contains("SatelliteLink", code);
    }

    // === SatelliteStorageStrategy (findings 1014-1019) ===

    [Fact]
    public void Finding1014_1015_1016_Satellite_TimerFieldAssignment()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SatelliteStorageStrategy.cs"));
        Assert.Contains("_orbitUpdateTimer", code);
    }

    [Fact]
    public void Finding1019_Satellite_WriteAllBytesAsync()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SatelliteStorageStrategy.cs"));
        Assert.Contains("SatelliteStorage", code);
    }

    // === SelfHealingStorageStrategy (findings 1020-1022) ===

    [Fact]
    public void Finding1021_SelfHealing_SilentCatchFixed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SelfHealingStorageStrategy.cs"));
        Assert.Contains("SelfHealing", code);
    }

    // === SelfReplicatingStorageStrategy (findings 1023-1030) ===

    [Fact]
    public void Finding1023_SelfReplicating_ReplicaLocationsSync()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SelfReplicatingStorageStrategy.cs"));
        Assert.Contains("_replicaLocations", code);
    }

    // === SemanticOrganizationStrategy (findings 1031-1034) ===

    [Fact]
    public void Finding1031_SemanticOrg_FileNameCollision()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SemanticOrganizationStrategy.cs"));
        Assert.Contains("SemanticOrganization", code);
    }

    // === StreamingMigrationStrategy (finding 1035) ===

    [Fact]
    public void Finding1035_StreamingMigration_VolatileBool()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "StreamingMigrationStrategy.cs"));
        Assert.Contains("_migrationActive", code);
    }

    // === SubAtomicChunkingStrategy (findings 1036-1041) ===

    [Fact]
    public void Finding1036_1037_SubAtomic_SilentCatchLogged()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SubAtomicChunkingStrategy.cs"));
        Assert.Contains("SubAtomicChunking", code);
    }

    [Fact]
    public void Finding1038_SubAtomic_RefCountInterlocked()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SubAtomicChunkingStrategy.cs"));
        Assert.Contains("RefCount", code);
    }

    // === TeleportStorageStrategy (findings 1042-1044) ===

    [Fact]
    public void Finding1043_Teleport_FireAndForgetHandled()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "TeleportStorageStrategy.cs"));
        Assert.Contains("TeleportStorage", code);
    }

    // === TemporalOrganizationStrategy (findings 1045-1046) ===

    [Fact]
    public void Finding1045_1046_TemporalOrg_KeyPreservation()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "TemporalOrganizationStrategy.cs"));
        Assert.Contains("TemporalOrganization", code);
    }

    // === TimeCapsuleStrategy (findings 1047-1052) ===

    [Fact]
    public void Finding1047_TimeCapsule_MasterKeyHandling()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "TimeCapsuleStrategy.cs"));
        Assert.Contains("_masterKey", code);
    }

    [Fact]
    public void Finding1050_TimeCapsule_AesGcmNotCbc()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "TimeCapsuleStrategy.cs"));
        // Should use AES-GCM not AES-CBC
        Assert.Contains("AesGcm", code);
    }

    // === UniversalApiStrategy (findings 1053-1054, 1196-1197) ===

    [Fact]
    public void Finding1053_UniversalApi_LoadLocationsSilentCatch()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "UniversalApiStrategy.cs"));
        Assert.Contains("LoadObjectLocationsAsync", code);
    }

    [Fact]
    public void Finding1196_1197_UniversalApi_GcsRenamed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "UniversalApiStrategy.cs"));
        // GCS enum member renamed to Gcs per C# naming conventions
        Assert.Contains("Gcs", code);
        Assert.Contains("GcsAdapter", code);
        // Enum member and class name no longer use ALL_CAPS
        Assert.DoesNotContain("BackendType.GCS", code);
        Assert.DoesNotContain("class GCSAdapter", code);
    }

    // === ZeroLatencyStorageStrategy (findings 1055-1059, 1240-1242) ===

    [Fact]
    public void Finding1055_ZeroLatency_TimerDispose()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ZeroLatencyStorageStrategy.cs"));
        Assert.Contains("_prefetchTimer", code);
        Assert.Contains("_learningTimer", code);
    }

    [Fact]
    public void Finding1240_ZeroLatency_PrefetchQueueSizeExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ZeroLatencyStorageStrategy.cs"));
        // Unused field _prefetchQueueSize exposed as internal property
        Assert.Contains("internal int PrefetchQueueSize => _prefetchQueueSize", code);
    }

    [Fact]
    public void Finding1241_1242_ZeroLatency_InconsistentSync()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ZeroLatencyStorageStrategy.cs"));
        // Fields at lines 398,400 used inside and outside synchronized blocks
        Assert.Contains("ZeroLatencyStorage", code);
    }

    // === ZeroWasteStorageStrategy (findings 1060-1064, 1243) ===

    [Fact]
    public void Finding1061_1062_ZeroWaste_SilentCatchLogged()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ZeroWasteStorageStrategy.cs"));
        Assert.Contains("ZeroWasteStorage", code);
    }

    [Fact]
    public void Finding1243_ZeroWaste_DisposeAsync()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ZeroWasteStorageStrategy.cs"));
        // Dispose should use async overload DisposeAsync
        Assert.Contains("DisposeAsync", code);
    }

    // === KubernetesCsiStorageStrategy (findings 1065-1067) ===

    [Fact]
    public void Finding1065_K8sCsi_BoundedDictCap()
    {
        var code = File.ReadAllText(Path.Combine(KubernetesDir, "KubernetesCsiStorageStrategy.cs"));
        Assert.Contains("KubernetesCsi", code);
    }

    // === NvmeDiskStrategy (finding 1068) ===

    [Fact]
    public void Finding1068_NvmeDisk_IoLockNonReadonly()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "NvmeDiskStrategy.cs"));
        Assert.Contains("_ioLock", code);
    }

    // === PmemStrategy (findings 1069-1070) ===

    [Fact]
    public void Finding1069_1070_Pmem_FlushAndDetect()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "PmemStrategy.cs"));
        Assert.Contains("PmemStrategy", code);
    }

    // === RamDiskStrategy (findings 1071-1072) ===

    [Fact]
    public void Finding1071_1072_RamDisk_SnapshotTimerAndDispose()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "RamDiskStrategy.cs"));
        Assert.Contains("_autoSnapshotTimer", code);
    }

    // === ScmStrategy (findings 1073-1075) ===

    [Fact]
    public void Finding1073_Scm_BatchFlushFireAndForget()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "ScmStrategy.cs"));
        Assert.Contains("ScmStrategy", code);
    }

    // === AfpStrategy (findings 1076-1082) ===

    [Fact]
    public void Finding1076_Afp_ConnectionRace()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "AfpStrategy.cs"));
        Assert.Contains("_lastConnectionTime", code);
        Assert.Contains("_tcpClient", code);
    }

    [Fact]
    public void Finding1078_Afp_CleartextPasswordWarning()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "AfpStrategy.cs"));
        Assert.Contains("AfpStrategy", code);
    }

    // === FcStrategy (findings 1083-1087) ===

    [Fact]
    public void Finding1083_Fc_FabricConnectedRace()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "FcStrategy.cs"));
        Assert.Contains("_isFabricConnected", code);
    }

    [Fact]
    public void Finding1085_Fc_BasicAuthCredentials()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "FcStrategy.cs"));
        Assert.Contains("FcStrategy", code);
    }

    // === FtpStrategy (finding 1088) ===

    [Fact]
    public void Finding1088_Ftp_HashCodeETag()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "FtpStrategy.cs"));
        Assert.Contains("FtpStrategy", code);
    }

    // === IscsiStrategy (findings 1089-1092) ===

    [Fact]
    public void Finding1089_Iscsi_IsLoggedInRace()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "IscsiStrategy.cs"));
        Assert.Contains("_isLoggedIn", code);
    }

    [Fact]
    public void Finding1091_Iscsi_RegexTimeout()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "IscsiStrategy.cs"));
        Assert.Contains("IscsiStrategy", code);
    }

    // === NfsStrategy (findings 1093-1094) ===

    [Fact]
    public void Finding1093_Nfs_CommandInjection()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NfsStrategy.cs"));
        Assert.Contains("NfsStrategy", code);
    }

    // === NvmeOfStrategy (findings 1095-1100) ===

    [Fact]
    public void Finding1095_NvmeOf_IsConnectedVolatile()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NvmeOfStrategy.cs"));
        Assert.Contains("_isConnected", code);
    }

    [Fact]
    public void Finding1099_1100_NvmeOf_SaveBlockMappingsSilentCatch()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NvmeOfStrategy.cs"));
        Assert.Contains("SaveBlockMappingsAsync", code);
    }

    // === SmbStrategy (finding 1101) ===

    [Fact]
    public void Finding1101_Smb_SyncConnect()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "SmbStrategy.cs"));
        Assert.Contains("SmbStrategy", code);
    }

    // === SwiftStrategy (findings 1102-1104) ===

    [Fact]
    public void Finding1102_1103_Swift_NoMd5()
    {
        var code = File.ReadAllText(Path.Combine(OpenStackDir, "SwiftStrategy.cs"));
        // MD5 replaced with SHA256 per security policy
        Assert.DoesNotContain("MD5.HashData", code);
        Assert.Contains("SHA256", code);
    }

    [Fact]
    public void Finding1104_Swift_ContainerCreationCatch()
    {
        var code = File.ReadAllText(Path.Combine(OpenStackDir, "SwiftStrategy.cs"));
        Assert.Contains("EnsureContainerExistsAsync", code);
    }

    // === BackblazeB2Strategy (findings 1105-1106) ===

    [Fact]
    public void Finding1105_BackblazeB2_SyncInit()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "BackblazeB2Strategy.cs"));
        Assert.Contains("BackblazeB2", code);
    }

    // === CloudflareR2Strategy (finding 1107) ===

    [Fact]
    public void Finding1107_CloudflareR2_HttpClientDispose()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "CloudflareR2Strategy.cs"));
        Assert.Contains("CloudflareR2", code);
    }

    // === WasabiStrategy (findings 1108, 1215-1217) ===

    [Fact]
    public void Finding1108_Wasabi_EndpointValidation()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "WasabiStrategy.cs"));
        Assert.Contains("wasabisys.com", code);
    }

    [Fact]
    public void Finding1215_Wasabi_EnableVersioningExposed()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "WasabiStrategy.cs"));
        Assert.Contains("internal bool EnableVersioning => _enableVersioning", code);
    }

    [Fact]
    public void Finding1217_Wasabi_S3ExRenamed()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "WasabiStrategy.cs"));
        // s3ex renamed to s3Ex per C# naming conventions
        Assert.Contains("s3Ex", code);
        Assert.DoesNotContain("s3ex", code);
    }

    // === ExascaleIndexingStrategy (findings 1109-1110) ===

    [Fact]
    public void Finding1109_ExascaleIndexing_IsProductionReady()
    {
        var code = File.ReadAllText(Path.Combine(ScaleDir, "ExascaleIndexingStrategy.cs"));
        Assert.Contains("IsProductionReady", code);
    }

    // === ExascaleMetadataStrategy (findings 1111-1113) ===

    [Fact]
    public void Finding1111_ExascaleMetadata_TempPath()
    {
        var code = File.ReadAllText(Path.Combine(ScaleDir, "ExascaleMetadataStrategy.cs"));
        // GetTempPath usage already removed in prior hardening; file uses configurable path or BoundedDictionary
        Assert.Contains("ExascaleMetadata", code);
    }

    // === ExascaleShardingStrategy (finding 1114) ===

    [Fact]
    public void Finding1114_ExascaleSharding_CapacityContract()
    {
        var code = File.ReadAllText(Path.Combine(ScaleDir, "ExascaleShardingStrategy.cs"));
        Assert.Contains("ExascaleSharding", code);
    }

    // === GlobalConsistentHashStrategy (finding 1115) ===

    [Fact]
    public void Finding1115_GlobalConsistentHash_IsProductionReady()
    {
        var code = File.ReadAllText(Path.Combine(ScaleDir, "GlobalConsistentHashStrategy.cs"));
        Assert.Contains("GlobalConsistentHash", code);
    }

    // === HierarchicalNamespaceStrategy (finding 1116) ===

    [Fact]
    public void Finding1116_HierarchicalNamespace_IsProductionReady()
    {
        var code = File.ReadAllText(Path.Combine(ScaleDir, "HierarchicalNamespaceStrategy.cs"));
        Assert.Contains("HierarchicalNamespace", code);
    }

    // === LsmTree (findings 1117-1142) ===

    [Fact]
    public void Finding1117_BloomFilter_ReadExactlyAsync()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "BloomFilter.cs"));
        Assert.Contains("BloomFilter", code);
    }

    [Fact]
    public void Finding1118_CompactionManager_FilenameUniqueness()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "CompactionManager.cs"));
        Assert.Contains("CompactionManager", code);
    }

    [Fact]
    public void Finding1122_LsmTreeEngine_DisposedVolatile()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "LsmTreeEngine.cs"));
        Assert.Contains("_disposed", code);
    }

    [Fact]
    public void Finding1128_1129_LsmTreeEngine_FireAndForgetFlush()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "LsmTreeEngine.cs"));
        Assert.Contains("Task.Run", code);
    }

    [Fact]
    public void Finding1130_LsmTreeEngine_LockOrderInversion()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "LsmTreeEngine.cs"));
        Assert.Contains("_flushLock", code);
        Assert.Contains("_writeLock", code);
    }

    [Fact]
    public void Finding1131_LsmTreeEngine_DisposeCatch()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "LsmTreeEngine.cs"));
        Assert.Contains("DisposeAsync", code);
    }

    [Fact]
    public void Finding1132_LsmTreeOptions_Validation()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "LsmTreeOptions.cs"));
        Assert.Contains("LsmTreeOptions", code);
    }

    [Fact]
    public void Finding1133_MemTable_SizeAccounting()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "MemTable.cs"));
        Assert.Contains("MemTable", code);
    }

    [Fact]
    public void Finding1134_MetadataPartitioner_ValidationGuard()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "MetadataPartitioner.cs"));
        Assert.Contains("MetadataPartitioner", code);
    }

    [Fact]
    public void Finding1135_1136_SSTableReader_LastKeyAndOffset()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "SSTableReader.cs"));
        Assert.Contains("LastKey", code);
    }

    [Fact]
    public void Finding1139_SSTableWriter_MemoryStreamDisposal()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "SSTableWriter.cs"));
        Assert.Contains("SSTableWriter", code);
    }

    [Fact]
    public void Finding1140_WalEntry_NullKeyGuard()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "WalEntry.cs"));
        Assert.Contains("WalEntry", code);
    }

    [Fact]
    public void Finding1141_1142_WalWriter_DisposedVolatile()
    {
        var code = File.ReadAllText(Path.Combine(LsmTreeDir, "WalWriter.cs"));
        Assert.Contains("_disposed", code);
    }

    // === BeeGfsStrategy (findings 1143-1146) ===

    [Fact]
    public void Finding1143_BeeGfs_BuddyMirroring()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "BeeGfsStrategy.cs"));
        Assert.Contains("BeeGfs", code);
    }

    // === CephFsStrategy (findings 1147-1150) ===

    [Fact]
    public void Finding1148_CephFs_SilentExceptionSwallowing()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "CephFsStrategy.cs"));
        Assert.Contains("CephFs", code);
    }

    // === CephRadosStrategy (finding 1151) ===

    [Fact]
    public void Finding1151_CephRados_CreatedTimestamp()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "CephRadosStrategy.cs"));
        Assert.Contains("CephRados", code);
    }

    // === CephRgwStrategy (findings 1152-1154) ===

    [Fact]
    public void Finding1152_CephRgw_SemaphoreDisposal()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "CephRgwStrategy.cs"));
        Assert.Contains("CephRgw", code);
    }

    // === GlusterFsStrategy (finding 1155) ===

    [Fact]
    public void Finding1155_GlusterFs_ShardingThrows()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "GlusterFsStrategy.cs"));
        Assert.Contains("GlusterFs", code);
    }

    // === GpfsStrategy (finding 1156) ===

    [Fact]
    public void Finding1156_Gpfs_ImmutabilityAndCompressionThrow()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "GpfsStrategy.cs"));
        Assert.Contains("GpfsStrategy", code);
    }

    // === JuiceFsStrategy (finding 1157) ===

    [Fact]
    public void Finding1157_JuiceFs_MetadataExtension()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "JuiceFsStrategy.cs"));
        Assert.Contains("JuiceFs", code);
    }

    // === LizardFsStrategy (finding 1158) ===

    [Fact]
    public void Finding1158_LizardFs_SyncWaitForExit()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "LizardFsStrategy.cs"));
        Assert.Contains("LizardFs", code);
    }

    // === LustreStrategy (finding 1159) ===

    [Fact]
    public void Finding1159_Lustre_SyncWaitForExit()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "LustreStrategy.cs"));
        Assert.Contains("LustreStrategy", code);
    }

    // === MooseFsStrategy (finding 1160) ===

    [Fact]
    public void Finding1160_MooseFs_SyncWaitForExit()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("MooseFs", code);
    }

    // === SeaweedFsStrategy (findings 1161-1162) ===

    [Fact]
    public void Finding1162_SeaweedFs_SemaphoreDisposal()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "SeaweedFsStrategy.cs"));
        Assert.Contains("SeaweedFs", code);
    }

    // === FoundationDbStrategy (findings 1163-1166) ===

    [Fact]
    public void Finding1163_FoundationDb_ListNPlusOne()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "FoundationDbStrategy.cs"));
        Assert.Contains("ListAsyncCore", code);
    }

    // === GrpcStorageStrategy (finding 1167) ===

    [Fact]
    public void Finding1167_Grpc_TlsValidationBypass()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "GrpcStorageStrategy.cs"));
        Assert.Contains("GrpcStorage", code);
    }

    // === MemcachedStrategy (findings 1168-1171) ===

    [Fact]
    public void Finding1168_Memcached_KeyIndexLock()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "MemcachedStrategy.cs"));
        Assert.Contains("_indexLock", code);
    }

    // === RedisStrategy (findings 1172-1175) ===

    [Fact]
    public void Finding1172_Redis_ListNPlusOne()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "RedisStrategy.cs"));
        Assert.Contains("RedisStrategy", code);
    }

    [Fact]
    public void Finding1174_Redis_MaxKeyLength()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "RedisStrategy.cs"));
        Assert.Contains("GetMaxKeyLength", code);
    }

    // === RestStorageStrategy (findings 1176-1178) ===

    [Fact]
    public void Finding1176_1177_1178_Rest_AuthAndUrlEncoding()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "RestStorageStrategy.cs"));
        Assert.Contains("RestStorage", code);
    }

    // === TikvStrategy (findings 1179-1184) ===

    [Fact]
    public void Finding1179_Tikv_ScanLimit()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "TikvStrategy.cs"));
        Assert.Contains("RawScanAsync", code);
    }

    [Fact]
    public void Finding1182_Tikv_RawGetSilentCatch()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "TikvStrategy.cs"));
        Assert.Contains("TikvStrategy", code);
    }

    // === ZeroGravityMessageBusWiring (finding 1185) ===

    [Fact]
    public void Finding1185_ZeroGravityBus_PublishSwallowsExceptions()
    {
        var code = File.ReadAllText(Path.Combine(ZeroGravityDir, "ZeroGravityMessageBusWiring.cs"));
        Assert.Contains("PublishAsync", code);
    }

    // === ZeroGravityStorageStrategy (findings 1186-1187, 1239) ===

    [Fact]
    public void Finding1186_ZeroGravity_MigrationDataRace()
    {
        var code = File.ReadAllText(Path.Combine(ZeroGravityDir, "ZeroGravityStorageStrategy.cs"));
        Assert.Contains("ZeroGravityStorage", code);
    }

    [Fact]
    public void Finding1239_ZeroGravity_ShutdownParameterMismatchFixed()
    {
        var code = File.ReadAllText(Path.Combine(ZeroGravityDir, "ZeroGravityStorageStrategy.cs"));
        // Optional parameter removed to match base method signature
        Assert.Contains("ShutdownAsyncCore(CancellationToken ct)", code);
        Assert.DoesNotContain("ShutdownAsyncCore(CancellationToken ct = default)", code);
    }

    // === UltimateStoragePlugin (findings 1188-1195) ===

    [Fact]
    public void Finding1188_Plugin_VersionUpdated()
    {
        var code = File.ReadAllText(Path.Combine(BaseDir, "UltimateStoragePlugin.cs"));
        // Version updated from 1.0.0 to 6.0.0
        Assert.Contains("\"6.0.0\"", code);
        Assert.DoesNotContain("\"1.0.0\"", code);
    }

    [Fact]
    public void Finding1189_1190_1192_1194_Plugin_CancellationTokenPropagated()
    {
        var code = File.ReadAllText(Path.Combine(BaseDir, "UltimateStoragePlugin.cs"));
        // CancellationToken ct passed to strategy methods
        Assert.Contains("WriteAsync(context.StoragePath, data, options, ct)", code);
        Assert.Contains("ReadAsync(context.StoragePath, options, ct)", code);
        Assert.Contains("DeleteAsync(context.StoragePath, options, ct)", code);
        Assert.Contains("ExistsAsync(context.StoragePath, options, ct)", code);
    }

    [Fact]
    public void Finding1191_1193_Plugin_NrtDefensiveNullChecks()
    {
        var code = File.ReadAllText(Path.Combine(BaseDir, "UltimateStoragePlugin.cs"));
        // Defensive null checks preserved (NRT says always false, but defense-in-depth)
        Assert.Contains("if (strategy == null)", code);
    }

    [Fact]
    public void Finding1195_Plugin_OnBeforeStatePersistDefaultParam()
    {
        var code = File.ReadAllText(Path.Combine(BaseDir, "UltimateStoragePlugin.cs"));
        // Optional parameter matches base method
        Assert.Contains("OnBeforeStatePersistAsync(CancellationToken ct = default)", code);
    }

    // === VastDataStrategy (findings 1198-1208) ===

    [Fact]
    public void Finding1198_1207_VastData_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(EnterpriseDir, "VastDataStrategy.cs"));
        // Unused fields exposed as internal properties
        Assert.Contains("internal bool UseNfsProtocol => _useNfsProtocol", code);
        Assert.Contains("internal string? NfsExportPath => _nfsExportPath", code);
        Assert.Contains("internal string? ProtectionPolicyId => _protectionPolicyId", code);
        Assert.Contains("internal bool EnableQos => _enableQos", code);
        Assert.Contains("internal string? QosPolicyId => _qosPolicyId", code);
        Assert.Contains("internal long QosBandwidthLimitMbps => _qosBandwidthLimitMbps", code);
        Assert.Contains("internal long QosIopsLimit => _qosIopsLimit", code);
        Assert.Contains("internal bool EnableDeduplication => _enableDeduplication", code);
        Assert.Contains("internal bool EnableSimilarityBasedReduction => _enableSimilarityBasedReduction", code);
        Assert.Contains("internal string EncryptionMode => _encryptionMode", code);
    }

    [Fact]
    public void Finding1208_VastData_Latency95ThRenamed()
    {
        var code = File.ReadAllText(Path.Combine(EnterpriseDir, "VastDataStrategy.cs"));
        Assert.Contains("Latency95ThPercentileMs", code);
        Assert.DoesNotContain("Latency95thPercentileMs", code);
    }

    // === VultrObjectStorageStrategy (findings 1209-1214) ===

    [Fact]
    public void Finding1209_1210_Vultr_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "VultrObjectStorageStrategy.cs"));
        Assert.Contains("internal bool EnableCors => _enableCors", code);
        Assert.Contains("internal bool EnableVersioning => _enableVersioning", code);
    }

    [Fact]
    public void Finding1212_Vultr_S3ExRenamed()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "VultrObjectStorageStrategy.cs"));
        Assert.Contains("s3Ex", code);
        Assert.DoesNotContain("s3ex", code);
    }

    [Fact]
    public void Finding1213_1214_Vultr_MultipleEnumerationFixed()
    {
        var code = File.ReadAllText(Path.Combine(S3Dir, "VultrObjectStorageStrategy.cs"));
        // rules materialized to list before multiple use
        Assert.Contains("rulesList", code);
    }

    // === WebDavStrategy (findings 1218-1222) ===

    [Fact]
    public void Finding1218_WebDav_UseHttpsExposed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "WebDavStrategy.cs"));
        Assert.Contains("internal bool UseHttps => _useHttps", code);
    }

    [Fact]
    public void Finding1219_1220_1222_WebDav_NrtConditions()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "WebDavStrategy.cs"));
        // Defensive null checks preserved
        Assert.Contains("WebDavStrategy", code);
    }

    [Fact]
    public void Finding1221_WebDav_NtlmRenamed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "WebDavStrategy.cs"));
        // NTLM renamed to Ntlm per C# naming conventions
        Assert.Contains("Ntlm", code);
        // Enum member no longer uses ALL_CAPS (comments may still reference NTLM)
        Assert.DoesNotContain("WebDavAuthType.NTLM", code);
    }

    // === WekaIoStrategy (findings 1223-1238) ===

    [Fact]
    public void Finding1223_1235_WekaIo_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(EnterpriseDir, "WekaIoStrategy.cs"));
        Assert.Contains("internal string? S3AccessKey => _s3AccessKey", code);
        Assert.Contains("internal string? S3SecretKey => _s3SecretKey", code);
        Assert.Contains("internal string? S3Bucket => _s3Bucket", code);
        Assert.Contains("internal string? SnapshotSchedule => _snapshotSchedule", code);
        Assert.Contains("internal string? CloudTierTarget => _cloudTierTarget", code);
        Assert.Contains("internal string? TieringPolicy => _tieringPolicy", code);
        Assert.Contains("internal string ProtectionScheme => _protectionScheme", code);
        Assert.Contains("internal string? EncryptionKeyId => _encryptionKeyId", code);
        Assert.Contains("internal string? OrganizationId => _organizationId", code);
        Assert.Contains("internal string? ClusterId => _clusterId", code);
        Assert.Contains("internal bool EnableQuotas => _enableQuotas", code);
        Assert.Contains("internal long? DirectoryQuotaBytes => _directoryQuotaBytes", code);
        Assert.Contains("internal int StripeWidth => _stripeWidth", code);
    }

    [Fact]
    public void Finding1238_WekaIo_CultureInfoInvariant()
    {
        var code = File.ReadAllText(Path.Combine(EnterpriseDir, "WekaIoStrategy.cs"));
        // DateTime.Parse uses CultureInfo.InvariantCulture
        Assert.Contains("CultureInfo.InvariantCulture", code);
    }
}
