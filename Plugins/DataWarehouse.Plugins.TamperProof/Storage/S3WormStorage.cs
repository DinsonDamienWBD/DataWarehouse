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
    // Assigned when AWS SDK integration verifies Object Lock configuration on the bucket.
    // Currently always false since all methods throw PlatformNotSupportedException until AWS SDK is configured.
#pragma warning disable CS0649 // Field is assigned when AWS SDK integration is implemented
    private bool _objectLockVerified;
#pragma warning restore CS0649

    private const string S3NotConfiguredMessage =
        "S3 WORM storage requires AWS SDK configuration. " +
        "Install AWSSDK.S3 NuGet package and provide IAM credentials with s3:PutObject, " +
        "s3:GetObject, s3:PutObjectRetention, s3:GetObjectRetention, " +
        "s3:PutObjectLegalHold, s3:GetObjectLegalHold permissions. " +
        "The target bucket must have Object Lock enabled at creation time.";

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
    public Task<bool> VerifyObjectLockConfigurationAsync(CancellationToken ct = default)
    {
        // Real implementation requires AWS SDK S3 client:
        // var response = await _s3Client.GetObjectLockConfigurationAsync(
        //     new GetObjectLockConfigurationRequest { BucketName = _config.BucketName }, ct);
        // _objectLockVerified = response.ObjectLockConfiguration.ObjectLockEnabled == ObjectLockEnabled.Enabled;
        // return _objectLockVerified;
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
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
    public Task<S3WormWriteResult> WriteWithObjectLockAsync(
        Guid objectId,
        Stream data,
        WormRetentionPolicy retention,
        S3ObjectLockMode? lockMode = null,
        bool applyLegalHold = false,
        CancellationToken ct = default)
    {
        // Real implementation requires AWS SDK S3 client with PutObjectAsync + Object Lock headers.
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
    }

    /// <inheritdoc/>
    protected override Task<SdkWormWriteResult> WriteInternalAsync(
        Guid objectId,
        Stream data,
        WormRetentionPolicy retention,
        WriteContext context,
        CancellationToken ct)
    {
        // Real implementation delegates to WriteWithObjectLockAsync which calls AWS SDK PutObjectAsync.
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
    }

    /// <inheritdoc/>
    public override Task<Stream> ReadAsync(Guid objectId, CancellationToken ct = default)
    {
        // Real implementation: await _s3Client.GetObjectAsync(_config.BucketName, key, ct)
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
    }

    /// <inheritdoc/>
    public override Task<WormObjectStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default)
    {
        // Real implementation: GetObjectRetentionAsync + GetObjectLegalHoldAsync + HeadObjectAsync
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
    }

    /// <inheritdoc/>
    public override Task<bool> ExistsAsync(Guid objectId, CancellationToken ct = default)
    {
        // Real implementation: HeadObjectAsync to check existence
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
    }

    /// <inheritdoc/>
    public override Task<IReadOnlyList<LegalHold>> GetLegalHoldsAsync(Guid objectId, CancellationToken ct = default)
    {
        // Real implementation: GetObjectLegalHoldAsync
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
    }

    /// <inheritdoc/>
    protected override Task ExtendRetentionInternalAsync(
        Guid objectId,
        DateTimeOffset newExpiry,
        CancellationToken ct)
    {
        // Real implementation: PutObjectRetentionAsync with new RetainUntilDate
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
    }

    /// <inheritdoc/>
    protected override Task PlaceLegalHoldInternalAsync(
        Guid objectId,
        string holdId,
        string reason,
        CancellationToken ct)
    {
        // Real implementation: PutObjectLegalHoldAsync with Status = ON
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
    }

    /// <inheritdoc/>
    protected override Task RemoveLegalHoldInternalAsync(
        Guid objectId,
        string holdId,
        CancellationToken ct)
    {
        // Real implementation: PutObjectLegalHoldAsync with Status = OFF
        throw new PlatformNotSupportedException(S3NotConfiguredMessage);
    }

    /// <summary>
    /// Builds the S3 object key for a given object ID.
    /// Uses hierarchical key structure for better S3 performance.
    /// </summary>
    private string BuildObjectKey(Guid objectId)
    {
        var idStr = objectId.ToString("N");
        return $"{_keyPrefix}{idStr[..2]}/{idStr[2..4]}/{idStr}";
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
}
