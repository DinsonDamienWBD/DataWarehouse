using System.Runtime.CompilerServices;

namespace DataWarehouse.Plugins.Backup;

/// <summary>
/// Base interface for all backup providers.
/// Defines the contract for backup operations including full, incremental,
/// differential, block-level, and synthetic full backups.
/// </summary>
public interface IBackupProvider : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this provider.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets the display name for this provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the backup type this provider supports.
    /// </summary>
    BackupProviderType ProviderType { get; }

    /// <summary>
    /// Gets whether this provider is currently monitoring for changes.
    /// </summary>
    bool IsMonitoring { get; }

    /// <summary>
    /// Initializes the backup provider.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Performs a full backup of the specified paths.
    /// </summary>
    Task<BackupResult> PerformFullBackupAsync(
        IEnumerable<string> paths,
        BackupOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Performs an incremental backup (changes since last backup).
    /// </summary>
    Task<BackupResult> PerformIncrementalBackupAsync(
        BackupOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Restores from a backup to the specified target path.
    /// </summary>
    Task<RestoreResult> RestoreAsync(
        string backupId,
        string? targetPath = null,
        DateTime? pointInTime = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets backup statistics.
    /// </summary>
    BackupStatistics GetStatistics();

    /// <summary>
    /// Gets metadata for a specific backup.
    /// </summary>
    Task<BackupMetadata?> GetBackupMetadataAsync(string backupId, CancellationToken ct = default);

    /// <summary>
    /// Lists all available backups.
    /// </summary>
    IAsyncEnumerable<BackupMetadata> ListBackupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Event raised when backup progress changes.
    /// </summary>
    event EventHandler<BackupProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// Event raised when a backup completes.
    /// </summary>
    event EventHandler<BackupCompletedEventArgs>? BackupCompleted;
}

/// <summary>
/// Interface for continuous backup providers that support real-time monitoring.
/// </summary>
public interface IContinuousBackupProvider : IBackupProvider
{
    /// <summary>
    /// Starts monitoring the specified paths for changes.
    /// </summary>
    Task StartMonitoringAsync(IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>
    /// Stops monitoring for changes.
    /// </summary>
    Task StopMonitoringAsync();

    /// <summary>
    /// Event raised when a file change is detected.
    /// </summary>
    event EventHandler<FileChangeDetectedEventArgs>? ChangeDetected;
}

/// <summary>
/// Interface for providers that support differential backups.
/// </summary>
public interface IDifferentialBackupProvider : IBackupProvider
{
    /// <summary>
    /// Performs a differential backup (changes since last full backup).
    /// </summary>
    Task<BackupResult> PerformDifferentialBackupAsync(
        BackupOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>
/// Interface for providers that support block-level backups.
/// </summary>
public interface IDeltaBackupProvider : IBackupProvider
{
    /// <summary>
    /// Performs a block-level incremental backup for a specific file.
    /// </summary>
    Task<DeltaBackupResult> PerformBlockLevelBackupAsync(
        string filePath,
        CancellationToken ct = default);

    /// <summary>
    /// Gets changed blocks for a file since the last backup.
    /// </summary>
    Task<IReadOnlyList<ChangedBlock>> GetChangedBlocksAsync(
        string filePath,
        CancellationToken ct = default);
}

/// <summary>
/// Interface for providers that support synthetic full backups.
/// </summary>
public interface ISyntheticFullBackupProvider : IBackupProvider
{
    /// <summary>
    /// Creates a synthetic full backup by merging incrementals.
    /// </summary>
    Task<SyntheticFullBackupResult> CreateSyntheticFullBackupAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Gets the number of incrementals available for synthesis.
    /// </summary>
    int GetIncrementalCount();
}

/// <summary>
/// Backup provider type enumeration.
/// </summary>
public enum BackupProviderType
{
    /// <summary>Full backup provider.</summary>
    Full,
    /// <summary>Incremental backup provider.</summary>
    Incremental,
    /// <summary>Differential backup provider.</summary>
    Differential,
    /// <summary>Continuous real-time backup provider.</summary>
    Continuous,
    /// <summary>Block-level delta backup provider.</summary>
    Delta,
    /// <summary>Synthetic full backup provider.</summary>
    SyntheticFull
}

/// <summary>
/// Options for backup operations.
/// </summary>
public sealed record BackupOptions
{
    /// <summary>Gets or sets whether to verify after backup.</summary>
    public bool VerifyAfterBackup { get; init; } = true;

    /// <summary>Gets or sets whether to compress data.</summary>
    public bool CompressData { get; init; } = true;

    /// <summary>Gets or sets whether to encrypt data.</summary>
    public bool EncryptData { get; init; }

    /// <summary>Gets or sets file patterns to include.</summary>
    public string[]? IncludePatterns { get; init; }

    /// <summary>Gets or sets file patterns to exclude.</summary>
    public string[]? ExcludePatterns { get; init; }

    /// <summary>Gets or sets the maximum file size in bytes (0 = unlimited).</summary>
    public long MaxFileSizeBytes { get; init; }

    /// <summary>Gets or sets whether to follow symlinks.</summary>
    public bool FollowSymlinks { get; init; }

    /// <summary>Gets or sets whether to preserve permissions.</summary>
    public bool PreservePermissions { get; init; } = true;

    /// <summary>Gets or sets bandwidth limit in bytes per second (0 = unlimited).</summary>
    public long BandwidthLimitBytesPerSecond { get; init; }
}

/// <summary>
/// Result of a backup operation.
/// </summary>
public sealed record BackupResult
{
    /// <summary>Gets the backup ID.</summary>
    public required string BackupId { get; init; }

    /// <summary>Gets whether the backup succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the backup type.</summary>
    public BackupProviderType BackupType { get; init; }

    /// <summary>Gets the sequence number.</summary>
    public long Sequence { get; init; }

    /// <summary>Gets the total files processed.</summary>
    public long TotalFiles { get; set; }

    /// <summary>Gets the total bytes processed.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets any errors that occurred.</summary>
    public List<BackupError> Errors { get; init; } = new();

    /// <summary>Gets when the backup started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets when the backup completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets the duration of the backup.</summary>
    public TimeSpan Duration => CompletedAt.HasValue
        ? CompletedAt.Value - StartedAt
        : DateTime.UtcNow - StartedAt;
}

/// <summary>
/// Result of a restore operation.
/// </summary>
public sealed record RestoreResult
{
    /// <summary>Gets whether the restore succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the backup ID restored from.</summary>
    public required string BackupId { get; init; }

    /// <summary>Gets the target path.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Gets the point in time restored to.</summary>
    public DateTime? PointInTime { get; init; }

    /// <summary>Gets the bytes restored.</summary>
    public long BytesRestored { get; init; }

    /// <summary>Gets the files restored.</summary>
    public long FilesRestored { get; init; }

    /// <summary>Gets any error message.</summary>
    public string? Error { get; init; }

    /// <summary>Gets when the restore completed.</summary>
    public DateTime RestoredAt { get; init; }
}

/// <summary>
/// Result of a delta/block-level backup.
/// </summary>
public sealed record DeltaBackupResult
{
    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the changed blocks.</summary>
    public List<ChangedBlock> ChangedBlocks { get; init; } = new();

    /// <summary>Gets the number of unchanged blocks.</summary>
    public int UnchangedBlocks { get; set; }

    /// <summary>Gets the bytes changed.</summary>
    public long BytesChanged { get; set; }

    /// <summary>Gets when the backup started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets when the backup completed.</summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Result of a synthetic full backup.
/// </summary>
public sealed record SyntheticFullBackupResult
{
    /// <summary>Gets the base full backup sequence.</summary>
    public long BasedOnFullSequence { get; init; }

    /// <summary>Gets the number of incrementals merged.</summary>
    public int IncrementalCount { get; init; }

    /// <summary>Gets the files processed.</summary>
    public int FilesProcessed { get; set; }

    /// <summary>Gets the bytes processed.</summary>
    public long BytesProcessed { get; set; }

    /// <summary>Gets when the backup started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets when the backup completed.</summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// A changed block in a delta backup.
/// </summary>
public sealed record ChangedBlock
{
    /// <summary>Gets the block index.</summary>
    public int BlockIndex { get; init; }

    /// <summary>Gets the offset in the file.</summary>
    public long Offset { get; init; }

    /// <summary>Gets the size of the block.</summary>
    public int Size { get; init; }

    /// <summary>Gets the hash of the block.</summary>
    public required string Hash { get; init; }

    /// <summary>Gets the block data.</summary>
    public required byte[] Data { get; init; }
}

/// <summary>
/// Backup error information.
/// </summary>
public sealed record BackupError
{
    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the error message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets when the error occurred.</summary>
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Metadata for a backup.
/// </summary>
public sealed record BackupMetadata
{
    /// <summary>Gets the backup ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the backup type.</summary>
    public BackupProviderType Type { get; init; }

    /// <summary>Gets the sequence number.</summary>
    public long Sequence { get; init; }

    /// <summary>Gets when the backup was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets the total files.</summary>
    public long TotalFiles { get; init; }

    /// <summary>Gets the total bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Gets the checksum.</summary>
    public string? Checksum { get; init; }

    /// <summary>Gets custom metadata.</summary>
    public Dictionary<string, string> CustomMetadata { get; init; } = new();
}

/// <summary>
/// Backup statistics.
/// </summary>
public sealed record BackupStatistics
{
    /// <summary>Gets the total tracked files.</summary>
    public int TrackedFiles { get; init; }

    /// <summary>Gets the pending changes count.</summary>
    public int PendingChanges { get; init; }

    /// <summary>Gets the last backup sequence.</summary>
    public long LastBackupSequence { get; init; }

    /// <summary>Gets the last full backup time.</summary>
    public DateTime? LastFullBackupTime { get; init; }

    /// <summary>Gets the last incremental backup time.</summary>
    public DateTime? LastIncrementalBackupTime { get; init; }

    /// <summary>Gets whether monitoring is active.</summary>
    public bool IsMonitoring { get; init; }

    /// <summary>Gets the total bytes backed up.</summary>
    public long TotalBytesBackedUp { get; init; }

    /// <summary>Gets the deduplication ratio.</summary>
    public double DeduplicationRatio { get; init; }
}

/// <summary>
/// Event args for backup progress.
/// </summary>
public sealed class BackupProgressEventArgs : EventArgs
{
    /// <summary>Gets the backup ID.</summary>
    public required string BackupId { get; init; }

    /// <summary>Gets the current operation.</summary>
    public string? CurrentOperation { get; init; }

    /// <summary>Gets the processed files.</summary>
    public long ProcessedFiles { get; init; }

    /// <summary>Gets the total files.</summary>
    public long TotalFiles { get; init; }

    /// <summary>Gets the processed bytes.</summary>
    public long ProcessedBytes { get; init; }

    /// <summary>Gets the total bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Gets the percent complete.</summary>
    public double PercentComplete => TotalBytes > 0
        ? (double)ProcessedBytes / TotalBytes * 100
        : 0;
}

/// <summary>
/// Event args for backup completion.
/// </summary>
public sealed class BackupCompletedEventArgs : EventArgs
{
    /// <summary>Gets the backup result.</summary>
    public required BackupResult Result { get; init; }

    /// <summary>Gets any exception that occurred.</summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Event args for file change detection.
/// </summary>
public sealed class FileChangeDetectedEventArgs : EventArgs
{
    /// <summary>Gets the file path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the change type.</summary>
    public FileChangeType ChangeType { get; init; }

    /// <summary>Gets the old path (for renames).</summary>
    public string? OldPath { get; init; }

    /// <summary>Gets when the change was detected.</summary>
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// File change type.
/// </summary>
public enum FileChangeType
{
    /// <summary>File was created.</summary>
    Created,
    /// <summary>File was modified.</summary>
    Modified,
    /// <summary>File was deleted.</summary>
    Deleted,
    /// <summary>File was renamed.</summary>
    Renamed
}
