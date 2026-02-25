# Plugin: UltimateKeyManagement
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateKeyManagement

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/UltimateKeyManagementPlugin.cs
```csharp
public class UltimateKeyManagementPlugin : SecurityPluginBase, IKeyStoreRegistry, IDisposable, IAsyncDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string SecurityDomain;;
    public override PluginCategory Category;;
    public string KeyStoreType;;
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = "key-management",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                DisplayName = "Ultimate Key Management",
                Description = "Comprehensive key management system with multiple storage strategies, envelope encryption, key rotation, and HSM support",
                Category = SDK.Contracts.CapabilityCategory.Security,
                Tags = ["keymanagement", "security", "encryption", "rotation", "hsm", "envelope"]
            }
        };
        foreach (var(id, strategy)in _strategies)
        {
            var caps = strategy.Capabilities;
            var tags = new List<string>
            {
                "keystore",
                "security"
            };
            if (caps.SupportsEnvelope)
                tags.Add("envelope");
            if (caps.SupportsRotation)
                tags.Add("rotation");
            if (caps.SupportsHsm)
                tags.Add("hsm");
            capabilities.Add(new RegisteredCapability { CapabilityId = $"keystore-{id.Replace(".", "-").Replace(" ", "-").ToLowerInvariant()}", PluginId = Id, PluginName = Name, PluginVersion = Version, DisplayName = $"Key Store: {id}", Description = $"Key storage strategy supporting: " + $"Envelope={caps.SupportsEnvelope}, " + $"Rotation={caps.SupportsRotation}, " + $"HSM={caps.SupportsHsm}", Category = SDK.Contracts.CapabilityCategory.Security, Tags = tags.ToArray() });
        }

        return capabilities.AsReadOnly();
    }
}
    protected override async Task OnStartCoreAsync(CancellationToken ct);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override async Task OnBeforeStatePersistAsync(CancellationToken ct);
    protected override async Task OnStopCoreAsync();
    public void Register(string pluginId, IKeyStore keyStore);
    public void RegisterEnvelope(string pluginId, IEnvelopeKeyStore envelopeKeyStore);
    public IKeyStore? GetKeyStore(string? pluginId);
    public IEnvelopeKeyStore? GetEnvelopeKeyStore(string? pluginId);
    public IReadOnlyList<string> GetRegisteredKeyStoreIds();
    public IReadOnlyList<string> GetRegisteredEnvelopeKeyStoreIds();
    protected override async Task<NativeKeyHandle> GetKeyNativeAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task OnMessageAsync(PluginMessage message);
    protected override Dictionary<string, object> GetMetadata();
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override void Dispose(bool disposing);
    protected override async ValueTask DisposeAsyncCore();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Configuration.cs
```csharp
public record UltimateKeyManagementConfig
{
}
    public bool AutoDiscoverStrategies { get; init; };
    public List<string> DiscoveryAssemblyPatterns { get; init; };
    public List<string> DiscoveryExcludePatterns { get; init; };
    public bool EnableKeyRotation { get; init; };
    public KeyRotationPolicy DefaultRotationPolicy { get; init; };
    public Dictionary<string, KeyRotationPolicy> StrategyRotationPolicies { get; init; };
    public Dictionary<string, Dictionary<string, object>> StrategyConfigurations { get; init; };
    public bool PublishKeyEvents { get; init; };
    public TimeSpan RotationCheckInterval { get; init; };
}
```
```csharp
public record KeyRotationPolicy
{
}
    public bool Enabled { get; init; }
    public TimeSpan RotationInterval { get; init; };
    public TimeSpan GracePeriod { get; init; };
    public TimeSpan? MaxKeyAge { get; init; }
    public bool DeleteOldKeysAfterGrace { get; init; }
    public List<string> TargetKeyIds { get; init; };
    public bool NotifyOnRotation { get; init; };
    public RotationRetryPolicy RetryPolicy { get; init; };
}
```
```csharp
public record RotationRetryPolicy
{
}
    public int MaxRetries { get; init; };
    public TimeSpan InitialDelay { get; init; };
    public double BackoffMultiplier { get; init; };
    public TimeSpan MaxDelay { get; init; };
}
```
```csharp
public record StrategyConfiguration
{
}
    public string StrategyId { get; init; };
    public Dictionary<string, object> Configuration { get; init; };
    public KeyRotationPolicy? RotationPolicy { get; init; }
    public bool Enabled { get; init; };
    public int Priority { get; init; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/KeyRotationScheduler.cs
```csharp
public class KeyRotationScheduler : IDisposable, IAsyncDisposable
{
}
    public KeyRotationScheduler(UltimateKeyManagementConfig config, IMessageBus? messageBus = null);
    public void RegisterStrategy(string strategyId, IKeyStoreStrategy strategy, KeyRotationPolicy? policy = null);
    public bool UnregisterStrategy(string strategyId);
    public void Start();
    public async Task StopAsync(TimeSpan? timeout = null);
    public void Dispose();
    public async ValueTask DisposeAsync();
}
```
```csharp
private sealed class SystemSecurityContext : ISecurityContext
{
}
    public string UserId;;
    public string? TenantId;;
    public IEnumerable<string> Roles;;
    public bool IsSystemAdmin;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Migration/ConfigurationMigrator.cs
```csharp
public sealed class ConfigurationMigrator
{
#endregion
}
    public ConfigurationMigrator(ConfigurationMigratorOptions? options = null);
    public async Task<ConfigurationMigrationResult> MigrateConfigurationFileAsync(string sourceConfigPath, string? outputConfigPath = null, CancellationToken cancellationToken = default);
    public async Task<BatchConfigurationMigrationResult> MigrateDirectoryAsync(string sourceDirectory, string? outputDirectory = null, CancellationToken cancellationToken = default);
    public IReadOnlyList<ConfigurationMigrationEntry> GetMigrationLog();
    public async Task ExportMigrationLogAsync(string outputPath, CancellationToken cancellationToken = default);
}
```
```csharp
internal static class FileExtensions
{
}
    public static async Task CopyAsync(string sourceFile, string destinationFile, CancellationToken cancellationToken);
}
```
```csharp
public sealed class ConfigurationMigratorOptions
{
}
    public bool CreateBackups { get; set; };
    public string? BackupDirectory { get; set; }
    public bool ValidateAfterMigration { get; set; };
    public bool PreserveComments { get; set; };
}
```
```csharp
public sealed class ConfigurationMigrationResult
{
}
    public bool Success { get; set; }
    public string SourcePath { get; set; };
    public string OutputPath { get; set; };
    public string? BackupPath { get; set; }
    public ConfigurationType DetectedConfigType { get; set; }
    public UltimateKeyManagementConfig? MigratedConfiguration { get; set; }
    public ConfigurationValidationResult? ValidationResult { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime MigrationStartedAt { get; set; }
    public DateTime MigrationCompletedAt { get; set; }
}
```
```csharp
public sealed class BatchConfigurationMigrationResult
{
}
    public string SourceDirectory { get; set; };
    public string OutputDirectory { get; set; };
    public int TotalFilesFound { get; set; }
    public int SuccessfulMigrations { get; set; }
    public int FailedMigrations { get; set; }
    public List<ConfigurationMigrationResult> FileResults { get; set; };
    public DateTime MigrationStartedAt { get; set; }
    public DateTime MigrationCompletedAt { get; set; }
}
```
```csharp
public sealed class ConfigurationValidationResult
{
}
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; };
    public List<string> Warnings { get; set; };
}
```
```csharp
public sealed class ConfigurationMigrationEntry
{
}
    public string SourcePath { get; set; };
    public string TargetPath { get; set; };
    public ConfigurationType ConfigurationType { get; set; }
    public bool Success { get; set; }
    public DateTime MigratedAt { get; set; }
}
```
```csharp
internal sealed class LegacyFileKeyStoreConfig
{
}
    public string? KeyStorePath { get; set; }
    public string? MasterKeyEnvVar { get; set; }
    public int KeySizeBytes { get; set; };
    public TimeSpan CacheExpiration { get; set; };
    public bool RequireAuthentication { get; set; };
    public bool RequireAdminForCreate { get; set; };
    public string? DpapiScope { get; set; }
    public string? DpapiEntropy { get; set; }
    public string? CredentialManagerTarget { get; set; }
    public string? FallbackPassword { get; set; }
}
```
```csharp
internal sealed class LegacyVaultConfig
{
}
    public LegacyHashiCorpVaultConfig? HashiCorpVault { get; set; }
    public LegacyAzureKeyVaultConfig? AzureKeyVault { get; set; }
    public LegacyAwsKmsConfig? AwsKms { get; set; }
    public TimeSpan? CacheExpiration { get; set; }
    public TimeSpan? RequestTimeout { get; set; }
}
```
```csharp
internal sealed class LegacyHashiCorpVaultConfig
{
}
    public string? Address { get; set; }
    public string? Token { get; set; }
    public string? MountPath { get; set; }
    public string? DefaultKeyName { get; set; }
}
```
```csharp
internal sealed class LegacyAzureKeyVaultConfig
{
}
    public string? VaultUrl { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? DefaultKeyName { get; set; }
}
```
```csharp
internal sealed class LegacyAwsKmsConfig
{
}
    public string? Region { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? DefaultKeyId { get; set; }
}
```
```csharp
internal sealed class LegacyKeyRotationConfig
{
}
    public bool EnableScheduledRotation { get; set; };
    public TimeSpan RotationInterval { get; set; };
    public TimeSpan GracePeriod { get; set; };
    public int MaxConcurrentReencryptions { get; set; };
    public TimeSpan ReencryptionTimeout { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Migration/PluginMigrationHelper.cs
```csharp
public sealed class PluginMigrationHelper : IDisposable
{
#endregion
}
    public static readonly IReadOnlyDictionary<string, string> PluginMappings = new Dictionary<string, string>
{
    // FileKeyStorePlugin -> FileKeyStoreStrategy
    ["datawarehouse.plugins.keystore.file"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy",
    ["DataWarehouse.Plugins.FileKeyStore.FileKeyStorePlugin"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local.FileKeyStoreStrategy",
    // VaultKeyStorePlugin -> VaultKeyStoreStrategy
    ["datawarehouse.plugins.keystore.vault"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement.VaultKeyStoreStrategy",
    ["DataWarehouse.Plugins.VaultKeyStore.VaultKeyStorePlugin"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement.VaultKeyStoreStrategy",
    // KeyRotationPlugin -> Built-in KeyRotationScheduler
    ["datawarehouse.plugins.security.keyrotation"] = "com.datawarehouse.keymanagement.ultimate",
    ["DataWarehouse.Plugins.KeyRotation.KeyRotationPlugin"] = "com.datawarehouse.keymanagement.ultimate",
    // Common type name references
    ["FileKeyStorePlugin"] = "FileKeyStoreStrategy",
    ["VaultKeyStorePlugin"] = "VaultKeyStoreStrategy",
    ["KeyRotationPlugin"] = "UltimateKeyManagementPlugin"
};
    public static readonly IReadOnlyDictionary<string, string> NamespaceMappings = new Dictionary<string, string>
{
    ["DataWarehouse.Plugins.FileKeyStore"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Local",
    ["DataWarehouse.Plugins.VaultKeyStore"] = "DataWarehouse.Plugins.UltimateKeyManagement.Strategies.SecretsManagement",
    ["DataWarehouse.Plugins.KeyRotation"] = "DataWarehouse.Plugins.UltimateKeyManagement"
};
    public static readonly IReadOnlyDictionary<string, string> ConfigTypeMappings = new Dictionary<string, string>
{
    ["FileKeyStoreConfig"] = "FileKeyStoreConfig", // Same name, different namespace
    ["VaultConfig"] = "VaultKeyStoreConfig",
    ["HashiCorpVaultConfig"] = "VaultKeyStoreConfig", // Consolidated
    ["AzureKeyVaultConfig"] = "AzureKeyVaultStrategyConfig",
    ["AwsKmsConfig"] = "AwsKmsStrategyConfig",
    ["KeyRotationConfig"] = "KeyRotationPolicy"
};
    public PluginMigrationHelper(MigrationHelperConfig? config = null);
    public async Task<MigrationScanResult> ScanForDeprecatedReferencesAsync(string rootDirectory, IEnumerable<string>? filePatterns = null, CancellationToken cancellationToken = default);
    public async Task<FileScanResult> ScanFileAsync(string filePath, CancellationToken cancellationToken = default);
    public IReadOnlyList<AssemblyReferenceInfo> ScanLoadedAssemblies();
    public IReadOnlyList<MigrationSuggestion> GetSuggestions();
    public async Task<MigrationActionResult> ApplyAutoFixAsync(string suggestionId, bool createBackup = true, CancellationToken cancellationToken = default);
    public async Task<BatchMigrationResult> ApplyAllAutoFixesAsync(bool createBackups = true, CancellationToken cancellationToken = default);
    public CompatibilityShim CreateCompatibilityShim(UltimateKeyManagementPlugin ultimatePlugin);
    public void Dispose();
}
```
```csharp
public sealed class CompatibilityShim : IKeyStore
{
}
    internal CompatibilityShim(UltimateKeyManagementPlugin ultimatePlugin, MigrationHelperConfig config);
    public IKeyStore? GetLegacyKeyStore(string legacyPluginId);
    public void RegisterLegacyMapping(string legacyId, string strategyId);
    public Task<string> GetCurrentKeyIdAsync();
    public byte[] GetKey(string keyId);
    public async Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context);
    public async Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context);
}
```
```csharp
private sealed class ShimSecurityContext : ISecurityContext
{
}
    public string UserId;;
    public string? TenantId;;
    public IEnumerable<string> Roles;;
    public bool IsSystemAdmin;;
}
```
```csharp
public sealed class MigrationHelperConfig
{
}
    public bool EmitDeprecationWarnings { get; set; };
    public bool LogMigrationActivity { get; set; };
    public string BackupDirectory { get; set; };
    public List<string> ExcludePatterns { get; set; };
}
```
```csharp
public sealed class MigrationScanResult
{
}
    public DateTime ScanStartedAt { get; set; }
    public DateTime ScanCompletedAt { get; set; }
    public string RootDirectory { get; set; };
    public int TotalFilesScanned { get; set; }
    public int TotalReferencesFound { get; set; }
    public List<FileScanResult> FilesWithReferences { get; set; };
}
```
```csharp
public sealed class FileScanResult
{
}
    public string FilePath { get; set; };
    public List<PluginReferenceInfo> References { get; set; };
    public string? ScanError { get; set; }
    public bool HasReferences;;
}
```
```csharp
public sealed class PluginReferenceInfo
{
}
    public string FilePath { get; set; };
    public int LineNumber { get; set; }
    public string OldReference { get; set; };
    public string NewReference { get; set; };
    public ReferenceType ReferenceType { get; set; }
    public string Context { get; set; };
}
```
```csharp
public sealed class AssemblyReferenceInfo
{
}
    public string ReferencingAssembly { get; set; };
    public string ReferencedAssembly { get; set; };
    public string Version { get; set; };
    public bool IsDeprecated { get; set; }
    public string MigrationTarget { get; set; };
}
```
```csharp
public sealed class MigrationSuggestion
{
}
    public string SuggestionId { get; set; };
    public string FilePath { get; set; };
    public SuggestionType SuggestionType { get; set; }
    public MigrationSeverity Severity { get; set; }
    public string Description { get; set; };
    public string RecommendedAction { get; set; };
    public List<PluginReferenceInfo> AffectedReferences { get; set; };
    public bool AutoFixAvailable { get; set; }
}
```
```csharp
public sealed class MigrationAction
{
}
    public string ActionId { get; set; };
    public string SuggestionId { get; set; };
    public string ActionType { get; set; };
    public string FilePath { get; set; };
    public DateTime AppliedAt { get; set; }
    public int ReplacementsCount { get; set; }
}
```
```csharp
public sealed class MigrationActionResult
{
}
    public bool Success { get; set; }
    public MigrationAction? Action { get; set; }
    public string FilePath { get; set; };
    public int ReplacementsApplied { get; set; }
    public string? ErrorMessage { get; set; }
}
```
```csharp
public sealed class BatchMigrationResult
{
}
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public int TotalSuggestions { get; set; }
    public int SuccessfulFixes { get; set; }
    public int FailedFixes { get; set; }
    public List<MigrationAction> AppliedActions { get; set; };
    public List<string> Errors { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Migration/DeprecationManager.cs
```csharp
public sealed class DeprecationManager : IDisposable
{
}
    public static DeprecationManager Instance;;
    public event EventHandler<DeprecationWarningEventArgs>? WarningEmitted;
    public DeprecationManager() : this(new DeprecationManagerOptions());
    public DeprecationManager(DeprecationManagerOptions options);
    public void RegisterDeprecation(DeprecationInfo info);
    public bool UnregisterDeprecation(string itemId);
    public bool CheckAndWarn(string itemId, [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0, [CallerMemberName] string callerMemberName = "");
    public bool CheckTypeAndWarn(Type type, [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0, [CallerMemberName] string callerMemberName = "");
    public DeprecationInfo? GetDeprecationInfo(string itemId);
    public IReadOnlyList<DeprecationInfo> GetAllDeprecations();
    public IReadOnlyList<DeprecationInfo> GetDeprecationsByType(DeprecatedItemType itemType);
    public IReadOnlyList<DeprecationWarning> GetEmittedWarnings();
    public DeprecationTimeline GetTimeline();
    public IReadOnlyList<DeprecationInfo> GetItemsScheduledForRemoval(TimeSpan within);
    public IReadOnlyList<string> GetMigrationSteps(string itemId);
    public DeprecationReport GenerateReport();
    public void Dispose();
}
```
```csharp
public sealed class DeprecationManagerOptions
{
}
    public bool EmitToConsole { get; set; };
    public bool EmitToTrace { get; set; };
    public bool EmitToDebug { get; set; };
    public bool SuppressDuplicateWarnings { get; set; };
    public bool TreatWarningsAsErrors { get; set; };
}
```
```csharp
public sealed class DeprecationInfo
{
}
    public string ItemId { get; init; };
    public DeprecatedItemType ItemType { get; init; }
    public string ItemName { get; init; };
    public string FullTypeName { get; init; };
    public Version? DeprecatedInVersion { get; init; }
    public Version? RemovedInVersion { get; init; }
    public DateTime? DeprecationDate { get; init; }
    public DateTime? RemovalDate { get; init; }
    public string MigrationTarget { get; init; };
    public string? MigrationGuideUrl { get; init; }
    public string Reason { get; init; };
    public List<string> MigrationSteps { get; init; };
}
```
```csharp
public sealed class DeprecationWarning
{
}
    public string WarningKey { get; init; };
    public string ItemId { get; init; };
    public string ItemName { get; init; };
    public DeprecatedItemType ItemType { get; init; }
    public string CallerFilePath { get; init; };
    public int CallerLineNumber { get; init; }
    public string CallerMemberName { get; init; };
    public DateTime EmittedAt { get; init; }
    public string Message { get; init; };
    public string MigrationTarget { get; init; };
    public string? MigrationGuideUrl { get; init; }
}
```
```csharp
public sealed class DeprecationWarningEventArgs : EventArgs
{
}
    public DeprecationWarning Warning { get; }
    public DeprecationWarningEventArgs(DeprecationWarning warning);
}
```
```csharp
public sealed class DeprecationTimeline
{
}
    public List<DeprecationInfo> CurrentlyDeprecated { get; init; };
    public List<DeprecationInfo> UpcomingRemovals { get; init; };
    public List<DeprecationInfo> AlreadyRemoved { get; init; };
}
```
```csharp
public sealed class DeprecationReport
{
}
    public DateTime GeneratedAt { get; init; }
    public int TotalDeprecatedItems { get; init; }
    public int WarningsEmitted { get; init; }
    public int UniqueItemsWarned { get; init; }
    public List<DeprecationReportItem> Items { get; init; };
}
```
```csharp
public sealed class DeprecationReportItem
{
}
    public string ItemId { get; init; };
    public string ItemName { get; init; };
    public DeprecatedItemType ItemType { get; init; }
    public string DeprecatedInVersion { get; init; };
    public string RemovedInVersion { get; init; };
    public string MigrationTarget { get; init; };
    public int WarningCount { get; init; }
    public List<string> Locations { get; init; };
    public List<string> MigrationSteps { get; init; };
}
```
```csharp
public sealed class DeprecationException : Exception
{
}
    public DeprecationInfo DeprecationInfo { get; }
    public DeprecationException(string message, DeprecationInfo info) : base(message);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/AdvancedKeyOperations.cs
```csharp
public sealed class AdvancedHkdfStrategy : KeyStoreStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override KeyStoreCapabilities Capabilities;;
    protected override Task InitializeStorage(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public byte[] DeriveKey(string info, byte[]? salt = null, int? outputLength = null);
    public byte[] Extract(byte[] ikm, byte[]? salt = null);
    public byte[] Expand(byte[] prk, string info, int outputLength);
    public Dictionary<string, byte[]> DeriveMultipleKeys(IEnumerable<string> contexts, byte[]? salt = null, int? outputLength = null);
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public sealed class KeyEscrowRecoveryStrategy : KeyStoreStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override KeyStoreCapabilities Capabilities;;
    protected override Task InitializeStorage(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public RecoveryRequestResult InitiateRecovery(string keyId, string requesterId, string reason);
    public RecoveryApprovalResult ApproveRecovery(string requestId, string approverId);
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
}
```
```csharp
public sealed class KeyAgreementStrategy : KeyStoreStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override KeyStoreCapabilities Capabilities;;
    protected override Task InitializeStorage(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair();
    public byte[] AgreeOnKey(byte[] ourPrivateKey, byte[] theirPublicKey, string context, int keyLength = 32);
    protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public sealed class KeyWrappingStrategy : KeyStoreStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override KeyStoreCapabilities Capabilities;;
    protected override Task InitializeStorage(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    public byte[] WrapKey(byte[] kek, byte[] dataKey);
    public byte[] UnwrapKey(byte[] kek, byte[] wrappedKey);
    protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
internal sealed class EscrowedKey
{
}
    public required string KeyId { get; init; }
    public required byte[] EncryptedKeyMaterial { get; init; }
    public required DateTime EscrowedAt { get; init; }
    public required string EscrowedBy { get; init; }
    public required int RecoveryThreshold { get; init; }
    public required int TotalShares { get; init; }
}
```
```csharp
internal sealed class RecoveryRequest
{
}
    public required string RequestId { get; init; }
    public required string KeyId { get; init; }
    public required string RequesterId { get; init; }
    public required string Reason { get; init; }
    public required DateTime RequestedAt { get; init; }
    public required DateTime EarliestRelease { get; init; }
    public required List<RecoveryApproval> Approvals { get; set; }
    public RecoveryStatus Status { get; set; }
}
```
```csharp
internal sealed class RecoveryApproval
{
}
    public required string ApproverId { get; init; }
    public required DateTime ApprovedAt { get; init; }
}
```
```csharp
public sealed class RecoveryRequestResult
{
}
    public required string RequestId { get; init; }
    public required DateTime EarliestRelease { get; init; }
    public required int ApprovalsRequired { get; init; }
    public required RecoveryStatus Status { get; init; }
}
```
```csharp
public sealed class RecoveryApprovalResult
{
}
    public required string RequestId { get; init; }
    public required int ApprovalsReceived { get; init; }
    public required int ApprovalsRequired { get; init; }
    public required RecoveryStatus Status { get; init; }
    public required bool CanRelease { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Features/KeyDerivationHierarchy.cs
```csharp
public sealed class KeyDerivationHierarchy : IDisposable
{
}
    public static class KeyPurpose;
    public KeyDerivationHierarchy(IKeyStore keyStore);
    public async Task RegisterMasterKeyAsync(string masterId, string keyId, ISecurityContext context, CancellationToken ct = default);
    public async Task<DerivedKeyResult> DeriveKeyAsync(string masterId, string purpose, ISecurityContext context, byte[]? salt = null, int keySizeBytes = 32, CancellationToken ct = default);
    public async Task<DerivedKeyResult> DeriveChildKeyAsync(string parentDerivedKeyId, string childPurpose, ISecurityContext context, byte[]? salt = null, int keySizeBytes = 32, CancellationToken ct = default);
    public async Task<byte[]> RederiveKeyAsync(string derivedKeyId, ISecurityContext context, CancellationToken ct = default);
    public DerivationPathInfo? GetDerivationPath(string derivedKeyId);
    public IReadOnlyList<DerivedKeyInfo> GetDerivedKeys(string masterId);
    public IReadOnlyList<DerivedKeyInfo> GetKeysByPurpose(string purpose);
    public KeyHierarchyTree GetHierarchyTree(string masterId);
    public bool ValidateDerivationPath(string derivationPath);
    public bool RemoveDerivedKey(string derivedKeyId);
    public int RemoveMasterKey(string masterId);
    public KeyHierarchyStatistics GetStatistics();
    public void Dispose();
}
```
```csharp
public static class KeyPurpose
{
}
    public const string Encryption = "encryption";
    public const string Signing = "signing";
    public const string Authentication = "authentication";
    public const string KeyWrapping = "key-wrapping";
    public const string DataEncryption = "data-encryption";
    public const string KeyEncryption = "key-encryption";
    public const string MacGeneration = "mac-generation";
    public const string TokenGeneration = "token-generation";
    public const string SessionKey = "session-key";
    public const string BackupKey = "backup-key";
}
```
```csharp
public class MasterKeyInfo
{
}
    public string MasterId { get; set; };
    public string KeyStoreKeyId { get; set; };
    public DateTime RegisteredAt { get; set; }
    public string? RegisteredBy { get; set; }
    public int KeySizeBytes { get; set; }
    public string DerivationPath { get; set; };
    public int DerivedKeyCount { get; set; }
}
```
```csharp
public class DerivedKeyInfo
{
}
    public string DerivedKeyId { get; set; };
    public string MasterId { get; set; };
    public string? ParentDerivedKeyId { get; set; }
    public string Purpose { get; set; };
    public string DerivationPath { get; set; };
    public DateTime DerivedAt { get; set; }
    public string? DerivedBy { get; set; }
    public int KeySizeBytes { get; set; }
    public string? SaltUsed { get; set; }
    public int DerivationLevel { get; set; }
    public int ChildKeyCount { get; set; }
}
```
```csharp
public class DerivedKeyResult
{
}
    public string DerivedKeyId { get; set; };
    public byte[] KeyMaterial { get; set; };
    public string Purpose { get; set; };
    public string DerivationPath { get; set; };
    public string MasterId { get; set; };
    public string? ParentKeyId { get; set; }
    public DateTime DerivedAt { get; set; }
    public int KeySizeBytes { get; set; }
}
```
```csharp
public class DerivationPathInfo
{
}
    public string DerivedKeyId { get; set; };
    public string FullPath { get; set; };
    public List<string> Components { get; set; };
    public int Level { get; set; }
    public string MasterId { get; set; };
    public string Purpose { get; set; };
    public string? ParentKeyId { get; set; }
}
```
```csharp
public class KeyHierarchyTree
{
}
    public string MasterId { get; set; };
    public string KeyStoreKeyId { get; set; };
    public DateTime RegisteredAt { get; set; }
    public List<KeyHierarchyNode> DirectChildren { get; set; };
}
```
```csharp
public class KeyHierarchyNode
{
}
    public string KeyId { get; set; };
    public string Purpose { get; set; };
    public string DerivationPath { get; set; };
    public int Level { get; set; }
    public DateTime DerivedAt { get; set; }
    public string? ParentKeyId { get; set; }
    public List<KeyHierarchyNode> Children { get; set; };
}
```
```csharp
public class KeyHierarchyStatistics
{
}
    public int TotalMasterKeys { get; set; }
    public int TotalDerivedKeys { get; set; }
    public int MaxDerivationDepth { get; set; }
    public Dictionary<string, int> KeysByPurpose { get; set; };
    public Dictionary<string, int> KeysByMaster { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Features/TamperProofManifestExtensions.cs
```csharp
public static class TamperProofManifestExtensions
{
#endregion
}
    public const string EncryptionMetadataKey = "encryption.metadata";
    public const string EncryptionConfigModeKey = "encryption.configMode";
    public const string EncryptionMetadataVersionKey = "encryption.metadataVersion";
    public const int CurrentEncryptionMetadataVersion = 1;
    public static EncryptionMetadata? GetEncryptionMetadata(this TamperProofManifest manifest);
    public static EncryptionMetadata GetRequiredEncryptionMetadata(this TamperProofManifest manifest);
    public static bool HasEncryptionMetadata(this TamperProofManifest manifest);
    public static EncryptionConfigMode? GetEncryptionConfigMode(this TamperProofManifest manifest);
    public static int GetEncryptionMetadataVersion(this TamperProofManifest manifest);
    public static Dictionary<string, object> CreateUserMetadataWithEncryption(EncryptionMetadata encryptionMetadata, EncryptionConfigMode configMode, Dictionary<string, object>? existingMetadata = null);
    public static void AddEncryptionMetadata(Dictionary<string, object> userMetadata, EncryptionMetadata encryptionMetadata, EncryptionConfigMode configMode);
    public static bool IsEncryptionMetadataVersionCompatible(this TamperProofManifest manifest);
    public static EncryptionMetadata? MigrateEncryptionMetadataIfNeeded(this TamperProofManifest manifest);
    public static PipelineStageRecord? GetEncryptionStage(this TamperProofManifest manifest);
    public static bool IsContentEncrypted(this TamperProofManifest manifest);
    public static IReadOnlyDictionary<string, object> GetEncryptionStageParameters(this TamperProofManifest manifest);
    public static IReadOnlyList<string> ValidateEncryptionMetadata(this TamperProofManifest manifest);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Features/EncryptionConfigModes.cs
```csharp
public sealed class PerObjectConfigMode : IEncryptionConfigMode
{
}
    public const string ModeId = "PerObject";
    public string Id;;
    public string DisplayName;;
    public string Description;;
    public EncryptionConfigMode Mode;;
    public (bool IsValid, string? Error) ValidateConfig(EncryptionMetadata metadata, EncryptionMetadata? existingConfig, EncryptionPolicy? policy);
    public (bool IsAllowed, string? Error) CanWriteWithConfig(EncryptionMetadata proposedConfig, EncryptionMetadata? existingConfig, EncryptionPolicy? policy);
    public (bool IsSealed, string? Error) IsSealedAfterFirstWrite(EncryptionMetadata? existingConfig);
}
```
```csharp
public sealed class FixedConfigMode : IEncryptionConfigMode
{
}
    public const string ModeId = "Fixed";
    public const string SealedConfigKey = "FixedConfig.Sealed";
    public string Id;;
    public string DisplayName;;
    public string Description;;
    public EncryptionConfigMode Mode;;
    public EncryptionMetadata? SealedConfiguration;;
    public bool IsConfigurationSealed;;
    public bool TrySeal(EncryptionMetadata config);
    public void LoadSealedConfig(EncryptionMetadata config);
    public (bool IsValid, string? Error) ValidateConfig(EncryptionMetadata metadata, EncryptionMetadata? existingConfig, EncryptionPolicy? policy);
    public (bool IsAllowed, string? Error) CanWriteWithConfig(EncryptionMetadata proposedConfig, EncryptionMetadata? existingConfig, EncryptionPolicy? policy);
    public (bool IsSealed, string? Error) IsSealedAfterFirstWrite(EncryptionMetadata? existingConfig);
}
```
```csharp
public sealed class PolicyEnforcedConfigMode : IEncryptionConfigMode
{
}
    public const string ModeId = "PolicyEnforced";
    public string Id;;
    public string DisplayName;;
    public string Description;;
    public EncryptionConfigMode Mode;;
    public EncryptionPolicy? Policy { get => _policy; set => _policy = value; }
    public PolicyEnforcedConfigMode(EncryptionPolicy? policy = null);
    public (bool IsValid, string? Error) ValidateConfig(EncryptionMetadata metadata, EncryptionMetadata? existingConfig, EncryptionPolicy? policy);
    public (bool IsAllowed, string? Error) CanWriteWithConfig(EncryptionMetadata proposedConfig, EncryptionMetadata? existingConfig, EncryptionPolicy? policy);
    public (bool IsSealed, string? Error) IsSealedAfterFirstWrite(EncryptionMetadata? existingConfig);
}
```
```csharp
public interface IEncryptionConfigMode
{
}
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    EncryptionConfigMode Mode { get; }
    (bool IsValid, string? Error) ValidateConfig(EncryptionMetadata metadata, EncryptionMetadata? existingConfig, EncryptionPolicy? policy);;
    (bool IsAllowed, string? Error) CanWriteWithConfig(EncryptionMetadata proposedConfig, EncryptionMetadata? existingConfig, EncryptionPolicy? policy);;
    (bool IsSealed, string? Error) IsSealedAfterFirstWrite(EncryptionMetadata? existingConfig);;
}
```
```csharp
public static class EncryptionConfigModeFactory
{
}
    public static IEncryptionConfigMode Create(EncryptionConfigMode mode, EncryptionPolicy? policy = null);
    public static IEncryptionConfigMode CreateFromId(string modeId, EncryptionPolicy? policy = null);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Features/EnvelopeVerification.cs
```csharp
public sealed class EnvelopeVerification
{
#endregion
}
    public EnvelopeVerification(IKeyStoreRegistry registry);
    public async Task<EnvelopeVerificationReport> VerifyAllEnvelopeKeyStoresAsync(CancellationToken ct = default);
    public async Task<EnvelopeStoreVerificationResult> VerifyEnvelopeKeyStoreAsync(string storeId, IEnvelopeKeyStore keyStore, CancellationToken ct = default);
    public bool SupportsEnvelopeEncryption(string storeId);
    public async Task<EnvelopeCapabilityReport> GetEnvelopeCapabilitiesAsync(string storeId, CancellationToken ct = default);
    public EnvelopeReadinessCheck CheckEnvelopeReadiness(string storeId, string kekId);
    public IReadOnlyList<EnvelopeStoreInfo> GetRegisteredEnvelopeStores();
    public void ClearCache();
}
```
```csharp
private class InterfaceVerification
{
}
    public bool HasWrapKey { get; set; }
    public bool HasUnwrapKey { get; set; }
    public bool HasSupportedAlgorithms { get; set; }
    public bool HasHsmSupport { get; set; }
}
```
```csharp
public class EnvelopeVerificationReport
{
}
    public DateTime VerifiedAt { get; set; }
    public int TotalStores { get; set; }
    public int ValidStores { get; set; }
    public int InvalidStores { get; set; }
    public List<EnvelopeStoreVerificationResult> Results { get; set; };
    public bool AllValid;;
}
```
```csharp
public class EnvelopeStoreVerificationResult
{
}
    public string StoreId { get; set; };
    public string StoreType { get; set; };
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }
    public bool HasWrapKeyMethod { get; set; }
    public bool HasUnwrapKeyMethod { get; set; }
    public bool HasSupportedAlgorithms { get; set; }
    public bool HasHsmSupport { get; set; }
    public bool WrapKeySignatureValid { get; set; }
    public bool UnwrapKeySignatureValid { get; set; }
    public List<string> SupportedAlgorithms { get; set; };
    public bool SupportsHsmKeyGeneration { get; set; }
    public CapabilityProbeResult? CapabilityProbeResult { get; set; }
}
```
```csharp
public class CapabilityProbeResult
{
}
    public DateTime ProbeTime { get; set; }
    public bool ProbeSuccessful { get; set; }
    public string? ProbeError { get; set; }
    public bool CanGetCurrentKey { get; set; }
    public string? CurrentKeyId { get; set; }
    public bool HealthCheckPassed { get; set; }
}
```
```csharp
public class EnvelopeCapabilityReport
{
}
    public string StoreId { get; set; };
    public string StoreType { get; set; };
    public bool IsAvailable { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> SupportedAlgorithms { get; set; };
    public bool SupportsHsmKeyGeneration { get; set; }
    public bool SupportsRotation { get; set; }
    public bool SupportsVersioning { get; set; }
    public bool SupportsAuditLogging { get; set; }
    public bool SupportsReplication { get; set; }
    public Dictionary<string, object> Metadata { get; set; };
    public DateTime CachedAt { get; set; }
}
```
```csharp
public class EnvelopeReadinessCheck
{
}
    public string StoreId { get; set; };
    public string KekId { get; set; };
    public DateTime CheckedAt { get; set; }
    public bool IsReady { get; set; }
    public bool StoreRegistered { get; set; }
    public bool HasRequiredMethods { get; set; }
    public bool HasSupportedAlgorithms { get; set; }
    public bool KekIdProvided { get; set; }
    public List<string> Issues { get; set; };
}
```
```csharp
public class EnvelopeStoreInfo
{
}
    public string StoreId { get; set; };
    public string StoreType { get; set; };
    public bool SupportsEnvelope { get; set; }
    public bool SupportsRotation { get; set; }
    public bool SupportsHsm { get; set; }
    public bool SupportsHsmKeyGeneration { get; set; }
    public List<string> SupportedAlgorithms { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Features/ZeroDowntimeRotation.cs
```csharp
public sealed class ZeroDowntimeRotation : IDisposable
{
}
    public TimeSpan DefaultDualKeyPeriod { get; set; };
    public int MaxConcurrentReEncryption { get; set; };
    public ZeroDowntimeRotation(IKeyStoreRegistry registry, IMessageBus? messageBus = null);
    public async Task<RotationSession> InitiateRotationAsync(string keyStoreId, string currentKeyId, ISecurityContext context, RotationOptions? options = null, CancellationToken ct = default);
    public KeyValidationResult ValidateKey(string keyStoreId, string keyId);
    public async Task<RecommendedKey> GetRecommendedKeyAsync(string keyStoreId, ISecurityContext context, CancellationToken ct = default);
    public async Task<ReEncryptionJob> StartReEncryptionAsync(string sessionId, IEnumerable<ReEncryptionItem> items, ISecurityContext context, CancellationToken ct = default);
    public async Task<RotationCompletionResult> CompleteRotationAsync(string sessionId, ISecurityContext context, bool force = false, CancellationToken ct = default);
    public async Task<bool> CancelRotationAsync(string sessionId, ISecurityContext context, string? reason = null, CancellationToken ct = default);
    public RotationStatusReport? GetRotationStatus(string sessionId);
    public IReadOnlyList<RotationSession> GetActiveSessions();
    public IReadOnlyList<RotationHistory> GetRotationHistory(string? keyStoreId = null);
    public RotationVerification VerifyRotation(string sessionId);
    public void Dispose();
}
```
```csharp
public class RotationOptions
{
}
    public TimeSpan? DualKeyPeriod { get; set; }
    public string? NewKeyId { get; set; }
    public bool AutoReEncrypt { get; set; }
    public bool DeleteOldKeyOnComplete { get; set; }
    public Dictionary<string, object> Metadata { get; set; };
}
```
```csharp
public class RotationSession
{
}
    public string SessionId { get; set; };
    public string KeyStoreId { get; set; };
    public string CurrentKeyId { get; set; };
    public string NewKeyId { get; set; };
    public RotationStatus Status { get; set; }
    public DateTime InitiatedAt { get; set; }
    public string? InitiatedBy { get; set; }
    public DateTime DualKeyExpiresAt { get; set; }
    public DateTime? ReEncryptionStartedAt { get; set; }
    public DateTime? ReEncryptionCompletedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancellationReason { get; set; }
    public RotationOptions? Options { get; set; }
    public int NewKeySizeBytes { get; set; }
    public ReEncryptionStats? ReEncryptionStats { get; set; }
}
```
```csharp
public class KeyValidationResult
{
}
    public string KeyId { get; set; };
    public DateTime CheckedAt { get; set; }
    public bool IsValid { get; set; }
    public bool IsCurrentKey { get; set; }
    public bool IsNewKey { get; set; }
    public string? RotationSessionId { get; set; }
    public DateTime? DualKeyExpiresAt { get; set; }
    public string? RecommendedAction { get; set; }
}
```
```csharp
public class RecommendedKey
{
}
    public string KeyId { get; set; };
    public byte[] KeyMaterial { get; set; };
    public bool IsRotationKey { get; set; }
    public string? RotationSessionId { get; set; }
    public string? Reason { get; set; }
}
```
```csharp
public class ReEncryptionItem
{
}
    public string ItemId { get; set; };
    public Func<byte[], byte[], CancellationToken, Task>? ReEncryptCallback { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
```
```csharp
public class ReEncryptionJob
{
}
    public string JobId { get; set; };
    public string SessionId { get; set; };
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public ReEncryptionStatus Status { get; set; }
    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public int SuccessfulItems { get; set; }
    public int FailedItems { get; set; }
    public List<ReEncryptionResult> Results { get; set; };
}
```
```csharp
public class ReEncryptionResult
{
}
    public string ItemId { get; set; };
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
}
```
```csharp
public class ReEncryptionStats
{
}
    public int TotalItems { get; set; }
    public int SuccessfulItems { get; set; }
    public int FailedItems { get; set; }
    public long DurationMs { get; set; }
}
```
```csharp
public class RotationCompletionResult
{
}
    public string SessionId { get; set; };
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? NewKeyId { get; set; }
    public string? OldKeyId { get; set; }
    public DateTime CompletedAt { get; set; }
}
```
```csharp
public class RotationStatusReport
{
}
    public string SessionId { get; set; };
    public RotationStatus Status { get; set; }
    public bool IsActive { get; set; }
    public string CurrentKeyId { get; set; };
    public string NewKeyId { get; set; };
    public DateTime InitiatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? DualKeyExpiresAt { get; set; }
    public TimeSpan? DualKeyTimeRemaining { get; set; }
    public ReEncryptionStats? ReEncryptionStats { get; set; }
}
```
```csharp
public class RotationHistory
{
}
    public string SessionId { get; set; };
    public string KeyStoreId { get; set; };
    public string OldKeyId { get; set; };
    public string NewKeyId { get; set; };
    public DateTime InitiatedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string? InitiatedBy { get; set; }
    public string? CompletedBy { get; set; }
    public double DurationHours { get; set; }
    public bool WasCancelled { get; set; }
    public string? CancellationReason { get; set; }
    public ReEncryptionStats? ReEncryptionStats { get; set; }
}
```
```csharp
public class RotationVerification
{
}
    public string SessionId { get; set; };
    public DateTime VerifiedAt { get; set; }
    public bool IsComplete { get; set; }
    public RotationStatus Status { get; set; }
    public string? Message { get; set; }
    public string? NewKeyId { get; set; }
    public string? OldKeyId { get; set; }
    public bool NewKeyAccessible { get; set; }
    public ReEncryptionStats? ReEncryptionStats { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Features/KeyEscrowRecovery.cs
```csharp
public sealed class KeyEscrowRecovery : IDisposable
{
}
    public event EventHandler<RecoveryRequestEventArgs>? RecoveryRequested;
    public event EventHandler<AgentApprovalEventArgs>? AgentApproved;
    public event EventHandler<RecoveryCompletedEventArgs>? RecoveryCompleted;
    public KeyEscrowRecovery(IKeyStore keyStore, IMessageBus? messageBus = null);
    public async Task<EscrowAgent> RegisterAgentAsync(string agentId, EscrowAgentRegistration registration, ISecurityContext context, CancellationToken ct = default);
    public async Task<EscrowConfiguration> CreateEscrowConfigurationAsync(string configId, EscrowConfigurationRequest request, ISecurityContext context, CancellationToken ct = default);
    public async Task<EscrowedKeyInfo> EscrowKeyAsync(string keyId, string configId, ISecurityContext context, CancellationToken ct = default);
    public async Task<RecoveryRequest> RequestRecoveryAsync(string escrowId, RecoveryRequestDetails details, ISecurityContext context, CancellationToken ct = default);
    public async Task<ApprovalResult> ApproveRecoveryAsync(string requestId, string agentId, ApprovalDetails details, ISecurityContext context, CancellationToken ct = default);
    public async Task<bool> DenyRecoveryAsync(string requestId, string agentId, string reason, ISecurityContext context, CancellationToken ct = default);
    public async Task<RecoveryResult> ExecuteRecoveryAsync(string requestId, ISecurityContext context, CancellationToken ct = default);
    public RecoveryRequestStatus? GetRecoveryStatus(string requestId);
    public IReadOnlyList<RecoveryRequest> GetPendingRecoveryRequests(string? agentId = null);
    public IReadOnlyList<EscrowAgent> GetAgents(bool activeOnly = true);
    public async Task<bool> DeactivateAgentAsync(string agentId, ISecurityContext context, CancellationToken ct = default);
    public EscrowedKeyInfo? GetEscrowInfo(string escrowId);
    public IReadOnlyList<EscrowedKeyInfo> GetEscrowedKeys();
    public void Dispose();
}
```
```csharp
public class EscrowAgentRegistration
{
}
    public string Name { get; set; };
    public string Email { get; set; };
    public NotificationMethod NotificationMethod { get; set; };
    public byte[]? PublicKey { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
```
```csharp
public class EscrowAgent
{
}
    public string AgentId { get; set; };
    public string Name { get; set; };
    public string Email { get; set; };
    public NotificationMethod NotificationMethod { get; set; }
    public byte[]? PublicKey { get; set; }
    public DateTime RegisteredAt { get; set; }
    public string? RegisteredBy { get; set; }
    public bool IsActive { get; set; };
    public DateTime? DeactivatedAt { get; set; }
    public string? DeactivatedBy { get; set; }
    public Dictionary<string, object> Metadata { get; set; };
}
```
```csharp
public class EscrowConfigurationRequest
{
}
    public int RequiredApprovals { get; set; }
    public List<string> AgentIds { get; set; };
    public int ApprovalTimeoutHours { get; set; };
    public bool RequiresReason { get; set; };
    public bool RequiresManagerApproval { get; set; }
    public List<string>? AllowedRecoveryContexts { get; set; }
}
```
```csharp
public class EscrowConfiguration
{
}
    public string ConfigId { get; set; };
    public int RequiredApprovals { get; set; }
    public int TotalAgents { get; set; }
    public List<string> AgentIds { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public int ApprovalTimeoutHours { get; set; }
    public bool RequiresReason { get; set; }
    public bool RequiresManagerApproval { get; set; }
    public List<string> AllowedRecoveryContexts { get; set; };
}
```
```csharp
public class EscrowedKeyInfo
{
}
    public string EscrowId { get; set; };
    public string KeyId { get; set; };
    public string ConfigId { get; set; };
    public DateTime EscrowedAt { get; set; }
    public string? EscrowedBy { get; set; }
    public int KeySizeBytes { get; set; }
    public int ShareCount { get; set; }
    public int ThresholdRequired { get; set; }
    public Dictionary<string, byte[]> EncryptedShares { get; set; };
    public EscrowStatus Status { get; set; }
}
```
```csharp
public class RecoveryRequestDetails
{
}
    public string? Reason { get; set; }
    public string? RecoveryContext { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
```
```csharp
public class RecoveryRequest
{
}
    public string RequestId { get; set; };
    public string EscrowId { get; set; };
    public string KeyId { get; set; };
    public string ConfigId { get; set; };
    public DateTime RequestedAt { get; set; }
    public string? RequestedBy { get; set; }
    public string? Reason { get; set; }
    public string? RecoveryContext { get; set; }
    public int RequiredApprovals { get; set; }
    public DateTime ExpiresAt { get; set; }
    public RecoveryRequestStatus Status { get; set; }
    public List<AgentApproval> Approvals { get; set; };
    public string? DeniedBy { get; set; }
    public string? DenialReason { get; set; }
    public DateTime? DeniedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
}
```
```csharp
public class AgentApproval
{
}
    public string AgentId { get; set; };
    public DateTime ApprovedAt { get; set; }
    public string? Comment { get; set; }
    public byte[]? Share { get; set; }
}
```
```csharp
public class ApprovalDetails
{
}
    public string? Comment { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
```
```csharp
public class ApprovalResult
{
}
    public bool Success { get; set; }
    public string? Message { get; set; }
    public int ApprovalCount { get; set; }
    public int RequiredApprovals { get; set; }
    public bool RecoveryAuthorized { get; set; }
}
```
```csharp
public class RecoveryResult
{
}
    public bool Success { get; set; }
    public string? Message { get; set; }
    public byte[]? RecoveredKey { get; set; }
    public string? RequestId { get; set; }
    public string? KeyId { get; set; }
}
```
```csharp
public class RecoveryRequestEventArgs : EventArgs
{
}
    public required RecoveryRequest Request { get; set; }
}
```
```csharp
public class AgentApprovalEventArgs : EventArgs
{
}
    public string RequestId { get; set; };
    public string AgentId { get; set; };
    public int TotalApprovals { get; set; }
    public int RequiredApprovals { get; set; }
}
```
```csharp
public class RecoveryCompletedEventArgs : EventArgs
{
}
    public string RequestId { get; set; };
    public string KeyId { get; set; };
    public string? RecoveredBy { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Features/TamperProofEncryptionIntegration.cs
```csharp
public sealed class TamperProofEncryptionIntegration
{
#endregion
}
    public TamperProofEncryptionIntegration(IKeyStoreRegistry? keyStoreRegistry = null, IKeyManagementConfigProvider? configProvider = null, EncryptionConfigMode configMode = EncryptionConfigMode.PerObjectConfig);
    public IEncryptionConfigMode ConfigMode { get => _configMode; set => _configMode = value ?? throw new ArgumentNullException(nameof(value)); }
    public async Task<WriteTimeEncryptionConfig> ResolveWriteConfigAsync(ISecurityContext context, KeyManagementConfig? preferredConfig, string encryptionPluginId, TamperProofManifest? existingManifest = null, EncryptionPolicy? policy = null, CancellationToken cancellationToken = default);
    public Dictionary<string, object> CreateManifestUserMetadata(WriteTimeEncryptionConfig writeConfig, Dictionary<string, object>? existingUserMetadata = null);
    public async Task<ReadTimeEncryptionConfig> ExtractReadConfigAsync(TamperProofManifest manifest, ISecurityContext context, CancellationToken cancellationToken = default);
    public bool IsEncryptedContent(TamperProofManifest manifest);
}
```
```csharp
public sealed class WriteTimeEncryptionConfig
{
}
    public required ResolvedKeyManagementConfig ResolvedConfig { get; init; }
    public required EncryptionMetadata EncryptionMetadata { get; init; }
    public required EncryptionConfigMode ConfigMode { get; init; }
    public EncryptionPolicy? Policy { get; init; }
}
```
```csharp
public sealed class ReadTimeEncryptionConfig
{
}
    public required ResolvedKeyManagementConfig ResolvedConfig { get; init; }
    public required EncryptionMetadata EncryptionMetadata { get; init; }
    public required EncryptionConfigMode ConfigMode { get; init; }
    public required int ManifestVersion { get; init; }
    public required Guid ObjectId { get; init; }
    public required int ObjectVersion { get; init; }
}
```
```csharp
public sealed class TamperProofEncryptionIntegrationBuilder
{
}
    public TamperProofEncryptionIntegrationBuilder WithKeyStoreRegistry(IKeyStoreRegistry registry);
    public TamperProofEncryptionIntegrationBuilder WithConfigProvider(IKeyManagementConfigProvider provider);
    public TamperProofEncryptionIntegrationBuilder WithConfigMode(EncryptionConfigMode mode);
    public TamperProofEncryptionIntegrationBuilder WithPolicy(EncryptionPolicy policy);
    public TamperProofEncryptionIntegration Build();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Features/BreakGlassAccess.cs
```csharp
public sealed class BreakGlassAccess : IDisposable
{
}
    public event EventHandler<BreakGlassInitiatedEventArgs>? AccessInitiated;
    public event EventHandler<BreakGlassAuthorizedEventArgs>? AccessAuthorized;
    public event EventHandler<BreakGlassKeyAccessedEventArgs>? KeyAccessed;
    public event EventHandler<BreakGlassSessionEndedEventArgs>? SessionEnded;
    public TimeSpan DefaultSessionTimeout { get; set; };
    public int MaxKeysPerSession { get; set; };
    public bool RequireSecurityNotification { get; set; };
    public BreakGlassAccess(IKeyStore keyStore, IMessageBus? messageBus = null);
    public async Task<AuthorizedResponder> RegisterResponderAsync(string responderId, ResponderRegistration registration, ISecurityContext context, CancellationToken ct = default);
    public async Task<EmergencyAccessPolicy> CreatePolicyAsync(string policyId, EmergencyAccessPolicyRequest request, ISecurityContext context, CancellationToken ct = default);
    public async Task<BreakGlassSession> InitiateBreakGlassAsync(string policyId, BreakGlassRequest request, ISecurityContext context, CancellationToken ct = default);
    public async Task<AuthorizationResult> AuthorizeSessionAsync(string sessionId, string responderId, AuthorizationDetails details, ISecurityContext context, CancellationToken ct = default);
    public async Task<bool> DenySessionAsync(string sessionId, string responderId, string reason, ISecurityContext context, CancellationToken ct = default);
    public async Task<BreakGlassKeyAccessResult> AccessKeyAsync(string sessionId, string keyId, string accessReason, ISecurityContext context, CancellationToken ct = default);
    public async Task<bool> EndSessionAsync(string sessionId, string reason, ISecurityContext context, CancellationToken ct = default);
    public BreakGlassSessionStatus? GetSessionStatus(string sessionId);
    public BreakGlassSession? GetSession(string sessionId);
    public IReadOnlyList<BreakGlassSession> GetActiveSessions();
    public IReadOnlyList<BreakGlassAuditEntry> GetSessionAuditLog(string sessionId);
    public IReadOnlyList<BreakGlassAuditEntry> GetAuditLog(DateTime? fromDate = null, DateTime? toDate = null);
    public IReadOnlyList<AuthorizedResponder> GetResponders(bool activeOnly = true);
    public async Task<bool> DeactivateResponderAsync(string responderId, ISecurityContext context, CancellationToken ct = default);
    public BreakGlassComplianceReport GenerateComplianceReport(DateTime fromDate, DateTime toDate);
    public void Dispose();
}
```
```csharp
public class ResponderRegistration
{
}
    public string Name { get; set; };
    public string Email { get; set; };
    public string? Phone { get; set; }
    public ResponderRole Role { get; set; }
    public AuthorizationLevel AuthorizationLevel { get; set; };
    public bool CanInitiateBreakGlass { get; set; }
    public bool CanAuthorizeBreakGlass { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
```
```csharp
public class AuthorizedResponder
{
}
    public string ResponderId { get; set; };
    public string Name { get; set; };
    public string Email { get; set; };
    public string? Phone { get; set; }
    public ResponderRole Role { get; set; }
    public AuthorizationLevel AuthorizationLevel { get; set; }
    public bool CanInitiateBreakGlass { get; set; }
    public bool CanAuthorizeBreakGlass { get; set; }
    public DateTime RegisteredAt { get; set; }
    public string? RegisteredBy { get; set; }
    public bool IsActive { get; set; };
    public DateTime? DeactivatedAt { get; set; }
    public string? DeactivatedBy { get; set; }
    public Dictionary<string, object> Metadata { get; set; };
}
```
```csharp
public class EmergencyAccessPolicyRequest
{
}
    public string Name { get; set; };
    public string? Description { get; set; }
    public int RequiredAuthorizationCount { get; set; }
    public List<string> AuthorizedResponderIds { get; set; };
    public List<string>? AllowedKeyPatterns { get; set; }
    public double? MaxSessionDurationHours { get; set; }
    public bool RequiresJustification { get; set; };
    public bool RequiresIncidentTicket { get; set; }
    public List<EmergencyType>? AllowedEmergencyTypes { get; set; }
}
```
```csharp
public class EmergencyAccessPolicy
{
}
    public string PolicyId { get; set; };
    public string Name { get; set; };
    public string? Description { get; set; }
    public int RequiredAuthorizationCount { get; set; }
    public List<string> AuthorizedResponderIds { get; set; };
    public List<string> AllowedKeyPatterns { get; set; };
    public double? MaxSessionDurationHours { get; set; }
    public bool RequiresJustification { get; set; }
    public bool RequiresIncidentTicket { get; set; }
    public List<EmergencyType> AllowedEmergencyTypes { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public bool IsActive { get; set; }
}
```
```csharp
public class BreakGlassRequest
{
}
    public string? InitiatorResponderId { get; set; }
    public EmergencyType EmergencyType { get; set; }
    public string? Justification { get; set; }
    public string? IncidentTicketId { get; set; }
    public List<string>? RequestedKeyPatterns { get; set; }
}
```
```csharp
public class BreakGlassSession
{
}
    public string SessionId { get; set; };
    public string PolicyId { get; set; };
    public DateTime InitiatedAt { get; set; }
    public string? InitiatedBy { get; set; }
    public EmergencyType EmergencyType { get; set; }
    public string? Justification { get; set; }
    public string? IncidentTicketId { get; set; }
    public List<string> RequestedKeyPatterns { get; set; };
    public BreakGlassSessionStatus Status { get; set; }
    public int RequiredAuthorizations { get; set; }
    public List<BreakGlassAuthorization> Authorizations { get; set; };
    public DateTime? ActivatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndedBy { get; set; }
    public string? EndReason { get; set; }
    public DateTime? DeniedAt { get; set; }
    public string? DeniedBy { get; set; }
    public string? DenialReason { get; set; }
    public List<KeyAccessLogEntry> KeyAccessLog { get; set; };
    public int TotalKeysAccessed { get; set; }
}
```
```csharp
public class BreakGlassAuthorization
{
}
    public string ResponderId { get; set; };
    public DateTime AuthorizedAt { get; set; }
    public string? Comment { get; set; }
    public VerificationMethod VerificationMethod { get; set; }
}
```
```csharp
public class AuthorizationDetails
{
}
    public string? Comment { get; set; }
    public VerificationMethod? VerificationMethod { get; set; }
}
```
```csharp
public class AuthorizationResult
{
}
    public bool Success { get; set; }
    public string? Message { get; set; }
    public int AuthorizationCount { get; set; }
    public int RequiredAuthorizations { get; set; }
    public bool SessionActive { get; set; }
}
```
```csharp
public class KeyAccessLogEntry
{
}
    public string KeyId { get; set; };
    public DateTime AccessedAt { get; set; }
    public string? AccessedBy { get; set; }
    public string? Reason { get; set; }
    public int KeySizeBytes { get; set; }
}
```
```csharp
public class BreakGlassKeyAccessResult
{
}
    public bool Success { get; set; }
    public string? Message { get; set; }
    public byte[]? KeyData { get; set; }
    public string? KeyId { get; set; }
    public string? SessionId { get; set; }
}
```
```csharp
public class BreakGlassAuditEntry
{
}
    public string EntryId { get; set; };
    public DateTime Timestamp { get; set; }
    public BreakGlassAction Action { get; set; }
    public string? SessionId { get; set; }
    public string? PolicyId { get; set; }
    public string? ResponderId { get; set; }
    public string? KeyId { get; set; }
    public string? PerformedBy { get; set; }
    public string? Details { get; set; }
    public EmergencyType? EmergencyType { get; set; }
    public string? IncidentTicketId { get; set; }
}
```
```csharp
public class BreakGlassComplianceReport
{
}
    public DateTime ReportPeriodStart { get; set; }
    public DateTime ReportPeriodEnd { get; set; }
    public DateTime GeneratedAt { get; set; }
    public int TotalSessionsInitiated { get; set; }
    public int TotalSessionsAuthorized { get; set; }
    public int TotalSessionsDenied { get; set; }
    public int TotalKeysAccessed { get; set; }
    public Dictionary<EmergencyType, int> SessionsByEmergencyType { get; set; };
    public double AverageSessionDurationMinutes { get; set; }
    public int AuditEntryCount { get; set; }
    public List<BreakGlassSessionSummary> Sessions { get; set; };
}
```
```csharp
public class BreakGlassSessionSummary
{
}
    public string SessionId { get; set; };
    public DateTime InitiatedAt { get; set; }
    public string? InitiatedBy { get; set; }
    public EmergencyType EmergencyType { get; set; }
    public BreakGlassSessionStatus Status { get; set; }
    public int KeysAccessed { get; set; }
    public string? IncidentTicketId { get; set; }
}
```
```csharp
public class BreakGlassInitiatedEventArgs : EventArgs
{
}
    public required BreakGlassSession Session { get; set; }
    public required EmergencyAccessPolicy Policy { get; set; }
}
```
```csharp
public class BreakGlassAuthorizedEventArgs : EventArgs
{
}
    public required BreakGlassSession Session { get; set; }
    public required AuthorizedResponder FinalAuthorizer { get; set; }
}
```
```csharp
public class BreakGlassKeyAccessedEventArgs : EventArgs
{
}
    public string SessionId { get; set; };
    public string KeyId { get; set; };
    public string? AccessedBy { get; set; }
    public string? Reason { get; set; }
}
```
```csharp
public class BreakGlassSessionEndedEventArgs : EventArgs
{
}
    public required BreakGlassSession Session { get; set; }
    public string? EndedBy { get; set; }
    public string? Reason { get; set; }
}
```
```csharp
internal sealed class EmergencySecurityContext : ISecurityContext
{
}
    public EmergencySecurityContext(ISecurityContext originalContext, BreakGlassSession session);
    public string UserId;;
    public string? TenantId;;
    public IEnumerable<string> Roles;;
    public bool IsSystemAdmin;;
    public string SessionId;;
    public EmergencyType EmergencyType;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Container/ExternalSecretsStrategy.cs
```csharp
public sealed class ExternalSecretsStrategy : KeyStoreStrategyBase
{
#endregion
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
private class ExternalSecretList
{
}
    [JsonPropertyName("items")]
public List<ExternalSecretResource>? Items { get; set; }
}
```
```csharp
private class ExternalSecretResource
{
}
    [JsonPropertyName("apiVersion")]
public string? ApiVersion { get; set; }
    [JsonPropertyName("kind")]
public string? Kind { get; set; }
    [JsonPropertyName("metadata")]
public V1ObjectMeta? Metadata { get; set; }
    [JsonPropertyName("spec")]
public ExternalSecretSpec? Spec { get; set; }
    [JsonPropertyName("status")]
public ExternalSecretStatus? Status { get; set; }
}
```
```csharp
private class ExternalSecretSpec
{
}
    [JsonPropertyName("refreshInterval")]
public string? RefreshInterval { get; set; }
    [JsonPropertyName("secretStoreRef")]
public SecretStoreRef? SecretStoreRef { get; set; }
    [JsonPropertyName("target")]
public ExternalSecretTarget? Target { get; set; }
    [JsonPropertyName("data")]
public List<ExternalSecretData>? Data { get; set; }
}
```
```csharp
private class SecretStoreRef
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("kind")]
public string? Kind { get; set; }
}
```
```csharp
private class ExternalSecretTarget
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("creationPolicy")]
public string? CreationPolicy { get; set; }
    [JsonPropertyName("deletionPolicy")]
public string? DeletionPolicy { get; set; }
}
```
```csharp
private class ExternalSecretData
{
}
    [JsonPropertyName("secretKey")]
public string? SecretKey { get; set; }
    [JsonPropertyName("remoteRef")]
public RemoteRef? RemoteRef { get; set; }
}
```
```csharp
private class RemoteRef
{
}
    [JsonPropertyName("key")]
public string? Key { get; set; }
    [JsonPropertyName("property")]
public string? Property { get; set; }
}
```
```csharp
private class ExternalSecretStatus
{
}
    [JsonPropertyName("conditions")]
public List<ExternalSecretCondition>? Conditions { get; set; }
}
```
```csharp
private class ExternalSecretCondition
{
}
    [JsonPropertyName("type")]
public string? Type { get; set; }
    [JsonPropertyName("status")]
public string? Status { get; set; }
}
```
```csharp
private class PushSecretResource
{
}
    [JsonPropertyName("apiVersion")]
public string? ApiVersion { get; set; }
    [JsonPropertyName("kind")]
public string? Kind { get; set; }
    [JsonPropertyName("metadata")]
public V1ObjectMeta? Metadata { get; set; }
    [JsonPropertyName("spec")]
public PushSecretSpec? Spec { get; set; }
}
```
```csharp
private class PushSecretSpec
{
}
    [JsonPropertyName("refreshInterval")]
public string? RefreshInterval { get; set; }
    [JsonPropertyName("secretStoreRefs")]
public List<PushSecretStoreRef>? SecretStoreRefs { get; set; }
    [JsonPropertyName("selector")]
public PushSecretSelector? Selector { get; set; }
    [JsonPropertyName("data")]
public List<PushSecretData>? Data { get; set; }
}
```
```csharp
private class PushSecretStoreRef
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("kind")]
public string? Kind { get; set; }
}
```
```csharp
private class PushSecretSelector
{
}
    [JsonPropertyName("secret")]
public SecretSelector? Secret { get; set; }
}
```
```csharp
private class SecretSelector
{
}
    [JsonPropertyName("name")]
public string? Name { get; set; }
}
```
```csharp
private class PushSecretData
{
}
    [JsonPropertyName("match")]
public PushSecretMatch? Match { get; set; }
}
```
```csharp
private class PushSecretMatch
{
}
    [JsonPropertyName("secretKey")]
public string? SecretKey { get; set; }
    [JsonPropertyName("remoteRef")]
public PushRemoteRef? RemoteRef { get; set; }
}
```
```csharp
private class PushRemoteRef
{
}
    [JsonPropertyName("remoteKey")]
public string? RemoteKey { get; set; }
    [JsonPropertyName("property")]
public string? Property { get; set; }
}
```
```csharp
public class ExternalSecretsConfig
{
}
    public string Namespace { get; set; };
    public string SecretStoreName { get; set; };
    public string SecretStoreKind { get; set; };
    public string RefreshInterval { get; set; };
    public string Provider { get; set; };
    public string RemoteKeyPrefix { get; set; };
    public string? KubeconfigPath { get; set; }
    public string SecretNamePrefix { get; set; };
    public string CreationPolicy { get; set; };
    public string DeletionPolicy { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Container/KubernetesSecretsStrategy.cs
```csharp
public sealed class KubernetesSecretsStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class KubernetesSecretsConfig
{
}
    public string Namespace { get; set; };
    public string SecretNamePrefix { get; set; };
    public string? KubeconfigPath { get; set; }
    public string? Context { get; set; }
    public string LabelSelector { get; set; };
    public bool UseInClusterConfig { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Container/DockerSecretsStrategy.cs
```csharp
public sealed class DockerSecretsStrategy : KeyStoreStrategyBase
{
#endregion
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
private class DockerInfo
{
}
    [JsonPropertyName("Swarm")]
public SwarmInfo? Swarm { get; set; }
}
```
```csharp
private class SwarmInfo
{
}
    [JsonPropertyName("LocalNodeState")]
public string? LocalNodeState { get; set; }
}
```
```csharp
private class DockerSecret
{
}
    [JsonPropertyName("ID")]
public string? Id { get; set; }
    [JsonPropertyName("Version")]
public DockerVersion? Version { get; set; }
    [JsonPropertyName("CreatedAt")]
public DateTime? CreatedAt { get; set; }
    [JsonPropertyName("Spec")]
public DockerSecretSpec? Spec { get; set; }
}
```
```csharp
private class DockerVersion
{
}
    [JsonPropertyName("Index")]
public long Index { get; set; }
}
```
```csharp
private class DockerSecretSpec
{
}
    [JsonPropertyName("Name")]
public string? Name { get; set; }
    [JsonPropertyName("Data")]
public string? Data { get; set; }
    [JsonPropertyName("Labels")]
public Dictionary<string, string>? Labels { get; set; }
}
```
```csharp
public class DockerSecretsConfig
{
}
    public string SecretsPath { get; set; };
    public string SecretNamePrefix { get; set; };
    public string DockerHost { get; set; };
    public string LabelPrefix { get; set; };
    public bool AllowApiWrites { get; set; };
    public string? TlsCertPath { get; set; }
    public string? TlsKeyPath { get; set; }
    public string? TlsCaPath { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Container/SopsStrategy.cs
```csharp
public sealed class SopsStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public SopsStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public async Task RotateKeyAsync(string keyId, CancellationToken cancellationToken);
    public string GenerateSopsConfig();
}
```
```csharp
public class SopsConfig
{
}
    public string SecretsDirectory { get; set; };
    public string? SopsPath { get; set; }
    public string Backend { get; set; };
    public string? AgeRecipient { get; set; }
    public string? AgeKeyFile { get; set; }
    public string? PgpFingerprint { get; set; }
    public string? KmsArn { get; set; }
    public string? GcpKmsResourceId { get; set; }
    public string? AzureKeyVaultUrl { get; set; }
    public string? VaultTransitPath { get; set; }
    public string FileFormat { get; set; };
    public string SecretNamePrefix { get; set; };
    public string? SopsConfigFile { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Container/SealedSecretsStrategy.cs
```csharp
public sealed class SealedSecretsStrategy : KeyStoreStrategyBase
{
#endregion
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
private class SealedSecretList
{
}
    [JsonPropertyName("items")]
public List<SealedSecretResource>? Items { get; set; }
}
```
```csharp
private class SealedSecretResource
{
}
    [JsonPropertyName("metadata")]
public V1ObjectMeta? Metadata { get; set; }
    [JsonPropertyName("spec")]
public SealedSecretSpec? Spec { get; set; }
}
```
```csharp
private class SealedSecretSpec
{
}
    [JsonPropertyName("encryptedData")]
public Dictionary<string, string>? EncryptedData { get; set; }
}
```
```csharp
public class SealedSecretsConfig
{
}
    public string Namespace { get; set; };
    public string ControllerNamespace { get; set; };
    public string ControllerName { get; set; };
    public string? KubesealPath { get; set; }
    public string? CertificatePath { get; set; }
    public string Scope { get; set; };
    public string? KubeconfigPath { get; set; }
    public string SecretNamePrefix { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/PasswordDerived/PasswordDerivedBalloonStrategy.cs
```csharp
public sealed class PasswordDerivedBalloonStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class BalloonConfig
{
}
    public int SpaceCost { get; set; };
    public int TimeCost { get; set; };
    public int Delta { get; set; };
    public int KeySizeBytes { get; set; };
    public int SaltSizeBytes { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class BalloonEncryptedKeyData
{
}
    public string KeyId { get; set; };
    public byte[] Salt { get; set; };
    public byte[] EncryptedKey { get; set; };
    public byte[] Nonce { get; set; };
    public byte[] Tag { get; set; };
    public int SpaceCost { get; set; }
    public int TimeCost { get; set; }
    public int Delta { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Version { get; set; }
}
```
```csharp
internal class BalloonStoredKeyFile
{
}
    public string? CurrentKeyId { get; set; }
    public List<BalloonEncryptedKeyData> Keys { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/PasswordDerived/PasswordDerivedPbkdf2Strategy.cs
```csharp
public sealed class PasswordDerivedPbkdf2Strategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class Pbkdf2Config
{
}
    public int Iterations { get; set; };
    public HashAlgorithmName HashAlgorithmName { get; set; };
    public int KeySizeBytes { get; set; };
    public int SaltSizeBytes { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class Pbkdf2EncryptedKeyData
{
}
    public string KeyId { get; set; };
    public byte[] Salt { get; set; };
    public byte[] EncryptedKey { get; set; };
    public byte[] Nonce { get; set; };
    public byte[] Tag { get; set; };
    public int Iterations { get; set; }
    public string HashAlgorithm { get; set; };
    public DateTime CreatedAt { get; set; }
    public int Version { get; set; }
}
```
```csharp
internal class Pbkdf2StoredKeyFile
{
}
    public string? CurrentKeyId { get; set; }
    public List<Pbkdf2EncryptedKeyData> Keys { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/PasswordDerived/PasswordDerivedArgon2Strategy.cs
```csharp
public sealed class PasswordDerivedArgon2Strategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class Argon2Config
{
}
    public int MemoryKiB { get; set; };
    public int Iterations { get; set; };
    public int Parallelism { get; set; };
    public int KeySizeBytes { get; set; };
    public int SaltSizeBytes { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class EncryptedKeyData
{
}
    public string KeyId { get; set; };
    public byte[] Salt { get; set; };
    public byte[] EncryptedKey { get; set; };
    public byte[] Nonce { get; set; };
    public byte[] Tag { get; set; };
    public int MemoryKiB { get; set; }
    public int Iterations { get; set; }
    public int Parallelism { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Version { get; set; }
}
```
```csharp
internal class StoredKeyFile
{
}
    public string? CurrentKeyId { get; set; }
    public List<EncryptedKeyData> Keys { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/PasswordDerived/PasswordDerivedScryptStrategy.cs
```csharp
public sealed class PasswordDerivedScryptStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class ScryptConfig
{
}
    public int N { get; set; };
    public int R { get; set; };
    public int P { get; set; };
    public int KeySizeBytes { get; set; };
    public int SaltSizeBytes { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class ScryptEncryptedKeyData
{
}
    public string KeyId { get; set; };
    public byte[] Salt { get; set; };
    public byte[] EncryptedKey { get; set; };
    public byte[] Nonce { get; set; };
    public byte[] Tag { get; set; };
    public int N { get; set; }
    public int R { get; set; }
    public int P { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Version { get; set; }
}
```
```csharp
internal class ScryptStoredKeyFile
{
}
    public string? CurrentKeyId { get; set; }
    public List<ScryptEncryptedKeyData> Keys { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Local/FileKeyStoreStrategy.cs
```csharp
public sealed class FileKeyStoreStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
}
```
```csharp
internal interface IKeyProtectionTier
{
}
    string Name { get; }
    bool IsAvailable { get; }
    byte[] Encrypt(byte[] data);;
    byte[] Decrypt(byte[] encryptedData);;
}
```
```csharp
internal class DpapiTier : IKeyProtectionTier
{
}
    public string Name;;
    public bool IsAvailable;;
    public DpapiTier(FileKeyStoreConfig config);
    public byte[] Encrypt(byte[] data);
    public byte[] Decrypt(byte[] encryptedData);
}
```
```csharp
internal class CredentialManagerTier : IKeyProtectionTier
{
}
    public string Name;;
    public bool IsAvailable;;
    public CredentialManagerTier(FileKeyStoreConfig config);
    public byte[] Encrypt(byte[] data);
    public byte[] Decrypt(byte[] encryptedData);
}
```
```csharp
internal class DatabaseTier : IKeyProtectionTier
{
}
    public string Name;;
    public bool IsAvailable;;
    public DatabaseTier(FileKeyStoreConfig config);
    public byte[] Encrypt(byte[] data);
    public byte[] Decrypt(byte[] encryptedData);
}
```
```csharp
internal class PasswordTier : IKeyProtectionTier
{
}
    public string Name;;
    public bool IsAvailable;;
    public PasswordTier(FileKeyStoreConfig config);
    public byte[] Encrypt(byte[] data);
    public byte[] Decrypt(byte[] encryptedData);
}
```
```csharp
public class FileKeyStoreConfig
{
}
    public string KeyStorePath { get; set; };
    public string MasterKeyEnvVar { get; set; };
    public int KeySizeBytes { get; set; };
    public bool RequireAuthentication { get; set; };
    public bool RequireAdminForCreate { get; set; };
    public DpapiScope DpapiScope { get; set; };
    public string? DpapiEntropy { get; set; }
    public string? CredentialManagerTarget { get; set; }
    public string? FallbackPassword { get; set; }
}
```
```csharp
internal class KeyStoreMetadata
{
}
    public string CurrentKeyId { get; set; };
    public DateTime LastUpdated { get; set; }
    public int Version { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/GcpCloudHsmStrategy.cs
```csharp
public sealed class GcpCloudHsmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public async Task SetRotationScheduleAsync(string keyId, TimeSpan rotationPeriod, CancellationToken cancellationToken = default);
    public async Task<string> RotateKeyAsync(string keyId, CancellationToken cancellationToken = default);
    public async Task<IReadOnlyList<CryptoKeyVersionInfo>> GetKeyVersionsAsync(string keyId, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class GcpCloudHsmConfig
{
}
    public string ProjectId { get; set; };
    public string Location { get; set; };
    public string KeyRing { get; set; };
    public string KeyName { get; set; };
    public string ServiceAccountJson { get; set; };
    public string ProtectionLevel { get; set; };
    public string Algorithm { get; set; };
}
```
```csharp
public class CryptoKeyVersionInfo
{
}
    public string Name { get; set; };
    public string State { get; set; };
    public string Algorithm { get; set; };
    public string ProtectionLevel { get; set; };
    public DateTime? CreateTime { get; set; }
    public DateTime? DestroyTime { get; set; }
    public DateTime? DestroyEventTime { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/UtimacoStrategy.cs
```csharp
public sealed class UtimacoStrategy : Pkcs11HsmStrategyBase
{
}
    protected override string VendorName;;
    protected override string DefaultLibraryPath;;
    protected override Dictionary<string, object> VendorMetadata;;
}
```
```csharp
public class UtimacoConfig : Pkcs11HsmConfig
{
}
    public string? ConfigFile { get; set; }
    public bool UseSimulator { get; set; }
    public string? DeviceAddress { get; set; }
    public int OperationTimeoutMs { get; set; };
    public bool EnableKeyBackup { get; set; }
    public string? AuthDomain { get; set; }
    public bool EnableAuditLog { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/Pkcs11HsmStrategyBase.cs
```csharp
public abstract class Pkcs11HsmStrategyBase : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    protected abstract string VendorName { get; }
    protected abstract string DefaultLibraryPath { get; }
    protected abstract Dictionary<string, object> VendorMetadata { get; }
    public override KeyStoreCapabilities Capabilities;;
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    public override Task<string> GetCurrentKeyIdAsync();
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public async Task<IObjectHandle> GenerateHsmKeyAsync(string keyLabel, bool extractable = false);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public override void Dispose();
}
```
```csharp
public class Pkcs11HsmBaseConfig
{
}
    public string LibraryPath { get; set; };
    public ulong? SlotId { get; set; }
    public string? TokenLabel { get; set; }
    public string? Pin { get; set; }
    public bool UseUserPin { get; set; };
    public string DefaultKeyLabel { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/ThalesLunaStrategy.cs
```csharp
public sealed class ThalesLunaStrategy : Pkcs11HsmStrategyBase
{
}
    protected override string VendorName;;
    protected override string DefaultLibraryPath;;
    protected override Dictionary<string, object> VendorMetadata;;
}
```
```csharp
public class ThalesLunaConfig : Pkcs11HsmConfig
{
}
    public bool EnableHa { get; set; }
    public string? HaGroupLabel { get; set; }
    public int ConnectionTimeoutSeconds { get; set; };
    public bool EnableAutoReconnect { get; set; };
    public bool UsePedAuthentication { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/AzureDedicatedHsmStrategy.cs
```csharp
public sealed class AzureDedicatedHsmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public async Task<string> RotateKeyAsync(string keyId, CancellationToken cancellationToken = default);
    public async Task<IReadOnlyList<string>> GetKeyVersionsAsync(string keyId, CancellationToken cancellationToken = default);
    public async Task<byte[]> BackupKeyAsync(string keyId, CancellationToken cancellationToken = default);
    public async Task<string> RestoreKeyAsync(byte[] backup, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class AzureDedicatedHsmConfig
{
}
    public string ManagedHsmUrl { get; set; };
    public string TenantId { get; set; };
    public string ClientId { get; set; };
    public string ClientSecret { get; set; };
    public string DefaultKeyName { get; set; };
    public string KeyType { get; set; };
    public int KeySize { get; set; };
    public bool UseManagedIdentity { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/NcipherStrategy.cs
```csharp
public sealed class NcipherStrategy : Pkcs11HsmStrategyBase
{
}
    protected override string VendorName;;
    protected override string DefaultLibraryPath;;
    protected override Dictionary<string, object> VendorMetadata;;
}
```
```csharp
public class NcipherConfig : Pkcs11HsmConfig
{
}
    public string? SecurityWorldPath { get; set; }
    public NshieldProtectionMode ProtectionMode { get; set; };
    public string? OcsName { get; set; }
    public string? SoftcardName { get; set; }
    public int OcsQuorum { get; set; };
    public bool EnablePreload { get; set; }
    public string? PreloadPath { get; set; }
    public int? ModuleNumber { get; set; }
    public bool StrictFipsMode { get; set; }
    public int ConnectionTimeoutSeconds { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/HsmRotationStrategy.cs
```csharp
public sealed class HsmRotationStrategy : KeyStoreStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override KeyStoreCapabilities Capabilities;;
    protected override Task InitializeStorage(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<RotationResult> RotateKeyAsync(string keyId, ISecurityContext context, string reason = "Scheduled rotation");
    public async Task<RotationResult> EmergencyRotateAsync(string keyId, ISecurityContext context, string incidentId);
    public IReadOnlyList<RotationAuditEntry> GetAuditTrail(string keyId);
    public KeyVersionInfo? GetKeyVersionInfo(string keyId);
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public sealed class RotationResult
{
}
    public required string KeyId { get; init; }
    public required int NewVersion { get; init; }
    public required int PreviousVersion { get; init; }
    public required DateTime RotatedAt { get; init; }
    public required string Reason { get; init; }
    public required int RetainedVersionCount { get; init; }
    public required DateTime NextRotationDue { get; init; }
}
```
```csharp
public sealed class KeyVersionInfo
{
}
    public required string KeyId { get; init; }
    public required int CurrentVersion { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? LastRotatedAt { get; init; }
    public required DateTime NextRotationDue { get; init; }
    public required int RetainedVersionCount { get; init; }
    public required List<string> RetainedVersionIds { get; init; }
}
```
```csharp
public sealed class RotationAuditEntry
{
}
    public required DateTime Timestamp { get; init; }
    public required string KeyId { get; init; }
    public required string Action { get; init; }
    public required string Details { get; init; }
    public required string EntryId { get; init; }
}
```
```csharp
internal sealed class KeyVersion
{
}
    public required string KeyId { get; init; }
    public int Version { get; set; }
    public byte[] KeyMaterial { get; set; };
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastRotatedAt { get; set; }
    public List<ArchivedKeyVersion> PreviousVersions { get; set; };
}
```
```csharp
internal sealed class ArchivedKeyVersion
{
}
    public required string VersionedKeyId { get; init; }
    public required int Version { get; init; }
    public required byte[] KeyMaterial { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime RetiredAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
}
```
```csharp
internal sealed class SystemAdminSecurityContext : DataWarehouse.SDK.Security.ISecurityContext
{
}
    public string UserId;;
    public string? TenantId;;
    public IEnumerable<string> Roles;;
    public bool IsSystemAdmin;;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/AwsCloudHsmStrategy.cs
```csharp
public sealed class AwsCloudHsmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public AwsCloudHsmStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class AwsCloudHsmConfig
{
}
    public string ClusterId { get; set; };
    public string Region { get; set; };
    public string AccessKeyId { get; set; };
    public string SecretAccessKey { get; set; };
    public string HsmIpAddress { get; set; };
    public string CryptoUserName { get; set; };
    public string CryptoUserPassword { get; set; };
    public string DefaultKeyLabel { get; set; };
    public string CustomerCaCertPath { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/Pkcs11HsmStrategy.cs
```csharp
public sealed class Pkcs11HsmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class Pkcs11HsmConfig
{
}
    public string LibraryPath { get; set; };
    public ulong SlotId { get; set; };
    public string Pin { get; set; };
    public string DefaultKeyLabel { get; set; };
    public bool UseRsaWrapping { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/FortanixDsmStrategy.cs
```csharp
public sealed class FortanixDsmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public FortanixDsmStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class FortanixDsmConfig
{
}
    public string ApiEndpoint { get; set; };
    public string? ApiKey { get; set; }
    public string? AppUuid { get; set; }
    public string? AppSecret { get; set; }
    public string? AccountId { get; set; }
    public string? GroupId { get; set; }
    public string DefaultKeyName { get; set; };
    public int TimeoutSeconds { get; set; };
    public bool EnableRetry { get; set; };
    public int MaxRetries { get; set; };
}
```
```csharp
internal class DsmAuthResponse
{
}
    [JsonPropertyName("access_token")]
public string? AccessToken { get; set; }
    [JsonPropertyName("token_type")]
public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")]
public int ExpiresIn { get; set; }
}
```
```csharp
internal class DsmSecurityObject
{
}
    [JsonPropertyName("kid")]
public string Kid { get; set; };
    [JsonPropertyName("name")]
public string Name { get; set; };
    [JsonPropertyName("obj_type")]
public string? ObjType { get; set; }
    [JsonPropertyName("key_size")]
public int KeySize { get; set; }
    [JsonPropertyName("key_ops")]
public string[]? KeyOps { get; set; }
    [JsonPropertyName("enabled")]
public bool Enabled { get; set; }
    [JsonPropertyName("exportable")]
public bool Exportable { get; set; }
    [JsonPropertyName("group_id")]
public string? GroupId { get; set; }
    [JsonPropertyName("created_at")]
public DateTime? CreatedAt { get; set; }
    [JsonPropertyName("version")]
public int Version { get; set; };
}
```
```csharp
internal class DsmCreateKeyRequest
{
}
    [JsonPropertyName("name")]
public string Name { get; set; };
    [JsonPropertyName("obj_type")]
public string ObjType { get; set; };
    [JsonPropertyName("key_size")]
public int KeySize { get; set; };
    [JsonPropertyName("key_ops")]
public string[]? KeyOps { get; set; }
    [JsonPropertyName("value")]
public string? Value { get; set; }
    [JsonPropertyName("exportable")]
public bool Exportable { get; set; }
    [JsonPropertyName("enabled")]
public bool Enabled { get; set; };
    [JsonPropertyName("group_id")]
public string? GroupId { get; set; }
}
```
```csharp
internal class DsmKeyReference
{
}
    [JsonPropertyName("kid")]
public string? Kid { get; set; }
    [JsonPropertyName("name")]
public string? Name { get; set; }
}
```
```csharp
internal class DsmWrapRequest
{
}
    [JsonPropertyName("key")]
public DsmKeyReference? Key { get; set; }
    [JsonPropertyName("alg")]
public string Alg { get; set; };
    [JsonPropertyName("plain")]
public string? Plain { get; set; }
    [JsonPropertyName("mode")]
public string? Mode { get; set; }
    [JsonPropertyName("iv")]
public string? Iv { get; set; }
}
```
```csharp
internal class DsmWrapResponse
{
}
    [JsonPropertyName("wrapped")]
public string? Wrapped { get; set; }
    [JsonPropertyName("iv")]
public string? Iv { get; set; }
}
```
```csharp
internal class DsmUnwrapRequest
{
}
    [JsonPropertyName("key")]
public DsmKeyReference? Key { get; set; }
    [JsonPropertyName("alg")]
public string Alg { get; set; };
    [JsonPropertyName("wrapped")]
public string? Wrapped { get; set; }
    [JsonPropertyName("mode")]
public string? Mode { get; set; }
    [JsonPropertyName("iv")]
public string? Iv { get; set; }
}
```
```csharp
internal class DsmUnwrapResponse
{
}
    [JsonPropertyName("plain")]
public string? Plain { get; set; }
}
```
```csharp
internal class DsmExportResponse
{
}
    [JsonPropertyName("value")]
public string? Value { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Database/SqlTdeMetadataStrategy.cs
```csharp
public sealed class SqlTdeMetadataStrategy : KeyStoreStrategyBase
{
#endregion
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}
```
```csharp
public class SqlTdeConfig
{
}
    public string CertificateFilePath { get; set; };
    public string? PrivateKeyFilePath { get; set; }
    public string? PrivateKeyPassword { get; set; }
    public string? SqlConnectionString { get; set; }
    public string ExportDirectory { get; set; };
    public string MetadataStoragePath { get; set; };
}
```
```csharp
public class TdeCertificateMetadata
{
}
    public string CertificateId { get; set; };
    public string? Thumbprint { get; set; }
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public string? SerialNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? SignatureAlgorithm { get; set; }
    public byte[]? PublicKeyData { get; set; }
    public string? KeyAlgorithm { get; set; }
    public int KeySize { get; set; }
    public bool HasPrivateKey { get; set; }
    public bool PrivateKeyEncrypted { get; set; }
    public string[]? DatabaseMappings { get; set; }
    public DateTime ImportedAt { get; set; }
    public string? ImportedBy { get; set; }
    public string? ImportSource { get; set; }
    public int Version { get; set; };
    public bool IsExpired;;
    public bool IsValid { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/DnaEncodedKeyStrategy.cs
```csharp
public sealed class DnaEncodedKeyStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public string EncodeBinaryToDna(byte[] data);
    public byte[] DecodeDnaToBinary(string dnaSequence);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class DnaConfig
{
}
    public DnaSynthesisProvider SynthesisProvider { get; set; };
    public DnaSequencingProvider SequencingProvider { get; set; };
    public string SynthesisApiKey { get; set; };
    public string SequencingApiKey { get; set; };
    public int RedundantCopies { get; set; };
    public string IndexPrefix { get; set; };
}
```
```csharp
internal class DnaKeyEntry
{
}
    public string KeyId { get; set; };
    public string DnaSequence { get; set; };
    public byte[] ParityData { get; set; };
    public string SynthesisOrderId { get; set; };
    public string DnaSampleId { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public byte[]? DecodedKey { get; set; }
    public int SequenceLength { get; set; }
    public int RedundantCopies { get; set; }
}
```
```csharp
internal class DnaKeyEntrySerialized
{
}
    public string KeyId { get; set; };
    public string DnaSequence { get; set; };
    public string ParityData { get; set; };
    public string SynthesisOrderId { get; set; };
    public string DnaSampleId { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? DecodedKey { get; set; }
    public int SequenceLength { get; set; }
    public int RedundantCopies { get; set; }
}
```
```csharp
internal class DnaSynthesisOrder
{
}
    public string OrderId { get; set; };
    public string SampleId { get; set; };
    public string Status { get; set; };
    public DateTime? EstimatedDelivery { get; set; }
}
```
```csharp
internal class TwistSynthesisRequest
{
}
    [JsonPropertyName("name")]
public string Name { get; set; };
    [JsonPropertyName("sequence")]
public string Sequence { get; set; };
    [JsonPropertyName("scale")]
public string Scale { get; set; };
    [JsonPropertyName("purification")]
public string Purification { get; set; };
    [JsonPropertyName("quantity")]
public int Quantity { get; set; }
    [JsonPropertyName("modifications")]
public Dictionary<string, string> Modifications { get; set; };
}
```
```csharp
internal class TwistSynthesisResponse
{
}
    [JsonPropertyName("order_id")]
public string OrderId { get; set; };
    [JsonPropertyName("sample_ids")]
public string[]? SampleIds { get; set; }
    [JsonPropertyName("status")]
public string Status { get; set; };
    [JsonPropertyName("estimated_delivery")]
public DateTime? EstimatedDelivery { get; set; }
}
```
```csharp
internal class IdtSynthesisRequest
{
}
    [JsonPropertyName("name")]
public string Name { get; set; };
    [JsonPropertyName("sequence")]
public string Sequence { get; set; };
    [JsonPropertyName("scale")]
public string Scale { get; set; };
    [JsonPropertyName("purification")]
public string Purification { get; set; };
    [JsonPropertyName("quantity")]
public int Quantity { get; set; }
}
```
```csharp
internal class IdtSynthesisResponse
{
}
    [JsonPropertyName("order_number")]
public string OrderNumber { get; set; };
    [JsonPropertyName("oligo_id")]
public string? OligoId { get; set; }
    [JsonPropertyName("status")]
public string Status { get; set; };
    [JsonPropertyName("expected_ship_date")]
public DateTime? ExpectedShipDate { get; set; }
}
```
```csharp
internal class IlluminaSequencingRequest
{
}
    [JsonPropertyName("sample_id")]
public string SampleId { get; set; };
    [JsonPropertyName("read_length")]
public int ReadLength { get; set; }
    [JsonPropertyName("read_type")]
public string ReadType { get; set; };
    [JsonPropertyName("coverage")]
public int Coverage { get; set; }
    [JsonPropertyName("index_prefix")]
public string IndexPrefix { get; set; };
}
```
```csharp
internal class IlluminaSubmitResponse
{
}
    [JsonPropertyName("run_id")]
public string RunId { get; set; };
}
```
```csharp
internal class IlluminaStatusResponse
{
}
    [JsonPropertyName("status")]
public string Status { get; set; };
}
```
```csharp
internal class IlluminaResultResponse
{
}
    [JsonPropertyName("consensus_sequence")]
public string? ConsensusSequence { get; set; }
}
```
```csharp
internal class NanoporeSequencingRequest
{
}
    [JsonPropertyName("sample_id")]
public string SampleId { get; set; };
    [JsonPropertyName("flow_cell_type")]
public string FlowCellType { get; set; };
    [JsonPropertyName("kit")]
public string Kit { get; set; };
    [JsonPropertyName("run_duration")]
public double RunDuration { get; set; }
}
```
```csharp
internal class NanoporeSubmitResponse
{
}
    [JsonPropertyName("run_id")]
public string RunId { get; set; };
}
```
```csharp
internal class NanoporeStatusResponse
{
}
    [JsonPropertyName("state")]
public string State { get; set; };
}
```
```csharp
internal class NanoporeResultResponse
{
}
    [JsonPropertyName("sequence")]
public string? Sequence { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/VerifiableDelayStrategy.cs
```csharp
public sealed class VerifiableDelayStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<VdfCreationResult> CreateVdfProtectedKeyAsync(string keyId, byte[] keyData, TimeSpan delay, bool publishForOthers, ISecurityContext context);
    public async Task<VdfEvaluationResult> StartEvaluationAsync(string keyId, ISecurityContext context, IProgress<VdfProgress>? progress = null, CancellationToken cancellationToken = default);
    public async Task<VdfVerificationResult> VerifyVdfAsync(string keyId, byte[] outputY, byte[] proof, ISecurityContext context);
    public VdfProgress? GetEvaluationProgress(string keyId);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override void Dispose();
}
```
```csharp
public class VdfConfig
{
}
    public long SquaringsPerSecond { get; set; }
    public long DefaultDelaySeconds { get; set; };
    public int SecurityParameter { get; set; };
    public bool EnableProofCaching { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class VdfKeyData
{
}
    public string KeyId { get; set; };
    public byte[] Modulus { get; set; };
    public byte[] InputX { get; set; };
    public byte[] TotalSquarings { get; set; };
    public byte[] EncryptedKey { get; set; };
    public int KeySizeBytes { get; set; }
    public byte[]? OutputY { get; set; }
    public byte[]? Proof { get; set; }
    public bool IsEvaluated { get; set; }
    public bool IsVerified { get; set; }
    public long EstimatedDelaySeconds { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? EvaluatedAt { get; set; }
    public byte[]? DecryptedKey { get; set; }
}
```
```csharp
internal class VdfKeyDataSerialized
{
}
    public string? KeyId { get; set; }
    public byte[]? Modulus { get; set; }
    public byte[]? InputX { get; set; }
    public byte[]? TotalSquarings { get; set; }
    public byte[]? EncryptedKey { get; set; }
    public int KeySizeBytes { get; set; }
    public byte[]? OutputY { get; set; }
    public byte[]? Proof { get; set; }
    public bool IsEvaluated { get; set; }
    public bool IsVerified { get; set; }
    public long EstimatedDelaySeconds { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? EvaluatedAt { get; set; }
    public byte[]? DecryptedKey { get; set; }
}
```
```csharp
internal class VdfEvaluatorState
{
}
    public string KeyId { get; set; };
    public BigInteger Modulus { get; set; };
    public BigInteger Current { get; set; };
    public BigInteger TotalSquarings { get; set; };
    public BigInteger CompletedSquarings { get; set; };
    public BigInteger OutputY { get; set; };
    public BigInteger? Proof { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsComplete { get; set; }
    public bool IsCancelled { get; set; }
    public string? Error { get; set; }
}
```
```csharp
public class VdfCreationResult
{
}
    public bool Success { get; set; }
    public string? KeyId { get; set; }
    public byte[]? InputX { get; set; }
    public byte[]? Modulus { get; set; }
    public BigInteger TotalSquarings { get; set; };
    public TimeSpan EstimatedDelay { get; set; }
    public byte[]? Proof { get; set; }
    public string? Error { get; set; }
}
```
```csharp
public class VdfEvaluationResult
{
}
    public bool Success { get; set; }
    public string? KeyId { get; set; }
    public byte[]? DecryptedKey { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}
```
```csharp
public class VdfVerificationResult
{
}
    public bool IsValid { get; set; }
    public byte[]? DecryptedKey { get; set; }
    public TimeSpan VerificationTime { get; set; }
    public string? Error { get; set; }
}
```
```csharp
public class VdfProgress
{
}
    public string? KeyId { get; set; }
    public BigInteger CompletedSquarings { get; set; };
    public BigInteger TotalSquarings { get; set; };
    public int PercentComplete { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public TimeSpan EstimatedRemaining { get; set; }
    public bool IsComplete { get; set; }
    public bool IsCancelled { get; set; }
    public string? Error { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/BiometricDerivedKeyStrategy.cs
```csharp
public sealed class BiometricDerivedKeyStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> EnrollBiometricAsync(string keyId, byte[] biometricTemplate, ISecurityContext context);
    public async Task<byte[]> ReproduceKeyAsync(string keyId, byte[] biometricSample, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public async Task<byte[]> ReEnrollBiometricAsync(string keyId, byte[] oldBiometricSample, byte[] newBiometricTemplate, ISecurityContext context);
    public override void Dispose();
}
```
```csharp
public class BiometricConfig
{
}
    public BiometricType BiometricType { get; set; };
    public int ErrorTolerance { get; set; };
    public int MinEntropyBits { get; set; };
    public bool RequireMultiFactor { get; set; };
    public string? StoragePath { get; set; }
    public int KeyExpirationDays { get; set; };
}
```
```csharp
internal class BiometricKeyEntry
{
}
    public string KeyId { get; set; };
    public FuzzyHelperData HelperData { get; set; };
    public string TemplateHash { get; set; };
    public BiometricType BiometricType { get; set; }
    public int ErrorTolerance { get; set; }
    public byte[]? CachedKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public double EnrollmentEntropy { get; set; }
    public int AccessCount { get; set; }
    public DateTime? LastAccessedAt { get; set; }
}
```
```csharp
internal class BiometricKeyEntrySerialized
{
}
    public string KeyId { get; set; };
    public FuzzyHelperData HelperData { get; set; };
    public string TemplateHash { get; set; };
    public BiometricType BiometricType { get; set; }
    public int ErrorTolerance { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public double EnrollmentEntropy { get; set; }
    public int AccessCount { get; set; }
    public DateTime? LastAccessedAt { get; set; }
}
```
```csharp
public class FuzzyHelperData
{
}
    [JsonPropertyName("sketch")]
public string Sketch { get; set; };
    [JsonPropertyName("seed")]
public string Seed { get; set; };
    [JsonPropertyName("code_params")]
public BchCodeParams CodeParameters { get; set; };
}
```
```csharp
public class BchCodeParams
{
}
    [JsonPropertyName("n")]
public int N { get; set; }
    [JsonPropertyName("k")]
public int K { get; set; }
    [JsonPropertyName("t")]
public int T { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/GeoLockedKeyStrategy.cs
```csharp
public sealed class GeoLockedKeyStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyMaterial, ISecurityContext context);
    public static double CalculateHaversineDistance(double lat1, double lon1, double lat2, double lon2);
    public static byte[] CreateLocationAttestation(double latitude, double longitude, double accuracy, byte[] providerId, ECDsa signingKey);
    public async Task UpdateGeofenceAsync(string keyId, double latitude, double longitude, double radiusKm, ISecurityContext context);
    public async Task UpdateTimeWindowsAsync(string keyId, List<TimeWindow> timeWindows, ISecurityContext context);
    public async Task<IReadOnlyList<GeoAccessLogEntry>> GetAccessLogAsync(string keyId, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override void Dispose();
}
```
```csharp
public class GeoLockConfig
{
}
    public double CenterLatitude { get; set; }
    public double CenterLongitude { get; set; }
    public double RadiusKm { get; set; };
    public bool AllowIpGeolocation { get; set; };
    public bool RequireLocationAttestation { get; set; }
    public byte[]? AttestationPublicKey { get; set; }
    public string? IpGeolocationApiKey { get; set; }
    public List<TimeWindow> AllowedTimeWindows { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
public class TimeWindow
{
}
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public TimeZoneInfo? TimeZone { get; set; }
    public DayOfWeek[] AllowedDays { get; set; };
}
```
```csharp
internal class TimeWindowSerialized
{
}
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public string? TimeZoneId { get; set; }
    public int[]? AllowedDays { get; set; }
}
```
```csharp
public struct GeoLocation
{
}
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public LocationSource Source { get; set; }
    public DateTime Timestamp { get; set; }
}
```
```csharp
public interface IGeoSecurityContext : ISecurityContext
{
}
    double Latitude { get; }
    double Longitude { get; }
    double? Accuracy { get; }
}
```
```csharp
public interface INetworkSecurityContext : ISecurityContext
{
}
    string? ClientIpAddress { get; }
}
```
```csharp
public interface IAttestationSecurityContext : ISecurityContext
{
}
    byte[]? LocationAttestation { get; }
}
```
```csharp
public class GeoAccessLogEntry
{
}
    public DateTime Timestamp { get; set; }
    public string? UserId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double DistanceFromCenter { get; set; }
    public bool AccessGranted { get; set; }
    public string? DenialReason { get; set; }
}
```
```csharp
internal class GeoLockedKeyData
{
}
    public string KeyId { get; set; };
    public byte[] EncryptedKeyMaterial { get; set; };
    public double CenterLatitude { get; set; }
    public double CenterLongitude { get; set; }
    public double RadiusKm { get; set; }
    public List<TimeWindow> AllowedTimeWindows { get; set; };
    public bool RequireAttestation { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
    public List<GeoAccessLogEntry> AccessLog { get; set; };
}
```
```csharp
internal class GeoLockedKeyDataSerialized
{
}
    public string KeyId { get; set; };
    public byte[]? EncryptedKeyMaterial { get; set; }
    public double CenterLatitude { get; set; }
    public double CenterLongitude { get; set; }
    public double RadiusKm { get; set; }
    public List<TimeWindowSerialized>? AllowedTimeWindows { get; set; }
    public bool RequireAttestation { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/QuantumKeyDistributionStrategy.cs
```csharp
public sealed class QuantumKeyDistributionStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<QkdChannelStatus> GetChannelStatusAsync(CancellationToken cancellationToken = default);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class QkdConfig
{
}
    public QkdSystemType QkdSystem { get; set; };
    public string ApiEndpoint { get; set; };
    public string ApiKey { get; set; };
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    public double QberThreshold { get; set; };
    public double MinKeyRateBps { get; set; };
    public string QuantumChannelId { get; set; };
    public string SaeId { get; set; };
    public int KeyExpirationHours { get; set; };
    public int ChannelMonitorIntervalSeconds { get; set; };
    public int QberSampleSize { get; set; };
}
```
```csharp
public class QkdChannelStatus
{
}
    public bool IsOperational { get; set; }
    public double QuantumBitErrorRate { get; set; }
    public double CurrentKeyRateBps { get; set; }
    public int AvailableKeyCount { get; set; }
    public DateTime LastUpdated { get; set; }
    public string? ChannelId { get; set; }
}
```
```csharp
internal class QkdKeyEntry
{
}
    public string KeyId { get; set; };
    public byte[] KeyMaterial { get; set; };
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? CreatedBy { get; set; }
    public string Source { get; set; };
    public double Qber { get; set; }
}
```
```csharp
internal class QkdKeyEntrySerialized
{
}
    public string KeyId { get; set; };
    public string KeyMaterial { get; set; };
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? CreatedBy { get; set; }
    public string Source { get; set; };
    public double Qber { get; set; }
}
```
```csharp
internal class IdqKeyRequest
{
}
    [JsonPropertyName("number")]
public int Number { get; set; }
    [JsonPropertyName("size")]
public int Size { get; set; }
    [JsonPropertyName("extension_mandatory")]
public Dictionary<string, object> ExtensionMandatory { get; set; };
    [JsonPropertyName("extension_optional")]
public Dictionary<string, object> ExtensionOptional { get; set; };
}
```
```csharp
internal class IdqKeyContainer
{
}
    [JsonPropertyName("keys")]
public IdqKeyResponse[] Keys { get; set; };
}
```
```csharp
internal class IdqKeyResponse
{
}
    [JsonPropertyName("key_ID")]
public string? KeyId { get; set; }
    [JsonPropertyName("key")]
public string Key { get; set; };
}
```
```csharp
internal class IdqStatusResponse
{
}
    [JsonPropertyName("source_KME_ID")]
public string? SourceKmeId { get; set; }
    [JsonPropertyName("target_KME_ID")]
public string? TargetKmeId { get; set; }
    [JsonPropertyName("master_SAE_ID")]
public string? MasterSaeId { get; set; }
    [JsonPropertyName("slave_SAE_ID")]
public string? SlaveSaeId { get; set; }
    [JsonPropertyName("key_size")]
public int KeySize { get; set; }
    [JsonPropertyName("stored_key_count")]
public int StoredKeyCount { get; set; }
    [JsonPropertyName("max_key_count")]
public int MaxKeyCount { get; set; }
    [JsonPropertyName("key_rate")]
public double KeyRate { get; set; }
    [JsonPropertyName("qber")]
public double Qber { get; set; }
}
```
```csharp
internal class ToshibaKeyRequest
{
}
    [JsonPropertyName("key_id")]
public string KeyId { get; set; };
    [JsonPropertyName("key_length_bits")]
public int KeyLengthBits { get; set; }
    [JsonPropertyName("channel_id")]
public string ChannelId { get; set; };
    [JsonPropertyName("requester_id")]
public string RequesterId { get; set; };
    [JsonPropertyName("timestamp")]
public long Timestamp { get; set; }
}
```
```csharp
internal class ToshibaKeyResponse
{
}
    [JsonPropertyName("assigned_key_id")]
public string? AssignedKeyId { get; set; }
    [JsonPropertyName("key_material")]
public string KeyMaterial { get; set; };
    [JsonPropertyName("key_hash")]
public string KeyHash { get; set; };
    [JsonPropertyName("measured_qber")]
public double MeasuredQber { get; set; }
}
```
```csharp
internal class ToshibaStatusResponse
{
}
    [JsonPropertyName("state")]
public string State { get; set; };
    [JsonPropertyName("current_qber")]
public double CurrentQber { get; set; }
    [JsonPropertyName("key_generation_rate")]
public double KeyGenerationRate { get; set; }
    [JsonPropertyName("key_pool_size")]
public int KeyPoolSize { get; set; }
}
```
```csharp
internal class Bb84RawKeyResponse
{
}
    public byte[] RawBits { get; set; };
    public byte[] AliceBases { get; set; };
    public byte[] BobBases { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/SocialRecoveryStrategy.cs
```csharp
public sealed class SocialRecoveryStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<GuardianRegistrationResult> RegisterGuardianAsync(string keyId, string guardianId, GuardianIdentity identity, ISecurityContext context);
    public async Task<RecoverySessionResult> InitiateRecoveryAsync(string keyId, string requestorId, string reason, ISecurityContext context);
    public async Task<ShareSubmissionResult> SubmitGuardianShareAsync(string sessionId, string guardianId, byte[] share, GuardianIdentity identity, ISecurityContext context);
    public async Task<RecoveryCompletionResult> CompleteRecoveryAsync(string sessionId, ISecurityContext context);
    public async Task<GuardianRotationResult> RotateGuardianAsync(string keyId, string oldGuardianId, GuardianIdentity newIdentity, byte[] oldShare, // Required to prove knowledge of the share
 ISecurityContext context);
    public async Task<IReadOnlyList<RecoveryAuditEntry>> GetAuditLogAsync(string keyId, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override void Dispose();
}
```
```csharp
public class SocialRecoveryConfig
{
}
    public int Threshold { get; set; };
    public int TotalGuardians { get; set; };
    public int RecoveryCooldownHours { get; set; };
    public bool RequireMultiFactorVerification { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
public class Guardian
{
}
    public string GuardianId { get; set; };
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public byte[]? PublicKey { get; set; }
    public VerificationType VerificationType { get; set; }
    public int ShareIndex { get; set; }
    public byte[] EncryptedShare { get; set; };
    public GuardianStatus Status { get; set; }
    public string? IdentityHash { get; set; }
    public int VerificationAttempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
```
```csharp
public class GuardianIdentity
{
}
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public byte[]? PublicKey { get; set; }
    public byte[]? BiometricHash { get; set; }
    public VerificationType VerificationType { get; set; }
}
```
```csharp
public class GuardianInfo
{
}
    public string? GuardianId { get; set; }
    public string? Name { get; set; }
}
```
```csharp
public class RecoveryAuditEntry
{
}
    public DateTime Timestamp { get; set; }
    public AuditAction Action { get; set; }
    public string? SessionId { get; set; }
    public string? GuardianId { get; set; }
    public string? Details { get; set; }
}
```
```csharp
internal class ShamirShare
{
}
    public int Index { get; set; }
    public BigInteger Value { get; set; };
}
```
```csharp
internal class ShamirShareSerialized
{
}
    public int Index { get; set; }
    public byte[]? Value { get; set; }
}
```
```csharp
internal class RecoverySession
{
}
    public string SessionId { get; set; };
    public string KeyId { get; set; };
    public string? RequestorId { get; set; }
    public string? Reason { get; set; }
    public RecoveryStatus Status { get; set; }
    public int RequiredShares { get; set; }
    public Dictionary<string, ShamirShare> CollectedShares { get; set; };
    public DateTime CooldownEndsAt { get; set; }
    public DateTime InitiatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public byte[]? RecoveredKey { get; set; }
}
```
```csharp
internal class RecoverySessionSerialized
{
}
    public string? SessionId { get; set; }
    public string? KeyId { get; set; }
    public string? RequestorId { get; set; }
    public string? Reason { get; set; }
    public RecoveryStatus Status { get; set; }
    public int RequiredShares { get; set; }
    public Dictionary<string, ShamirShareSerialized>? CollectedShares { get; set; }
    public DateTime CooldownEndsAt { get; set; }
    public DateTime InitiatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
```
```csharp
internal class SocialRecoveryKeyData
{
}
    public string KeyId { get; set; };
    public int Threshold { get; set; }
    public int KeySizeBytes { get; set; }
    public int CooldownHours { get; set; }
    public bool RequireMultiFactor { get; set; }
    public List<Guardian> Guardians { get; set; };
    public List<RecoveryAuditEntry> AuditLog { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
internal class SocialRecoveryStorageData
{
}
    public Dictionary<string, SocialRecoveryKeyData> Keys { get; set; };
    public Dictionary<string, RecoverySessionSerialized> Sessions { get; set; };
}
```
```csharp
public class VerificationResult
{
}
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
}
```
```csharp
public class GuardianRegistrationResult
{
}
    public bool Success { get; set; }
    public string? GuardianId { get; set; }
    public byte[]? ShareData { get; set; }
    public string? FailureReason { get; set; }
}
```
```csharp
public class RecoverySessionResult
{
}
    public string? SessionId { get; set; }
    public RecoveryStatus Status { get; set; }
    public int RequiredShares { get; set; }
    public int CollectedShares { get; set; }
    public DateTime CooldownEndsAt { get; set; }
    public List<GuardianInfo>? ActiveGuardians { get; set; }
}
```
```csharp
public class ShareSubmissionResult
{
}
    public bool Success { get; set; }
    public int SharesCollected { get; set; }
    public int SharesRequired { get; set; }
    public RecoveryStatus Status { get; set; }
    public DateTime CooldownEndsAt { get; set; }
    public string? FailureReason { get; set; }
}
```
```csharp
public class RecoveryCompletionResult
{
}
    public bool Success { get; set; }
    public byte[]? RecoveredKey { get; set; }
    public string? SessionId { get; set; }
    public string? FailureReason { get; set; }
}
```
```csharp
public class GuardianRotationResult
{
}
    public bool Success { get; set; }
    public string? OldGuardianId { get; set; }
    public string? NewGuardianId { get; set; }
    public string? FailureReason { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/SmartContractKeyStrategy.cs
```csharp
public sealed class SmartContractKeyStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task RequestKeyAccessAsync(string keyId, ISecurityContext context);
    public async Task ApproveKeyAccessAsync(string keyId, string requesterAddress, ISecurityContext context);
    public async Task<SmartContractKeyInfo> GetKeyInfoFromContract(string keyId);
    public async Task<(bool approved, int count)> GetApprovalStatusAsync(string keyId, string requesterAddress);
    public async Task RevokeKeyAsync(string keyId, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class SmartContractConfig
{
}
    public string RpcUrl { get; set; };
    public int ChainId { get; set; };
    public string PrivateKey { get; set; };
    public string? ContractAddress { get; set; }
    public long GasPriceGwei { get; set; };
    public int DefaultTimeLockHours { get; set; };
    public int RequiredApprovals { get; set; };
    public string[] Approvers { get; set; };
}
```
```csharp
internal class SmartContractKeyEntry
{
}
    public string KeyId { get; set; };
    public byte[] EncryptedKey { get; set; };
    public byte[]? DecryptedKey { get; set; }
    public string? TransactionHash { get; set; }
    public string ContractAddress { get; set; };
    public string Owner { get; set; };
    public DateTime TimeLockUntil { get; set; }
    public int RequiredApprovals { get; set; }
    public string[] Approvers { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
internal class SmartContractKeyEntrySerialized
{
}
    public string KeyId { get; set; };
    public string EncryptedKey { get; set; };
    public string? DecryptedKey { get; set; }
    public string? TransactionHash { get; set; }
    public string ContractAddress { get; set; };
    public string Owner { get; set; };
    public DateTime TimeLockUntil { get; set; }
    public int RequiredApprovals { get; set; }
    public string[] Approvers { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
public class SmartContractKeyInfo
{
}
    public string KeyId { get; set; };
    public string Owner { get; set; };
    public int RequiredApprovals { get; set; }
    public int CurrentApprovals { get; set; }
    public DateTime TimeLockUntil { get; set; }
    public bool IsActive { get; set; }
    public DateTime DepositTime { get; set; }
}
```
```csharp
public class KeyEscrowDeployment : ContractDeploymentMessage
{
}
    public KeyEscrowDeployment() : base(ContractBytecode);
    public static string ContractBytecode;;
}
```
```csharp
[FunctionOutput]
public class KeyInfoOutputDTO : IFunctionOutputDTO
{
}
    [Parameter("address", "owner", 1)]
public string Owner { get; set; };
    [Parameter("uint8", "requiredApprovals", 2)]
public byte RequiredApprovals { get; set; }
    [Parameter("uint8", "currentApprovals", 3)]
public byte CurrentApprovals { get; set; }
    [Parameter("uint256", "timeLockUntil", 4)]
public BigInteger TimeLockUntil { get; set; }
    [Parameter("bool", "isActive", 5)]
public bool IsActive { get; set; }
    [Parameter("uint256", "depositTime", 6)]
public BigInteger DepositTime { get; set; }
}
```
```csharp
[FunctionOutput]
public class ApprovalStatusOutputDTO : IFunctionOutputDTO
{
}
    [Parameter("bool", "approved", 1)]
public bool Approved { get; set; }
    [Parameter("uint8", "count", 2)]
public byte Count { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/AiCustodianStrategy.cs
```csharp
public sealed class AiCustodianStrategy : KeyStoreStrategyBase
{
}
    public AiCustodianStrategy();
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task ConfigurePoliciesAsync(string keyId, List<string> policies, ISecurityContext context);
    public async Task<bool> ResolveEscalationAsync(string escalationId, bool approved, string resolverNotes, ISecurityContext context);
    public async Task<IReadOnlyList<AiAccessAuditEntry>> GetAuditLogAsync(string keyId, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override void Dispose();
}
```
```csharp
public class AiCustodianConfig
{
}
    public LlmProvider Provider { get; set; };
    public string ApiKey { get; set; };
    public string? ApiEndpoint { get; set; }
    public string? Model { get; set; }
    public double ConfidenceThreshold { get; set; };
    public string? EscalationEmail { get; set; }
    public int MaxAccessHistorySize { get; set; };
    public string? StoragePath { get; set; }
    public string? SystemPrompt { get; set; }
    public List<string> DefaultPolicies { get; set; };
}
```
```csharp
internal class AiProtectedKeyData
{
}
    public string KeyId { get; set; };
    public byte[] EncryptedKeyMaterial { get; set; };
    public List<string> Policies { get; set; };
    public List<AiAccessAuditEntry> AuditLog { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
public class AiAccessAuditEntry
{
}
    public DateTime Timestamp { get; set; }
    public string? UserId { get; set; }
    public string? TenantId { get; set; }
    public AccessDecision Decision { get; set; }
    public double Confidence { get; set; }
    public string? Reasoning { get; set; }
    public string[]? Factors { get; set; }
}
```
```csharp
internal class AccessHistory
{
}
    public string KeyId { get; set; };
    public List<AccessHistoryEntry> Entries { get; set; };
}
```
```csharp
internal class AccessHistoryEntry
{
}
    public DateTime Timestamp { get; set; }
    public string? UserId { get; set; }
    public bool WasApproved { get; set; }
}
```
```csharp
internal class PendingEscalation
{
}
    public string EscalationId { get; set; };
    public string KeyId { get; set; };
    public string? RequesterId { get; set; }
    public DateTime RequestTime { get; set; }
    public string? AiReasoning { get; set; }
    public string[]? Factors { get; set; }
    public EscalationStatus Status { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolverId { get; set; }
    public string? ResolverNotes { get; set; }
}
```
```csharp
internal class AccessRequestContext
{
}
    public string KeyId { get; set; };
    public string UserId { get; set; };
    public string? TenantId { get; set; }
    public string[] Roles { get; set; };
    public bool IsAdmin { get; set; }
    public DateTime RequestTime { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public List<AccessHistoryEntry> RecentAccesses { get; set; };
    public int RecentAccessCount { get; set; }
    public double AnomalyScore { get; set; }
    public bool IsUnusual { get; set; }
    public string? TypicalAccessTime { get; set; }
    public string? TypicalFrequency { get; set; }
}
```
```csharp
internal class AiAccessDecision
{
}
    public AccessDecision Decision { get; set; }
    public double Confidence { get; set; }
    public string Reasoning { get; set; };
    public string[] Factors { get; set; };
    public string? RequiredVerification { get; set; }
}
```
```csharp
internal class AiDecisionJson
{
}
    [JsonPropertyName("decision")]
public string? Decision { get; set; }
    [JsonPropertyName("confidence")]
public double Confidence { get; set; }
    [JsonPropertyName("reasoning")]
public string? Reasoning { get; set; }
    [JsonPropertyName("factors")]
public string[]? Factors { get; set; }
    [JsonPropertyName("required_verification")]
public string? RequiredVerification { get; set; }
}
```
```csharp
internal class AiCustodianStorageData
{
}
    public Dictionary<string, AiProtectedKeyData> Keys { get; set; };
    public Dictionary<string, AccessHistory> Histories { get; set; };
    public List<PendingEscalation> Escalations { get; set; };
}
```
```csharp
internal class OpenAiResponse
{
}
    [JsonPropertyName("choices")]
public List<OpenAiChoice>? Choices { get; set; }
}
```
```csharp
internal class OpenAiChoice
{
}
    [JsonPropertyName("message")]
public OpenAiMessage? Message { get; set; }
}
```
```csharp
internal class OpenAiMessage
{
}
    [JsonPropertyName("content")]
public string? Content { get; set; }
}
```
```csharp
internal class AnthropicResponse
{
}
    [JsonPropertyName("content")]
public List<AnthropicContent>? Content { get; set; }
}
```
```csharp
internal class AnthropicContent
{
}
    [JsonPropertyName("text")]
public string? Text { get; set; }
}
```
```csharp
internal class OllamaResponse
{
}
    [JsonPropertyName("response")]
public string? Response { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/StellarAnchorsStrategy.cs
```csharp
public sealed class StellarAnchorsStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> RotateKeyAsync(string keyId, ISecurityContext context);
    public async Task RevokeKeyAsync(string keyId, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class StellarConfig
{
}
    public string NetworkPassphrase { get; set; };
    public string HorizonUrl { get; set; };
    public string AccountSecret { get; set; };
    public bool UseTestnet { get; set; };
    public int BaseFee { get; set; };
    public int TransactionTimeoutSeconds { get; set; };
    public int KeyExpirationHours { get; set; };
}
```
```csharp
internal class StellarKeyEntry
{
}
    public string KeyId { get; set; };
    public byte[] DerivedKey { get; set; };
    public string TransactionHash { get; set; };
    public long SequenceNumber { get; set; }
    public string AccountId { get; set; };
    public string KeyHash { get; set; };
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? Operation { get; set; }
    public KeyMetadataPayload? Metadata { get; set; }
}
```
```csharp
internal class StellarKeyEntrySerialized
{
}
    public string KeyId { get; set; };
    public string DerivedKey { get; set; };
    public string TransactionHash { get; set; };
    public long SequenceNumber { get; set; }
    public string AccountId { get; set; };
    public string KeyHash { get; set; };
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? Operation { get; set; }
}
```
```csharp
public class KeyMetadataPayload
{
}
    [JsonPropertyName("key_id")]
public string KeyId { get; set; };
    [JsonPropertyName("operation")]
public string Operation { get; set; };
    [JsonPropertyName("key_hash")]
public string KeyHash { get; set; };
    [JsonPropertyName("created_by")]
public string CreatedBy { get; set; };
    [JsonPropertyName("timestamp")]
public long Timestamp { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/IndustryFirst/TimeLockPuzzleStrategy.cs
```csharp
public sealed class TimeLockPuzzleStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<TimeLockPuzzleResult> CreatePuzzleAsync(string keyId, byte[] keyData, TimeSpan unlockTime, ISecurityContext context);
    public async Task<ParallelPuzzleChainResult> CreateParallelPuzzleChainsAsync(string baseKeyId, byte[] keyData, TimeSpan[] unlockTimes, ISecurityContext context);
    public async Task<PuzzleSolveResult> StartSolvingAsync(string keyId, ISecurityContext context, IProgress<PuzzleSolveProgress>? progress = null, CancellationToken cancellationToken = default);
    public PuzzleSolveProgress? GetSolverProgress(string keyId);
    public async Task<bool> VerifyPuzzleSolutionAsync(string keyId, BigInteger solution);
    public async Task<PuzzleSolveResult> ResumeSolvingAsync(string keyId, ISecurityContext context, IProgress<PuzzleSolveProgress>? progress = null, CancellationToken cancellationToken = default);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override void Dispose();
}
```
```csharp
private class ShamirShare
{
}
    public int Index { get; set; }
    public BigInteger Value { get; set; };
}
```
```csharp
public class TimeLockConfig
{
}
    public long SquaringsPerSecond { get; set; }
    public long DefaultUnlockSeconds { get; set; };
    public bool EnableCheckpointing { get; set; };
    public int CheckpointIntervalSeconds { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class TimeLockKeyData
{
}
    public string KeyId { get; set; };
    public byte[] Modulus { get; set; };
    public byte[] Base { get; set; };
    public byte[] TotalSquarings { get; set; };
    public byte[] EncryptedKey { get; set; };
    public byte[] ExpectedResultHash { get; set; };
    public int KeySizeBytes { get; set; }
    public long EstimatedUnlockSeconds { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? SolvedAt { get; set; }
    public byte[]? DecryptedKey { get; set; }
}
```
```csharp
internal class TimeLockKeyDataSerialized
{
}
    public string? KeyId { get; set; }
    public byte[]? Modulus { get; set; }
    public byte[]? Base { get; set; }
    public byte[]? TotalSquarings { get; set; }
    public byte[]? EncryptedKey { get; set; }
    public byte[]? ExpectedResultHash { get; set; }
    public int KeySizeBytes { get; set; }
    public long EstimatedUnlockSeconds { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? SolvedAt { get; set; }
    public byte[]? DecryptedKey { get; set; }
}
```
```csharp
internal class PuzzleSolverState
{
}
    public string KeyId { get; set; };
    public BigInteger Modulus { get; set; };
    public BigInteger Current { get; set; };
    public BigInteger TotalSquarings { get; set; };
    public BigInteger CompletedSquarings { get; set; };
    public byte[] EncryptedKey { get; set; };
    public byte[] ExpectedResultHash { get; set; };
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsComplete { get; set; }
    public bool IsCancelled { get; set; }
    public string? Error { get; set; }
    public byte[]? DecryptedKey { get; set; }
}
```
```csharp
internal class PuzzleCheckpoint
{
}
    public string? KeyId { get; set; }
    public byte[]? CurrentValue { get; set; }
    public byte[]? CompletedSquarings { get; set; }
    public int PercentComplete { get; set; }
    public DateTime CheckpointedAt { get; set; }
}
```
```csharp
public class TimeLockPuzzleResult
{
}
    public bool Success { get; set; }
    public string? KeyId { get; set; }
    public BigInteger TotalSquarings { get; set; };
    public TimeSpan EstimatedUnlockTime { get; set; }
    public byte[]? Modulus { get; set; }
    public string? Error { get; set; }
}
```
```csharp
public class PuzzleSolveResult
{
}
    public bool Success { get; set; }
    public string? KeyId { get; set; }
    public byte[]? DecryptedKey { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}
```
```csharp
public class PuzzleSolveProgress
{
}
    public string? KeyId { get; set; }
    public BigInteger CompletedSquarings { get; set; };
    public BigInteger TotalSquarings { get; set; };
    public int PercentComplete { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public TimeSpan EstimatedRemaining { get; set; }
    public bool IsComplete { get; set; }
    public bool IsCancelled { get; set; }
    public string? Error { get; set; }
}
```
```csharp
public class ParallelPuzzleChainResult
{
}
    public bool Success { get; set; }
    public string? BaseKeyId { get; set; }
    public List<PuzzleChainInfo> Chains { get; set; };
    public int RequiredChains { get; set; }
}
```
```csharp
public class PuzzleChainInfo
{
}
    public string? ChainId { get; set; }
    public TimeSpan UnlockTime { get; set; }
    public int ShareIndex { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Platform/WindowsCredManagerStrategy.cs
```csharp
[SupportedOSPlatform("windows")]
public sealed class WindowsCredManagerStrategy : KeyStoreStrategyBase
{
#endregion
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public WindowsCredManagerStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
}
```
```csharp
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
private struct CREDENTIAL
{
}
    public uint Flags;
    public uint Type;
    [MarshalAs(UnmanagedType.LPWStr)]
public string? TargetName;
    [MarshalAs(UnmanagedType.LPWStr)]
public string? Comment;
    public long LastWritten;
    public uint CredentialBlobSize;
    public IntPtr CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public IntPtr Attributes;
    [MarshalAs(UnmanagedType.LPWStr)]
public string? TargetAlias;
    [MarshalAs(UnmanagedType.LPWStr)]
public string? UserName;
}
```
```csharp
public class WindowsCredManagerConfig
{
}
    public string CredentialPrefix { get; set; };
    public CredentialPersistence PersistenceLevel { get; set; };
    public bool UseAdditionalEntropy { get; set; };
    public string? EntropyKey { get; set; }
}
```
```csharp
internal class CredManagerMetadata
{
}
    public string CurrentKeyId { get; set; };
    public DateTime LastUpdated { get; set; }
    public int Version { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Platform/SshAgentStrategy.cs
```csharp
public sealed class SshAgentStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class SshAgentConfig
{
}
    public string? AgentSocketPath { get; set; }
    public string? KeyFingerprint { get; set; }
    public string StoragePath { get; set; };
    public bool FallbackToFileEncryption { get; set; };
}
```
```csharp
internal class SshAgentMetadata
{
}
    public string CurrentKeyId { get; set; };
    public DateTime LastUpdated { get; set; }
    public int Version { get; set; }
    public string AgentKeyFingerprint { get; set; };
}
```
```csharp
internal class AgentIdentity
{
}
    public string KeyType { get; set; };
    public string PublicKeyBase64 { get; set; };
    public string Comment { get; set; };
    public string Fingerprint { get; set; };
}
```
```csharp
internal class ProcessBasedAgent : IAgentProtocol
{
}
    public List<AgentIdentity> Identities { get; }
    public ProcessBasedAgent(List<AgentIdentity> identities);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Platform/LinuxSecretServiceStrategy.cs
```csharp
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretServiceStrategy : KeyStoreStrategyBase
{
#endregion
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public LinuxSecretServiceStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
}
```
```csharp
public class LinuxSecretServiceConfig
{
}
    public string ApplicationName { get; set; };
    public string CollectionName { get; set; };
    public bool UseAdditionalEncryption { get; set; };
}
```
```csharp
internal class SecretServiceMetadata
{
}
    public string CurrentKeyId { get; set; };
    public DateTime LastUpdated { get; set; }
    public int Version { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Platform/MacOsKeychainStrategy.cs
```csharp
[SupportedOSPlatform("macos")]
public sealed class MacOsKeychainStrategy : KeyStoreStrategyBase
{
#endregion
#region P/Invoke declarations (for future native implementation)
// These are provided for reference if a pure P/Invoke implementation is needed
// Currently using the 'security' command-line tool for better compatibility
/*
        private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            string serviceName,
            uint accountNameLength,
            string accountName,
            uint passwordLength,
            byte[] passwordData,
            out IntPtr itemRef);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainFindGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            string serviceName,
            uint accountNameLength,
            string accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        private const int errSecSuccess = 0;
        private const int errSecItemNotFound = -25300;
        private const int errSecDuplicateItem = -25299;
        */
#endregion
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public MacOsKeychainStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
}
```
```csharp
public class MacOsKeychainConfig
{
}
    public string ServiceName { get; set; };
    public string? KeychainPath { get; set; }
    public string? AccessGroup { get; set; }
}
```
```csharp
internal class KeychainMetadata
{
}
    public string CurrentKeyId { get; set; };
    public DateTime LastUpdated { get; set; }
    public int Version { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Platform/PgpKeyringStrategy.cs
```csharp
public sealed class PgpKeyringStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class PgpKeyringConfig
{
}
    public string KeyringPath { get; set; };
    public string? MasterKeyId { get; set; }
    public string? Passphrase { get; set; }
    public string? PassphraseEnvVar { get; set; };
    public PgpKeyAlgorithm KeyAlgorithm { get; set; };
    public int KeySizeBits { get; set; };
}
```
```csharp
internal class PgpKeyringMetadata
{
}
    public string CurrentKeyId { get; set; };
    public DateTime LastUpdated { get; set; }
    public int Version { get; set; }
    public string MasterKeyId { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hardware/QkdStrategy.cs
```csharp
public sealed class QkdStrategy : KeyStoreStrategyBase
{
}
    public override string StrategyId;;
    public override string Name;;
    public override KeyStoreCapabilities Capabilities;;
    protected override Task InitializeStorage(CancellationToken cancellationToken);
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> GenerateQkdKeyAsync(string keyId);
    public QkdChannelStats GetChannelStats();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public sealed class QkdSessionInfo
{
}
    public required string KeyId { get; init; }
    public required QkdProtocol Protocol { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required int KeyLengthBits { get; init; }
    public required double Qber { get; init; }
    public required bool IsSimulated { get; init; }
    public required double KeyRateBitsPerSecond { get; init; }
}
```
```csharp
public sealed class QkdChannelStats
{
}
    public required QkdProtocol Protocol { get; init; }
    public required bool IsSimulated { get; init; }
    public required long TotalBitsGenerated { get; init; }
    public required long TotalErrors { get; init; }
    public required double CurrentQber { get; init; }
    public required double KeyRateBitsPerSecond { get; init; }
    public required int SessionCount { get; init; }
    public required double UptimeSeconds { get; init; }
    public required double QberThreshold { get; init; }
    public required bool IsChannelSecure { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hardware/NitrokeyStrategy.cs
```csharp
public sealed class NitrokeyStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public async Task<(IObjectHandle PublicKey, IObjectHandle PrivateKey)> GenerateRsaKeyPairAsync(string keyLabel, int keySizeBits = 2048);
    public async Task<IObjectHandle> GenerateAesKeyAsync(string keyLabel, int keySizeBits = 256);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class NitrokeyConfig
{
}
    public string? LibraryPath { get; set; }
    public ulong? SlotId { get; set; }
    public string? UserPin { get; set; }
    public string? SoPin { get; set; }
    public string DefaultKeyLabel { get; set; };
    public NitrokeyModel Model { get; set; };
    public bool AllowExtractableKeys { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hardware/TpmStrategy.cs
```csharp
public sealed class TpmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class TpmConfig
{
}
    public string? DevicePath { get; set; }
    public int[] PcrSelection { get; set; };
    public string? AuthValue { get; set; }
    public string? KeyStoragePath { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hardware/TrezorStrategy.cs
```csharp
public sealed class TrezorStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> GetPublicKeyAsync(string derivationPath);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class TrezorConfig
{
}
    public string DerivationPath { get; set; };
    public string KeyValueKey { get; set; };
    public bool RequireConfirmation { get; set; };
    public string? KeyStoragePath { get; set; }
}
```
```csharp
public class TrezorFeatures
{
}
    public string? Model { get; set; }
    public string? Label { get; set; }
    public bool Initialized { get; set; }
    public bool Unlocked { get; set; }
    public bool PassphraseProtection { get; set; }
}
```
```csharp
public class TrezorDerivedKey
{
}
    public string KeyId { get; set; };
    public string DerivationPath { get; set; };
    public string KeyName { get; set; };
    public byte[] DerivedKey { get; set; };
    public DateTime CreatedAt { get; set; }
}
```
```csharp
internal class TrezorDerivedKeyDto
{
}
    public string KeyId { get; set; };
    public string DerivationPath { get; set; };
    public string KeyName { get; set; };
    public string DerivedKey { get; set; };
    public DateTime CreatedAt { get; set; }
    public static TrezorDerivedKeyDto FromKey(TrezorDerivedKey k);;
    public TrezorDerivedKey ToKey();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hardware/YubikeyStrategy.cs
```csharp
public sealed class YubikeyStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public async Task<byte[]> GeneratePivKeyAsync(string keyId, PivAlgorithm algorithm = PivAlgorithm.Rsa2048);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class YubikeyConfig
{
}
    public int? SerialNumber { get; set; }
    public string? Pin { get; set; }
    public byte[]? ManagementKey { get; set; }
    public byte PreferredSlot { get; set; };
    public int HmacSlot { get; set; };
    public bool RequireTouch { get; set; };
    public string? KeyStoragePath { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hardware/OnlyKeyStrategy.cs
```csharp
public sealed class OnlyKeyStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public async Task<byte[]> SignDataAsync(int slot, byte[] data);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class OnlyKeyConfig
{
}
    public string? SerialNumber { get; set; }
    public string? Pin { get; set; }
    public OnlyKeyProfile Profile { get; set; };
    public int DefaultSlot { get; set; };
    public string? MappingStoragePath { get; set; }
}
```
```csharp
public class OnlyKeySlotMapping
{
}
    public string KeyId { get; set; };
    public int Slot { get; set; }
    public OnlyKeyProfile Profile { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hardware/SoloKeyStrategy.cs
```csharp
public sealed class SoloKeyStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public async Task<Fido2Credential> RegisterCredentialAsync(string keyId, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class SoloKeyConfig
{
}
    public string Origin { get; set; };
    public string RpId { get; set; };
    public string RpName { get; set; };
    public bool RequireUserVerification { get; set; };
    public string? CredentialStoragePath { get; set; }
}
```
```csharp
public class Fido2Credential
{
}
    public byte[] CredentialId { get; set; };
    public byte[] PublicKey { get; set; };
    public byte[] UserId { get; set; };
    public string KeyId { get; set; };
    public DateTime CreatedAt { get; set; }
    public uint SignCount { get; set; }
}
```
```csharp
internal class Fido2CredentialDto
{
}
    public string CredentialId { get; set; };
    public string PublicKey { get; set; };
    public string UserId { get; set; };
    public string KeyId { get; set; };
    public DateTime CreatedAt { get; set; }
    public uint SignCount { get; set; }
    public static Fido2CredentialDto FromCredential(Fido2Credential cred);;
    public Fido2Credential ToCredential();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hardware/LedgerStrategy.cs
```csharp
public sealed class LedgerStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> GetPublicKeyAsync(string derivationPath, bool requireConfirmation = false);
    public async Task<byte[]> SignHashAsync(string derivationPath, byte[] hash);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class LedgerConfig
{
}
    public string DerivationPath { get; set; };
    public string AppName { get; set; };
    public bool RequireConfirmation { get; set; };
    public string? KeyStoragePath { get; set; }
}
```
```csharp
public class LedgerKeyDerivation
{
}
    public string KeyId { get; set; };
    public string DerivationPath { get; set; };
    public byte[] PublicKey { get; set; };
    public byte[] DerivedKey { get; set; };
    public DateTime CreatedAt { get; set; }
}
```
```csharp
internal class LedgerKeyDerivationDto
{
}
    public string KeyId { get; set; };
    public string DerivationPath { get; set; };
    public string PublicKey { get; set; };
    public string DerivedKey { get; set; };
    public DateTime CreatedAt { get; set; }
    public static LedgerKeyDerivationDto FromDerivation(LedgerKeyDerivation d);;
    public LedgerKeyDerivation ToDerivation();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Threshold/ThresholdBls12381Strategy.cs
```csharp
public sealed class ThresholdBls12381Strategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<BlsDkgRound1> InitiateDkgAsync(string keyId, ISecurityContext context);
    public async Task<BlsDkgResult> FinalizeDkgAsync(string keyId, BlsDkgRound1[] round1Messages, ISecurityContext context);
    public async Task<BlsPartialSignature> CreatePartialSignatureAsync(string keyId, byte[] message, ISecurityContext context);
    public byte[] CombinePartialSignatures(BlsPartialSignature[] partialSignatures, int[] signerIndices);
    public async Task<bool> VerifySignatureAsync(string keyId, byte[] message, byte[] signature, ISecurityContext context);
    public byte[] AggregateSignatures(byte[][] signatures);
    public byte[] AggregatePublicKeys(byte[][] publicKeys);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override void Dispose();
}
```
```csharp
public class BlsConfig
{
}
    public int Threshold { get; set; };
    public int TotalParties { get; set; };
    public int PartyIndex { get; set; };
    public string? StoragePath { get; set; }
    public string? DomainSeparationTag { get; set; }
}
```
```csharp
internal class BlsKeyData
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public BigInteger SecretKeyShare { get; set; };
    public byte[]? PublicKeyShare { get; set; }
    public byte[]? AggregatedPublicKey { get; set; }
    public BigInteger[]? PolynomialCoeffs { get; set; }
    public byte[][]? FeldmanCommitments { get; set; }
    public Dictionary<int, byte[]>? DkgShares { get; set; }
    public int DkgPhase { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
internal class BlsKeyDataSerialized
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public byte[]? SecretKeyShare { get; set; }
    public byte[]? PublicKeyShare { get; set; }
    public byte[]? AggregatedPublicKey { get; set; }
    public int DkgPhase { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
public class BlsDkgRound1
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[][] Commitments { get; set; };
    public Dictionary<int, byte[]> ShareForRecipient { get; set; };
}
```
```csharp
public class BlsDkgResult
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[]? PublicKeyShare { get; set; }
    public byte[]? AggregatedPublicKey { get; set; }
    public bool Success { get; set; }
}
```
```csharp
public class BlsPartialSignature
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[] PartialSignature { get; set; };
    public byte[] Message { get; set; };
    public byte[]? PublicKeyShare { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Threshold/MultiPartyComputationStrategy.cs
```csharp
public sealed class MultiPartyComputationStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<DkgRound1Message> InitiateDkgAsync(string keyId, ISecurityContext context);
    public async Task<DkgRound2Message> ProcessDkgRound1Async(string keyId, DkgRound1Message[] round1Messages, ISecurityContext context);
    public async Task FinalizeDkgAsync(string keyId, DkgRound2Message[] round2Messages, ISecurityContext context);
    public async Task<MpcPartialSignature> CreatePartialSignatureAsync(string keyId, byte[] messageHash, int[] participatingParties, ISecurityContext context);
    public static byte[] CombinePartialSignatures(MpcPartialSignature[] partialSignatures);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class MpcConfig
{
}
    public int Threshold { get; set; };
    public int TotalParties { get; set; };
    public int PartyIndex { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class MpcKeyData
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public BigInteger MyShare { get; set; };
    public ECPoint? PublicKey { get; set; }
    public ECPoint[]? Commitments { get; set; }
    public BigInteger[]? PolynomialCoefficients { get; set; }
    public Dictionary<int, ECPoint[]>? AllCommitments { get; set; }
    public int DkgPhase { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
internal class MpcKeyDataSerialized
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public byte[]? MyShare { get; set; }
    public byte[]? PublicKey { get; set; }
    public byte[][]? Commitments { get; set; }
    public int DkgPhase { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
public class DkgRound1Message
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[][] Commitments { get; set; };
}
```
```csharp
public class DkgRound2Message
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public Dictionary<int, byte[]> EncryptedShares { get; set; };
}
```
```csharp
public class MpcPartialSignature
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[] R { get; set; };
    public byte[] PartialS { get; set; };
    public byte[] MessageHash { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Threshold/FrostStrategy.cs
```csharp
public sealed class FrostStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<FrostDkgRound1> InitiateDkgAsync(string keyId, ISecurityContext context);
    public async Task<FrostDkgRound2> ProcessDkgRound1Async(string keyId, FrostDkgRound1[] round1Messages, ISecurityContext context);
    public async Task<FrostDkgResult> FinalizeDkgAsync(string keyId, FrostDkgRound2[] round2Messages, ISecurityContext context);
    public async Task<FrostNonceCommitment[]> GenerateNoncesAsync(string keyId, int count, ISecurityContext context);
    public async Task<FrostSignRound1> BeginSigningAsync(string keyId, byte[] message, int[] signerIndices, ISecurityContext context);
    public async Task<FrostSignRound2> ComputeSignatureShareAsync(string keyId, FrostSignRound1[] round1Messages, ISecurityContext context);
    public async Task<byte[]> AggregateSignaturesAsync(string keyId, FrostSignRound2[] round2Messages, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override void Dispose();
}
```
```csharp
public class FrostConfig
{
}
    public int Threshold { get; set; };
    public int TotalParties { get; set; };
    public int PartyIndex { get; set; };
    public int PreprocessBatchSize { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class FrostKeyData
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public BigInteger SecretShare { get; set; };
    public ECPoint? PublicShare { get; set; }
    public ECPoint? GroupPublicKey { get; set; }
    public BigInteger[]? PolynomialCoeffs { get; set; }
    public ECPoint[]? Commitments { get; set; }
    public Dictionary<int, ECPoint[]>? AllCommitments { get; set; }
    public Queue<FrostNonce> NoncePool { get; set; };
    public FrostSigningState? CurrentSigningState { get; set; }
    public int DkgPhase { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
internal class FrostKeyDataSerialized
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public byte[]? SecretShare { get; set; }
    public byte[]? PublicShare { get; set; }
    public byte[]? GroupPublicKey { get; set; }
    public int DkgPhase { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
internal class FrostNonce
{
}
    public BigInteger D { get; set; };
    public BigInteger E { get; set; };
    public required ECPoint DCommitment { get; set; }
    public required ECPoint ECommitment { get; set; }
    public int Index { get; set; }
}
```
```csharp
internal class FrostSigningState
{
}
    public byte[] Message { get; set; };
    public int[] SignerIndices { get; set; };
    public required FrostNonce ActiveNonce { get; set; }
    public ECPoint? R { get; set; }
    public BigInteger? Challenge { get; set; }
    public int Phase { get; set; }
}
```
```csharp
public class FrostDkgRound1
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[][] Commitments { get; set; };
    public byte[] ProofOfKnowledge { get; set; };
}
```
```csharp
public class FrostDkgRound2
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public Dictionary<int, byte[]> Shares { get; set; };
}
```
```csharp
public class FrostDkgResult
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[]? PublicShare { get; set; }
    public byte[]? GroupPublicKey { get; set; }
    public bool Success { get; set; }
}
```
```csharp
public class FrostNonceCommitment
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int NonceIndex { get; set; }
    public byte[] D { get; set; };
    public byte[] E { get; set; };
}
```
```csharp
public class FrostSignRound1
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[] D { get; set; };
    public byte[] E { get; set; };
    public byte[] Commitment { get; set; };
}
```
```csharp
public class FrostSignRound2
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[] Z { get; set; };
    public byte[] R { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Threshold/ThresholdEcdsaStrategy.cs
```csharp
public sealed class ThresholdEcdsaStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<Gg20KeyGenRound1> InitiateKeyGenerationAsync(string keyId, ISecurityContext context);
    public async Task<Gg20KeyGenRound2> ProcessKeyGenRound1Async(string keyId, Gg20KeyGenRound1[] round1Messages, ISecurityContext context);
    public async Task<Gg20PublicKey> FinalizeKeyGenerationAsync(string keyId, Gg20KeyGenRound2[] round2Messages, ISecurityContext context);
    public async Task<Gg20SignRound1> InitiateSigningAsync(string keyId, byte[] messageHash, int[] signers, ISecurityContext context);
    public async Task<Gg20SignRound2> ProcessSignRound1Async(string keyId, Gg20SignRound1[] round1Messages, ISecurityContext context);
    public async Task<Gg20SignRound3> ProcessSignRound2Async(string keyId, Gg20SignRound2[] round2Messages, ISecurityContext context);
    public async Task<byte[]> CombineSignaturesAsync(string keyId, Gg20SignRound3[] round3Messages, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override void Dispose();
}
```
```csharp
public class ThresholdEcdsaConfig
{
}
    public int Threshold { get; set; };
    public int TotalParties { get; set; };
    public int PartyIndex { get; set; };
    public int PaillierKeyBits { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class ThresholdEcdsaKeyData
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public BigInteger KeyShare { get; set; };
    public ECPoint? PublicKeyShare { get; set; }
    public ECPoint? AggregatedPublicKey { get; set; }
    public BigInteger[]? PolynomialCoeffs { get; set; }
    public ECPoint[]? FeldmanCommitments { get; set; }
    public BigInteger PaillierN { get; set; };
    public BigInteger PaillierLambda { get; set; };
    public BigInteger PaillierMu { get; set; };
    public BigInteger RingPedersenN { get; set; };
    public BigInteger RingPedersenS { get; set; };
    public BigInteger RingPedersenT { get; set; };
    public Dictionary<int, BigInteger>? OtherPartiesPaillierN { get; set; }
    public Dictionary<int, ECPoint[]>? OtherPartiesCommitments { get; set; }
    public int KeyGenPhase { get; set; }
    public SigningState? CurrentSigningState { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
internal class ThresholdEcdsaKeyDataSerialized
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public byte[]? KeyShare { get; set; }
    public byte[]? PublicKeyShare { get; set; }
    public byte[]? AggregatedPublicKey { get; set; }
    public byte[]? PaillierN { get; set; }
    public int KeyGenPhase { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
```
```csharp
internal class SigningState
{
}
    public byte[] MessageHash { get; set; };
    public int[] Signers { get; set; };
    public BigInteger Ki { get; set; };
    public BigInteger GammaI { get; set; };
    public BigInteger SigmaI { get; set; };
    public ECPoint? R { get; set; }
    public Dictionary<int, ECPoint>? OtherRi { get; set; }
    public Dictionary<int, ECPoint>? OtherGammaI { get; set; }
    public int Phase { get; set; }
}
```
```csharp
public class Gg20KeyGenRound1
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[] PaillierN { get; set; };
    public byte[][] FeldmanCommitments { get; set; };
    public byte[] RingPedersenN { get; set; };
    public byte[] RingPedersenS { get; set; };
    public byte[] RingPedersenT { get; set; };
}
```
```csharp
public class Gg20KeyGenRound2
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public Dictionary<int, byte[]> EncryptedShares { get; set; };
}
```
```csharp
public class Gg20PublicKey
{
}
    public string KeyId { get; set; };
    public byte[] PublicKey { get; set; };
    public Dictionary<int, byte[]> PartyPublicKeys { get; set; };
}
```
```csharp
public class Gg20SignRound1
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[] Ri { get; set; };
    public byte[] GammaI { get; set; };
    public byte[] EncryptedKiGammaI { get; set; };
    public byte[] Commitment { get; set; };
}
```
```csharp
public class Gg20SignRound2
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[] DeltaI { get; set; };
}
```
```csharp
public class Gg20SignRound3
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[] PartialS { get; set; };
    public byte[] R { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Threshold/SsssStrategy.cs
```csharp
public sealed class SsssStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();;
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<GuardianRegistrationResult> RegisterGuardianAsync(string keyId, GuardianInfo guardianInfo, ISecurityContext context);
    public async Task DistributeSharesAsync(string keyId, ISecurityContext context);
    public async Task<RecoveryRequest> InitiateRecoveryAsync(string keyId, string requesterId, string reason, ISecurityContext context);
    public async Task<RecoveryApprovalResult> ApproveRecoveryAsync(string keyId, string requestId, string guardianId, byte[] shareValue, string? verificationCode, ISecurityContext context);
    public async Task<byte[]> CompleteRecoveryAsync(string keyId, string requestId, ISecurityContext context);
    public async Task CancelRecoveryAsync(string keyId, string requestId, ISecurityContext context);
    public async Task RotateGuardianShareAsync(string keyId, string guardianId, ISecurityContext context);
    public async Task RemoveGuardianAsync(string keyId, string guardianId, ISecurityContext context);
    public async Task<RecoveryStatus?> GetRecoveryStatusAsync(string keyId);
    public async Task<GuardianShareExport> ExportGuardianShareAsync(string keyId, string guardianId, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default);
    public override void Dispose();
}
```
```csharp
public class SsssConfig
{
}
    public int Threshold { get; set; };
    public int RecoveryDelayMinutes { get; set; };
    public int MaxRecoveryAttempts { get; set; };
    public bool RequireIdentityVerification { get; set; };
    public string? StoragePath { get; set; }
}
```
```csharp
internal class SsssKeyData
{
}
    public string KeyId { get; set; };
    public int Threshold { get; set; }
    public int EffectiveThreshold { get; set; }
    public int KeySizeBytes { get; set; }
    public bool IsDistributed { get; set; }
    public byte[]? PendingSecret { get; set; }
    public Dictionary<string, SsssShare> Shares { get; set; };
    public RecoveryRequest? ActiveRecoveryRequest { get; set; }
    public int RecoveryDelayMinutes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? LastRotatedAt { get; set; }
}
```
```csharp
internal class SsssShare
{
}
    public string GuardianId { get; set; };
    public int ShareIndex { get; set; }
    public int Weight { get; set; };
    public byte[]? ShareValue { get; set; }
    public byte[]? ShareHash { get; set; }
    public bool IsAuthorized { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AuthorizedAt { get; set; }
    public DateTime? RotatedAt { get; set; }
}
```
```csharp
public class Guardian
{
}
    public string GuardianId { get; set; };
    public string Name { get; set; };
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int Weight { get; set; };
    public string VerificationMethod { get; set; };
    public byte[]? PublicKey { get; set; }
    public DateTime RegisteredAt { get; set; }
    public string? RegisteredBy { get; set; }
    public bool IsActive { get; set; }
}
```
```csharp
public class GuardianInfo
{
}
    public string GuardianId { get; set; };
    public string Name { get; set; };
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int Weight { get; set; };
    public string VerificationMethod { get; set; };
    public byte[]? PublicKey { get; set; }
}
```
```csharp
public class GuardianRegistrationResult
{
}
    public string GuardianId { get; set; };
    public int ShareIndex { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
}
```
```csharp
public class RecoveryRequest
{
}
    public string RequestId { get; set; };
    public string KeyId { get; set; };
    public string RequesterId { get; set; };
    public string? Reason { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime CanCompleteAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public RecoveryStatus Status { get; set; }
    public int RequiredWeight { get; set; }
    public int CurrentWeight { get; set; }
    public List<string> ApprovedShares { get; set; };
}
```
```csharp
public class RecoveryApprovalResult
{
}
    public bool Success { get; set; }
    public string GuardianId { get; set; };
    public int CurrentWeight { get; set; }
    public int RequiredWeight { get; set; }
    public bool CanComplete { get; set; }
    public TimeSpan TimeUntilCompletion { get; set; }
    public string? Message { get; set; }
}
```
```csharp
internal class RecoveryAttempt
{
}
    public string KeyId { get; set; };
    public string RequestId { get; set; };
    public string RequesterId { get; set; };
    public DateTime AttemptedAt { get; set; }
    public string Status { get; set; };
}
```
```csharp
public class GuardianShareExport
{
}
    public string KeyId { get; set; };
    public string GuardianId { get; set; };
    public int ShareIndex { get; set; }
    public byte[] ShareValue { get; set; };
    public bool IsEncrypted { get; set; }
    public byte[]? ShareHash { get; set; }
    public int Weight { get; set; }
    public DateTime ExportedAt { get; set; }
}
```
```csharp
internal class SsssStorageData
{
}
    public Dictionary<string, SsssKeyData>? Keys { get; set; }
    public Dictionary<string, Guardian>? Guardians { get; set; }
    public List<RecoveryAttempt>? RecoveryLog { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Threshold/ShamirSecretStrategy.cs
```csharp
public sealed class ShamirSecretStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<ShareDistributionResult> DistributeShareAsync(string keyId, int shareIndex, string shareholderId, ISecurityContext context);
    public async Task CollectShareAsync(string keyId, int shareIndex, byte[] shareValue, string shareholderId, ISecurityContext context);
    public async Task RefreshSharesAsync(string keyId, ISecurityContext context);
    public async Task<bool> VerifyShareAsync(string keyId, int shareIndex, byte[] shareValue);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
internal class ShamirShare
{
}
    public int Index { get; set; }
    public BigInteger Value { get; set; };
}
```
```csharp
public class ShamirConfig
{
}
    public int Threshold { get; set; };
    public int TotalShares { get; set; };
    public string? StoragePath { get; set; }
    public string[] ShareHolders { get; set; };
}
```
```csharp
internal class ShamirKeyData
{
}
    public string KeyId { get; set; };
    public int Threshold { get; set; }
    public int TotalShares { get; set; }
    public int KeySizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? LastRefreshedAt { get; set; }
    public Dictionary<int, byte[]?> Shares { get; set; };
}
```
```csharp
internal class ShamirKeyDataSerialized
{
}
    public string KeyId { get; set; };
    public int Threshold { get; set; }
    public int TotalShares { get; set; }
    public int KeySizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? LastRefreshedAt { get; set; }
    public Dictionary<int, string> Shares { get; set; };
}
```
```csharp
public class ShareDistributionResult
{
}
    public string KeyId { get; set; };
    public int ShareIndex { get; set; }
    public byte[] ShareValue { get; set; };
    public int Threshold { get; set; }
    public int TotalShares { get; set; }
    public string? ShareholderId { get; set; }
    public DateTime DistributedAt { get; set; }
    public string? DistributedBy { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Privacy/SmpcVaultStrategy.cs
```csharp
internal static class SmpcCurveParams
{
}
    internal static readonly X9ECParameters Curve = CustomNamedCurves.GetByName("secp256k1");
    internal static readonly ECDomainParameters Domain = new(Curve.Curve, Curve.G, Curve.N, Curve.H);
}
```
```csharp
public sealed class SmpcVaultStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public SmpcVaultStrategy();
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override async Task<string> GetCurrentKeyIdAsync();
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<DkgRound1Message> InitiateDkgAsync(string keyId, ISecurityContext context);
    public async Task<DkgRound2Message> ProcessDkgRound1Async(string keyId, DkgRound1Message[] round1Messages, ISecurityContext context);
    public async Task FinalizeDkgAsync(string keyId, DkgRound2Message[] round2Messages, ISecurityContext context);
    public async Task<SmpcPartialSignature> CreatePartialSignatureAsync(string keyId, byte[] messageHash, int[] participatingParties, ISecurityContext context);
    public static byte[] CombinePartialSignatures(SmpcPartialSignature[] partialSignatures);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class SmpcConfig
{
}
    public int Parties { get; set; };
    public int Threshold { get; set; };
    public int PartyIndex { get; set; };
    public MpcProtocol Protocol { get; set; };
    public string[] PartyEndpoints { get; set; };
    public int OperationTimeoutMs { get; set; };
    public string? StoragePath { get; set; }
    public string? AuditLogPath { get; set; }
}
```
```csharp
internal class SmpcKeyData
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public MpcProtocol Protocol { get; set; }
    public BigInteger MyShare { get; set; };
    public ECPoint? PublicKey { get; set; }
    public ECPoint[]? Commitments { get; set; }
    public BigInteger[]? PolynomialCoefficients { get; set; }
    public Dictionary<int, ECPoint[]>? AllCommitments { get; set; }
    public int DkgPhase { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? CorrelationId { get; set; }
}
```
```csharp
internal class SmpcKeyDataSerialized
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public int Threshold { get; set; }
    public int TotalParties { get; set; }
    public MpcProtocol Protocol { get; set; }
    public byte[]? MyShare { get; set; }
    public byte[]? PublicKey { get; set; }
    public byte[][]? Commitments { get; set; }
    public int DkgPhase { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? CorrelationId { get; set; }
}
```
```csharp
public class DkgRound1Message
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[][] Commitments { get; set; };
    public string? CorrelationId { get; set; }
}
```
```csharp
public class DkgRound2Message
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public Dictionary<int, byte[]> EncryptedShares { get; set; };
    public string? CorrelationId { get; set; }
}
```
```csharp
public class SmpcPartialSignature
{
}
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public byte[] R { get; set; };
    public byte[] PartialS { get; set; };
    public byte[] MessageHash { get; set; };
    public string? CorrelationId { get; set; }
}
```
```csharp
internal class AuditEvent
{
}
    public DateTime Timestamp { get; set; }
    public string Operation { get; set; };
    public string KeyId { get; set; };
    public int PartyIndex { get; set; }
    public string UserId { get; set; };
    public bool Success { get; set; }
    public string? CorrelationId { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
```
```csharp
internal class AuditLogger : IDisposable
{
}
    public void Initialize(string logPath);
    public async Task LogAsync(AuditEvent auditEvent);
    public void Dispose();
}
```
```csharp
public sealed class GarbledCircuit
{
}
    public int TotalWires { get; }
    public int TotalGates;;
    public byte[] CircuitHash { get; private set; };
    public GarbledCircuit(GarbledOperation operation, int inputBitsA, int inputBitsB);
    public byte[][] GetInputLabelsA(bool[] inputBits);
    public (byte[][] labels0, byte[][] labels1) GetInputLabelsForOT();
    public byte[] Serialize();
    public Dictionary<string, bool> GetOutputDecodingTable();
    public byte[][] Evaluate(byte[][] inputLabelsA, byte[][] inputLabelsB);
}
```
```csharp
public class GarbledGate
{
}
    public int LeftInput { get; set; }
    public int RightInput { get; set; }
    public int Output { get; set; }
    public GarbledOperation Type { get; set; }
    public byte[][]? GarbledTable { get; set; }
}
```
```csharp
public sealed class ObliviousTransfer
{
}
    public OtSenderSetup SenderSetup();
    public OtReceiverResponse ReceiverChoose(byte[] senderA, bool choiceBit);
    public OtSenderMessages SenderEncrypt(OtSenderSetup setup, byte[] receiverB, byte[] message0, byte[] message1);
    public byte[] ReceiverDecrypt(OtReceiverResponse response, OtSenderMessages messages, bool choiceBit);
    public async Task<byte[][]> BatchOTAsync(byte[][] messages0, byte[][] messages1, bool[] choices, Func<byte[], Task<byte[]>> sendAndReceive);
}
```
```csharp
public class OtSenderSetup
{
}
    public byte[] A { get; set; };
    public byte[] PrivateA { get; set; };
}
```
```csharp
public class OtReceiverResponse
{
}
    public byte[] B { get; set; };
    public byte[] ReceiverKey { get; set; };
}
```
```csharp
public class OtSenderMessages
{
}
    public byte[] EncryptedMessage0 { get; set; };
    public byte[] EncryptedMessage1 { get; set; };
}
```
```csharp
public sealed class ObliviousTransferN
{
}
    public async Task<byte[]> ReceiveOneOfNAsync(byte[][] messages, int choice, Func<byte[], Task<byte[]>> sendAndReceive);
}
```
```csharp
public sealed class ArithmeticCircuit
{
}
    public class SecretShare;
    public SecretShare[] CreateAdditiveShares(BigInteger secret, int numParties);
    public BigInteger ReconstructFromAdditiveShares(SecretShare[] shares);
    public SecretShare AddShares(SecretShare a, SecretShare b);
    public SecretShare AddConstant(SecretShare share, BigInteger constant, bool isFirstParty);
    public SecretShare MultiplyByConstant(SecretShare share, BigInteger constant);
    public async Task<SecretShare> MultiplySharesAsync(SecretShare x, SecretShare y, BeaverTriple triple, Func<BigInteger, Task<BigInteger[]>> broadcastAndCollect);
    public BeaverTriple GenerateBeaverTriple(int partyIndex, int numParties);
    public async Task<SecretShare> DotProductAsync(SecretShare[] vectorX, SecretShare[] vectorY, BeaverTriple[] triples, Func<BigInteger, Task<BigInteger[]>> broadcastAndCollect);
}
```
```csharp
public class SecretShare
{
}
    public BigInteger Value { get; set; };
    public int PartyIndex { get; set; }
    public string ShareId { get; set; };
}
```
```csharp
public class BeaverTriple
{
}
    public BigInteger ShareA { get; set; };
    public BigInteger ShareB { get; set; };
    public BigInteger ShareC { get; set; };
}
```
```csharp
public sealed class BooleanCircuit
{
}
    public class SharedBit;
    public SharedBit[] CreateXorShares(bool secret, int numParties);
    public bool ReconstructFromXorShares(SharedBit[] shares);
    public SharedBit Xor(SharedBit a, SharedBit b);
    public SharedBit XorConstant(SharedBit share, bool constant, bool isFirstParty);
    public SharedBit Not(SharedBit share, bool isFirstParty);
    public async Task<SharedBit> AndAsync(SharedBit x, SharedBit y, AndTriple triple, Func<bool, Task<bool[]>> broadcastAndCollect);
    public async Task<SharedBit> OrAsync(SharedBit a, SharedBit b, AndTriple triple, Func<bool, Task<bool[]>> broadcastAndCollect);
    public AndTriple GenerateAndTriple(int partyIndex, int numParties);
    public async Task<SharedBit> MultiAndAsync(SharedBit[] bits, AndTriple[] triples, Func<bool, Task<bool[]>> broadcastAndCollect);
    public async Task<SharedBit> EqualityTestAsync(SharedBit[] bitsA, SharedBit[] bitsB, AndTriple[] triples, Func<bool, Task<bool[]>> broadcastAndCollect);
}
```
```csharp
public class SharedBit
{
}
    public bool Share { get; set; }
    public int PartyIndex { get; set; }
    public string BitId { get; set; };
}
```
```csharp
public class AndTriple
{
}
    public bool ShareA { get; set; }
    public bool ShareB { get; set; }
    public bool ShareC { get; set; }
}
```
```csharp
public sealed class SmpcOperations
{
}
    public async Task<BigInteger> SecureSumAsync(BigInteger myInput, int myPartyIndex, int numParties, Func<BigInteger, Task<BigInteger[]>> broadcastAndCollect);
    public async Task<BigInteger> SecureAverageAsync(BigInteger myInput, int myPartyIndex, int numParties, Func<BigInteger, Task<BigInteger[]>> broadcastAndCollect);
    public async Task<ArithmeticCircuit.SecretShare> SecureComparisonAsync(ArithmeticCircuit.SecretShare shareA, ArithmeticCircuit.SecretShare shareB, int numBits, BeaverTriple[] triples, Func<BigInteger, Task<BigInteger[]>> broadcastAndCollect);
    public async Task<int> PrivateSetIntersectionCardinalityAsync(byte[][] mySet, int myPartyIndex, byte[] sharedKey, Func<byte[][], Task<byte[][]>> exchangeHashes);
    public async Task<byte[][]> PrivateSetIntersectionAsync(byte[][] mySet, int myPartyIndex, byte[] sharedKey, Func<(byte[] hash, byte[] encrypted)[], Task<(byte[] hash, byte[] encrypted)[]>> exchangeEncryptedElements);
    public async Task<BigInteger> SecureMaxAsync(BigInteger myInput, int myPartyIndex, int numParties, int numBits, BeaverTriple[] triples, Func<BigInteger, Task<BigInteger[]>> broadcastAndCollect);
    public async Task<BigInteger> SecureMinAsync(BigInteger myInput, int myPartyIndex, int numParties, int numBits, BeaverTriple[] triples, Func<BigInteger, Task<BigInteger[]>> broadcastAndCollect);
    public async Task<BigInteger> SecureMedianAsync(BigInteger myInput, int myPartyIndex, int numParties, Func<BigInteger, Task<BigInteger[]>> broadcastAndCollect);
}
```
```csharp
public sealed class MaliciousSecurity
{
}
    public class AuthenticatedShare;
    public class MacKeyShare;
    public AuthenticatedShare[] CreateAuthenticatedShares(BigInteger secret, BigInteger[] alphaShares, int numParties);
    public async Task<bool> VerifyMacAsync(BigInteger openedValue, AuthenticatedShare[] shares, MacKeyShare myAlphaShare, Func<BigInteger, Task<BigInteger[]>> broadcastAndCollect);
    public class PedersenCommitment;
    public PedersenCommitment Commit(BigInteger value, ECPoint h);
    public bool VerifyCommitment(PedersenCommitment commitment, BigInteger value, BigInteger randomness, ECPoint h);
    public class SchnorrProof;
    public SchnorrProof CreateSchnorrProof(BigInteger secretX, ECPoint publicY);
    public bool VerifySchnorrProof(SchnorrProof proof, ECPoint publicY);
    public async Task<int?> DetectCheatingPartyAsync(AuthenticatedShare[] shares, BigInteger claimedValue, MacKeyShare[] alphaShares, Func<int, PedersenCommitment, Task<bool>> verifyPartyCommitment);
    public async Task<bool> VerifyMultiplicationTriplesAsync(BeaverTriple[] triples, int numToCheck, Func<int, Task<(BigInteger a, BigInteger b, BigInteger c)>> revealTriple);
}
```
```csharp
public class AuthenticatedShare
{
}
    public BigInteger ValueShare { get; set; };
    public BigInteger MacShare { get; set; };
    public int PartyIndex { get; set; }
    public string ShareId { get; set; };
}
```
```csharp
public class MacKeyShare
{
}
    public BigInteger AlphaShare { get; set; };
    public int PartyIndex { get; set; }
}
```
```csharp
public class PedersenCommitment
{
}
    public ECPoint Commitment { get; set; };
    public BigInteger? Randomness { get; set; }
    public BigInteger? Value { get; set; }
}
```
```csharp
public class SchnorrProof
{
}
    public ECPoint Commitment { get; set; };
    public BigInteger Challenge { get; set; };
    public BigInteger Response { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/DevCiCd/GitCryptStrategy.cs
```csharp
public sealed class GitCryptStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public async Task<string> ExportSymmetricKeyAsync(CancellationToken cancellationToken = default);
    public async Task AddGpgUserAsync(string gpgUserId, CancellationToken cancellationToken = default);
    public async Task<IReadOnlyList<string>> ListGpgUsersAsync(CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class GitCryptConfig
{
}
    public string RepositoryPath { get; set; };
    public string KeyStorePath { get; set; };
    public string GitCryptPath { get; set; };
    public string SymmetricKeyEnvVar { get; set; };
    public bool AutoInit { get; set; };
}
```
```csharp
internal class GitCryptKeyData
{
}
    public string Key { get; set; };
    public string KeyId { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string Algorithm { get; set; };
}
```
```csharp
internal class ProcessResult
{
}
    public int ExitCode { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/DevCiCd/EnvironmentKeyStoreStrategy.cs
```csharp
public sealed class EnvironmentKeyStoreStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
}
```
```csharp
public class EnvironmentKeyStoreConfig
{
}
    public string KeyPrefix { get; set; };
    public string DefaultKeyEnvVar { get; set; };
    public bool AutoGenerateIfMissing { get; set; };
    public string KeyDerivationSalt { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/DevCiCd/OnePasswordConnectStrategy.cs
```csharp
public sealed class OnePasswordConnectStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public OnePasswordConnectStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class OnePasswordConnectConfig
{
}
    public string ConnectHost { get; set; };
    public string? ConnectToken { get; set; }
    public string ConnectTokenEnvVar { get; set; };
    public string? VaultId { get; set; }
    public string? VaultName { get; set; }
    public string ItemCategory { get; set; };
}
```
```csharp
internal class OpVault
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
    [JsonPropertyName("name")]
public string? Name { get; set; }
    [JsonPropertyName("description")]
public string? Description { get; set; }
    [JsonPropertyName("attributeVersion")]
public int? AttributeVersion { get; set; }
    [JsonPropertyName("contentVersion")]
public int? ContentVersion { get; set; }
    [JsonPropertyName("createdAt")]
public DateTime? CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")]
public DateTime? UpdatedAt { get; set; }
}
```
```csharp
internal class OpVaultRef
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
}
```
```csharp
internal class OpItem
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
    [JsonPropertyName("title")]
public string? Title { get; set; }
    [JsonPropertyName("vault")]
public OpVaultRef? Vault { get; set; }
    [JsonPropertyName("category")]
public string? Category { get; set; }
    [JsonPropertyName("tags")]
public List<string>? Tags { get; set; }
    [JsonPropertyName("version")]
public int? Version { get; set; }
    [JsonPropertyName("state")]
public string? State { get; set; }
    [JsonPropertyName("createdAt")]
public DateTime? CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")]
public DateTime? UpdatedAt { get; set; }
    [JsonPropertyName("lastEditedBy")]
public string? LastEditedBy { get; set; }
    [JsonPropertyName("fields")]
public List<OpField>? Fields { get; set; }
    [JsonPropertyName("sections")]
public List<OpSection>? Sections { get; set; }
}
```
```csharp
internal class OpField
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
    [JsonPropertyName("type")]
public string? Type { get; set; }
    [JsonPropertyName("purpose")]
public string? Purpose { get; set; }
    [JsonPropertyName("label")]
public string? Label { get; set; }
    [JsonPropertyName("value")]
public string? Value { get; set; }
    [JsonPropertyName("section")]
public OpSectionRef? Section { get; set; }
}
```
```csharp
internal class OpSection
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
    [JsonPropertyName("label")]
public string? Label { get; set; }
}
```
```csharp
internal class OpSectionRef
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/DevCiCd/AgeStrategy.cs
```csharp
public sealed class AgeStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public async Task<AgeIdentity> GenerateIdentityAsync(CancellationToken cancellationToken = default);
    public void AddRecipient(string publicKey);
    public async Task<string?> GetPublicKeyAsync(CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class AgeConfig
{
}
    public string KeyStorePath { get; set; };
    public string AgePath { get; set; };
    public string AgeKeygenPath { get; set; };
    public string? IdentityFile { get; set; }
    public string IdentityEnvVar { get; set; };
    public string PassphraseEnvVar { get; set; };
    public List<string> Recipients { get; set; };
    public string? RecipientsFile { get; set; }
    public bool AutoGenerateIdentity { get; set; };
}
```
```csharp
internal class AgeKeyData
{
}
    public string Key { get; set; };
    public string KeyId { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string Algorithm { get; set; };
}
```
```csharp
public class AgeIdentity
{
}
    public string PublicKey { get; set; };
    public string PrivateKey { get; set; };
    public string IdentityFile { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/DevCiCd/BitwardenConnectStrategy.cs
```csharp
public sealed class BitwardenConnectStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public BitwardenConnectStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class BitwardenSecretsConfig
{
}
    public string ApiUrl { get; set; };
    public string IdentityUrl { get; set; };
    public string? AccessToken { get; set; }
    public string AccessTokenEnvVar { get; set; };
    public string? ProjectId { get; set; }
    public string? OrganizationId { get; set; }
}
```
```csharp
internal class BitwardenTokenResponse
{
}
    [JsonPropertyName("access_token")]
public string? AccessToken { get; set; }
    [JsonPropertyName("expires_in")]
public int ExpiresIn { get; set; }
    [JsonPropertyName("token_type")]
public string? TokenType { get; set; }
    [JsonPropertyName("scope")]
public string? Scope { get; set; }
}
```
```csharp
internal class BitwardenSecret
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
    [JsonPropertyName("organizationId")]
public string? OrganizationId { get; set; }
    [JsonPropertyName("key")]
public string? Key { get; set; }
    [JsonPropertyName("value")]
public string? Value { get; set; }
    [JsonPropertyName("note")]
public string? Note { get; set; }
    [JsonPropertyName("creationDate")]
public DateTime? CreationDate { get; set; }
    [JsonPropertyName("revisionDate")]
public DateTime? RevisionDate { get; set; }
    [JsonPropertyName("projects")]
public List<BitwardenProjectRef>? Projects { get; set; }
}
```
```csharp
internal class BitwardenProjectRef
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
    [JsonPropertyName("name")]
public string? Name { get; set; }
}
```
```csharp
internal class BitwardenSecretsListResponse
{
}
    [JsonPropertyName("secrets")]
public List<BitwardenSecretSummary>? Secrets { get; set; }
}
```
```csharp
internal class BitwardenSecretSummary
{
}
    [JsonPropertyName("id")]
public string? Id { get; set; }
    [JsonPropertyName("organizationId")]
public string? OrganizationId { get; set; }
    [JsonPropertyName("key")]
public string? Key { get; set; }
    [JsonPropertyName("creationDate")]
public DateTime? CreationDate { get; set; }
    [JsonPropertyName("revisionDate")]
public DateTime? RevisionDate { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/DevCiCd/PassStrategy.cs
```csharp
public sealed class PassStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public async Task SyncAsync(CancellationToken cancellationToken = default);
    public async Task<IReadOnlyList<string>> GetGpgIdsAsync(string? subPath = null, CancellationToken cancellationToken = default);
    public async Task<string> GeneratePasswordAsync(string path, int length = 32, bool noSymbols = false, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class PassConfig
{
}
    public string PasswordStorePath { get; set; };
    public string PassPath { get; set; };
    public string? GpgId { get; set; }
    public string SubPath { get; set; };
    public bool AutoInit { get; set; };
    public bool? GitEnabled { get; set; }
    public string? GpgProgramPath { get; set; }
}
```
```csharp
internal class PassKeyData
{
}
    public string Key { get; set; };
    public string KeyId { get; set; };
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string Algorithm { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/CloudKms/GcpKmsStrategy.cs
```csharp
public sealed class GcpKmsStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public GcpKmsStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class GcpKmsConfig
{
}
    public string ProjectId { get; set; };
    public string Location { get; set; };
    public string KeyRing { get; set; };
    public string KeyName { get; set; };
    public string ServiceAccountJson { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/CloudKms/OracleVaultStrategy.cs
```csharp
public sealed class OracleVaultStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public OracleVaultStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class OracleVaultConfig
{
}
    public string Region { get; set; };
    public string TenancyOcid { get; set; };
    public string CompartmentOcid { get; set; };
    public string VaultOcid { get; set; };
    public string KeyOcid { get; set; };
    public string UserOcid { get; set; };
    public string Fingerprint { get; set; };
    public string PrivateKey { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/CloudKms/AwsKmsStrategy.cs
```csharp
public sealed class AwsKmsStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public AwsKmsStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class AwsKmsConfig
{
}
    public string Region { get; set; };
    public string AccessKeyId { get; set; };
    public string SecretAccessKey { get; set; };
    public string DefaultKeyId { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/CloudKms/AlibabaKmsStrategy.cs
```csharp
public sealed class AlibabaKmsStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public AlibabaKmsStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class AlibabaKmsConfig
{
}
    public string RegionId { get; set; };
    public string AccessKeyId { get; set; };
    public string AccessKeySecret { get; set; };
    public string DefaultKeyId { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/CloudKms/AzureKeyVaultStrategy.cs
```csharp
public sealed class AzureKeyVaultStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public AzureKeyVaultStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class AzureKeyVaultConfig
{
}
    public string VaultUrl { get; set; };
    public string TenantId { get; set; };
    public string ClientId { get; set; };
    public string ClientSecret { get; set; };
    public string DefaultKeyName { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/CloudKms/IbmKeyProtectStrategy.cs
```csharp
public sealed class IbmKeyProtectStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public IbmKeyProtectStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class IbmKeyProtectConfig
{
}
    public string InstanceId { get; set; };
    public string Region { get; set; };
    public string ApiKey { get; set; };
    public string DefaultKeyId { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/CloudKms/DigitalOceanVaultStrategy.cs
```csharp
public sealed class DigitalOceanVaultStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public DigitalOceanVaultStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class DigitalOceanVaultConfig
{
}
    public string ApiToken { get; set; };
    public string? DataCenter { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/SecretsManagement/DopplerStrategy.cs
```csharp
public sealed class DopplerStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public DopplerStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class DopplerConfig
{
}
    public string ServiceToken { get; set; };
    public string Project { get; set; };
    public string Config { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/SecretsManagement/CyberArkStrategy.cs
```csharp
public sealed class CyberArkStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public CyberArkStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class CyberArkConfig
{
}
    public string ApplianceUrl { get; set; };
    public string Account { get; set; };
    public string Username { get; set; };
    public string ApiKey { get; set; };
    public string Token { get; set; };
    public string PolicyPath { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/SecretsManagement/BeyondTrustStrategy.cs
```csharp
public sealed class BeyondTrustStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public BeyondTrustStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class BeyondTrustConfig
{
}
    public string BaseUrl { get; set; };
    public string ApiKey { get; set; };
    public string RunAsUser { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/SecretsManagement/DelineaStrategy.cs
```csharp
public sealed class DelineaStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public DelineaStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class DelineaConfig
{
}
    public string ServerUrl { get; set; };
    public string Username { get; set; };
    public string Password { get; set; };
    public string ApiKey { get; set; };
    public int FolderId { get; set; };
    public int SecretTemplateId { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/SecretsManagement/InfisicalStrategy.cs
```csharp
public sealed class InfisicalStrategy : KeyStoreStrategyBase
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public InfisicalStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class InfisicalConfig
{
}
    public string ApiUrl { get; set; };
    public string ClientId { get; set; };
    public string ClientSecret { get; set; };
    public string WorkspaceId { get; set; };
    public string Environment { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/SecretsManagement/AkeylessStrategy.cs
```csharp
public sealed class AkeylessStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public AkeylessStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class AkeylessConfig
{
}
    public string GatewayUrl { get; set; };
    public string AccessId { get; set; };
    public string AccessKey { get; set; };
    public string Token { get; set; };
    public string Path { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/SecretsManagement/VaultKeyStoreStrategy.cs
```csharp
public sealed class VaultKeyStoreStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
{
}
    public override KeyStoreCapabilities Capabilities;;
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken);
    public IReadOnlyList<string> SupportedWrappingAlgorithms;;
    public bool SupportsHsmKeyGeneration;;
    public VaultKeyStoreStrategy();
    protected override async Task InitializeStorage(CancellationToken cancellationToken);
    public override Task<string> GetCurrentKeyIdAsync();
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context);
    protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context);
    public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);
    public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);
    public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default);
    public override void Dispose();
}
```
```csharp
public class HashiCorpVaultConfig
{
}
    public string Address { get; set; };
    public string Token { get; set; };
    public string MountPath { get; set; };
    public string DefaultKeyName { get; set; };
}
```
