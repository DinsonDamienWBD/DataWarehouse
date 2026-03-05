// Hardening tests for UltimateStorage findings 601-750
// PredictiveCompression, Probabilistic, ProtocolMorphing, PureStorage, QuantumTunneling,
// RaidIntegration, RamDisk, Redis, Replication, RestApiConnector, S3Glacier, S3, Satellite,
// Scaleway, Scm, SearchScaling, SeaweedFs, SelfHealing, SelfReplicating, Semantic, Sftp,
// Sia, Smb, Snowflake, SqlServer, SSTable, StorageMigration, StorageStrategyBase, Storj,
// SubAtomicChunking, Swarm, TapeLibrary, Teleport, Tikv

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class SpecializedAndArchiveTests3
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");
    private static readonly string InnovationDir = Path.Combine(PluginRoot, "Strategies", "Innovation");
    private static readonly string EnterpriseDir = Path.Combine(PluginRoot, "Strategies", "Enterprise");
    private static readonly string FeaturesDir = Path.Combine(PluginRoot, "Features");
    private static readonly string LocalDir = Path.Combine(PluginRoot, "Strategies", "Local");
    private static readonly string SpecializedDir = Path.Combine(PluginRoot, "Strategies", "Specialized");
    private static readonly string ConnectorDir = Path.Combine(PluginRoot, "Strategies", "Connectors");
    private static readonly string ArchiveDir = Path.Combine(PluginRoot, "Strategies", "Archive");
    private static readonly string CloudDir = Path.Combine(PluginRoot, "Strategies", "Cloud");
    private static readonly string S3CompatDir = Path.Combine(PluginRoot, "Strategies", "S3Compatible");
    private static readonly string SoftwareDefinedDir = Path.Combine(PluginRoot, "Strategies", "SoftwareDefined");
    private static readonly string NetworkDir = Path.Combine(PluginRoot, "Strategies", "Network");
    private static readonly string DecentralizedDir = Path.Combine(PluginRoot, "Strategies", "Decentralized");
    private static readonly string ImportDir = Path.Combine(PluginRoot, "Strategies", "Import");
    private static readonly string ScaleDir = Path.Combine(PluginRoot, "Strategies", "Scale");
    private static readonly string MigrationDir = Path.Combine(PluginRoot, "Migration");
    private static readonly string ScalingDir = Path.Combine(PluginRoot, "Scaling");

    // === PredictiveCompressionStrategy (findings 602-604) ===

    [Fact]
    public void Finding602_603_Predictive_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "PredictiveCompressionStrategy.cs"));
        Assert.Contains("internal bool EnableParallelCompression", code);
        Assert.Contains("internal int ParallelChunkSize", code);
    }

    [Fact]
    public void Finding604_Predictive_StaticFieldRenamed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "PredictiveCompressionStrategy.cs"));
        Assert.Contains("AlreadyCompressedExtensions", code);
        Assert.DoesNotContain("_alreadyCompressedExtensions", code);
    }

    // === ProbabilisticStorageStrategy (findings 605-608) ===

    [Fact]
    public void Finding605_Probabilistic_UnusedModeVariableRemoved()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ProbabilisticStorageStrategy.cs"));
        Assert.DoesNotContain("string mode = \"auto\"", code);
    }

    [Fact]
    public void Finding606_607_Probabilistic_FprRenamed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ProbabilisticStorageStrategy.cs"));
        Assert.Contains("ActualFpr", code);
        Assert.Contains("ConfiguredFpr", code);
        Assert.DoesNotContain("ActualFPR", code);
        Assert.DoesNotContain("ConfiguredFPR", code);
    }

    // === ProtocolMorphingStrategy (findings 609-610) ===

    [Fact]
    public void Finding609_Protocol_GcsEnumRenamed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ProtocolMorphingStrategy.cs"));
        Assert.Contains("Gcs", code);
        Assert.DoesNotContain("{ S3, Azure, GCS,", code);
    }

    [Fact]
    public void Finding610_Protocol_GcsProtocolAdapterRenamed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "ProtocolMorphingStrategy.cs"));
        Assert.Contains("GcsProtocolAdapter", code);
        Assert.DoesNotContain("GCSProtocolAdapter", code);
    }

    // === PureStorageStrategy (findings 611-621) ===

    [Fact]
    public void Findings611to617_PureStorage_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(EnterpriseDir, "PureStorageStrategy.cs"));
        Assert.Contains("internal string? SnapshotPolicy", code);
        Assert.Contains("internal int? LifecycleTransitionDays", code);
        Assert.Contains("internal bool EnableQuotas", code);
        Assert.Contains("internal long? HardQuotaBytes", code);
        Assert.Contains("internal bool EnableDeduplication", code);
        Assert.Contains("internal string? NfsExportRules", code);
        Assert.Contains("internal string? SmbShareRules", code);
    }

    [Fact]
    public void Findings618to621_PureStorage_CultureExplicit()
    {
        var code = File.ReadAllText(Path.Combine(EnterpriseDir, "PureStorageStrategy.cs"));
        Assert.Contains("using System.Globalization;", code);
        Assert.Contains("CultureInfo.InvariantCulture", code);
    }

    // === QuantumTunnelingStrategy (finding 622) ===

    [Fact]
    public void Finding622_QuantumTunneling_EnableCompressionExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "QuantumTunnelingStrategy.cs"));
        Assert.Contains("internal bool EnableCompression", code);
    }

    // === RaidIntegrationFeature (findings 623-628) ===

    [Fact]
    public void Finding623_Raid_NullCheckSimplified()
    {
        var code = File.ReadAllText(Path.Combine(FeaturesDir, "RaidIntegrationFeature.cs"));
        Assert.Contains("if (message.Type == null", code);
        Assert.DoesNotContain("message?.Type == null", code);
    }

    [Fact]
    public void Findings624to628_Raid_EnumRenames()
    {
        var code = File.ReadAllText(Path.Combine(FeaturesDir, "RaidIntegrationFeature.cs"));
        Assert.Contains("Raid0", code);
        Assert.Contains("Raid1", code);
        Assert.Contains("Raid5", code);
        Assert.Contains("Raid6", code);
        Assert.Contains("Raid10", code);
        Assert.DoesNotContain("RAID0", code);
    }

    // === RamDiskStrategy (findings 629-640) ===

    [Fact]
    public void Findings629to637_RamDisk_TimerCallbacksFixed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "RamDiskStrategy.cs"));
        // Timer callbacks use Task.Run pattern instead of async lambda void
        Assert.Contains("state => Task.Run(async ()", code);
        Assert.DoesNotContain("_ => _ = Task.Run", code);
    }

    // === RedisStrategy (findings 641-642) ===

    [Fact]
    public void Finding641_Redis_MetaAssignmentFixed()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "RedisStrategy.cs"));
        Assert.Contains("StorageObjectMetadata? meta;", code);
    }

    [Fact]
    public void Finding642_Redis_AlwaysTrueCheckRemoved()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "RedisStrategy.cs"));
        Assert.Contains("yield return meta;", code);
        Assert.DoesNotContain("if (meta != null)\n                {\n                    yield return meta;", code);
    }

    // === ReplicationIntegrationFeature (findings 643-644) ===

    [Fact]
    public void Finding643_Replication_AsyncLambdaVoidFixed()
    {
        var code = File.ReadAllText(Path.Combine(FeaturesDir, "ReplicationIntegrationFeature.cs"));
        Assert.Contains("_ => Task.Run(async ()", code);
        Assert.DoesNotContain("async _ => await MonitorReplicationLagAsync()", code);
    }

    [Fact]
    public void Finding644_Replication_AlwaysTrueConditionFixed()
    {
        var code = File.ReadAllText(Path.Combine(FeaturesDir, "ReplicationIntegrationFeature.cs"));
        Assert.Contains("var capturedMetadata = new Dictionary<string, string>(metadata);", code);
        Assert.DoesNotContain("metadata != null ? new Dictionary", code);
    }

    // === RestApiConnectorStrategy (finding 645) ===

    [Fact]
    public void Finding645_RestApi_MaxRetriesExposed()
    {
        var code = File.ReadAllText(Path.Combine(ConnectorDir, "RestApiConnectorStrategy.cs"));
        Assert.Contains("internal int MaxRetriesConfig", code);
    }

    // === S3GlacierStrategy (finding 646) ===

    [Fact]
    public void Finding646_S3Glacier_DuplicatedStatementsFixed()
    {
        var code = File.ReadAllText(Path.Combine(ArchiveDir, "S3GlacierStrategy.cs"));
        // Glacier and DeepArchive both map to Archive - consolidated
        Assert.Contains("// Glacier, DeepArchive, and all other classes map to Archive", code);
    }

    // === S3Strategy (findings 647-650) ===

    [Fact]
    public void Finding647_S3_EnableVersioningExposed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "S3Strategy.cs"));
        Assert.Contains("internal bool EnableVersioning", code);
    }

    [Fact]
    public void Finding648_S3_CompletedPartsAssignmentFixed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "S3Strategy.cs"));
        Assert.Contains("List<ManualCompletedPart> completedParts;", code);
    }

    [Fact]
    public void Finding649_S3_DisposedVariableFixed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "S3Strategy.cs"));
        Assert.DoesNotContain("using var semaphore = new SemaphoreSlim", code);
        Assert.Contains("semaphore.Dispose()", code);
    }

    [Fact]
    public void Finding650_S3_ShutdownAsyncParameterFixed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "S3Strategy.cs"));
        Assert.Contains("ShutdownAsyncCore(CancellationToken ct)", code);
        Assert.DoesNotContain("ShutdownAsyncCore(CancellationToken ct = default)", code);
    }

    // === SatelliteLinkStrategy (findings 651-653) ===

    [Fact]
    public void Finding651_SatelliteLink_DownloadQueueExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SatelliteLinkStrategy.cs"));
        Assert.Contains("internal int DownloadQueueCount", code);
    }

    [Fact]
    public void Finding652_SatelliteLink_AsyncLambdaVoidFixed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SatelliteLinkStrategy.cs"));
        Assert.Contains("_ => Task.Run(async ()", code);
    }

    // === SatelliteStorageStrategy (findings 654-656) ===

    [Fact]
    public void Finding654_SatelliteStorage_DopplerExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SatelliteStorageStrategy.cs"));
        Assert.Contains("internal bool EnableDopplerCompensation", code);
    }

    [Fact]
    public void Finding655_SatelliteStorage_AsyncLambdaVoidFixed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SatelliteStorageStrategy.cs"));
        Assert.Contains("_ => Task.Run(async ()", code);
    }

    // === ScalewayObjectStorageStrategy (findings 657-662) ===

    [Fact]
    public void Findings657_658_Scaleway_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(S3CompatDir, "ScalewayObjectStorageStrategy.cs"));
        Assert.Contains("internal bool EnableCors", code);
        Assert.Contains("internal bool EnableVersioning", code);
    }

    [Fact]
    public void Finding659_Scaleway_UploadedPartsFixed()
    {
        var code = File.ReadAllText(Path.Combine(S3CompatDir, "ScalewayObjectStorageStrategy.cs"));
        Assert.Contains("List<PartETag> uploadedParts;", code);
    }

    [Fact]
    public void Finding660_Scaleway_S3ExRenamed()
    {
        var code = File.ReadAllText(Path.Combine(S3CompatDir, "ScalewayObjectStorageStrategy.cs"));
        Assert.Contains("s3Ex", code);
    }

    [Fact]
    public void Finding661_662_Scaleway_MultipleEnumerationFixed()
    {
        var code = File.ReadAllText(Path.Combine(S3CompatDir, "ScalewayObjectStorageStrategy.cs"));
        Assert.Contains("var rulesList = rules?.ToList()", code);
    }

    // === ScmStrategy (findings 663-670) ===

    [Fact]
    public void Findings663to666_Scm_ConstantsRenamed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "ScmStrategy.cs"));
        Assert.Contains("DefaultBlockSize", code);
        Assert.Contains("DefaultMaxMmapSize", code);
        Assert.Contains("DefaultFlushBatchSize", code);
        Assert.Contains("CacheLineSize", code);
        Assert.DoesNotContain("DEFAULT_BLOCK_SIZE", code);
        Assert.DoesNotContain("CACHE_LINE_SIZE", code);
    }

    [Fact]
    public void Findings667to670_Scm_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "ScmStrategy.cs"));
        Assert.Contains("internal bool EnableMemoryMapping", code);
        Assert.Contains("internal TieringPolicy TieringPolicyValue", code);
        Assert.Contains("internal DateTime LastWearLevelingCheck", code);
        Assert.Contains("internal WearLevelingInfo? CachedWearInfo", code);
    }

    // === SearchScalingManager (findings 671-672) ===

    [Fact]
    public void Finding672_SearchScaling_StopWordsRenamed()
    {
        var code = File.ReadAllText(Path.Combine(ScalingDir, "SearchScalingManager.cs"));
        Assert.Contains("StopWords", code);
        Assert.DoesNotContain("_stopWords", code);
    }

    // === SeaweedFsStrategy (findings 673-675) ===

    [Fact]
    public void Finding673_SeaweedFs_EnableGzipExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "SeaweedFsStrategy.cs"));
        Assert.Contains("internal bool EnableGzip", code);
    }

    [Fact]
    public void Finding674_675_SeaweedFs_DisposedVariableFixed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "SeaweedFsStrategy.cs"));
        Assert.Contains("List<ChunkInfo> chunkFileIds;", code);
        Assert.Contains("semaphore.Dispose()", code);
    }

    // === SelfHealingStorageStrategy (findings 676-683) ===

    [Fact]
    public void Findings676_677_SelfHealing_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SelfHealingStorageStrategy.cs"));
        Assert.Contains("internal bool EnableProactiveReplication", code);
        Assert.Contains("internal double CorruptionThreshold", code);
    }

    [Fact]
    public void Findings678to680_SelfHealing_AsyncLambdaVoidFixed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SelfHealingStorageStrategy.cs"));
        Assert.Contains("_ => Task.Run(async ()", code);
        Assert.DoesNotContain("async _ => await PerformHealthChecksAsync", code);
        Assert.DoesNotContain("async _ => await PerformScrubbingAsync", code);
        Assert.DoesNotContain("async _ => await ProcessRepairQueueAsync", code);
    }

    // === SelfReplicatingStorageStrategy (findings 684-687) ===

    [Fact]
    public void Findings684to686_SelfReplicating_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SelfReplicatingStorageStrategy.cs"));
        Assert.Contains("internal bool EnableErasureCoding", code);
        Assert.Contains("internal int ErasureDataShards", code);
        Assert.Contains("internal int ErasureParityShards", code);
    }

    [Fact]
    public void Finding687_SelfReplicating_DelayCancellation()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SelfReplicatingStorageStrategy.cs"));
        Assert.Contains("Task.Delay(_replicationDelayMs, CancellationToken.None)", code);
    }

    // === SemanticOrganizationStrategy (findings 688-690) ===

    [Fact]
    public void Findings688to690_Semantic_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SemanticOrganizationStrategy.cs"));
        Assert.Contains("internal bool EnableAutoTagging", code);
        Assert.Contains("internal bool EnableClustering", code);
        Assert.Contains("internal double SimilarityThreshold", code);
    }

    // === SftpStrategy (findings 691-696) ===

    [Fact]
    public void Findings691to693_Sftp_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "SftpStrategy.cs"));
        Assert.Contains("internal int MaxPacketSize", code);
        Assert.Contains("internal bool EnableCompression", code);
        Assert.Contains("internal bool PreserveTimestamps", code);
    }

    [Fact]
    public void Finding694_Sftp_CovariantArrayFixed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "SftpStrategy.cs"));
        Assert.Contains("keyFiles.Cast<IPrivateKeySource>().ToArray()", code);
    }

    // === SiaStrategy (findings 697-699) ===

    [Fact]
    public void Findings697_698_Sia_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "SiaStrategy.cs"));
        Assert.Contains("internal int UploadChunkSize", code);
        Assert.Contains("internal int MaxConcurrentUploads", code);
    }

    [Fact]
    public void Finding699_Sia_OsPropertyRenamed()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "SiaStrategy.cs"));
        Assert.Contains("public string? Os { get; set; }", code);
        Assert.DoesNotContain("public string? OS { get; set; }", code);
    }

    // === SmbStrategy (findings 700-705) ===

    [Fact]
    public void Finding700_Smb_TimeoutSecondsExposed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "SmbStrategy.cs"));
        Assert.Contains("internal int TimeoutSeconds", code);
    }

    [Fact]
    public void Findings701to704_Smb_FileHandleAssignmentsFixed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "SmbStrategy.cs"));
        Assert.Contains("object? fileHandle;", code);
        Assert.DoesNotContain("object? fileHandle = null;", code);
    }

    [Fact]
    public void Finding705_Smb_NullCheckRemoved()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "SmbStrategy.cs"));
        Assert.DoesNotContain("if (dirInfo == null)", code);
    }

    // === SqlServerImportStrategy (finding 707) ===

    [Fact]
    public void Finding707_SqlServer_UsingVarInitializerFixed()
    {
        var code = File.ReadAllText(Path.Combine(ImportDir, "SqlServerImportStrategy.cs"));
        Assert.Contains("using var bulkCopy = new SqlBulkCopy(connection);", code);
        Assert.Contains("bulkCopy.DestinationTableName", code);
    }

    // === StorageMigrationService (findings 711-713) ===

    [Fact]
    public void Findings711to713_StorageMigration_CollectionsQueried()
    {
        var code = File.ReadAllText(Path.Combine(MigrationDir, "StorageMigrationService.cs"));
        Assert.Contains("HasErrors", code);
        Assert.Contains("HasWarnings", code);
        Assert.Contains("HasRecommendations", code);
    }

    // === StorageStrategyBase (finding 714) ===

    [Fact]
    public void Finding714_StorageStrategyBase_DisposeNewRemoved()
    {
        var code = File.ReadAllText(Path.Combine(PluginRoot, "StorageStrategyBase.cs"));
        Assert.DoesNotContain("public new virtual void Dispose()", code);
        Assert.DoesNotContain("public new async ValueTask DisposeAsync()", code);
        Assert.Contains("protected override void Dispose(bool disposing)", code);
        Assert.Contains("protected override async ValueTask DisposeAsyncCore()", code);
    }

    // === StorjStrategy (findings 715-716) ===

    [Fact]
    public void Finding715_Storj_UseSignatureV4Exposed()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "StorjStrategy.cs"));
        Assert.Contains("internal bool UseSignatureV4", code);
    }

    [Fact]
    public void Finding716_Storj_CompletedPartsAssignmentFixed()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "StorjStrategy.cs"));
        Assert.Contains("List<CompletedPart> completedParts;", code);
    }

    // === SubAtomicChunkingStrategy (finding 717) ===

    [Fact]
    public void Finding717_SubAtomic_CacheSizeMbRenamed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "SubAtomicChunkingStrategy.cs"));
        Assert.Contains("_cacheSizeMb", code);
        Assert.DoesNotContain("_cacheSizeMB", code);
    }

    // === SwarmStrategy (findings 718-726) ===

    [Fact]
    public void Finding718_Swarm_UseManifestsExposed()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "SwarmStrategy.cs"));
        Assert.Contains("internal bool UseManifests", code);
    }

    [Fact]
    public void Finding719_Swarm_UseSingleOwnerChunksExposed()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "SwarmStrategy.cs"));
        Assert.Contains("internal bool UseSingleOwnerChunks", code);
    }

    [Fact]
    public void Findings720to726_Swarm_ReferenceAssignmentsFixed()
    {
        var code = File.ReadAllText(Path.Combine(DecentralizedDir, "SwarmStrategy.cs"));
        Assert.Contains("string? reference;", code);
        Assert.DoesNotContain("string? reference = null;", code);
    }

    // === TapeLibraryStrategy (findings 727-744) ===

    [Fact]
    public void Findings727_728_Tape_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(ArchiveDir, "TapeLibraryStrategy.cs"));
        Assert.Contains("internal bool EnableHardwareCompression", code);
        Assert.Contains("internal int MaxConcurrentDrives", code);
    }

    [Fact]
    public void Findings729to738_Tape_ConstantsRenamed()
    {
        var code = File.ReadAllText(Path.Combine(ArchiveDir, "TapeLibraryStrategy.cs"));
        Assert.Contains("GenericRead", code);
        Assert.Contains("GenericWrite", code);
        Assert.Contains("OpenExisting", code);
        Assert.Contains("FileAttributeNormal", code);
        Assert.Contains("IoctlTapeErase", code);
        Assert.Contains("IoctlTapePrepare", code);
        Assert.Contains("IoctlTapeWriteMarks", code);
        Assert.Contains("IoctlTapeGetPosition", code);
        Assert.Contains("IoctlTapeSetPosition", code);
        Assert.Contains("InvalidHandleValue", code);
        Assert.DoesNotContain("GENERIC_READ", code);
        Assert.DoesNotContain("IOCTL_TAPE_ERASE", code);
    }

    [Fact]
    public void Finding739_Tape_UsingVarInitializerFixed()
    {
        var code = File.ReadAllText(Path.Combine(ArchiveDir, "TapeLibraryStrategy.cs"));
        Assert.Contains("using var process = new System.Diagnostics.Process();", code);
        Assert.Contains("process.StartInfo = startInfo;", code);
    }

    [Fact]
    public void Findings742to744_Tape_LtoEnumRenamed()
    {
        var code = File.ReadAllText(Path.Combine(ArchiveDir, "TapeLibraryStrategy.cs"));
        Assert.Contains("Lto7 = 7", code);
        Assert.Contains("Lto8 = 8", code);
        Assert.Contains("Lto9 = 9", code);
        Assert.DoesNotContain("LTO7 =", code);
        Assert.DoesNotContain("LTO8 =", code);
        Assert.DoesNotContain("LTO9 =", code);
    }

    // === TeleportStorageStrategy (findings 745-748) ===

    [Fact]
    public void Findings745_746_Teleport_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "TeleportStorageStrategy.cs"));
        Assert.Contains("internal bool EnablePredictiveReplication", code);
        Assert.Contains("internal bool EnableLatencyRouting", code);
    }

    [Fact]
    public void Finding747_748_Teleport_CancellationTokenPassed()
    {
        var code = File.ReadAllText(Path.Combine(InnovationDir, "TeleportStorageStrategy.cs"));
        Assert.Contains("WriteAllBytesAsync(regionPath, dataBytes, CancellationToken.None)", code);
    }

    // === TikvStrategy (findings 749-750) ===

    [Fact]
    public void Finding749_Tikv_TransactionTimeoutExposed()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "TikvStrategy.cs"));
        Assert.Contains("internal TimeSpan TransactionTimeout", code);
    }

    [Fact]
    public void Finding750_Tikv_MetaAssignmentFixed()
    {
        var code = File.ReadAllText(Path.Combine(SpecializedDir, "TikvStrategy.cs"));
        Assert.Contains("StorageObjectMetadata? meta;", code);
        Assert.DoesNotContain("StorageObjectMetadata? meta = null;", code);
    }
}
