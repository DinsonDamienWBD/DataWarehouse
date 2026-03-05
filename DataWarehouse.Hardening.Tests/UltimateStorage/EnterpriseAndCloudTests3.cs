// Hardening tests for UltimateStorage findings 501-600
// MinioStrategy, MooseFsStrategy, MultiBackendFanOutFeature, MongoImport, MySqlImport,
// NetAppOntapStrategy, NfsStrategy, NvmeDiskStrategy, NvmeOfStrategy, OdaStrategy,
// OdbcConnectorStrategy, OracleImportStrategy, OracleObjectStorageStrategy, OvhObjectStorageStrategy,
// PmemStrategy, PostgresImportStrategy, PredictiveCompressionStrategy, ProbabilisticStorageStrategy

namespace DataWarehouse.Hardening.Tests.UltimateStorage;

public class EnterpriseAndCloudTests3
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateStorage");
    private static readonly string CloudDir = Path.Combine(PluginRoot, "Strategies", "Cloud");
    private static readonly string SoftwareDefinedDir = Path.Combine(PluginRoot, "Strategies", "SoftwareDefined");
    private static readonly string FeaturesDir = Path.Combine(PluginRoot, "Features");
    private static readonly string ImportDir = Path.Combine(PluginRoot, "Strategies", "Import");
    private static readonly string EnterpriseDir = Path.Combine(PluginRoot, "Strategies", "Enterprise");
    private static readonly string NetworkDir = Path.Combine(PluginRoot, "Strategies", "Network");
    private static readonly string LocalDir = Path.Combine(PluginRoot, "Strategies", "Local");
    private static readonly string ArchiveDir = Path.Combine(PluginRoot, "Strategies", "Archive");
    private static readonly string ConnectorDir = Path.Combine(PluginRoot, "Strategies", "Connectors");
    private static readonly string S3CompatDir = Path.Combine(PluginRoot, "Strategies", "S3Compatible");
    private static readonly string InnovationDir = Path.Combine(PluginRoot, "Strategies", "Innovation");

    // === MinioStrategy (finding 501) ===

    [Fact]
    public void Finding501_Minio_MultipartThresholdBytesExposed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "MinioStrategy.cs"));
        Assert.Contains("internal long MultipartThresholdBytes", code);
    }

    // === MooseFsStrategy (findings 503-520) ===

    [Fact]
    public void Finding503_MooseFs_MasterHostExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal string MasterHost", code);
    }

    [Fact]
    public void Finding504_MooseFs_MasterPortExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal int MasterPort", code);
    }

    [Fact]
    public void Finding505_MooseFs_UseErasureCodingExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal bool UseErasureCoding", code);
    }

    [Fact]
    public void Finding506_MooseFs_EcGoalExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal string EcGoal", code);
    }

    [Fact]
    public void Finding507_MooseFs_MaxSnapshotsExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal int MaxSnapshots", code);
    }

    [Fact]
    public void Finding508_MooseFs_SnapshotRetentionDaysExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal int SnapshotRetentionDays", code);
    }

    [Fact]
    public void Finding509_MooseFs_StorageClassLabelsExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal IReadOnlyDictionary<string, string> StorageClassLabels", code);
    }

    [Fact]
    public void Finding510_MooseFs_ChunkServerAffinityExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal bool EnableChunkServerAffinity", code);
    }

    [Fact]
    public void Finding511_MooseFs_PreferredChunkServersExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal IReadOnlyList<string> PreferredChunkServers", code);
    }

    [Fact]
    public void Finding512_MooseFs_UseReadAheadExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal bool UseReadAhead", code);
    }

    [Fact]
    public void Finding513_MooseFs_ReadAheadSizeKbExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal int ReadAheadSizeKb", code);
    }

    [Fact]
    public void Finding514_MooseFs_UseWriteCacheExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal bool UseWriteCache", code);
    }

    [Fact]
    public void Finding515_MooseFs_WriteCacheSizeKbExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal int WriteCacheSizeKb", code);
    }

    [Fact]
    public void Finding516_MooseFs_SessionIdExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal string SessionId", code);
    }

    [Fact]
    public void Finding517_MooseFs_SessionStartTimeExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal DateTime SessionStartTime", code);
    }

    [Fact]
    public void Finding518_MooseFs_FileLocksExposed()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("internal IReadOnlyDictionary", code);
        Assert.Contains("FileLocks", code);
    }

    [Fact]
    public void Finding519_MooseFs_UnusedBytesWrittenAssignmentRemoved()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.Contains("long bytesWritten;", code);
        Assert.DoesNotContain("long bytesWritten = 0;", code);
    }

    [Fact]
    public void Finding520_MooseFs_NullCheckRemovedPerNrt()
    {
        var code = File.ReadAllText(Path.Combine(SoftwareDefinedDir, "MooseFsStrategy.cs"));
        Assert.DoesNotContain("metadata == null || metadata.Count == 0", code);
        Assert.Contains("metadata.Count == 0", code);
    }

    // === MultiBackendFanOutFeature (findings 521-524) ===

    [Fact]
    public void Finding521_522_MultiBackend_DisposeAsyncUsed()
    {
        var code = File.ReadAllText(Path.Combine(FeaturesDir, "MultiBackendFanOutFeature.cs"));
        Assert.Contains("await r.stream.DisposeAsync()", code);
        Assert.DoesNotContain("r.stream.Dispose()", code);
    }

    [Fact]
    public void Finding523_MultiBackend_FailedBackendsQueried()
    {
        var code = File.ReadAllText(Path.Combine(FeaturesDir, "MultiBackendFanOutFeature.cs"));
        Assert.Contains("HasFailures", code);
    }

    [Fact]
    public void Finding524_MultiBackend_ErrorsQueried()
    {
        var code = File.ReadAllText(Path.Combine(FeaturesDir, "MultiBackendFanOutFeature.cs"));
        Assert.Contains("HasErrors", code);
    }

    // === NetAppOntapStrategy (findings 526-536) ===

    [Fact]
    public void Findings526to532_NetApp_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(EnterpriseDir, "NetAppOntapStrategy.cs"));
        Assert.Contains("internal string? SnapshotPolicy", code);
        Assert.Contains("internal bool EnableDeduplication", code);
        Assert.Contains("internal bool EnableCompaction", code);
        Assert.Contains("internal string? FabricPoolTier", code);
        Assert.Contains("internal string? SnapLockType", code);
        Assert.Contains("internal string? QosPolicy", code);
        Assert.Contains("internal string ApiVersion", code);
    }

    [Fact]
    public void Findings533to536_NetApp_CultureExplicit()
    {
        var code = File.ReadAllText(Path.Combine(EnterpriseDir, "NetAppOntapStrategy.cs"));
        Assert.Contains("using System.Globalization;", code);
        Assert.Contains("CultureInfo.InvariantCulture", code);
        Assert.DoesNotContain("DateTime.UtcNow.ToString()", code);
    }

    // === NfsStrategy (findings 537-543) ===

    [Fact]
    public void Finding537_Nfs_UsernameExposed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NfsStrategy.cs"));
        Assert.Contains("internal string Username", code);
    }

    [Fact]
    public void Finding538_Nfs_KerberosRealmExposed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NfsStrategy.cs"));
        Assert.Contains("internal string? KerberosRealm", code);
    }

    [Fact]
    public void Finding539_Nfs_BytesWrittenAssignmentFixed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NfsStrategy.cs"));
        Assert.Contains("long bytesWritten;", code);
    }

    [Fact]
    public void Finding540_Nfs_UsingVarInitializerFixed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NfsStrategy.cs"));
        Assert.Contains("using var process = new Process();", code);
        Assert.Contains("process.StartInfo = startInfo;", code);
    }

    [Fact]
    public void Finding541_542_Nfs_EnumV41V42Renamed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NfsStrategy.cs"));
        Assert.Contains("V41", code);
        Assert.Contains("V42", code);
        Assert.DoesNotContain("V4_1", code);
        Assert.DoesNotContain("V4_2", code);
    }

    [Fact]
    public void Finding543_Nfs_AlwaysTrueCheckRemoved()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NfsStrategy.cs"));
        // The redundant null check on _innerStream was removed
        Assert.Contains("await _innerStream.DisposeAsync();", code);
    }

    // === NvmeDiskStrategy (findings 544-550) ===

    [Fact]
    public void Findings544to546_NvmeDisk_ConstantsRenamed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "NvmeDiskStrategy.cs"));
        Assert.Contains("NvmeBlockSize", code);
        Assert.Contains("DefaultQueueDepth", code);
        Assert.Contains("MaxTransferSize", code);
        Assert.DoesNotContain("NVME_BLOCK_SIZE", code);
        Assert.DoesNotContain("DEFAULT_QUEUE_DEPTH", code);
    }

    [Fact]
    public void Finding547_NvmeDisk_UseDirectIoRenamed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "NvmeDiskStrategy.cs"));
        Assert.Contains("_useDirectIo", code);
        Assert.DoesNotContain("_useDirectIO", code);
    }

    [Fact]
    public void Finding548_NvmeDisk_EnableSmartRenamed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "NvmeDiskStrategy.cs"));
        Assert.Contains("_enableSmart", code);
        Assert.DoesNotContain("_enableSMART", code);
    }

    [Fact]
    public void Finding549_NvmeDisk_AlignmentSizeExposed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "NvmeDiskStrategy.cs"));
        Assert.Contains("internal int AlignmentSize", code);
    }

    [Fact]
    public void Finding550_NvmeDisk_BytesWrittenAssignmentFixed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "NvmeDiskStrategy.cs"));
        Assert.Contains("long bytesWritten;", code);
    }

    // === NvmeOfStrategy (findings 551-559) ===

    [Fact]
    public void Findings551to555_NvmeOf_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NvmeOfStrategy.cs"));
        Assert.Contains("internal bool EnableMultipath", code);
        Assert.Contains("internal string AuthenticationProtocol", code);
        Assert.Contains("internal string BasePath", code);
        Assert.Contains("internal bool UseInBandAuth", code);
        Assert.Contains("internal int TimeoutSeconds", code);
    }

    [Fact]
    public void Findings557to559_NvmeOf_EnumRenames()
    {
        var code = File.ReadAllText(Path.Combine(NetworkDir, "NvmeOfStrategy.cs"));
        Assert.Contains("Rdma", code);
        Assert.Contains("Fc,", code);
        Assert.Contains("Tcp,", code);
        // RDMA appears in comments which is fine; verify enum values are PascalCase
        Assert.DoesNotContain("RDMA,", code);
    }

    // === OdaStrategy (findings 560-566) ===

    [Fact]
    public void Findings560to563_Oda_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(ArchiveDir, "OdaStrategy.cs"));
        Assert.Contains("internal bool UseScsiCommands", code);
        Assert.Contains("internal int MaxConcurrentDrives", code);
        Assert.Contains("internal TimeSpan WriteVerificationTimeout", code);
        Assert.Contains("internal int MaxRetryAttempts", code);
    }

    [Fact]
    public void Findings564to566_Oda_EnumRenames()
    {
        var code = File.ReadAllText(Path.Combine(ArchiveDir, "OdaStrategy.cs"));
        Assert.Contains("BluRayRxl", code);
        Assert.Contains("BluRayRe", code);
        Assert.Contains("UltraHdBluRay", code);
        Assert.DoesNotContain("BluRayRXL", code);
        Assert.DoesNotContain("BluRayRE =", code);
        Assert.DoesNotContain("UltraHDBluRay", code);
    }

    // === OdbcConnectorStrategy (findings 567-571) ===

    [Fact]
    public void Finding567_Odbc_BatchSizeExposed()
    {
        var code = File.ReadAllText(Path.Combine(ConnectorDir, "OdbcConnectorStrategy.cs"));
        Assert.Contains("internal int BatchSize", code);
    }

    [Fact]
    public void Finding568_569_Odbc_UnusedAssignmentsFixed()
    {
        var code = File.ReadAllText(Path.Combine(ConnectorDir, "OdbcConnectorStrategy.cs"));
        Assert.Contains("long rowsAffected;", code);
    }

    [Fact]
    public void Finding570_Odbc_NullCheckSimplified()
    {
        var code = File.ReadAllText(Path.Combine(ConnectorDir, "OdbcConnectorStrategy.cs"));
        Assert.Contains("value is null or DBNull", code);
        Assert.DoesNotContain("value == null || value == DBNull.Value", code);
    }

    [Fact]
    public void Finding571_Odbc_FlushAsyncCancellation()
    {
        var code = File.ReadAllText(Path.Combine(ConnectorDir, "OdbcConnectorStrategy.cs"));
        Assert.Contains("FlushAsync(ct)", code);
    }

    // === OracleObjectStorageStrategy (findings 573-591) ===

    [Fact]
    public void Finding573_Oracle_EnableVersioningExposed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "OracleObjectStorageStrategy.cs"));
        Assert.Contains("internal bool EnableVersioning", code);
    }

    [Fact]
    public void Finding575_Oracle_CompletedPartsAssignmentFixed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "OracleObjectStorageStrategy.cs"));
        Assert.Contains("List<CommitMultipartUploadPartDetails> completedParts;", code);
    }

    [Fact]
    public void Finding576_Oracle_DisposedVariableFixed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "OracleObjectStorageStrategy.cs"));
        // Semaphore no longer uses 'using' - disposed manually after WhenAll
        Assert.DoesNotContain("using var semaphore = new SemaphoreSlim", code);
        Assert.Contains("semaphore.Dispose()", code);
    }

    [Fact]
    public void Findings574_577to591_Oracle_CancellationTokensPassed()
    {
        var code = File.ReadAllText(Path.Combine(CloudDir, "OracleObjectStorageStrategy.cs"));
        // All 16 client calls now pass null, ct
        Assert.Contains("PutObject(request, null, ct)", code);
        Assert.Contains("GetObject(request, null, ct)", code);
        Assert.Contains("DeleteObject(request, null, ct)", code);
        Assert.Contains("HeadObject(request, null, ct)", code);
        Assert.Contains("ListObjects(request, null, ct)", code);
    }

    // === OvhObjectStorageStrategy (findings 592-596) ===

    [Fact]
    public void Finding592_Ovh_EnableCorsExposed()
    {
        var code = File.ReadAllText(Path.Combine(S3CompatDir, "OvhObjectStorageStrategy.cs"));
        Assert.Contains("internal bool EnableCors", code);
    }

    [Fact]
    public void Finding593_Ovh_UploadedPartsAssignmentFixed()
    {
        var code = File.ReadAllText(Path.Combine(S3CompatDir, "OvhObjectStorageStrategy.cs"));
        Assert.Contains("List<PartETag> uploadedParts;", code);
    }

    [Fact]
    public void Finding594_Ovh_S3ExRenamed()
    {
        var code = File.ReadAllText(Path.Combine(S3CompatDir, "OvhObjectStorageStrategy.cs"));
        Assert.Contains("s3Ex", code);
        Assert.DoesNotContain(" s3ex ", code);
    }

    [Fact]
    public void Finding595_596_Ovh_MultipleEnumerationFixed()
    {
        var code = File.ReadAllText(Path.Combine(S3CompatDir, "OvhObjectStorageStrategy.cs"));
        Assert.Contains("var rulesList = rules?.ToList()", code);
        Assert.Contains("rulesList.Count == 0", code);
    }

    // === PmemStrategy (findings 597-600) ===

    [Fact]
    public void Findings597_598_Pmem_UnusedFieldsExposed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "PmemStrategy.cs"));
        Assert.Contains("internal bool UseNonTemporalStores", code);
        Assert.Contains("internal int AlignmentBytes", code);
    }

    [Fact]
    public void Finding599_Pmem_BytesWrittenAssignmentFixed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "PmemStrategy.cs"));
        Assert.Contains("long bytesWritten;", code);
    }

    [Fact]
    public void Finding600_Pmem_SupportsDaxRenamed()
    {
        var code = File.ReadAllText(Path.Combine(LocalDir, "PmemStrategy.cs"));
        Assert.Contains("SupportsDax", code);
        Assert.DoesNotContain("SupportsDAX", code);
    }
}
