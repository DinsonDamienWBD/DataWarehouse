using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AirGapBridge.Core;

#region Enums

/// <summary>
/// Mode of operation for air-gap bridge devices.
/// </summary>
public enum AirGapMode
{
    /// <summary>Transport mode - encrypted blob container for sneakernet transfer.</summary>
    Transport,

    /// <summary>Storage extension mode - acts as a capacity tier.</summary>
    StorageExtension,

    /// <summary>Pocket instance mode - full portable DataWarehouse.</summary>
    PocketInstance
}

/// <summary>
/// Type of removable media detected.
/// </summary>
public enum MediaType
{
    /// <summary>USB flash drive or external drive.</summary>
    Usb,

    /// <summary>SD card or other memory card.</summary>
    SdCard,

    /// <summary>NVMe external drive.</summary>
    NvMe,

    /// <summary>SATA external drive.</summary>
    Sata,

    /// <summary>Network attached storage configured as air-gap.</summary>
    NetworkDrive,

    /// <summary>Optical drive (CD, DVD, Blu-ray).</summary>
    Optical,

    /// <summary>Unknown media type.</summary>
    Unknown
}

/// <summary>
/// Security level for air-gap authentication.
/// </summary>
public enum SecurityLevel
{
    /// <summary>No authentication required.</summary>
    None,

    /// <summary>PIN or password authentication.</summary>
    Password,

    /// <summary>Keyfile-based authentication.</summary>
    Keyfile,

    /// <summary>Hardware key (YubiKey/FIDO2) authentication.</summary>
    HardwareKey,

    /// <summary>Multi-factor authentication.</summary>
    MultiFactorAuth
}

/// <summary>
/// Encryption mode for air-gap storage.
/// </summary>
public enum EncryptionMode
{
    /// <summary>No encryption.</summary>
    None,

    /// <summary>Internal encryption at rest.</summary>
    InternalAes256,

    /// <summary>BitLocker encryption (Windows).</summary>
    BitLocker,

    /// <summary>LUKS encryption (Linux).</summary>
    Luks,

    /// <summary>FileVault encryption (macOS).</summary>
    FileVault
}

/// <summary>
/// Status of an air-gap device.
/// </summary>
public enum DeviceStatus
{
    /// <summary>Device not connected.</summary>
    Disconnected,

    /// <summary>Device connected but not authenticated.</summary>
    Pending,

    /// <summary>Device authenticated and ready.</summary>
    Ready,

    /// <summary>Device is busy with an operation.</summary>
    Busy,

    /// <summary>Device encountered an error.</summary>
    Error,

    /// <summary>Device is locked (TTL expired or authentication failed).</summary>
    Locked
}

/// <summary>
/// Sync operation status.
/// </summary>
public enum SyncStatus
{
    /// <summary>Not started.</summary>
    NotStarted,

    /// <summary>In progress.</summary>
    InProgress,

    /// <summary>Completed successfully.</summary>
    Completed,

    /// <summary>Paused.</summary>
    Paused,

    /// <summary>Failed.</summary>
    Failed,

    /// <summary>Cancelled.</summary>
    Cancelled
}

/// <summary>
/// Type of sync operation.
/// </summary>
public enum SyncOperationType
{
    /// <summary>Import data from device.</summary>
    Import,

    /// <summary>Export data to device.</summary>
    Export,

    /// <summary>Bidirectional sync.</summary>
    Bidirectional,

    /// <summary>Merge with conflict resolution.</summary>
    Merge
}

#endregion

#region Configuration Types

/// <summary>
/// Configuration for an air-gap device stored in .dw-config file.
/// </summary>
public sealed class AirGapDeviceConfig
{
    /// <summary>Version of the config format.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Unique device identifier.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Device display name.</summary>
    public required string Name { get; init; }

    /// <summary>Mode of operation.</summary>
    public AirGapMode Mode { get; init; }

    /// <summary>Security level.</summary>
    public SecurityLevel Security { get; init; }

    /// <summary>Encryption mode.</summary>
    public EncryptionMode Encryption { get; init; }

    /// <summary>Device creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Device owner instance ID.</summary>
    public string? OwnerInstanceId { get; init; }

    /// <summary>Trusted instance IDs for auto-mount.</summary>
    public List<string> TrustedInstances { get; init; } = new();

    /// <summary>Time-to-live in days (0 = unlimited).</summary>
    public int TtlDays { get; init; }

    /// <summary>Last authentication timestamp.</summary>
    public DateTimeOffset? LastAuthAt { get; set; }

    /// <summary>Cryptographic signature of the config.</summary>
    public string? Signature { get; set; }

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>
    /// Validates the configuration signature.
    /// </summary>
    public bool ValidateSignature(byte[] publicKey)
    {
        if (string.IsNullOrEmpty(Signature)) return false;

        try
        {
            // Signature verification computed inline; bus delegation to UltimateEncryption available for centralized policy enforcement
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);

            var configBytes = GetSignableBytes();
            var signatureBytes = Convert.FromBase64String(Signature);

            return ecdsa.VerifyData(configBytes, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Signs the configuration.
    /// </summary>
    public void Sign(byte[] privateKey)
    {
        // Signing computed inline; bus delegation to UltimateEncryption available for centralized policy enforcement
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(privateKey, out _);

        var configBytes = GetSignableBytes();
        var signature = ecdsa.SignData(configBytes, HashAlgorithmName.SHA256);

        Signature = Convert.ToBase64String(signature);
    }

    private byte[] GetSignableBytes()
    {
        var json = JsonSerializer.Serialize(new
        {
            Version,
            DeviceId,
            Name,
            Mode,
            Security,
            Encryption,
            CreatedAt,
            OwnerInstanceId,
            TrustedInstances,
            TtlDays
        });
        return Encoding.UTF8.GetBytes(json);
    }
}

/// <summary>
/// Security policy for air-gap devices.
/// </summary>
public sealed class AirGapSecurityPolicy
{
    /// <summary>Required security level.</summary>
    public SecurityLevel MinSecurityLevel { get; init; } = SecurityLevel.Password;

    /// <summary>Required encryption mode.</summary>
    public EncryptionMode RequiredEncryption { get; init; } = EncryptionMode.InternalAes256;

    /// <summary>Maximum TTL in days (0 = unlimited).</summary>
    public int MaxTtlDays { get; init; } = 30;

    /// <summary>Allow auto-mount from trusted instances.</summary>
    public bool AllowAutoMount { get; init; } = true;

    /// <summary>Require signature verification.</summary>
    public bool RequireSignature { get; init; } = true;

    /// <summary>Wipe data after failed auth attempts.</summary>
    public int WipeAfterFailedAttempts { get; init; } = 5;

    /// <summary>Allow network-attached air-gap storage.</summary>
    public bool AllowNetworkStorage { get; init; }

    /// <summary>Audit all operations.</summary>
    public bool AuditEnabled { get; init; } = true;
}

#endregion

#region Data Transfer Types

/// <summary>
/// A DataWarehouse package (.dwpack) for encrypted transport.
/// </summary>
public sealed class DwPackage
{
    /// <summary>Package format version.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Unique package identifier.</summary>
    public required string PackageId { get; init; }

    /// <summary>Source instance identifier.</summary>
    public required string SourceInstanceId { get; init; }

    /// <summary>Target instance identifier (if specific).</summary>
    public string? TargetInstanceId { get; init; }

    /// <summary>Package creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Package manifest.</summary>
    public required PackageManifest Manifest { get; init; }

    /// <summary>Encrypted shards.</summary>
    public List<EncryptedShard> Shards { get; init; } = new();

    /// <summary>Processing manifest (for EHT convergence).</summary>
    public ProcessingManifest? ProcessingInfo { get; set; }

    /// <summary>Package signature.</summary>
    public string? Signature { get; set; }

    /// <summary>
    /// Serializes the package to bytes.
    /// </summary>
    public byte[] ToBytes()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }

    /// <summary>
    /// Deserializes a package from bytes.
    /// </summary>
    public static DwPackage FromBytes(byte[] data)
    {
        return JsonSerializer.Deserialize<DwPackage>(data)
            ?? throw new InvalidOperationException("Failed to deserialize DwPackage");
    }
}

/// <summary>
/// Manifest for a DwPackage.
/// </summary>
public sealed class PackageManifest
{
    /// <summary>Total number of shards.</summary>
    public int ShardCount { get; init; }

    /// <summary>Total data size before encryption.</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>Encryption algorithm used.</summary>
    public string EncryptionAlgorithm { get; init; } = "AES-256-GCM";

    /// <summary>Key derivation function.</summary>
    public string KeyDerivation { get; init; } = "Argon2id";

    /// <summary>Hash of all shard hashes (merkle root).</summary>
    public string? MerkleRoot { get; init; }

    /// <summary>Blob URIs included in the package.</summary>
    public List<string> BlobUris { get; init; } = new();

    /// <summary>Custom tags.</summary>
    public List<string> Tags { get; init; } = new();

    /// <summary>Is this an auto-ingest package.</summary>
    public bool AutoIngest { get; init; }

    /// <summary>
    /// Validates the package manifest for production readiness.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when manifest is invalid.</exception>
    public void Validate()
    {
        // Validate shard count
        if (ShardCount <= 0)
            throw new InvalidOperationException("ShardCount must be positive");

        // Validate total size (1MB minimum, 1TB maximum)
        if (TotalSizeBytes < 1024 * 1024)
            throw new InvalidOperationException("TotalSizeBytes must be at least 1MB");
        if (TotalSizeBytes > 1024L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("TotalSizeBytes cannot exceed 1TB");

        // Validate encryption algorithm
        var validAlgorithms = new[] { "AES-256-GCM", "AES-256-CBC", "ChaCha20-Poly1305" };
        if (!validAlgorithms.Contains(EncryptionAlgorithm))
            throw new InvalidOperationException($"EncryptionAlgorithm must be one of: {string.Join(", ", validAlgorithms)}");

        // Validate key derivation function
        var validKdfs = new[] { "Argon2id", "Argon2i", "PBKDF2", "scrypt" };
        if (!validKdfs.Contains(KeyDerivation))
            throw new InvalidOperationException($"KeyDerivation must be one of: {string.Join(", ", validKdfs)}");

        // Validate blob URIs
        if (BlobUris.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("All BlobUris must be non-empty");

        // Edge case: Empty transfer set
        if (BlobUris.Count == 0)
            throw new InvalidOperationException("Manifest must contain at least one blob URI");

        // Edge case: Single-file transfer optimization check
        if (ShardCount == 1 && BlobUris.Count > 1)
            throw new InvalidOperationException("Single shard transfer must have exactly one blob URI");

        // Edge case: Manifest size limit validation (prevent memory exhaustion)
        if (BlobUris.Count > 100000)
            throw new InvalidOperationException("Manifest cannot contain more than 100,000 blob URIs");
    }
}

/// <summary>
/// An encrypted shard within a package.
/// </summary>
public sealed class EncryptedShard
{
    /// <summary>Shard index.</summary>
    public int Index { get; init; }

    /// <summary>Original blob URI.</summary>
    public required string BlobUri { get; init; }

    /// <summary>Encrypted data.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Encryption nonce.</summary>
    public required byte[] Nonce { get; init; }

    /// <summary>Authentication tag.</summary>
    public required byte[] Tag { get; init; }

    /// <summary>Hash of the encrypted data.</summary>
    public required string Hash { get; init; }

    /// <summary>Original size before encryption.</summary>
    public long OriginalSize { get; init; }
}

/// <summary>
/// Processing manifest for EHT (Eventual Hierarchical Tree) convergence support.
/// </summary>
public sealed class ProcessingManifest
{
    /// <summary>Instance that performed processing.</summary>
    public required string ProcessingInstanceId { get; init; }

    /// <summary>Processing timestamp.</summary>
    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Operations performed locally.</summary>
    public List<ProcessingOperation> LocalOperations { get; init; } = new();

    /// <summary>Operations deferred for later processing.</summary>
    public List<DeferredOperation> DeferredOperations { get; init; } = new();

    /// <summary>Schema version at time of processing.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Data statistics.</summary>
    public DataStatistics Statistics { get; init; } = new();
}

/// <summary>
/// A processing operation record.
/// </summary>
public sealed class ProcessingOperation
{
    /// <summary>Operation ID.</summary>
    public required string OperationId { get; init; }

    /// <summary>Operation type.</summary>
    public required string Type { get; init; }

    /// <summary>Target blob URI.</summary>
    public required string TargetUri { get; init; }

    /// <summary>Operation timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Checksum of result.</summary>
    public string? ResultChecksum { get; init; }
}

/// <summary>
/// A deferred operation for later processing.
/// </summary>
public sealed class DeferredOperation
{
    /// <summary>Operation ID.</summary>
    public required string OperationId { get; init; }

    /// <summary>Operation type.</summary>
    public required string Type { get; init; }

    /// <summary>Target blob URI.</summary>
    public required string TargetUri { get; init; }

    /// <summary>Reason for deferral.</summary>
    public required string DeferralReason { get; init; }

    /// <summary>Priority level.</summary>
    public int Priority { get; init; }

    /// <summary>Expiration time (if any).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Data statistics for convergence.
/// </summary>
public sealed class DataStatistics
{
    /// <summary>Total blob count.</summary>
    public long BlobCount { get; init; }

    /// <summary>Total size in bytes.</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>Latest modification timestamp.</summary>
    public DateTimeOffset? LatestModification { get; init; }

    /// <summary>Schema hash.</summary>
    public string? SchemaHash { get; init; }

    /// <summary>Index entry count.</summary>
    public long IndexEntryCount { get; init; }
}

#endregion

#region Event Types

/// <summary>
/// Event raised when an air-gap device is detected.
/// </summary>
public sealed class DeviceDetectedEvent
{
    /// <summary>Device identifier.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Device path.</summary>
    public required string DevicePath { get; init; }

    /// <summary>Media type.</summary>
    public MediaType MediaType { get; init; }

    /// <summary>Device mode (if configured).</summary>
    public AirGapMode? Mode { get; init; }

    /// <summary>Device label.</summary>
    public string? Label { get; init; }

    /// <summary>Detection timestamp.</summary>
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Event raised when an instance is detected (for convergence).
/// </summary>
public sealed class InstanceDetectedEvent
{
    /// <summary>Unique event ID.</summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Instance name.</summary>
    public required string InstanceName { get; init; }

    /// <summary>Device path where instance was found.</summary>
    public required string DevicePath { get; init; }

    /// <summary>Instance version.</summary>
    public required string Version { get; init; }

    /// <summary>Instance metadata.</summary>
    public InstanceMetadata Metadata { get; init; } = new();

    /// <summary>Detection timestamp.</summary>
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Whether compatibility has been verified.</summary>
    public bool CompatibilityVerified { get; set; }

    /// <summary>Compatibility issues if any.</summary>
    public List<string> CompatibilityIssues { get; init; } = new();
}

/// <summary>
/// Metadata extracted from a detected instance.
/// </summary>
public sealed class InstanceMetadata
{
    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Data statistics.</summary>
    public DataStatistics Statistics { get; init; } = new();

    /// <summary>Last sync timestamp.</summary>
    public DateTimeOffset? LastSyncAt { get; init; }

    /// <summary>Parent instance ID (if forked).</summary>
    public string? ParentInstanceId { get; init; }

    /// <summary>Configuration hash.</summary>
    public string? ConfigHash { get; init; }

    /// <summary>Plugin versions.</summary>
    public Dictionary<string, string> PluginVersions { get; init; } = new();
}

/// <summary>
/// Event raised when a device is removed.
/// </summary>
public sealed class DeviceRemovedEvent
{
    /// <summary>Device identifier.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Removal timestamp.</summary>
    public DateTimeOffset RemovedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Was the removal safe (graceful unmount).</summary>
    public bool SafeRemoval { get; init; }
}

/// <summary>
/// Event raised when a sync operation completes.
/// </summary>
public sealed class SyncCompletedEvent
{
    /// <summary>Sync operation ID.</summary>
    public required string SyncId { get; init; }

    /// <summary>Device ID.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Operation type.</summary>
    public SyncOperationType OperationType { get; init; }

    /// <summary>Status.</summary>
    public SyncStatus Status { get; init; }

    /// <summary>Items transferred.</summary>
    public int ItemsTransferred { get; init; }

    /// <summary>Bytes transferred.</summary>
    public long BytesTransferred { get; init; }

    /// <summary>Conflicts encountered.</summary>
    public int ConflictsCount { get; init; }

    /// <summary>Duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }
}

#endregion

#region Result Types

/// <summary>
/// Result of importing a package.
/// </summary>
public sealed class ImportResult
{
    /// <summary>Whether import succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Package ID.</summary>
    public required string PackageId { get; init; }

    /// <summary>Shards imported.</summary>
    public int ShardsImported { get; init; }

    /// <summary>Shards skipped (duplicates).</summary>
    public int ShardsSkipped { get; init; }

    /// <summary>Total bytes imported.</summary>
    public long BytesImported { get; init; }

    /// <summary>Duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Warnings.</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Result of a secure wipe operation.
/// </summary>
public sealed class SecureWipeResult
{
    /// <summary>Whether wipe succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Bytes wiped.</summary>
    public long BytesWiped { get; init; }

    /// <summary>Wipe passes performed.</summary>
    public int WipePasses { get; init; }

    /// <summary>Duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Verification passed.</summary>
    public bool Verified { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of device authentication.
/// </summary>
public sealed class AuthenticationResult
{
    /// <summary>Whether authentication succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Authentication method used.</summary>
    public SecurityLevel Method { get; init; }

    /// <summary>Session token (if applicable).</summary>
    public string? SessionToken { get; init; }

    /// <summary>Session expiration.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Remaining authentication attempts.</summary>
    public int RemainingAttempts { get; init; }
}

/// <summary>
/// Result log entry written to USB for sender feedback.
/// </summary>
public sealed class ResultLogEntry
{
    /// <summary>Entry timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Package ID processed.</summary>
    public required string PackageId { get; init; }

    /// <summary>Operation result.</summary>
    public required string Result { get; init; }

    /// <summary>Target instance ID.</summary>
    public required string TargetInstanceId { get; init; }

    /// <summary>Items processed.</summary>
    public int ItemsProcessed { get; init; }

    /// <summary>Bytes processed.</summary>
    public long BytesProcessed { get; init; }

    /// <summary>Warnings.</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>Errors.</summary>
    public List<string> Errors { get; init; } = new();
}

#endregion

#region Hardware Detection Types

/// <summary>
/// Information about a detected drive.
/// </summary>
public sealed class DriveInfo
{
    /// <summary>Drive path (e.g., "E:\", "/media/usb").</summary>
    public required string Path { get; init; }

    /// <summary>Drive label.</summary>
    public string? Label { get; init; }

    /// <summary>Media type.</summary>
    public MediaType MediaType { get; init; }

    /// <summary>Total capacity in bytes.</summary>
    public long TotalCapacity { get; init; }

    /// <summary>Available space in bytes.</summary>
    public long AvailableSpace { get; init; }

    /// <summary>File system type.</summary>
    public string? FileSystem { get; init; }

    /// <summary>Serial number.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Vendor name.</summary>
    public string? Vendor { get; init; }

    /// <summary>Product name.</summary>
    public string? Product { get; init; }

    /// <summary>Is removable.</summary>
    public bool IsRemovable { get; init; }

    /// <summary>Is write protected.</summary>
    public bool IsWriteProtected { get; init; }
}

/// <summary>
/// Platform-specific hardware detection information.
/// </summary>
public sealed class HardwareDetectionInfo
{
    /// <summary>Platform (Windows, Linux, macOS).</summary>
    public required string Platform { get; init; }

    /// <summary>Detection method used.</summary>
    public required string DetectionMethod { get; init; }

    /// <summary>Is monitoring active.</summary>
    public bool IsMonitoring { get; set; }

    /// <summary>Last scan timestamp.</summary>
    public DateTimeOffset? LastScan { get; set; }

    /// <summary>Detected drives.</summary>
    public List<DriveInfo> Drives { get; init; } = new();
}

#endregion
