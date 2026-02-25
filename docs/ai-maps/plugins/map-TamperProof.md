# Plugin: TamperProof
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.TamperProof

### File: Plugins/DataWarehouse.Plugins.TamperProof/IAccessLogProvider.cs
```csharp
public interface IAccessLogProvider
{
}
    Task LogAccessAsync(AccessLog log, CancellationToken ct = default);;
    Task<IReadOnlyList<AccessLog>> QueryAccessLogsAsync(Guid objectId, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null, CancellationToken ct = default);;
    Task<IReadOnlyList<AccessLog>> QueryAccessLogsByPrincipalAsync(string principal, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/IPipelineOrchestrator.cs
```csharp
public interface IPipelineOrchestrator
{
}
    Task<Stream> ApplyPipelineAsync(Stream data, CancellationToken ct = default);;
    Task<Stream> ReversePipelineAsync(Stream transformedData, CancellationToken ct = default);;
    IReadOnlyList<string> GetConfiguredStages();;
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/TamperProofPlugin.cs
```csharp
public class TamperProofPlugin : IntegrityPluginBase, IDisposable
{
#endregion
}
    public TamperProofPlugin(TamperProofConfiguration config, IIntegrityProvider integrity, IBlockchainProvider blockchain, IWormStorageProvider worm, IAccessLogProvider accessLog, IPipelineOrchestrator pipelineOrchestrator, IStorageProvider dataStorage, IStorageProvider metadataStorage, IStorageProvider wormStorage, IStorageProvider blockchainStorage, ILogger<TamperIncidentService> incidentServiceLogger, ILogger<BlockchainVerificationService> blockchainVerificationLogger, ILogger<RecoveryService> recoveryServiceLogger, ILogger<BackgroundIntegrityScanner> backgroundScannerLogger, ILogger<TamperProofPlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public async Task<SecureWriteResult> ExecuteWritePipelineAsync(Stream data, WriteContext writeContext, CancellationToken ct = default);
    public async Task<SecureReadResult> ExecuteReadPipelineAsync(Guid objectId, int? version, ReadMode readMode, CancellationToken ct = default);
    public async Task<TamperIncidentReport?> GetTamperIncidentAsync(Guid objectId, CancellationToken ct = default);
    public async Task<IReadOnlyList<TamperIncidentReport>> GetTamperIncidentsAsync(Guid objectId, CancellationToken ct = default);
    public async Task<IReadOnlyList<TamperIncidentReport>> QueryTamperIncidentsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    public async Task<TamperIncidentStatistics> GetIncidentStatisticsAsync(CancellationToken ct = default);
    public async Task<AdvancedRecoveryResult> RecoverFromWormAsync(Guid objectId, int? version, CancellationToken ct = default);
    public async Task<bool> ValidateBlockchainIntegrityAsync(CancellationToken ct = default);
    public async Task<AuditChain> GetAuditChainAsync(Guid objectId, CancellationToken ct = default);
    public IBackgroundIntegrityScanner BackgroundScanner;;
    public async Task StartBackgroundScannerAsync(CancellationToken ct = default);
    public async Task StopBackgroundScannerAsync(CancellationToken ct = default);
    public ScannerStatus GetBackgroundScannerStatus();
    public async Task<ScanResult> ScanBlockIntegrityAsync(Guid blockId, CancellationToken ct = default);
    public async Task<FullScanResult> RunFullIntegrityScanAsync(CancellationToken ct = default);
    protected override Dictionary<string, object> GetMetadata();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    public IReadOnlyList<TimeLockProviderPluginBase>? TimeLockProviders;;
    public RansomwareVaccinationService? VaccinationService;;
    public TimeLockPolicyEngine? PolicyEngine;;
    public override async Task OnMessageAsync(PluginMessage message);
    protected override void Dispose(bool disposing);
    protected override async ValueTask DisposeAsyncCore();
    public override async Task<Dictionary<string, object>> VerifyAsync(string key, CancellationToken ct = default);
    public override async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/IWormStorageProvider.cs
```csharp
public interface IWormStorageProvider
{
}
    Task<PluginWormWriteResult> WriteAsync(PluginWormWriteRequest request, CancellationToken ct = default);;
    Task<byte[]?> ReadAsync(string recordId, CancellationToken ct = default);;
    Task<bool> ExistsAsync(string recordId, CancellationToken ct = default);;
    Task<WormRetentionPolicy?> GetRetentionAsync(string recordId, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/TimeLock/CloudTimeLockProvider.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 59: Ransomware vaccination")]
public sealed class CloudTimeLockProvider : TimeLockProviderPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override TimeLockMode DefaultMode;;
    public override TimeLockPolicy Policy { get; };
    protected override async Task<TimeLockResult> LockInternalAsync(TimeLockRequest request, CancellationToken ct);
    protected override async Task ExtendLockInternalAsync(Guid objectId, TimeSpan additionalDuration, CancellationToken ct);
    protected override async Task<bool> AttemptUnlockInternalAsync(Guid objectId, UnlockCondition condition, CancellationToken ct);
    public override Task<TimeLockStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);
    public override Task<bool> IsLockedAsync(Guid objectId, CancellationToken ct = default);
    public override Task<RansomwareVaccinationInfo> GetVaccinationInfoAsync(Guid objectId, CancellationToken ct = default);
    public override Task<IReadOnlyList<TimeLockStatus>> ListLockedObjectsAsync(int limit, int offset, CancellationToken ct = default);
}
```
```csharp
private static class CloudProviders
{
}
    public const string AwsS3 = "aws-s3-object-lock";
    public const string AzureBlob = "azure-immutable-blob";
    public const string GcsRetention = "gcs-retention-policy";
    public const string Auto = "auto-detect";
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/TimeLock/HsmTimeLockProvider.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 59: Ransomware vaccination")]
public sealed class HsmTimeLockProvider : TimeLockProviderPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override TimeLockMode DefaultMode;;
    public override TimeLockPolicy Policy { get; };
    protected override async Task<TimeLockResult> LockInternalAsync(TimeLockRequest request, CancellationToken ct);
    protected override async Task ExtendLockInternalAsync(Guid objectId, TimeSpan additionalDuration, CancellationToken ct);
    protected override async Task<bool> AttemptUnlockInternalAsync(Guid objectId, UnlockCondition condition, CancellationToken ct);
    public override Task<TimeLockStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);
    public override Task<bool> IsLockedAsync(Guid objectId, CancellationToken ct = default);
    public override Task<RansomwareVaccinationInfo> GetVaccinationInfoAsync(Guid objectId, CancellationToken ct = default);
    public override Task<IReadOnlyList<TimeLockStatus>> ListLockedObjectsAsync(int limit, int offset, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/TimeLock/TimeLockPolicyEngine.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 59: Time-lock engine")]
public sealed class TimeLockPolicyEngine
{
}
    public IReadOnlyList<TimeLockRule> Rules;;
    public TimeLockPolicyEngine();
    public TimeLockPolicy EvaluatePolicy(string? dataClassification, string? complianceFramework, string? contentType);
    public (TimeLockPolicy Policy, string RuleName) GetEffectivePolicy(string? dataClassification, string? complianceFramework, string? contentType);
    public void AddRule(TimeLockRule rule);
    public bool RemoveRule(string ruleName);
}
```
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 59: Time-lock engine")]
public sealed record TimeLockRule
{
}
    public required string Name { get; init; }
    public required int Priority { get; init; }
    public string? DataClassificationPattern { get; init; }
    public required string[] ComplianceFrameworks { get; init; }
    public required string[] ContentTypePatterns { get; init; }
    public required TimeSpan MinLockDuration { get; init; }
    public required TimeSpan MaxLockDuration { get; init; }
    public required TimeSpan DefaultLockDuration { get; init; }
    public required VaccinationLevel VaccinationLevel { get; init; }
    public required bool RequireMultiPartyUnlock { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/TimeLock/RansomwareVaccinationService.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 59: Ransomware vaccination")]
public sealed class RansomwareVaccinationService
{
#endregion
}
    public RansomwareVaccinationService(IMessageBus messageBus);
    public async Task<RansomwareVaccinationInfo> VaccinateAsync(Guid objectId, VaccinationLevel level, TimeLockPolicy policy, CancellationToken ct = default);
    public async Task<RansomwareVaccinationInfo> VerifyVaccinationAsync(Guid objectId, CancellationToken ct = default);
    public async IAsyncEnumerable<RansomwareVaccinationInfo> ScanAllAsync(int batchSize, [EnumeratorCancellation] CancellationToken ct = default);
    public Task<Dictionary<string, object>> GetThreatDashboardAsync(CancellationToken ct = default);
    internal static double CalculateThreatScore(bool timeLockActive, bool integrityVerified, bool? pqcSignatureValid, bool? blockchainAnchored);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/TimeLock/TimeLockMessageBusIntegration.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 59: Time-lock engine")]
public static class TimeLockMessageBusIntegration
{
}
    public const string TimeLockLocked = "timelock.locked";
    public const string TimeLockUnlocked = "timelock.unlocked";
    public const string TimeLockExtended = "timelock.extended";
    public const string TimeLockTamperDetected = "timelock.tamper.detected";
    public const string TimeLockVaccinationScan = "timelock.vaccination.scan";
    public const string TimeLockPolicyEvaluated = "timelock.policy.evaluated";
    public static async Task PublishLockEventAsync(IMessageBus bus, TimeLockResult result, CancellationToken ct = default);
    public static async Task PublishUnlockEventAsync(IMessageBus bus, Guid objectId, string lockId, UnlockConditionType reason, CancellationToken ct = default);
    public static async Task PublishExtendEventAsync(IMessageBus bus, Guid objectId, TimeSpan newDuration, CancellationToken ct = default);
    public static async Task PublishTamperDetectedEventAsync(IMessageBus bus, Guid objectId, string details, CancellationToken ct = default);
    public static async Task PublishVaccinationScanEventAsync(IMessageBus bus, Guid objectId, RansomwareVaccinationInfo vaccinationInfo, CancellationToken ct = default);
    public static async Task PublishPolicyEvaluatedEventAsync(IMessageBus bus, string? dataClassification, string? complianceFramework, string? contentType, string selectedRuleName, TimeLockPolicy policy, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/TimeLock/SoftwareTimeLockProvider.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 59: Time-lock engine")]
public sealed class SoftwareTimeLockProvider : TimeLockProviderPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override TimeLockMode DefaultMode;;
    public override TimeLockPolicy Policy { get; };
    protected override async Task<TimeLockResult> LockInternalAsync(TimeLockRequest request, CancellationToken ct);
    protected override async Task ExtendLockInternalAsync(Guid objectId, TimeSpan additionalDuration, CancellationToken ct);
    protected override async Task<bool> AttemptUnlockInternalAsync(Guid objectId, UnlockCondition condition, CancellationToken ct);
    public override Task<TimeLockStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);
    public override Task<bool> IsLockedAsync(Guid objectId, CancellationToken ct = default);
    public override Task<RansomwareVaccinationInfo> GetVaccinationInfoAsync(Guid objectId, CancellationToken ct = default);
    public override Task<IReadOnlyList<TimeLockStatus>> ListLockedObjectsAsync(int limit, int offset, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Registration/TimeLockRegistration.cs
```csharp
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock integration")]
public static class TimeLockRegistration
{
}
    public static async Task<IReadOnlyList<TimeLockProviderPluginBase>> RegisterTimeLockProviders(IMessageBus bus, string sourcePluginId = "com.datawarehouse.tamperproof");
    public static async Task<RansomwareVaccinationService> RegisterVaccinationService(IMessageBus bus, string sourcePluginId = "com.datawarehouse.tamperproof");
    public static TimeLockPolicyEngine RegisterPolicyEngine();
    public static async Task PublishTimeLockCapabilities(IMessageBus bus, int providerCount = 3, string sourcePluginId = "com.datawarehouse.tamperproof");
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Scaling/TamperProofScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-09: TamperProof scaling with per-tier locks, bounded caches, RAID shards, scan throttling")]
public sealed class TamperProofScalingManager : IScalableSubsystem, IBackpressureAware, IDisposable
{
}
    public const int DefaultHashCacheCapacity = 500_000;
    public const int DefaultManifestCacheCapacity = 50_000;
    public const int DefaultVerificationCacheCapacity = 100_000;
    public static readonly TimeSpan DefaultVerificationTtl = TimeSpan.FromMinutes(10);
    public const long DefaultScanThroughputBytesPerSecond = 100L * 1024 * 1024;
    public const int DefaultSmallShardCount = 3;
    public const int DefaultMediumShardCount = 6;
    public const int DefaultLargeShardCount = 12;
    public static readonly IReadOnlyList<string> StandardTiers = new[]
{
    "WORM",
    "hot",
    "warm",
    "cold",
    "archive"
};
    public BackpressureStrategy Strategy { get; set; };
    public event Action<BackpressureStateChangedEventArgs>? OnBackpressureChanged;
    public TamperProofScalingManager(ScalingLimits? initialLimits = null, int hashCacheCapacity = DefaultHashCacheCapacity, int manifestCacheCapacity = DefaultManifestCacheCapacity, int verificationCacheCapacity = DefaultVerificationCacheCapacity, TimeSpan? verificationTtl = null, long scanThroughputBytesPerSecond = DefaultScanThroughputBytesPerSecond, int? maxConcurrentPerTier = null);
    public async Task<T> ExecuteOnTierAsync<T>(string tierName, Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
    public async Task ExecuteOnTierAsync(string tierName, Func<CancellationToken, Task> operation, CancellationToken ct = default);
    public void CacheHash(string dataKey, byte[] hash);
    public byte[]? GetCachedHash(string dataKey);
    public void CacheManifest(string manifestKey, byte[] manifest);
    public byte[]? GetCachedManifest(string manifestKey);
    public void CacheVerification(string verificationKey, bool isValid);
    public bool TryGetCachedVerification(string verificationKey, out bool result);
    public int GetRecommendedShardCount(long dataSizeBytes);
    public int SmallShardCount { get => _smallShardCount; set => _smallShardCount = Math.Max(1, value); }
    public int MediumShardCount { get => _mediumShardCount; set => _mediumShardCount = Math.Max(1, value); }
    public int LargeShardCount { get => _largeShardCount; set => _largeShardCount = Math.Max(1, value); }
    public async Task<long> ExecuteThrottledScanAsync(Func<CancellationToken, Task<long>> scanOperation, CancellationToken ct = default);
    public long ScanThroughputLimit { get => Interlocked.Read(ref _scanThroughputLimit); set => Interlocked.Exchange(ref _scanThroughputLimit, Math.Max(1, value)); }
    public long TotalScanBytesProcessed;;
    public BackpressureState CurrentState;;
    public Task ApplyBackpressureAsync(BackpressureContext context, CancellationToken ct = default);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits
{
    get
    {
        lock (_configLock)
        {
            return _currentLimits;
        }
    }
}
    public BackpressureState CurrentBackpressureState
{
    get
    {
        var hashStats = _hashCache.GetStatistics();
        var manifestStats = _manifestCache.GetStatistics();
        long totalItems = hashStats.ItemCount + manifestStats.ItemCount;
        long totalCapacity = DefaultHashCacheCapacity + DefaultManifestCacheCapacity;
        double utilization = totalCapacity > 0 ? (double)totalItems / totalCapacity : 0;
        return utilization switch
        {
            >= 0.85 => BackpressureState.Critical,
            >= 0.50 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }
}
    public void Dispose();
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Pipeline/WritePhaseHandlers.cs
```csharp
public static class WritePhaseHandlers
{
}
    public static async Task VerifyNotSealedAsync(Guid objectId, ISealService? sealService, ILogger logger, CancellationToken ct);
    public static async Task VerifyShardsNotSealedAsync(Guid objectId, IEnumerable<int> shardIndices, ISealService? sealService, ILogger logger, CancellationToken ct);
    public static async Task<(byte[] transformedData, List<PipelineStageRecord> stages)> ApplyUserTransformationsAsync(Stream data, IPipelineOrchestrator orchestrator, ILogger logger, CancellationToken ct);
    public static (byte[] paddedData, ContentPaddingRecord paddingRecord) ApplyContentPadding(byte[] data, ContentPaddingConfig config, ILogger logger);
    public static async Task<IntegrityHash> ComputeIntegrityHashAsync(byte[] data, DataWarehouse.SDK.Contracts.TamperProof.HashAlgorithmType algorithm, IIntegrityProvider integrity, ILogger logger, CancellationToken ct);
    public static async Task<(List<byte[]> shards, RaidRecord raidConfig)> PerformRaidShardingAsync(byte[] data, Guid objectId, RaidConfig raidConfig, IIntegrityProvider integrity, ILogger logger, CancellationToken ct);
    public static async Task<TransactionResult> ExecuteTransactionalWriteAsync(Guid objectId, TamperProofManifest manifest, List<byte[]> shards, byte[] fullData, IStorageProvider dataStorage, IStorageProvider metadataStorage, IWormStorageProvider worm, TamperProofConfiguration config, ISealService? sealService, ILogger logger, CancellationToken ct);
    public static Task<TransactionResult> ExecuteTransactionalWriteAsync(Guid objectId, TamperProofManifest manifest, List<byte[]> shards, byte[] fullData, IStorageProvider dataStorage, IStorageProvider metadataStorage, IWormStorageProvider worm, TamperProofConfiguration config, ILogger logger, CancellationToken ct);
    public static async Task ValidateRetentionBeforeDeletionAsync(Guid blockId, IRetentionPolicyService retentionService, ILogger logger, CancellationToken ct);
    public static async Task<bool> HasActiveLegalHoldsAsync(Guid blockId, IRetentionPolicyService retentionService, CancellationToken ct);
    public static async Task<(bool Allowed, string? BlockedReason)> CanRollbackDeleteAsync(Guid objectId, IRetentionPolicyService? retentionService, ILogger logger, CancellationToken ct);
}
```
```csharp
public class RetentionPolicyBlockedException : InvalidOperationException
{
}
    public Guid BlockId { get; }
    public DeletionBlockedReason BlockedReason { get; }
    public string BlockedDetails { get; }
    public IReadOnlyList<RetentionLegalHold>? ActiveLegalHolds { get; }
    public Services.RetentionPolicy? RetentionPolicy { get; }
    public RetentionPolicyBlockedException(Guid blockId, DeletionBlockedReason reason, string details, IReadOnlyList<RetentionLegalHold>? legalHolds = null, Services.RetentionPolicy? retentionPolicy = null) : base($"Deletion blocked for block {blockId}: {details}");
}
```
```csharp
public class PluginWormWriteRequest
{
}
    public required Guid ObjectId { get; init; }
    public required int Version { get; init; }
    public required byte[] Data { get; init; }
    public required WormRetentionPolicy RetentionPolicy { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public class PluginWormWriteResult
{
}
    public required bool Success { get; init; }
    public required string RecordId { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public class VersionIndexRecord
{
}
    public required Guid ObjectId { get; init; }
    public required int LatestVersion { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public List<int>? AvailableVersions { get; init; }
}
```
```csharp
public static class WriteAuditTrailExtensions
{
}
    public static async Task<TamperProofAuditEntry> LogCreationAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string dataHash, string? userId, string? details = null, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogModificationAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string dataHash, string? userId, string? details = null, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogShardsWrittenAsync(this IAuditTrailService auditTrail, Guid objectId, int shardCount, int dataShards, int parityShards, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogWormBackupCreatedAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string wormRecordId, string dataHash, TimeSpan retentionPeriod, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogBlockchainAnchoredAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string anchorId, string dataHash, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogManifestUpdatedAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string manifestHash, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogSecureCorrectionAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string dataHash, string? userId, string reason, string? authorizationId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogSealedAsync(this IAuditTrailService auditTrail, Guid objectId, string? userId, string reason, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Pipeline/ReadPhaseHandlers.cs
```csharp
public static class ReadPhaseHandlers
{
}
    public static async Task<SealStatusInfo?> GetSealStatusAsync(Guid objectId, ISealService? sealService, ILogger logger, CancellationToken ct);
    public static async Task<TamperProofManifest?> LoadManifestAsync(Guid objectId, int? version, IStorageProvider metadataStorage, ILogger logger, CancellationToken ct);
    public static async Task<byte[]> LoadAndReconstructShardsAsync(TamperProofManifest manifest, IStorageProvider dataStorage, ILogger logger, CancellationToken ct);
    public static async Task<ShardReconstructionResult> LoadAndReconstructShardsWithVerificationAsync(TamperProofManifest manifest, IStorageProvider dataStorage, ILogger logger, CancellationToken ct);
    public static async Task<IntegrityVerificationResult> VerifyIntegrityAsync(byte[] data, TamperProofManifest manifest, ReadMode readMode, IIntegrityProvider integrity, ILogger logger, CancellationToken ct);
    public static async Task<RecoveryResult> RecoverFromWormAsync(TamperProofManifest manifest, IWormStorageProvider worm, IStorageProvider dataStorage, ILogger logger, CancellationToken ct);
    public static async Task<byte[]> ReversePipelineTransformationsAsync(byte[] transformedData, TamperProofManifest manifest, IPipelineOrchestrator orchestrator, ILogger logger, CancellationToken ct);
    public static async Task<BlockchainVerificationResult> VerifyBlockchainAnchorAsync(TamperProofManifest manifest, BlockchainVerificationService blockchainService, ILogger logger, CancellationToken ct);
    public static async Task<AuditChain> GetAuditChainAsync(Guid objectId, BlockchainVerificationService blockchainService, ILogger logger, CancellationToken ct);
    public static Task<ComprehensiveVerificationResult> VerifyIntegrityWithBlockchainAsync(byte[] data, TamperProofManifest manifest, ReadMode readMode, IIntegrityProvider integrity, BlockchainVerificationService? blockchainService, ILogger logger, CancellationToken ct);
    public static async Task<ComprehensiveVerificationResult> VerifyIntegrityWithBlockchainAsync(byte[] data, TamperProofManifest manifest, ReadMode readMode, IIntegrityProvider integrity, BlockchainVerificationService? blockchainService, ISealService? sealService, ILogger logger, CancellationToken ct);
}
```
```csharp
public class ComprehensiveVerificationResult
{
}
    public required IntegrityVerificationResult IntegrityResult { get; init; }
    public BlockchainVerificationResult? BlockchainResult { get; init; }
    public required ReadMode ReadMode { get; init; }
    public required bool OverallValid { get; init; }
    public SealStatusInfo? SealStatus { get; init; }
    public string Summary
{
    get
    {
        var parts = new List<string>();
        if (IntegrityResult.IntegrityValid)
            parts.Add("Integrity: VALID");
        else
            parts.Add($"Integrity: FAILED ({IntegrityResult.ErrorMessage})");
        if (BlockchainResult != null)
        {
            if (BlockchainResult.Success)
                parts.Add($"Blockchain: VALID (Block {BlockchainResult.BlockNumber})");
            else
                parts.Add($"Blockchain: FAILED ({BlockchainResult.ErrorMessage})");
        }

        if (SealStatus?.IsSealed == true)
            parts.Add($"Seal: SEALED (since {SealStatus.SealedAt:O})");
        return string.Join("; ", parts);
    }
}
}
```
```csharp
public class SealStatusInfo
{
}
    public required bool IsSealed { get; init; }
    public DateTime? SealedAt { get; init; }
    public string? Reason { get; init; }
    public string? SealedBy { get; init; }
    public string? SealToken { get; init; }
    public static SealStatusInfo NotSealed();;
    public static SealStatusInfo Sealed(DateTime sealedAt, string reason, string sealedBy, string sealToken);;
}
```
```csharp
public class ShardLoadResult
{
}
    public required int ShardIndex { get; init; }
    public byte[]? Data { get; init; }
    public required bool LoadedSuccessfully { get; init; }
    public required bool IntegrityValid { get; init; }
    public string? ExpectedHash { get; init; }
    public string? ActualHash { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public class ShardReconstructionResult
{
}
    public required bool Success { get; init; }
    public byte[]? Data { get; init; }
    public required IReadOnlyList<ShardVerificationResult> ShardResults { get; init; }
    public required IReadOnlyList<int> CorruptedShards { get; init; }
    public required IReadOnlyList<int> MissingShards { get; init; }
    public required IReadOnlyList<int> ReconstructedShards { get; init; }
    public string? ErrorMessage { get; init; }
    public bool ReconstructionPerformed;;
    public int ProblematicShardCount;;
}
```
```csharp
public class VersionIndex
{
}
    public required Guid ObjectId { get; init; }
    public required int LatestVersion { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public List<int>? AvailableVersions { get; init; }
}
```
```csharp
public static class ReadAuditTrailExtensions
{
}
    public static async Task<TamperProofAuditEntry> LogReadAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string? userId, ReadMode readMode, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogVerificationAsync(this IAuditTrailService auditTrail, Guid objectId, int version, bool isValid, string dataHash, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogCorruptionDetectedAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string expectedHash, string actualHash, IReadOnlyList<int>? affectedShards, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogRecoveryAttemptedAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string recoverySource, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogRecoverySucceededAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string recoverySource, string restoredHash, int shardsRestored, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogRecoveryFailedAsync(this IAuditTrailService auditTrail, Guid objectId, int version, string recoverySource, string errorMessage, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogLegalHoldAppliedAsync(this IAuditTrailService auditTrail, Guid objectId, string holdId, string reason, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogLegalHoldReleasedAsync(this IAuditTrailService auditTrail, Guid objectId, string holdId, string? reason, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogRetentionPolicyAppliedAsync(this IAuditTrailService auditTrail, Guid objectId, int retentionDays, DateTime expiryDate, string? userId, CancellationToken ct = default);
    public static async Task<TamperProofAuditEntry> LogDeletedAsync(this IAuditTrailService auditTrail, Guid objectId, int? version, string? userId, string? reason, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/OrphanCleanupService.cs
```csharp
internal class OrphanCleanupService : IDisposable, IAsyncDisposable
{
}
    public OrphanCleanupService(ILogger<OrphanCleanupService> logger, TimeSpan? scanInterval = null);
    public Task StartAsync(CancellationToken ct = default);
    public async Task StopAsync(CancellationToken ct = default);
    public void RegisterOrphan(OrphanedWormRecord orphan);
    public void RegisterSuccessfulTransaction(string contentHash, Guid transactionId, Guid objectId);
    public OrphanCleanupStatus GetStatus();
    public async Task<int> ProcessOrphansAsync(CancellationToken ct = default);
    public void Dispose();
    protected virtual void Dispose(bool disposing);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/RetentionPolicyService.cs
```csharp
public interface IRetentionPolicyService
{
}
    Task<RetentionPolicy> SetPolicyAsync(Guid blockId, RetentionPolicy policy, CancellationToken ct = default);;
    Task<RetentionPolicy?> GetPolicyAsync(Guid blockId, CancellationToken ct = default);;
    Task<bool> IsRetainedAsync(Guid blockId, CancellationToken ct = default);;
    Task<LegalHoldResult> ApplyLegalHoldAsync(Guid blockId, LegalHoldRequest request, CancellationToken ct = default);;
    Task<LegalHoldResult> ReleaseLegalHoldAsync(Guid blockId, string holdId, string authorizedBy, CancellationToken ct = default);;
    Task<IReadOnlyList<RetentionLegalHold>> GetActiveLegalHoldsAsync(Guid blockId, CancellationToken ct = default);;
    Task<RetentionReport> GetRetentionReportAsync(CancellationToken ct = default);;
    Task<DeletionValidationResult> ValidateDeletionAsync(Guid blockId, CancellationToken ct = default);;
    Task<RetentionPolicy> ExtendRetentionAsync(Guid blockId, DateTime newRetainUntil, string extendedBy, CancellationToken ct = default);;
}
```
```csharp
public class RetentionPolicyService : IRetentionPolicyService
{
}
    public RetentionPolicyService(ILogger<RetentionPolicyService> logger);
    public Task<RetentionPolicy> SetPolicyAsync(Guid blockId, RetentionPolicy policy, CancellationToken ct = default);
    public Task<RetentionPolicy?> GetPolicyAsync(Guid blockId, CancellationToken ct = default);
    public async Task<bool> IsRetainedAsync(Guid blockId, CancellationToken ct = default);
    public Task<LegalHoldResult> ApplyLegalHoldAsync(Guid blockId, LegalHoldRequest request, CancellationToken ct = default);
    public Task<LegalHoldResult> ReleaseLegalHoldAsync(Guid blockId, string holdId, string authorizedBy, CancellationToken ct = default);
    public Task<IReadOnlyList<RetentionLegalHold>> GetActiveLegalHoldsAsync(Guid blockId, CancellationToken ct = default);
    public async Task<RetentionReport> GetRetentionReportAsync(CancellationToken ct = default);
    public async Task<DeletionValidationResult> ValidateDeletionAsync(Guid blockId, CancellationToken ct = default);
    public Task<RetentionPolicy> ExtendRetentionAsync(Guid blockId, DateTime newRetainUntil, string extendedBy, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/SealService.cs
```csharp
public interface ISealService
{
}
    Task<SealResult> SealBlockAsync(Guid blockId, string reason, CancellationToken ct = default);;
    Task<SealResult> SealShardAsync(Guid blockId, int shardIndex, string reason, CancellationToken ct = default);;
    Task<SealResult> SealRangeAsync(DateTime from, DateTime to, string reason, CancellationToken ct = default);;
    Task<bool> IsSealedAsync(Guid blockId, CancellationToken ct = default);;
    Task<bool> IsShardSealedAsync(Guid blockId, int shardIndex, CancellationToken ct = default);;
    Task<SealInfo?> GetSealInfoAsync(Guid blockId, CancellationToken ct = default);;
    Task<IReadOnlyList<SealInfo>> GetAllSealsAsync(CancellationToken ct = default);;
    Task<bool> VerifySealTokenAsync(Guid blockId, string sealToken, CancellationToken ct = default);;
}
```
```csharp
public record SealResult(bool Success, string SealToken, DateTime SealedAt, string Reason, string? Error = null)
{
}
    public int SealedCount { get; init; };
    public static SealResult CreateSuccess(string sealToken, string reason, int sealedCount = 1);
    public static SealResult CreateFailure(string error, string reason);
}
```
```csharp
public record SealInfo(Guid BlockId, int? ShardIndex, DateTime SealedAt, string Reason, string SealToken, string SealedBy)
{
}
    public DateTime? RangeStart { get; init; }
    public DateTime? RangeEnd { get; init; }
    public bool IsBlockSeal;;
    public bool IsRangeSeal;;
}
```
```csharp
public class SealService : ISealService
{
}
    public SealService(IStorageProvider? persistentStorage, string? sealingKeyBase64, ILogger<SealService> logger);
    public SealService(ILogger<SealService> logger) : this(null, null, logger);
    public async Task<SealResult> SealBlockAsync(Guid blockId, string reason, CancellationToken ct = default);
    public async Task<SealResult> SealShardAsync(Guid blockId, int shardIndex, string reason, CancellationToken ct = default);
    public async Task<SealResult> SealRangeAsync(DateTime from, DateTime to, string reason, CancellationToken ct = default);
    public Task<bool> IsSealedAsync(Guid blockId, CancellationToken ct = default);
    public Task<bool> IsShardSealedAsync(Guid blockId, int shardIndex, CancellationToken ct = default);
    public Task<SealInfo?> GetSealInfoAsync(Guid blockId, CancellationToken ct = default);
    public Task<IReadOnlyList<SealInfo>> GetAllSealsAsync(CancellationToken ct = default);
    public Task<bool> VerifySealTokenAsync(Guid blockId, string sealToken, CancellationToken ct = default);
    public async Task ThrowIfSealedAsync(Guid blockId, int? shardIndex, CancellationToken ct = default);
    public IReadOnlyList<SealAuditEntry> GetAuditLog(DateTime? from = null, DateTime? to = null);
}
```
```csharp
private class SealRecord
{
}
    public required Guid BlockId { get; init; }
    public int? ShardIndex { get; init; }
    public required string SealToken { get; init; }
    public required DateTime SealedAt { get; init; }
    public required string Reason { get; init; }
    public required string SealedBy { get; init; }
}
```
```csharp
private class RangeSealRecord
{
}
    public required DateTime RangeStart { get; init; }
    public required DateTime RangeEnd { get; init; }
    public required string SealToken { get; init; }
    public required DateTime SealedAt { get; init; }
    public required string Reason { get; init; }
    public required string SealedBy { get; init; }
}
```
```csharp
public class SealAuditEntry
{
}
    public required Guid EntryId { get; init; }
    public required SealOperation Operation { get; init; }
    public required Guid BlockId { get; init; }
    public int? ShardIndex { get; init; }
    public required string Reason { get; init; }
    public required string Principal { get; init; }
    public required DateTime Timestamp { get; init; }
    public string? AdditionalInfo { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/ComplianceReportingService.cs
```csharp
public interface IComplianceReportingService
{
}
    Task<ComplianceReport> GenerateReportAsync(ComplianceReportRequest request, CancellationToken ct = default);;
    Task<AttestationResult> CreateAttestationAsync(AttestationRequest request, CancellationToken ct = default);;
    Task<bool> VerifyAttestationAsync(string attestationToken, CancellationToken ct = default);;
    Task<IReadOnlyList<ComplianceViolation>> GetViolationsAsync(DateTime? since = null, CancellationToken ct = default);;
    Task<RetentionPolicySummary> GetRetentionPolicyStatusAsync(CancellationToken ct = default);;
    Task<LegalHoldSummary> GetLegalHoldStatusAsync(CancellationToken ct = default);;
}
```
```csharp
public class ComplianceReportingService : IComplianceReportingService
{
#endregion
}
    public ComplianceReportingService(TamperIncidentService incidentService, BlockchainVerificationService blockchainService, TamperProofConfiguration config, ILogger<ComplianceReportingService> logger);
    public async Task<ComplianceReport> GenerateReportAsync(ComplianceReportRequest request, CancellationToken ct = default);
    public async Task<AttestationResult> CreateAttestationAsync(AttestationRequest request, CancellationToken ct = default);
    public async Task<bool> VerifyAttestationAsync(string attestationToken, CancellationToken ct = default);
    public Task<IReadOnlyList<ComplianceViolation>> GetViolationsAsync(DateTime? since = null, CancellationToken ct = default);
    public Task<RetentionPolicySummary> GetRetentionPolicyStatusAsync(CancellationToken ct = default);
    public Task<LegalHoldSummary> GetLegalHoldStatusAsync(CancellationToken ct = default);
    public void TrackBlock(Guid blockId, DateTimeOffset createdAt, DateTimeOffset? retentionExpiresAt);
    public void RecordBlockVerification(Guid blockId, bool integrityValid);
    public void ApplyLegalHold(Guid blockId, string holdId);
    public void CreateLegalHold(string holdId, string holdName, string createdBy);
}
```
```csharp
private class IntegrityProof
{
}
    public Guid BlockId { get; init; }
    public required string ContentHash { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public bool HasBlockchainAnchor { get; init; }
    public bool RetentionValid { get; init; }
}
```
```csharp
private class AttestationPayload
{
}
    public Guid AttestationId { get; init; }
    public required string AttesterIdentity { get; init; }
    public required string Purpose { get; init; }
    public required Guid[] BlockIds { get; init; }
    public required string MerkleRoot { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset ValidUntil { get; init; }
    public int ProofCount { get; init; }
}
```
```csharp
private class AttestationRecord
{
}
    public Guid AttestationId { get; init; }
    public required AttestationPayload Payload { get; init; }
    public required string Signature { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```
```csharp
private class BlockTrackingInfo
{
}
    public Guid BlockId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastVerifiedAt { get; set; }
    public bool? LastVerificationResult { get; set; }
    public DateTimeOffset? RetentionExpiresAt { get; init; }
    public required List<string> LegalHoldIds { get; init; }
    public bool IsDeleted { get; set; }
    public bool ModifiedAfterHold { get; set; }
    public bool HasBlockchainAnchor { get; set; }
    public string? ContentHash { get; set; }
}
```
```csharp
private class LegalHoldInfo
{
}
    public required string HoldId { get; init; }
    public required string HoldName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    public bool IsActive { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/MessageBusIntegration.cs
```csharp
public class MessageBusIntegrationService
{
}
    public MessageBusIntegrationService(IMessageBus? messageBus, TamperProofConfiguration config, ILogger<MessageBusIntegrationService> logger);
    public bool IsAvailable;;
    public async Task<EncryptionResponse?> RequestEncryptionAsync(byte[] data, string keyId, string? algorithmHint = null, CancellationToken ct = default);
    public async Task<DecryptionResponse?> RequestDecryptionAsync(byte[] encryptedData, string keyId, CancellationToken ct = default);
    public async Task<KeyGenerationResponse?> RequestKeyGenerationAsync(string keyPurpose, string keyType = "AES256", CancellationToken ct = default);
    public async Task<KeyRetrievalResponse?> RequestKeyRetrievalAsync(string keyId, CancellationToken ct = default);
    public async Task<WormAccessValidationResponse?> ValidateWormAccessAsync(Guid objectId, string principal, AccessType accessType, CancellationToken ct = default);
    public async Task PublishTamperAlertAsync(TamperIncidentReport incident, CancellationToken ct = default);
    public async Task PublishRecoveryNotificationAsync(Guid objectId, string recoverySource, bool success, CancellationToken ct = default);
}
```
```csharp
public class EncryptionRequest
{
}
    public required Guid RequestId { get; init; }
    public required byte[] Data { get; init; }
    public required string KeyId { get; init; }
    public string? AlgorithmHint { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}
```
```csharp
public class EncryptionResponse
{
}
    public required Guid RequestId { get; init; }
    public required bool Success { get; init; }
    public byte[]? EncryptedData { get; init; }
    public string? Algorithm { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public class DecryptionRequest
{
}
    public required Guid RequestId { get; init; }
    public required byte[] EncryptedData { get; init; }
    public required string KeyId { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}
```
```csharp
public class DecryptionResponse
{
}
    public required Guid RequestId { get; init; }
    public required bool Success { get; init; }
    public byte[]? DecryptedData { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public class KeyGenerationRequest
{
}
    public required Guid RequestId { get; init; }
    public required string Purpose { get; init; }
    public required string KeyType { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}
```
```csharp
public class KeyGenerationResponse
{
}
    public required Guid RequestId { get; init; }
    public required bool Success { get; init; }
    public string? KeyId { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public class KeyRetrievalRequest
{
}
    public required Guid RequestId { get; init; }
    public required string KeyId { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}
```
```csharp
public class KeyRetrievalResponse
{
}
    public required Guid RequestId { get; init; }
    public required bool Success { get; init; }
    public byte[]? KeyMaterial { get; init; }
    public string? KeyType { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public class WormAccessValidationRequest
{
}
    public required Guid RequestId { get; init; }
    public required Guid ObjectId { get; init; }
    public required string Principal { get; init; }
    public required AccessType AccessType { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}
```
```csharp
public class WormAccessValidationResponse
{
}
    public required Guid RequestId { get; init; }
    public required bool Allowed { get; init; }
    public string? DenialReason { get; init; }
    public DateTimeOffset? RetentionExpiresAt { get; init; }
    public bool? HasLegalHold { get; init; }
}
```
```csharp
public class TamperAlert
{
}
    public required Guid AlertId { get; init; }
    public required Guid IncidentId { get; init; }
    public required Guid ObjectId { get; init; }
    public required AlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public TamperIncidentReport? IncidentDetails { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}
```
```csharp
public class RecoveryNotification
{
}
    public required Guid NotificationId { get; init; }
    public required Guid ObjectId { get; init; }
    public required string RecoverySource { get; init; }
    public required bool Success { get; init; }
    public required DateTimeOffset NotifiedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/AuditTrailService.cs
```csharp
public interface IAuditTrailService
{
}
    Task<TamperProofAuditEntry> LogOperationAsync(AuditOperation operation, CancellationToken ct = default);;
    Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailAsync(Guid blockId, CancellationToken ct = default);;
    Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);;
    Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailByOperationTypeAsync(AuditOperationType operationType, DateTime? from = null, DateTime? to = null, CancellationToken ct = default);;
    Task<ProvenanceChain> GetProvenanceChainAsync(Guid blockId, CancellationToken ct = default);;
    Task<bool> VerifyProvenanceChainAsync(Guid blockId, CancellationToken ct = default);;
    Task<ProvenanceChainVerificationResult> VerifyProvenanceChainDetailedAsync(Guid blockId, CancellationToken ct = default);;
    Task<AuditTrailStatistics> GetStatisticsAsync(CancellationToken ct = default);;
}
```
```csharp
public class AuditTrailService : IAuditTrailService
{
}
    public AuditTrailService(TamperProofConfiguration config, ILogger<AuditTrailService> logger);
    public Task<TamperProofAuditEntry> LogOperationAsync(AuditOperation operation, CancellationToken ct);
    public Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailAsync(Guid blockId, CancellationToken ct);
    public Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailByTimeRangeAsync(DateTime from, DateTime to, CancellationToken ct);
    public Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailByOperationTypeAsync(AuditOperationType operationType, DateTime? from, DateTime? to, CancellationToken ct);
    public Task<ProvenanceChain> GetProvenanceChainAsync(Guid blockId, CancellationToken ct);
    public Task<bool> VerifyProvenanceChainAsync(Guid blockId, CancellationToken ct);
    public Task<ProvenanceChainVerificationResult> VerifyProvenanceChainDetailedAsync(Guid blockId, CancellationToken ct);
    public Task<AuditTrailStatistics> GetStatisticsAsync(CancellationToken ct);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/BackgroundIntegrityScanner.cs
```csharp
public interface IBackgroundIntegrityScanner
{
}
    Task StartAsync(CancellationToken ct = default);;
    Task StopAsync(CancellationToken ct = default);;
    ScannerStatus GetStatus();;
    Task<ScanResult> ScanBlockAsync(Guid blockId, CancellationToken ct = default);;
    Task<FullScanResult> RunFullScanAsync(CancellationToken ct = default);;
    event EventHandler<IntegrityViolationEventArgs>? ViolationDetected;
}
```
```csharp
public class BackgroundIntegrityScanner : IBackgroundIntegrityScanner, IDisposable, IAsyncDisposable
{
}
    public event EventHandler<IntegrityViolationEventArgs>? ViolationDetected;
    public BackgroundIntegrityScanner(RecoveryService recoveryService, IStorageProvider metadataStorage, IStorageProvider dataStorage, ILogger<BackgroundIntegrityScanner> logger, TimeSpan? scanInterval = null, int batchSize = 100);
    public async Task StartAsync(CancellationToken ct = default);
    public async Task StopAsync(CancellationToken ct = default);
    public ScannerStatus GetStatus();
    public async Task<ScanResult> ScanBlockAsync(Guid blockId, CancellationToken ct = default);
    public async Task<FullScanResult> RunFullScanAsync(CancellationToken ct = default);
    protected virtual void OnViolationDetected(IntegrityViolationEventArgs e);
    public void TrackBlock(Guid blockId);
    public void Dispose();
    public async ValueTask DisposeAsync();
    protected virtual void Dispose(bool disposing);
    protected virtual async ValueTask DisposeAsyncCore();
}
```
```csharp
public class IntegrityViolationEventArgs : EventArgs
{
}
    public Guid BlockId { get; init; }
    public IReadOnlyList<ShardViolation> Violations { get; init; };
    public DateTime DetectedAt { get; init; }
}
```
```csharp
internal class BlockIndex
{
}
    public List<Guid>? BlockIds { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/BlockchainVerificationService.cs
```csharp
public class BlockchainVerificationService
{
}
    public BlockchainVerificationService(IBlockchainProvider blockchain, ILogger<BlockchainVerificationService> logger);
    public async Task<BlockchainVerificationResult> VerifyAnchorAsync(Guid objectId, IntegrityHash expectedHash, CancellationToken ct = default);
    public async Task<AuditChain> GetAuditChainAsync(Guid objectId, CancellationToken ct = default);
    public async Task<bool> ValidateChainIntegrityAsync(CancellationToken ct = default);
    public async Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default);
    public async Task<ExternalAnchorResult> CreateExternalAnchorAsync(Guid objectId, string merkleRoot, string targetChain, CancellationToken ct = default);
}
```
```csharp
public class ExternalAnchorResult
{
}
    public required bool Success { get; init; }
    public required ExternalAnchorRecord AnchorRecord { get; init; }
    public string? Message { get; init; }
}
```
```csharp
public record ExternalAnchorRecord
{
}
    public required Guid AnchorId { get; init; }
    public required Guid ObjectId { get; init; }
    public required string MerkleRoot { get; init; }
    public required string TargetChain { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ExternalAnchorStatus Status { get; init; }
    public string? ExternalTransactionId { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
}
```
```csharp
public class BlockchainVerificationResult
{
}
    public required bool Success { get; init; }
    public required Guid ObjectId { get; init; }
    public long? BlockNumber { get; init; }
    public DateTimeOffset? AnchoredAt { get; init; }
    public int? Confirmations { get; init; }
    public BlockchainAnchor? Anchor { get; init; }
    public string? ErrorMessage { get; init; }
    public IntegrityHash? ExpectedHash { get; init; }
    public IntegrityHash? ActualHash { get; init; }
    public static BlockchainVerificationResult CreateSuccess(Guid objectId, long blockNumber, DateTimeOffset anchoredAt, int confirmations, BlockchainAnchor? anchor);
    public static BlockchainVerificationResult CreateFailure(Guid objectId, string errorMessage, IntegrityHash? expectedHash, IntegrityHash? actualHash);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/RecoveryService.cs
```csharp
public class RecoveryService
{
}
    public RecoveryService(PluginWormProvider worm, IIntegrityProvider integrity, IBlockchainProvider blockchain, IStorageProvider dataStorage, TamperIncidentService incidentService, TamperProofConfiguration config, ILogger<RecoveryService> logger);
    public async Task<AdvancedRecoveryResult> RecoverFromWormAsync(TamperProofManifest manifest, IntegrityHash expectedHash, IntegrityHash actualHash, List<int>? affectedShards, CancellationToken ct = default);
    public async Task<AdvancedRecoveryResult> RecoverFromRaidParityAsync(TamperProofManifest manifest, List<int> corruptedShards, Dictionary<int, byte[]> availableShards, CancellationToken ct = default);
    public async Task<AdvancedRecoveryResult> RecoverCorruptedShardsAsync(TamperProofManifest manifest, List<int> corruptedShardIndices, CancellationToken ct = default);
    public async Task<ShardIntegrityCheckResult> VerifyShardIntegrityAsync(TamperProofManifest manifest, CancellationToken ct = default);
    public async Task<AdvancedRecoveryResult> HandleManualOnlyRecoveryAsync(TamperProofManifest manifest, IntegrityHash expectedHash, IntegrityHash actualHash, List<int>? affectedShards, CancellationToken ct = default);
    public async Task HandleFailClosedRecoveryAsync(TamperProofManifest manifest, IntegrityHash expectedHash, IntegrityHash actualHash, List<int>? affectedShards, CancellationToken ct = default);
    public async Task<SecureCorrectionResult> SecureCorrectAsync(Guid blockId, byte[] correctedData, string authorizationToken, string reason, CancellationToken ct = default);
}
```
```csharp
internal class AuthorizationResult
{
}
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public string AuthorizedBy { get; init; };
}
```
```csharp
internal class CorrectionApplicationResult
{
}
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
internal class SecureCorrectionAuditEntry
{
}
    public Guid AuditId { get; init; }
    public Guid BlockId { get; init; }
    public int Version { get; init; }
    public string OriginalHash { get; init; };
    public string AuthorizedBy { get; init; };
    public string Reason { get; init; };
    public DateTimeOffset Timestamp { get; init; }
    public SecureCorrectionAuditStatus Status { get; set; }
}
```
```csharp
internal class BlockSealRecord
{
}
    public Guid ObjectId { get; init; }
    public int Version { get; init; }
    public DateTimeOffset SealedAt { get; init; }
    public List<int>? AffectedShards { get; init; }
    public string Reason { get; init; };
}
```
```csharp
public class AdvancedRecoveryResult
{
}
    public required bool Success { get; init; }
    public required Guid ObjectId { get; init; }
    public required int Version { get; init; }
    public required RecoverySource RecoverySource { get; init; }
    public required List<RecoveryStep> RecoverySteps { get; init; }
    public IntegrityHash? RestoredDataHash { get; init; }
    public TamperIncidentReport? IncidentReport { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Duration;;
    public string? Details { get; init; }
    public string? ErrorMessage { get; init; }
}
```
```csharp
public class RecoveryStep
{
}
    public required string StepName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required RecoveryStepStatus Status { get; set; }
    public string? Details { get; set; }
    public string? ErrorMessage { get; set; }
}
```
```csharp
public class ShardIntegrityCheckResult
{
}
    public required Guid ObjectId { get; init; }
    public required int Version { get; init; }
    public required int TotalShards { get; init; }
    public required int ValidShards { get; init; }
    public required List<int> CorruptedShards { get; init; }
    public required List<int> MissingShards { get; init; }
    public required List<ShardCheckResult> ShardResults { get; init; }
    public required bool CanRecover { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
    public ShardHealthStatus OverallHealth
{
    get
    {
        if (CorruptedShards.Count == 0 && MissingShards.Count == 0)
            return ShardHealthStatus.Healthy;
        if (CanRecover)
            return ShardHealthStatus.Degraded;
        return ShardHealthStatus.Critical;
    }
}
}
```
```csharp
public class ShardCheckResult
{
}
    public required int ShardIndex { get; init; }
    public required ShardStatus Status { get; init; }
    public required string ExpectedHash { get; init; }
    public string? ActualHash { get; init; }
    public long? SizeBytes { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/DegradationStateService.cs
```csharp
public class StateChangedEventArgs : EventArgs
{
}
    public required string InstanceId { get; init; }
    public required InstanceDegradationState OldState { get; init; }
    public required InstanceDegradationState NewState { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Reason { get; init; }
    public bool IsAdminOverride { get; init; }
    public string? AdminPrincipal { get; init; }
}
```
```csharp
internal class DegradationStateService
{
}
    public event EventHandler<StateChangedEventArgs>? StateChanged;
    public DegradationStateService(ILogger<DegradationStateService> logger);
    public Task<bool> TransitionStateAsync(string instanceId, InstanceDegradationState newState, string? reason = null);
    public InstanceDegradationState GetCurrentState(string instanceId);
    public Task AdminOverrideStateAsync(string instanceId, InstanceDegradationState newState, string adminPrincipal, string reason);
    public async Task<InstanceDegradationState> DetectStateAsync(string instanceId, HealthCheckResult healthCheck);
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Services/TamperIncidentService.cs
```csharp
public class TamperIncidentService
{
}
    public TamperIncidentService(TamperProofConfiguration config, SdkAccessLogProvider? accessLogProvider, ILogger<TamperIncidentService> logger);
    public TamperIncidentService(TamperProofConfiguration config, PluginAccessLogProvider? accessLogProvider, ILogger<TamperIncidentService> logger);
    public async Task<TamperIncidentReport> RecordIncidentAsync(Guid objectId, int version, IntegrityHash expectedHash, IntegrityHash actualHash, string affectedInstance, List<int>? affectedShards, TamperRecoveryBehavior recoveryAction, bool recoverySucceeded, CancellationToken ct = default);
    public Task<TamperIncidentReport?> GetLatestIncidentAsync(Guid objectId, CancellationToken ct = default);
    public Task<IReadOnlyList<TamperIncidentReport>> GetIncidentsAsync(Guid objectId, CancellationToken ct = default);
    public Task<IReadOnlyList<TamperIncidentReport>> QueryIncidentsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    public Task<TamperIncidentStatistics> GetStatisticsAsync(CancellationToken ct = default);
}
```
```csharp
public class TamperIncidentStatistics
{
}
    public required int TotalIncidents { get; init; }
    public required int RecoveredCount { get; init; }
    public required int FailedRecoveryCount { get; init; }
    public required Dictionary<AttributionConfidence, int> IncidentsByConfidence { get; init; }
    public required int IncidentsLast24Hours { get; init; }
    public required int AffectedObjectCount { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Storage/AzureWormStorage.cs
```csharp
public record AzureBlobWormConfiguration(string ConnectionString, string ContainerName, AzureImmutabilityPolicyType DefaultPolicyType = AzureImmutabilityPolicyType.Unlocked, TimeSpan DefaultRetention = default, bool EnableVersionLevelImmutability = true, string? AccountName = null, string? AccountKey = null, bool UseManagedIdentity = false)
{
}
    public TimeSpan EffectiveDefaultRetention;;
    public IReadOnlyList<string> Validate();
}
```
```csharp
public record AzureWormWriteResult
{
}
    public required bool Success { get; init; }
    public required string BlobName { get; init; }
    public string? VersionId { get; init; }
    public string? ETag { get; init; }
    public required DateTimeOffset ImmutabilityPolicyExpiry { get; init; }
    public required AzureImmutabilityPolicyType PolicyType { get; init; }
    public bool LegalHoldActive { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentHash { get; init; }
    public string? Error { get; init; }
    public string? RequestId { get; init; }
    public static AzureWormWriteResult CreateSuccess(string blobName, string versionId, string etag, DateTimeOffset policyExpiry, AzureImmutabilityPolicyType policyType, long sizeBytes, string contentHash, bool legalHoldActive = false, string? requestId = null);
    public static AzureWormWriteResult CreateFailure(string blobName, string error, string? requestId = null);
}
```
```csharp
public class AzureWormStorage : WormStorageProviderPluginBase
{
}
    public AzureWormStorage(AzureBlobWormConfiguration config, ILogger<AzureWormStorage> logger, string blobPrefix = "worm/");
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override async Task StartAsync(CancellationToken ct);
    public override async Task StopAsync();
    public override WormEnforcementMode EnforcementMode;;
    public string ContainerName;;
    public async Task<bool> VerifyImmutabilityConfigurationAsync(CancellationToken ct = default);
    public async Task<AzureWormWriteResult> WriteWithImmutabilityAsync(Guid objectId, Stream data, WormRetentionPolicy retention, AzureImmutabilityPolicyType? policyType = null, bool applyLegalHold = false, CancellationToken ct = default);
    protected override async Task<SdkWormWriteResult> WriteInternalAsync(Guid objectId, Stream data, WormRetentionPolicy retention, WriteContext context, CancellationToken ct);
    public override async Task<Stream> ReadAsync(Guid objectId, CancellationToken ct = default);
    public override async Task<WormObjectStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);
    public override async Task<bool> ExistsAsync(Guid objectId, CancellationToken ct = default);
    public override async Task<IReadOnlyList<LegalHold>> GetLegalHoldsAsync(Guid objectId, CancellationToken ct = default);
    protected override async Task ExtendRetentionInternalAsync(Guid objectId, DateTimeOffset newExpiry, CancellationToken ct);
    protected override async Task PlaceLegalHoldInternalAsync(Guid objectId, string holdId, string reason, CancellationToken ct);
    protected override async Task RemoveLegalHoldInternalAsync(Guid objectId, string holdId, CancellationToken ct);
    public async Task LockImmutabilityPolicyAsync(Guid objectId, CancellationToken ct = default);
    protected override Dictionary<string, object> GetMetadata();
}
```
```csharp
private class AzureBlobRecord
{
}
    public required string BlobName { get; init; }
    public required string VersionId { get; init; }
    public required byte[] Data { get; init; }
    public required string ContentHash { get; init; }
    public required string ETag { get; init; }
    public AzureImmutabilityPolicyType PolicyType { get; set; }
    public DateTimeOffset PolicyExpiry { get; set; }
    public bool LegalHoldActive { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long SizeBytes { get; init; }
    public required List<AzureLegalHoldRecord> LegalHolds { get; init; }
}
```
```csharp
private class AzureLegalHoldRecord
{
}
    public required string HoldId { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset PlacedAt { get; init; }
    public required string PlacedBy { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.TamperProof/Storage/S3WormStorage.cs
```csharp
public record S3WormConfiguration(string BucketName, string Region, string? AccessKeyId = null, string? SecretAccessKey = null, S3ObjectLockMode DefaultMode = S3ObjectLockMode.Governance, TimeSpan DefaultRetention = default, string? EndpointUrl = null, bool UsePathStyleAddressing = false, bool EnableVersioning = true)
{
}
    public TimeSpan EffectiveDefaultRetention;;
    public IReadOnlyList<string> Validate();
}
```
```csharp
public record S3WormWriteResult
{
}
    public required bool Success { get; init; }
    public required string Key { get; init; }
    public string? VersionId { get; init; }
    public string? ETag { get; init; }
    public required DateTimeOffset RetainUntil { get; init; }
    public required S3ObjectLockMode LockMode { get; init; }
    public bool LegalHoldActive { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentHash { get; init; }
    public string? Error { get; init; }
    public string? RequestId { get; init; }
    public static S3WormWriteResult CreateSuccess(string key, string versionId, string etag, DateTimeOffset retainUntil, S3ObjectLockMode lockMode, long sizeBytes, string contentHash, bool legalHoldActive = false, string? requestId = null);
    public static S3WormWriteResult CreateFailure(string key, string error, string? requestId = null);
}
```
```csharp
public class S3WormStorage : WormStorageProviderPluginBase
{
}
    public S3WormStorage(S3WormConfiguration config, ILogger<S3WormStorage> logger, string keyPrefix = "worm/");
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override async Task StartAsync(CancellationToken ct);
    public override async Task StopAsync();
    public override WormEnforcementMode EnforcementMode;;
    public string BucketName;;
    public string Region;;
    public async Task<bool> VerifyObjectLockConfigurationAsync(CancellationToken ct = default);
    public async Task<S3WormWriteResult> WriteWithObjectLockAsync(Guid objectId, Stream data, WormRetentionPolicy retention, S3ObjectLockMode? lockMode = null, bool applyLegalHold = false, CancellationToken ct = default);
    protected override async Task<SdkWormWriteResult> WriteInternalAsync(Guid objectId, Stream data, WormRetentionPolicy retention, WriteContext context, CancellationToken ct);
    public override async Task<Stream> ReadAsync(Guid objectId, CancellationToken ct = default);
    public override async Task<WormObjectStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);
    public override async Task<bool> ExistsAsync(Guid objectId, CancellationToken ct = default);
    public override async Task<IReadOnlyList<LegalHold>> GetLegalHoldsAsync(Guid objectId, CancellationToken ct = default);
    protected override async Task ExtendRetentionInternalAsync(Guid objectId, DateTimeOffset newExpiry, CancellationToken ct);
    protected override async Task PlaceLegalHoldInternalAsync(Guid objectId, string holdId, string reason, CancellationToken ct);
    protected override async Task RemoveLegalHoldInternalAsync(Guid objectId, string holdId, CancellationToken ct);
    protected override Dictionary<string, object> GetMetadata();
}
```
```csharp
private class S3ObjectRecord
{
}
    public required string Key { get; init; }
    public required string VersionId { get; init; }
    public required byte[] Data { get; init; }
    public required string ContentHash { get; init; }
    public required string ETag { get; init; }
    public required S3ObjectLockMode LockMode { get; init; }
    public DateTimeOffset RetainUntil { get; set; }
    public bool LegalHoldActive { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long SizeBytes { get; init; }
}
```
