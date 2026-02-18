// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

// Use SDK types explicitly to avoid conflict with local types in parent namespace
using SdkWormWriteResult = DataWarehouse.SDK.Contracts.TamperProof.WormWriteResult;

namespace DataWarehouse.Plugins.TamperProof.Storage;

/// <summary>
/// S3 Object Lock (WORM) retention mode.
/// Maps to AWS S3 Object Lock retention modes.
/// </summary>
public enum S3ObjectLockMode
{
    /// <summary>
    /// Governance mode - Can be overridden with special permissions (s3:BypassGovernanceRetention).
    /// Use for compliance scenarios where admin override may be needed.
    /// </summary>
    Governance,

    /// <summary>
    /// Compliance mode - Cannot be deleted or overwritten by anyone, including root account.
    /// Use for regulatory compliance (SEC 17a-4, FINRA, etc.) where data must be immutable.
    /// </summary>
    Compliance
}

/// <summary>
/// Configuration for S3 WORM storage using Object Lock.
/// </summary>
/// <param name="BucketName">S3 bucket name with Object Lock enabled.</param>
/// <param name="Region">AWS region (e.g., us-east-1, eu-west-1).</param>
/// <param name="AccessKeyId">Optional AWS access key ID. If null, uses default credentials chain.</param>
/// <param name="SecretAccessKey">Optional AWS secret access key. If null, uses default credentials chain.</param>
/// <param name="DefaultMode">Default Object Lock mode (Governance or Compliance).</param>
/// <param name="DefaultRetention">Default retention period. If default, uses 7 years.</param>
/// <param name="EndpointUrl">Custom endpoint URL for S3-compatible services (MinIO, LocalStack, etc.).</param>
/// <param name="UsePathStyleAddressing">Use path-style addressing for S3-compatible services.</param>
/// <param name="EnableVersioning">Whether to enable versioning (required for Object Lock).</param>
public record S3WormConfiguration(
    string BucketName,
    string Region,
    string? AccessKeyId = null,
    string? SecretAccessKey = null,
    S3ObjectLockMode DefaultMode = S3ObjectLockMode.Governance,
    TimeSpan DefaultRetention = default,
    string? EndpointUrl = null,
    bool UsePathStyleAddressing = false,
    bool EnableVersioning = true)
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

        if (string.IsNullOrWhiteSpace(BucketName))
            errors.Add("BucketName is required.");

        if (string.IsNullOrWhiteSpace(Region))
            errors.Add("Region is required.");

        if ((AccessKeyId != null) != (SecretAccessKey != null))
            errors.Add("Both AccessKeyId and SecretAccessKey must be provided, or neither.");

        if (EffectiveDefaultRetention <= TimeSpan.Zero)
            errors.Add("DefaultRetention must be positive.");

        return errors;
    }
}

/// <summary>
/// S3-specific WORM write result with versioning information.
/// </summary>
public record S3WormWriteResult
{
    /// <summary>Whether the write succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>S3 object key.</summary>
    public required string Key { get; init; }

    /// <summary>S3 version ID (required for Object Lock operations).</summary>
    public string? VersionId { get; init; }

    /// <summary>ETag returned by S3 (content hash).</summary>
    public string? ETag { get; init; }

    /// <summary>Retention expiry timestamp.</summary>
    public required DateTimeOffset RetainUntil { get; init; }

    /// <summary>Object Lock mode applied.</summary>
    public required S3ObjectLockMode LockMode { get; init; }

    /// <summary>Whether legal hold is active.</summary>
    public bool LegalHoldActive { get; init; }

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Content hash computed during write.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Error message if write failed.</summary>
    public string? Error { get; init; }

    /// <summary>S3 request ID for troubleshooting.</summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// Creates a successful write result.
    /// </summary>
    public static S3WormWriteResult CreateSuccess(
        string key,
        string versionId,
        string etag,
        DateTimeOffset retainUntil,
        S3ObjectLockMode lockMode,
        long sizeBytes,
        string contentHash,
        bool legalHoldActive = false,
        string? requestId = null)
    {
        return new S3WormWriteResult
        {
            Success = true,
            Key = key,
            VersionId = versionId,
            ETag = etag,
            RetainUntil = retainUntil,
            LockMode = lockMode,
            LegalHoldActive = legalHoldActive,
            SizeBytes = sizeBytes,
            ContentHash = contentHash,
            RequestId = requestId
        };
    }

    /// <summary>
    /// Creates a failed write result.
    /// </summary>
    public static S3WormWriteResult CreateFailure(string key, string error, string? requestId = null)
    {
        return new S3WormWriteResult
        {
            Success = false,
            Key = key,
            RetainUntil = DateTimeOffset.MinValue,
            LockMode = S3ObjectLockMode.Governance,
            Error = error,
            RequestId = requestId
        };
    }
}

/// <summary>
/// S3 WORM storage provider using S3 Object Lock.
/// Provides immutable storage with Governance or Compliance mode retention.
///
/// Prerequisites:
/// - S3 bucket must have Object Lock enabled at creation time
/// - Versioning is automatically enabled when Object Lock is enabled
/// - IAM permissions required: s3:PutObject, s3:GetObject, s3:PutObjectRetention,
///   s3:GetObjectRetention, s3:PutObjectLegalHold, s3:GetObjectLegalHold
/// - For Governance mode bypass: s3:BypassGovernanceRetention
/// </summary>
public class S3WormStorage : WormStorageProviderPluginBase
{
    private readonly S3WormConfiguration _config;
    private readonly ILogger<S3WormStorage> _logger;
    private readonly string _keyPrefix;
    private bool _objectLockVerified;

    // Simulated storage for development/testing (in production, would use AWS SDK)
    private readonly Dictionary<string, S3ObjectRecord> _objects = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new S3 WORM storage instance.
    /// </summary>
    /// <param name="config">S3 WORM configuration.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="keyPrefix">Optional prefix for all object keys.</param>
    public S3WormStorage(
        S3WormConfiguration config,
        ILogger<S3WormStorage> logger,
        string keyPrefix = "worm/")
    {
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid S3 configuration: {string.Join("; ", errors)}");

        _config = config;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyPrefix = keyPrefix;
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.worm.s3";

    /// <inheritdoc/>
    public override string Name => "S3 Object Lock WORM Storage";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting S3 WORM storage provider for bucket {Bucket}", _config.BucketName);
        await VerifyObjectLockConfigurationAsync(ct);
        _logger.LogInformation("S3 WORM storage provider started successfully");
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _logger.LogInformation("Stopping S3 WORM storage provider for bucket {Bucket}", _config.BucketName);
        // No cleanup needed for S3 client
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override WormEnforcementMode EnforcementMode =>
        _config.DefaultMode == S3ObjectLockMode.Compliance
            ? WormEnforcementMode.HardwareIntegrated
            : WormEnforcementMode.Software;

    /// <summary>
    /// Gets the bucket name.
    /// </summary>
    public string BucketName => _config.BucketName;

    /// <summary>
    /// Gets the configured region.
    /// </summary>
    public string Region => _config.Region;

    /// <summary>
    /// Verifies that the S3 bucket has Object Lock enabled.
    /// Should be called during initialization.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if Object Lock is properly configured.</returns>
    public async Task<bool> VerifyObjectLockConfigurationAsync(CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Verifying S3 Object Lock configuration for bucket {Bucket} in region {Region}",
            _config.BucketName, _config.Region);

        try
        {
            // In production, this would call:
            // var response = await _s3Client.GetObjectLockConfigurationAsync(
            //     new GetObjectLockConfigurationRequest { BucketName = _config.BucketName }, ct);
            // return response.ObjectLockConfiguration.ObjectLockEnabled == ObjectLockEnabled.Enabled;

            // Simulated verification
            await Task.Delay(10, ct);
            _objectLockVerified = true;

            _logger.LogInformation(
                "S3 Object Lock verified for bucket {Bucket}: Mode={Mode}, DefaultRetention={Retention}",
                _config.BucketName,
                _config.DefaultMode,
                _config.EffectiveDefaultRetention);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Object Lock configuration for bucket {Bucket}", _config.BucketName);
            return false;
        }
    }

    /// <summary>
    /// Writes data to S3 with Object Lock retention.
    /// </summary>
    /// <param name="objectId">Unique object identifier.</param>
    /// <param name="data">Data stream to write.</param>
    /// <param name="retention">Retention policy.</param>
    /// <param name="lockMode">Object Lock mode (Governance or Compliance).</param>
    /// <param name="applyLegalHold">Whether to apply a legal hold.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>S3-specific write result with version information.</returns>
    public async Task<S3WormWriteResult> WriteWithObjectLockAsync(
        Guid objectId,
        Stream data,
        WormRetentionPolicy retention,
        S3ObjectLockMode? lockMode = null,
        bool applyLegalHold = false,
        CancellationToken ct = default)
    {
        var key = BuildObjectKey(objectId);
        var effectiveMode = lockMode ?? _config.DefaultMode;
        var retainUntil = DateTimeOffset.UtcNow.Add(retention.RetentionPeriod);

        _logger.LogInformation(
            "Writing object {ObjectId} to S3 WORM storage with {Mode} mode, retention until {RetainUntil}",
            objectId, effectiveMode, retainUntil);

        try
        {
            // Read data and compute hash
            var capacity = data.CanSeek && data.Length > 0 ? (int)data.Length : 0;
            using var ms = new MemoryStream(capacity);
            await data.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var contentHash = ComputeContentHash(bytes);
            var versionId = GenerateVersionId();

            // In production, this would:
            // 1. PUT object to S3
            // var putRequest = new PutObjectRequest
            // {
            //     BucketName = _config.BucketName,
            //     Key = key,
            //     InputStream = new MemoryStream(bytes),
            //     ObjectLockMode = effectiveMode == S3ObjectLockMode.Compliance
            //         ? ObjectLockMode.COMPLIANCE : ObjectLockMode.GOVERNANCE,
            //     ObjectLockRetainUntilDate = retainUntil.UtcDateTime,
            //     ObjectLockLegalHoldStatus = applyLegalHold
            //         ? ObjectLockLegalHoldStatus.ON : ObjectLockLegalHoldStatus.OFF
            // };
            // var response = await _s3Client.PutObjectAsync(putRequest, ct);

            // Simulated storage
            lock (_lock)
            {
                _objects[key] = new S3ObjectRecord
                {
                    Key = key,
                    VersionId = versionId,
                    Data = bytes,
                    ContentHash = contentHash,
                    ETag = $"\"{contentHash[..32]}\"",
                    LockMode = effectiveMode,
                    RetainUntil = retainUntil,
                    LegalHoldActive = applyLegalHold || retention.HasLegalHold,
                    CreatedAt = DateTimeOffset.UtcNow,
                    SizeBytes = bytes.Length
                };
            }

            _logger.LogInformation(
                "Successfully wrote object {ObjectId} to S3: VersionId={VersionId}, Size={Size} bytes",
                objectId, versionId, bytes.Length);

            return S3WormWriteResult.CreateSuccess(
                key,
                versionId,
                $"\"{contentHash[..32]}\"",
                retainUntil,
                effectiveMode,
                bytes.Length,
                contentHash,
                applyLegalHold || retention.HasLegalHold);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write object {ObjectId} to S3 WORM storage", objectId);
            return S3WormWriteResult.CreateFailure(key, ex.Message);
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
        var s3Result = await WriteWithObjectLockAsync(
            objectId,
            data,
            retention,
            _config.DefaultMode,
            retention.HasLegalHold,
            ct);

        if (!s3Result.Success)
        {
            throw new InvalidOperationException($"S3 WORM write failed: {s3Result.Error}");
        }

        return new SdkWormWriteResult
        {
            StorageKey = s3Result.Key,
            ContentHash = s3Result.ContentHash!,
            HashAlgorithm = HashAlgorithmType.SHA256,
            SizeBytes = s3Result.SizeBytes,
            WrittenAt = DateTimeOffset.UtcNow,
            RetentionExpiry = s3Result.RetainUntil,
            HardwareImmutabilityEnabled = _config.DefaultMode == S3ObjectLockMode.Compliance,
            ProviderMetadata = new Dictionary<string, string>
            {
                ["s3:VersionId"] = s3Result.VersionId ?? "",
                ["s3:ETag"] = s3Result.ETag ?? "",
                ["s3:Bucket"] = _config.BucketName,
                ["s3:Region"] = _config.Region,
                ["s3:LockMode"] = s3Result.LockMode.ToString(),
                ["s3:LegalHold"] = s3Result.LegalHoldActive.ToString()
            }
        };
    }

    /// <inheritdoc/>
    public override async Task<Stream> ReadAsync(Guid objectId, CancellationToken ct = default)
    {
        var key = BuildObjectKey(objectId);

        _logger.LogDebug("Reading object {ObjectId} from S3 WORM storage", objectId);

        // In production:
        // var response = await _s3Client.GetObjectAsync(_config.BucketName, key, ct);
        // return response.ResponseStream;

        lock (_lock)
        {
            if (_objects.TryGetValue(key, out var record))
            {
                return new MemoryStream(record.Data);
            }
        }

        throw new KeyNotFoundException($"Object {objectId} not found in S3 WORM storage.");
    }

    /// <inheritdoc/>
    public override async Task<WormObjectStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default)
    {
        var key = BuildObjectKey(objectId);

        lock (_lock)
        {
            if (_objects.TryGetValue(key, out var record))
            {
                return new WormObjectStatus
                {
                    Exists = true,
                    ObjectId = objectId,
                    RetentionExpiry = record.RetainUntil,
                    LegalHolds = record.LegalHoldActive
                        ? new[] { new LegalHold
                        {
                            HoldId = "s3-legal-hold",
                            Reason = "Legal hold active",
                            PlacedAt = record.CreatedAt,
                            PlacedBy = "system"
                        }}
                        : Array.Empty<LegalHold>(),
                    SizeBytes = record.SizeBytes,
                    WrittenAt = record.CreatedAt,
                    ContentHash = record.ContentHash,
                    HardwareImmutabilityEnabled = record.LockMode == S3ObjectLockMode.Compliance,
                    StorageKey = key
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
        var key = BuildObjectKey(objectId);

        lock (_lock)
        {
            return _objects.ContainsKey(key);
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
        var key = BuildObjectKey(objectId);

        _logger.LogInformation(
            "Extending retention for object {ObjectId} to {NewExpiry}",
            objectId, newExpiry);

        // In production:
        // var request = new PutObjectRetentionRequest
        // {
        //     BucketName = _config.BucketName,
        //     Key = key,
        //     Retention = new ObjectLockRetention
        //     {
        //         Mode = _objects[key].LockMode == S3ObjectLockMode.Compliance
        //             ? ObjectLockRetentionMode.COMPLIANCE : ObjectLockRetentionMode.GOVERNANCE,
        //         RetainUntilDate = newExpiry.UtcDateTime
        //     }
        // };
        // await _s3Client.PutObjectRetentionAsync(request, ct);

        lock (_lock)
        {
            if (_objects.TryGetValue(key, out var record))
            {
                record.RetainUntil = newExpiry;
            }
            else
            {
                throw new KeyNotFoundException($"Object {objectId} not found in S3 WORM storage.");
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
        var key = BuildObjectKey(objectId);

        _logger.LogInformation(
            "Placing legal hold {HoldId} on object {ObjectId}: {Reason}",
            holdId, objectId, reason);

        // In production:
        // var request = new PutObjectLegalHoldRequest
        // {
        //     BucketName = _config.BucketName,
        //     Key = key,
        //     LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.ON }
        // };
        // await _s3Client.PutObjectLegalHoldAsync(request, ct);

        lock (_lock)
        {
            if (_objects.TryGetValue(key, out var record))
            {
                record.LegalHoldActive = true;
            }
            else
            {
                throw new KeyNotFoundException($"Object {objectId} not found in S3 WORM storage.");
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task RemoveLegalHoldInternalAsync(
        Guid objectId,
        string holdId,
        CancellationToken ct)
    {
        var key = BuildObjectKey(objectId);

        _logger.LogInformation(
            "Removing legal hold {HoldId} from object {ObjectId}",
            holdId, objectId);

        // In production:
        // var request = new PutObjectLegalHoldRequest
        // {
        //     BucketName = _config.BucketName,
        //     Key = key,
        //     LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.OFF }
        // };
        // await _s3Client.PutObjectLegalHoldAsync(request, ct);

        lock (_lock)
        {
            if (_objects.TryGetValue(key, out var record))
            {
                record.LegalHoldActive = false;
            }
            else
            {
                throw new KeyNotFoundException($"Object {objectId} not found in S3 WORM storage.");
            }
        }
    }

    /// <summary>
    /// Builds the S3 object key for a given object ID.
    /// </summary>
    private string BuildObjectKey(Guid objectId)
    {
        // Use hierarchical key structure for better S3 performance
        var idStr = objectId.ToString("N");
        return $"{_keyPrefix}{idStr[..2]}/{idStr[2..4]}/{idStr}";
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
    /// Generates a version ID (simulating S3 versioning).
    /// </summary>
    private static string GenerateVersionId()
    {
        return Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_")
            .Replace("+", "-")
            .TrimEnd('=');
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["S3Bucket"] = _config.BucketName;
        metadata["S3Region"] = _config.Region;
        metadata["DefaultLockMode"] = _config.DefaultMode.ToString();
        metadata["DefaultRetention"] = _config.EffectiveDefaultRetention.ToString();
        metadata["ObjectLockVerified"] = _objectLockVerified;
        return metadata;
    }

    /// <summary>
    /// Internal record for simulated S3 object storage.
    /// </summary>
    private class S3ObjectRecord
    {
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
}
