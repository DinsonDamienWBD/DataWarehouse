// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

// Use SDK types explicitly to avoid conflict with local types in parent namespace
using SdkWormWriteResult = DataWarehouse.SDK.Contracts.TamperProof.WormWriteResult;

namespace DataWarehouse.Plugins.TamperProof.Storage;

/// <summary>
/// Azure Blob immutability policy type.
/// Maps to Azure Blob Storage immutability policies.
/// </summary>
public enum AzureImmutabilityPolicyType
{
    /// <summary>
    /// Unlocked policy - Can be shortened, extended, or deleted.
    /// Use during testing or when policy may need adjustment.
    /// </summary>
    Unlocked,

    /// <summary>
    /// Locked policy - Cannot be shortened or deleted; can only be extended.
    /// Once locked, provides compliance-grade immutability similar to S3 Compliance mode.
    /// </summary>
    Locked
}

/// <summary>
/// Configuration for Azure Blob WORM storage using Immutable Storage.
/// </summary>
/// <param name="ConnectionString">Azure Storage connection string.</param>
/// <param name="ContainerName">Blob container name with immutability configured.</param>
/// <param name="DefaultPolicyType">Default immutability policy type.</param>
/// <param name="DefaultRetention">Default retention period. If default, uses 7 years.</param>
/// <param name="EnableVersionLevelImmutability">Enable version-level immutability (recommended).</param>
/// <param name="AccountName">Storage account name (alternative to connection string).</param>
/// <param name="AccountKey">Storage account key (alternative to connection string).</param>
/// <param name="UseManagedIdentity">Use Azure Managed Identity for authentication.</param>
public record AzureBlobWormConfiguration(
    string ConnectionString,
    string ContainerName,
    AzureImmutabilityPolicyType DefaultPolicyType = AzureImmutabilityPolicyType.Unlocked,
    TimeSpan DefaultRetention = default,
    bool EnableVersionLevelImmutability = true,
    string? AccountName = null,
    string? AccountKey = null,
    bool UseManagedIdentity = false)
{
    /// <summary>
    /// Gets the effective default retention period (7 years if not specified).
    /// </summary>
    public TimeSpan EffectiveDefaultRetention =>
        DefaultRetention == default ? TimeSpan.FromDays(7 * 365) : DefaultRetention;

    /// <summary>
    /// Validates the configuration.
    /// </summary>
    /// <returns>List of validation errors, empty if valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        var hasConnectionString = !string.IsNullOrWhiteSpace(ConnectionString);
        var hasAccountCredentials = !string.IsNullOrWhiteSpace(AccountName);
        var hasManagedIdentity = UseManagedIdentity;

        if (!hasConnectionString && !hasAccountCredentials && !hasManagedIdentity)
            errors.Add("Either ConnectionString, AccountName/AccountKey, or UseManagedIdentity must be configured.");

        if (hasAccountCredentials && !hasManagedIdentity && string.IsNullOrWhiteSpace(AccountKey))
            errors.Add("AccountKey is required when using AccountName without Managed Identity.");

        if (string.IsNullOrWhiteSpace(ContainerName))
            errors.Add("ContainerName is required.");

        if (EffectiveDefaultRetention <= TimeSpan.Zero)
            errors.Add("DefaultRetention must be positive.");

        return errors;
    }
}

/// <summary>
/// Azure-specific WORM write result with immutability policy information.
/// </summary>
public record AzureWormWriteResult
{
    /// <summary>Whether the write succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Blob name/path.</summary>
    public required string BlobName { get; init; }

    /// <summary>Blob version ID (for version-level immutability).</summary>
    public string? VersionId { get; init; }

    /// <summary>ETag returned by Azure (content identifier).</summary>
    public string? ETag { get; init; }

    /// <summary>Immutability policy expiry timestamp.</summary>
    public required DateTimeOffset ImmutabilityPolicyExpiry { get; init; }

    /// <summary>Immutability policy type applied.</summary>
    public required AzureImmutabilityPolicyType PolicyType { get; init; }

    /// <summary>Whether legal hold is active.</summary>
    public bool LegalHoldActive { get; init; }

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Content hash (MD5 or SHA256).</summary>
    public string? ContentHash { get; init; }

    /// <summary>Error message if write failed.</summary>
    public string? Error { get; init; }

    /// <summary>Azure request ID for troubleshooting.</summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// Creates a successful write result.
    /// </summary>
    public static AzureWormWriteResult CreateSuccess(
        string blobName,
        string versionId,
        string etag,
        DateTimeOffset policyExpiry,
        AzureImmutabilityPolicyType policyType,
        long sizeBytes,
        string contentHash,
        bool legalHoldActive = false,
        string? requestId = null)
    {
        return new AzureWormWriteResult
        {
            Success = true,
            BlobName = blobName,
            VersionId = versionId,
            ETag = etag,
            ImmutabilityPolicyExpiry = policyExpiry,
            PolicyType = policyType,
            LegalHoldActive = legalHoldActive,
            SizeBytes = sizeBytes,
            ContentHash = contentHash,
            RequestId = requestId
        };
    }

    /// <summary>
    /// Creates a failed write result.
    /// </summary>
    public static AzureWormWriteResult CreateFailure(string blobName, string error, string? requestId = null)
    {
        return new AzureWormWriteResult
        {
            Success = false,
            BlobName = blobName,
            ImmutabilityPolicyExpiry = DateTimeOffset.MinValue,
            PolicyType = AzureImmutabilityPolicyType.Unlocked,
            Error = error,
            RequestId = requestId
        };
    }
}

/// <summary>
/// Azure Blob WORM storage provider using Immutable Storage.
/// Provides immutable storage with time-based retention policies and legal holds.
///
/// Prerequisites:
/// - Storage account must be v2 or premium
/// - Container must be created with version-level immutability enabled, OR
/// - Container must have a default immutability policy configured
/// - Required RBAC roles: Storage Blob Data Contributor, Storage Blob Data Owner (for legal holds)
///
/// Supports:
/// - Time-based retention policies (unlocked or locked)
/// - Legal holds (indefinite preservation)
/// - Version-level immutability (recommended)
/// - Container-level default policies
/// </summary>
public class AzureWormStorage : WormStorageProviderPluginBase
{
    private readonly AzureBlobWormConfiguration _config;
    private readonly ILogger<AzureWormStorage> _logger;
    private readonly string _blobPrefix;
    private bool _immutabilityVerified;

    // Simulated storage for development/testing (in production, would use Azure SDK)
    private readonly Dictionary<string, AzureBlobRecord> _blobs = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new Azure Blob WORM storage instance.
    /// </summary>
    /// <param name="config">Azure Blob WORM configuration.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="blobPrefix">Optional prefix for all blob names.</param>
    public AzureWormStorage(
        AzureBlobWormConfiguration config,
        ILogger<AzureWormStorage> logger,
        string blobPrefix = "worm/")
    {
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid Azure configuration: {string.Join("; ", errors)}");

        _config = config;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blobPrefix = blobPrefix;
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.worm.azure";

    /// <inheritdoc/>
    public override string Name => "Azure Blob Immutable Storage (WORM)";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting Azure WORM storage provider for container {Container}", _config.ContainerName);
        await VerifyImmutabilityConfigurationAsync(ct);
        _logger.LogInformation("Azure WORM storage provider started successfully");
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _logger.LogInformation("Stopping Azure WORM storage provider for container {Container}", _config.ContainerName);
        // No cleanup needed for Azure client
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override WormEnforcementMode EnforcementMode =>
        _config.DefaultPolicyType == AzureImmutabilityPolicyType.Locked
            ? WormEnforcementMode.HardwareIntegrated
            : WormEnforcementMode.Software;

    /// <summary>
    /// Gets the container name.
    /// </summary>
    public string ContainerName => _config.ContainerName;

    /// <summary>
    /// Verifies that the Azure container has immutability configured.
    /// Should be called during initialization.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if immutability is properly configured.</returns>
    public async Task<bool> VerifyImmutabilityConfigurationAsync(CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Verifying Azure Blob immutability configuration for container {Container}",
            _config.ContainerName);

        try
        {
            // Finding 1023: AzureWormStorage uses in-memory Dictionary simulation because
            // Azure.Storage.Blobs (Azure SDK) is not referenced. VerifyImmutabilityConfigurationAsync
            // previously set _immutabilityVerified = true unconditionally after a Task.Delay(10).
            // The simulation is acceptable for dev/test, but we must NOT auto-verify in production
            // environments. We log a prominent warning and treat this as development mode only.
            _logger.LogWarning(
                "AzureWormStorage is running in SIMULATION MODE — Azure.Storage.Blobs SDK is not referenced. " +
                "All data is stored in-memory only. Do NOT use in production. " +
                "Integrate the Azure.Storage.Blobs NuGet package to enable real Azure WORM storage.");

            await Task.CompletedTask;
            _immutabilityVerified = false; // Not verified — simulation does not contact Azure

            _logger.LogInformation(
                "Azure Blob immutability simulation initialized for container {Container}: PolicyType={PolicyType}, DefaultRetention={Retention}, VersionLevel={VersionLevel}",
                _config.ContainerName,
                _config.DefaultPolicyType,
                _config.EffectiveDefaultRetention,
                _config.EnableVersionLevelImmutability);

            return false; // Not actually verified — production must use real Azure SDK
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify immutability configuration for container {Container}",
                _config.ContainerName);
            return false;
        }
    }

    /// <summary>
    /// Writes data to Azure Blob Storage with immutability policy.
    /// </summary>
    /// <param name="objectId">Unique object identifier.</param>
    /// <param name="data">Data stream to write.</param>
    /// <param name="retention">Retention policy.</param>
    /// <param name="policyType">Immutability policy type (Unlocked or Locked).</param>
    /// <param name="applyLegalHold">Whether to apply a legal hold.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Azure-specific write result with version information.</returns>
    public async Task<AzureWormWriteResult> WriteWithImmutabilityAsync(
        Guid objectId,
        Stream data,
        WormRetentionPolicy retention,
        AzureImmutabilityPolicyType? policyType = null,
        bool applyLegalHold = false,
        CancellationToken ct = default)
    {
        var blobName = BuildBlobName(objectId);
        var effectivePolicyType = policyType ?? _config.DefaultPolicyType;
        var policyExpiry = DateTimeOffset.UtcNow.Add(retention.RetentionPeriod);

        _logger.LogInformation(
            "Writing object {ObjectId} to Azure WORM storage with {PolicyType} policy, retention until {PolicyExpiry}",
            objectId, effectivePolicyType, policyExpiry);

        try
        {
            // Read data and compute hash
            var capacity = data.CanSeek && data.Length > 0 ? (int)data.Length : 0;
            using var ms = new MemoryStream(capacity);
            await data.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var contentHash = ComputeContentHash(bytes);
            var versionId = GenerateVersionId();
            var etag = $"\"{contentHash[..32]}\"";

            // In production, this would:
            // 1. Upload blob
            // var blobClient = new BlobClient(_config.ConnectionString, _config.ContainerName, blobName);
            // var uploadResponse = await blobClient.UploadAsync(new MemoryStream(bytes), overwrite: false, ct);
            //
            // 2. Set immutability policy (version-level)
            // var blobClient = blobClient.WithVersion(uploadResponse.Value.VersionId);
            // await blobClient.SetImmutabilityPolicyAsync(
            //     new BlobImmutabilityPolicy
            //     {
            //         ExpiresOn = policyExpiry,
            //         PolicyMode = effectivePolicyType == AzureImmutabilityPolicyType.Locked
            //             ? BlobImmutabilityPolicyMode.Locked : BlobImmutabilityPolicyMode.Unlocked
            //     }, ct);
            //
            // 3. Optionally set legal hold
            // if (applyLegalHold)
            //     await blobClient.SetLegalHoldAsync(true, ct);

            // Simulated storage
            lock (_lock)
            {
                _blobs[blobName] = new AzureBlobRecord
                {
                    BlobName = blobName,
                    VersionId = versionId,
                    Data = bytes,
                    ContentHash = contentHash,
                    ETag = etag,
                    PolicyType = effectivePolicyType,
                    PolicyExpiry = policyExpiry,
                    LegalHoldActive = applyLegalHold || retention.HasLegalHold,
                    CreatedAt = DateTimeOffset.UtcNow,
                    SizeBytes = bytes.Length,
                    LegalHolds = new List<AzureLegalHoldRecord>()
                };

                if (applyLegalHold || retention.HasLegalHold)
                {
                    _blobs[blobName].LegalHolds.Add(new AzureLegalHoldRecord
                    {
                        HoldId = "default-legal-hold",
                        Reason = "Applied during write",
                        PlacedAt = DateTimeOffset.UtcNow,
                        PlacedBy = "system"
                    });
                }
            }

            _logger.LogInformation(
                "Successfully wrote object {ObjectId} to Azure Blob: VersionId={VersionId}, Size={Size} bytes",
                objectId, versionId, bytes.Length);

            return AzureWormWriteResult.CreateSuccess(
                blobName,
                versionId,
                etag,
                policyExpiry,
                effectivePolicyType,
                bytes.Length,
                contentHash,
                applyLegalHold || retention.HasLegalHold);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write object {ObjectId} to Azure WORM storage", objectId);
            return AzureWormWriteResult.CreateFailure(blobName, ex.Message);
        }
    }

    /// <inheritdoc/>
    protected override async Task<SdkWormWriteResult> WriteInternalAsync(
        Guid objectId,
        Stream data,
        WormRetentionPolicy retention,
        WriteContext context,
        CancellationToken ct)
    {
        var azureResult = await WriteWithImmutabilityAsync(
            objectId,
            data,
            retention,
            _config.DefaultPolicyType,
            retention.HasLegalHold,
            ct);

        if (!azureResult.Success)
        {
            throw new InvalidOperationException($"Azure WORM write failed: {azureResult.Error}");
        }

        return new SdkWormWriteResult
        {
            StorageKey = azureResult.BlobName,
            ContentHash = azureResult.ContentHash!,
            HashAlgorithm = HashAlgorithmType.SHA256,
            SizeBytes = azureResult.SizeBytes,
            WrittenAt = DateTimeOffset.UtcNow,
            RetentionExpiry = azureResult.ImmutabilityPolicyExpiry,
            HardwareImmutabilityEnabled = _config.DefaultPolicyType == AzureImmutabilityPolicyType.Locked,
            ProviderMetadata = new Dictionary<string, string>
            {
                ["azure:VersionId"] = azureResult.VersionId ?? "",
                ["azure:ETag"] = azureResult.ETag ?? "",
                ["azure:Container"] = _config.ContainerName,
                ["azure:PolicyType"] = azureResult.PolicyType.ToString(),
                ["azure:LegalHold"] = azureResult.LegalHoldActive.ToString(),
                ["azure:VersionLevelImmutability"] = _config.EnableVersionLevelImmutability.ToString()
            }
        };
    }

    /// <inheritdoc/>
    public override async Task<Stream> ReadAsync(Guid objectId, CancellationToken ct = default)
    {
        var blobName = BuildBlobName(objectId);

        _logger.LogDebug("Reading object {ObjectId} from Azure WORM storage", objectId);

        // In production:
        // var blobClient = new BlobClient(_config.ConnectionString, _config.ContainerName, blobName);
        // var response = await blobClient.DownloadStreamingAsync(ct);
        // return response.Value.Content;

        lock (_lock)
        {
            if (_blobs.TryGetValue(blobName, out var record))
            {
                return new MemoryStream(record.Data);
            }
        }

        throw new KeyNotFoundException($"Object {objectId} not found in Azure WORM storage.");
    }

    /// <inheritdoc/>
    public override async Task<WormObjectStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default)
    {
        var blobName = BuildBlobName(objectId);

        lock (_lock)
        {
            if (_blobs.TryGetValue(blobName, out var record))
            {
                return new WormObjectStatus
                {
                    Exists = true,
                    ObjectId = objectId,
                    RetentionExpiry = record.PolicyExpiry,
                    LegalHolds = record.LegalHolds.Select(h => new LegalHold
                    {
                        HoldId = h.HoldId,
                        Reason = h.Reason,
                        PlacedAt = h.PlacedAt,
                        PlacedBy = h.PlacedBy
                    }).ToList(),
                    SizeBytes = record.SizeBytes,
                    WrittenAt = record.CreatedAt,
                    ContentHash = record.ContentHash,
                    HardwareImmutabilityEnabled = record.PolicyType == AzureImmutabilityPolicyType.Locked,
                    StorageKey = blobName
                };
            }
        }

        return new WormObjectStatus
        {
            Exists = false,
            ObjectId = objectId
        };
    }

    /// <inheritdoc/>
    public override async Task<bool> ExistsAsync(Guid objectId, CancellationToken ct = default)
    {
        var blobName = BuildBlobName(objectId);

        lock (_lock)
        {
            return _blobs.ContainsKey(blobName);
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<LegalHold>> GetLegalHoldsAsync(Guid objectId, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(objectId, ct);
        return status.LegalHolds;
    }

    /// <inheritdoc/>
    protected override async Task ExtendRetentionInternalAsync(
        Guid objectId,
        DateTimeOffset newExpiry,
        CancellationToken ct)
    {
        var blobName = BuildBlobName(objectId);

        _logger.LogInformation(
            "Extending retention for object {ObjectId} to {NewExpiry}",
            objectId, newExpiry);

        // In production:
        // var blobClient = new BlobClient(_config.ConnectionString, _config.ContainerName, blobName);
        // await blobClient.SetImmutabilityPolicyAsync(
        //     new BlobImmutabilityPolicy
        //     {
        //         ExpiresOn = newExpiry,
        //         PolicyMode = _blobs[blobName].PolicyType == AzureImmutabilityPolicyType.Locked
        //             ? BlobImmutabilityPolicyMode.Locked : BlobImmutabilityPolicyMode.Unlocked
        //     }, ct);

        lock (_lock)
        {
            if (_blobs.TryGetValue(blobName, out var record))
            {
                record.PolicyExpiry = newExpiry;
            }
            else
            {
                throw new KeyNotFoundException($"Object {objectId} not found in Azure WORM storage.");
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task PlaceLegalHoldInternalAsync(
        Guid objectId,
        string holdId,
        string reason,
        CancellationToken ct)
    {
        var blobName = BuildBlobName(objectId);

        _logger.LogInformation(
            "Placing legal hold {HoldId} on object {ObjectId}: {Reason}",
            holdId, objectId, reason);

        // In production:
        // var blobClient = new BlobClient(_config.ConnectionString, _config.ContainerName, blobName);
        // await blobClient.SetLegalHoldAsync(true, ct);

        lock (_lock)
        {
            if (_blobs.TryGetValue(blobName, out var record))
            {
                record.LegalHoldActive = true;
                record.LegalHolds.Add(new AzureLegalHoldRecord
                {
                    HoldId = holdId,
                    Reason = reason,
                    PlacedAt = DateTimeOffset.UtcNow,
                    PlacedBy = "system"
                });
            }
            else
            {
                throw new KeyNotFoundException($"Object {objectId} not found in Azure WORM storage.");
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task RemoveLegalHoldInternalAsync(
        Guid objectId,
        string holdId,
        CancellationToken ct)
    {
        var blobName = BuildBlobName(objectId);

        _logger.LogInformation(
            "Removing legal hold {HoldId} from object {ObjectId}",
            holdId, objectId);

        // In production:
        // Note: Azure's SetLegalHold is a single boolean flag, not per-hold.
        // For multiple holds, you need application-level tracking.
        // var blobClient = new BlobClient(_config.ConnectionString, _config.ContainerName, blobName);
        // Check if any holds remain before setting to false
        // await blobClient.SetLegalHoldAsync(false, ct);

        lock (_lock)
        {
            if (_blobs.TryGetValue(blobName, out var record))
            {
                record.LegalHolds.RemoveAll(h => h.HoldId == holdId);
                record.LegalHoldActive = record.LegalHolds.Count > 0;
            }
            else
            {
                throw new KeyNotFoundException($"Object {objectId} not found in Azure WORM storage.");
            }
        }
    }

    /// <summary>
    /// Locks an unlocked immutability policy, making it compliance-grade.
    /// Once locked, the policy cannot be shortened or removed.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task LockImmutabilityPolicyAsync(Guid objectId, CancellationToken ct = default)
    {
        var blobName = BuildBlobName(objectId);

        _logger.LogWarning(
            "Locking immutability policy for object {ObjectId} - this action is IRREVERSIBLE",
            objectId);

        // In production:
        // var blobClient = new BlobClient(_config.ConnectionString, _config.ContainerName, blobName);
        // var properties = await blobClient.GetPropertiesAsync(ct);
        // var currentPolicy = properties.Value.ImmutabilityPolicy;
        // await blobClient.SetImmutabilityPolicyAsync(
        //     new BlobImmutabilityPolicy
        //     {
        //         ExpiresOn = currentPolicy.ExpiresOn,
        //         PolicyMode = BlobImmutabilityPolicyMode.Locked
        //     }, ct);

        lock (_lock)
        {
            if (_blobs.TryGetValue(blobName, out var record))
            {
                if (record.PolicyType == AzureImmutabilityPolicyType.Locked)
                {
                    _logger.LogInformation("Policy for object {ObjectId} is already locked", objectId);
                    return;
                }

                record.PolicyType = AzureImmutabilityPolicyType.Locked;
                _logger.LogInformation("Successfully locked immutability policy for object {ObjectId}", objectId);
            }
            else
            {
                throw new KeyNotFoundException($"Object {objectId} not found in Azure WORM storage.");
            }
        }
    }

    /// <summary>
    /// Builds the blob name for a given object ID.
    /// </summary>
    private string BuildBlobName(Guid objectId)
    {
        // Use hierarchical path structure for better Azure Blob performance
        var idStr = objectId.ToString("N");
        return $"{_blobPrefix}{idStr[..2]}/{idStr[2..4]}/{idStr}";
    }

    /// <summary>
    /// Computes SHA-256 hash of content.
    /// </summary>
    private static string ComputeContentHash(byte[] data)
    {
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a version ID (simulating Azure blob versioning).
    /// </summary>
    private static string GenerateVersionId()
    {
        // Azure uses timestamp-based version IDs
        return DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["AzureContainer"] = _config.ContainerName;
        metadata["DefaultPolicyType"] = _config.DefaultPolicyType.ToString();
        metadata["DefaultRetention"] = _config.EffectiveDefaultRetention.ToString();
        metadata["VersionLevelImmutability"] = _config.EnableVersionLevelImmutability;
        metadata["ImmutabilityVerified"] = _immutabilityVerified;
        metadata["UseManagedIdentity"] = _config.UseManagedIdentity;
        return metadata;
    }

    /// <summary>
    /// Internal record for simulated Azure blob storage.
    /// </summary>
    private class AzureBlobRecord
    {
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

    /// <summary>
    /// Internal record for Azure legal hold tracking.
    /// Note: Azure's actual legal hold is a single flag; this provides application-level tracking.
    /// </summary>
    private class AzureLegalHoldRecord
    {
        public required string HoldId { get; init; }
        public required string Reason { get; init; }
        public required DateTimeOffset PlacedAt { get; init; }
        public required string PlacedBy { get; init; }
    }
}
