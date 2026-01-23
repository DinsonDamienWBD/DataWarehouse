using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AirGappedBackup;

#region Media Types

/// <summary>
/// Represents the type of air-gapped storage media.
/// </summary>
public enum MediaType
{
    /// <summary>LTO-5 tape (1.5TB native, 3TB compressed).</summary>
    LTO5,
    /// <summary>LTO-6 tape (2.5TB native, 6.25TB compressed).</summary>
    LTO6,
    /// <summary>LTO-7 tape (6TB native, 15TB compressed).</summary>
    LTO7,
    /// <summary>LTO-8 tape (12TB native, 30TB compressed).</summary>
    LTO8,
    /// <summary>LTO-9 tape (18TB native, 45TB compressed).</summary>
    LTO9,
    /// <summary>Removable USB hard drive.</summary>
    RemovableHDD,
    /// <summary>Removable solid state drive.</summary>
    RemovableSSD,
    /// <summary>M-DISC optical media (archival grade).</summary>
    MDisc,
    /// <summary>Blu-ray archival disc.</summary>
    BluRayArchival,
    /// <summary>Custom offline storage device.</summary>
    Custom
}

/// <summary>
/// Represents the current status of a media unit.
/// </summary>
public enum MediaStatus
{
    /// <summary>Media is available and ready for use.</summary>
    Available,
    /// <summary>Media is currently in use for read/write operations.</summary>
    InUse,
    /// <summary>Media has been offsite and is pending verification.</summary>
    PendingVerification,
    /// <summary>Media has been verified and is in secure storage.</summary>
    InSecureStorage,
    /// <summary>Media is in transit to offsite location.</summary>
    InTransitOffsite,
    /// <summary>Media is stored at offsite location.</summary>
    OffsiteStorage,
    /// <summary>Media is in transit returning from offsite.</summary>
    InTransitOnsite,
    /// <summary>Media has been retired and should not be used.</summary>
    Retired,
    /// <summary>Media has failed verification and is potentially corrupted.</summary>
    Failed,
    /// <summary>Media is being sanitized for reuse.</summary>
    Sanitizing
}

/// <summary>
/// Represents the write protection state of media.
/// </summary>
public enum WriteProtectionState
{
    /// <summary>Write protection status is unknown.</summary>
    Unknown,
    /// <summary>Hardware write protection is enabled.</summary>
    HardwareProtected,
    /// <summary>Software write protection is enabled.</summary>
    SoftwareProtected,
    /// <summary>Media is not write protected.</summary>
    NotProtected,
    /// <summary>Media is read-only by design.</summary>
    ReadOnlyMedia
}

/// <summary>
/// Represents information about a physical media unit.
/// </summary>
public sealed class MediaInfo
{
    /// <summary>Gets or sets the unique media identifier (barcode/serial number).</summary>
    public required string MediaId { get; init; }

    /// <summary>Gets or sets the human-readable label for the media.</summary>
    public required string Label { get; init; }

    /// <summary>Gets or sets the type of media.</summary>
    public MediaType Type { get; init; }

    /// <summary>Gets or sets the current status of the media.</summary>
    public MediaStatus Status { get; set; }

    /// <summary>Gets or sets the write protection state.</summary>
    public WriteProtectionState WriteProtection { get; set; }

    /// <summary>Gets or sets the total capacity in bytes.</summary>
    public long CapacityBytes { get; init; }

    /// <summary>Gets or sets the used capacity in bytes.</summary>
    public long UsedBytes { get; set; }

    /// <summary>Gets or sets the remaining capacity in bytes.</summary>
    public long RemainingBytes => CapacityBytes - UsedBytes;

    /// <summary>Gets or sets when the media was first used.</summary>
    public DateTime? FirstUsedAt { get; set; }

    /// <summary>Gets or sets when the media was last written to.</summary>
    public DateTime? LastWriteAt { get; set; }

    /// <summary>Gets or sets when the media was last verified.</summary>
    public DateTime? LastVerifiedAt { get; set; }

    /// <summary>Gets or sets the total write cycles for the media.</summary>
    public int TotalWriteCycles { get; set; }

    /// <summary>Gets or sets the maximum recommended write cycles.</summary>
    public int MaxWriteCycles { get; init; }

    /// <summary>Gets or sets the current rotation pool assignment.</summary>
    public string? RotationPool { get; set; }

    /// <summary>Gets or sets the current physical location.</summary>
    public string? CurrentLocation { get; set; }

    /// <summary>Gets or sets the manufacturer serial number.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Gets or sets the media's root encryption key hash (for verification).</summary>
    public string? EncryptionKeyHash { get; set; }

    /// <summary>Gets or sets the last integrity verification hash.</summary>
    public string? LastVerificationHash { get; set; }

    /// <summary>Gets or sets custom metadata for the media.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>Gets or sets the list of backup IDs stored on this media.</summary>
    public List<string> BackupIds { get; init; } = new();

    /// <summary>Gets the percentage of lifecycle consumed.</summary>
    public double LifecyclePercentUsed => MaxWriteCycles > 0
        ? (double)TotalWriteCycles / MaxWriteCycles * 100
        : 0;
}

/// <summary>
/// Represents a media rotation schedule entry.
/// </summary>
public sealed class MediaRotationEntry
{
    /// <summary>Gets or sets the rotation entry ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets or sets the media ID.</summary>
    public required string MediaId { get; init; }

    /// <summary>Gets or sets the rotation pool name.</summary>
    public required string PoolName { get; init; }

    /// <summary>Gets or sets the scheduled rotation date.</summary>
    public DateTime ScheduledDate { get; init; }

    /// <summary>Gets or sets the actual rotation date.</summary>
    public DateTime? ActualDate { get; set; }

    /// <summary>Gets or sets the rotation type (to offsite, from offsite, etc.).</summary>
    public RotationType RotationType { get; init; }

    /// <summary>Gets or sets whether the rotation has been completed.</summary>
    public bool IsCompleted { get; set; }

    /// <summary>Gets or sets the person responsible for this rotation.</summary>
    public string? ResponsiblePerson { get; set; }

    /// <summary>Gets or sets notes about this rotation.</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Specifies the type of media rotation operation.
/// </summary>
public enum RotationType
{
    /// <summary>Media is being rotated to offsite storage.</summary>
    ToOffsite,
    /// <summary>Media is being rotated from offsite to onsite.</summary>
    ToOnsite,
    /// <summary>Media is being retired from rotation.</summary>
    Retirement,
    /// <summary>Media is being introduced to the rotation pool.</summary>
    Introduction,
    /// <summary>Media is being moved between pools.</summary>
    PoolTransfer
}

#endregion

#region Tape Library Types

/// <summary>
/// Configuration for a tape library system.
/// </summary>
public sealed record TapeLibraryConfig
{
    /// <summary>Gets or sets the library identifier.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Gets or sets the library name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets or sets the library type.</summary>
    public TapeLibraryType LibraryType { get; init; }

    /// <summary>Gets or sets the device path (e.g., /dev/sg0 or \\.\Tape0).</summary>
    public required string DevicePath { get; init; }

    /// <summary>Gets or sets the number of tape drives in the library.</summary>
    public int DriveCount { get; init; } = 1;

    /// <summary>Gets or sets the number of slots in the library.</summary>
    public int SlotCount { get; init; }

    /// <summary>Gets or sets whether the library supports barcodes.</summary>
    public bool SupportsBarcodes { get; init; } = true;

    /// <summary>Gets or sets the SCSI target ID.</summary>
    public int? ScsiTargetId { get; init; }

    /// <summary>Gets or sets the SCSI LUN.</summary>
    public int? ScsiLun { get; init; }

    /// <summary>Gets or sets vendor-specific configuration.</summary>
    public Dictionary<string, string> VendorConfig { get; init; } = new();
}

/// <summary>
/// Specifies the type of tape library.
/// </summary>
public enum TapeLibraryType
{
    /// <summary>Single standalone tape drive.</summary>
    StandaloneDrive,
    /// <summary>Small autoloader (typically 1 drive, multiple slots).</summary>
    Autoloader,
    /// <summary>Medium tape library with robotic handling.</summary>
    MediumLibrary,
    /// <summary>Large enterprise tape library.</summary>
    EnterpriseLibrary,
    /// <summary>Virtual tape library (VTL).</summary>
    VirtualLibrary
}

/// <summary>
/// Represents the status of a tape drive.
/// </summary>
public sealed class TapeDriveStatus
{
    /// <summary>Gets or sets the drive index.</summary>
    public int DriveIndex { get; init; }

    /// <summary>Gets or sets whether the drive is online.</summary>
    public bool IsOnline { get; set; }

    /// <summary>Gets or sets whether the drive has media loaded.</summary>
    public bool HasMedia { get; set; }

    /// <summary>Gets or sets the loaded media ID.</summary>
    public string? LoadedMediaId { get; set; }

    /// <summary>Gets or sets whether the drive is busy.</summary>
    public bool IsBusy { get; set; }

    /// <summary>Gets or sets the current operation.</summary>
    public string? CurrentOperation { get; set; }

    /// <summary>Gets or sets the drive firmware version.</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>Gets or sets the cleaning required status.</summary>
    public bool CleaningRequired { get; set; }

    /// <summary>Gets or sets the total bytes written since last cleaning.</summary>
    public long BytesSinceLastCleaning { get; set; }
}

/// <summary>
/// Represents a slot in a tape library.
/// </summary>
public sealed class TapeSlotInfo
{
    /// <summary>Gets or sets the slot number.</summary>
    public int SlotNumber { get; init; }

    /// <summary>Gets or sets whether the slot is occupied.</summary>
    public bool IsOccupied { get; set; }

    /// <summary>Gets or sets the media ID in the slot (if occupied).</summary>
    public string? MediaId { get; set; }

    /// <summary>Gets or sets the barcode label (if readable).</summary>
    public string? BarcodeLabel { get; set; }

    /// <summary>Gets or sets whether this is an import/export slot.</summary>
    public bool IsImportExportSlot { get; init; }
}

#endregion

#region Chain of Custody Types

/// <summary>
/// Represents an entry in the chain of custody audit trail.
/// </summary>
public sealed class ChainOfCustodyEntry
{
    /// <summary>Gets or sets the unique entry ID.</summary>
    public required string EntryId { get; init; }

    /// <summary>Gets or sets the media ID.</summary>
    public required string MediaId { get; init; }

    /// <summary>Gets or sets the timestamp of the event.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the type of custody event.</summary>
    public CustodyEventType EventType { get; init; }

    /// <summary>Gets or sets the previous custodian.</summary>
    public string? FromCustodian { get; init; }

    /// <summary>Gets or sets the new custodian.</summary>
    public string? ToCustodian { get; init; }

    /// <summary>Gets or sets the location of the event.</summary>
    public string? Location { get; init; }

    /// <summary>Gets or sets the previous location.</summary>
    public string? FromLocation { get; init; }

    /// <summary>Gets or sets the new location.</summary>
    public string? ToLocation { get; init; }

    /// <summary>Gets or sets the verification performed.</summary>
    public string? VerificationPerformed { get; init; }

    /// <summary>Gets or sets the verification result hash.</summary>
    public string? VerificationHash { get; init; }

    /// <summary>Gets or sets whether the seal was verified (if applicable).</summary>
    public bool? SealVerified { get; set; }

    /// <summary>Gets or sets the new seal number (if resealed).</summary>
    public string? NewSealNumber { get; set; }

    /// <summary>Gets or sets the authorizing person.</summary>
    public string? AuthorizedBy { get; init; }

    /// <summary>Gets or sets the witness (if required).</summary>
    public string? Witness { get; init; }

    /// <summary>Gets or sets the digital signature of this entry.</summary>
    public string? DigitalSignature { get; set; }

    /// <summary>Gets or sets notes about the event.</summary>
    public string? Notes { get; init; }

    /// <summary>Gets or sets attached evidence (e.g., photo paths).</summary>
    public List<string> AttachedEvidence { get; init; } = new();
}

/// <summary>
/// Specifies the type of custody event.
/// </summary>
public enum CustodyEventType
{
    /// <summary>Media created or introduced to inventory.</summary>
    Created,
    /// <summary>Media handed over to another custodian.</summary>
    Handover,
    /// <summary>Media moved to a new location.</summary>
    LocationChange,
    /// <summary>Media verified (integrity check).</summary>
    Verification,
    /// <summary>Media sealed for transport.</summary>
    Sealed,
    /// <summary>Media seal opened.</summary>
    SealOpened,
    /// <summary>Media loaded into drive/library.</summary>
    Loaded,
    /// <summary>Media unloaded from drive/library.</summary>
    Unloaded,
    /// <summary>Write operation performed.</summary>
    WriteOperation,
    /// <summary>Read operation performed.</summary>
    ReadOperation,
    /// <summary>Media retired from service.</summary>
    Retired,
    /// <summary>Media destroyed.</summary>
    Destroyed,
    /// <summary>Anomaly or incident detected.</summary>
    Incident
}

/// <summary>
/// Represents a custody location.
/// </summary>
public sealed class CustodyLocation
{
    /// <summary>Gets or sets the location ID.</summary>
    public required string LocationId { get; init; }

    /// <summary>Gets or sets the location name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets or sets the location type.</summary>
    public LocationType Type { get; init; }

    /// <summary>Gets or sets the physical address.</summary>
    public string? Address { get; init; }

    /// <summary>Gets or sets the security level.</summary>
    public SecurityLevel SecurityLevel { get; init; }

    /// <summary>Gets or sets whether the location is offsite.</summary>
    public bool IsOffsite { get; init; }

    /// <summary>Gets or sets the maximum media capacity.</summary>
    public int? MaxCapacity { get; init; }

    /// <summary>Gets or sets environmental controls information.</summary>
    public string? EnvironmentalControls { get; init; }

    /// <summary>Gets or sets access control information.</summary>
    public string? AccessControl { get; init; }
}

/// <summary>
/// Specifies the type of custody location.
/// </summary>
public enum LocationType
{
    /// <summary>Primary data center.</summary>
    PrimaryDataCenter,
    /// <summary>Secondary/DR data center.</summary>
    SecondaryDataCenter,
    /// <summary>Secure vault.</summary>
    SecureVault,
    /// <summary>Offsite storage facility.</summary>
    OffsiteStorage,
    /// <summary>Secure transit container.</summary>
    TransitContainer,
    /// <summary>Disaster recovery site.</summary>
    DisasterRecoverySite
}

/// <summary>
/// Specifies the security level of a location.
/// </summary>
public enum SecurityLevel
{
    /// <summary>Standard security.</summary>
    Standard,
    /// <summary>Enhanced security with additional controls.</summary>
    Enhanced,
    /// <summary>High security with strict access control.</summary>
    High,
    /// <summary>Maximum security (vault-grade).</summary>
    Maximum
}

#endregion

#region Catalog Types

/// <summary>
/// Represents an offline backup catalog.
/// </summary>
public sealed class OfflineCatalog
{
    /// <summary>Gets or sets the catalog ID.</summary>
    public required string CatalogId { get; init; }

    /// <summary>Gets or sets the catalog version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets when the catalog was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets or sets when the catalog was last updated.</summary>
    public DateTime LastUpdatedAt { get; set; }

    /// <summary>Gets or sets the catalog entries.</summary>
    public Dictionary<string, CatalogEntry> Entries { get; init; } = new();

    /// <summary>Gets or sets the media index (media ID -> backup IDs).</summary>
    public Dictionary<string, List<string>> MediaIndex { get; init; } = new();

    /// <summary>Gets or sets the path index for quick file lookups.</summary>
    public Dictionary<string, List<string>> PathIndex { get; init; } = new();

    /// <summary>Gets or sets the catalog checksum for integrity verification.</summary>
    public string? Checksum { get; set; }
}

/// <summary>
/// Represents an entry in the offline catalog.
/// </summary>
public sealed class CatalogEntry
{
    /// <summary>Gets or sets the backup ID.</summary>
    public required string BackupId { get; init; }

    /// <summary>Gets or sets the media ID where this backup is stored.</summary>
    public required string MediaId { get; init; }

    /// <summary>Gets or sets the backup type.</summary>
    public string BackupType { get; init; } = "Full";

    /// <summary>Gets or sets when the backup was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets or sets the total size in bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Gets or sets the total file count.</summary>
    public long TotalFiles { get; init; }

    /// <summary>Gets or sets the backup content hash.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Gets or sets the encryption key ID used.</summary>
    public string? EncryptionKeyId { get; init; }

    /// <summary>Gets or sets the file manifest.</summary>
    public List<CatalogFileEntry> Files { get; init; } = new();

    /// <summary>Gets or sets custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>Gets or sets the tape block position (for tapes).</summary>
    public long? TapeBlockPosition { get; init; }

    /// <summary>Gets or sets the tape file number (for tapes).</summary>
    public int? TapeFileNumber { get; init; }
}

/// <summary>
/// Represents a file entry in the catalog.
/// </summary>
public sealed class CatalogFileEntry
{
    /// <summary>Gets or sets the relative path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets or sets the file size.</summary>
    public long Size { get; init; }

    /// <summary>Gets or sets the file hash.</summary>
    public required string Hash { get; init; }

    /// <summary>Gets or sets the last modified time.</summary>
    public DateTime ModifiedAt { get; init; }

    /// <summary>Gets or sets file attributes.</summary>
    public string? Attributes { get; init; }
}

#endregion

#region Transfer Types

/// <summary>
/// Configuration for secure media transfer.
/// </summary>
public sealed record SecureTransferConfig
{
    /// <summary>Gets or sets whether to require dual custody.</summary>
    public bool RequireDualCustody { get; init; }

    /// <summary>Gets or sets whether to require sealed containers.</summary>
    public bool RequireSealedContainer { get; init; }

    /// <summary>Gets or sets whether to require verification before transfer.</summary>
    public bool RequirePreTransferVerification { get; init; } = true;

    /// <summary>Gets or sets whether to require verification after transfer.</summary>
    public bool RequirePostTransferVerification { get; init; } = true;

    /// <summary>Gets or sets the maximum transit time allowed.</summary>
    public TimeSpan? MaxTransitTime { get; init; }

    /// <summary>Gets or sets whether GPS tracking is required.</summary>
    public bool RequireGpsTracking { get; init; }

    /// <summary>Gets or sets the required security level for transport.</summary>
    public SecurityLevel MinimumSecurityLevel { get; init; } = SecurityLevel.Enhanced;
}

/// <summary>
/// Represents a media transfer operation.
/// </summary>
public sealed class MediaTransfer
{
    /// <summary>Gets or sets the transfer ID.</summary>
    public required string TransferId { get; init; }

    /// <summary>Gets or sets the media IDs being transferred.</summary>
    public List<string> MediaIds { get; init; } = new();

    /// <summary>Gets or sets the source location.</summary>
    public required string FromLocation { get; init; }

    /// <summary>Gets or sets the destination location.</summary>
    public required string ToLocation { get; init; }

    /// <summary>Gets or sets the transfer status.</summary>
    public TransferStatus Status { get; set; }

    /// <summary>Gets or sets when the transfer was initiated.</summary>
    public DateTime InitiatedAt { get; init; }

    /// <summary>Gets or sets when the transfer was completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the sending custodian.</summary>
    public required string SendingCustodian { get; init; }

    /// <summary>Gets or sets the receiving custodian.</summary>
    public string? ReceivingCustodian { get; set; }

    /// <summary>Gets or sets the container seal number.</summary>
    public string? SealNumber { get; init; }

    /// <summary>Gets or sets the courier/carrier information.</summary>
    public string? Carrier { get; init; }

    /// <summary>Gets or sets the tracking number.</summary>
    public string? TrackingNumber { get; init; }

    /// <summary>Gets or sets pre-transfer verification results.</summary>
    public Dictionary<string, string> PreTransferVerification { get; init; } = new();

    /// <summary>Gets or sets post-transfer verification results.</summary>
    public Dictionary<string, string> PostTransferVerification { get; init; } = new();
}

/// <summary>
/// Specifies the status of a media transfer.
/// </summary>
public enum TransferStatus
{
    /// <summary>Transfer is being prepared.</summary>
    Preparing,
    /// <summary>Transfer is awaiting pickup.</summary>
    AwaitingPickup,
    /// <summary>Transfer is in transit.</summary>
    InTransit,
    /// <summary>Transfer has been delivered.</summary>
    Delivered,
    /// <summary>Transfer is being verified at destination.</summary>
    Verifying,
    /// <summary>Transfer completed successfully.</summary>
    Completed,
    /// <summary>Transfer failed.</summary>
    Failed,
    /// <summary>Transfer was cancelled.</summary>
    Cancelled
}

#endregion

#region Verification Types

/// <summary>
/// Result of an integrity verification operation.
/// </summary>
public sealed class VerificationResult
{
    /// <summary>Gets or sets the verification ID.</summary>
    public required string VerificationId { get; init; }

    /// <summary>Gets or sets the media ID verified.</summary>
    public required string MediaId { get; init; }

    /// <summary>Gets or sets when verification started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets or sets when verification completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets whether verification passed.</summary>
    public bool Passed { get; set; }

    /// <summary>Gets or sets the overall hash of verified content.</summary>
    public string? OverallHash { get; set; }

    /// <summary>Gets or sets the expected hash.</summary>
    public string? ExpectedHash { get; init; }

    /// <summary>Gets or sets the bytes verified.</summary>
    public long BytesVerified { get; set; }

    /// <summary>Gets or sets the files verified.</summary>
    public long FilesVerified { get; set; }

    /// <summary>Gets or sets any errors encountered.</summary>
    public List<VerificationError> Errors { get; init; } = new();

    /// <summary>Gets or sets verification details per backup.</summary>
    public Dictionary<string, BackupVerificationDetail> BackupDetails { get; init; } = new();
}

/// <summary>
/// Detailed verification result for a specific backup.
/// </summary>
public sealed class BackupVerificationDetail
{
    /// <summary>Gets or sets the backup ID.</summary>
    public required string BackupId { get; init; }

    /// <summary>Gets or sets whether this backup passed verification.</summary>
    public bool Passed { get; set; }

    /// <summary>Gets or sets the computed hash.</summary>
    public string? ComputedHash { get; set; }

    /// <summary>Gets or sets the expected hash.</summary>
    public string? ExpectedHash { get; init; }

    /// <summary>Gets or sets files that failed verification.</summary>
    public List<string> FailedFiles { get; init; } = new();
}

/// <summary>
/// Represents a verification error.
/// </summary>
public sealed class VerificationError
{
    /// <summary>Gets or sets the error type.</summary>
    public VerificationErrorType ErrorType { get; init; }

    /// <summary>Gets or sets the file path (if applicable).</summary>
    public string? FilePath { get; init; }

    /// <summary>Gets or sets the expected value.</summary>
    public string? ExpectedValue { get; init; }

    /// <summary>Gets or sets the actual value.</summary>
    public string? ActualValue { get; init; }

    /// <summary>Gets or sets the error message.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Specifies the type of verification error.
/// </summary>
public enum VerificationErrorType
{
    /// <summary>Hash mismatch detected.</summary>
    HashMismatch,
    /// <summary>File is missing.</summary>
    MissingFile,
    /// <summary>File is corrupted.</summary>
    CorruptedFile,
    /// <summary>Read error occurred.</summary>
    ReadError,
    /// <summary>Media is unreadable.</summary>
    MediaUnreadable,
    /// <summary>Checksum validation failed.</summary>
    ChecksumFailure,
    /// <summary>Encryption verification failed.</summary>
    EncryptionError
}

#endregion

#region Air Gap Verification Types

/// <summary>
/// Result of air gap verification.
/// </summary>
public sealed class AirGapVerificationResult
{
    /// <summary>Gets or sets the verification ID.</summary>
    public required string VerificationId { get; init; }

    /// <summary>Gets or sets when verification was performed.</summary>
    public DateTime VerifiedAt { get; init; }

    /// <summary>Gets or sets whether the air gap is verified.</summary>
    public bool IsAirGapped { get; set; }

    /// <summary>Gets or sets network interface statuses.</summary>
    public List<NetworkInterfaceStatus> NetworkInterfaces { get; init; } = new();

    /// <summary>Gets or sets detected network connections.</summary>
    public List<string> DetectedConnections { get; init; } = new();

    /// <summary>Gets or sets the wireless status.</summary>
    public WirelessStatus WirelessStatus { get; set; }

    /// <summary>Gets or sets Bluetooth status.</summary>
    public bool BluetoothEnabled { get; set; }

    /// <summary>Gets or sets any warnings.</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Network interface status for air gap verification.
/// </summary>
public sealed class NetworkInterfaceStatus
{
    /// <summary>Gets or sets the interface name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets or sets whether the interface is up.</summary>
    public bool IsUp { get; set; }

    /// <summary>Gets or sets whether the interface has an IP address.</summary>
    public bool HasIpAddress { get; set; }

    /// <summary>Gets or sets the interface type.</summary>
    public string? InterfaceType { get; init; }
}

/// <summary>
/// Wireless network status.
/// </summary>
public enum WirelessStatus
{
    /// <summary>Wireless is disabled.</summary>
    Disabled,
    /// <summary>Wireless is enabled but not connected.</summary>
    EnabledNotConnected,
    /// <summary>Wireless is connected.</summary>
    Connected,
    /// <summary>Unable to determine status.</summary>
    Unknown
}

#endregion

#region Configuration Types

/// <summary>
/// Configuration for the air-gapped backup plugin.
/// </summary>
public sealed record AirGappedBackupConfig
{
    /// <summary>Gets or sets the state storage path.</summary>
    public string? StatePath { get; init; }

    /// <summary>Gets or sets tape library configurations.</summary>
    public List<TapeLibraryConfig> TapeLibraries { get; init; } = new();

    /// <summary>Gets or sets the default encryption algorithm.</summary>
    public string EncryptionAlgorithm { get; init; } = "AES-256-GCM";

    /// <summary>Gets or sets whether encryption is mandatory.</summary>
    public bool MandatoryEncryption { get; init; } = true;

    /// <summary>Gets or sets the hash algorithm for verification.</summary>
    public string HashAlgorithm { get; init; } = "SHA-256";

    /// <summary>Gets or sets whether to require air gap verification before writes.</summary>
    public bool RequireAirGapVerification { get; init; } = true;

    /// <summary>Gets or sets the secure transfer configuration.</summary>
    public SecureTransferConfig TransferConfig { get; init; } = new();

    /// <summary>Gets or sets custody locations.</summary>
    public List<CustodyLocation> CustodyLocations { get; init; } = new();

    /// <summary>Gets or sets the verification interval for stored media.</summary>
    public TimeSpan VerificationInterval { get; init; } = TimeSpan.FromDays(90);

    /// <summary>Gets or sets the rotation schedule interval.</summary>
    public TimeSpan RotationInterval { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Gets or sets the maximum media age before retirement.</summary>
    public TimeSpan MaxMediaAge { get; init; } = TimeSpan.FromDays(365 * 5);

    /// <summary>Gets or sets whether to enable hardware write blocking detection.</summary>
    public bool EnableWriteBlockDetection { get; init; } = true;

    /// <summary>Gets or sets the catalog sync path for offline catalog storage.</summary>
    public string? CatalogSyncPath { get; init; }
}

/// <summary>
/// Options for air-gapped backup operations.
/// </summary>
public sealed record AirGappedBackupOptions
{
    /// <summary>Gets or sets the target media ID.</summary>
    public string? TargetMediaId { get; init; }

    /// <summary>Gets or sets the encryption key ID to use.</summary>
    public string? EncryptionKeyId { get; init; }

    /// <summary>Gets or sets whether to verify after write.</summary>
    public bool VerifyAfterWrite { get; init; } = true;

    /// <summary>Gets or sets whether to enable write protection after backup.</summary>
    public bool EnableWriteProtectionAfterBackup { get; init; } = true;

    /// <summary>Gets or sets the compression level (0-9).</summary>
    public int CompressionLevel { get; init; } = 6;

    /// <summary>Gets or sets whether to span across multiple media if needed.</summary>
    public bool AllowMultiMediaSpan { get; init; } = true;

    /// <summary>Gets or sets the custodian performing the backup.</summary>
    public string? Custodian { get; init; }

    /// <summary>Gets or sets the location where backup is being performed.</summary>
    public string? Location { get; init; }

    /// <summary>Gets or sets custom metadata to include.</summary>
    public Dictionary<string, string> CustomMetadata { get; init; } = new();
}

/// <summary>
/// Options for import operations.
/// </summary>
public sealed record ImportOptions
{
    /// <summary>Gets or sets the source media ID.</summary>
    public required string SourceMediaId { get; init; }

    /// <summary>Gets or sets specific backup IDs to import (null = all).</summary>
    public List<string>? BackupIds { get; init; }

    /// <summary>Gets or sets the target path for imported data.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Gets or sets whether to verify during import.</summary>
    public bool VerifyDuringImport { get; init; } = true;

    /// <summary>Gets or sets the decryption key ID.</summary>
    public string? DecryptionKeyId { get; init; }

    /// <summary>Gets or sets the custodian performing the import.</summary>
    public string? Custodian { get; init; }
}

/// <summary>
/// Options for export operations.
/// </summary>
public sealed record ExportOptions
{
    /// <summary>Gets or sets the backup IDs to export.</summary>
    public required List<string> BackupIds { get; init; }

    /// <summary>Gets or sets the target media ID.</summary>
    public required string TargetMediaId { get; init; }

    /// <summary>Gets or sets the encryption key ID.</summary>
    public string? EncryptionKeyId { get; init; }

    /// <summary>Gets or sets whether to verify after export.</summary>
    public bool VerifyAfterExport { get; init; } = true;

    /// <summary>Gets or sets the custodian performing the export.</summary>
    public string? Custodian { get; init; }

    /// <summary>Gets or sets the intended destination location.</summary>
    public string? DestinationLocation { get; init; }
}

#endregion

#region Result Types

/// <summary>
/// Result of an air-gapped backup operation.
/// </summary>
public sealed class AirGappedBackupResult
{
    /// <summary>Gets or sets the backup ID.</summary>
    public required string BackupId { get; init; }

    /// <summary>Gets or sets whether the backup succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the media IDs used.</summary>
    public List<string> MediaIds { get; init; } = new();

    /// <summary>Gets or sets the total bytes written.</summary>
    public long TotalBytesWritten { get; set; }

    /// <summary>Gets or sets the total files written.</summary>
    public long TotalFilesWritten { get; set; }

    /// <summary>Gets or sets when the backup started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets or sets when the backup completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the content hash.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Gets or sets the encryption key ID used.</summary>
    public string? EncryptionKeyId { get; set; }

    /// <summary>Gets or sets the verification result.</summary>
    public VerificationResult? Verification { get; set; }

    /// <summary>Gets or sets any errors that occurred.</summary>
    public List<string> Errors { get; init; } = new();

    /// <summary>Gets or sets the chain of custody entries created.</summary>
    public List<string> CustodyEntryIds { get; init; } = new();
}

/// <summary>
/// Result of an import operation.
/// </summary>
public sealed class ImportResult
{
    /// <summary>Gets or sets the import ID.</summary>
    public required string ImportId { get; init; }

    /// <summary>Gets or sets whether the import succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the source media ID.</summary>
    public required string SourceMediaId { get; init; }

    /// <summary>Gets or sets the backup IDs imported.</summary>
    public List<string> ImportedBackupIds { get; init; } = new();

    /// <summary>Gets or sets the total bytes imported.</summary>
    public long TotalBytesImported { get; set; }

    /// <summary>Gets or sets the total files imported.</summary>
    public long TotalFilesImported { get; set; }

    /// <summary>Gets or sets when the import started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets or sets when the import completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets verification results.</summary>
    public VerificationResult? Verification { get; set; }

    /// <summary>Gets or sets any errors that occurred.</summary>
    public List<string> Errors { get; init; } = new();
}

/// <summary>
/// Result of an export operation.
/// </summary>
public sealed class ExportResult
{
    /// <summary>Gets or sets the export ID.</summary>
    public required string ExportId { get; init; }

    /// <summary>Gets or sets whether the export succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the target media ID.</summary>
    public required string TargetMediaId { get; init; }

    /// <summary>Gets or sets the backup IDs exported.</summary>
    public List<string> ExportedBackupIds { get; init; } = new();

    /// <summary>Gets or sets the total bytes exported.</summary>
    public long TotalBytesExported { get; set; }

    /// <summary>Gets or sets when the export started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets or sets when the export completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the content hash for verification.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Gets or sets verification results.</summary>
    public VerificationResult? Verification { get; set; }

    /// <summary>Gets or sets any errors that occurred.</summary>
    public List<string> Errors { get; init; } = new();
}

#endregion

#region Event Args

/// <summary>
/// Event args for media status changes.
/// </summary>
public sealed class MediaStatusChangedEventArgs : EventArgs
{
    /// <summary>Gets the media ID.</summary>
    public required string MediaId { get; init; }

    /// <summary>Gets the previous status.</summary>
    public MediaStatus PreviousStatus { get; init; }

    /// <summary>Gets the new status.</summary>
    public MediaStatus NewStatus { get; init; }

    /// <summary>Gets when the change occurred.</summary>
    public DateTime ChangedAt { get; init; }

    /// <summary>Gets the reason for the change.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Event args for chain of custody events.
/// </summary>
public sealed class CustodyEventArgs : EventArgs
{
    /// <summary>Gets the custody entry.</summary>
    public required ChainOfCustodyEntry Entry { get; init; }
}

/// <summary>
/// Event args for verification progress.
/// </summary>
public sealed class VerificationProgressEventArgs : EventArgs
{
    /// <summary>Gets the verification ID.</summary>
    public required string VerificationId { get; init; }

    /// <summary>Gets the media ID.</summary>
    public required string MediaId { get; init; }

    /// <summary>Gets the bytes verified.</summary>
    public long BytesVerified { get; init; }

    /// <summary>Gets the total bytes to verify.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Gets the current file being verified.</summary>
    public string? CurrentFile { get; init; }

    /// <summary>Gets the percentage complete.</summary>
    public double PercentComplete => TotalBytes > 0
        ? (double)BytesVerified / TotalBytes * 100
        : 0;
}

#endregion
