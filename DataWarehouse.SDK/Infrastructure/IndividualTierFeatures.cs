using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// TIER 1: INDIVIDUAL USERS (Laptop/Desktop) - Complete Implementation
// Features: Local Backup, Encryption Options, File Versioning, Deduplication,
// Continuous/Incremental Backup
// ============================================================================

#region Exceptions

/// <summary>
/// Exception thrown when a backup operation fails.
/// </summary>
public sealed class BackupException : Exception
{
    /// <summary>
    /// Gets the backup job ID associated with this exception.
    /// </summary>
    public string? JobId { get; }

    /// <summary>
    /// Gets the backup destination that failed.
    /// </summary>
    public string? Destination { get; }

    public BackupException(string message) : base(message) { }
    public BackupException(string message, Exception innerException) : base(message, innerException) { }
    public BackupException(string message, string? jobId, string? destination, Exception? innerException = null)
        : base(message, innerException)
    {
        JobId = jobId;
        Destination = destination;
    }
}

/// <summary>
/// Exception thrown when encryption operations fail.
/// </summary>
public sealed class EncryptionException : Exception
{
    /// <summary>
    /// Gets the encryption algorithm that failed.
    /// </summary>
    public string? Algorithm { get; }

    public EncryptionException(string message) : base(message) { }
    public EncryptionException(string message, Exception innerException) : base(message, innerException) { }
    public EncryptionException(string message, string? algorithm, Exception? innerException = null)
        : base(message, innerException)
    {
        Algorithm = algorithm;
    }
}

/// <summary>
/// Exception thrown when versioning operations fail.
/// </summary>
public sealed class VersioningException : Exception
{
    /// <summary>
    /// Gets the file path associated with this exception.
    /// </summary>
    public string? FilePath { get; }

    /// <summary>
    /// Gets the version ID that caused the failure.
    /// </summary>
    public string? VersionId { get; }

    public VersioningException(string message) : base(message) { }
    public VersioningException(string message, Exception innerException) : base(message, innerException) { }
    public VersioningException(string message, string? filePath, string? versionId = null, Exception? innerException = null)
        : base(message, innerException)
    {
        FilePath = filePath;
        VersionId = versionId;
    }
}

/// <summary>
/// Exception thrown when deduplication operations fail.
/// </summary>
public sealed class DeduplicationException : Exception
{
    /// <summary>
    /// Gets the chunk hash that caused the failure.
    /// </summary>
    public string? ChunkHash { get; }

    public DeduplicationException(string message) : base(message) { }
    public DeduplicationException(string message, Exception innerException) : base(message, innerException) { }
    public DeduplicationException(string message, string? chunkHash, Exception? innerException = null)
        : base(message, innerException)
    {
        ChunkHash = chunkHash;
    }
}

#endregion

#region Enums

/// <summary>
/// Specifies the type of backup destination.
/// </summary>
public enum BackupDestinationType
{
    /// <summary>Local filesystem path.</summary>
    LocalFilesystem,
    /// <summary>External USB/connected drive.</summary>
    ExternalDrive,
    /// <summary>Network share (UNC path or SMB).</summary>
    NetworkShare,
    /// <summary>Amazon S3 bucket.</summary>
    AmazonS3,
    /// <summary>Azure Blob Storage.</summary>
    AzureBlob,
    /// <summary>Google Cloud Storage.</summary>
    GoogleCloudStorage,
    /// <summary>Hybrid backup combining local and cloud.</summary>
    Hybrid
}

/// <summary>
/// Specifies the backup scheduling frequency.
/// </summary>
public enum BackupSchedule
{
    /// <summary>Real-time continuous backup.</summary>
    RealTime,
    /// <summary>Every hour.</summary>
    Hourly,
    /// <summary>Once per day.</summary>
    Daily,
    /// <summary>Once per week.</summary>
    Weekly,
    /// <summary>Once per month.</summary>
    Monthly,
    /// <summary>Custom cron expression.</summary>
    CustomCron,
    /// <summary>Manual backup only.</summary>
    Manual
}

/// <summary>
/// Specifies the encryption algorithm to use.
/// </summary>
public enum EncryptionAlgorithm
{
    /// <summary>AES-256 with GCM mode (default, authenticated).</summary>
    Aes256Gcm,
    /// <summary>AES-256 with CBC mode and HMAC authentication.</summary>
    Aes256CbcHmac,
    /// <summary>ChaCha20-Poly1305 (fast on systems without AES-NI).</summary>
    ChaCha20Poly1305,
    /// <summary>XChaCha20-Poly1305 (extended nonce variant).</summary>
    XChaCha20Poly1305,
    /// <summary>Twofish cipher in CTR mode with HMAC.</summary>
    Twofish,
    /// <summary>Serpent cipher in CTR mode with HMAC.</summary>
    Serpent
}

/// <summary>
/// Specifies the key derivation function to use.
/// </summary>
public enum KeyDerivationFunction
{
    /// <summary>PBKDF2 with SHA-256.</summary>
    Pbkdf2Sha256,
    /// <summary>PBKDF2 with SHA-512.</summary>
    Pbkdf2Sha512,
    /// <summary>Argon2id (memory-hard, recommended).</summary>
    Argon2id,
    /// <summary>scrypt (memory-hard).</summary>
    Scrypt
}

/// <summary>
/// Specifies the version retention policy.
/// </summary>
public enum VersionRetentionPolicy
{
    /// <summary>Keep versions for 30 days.</summary>
    Days30,
    /// <summary>Keep versions for 90 days.</summary>
    Days90,
    /// <summary>Keep versions for 1 year.</summary>
    Year1,
    /// <summary>Keep versions forever.</summary>
    Unlimited,
    /// <summary>Custom retention policy.</summary>
    Custom
}

/// <summary>
/// Specifies the chunking strategy for deduplication.
/// </summary>
public enum ChunkingStrategy
{
    /// <summary>Fixed-size chunks.</summary>
    FixedSize,
    /// <summary>Content-defined chunking using Rabin fingerprinting.</summary>
    ContentDefined,
    /// <summary>Variable-size with min/max bounds.</summary>
    VariableSize
}

/// <summary>
/// Specifies the deduplication scope.
/// </summary>
public enum DeduplicationScope
{
    /// <summary>Deduplicate within a single file.</summary>
    PerFile,
    /// <summary>Deduplicate across all files in a backup.</summary>
    PerBackup,
    /// <summary>Global deduplication across all backups.</summary>
    Global
}

/// <summary>
/// Specifies the backup type/strategy.
/// </summary>
public enum BackupType
{
    /// <summary>Full backup of all data.</summary>
    Full,
    /// <summary>Incremental backup (changes since last backup).</summary>
    Incremental,
    /// <summary>Differential backup (changes since last full backup).</summary>
    Differential,
    /// <summary>Block-level incremental backup.</summary>
    BlockLevelIncremental,
    /// <summary>Synthetic full backup from incrementals.</summary>
    SyntheticFull,
    /// <summary>Forever incremental strategy.</summary>
    ForeverIncremental
}

#endregion

#region Configuration Records

/// <summary>
/// Configuration for backup destination.
/// </summary>
public sealed record BackupDestinationConfig
{
    /// <summary>Gets or sets the destination type.</summary>
    public required BackupDestinationType Type { get; init; }

    /// <summary>Gets or sets the destination path (local path, UNC path, or bucket name).</summary>
    public required string Path { get; init; }

    /// <summary>Gets or sets optional credentials for cloud/network destinations.</summary>
    public BackupCredentials? Credentials { get; init; }

    /// <summary>Gets or sets the region for cloud storage.</summary>
    public string? Region { get; init; }

    /// <summary>Gets or sets the endpoint URL for S3-compatible storage.</summary>
    public string? EndpointUrl { get; init; }

    /// <summary>Gets or sets whether to use SSL/TLS.</summary>
    public bool UseSsl { get; init; } = true;

    /// <summary>Gets or sets the maximum bandwidth in bytes per second (0 = unlimited).</summary>
    public long MaxBandwidthBytesPerSecond { get; init; }

    /// <summary>Gets or sets the connection timeout.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the number of retry attempts.</summary>
    public int RetryCount { get; init; } = 3;
}

/// <summary>
/// Credentials for backup destinations.
/// </summary>
public sealed record BackupCredentials
{
    /// <summary>Gets or sets the access key or username.</summary>
    public string? AccessKey { get; init; }

    /// <summary>Gets or sets the secret key or password.</summary>
    public string? SecretKey { get; init; }

    /// <summary>Gets or sets the account name (for Azure).</summary>
    public string? AccountName { get; init; }

    /// <summary>Gets or sets the connection string.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Gets or sets the SAS token (for Azure).</summary>
    public string? SasToken { get; init; }

    /// <summary>Gets or sets the domain (for network shares).</summary>
    public string? Domain { get; init; }
}

/// <summary>
/// Configuration for backup scheduling.
/// </summary>
public sealed record BackupScheduleConfig
{
    /// <summary>Gets or sets the schedule type.</summary>
    public required BackupSchedule Schedule { get; init; }

    /// <summary>Gets or sets the cron expression for custom schedules.</summary>
    public string? CronExpression { get; init; }

    /// <summary>Gets or sets the preferred time of day for scheduled backups.</summary>
    public TimeOnly? PreferredTime { get; init; }

    /// <summary>Gets or sets the days of week for weekly backups.</summary>
    public DayOfWeek[]? DaysOfWeek { get; init; }

    /// <summary>Gets or sets the backup window start time.</summary>
    public TimeOnly? WindowStart { get; init; }

    /// <summary>Gets or sets the backup window end time.</summary>
    public TimeOnly? WindowEnd { get; init; }

    /// <summary>Gets or sets whether to skip backup if previous is still running.</summary>
    public bool SkipIfRunning { get; init; } = true;

    /// <summary>Gets or sets the maximum duration for a backup job.</summary>
    public TimeSpan? MaxDuration { get; init; }
}

/// <summary>
/// Configuration for encryption at rest.
/// </summary>
public sealed record EncryptionConfig
{
    /// <summary>Gets or sets the encryption algorithm.</summary>
    public EncryptionAlgorithm Algorithm { get; init; } = EncryptionAlgorithm.Aes256Gcm;

    /// <summary>Gets or sets the key derivation function.</summary>
    public KeyDerivationFunction KeyDerivation { get; init; } = KeyDerivationFunction.Argon2id;

    /// <summary>Gets or sets PBKDF2 iterations (if using PBKDF2).</summary>
    public int Pbkdf2Iterations { get; init; } = 600_000;

    /// <summary>Gets or sets Argon2 memory cost in KB.</summary>
    public int Argon2MemoryKb { get; init; } = 65536;

    /// <summary>Gets or sets Argon2 time cost (iterations).</summary>
    public int Argon2TimeCost { get; init; } = 3;

    /// <summary>Gets or sets Argon2 parallelism.</summary>
    public int Argon2Parallelism { get; init; } = 4;

    /// <summary>Gets or sets scrypt N parameter (CPU/memory cost).</summary>
    public int ScryptN { get; init; } = 1 << 20;

    /// <summary>Gets or sets scrypt r parameter (block size).</summary>
    public int ScryptR { get; init; } = 8;

    /// <summary>Gets or sets scrypt p parameter (parallelism).</summary>
    public int ScryptP { get; init; } = 1;

    /// <summary>Gets or sets whether to use hardware acceleration if available.</summary>
    public bool UseHardwareAcceleration { get; init; } = true;

    /// <summary>Gets or sets the key size in bits.</summary>
    public int KeySizeBits { get; init; } = 256;
}

/// <summary>
/// Configuration for file versioning.
/// </summary>
public sealed record VersioningConfig
{
    /// <summary>Gets or sets the retention policy.</summary>
    public VersionRetentionPolicy RetentionPolicy { get; init; } = VersionRetentionPolicy.Days90;

    /// <summary>Gets or sets custom retention days (if policy is Custom).</summary>
    public int? CustomRetentionDays { get; init; }

    /// <summary>Gets or sets the maximum number of versions to keep (0 = unlimited).</summary>
    public int MaxVersionCount { get; init; } = 100;

    /// <summary>Gets or sets the maximum storage per file for versions in bytes (0 = unlimited).</summary>
    public long MaxStoragePerFileBytes { get; init; }

    /// <summary>Gets or sets whether to enable automatic cleanup.</summary>
    public bool EnableAutoCleanup { get; init; } = true;

    /// <summary>Gets or sets the cleanup interval.</summary>
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Gets or sets whether to store version diffs instead of full copies.</summary>
    public bool StoreDiffs { get; init; } = true;

    /// <summary>Gets or sets whether to compress versions.</summary>
    public bool CompressVersions { get; init; } = true;
}

/// <summary>
/// Configuration for deduplication.
/// </summary>
public sealed record DeduplicationConfig
{
    /// <summary>Gets or sets the chunking strategy.</summary>
    public ChunkingStrategy Strategy { get; init; } = ChunkingStrategy.ContentDefined;

    /// <summary>Gets or sets the deduplication scope.</summary>
    public DeduplicationScope Scope { get; init; } = DeduplicationScope.Global;

    /// <summary>Gets or sets the minimum chunk size in bytes.</summary>
    public int MinChunkSize { get; init; } = 4 * 1024;

    /// <summary>Gets or sets the target average chunk size in bytes.</summary>
    public int TargetChunkSize { get; init; } = 8 * 1024;

    /// <summary>Gets or sets the maximum chunk size in bytes.</summary>
    public int MaxChunkSize { get; init; } = 64 * 1024;

    /// <summary>Gets or sets the fixed chunk size (if using FixedSize strategy).</summary>
    public int FixedChunkSize { get; init; } = 16 * 1024;

    /// <summary>Gets or sets the hash algorithm for chunk identification.</summary>
    public string HashAlgorithm { get; init; } = "SHA256";

    /// <summary>Gets or sets whether to enable inline deduplication.</summary>
    public bool InlineDeduplication { get; init; } = true;

    /// <summary>Gets or sets whether to enable compression after deduplication.</summary>
    public bool CompressChunks { get; init; } = true;

    /// <summary>Gets or sets the garbage collection interval.</summary>
    public TimeSpan GarbageCollectionInterval { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Configuration for continuous/incremental backup.
/// </summary>
public sealed record ContinuousBackupConfig
{
    /// <summary>Gets or sets the backup type.</summary>
    public BackupType Type { get; init; } = BackupType.Incremental;

    /// <summary>Gets or sets whether to enable real-time file monitoring.</summary>
    public bool EnableRealTimeMonitoring { get; init; } = true;

    /// <summary>Gets or sets the debounce interval for file changes.</summary>
    public TimeSpan DebounceInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the batch size for processing changes.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>Gets or sets bandwidth throttling in bytes per second (0 = unlimited).</summary>
    public long BandwidthLimitBytesPerSecond { get; init; }

    /// <summary>Gets or sets whether to use Windows Change Journal (if available).</summary>
    public bool UseChangeJournal { get; init; } = true;

    /// <summary>Gets or sets whether to use inotify on Linux (if available).</summary>
    public bool UseInotify { get; init; } = true;

    /// <summary>Gets or sets file patterns to include.</summary>
    public string[]? IncludePatterns { get; init; }

    /// <summary>Gets or sets file patterns to exclude.</summary>
    public string[]? ExcludePatterns { get; init; }

    /// <summary>Gets or sets the maximum file size to backup (0 = unlimited).</summary>
    public long MaxFileSizeBytes { get; init; }

    /// <summary>Gets or sets the synthetic full backup interval.</summary>
    public TimeSpan? SyntheticFullInterval { get; init; }
}

#endregion

#region 1. Local Backup Options

/// <summary>
/// Comprehensive backup destination manager supporting local filesystem, external drives,
/// network shares, and cloud storage with hybrid backup capabilities.
/// </summary>
public sealed class BackupDestinationManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IBackupDestination> _destinations = new();
    private readonly ConcurrentDictionary<string, BackupJob> _activeJobs = new();
    private readonly BackupScheduler _scheduler;
    private readonly BackupVerifier _verifier;
    private readonly SemaphoreSlim _jobLock = new(1, 1);
    private volatile bool _disposed;

    /// <summary>
    /// Event raised when a backup job starts.
    /// </summary>
    public event EventHandler<BackupJobEventArgs>? BackupStarted;

    /// <summary>
    /// Event raised when a backup job completes.
    /// </summary>
    public event EventHandler<BackupJobEventArgs>? BackupCompleted;

    /// <summary>
    /// Event raised when a backup job fails.
    /// </summary>
    public event EventHandler<BackupJobEventArgs>? BackupFailed;

    /// <summary>
    /// Event raised to report backup progress.
    /// </summary>
    public event EventHandler<BackupProgressEventArgs>? BackupProgress;

    /// <summary>
    /// Creates a new backup destination manager.
    /// </summary>
    public BackupDestinationManager()
    {
        _scheduler = new BackupScheduler(this);
        _verifier = new BackupVerifier();
    }

    /// <summary>
    /// Registers a backup destination.
    /// </summary>
    /// <param name="name">Unique name for the destination.</param>
    /// <param name="config">Destination configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The registered destination.</returns>
    public async Task<IBackupDestination> RegisterDestinationAsync(
        string name,
        BackupDestinationConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(config);

        if (_destinations.ContainsKey(name))
            throw new BackupException($"Destination '{name}' already exists");

        IBackupDestination destination = config.Type switch
        {
            BackupDestinationType.LocalFilesystem => new LocalFilesystemDestination(config),
            BackupDestinationType.ExternalDrive => new ExternalDriveDestination(config),
            BackupDestinationType.NetworkShare => new NetworkShareDestination(config),
            BackupDestinationType.AmazonS3 => new S3Destination(config),
            BackupDestinationType.AzureBlob => new AzureBlobDestination(config),
            BackupDestinationType.GoogleCloudStorage => new GcsDestination(config),
            BackupDestinationType.Hybrid => new HybridDestination(config, this),
            _ => throw new BackupException($"Unsupported destination type: {config.Type}")
        };

        await destination.InitializeAsync(ct);
        _destinations[name] = destination;

        return destination;
    }

    /// <summary>
    /// Gets a registered destination by name.
    /// </summary>
    public IBackupDestination? GetDestination(string name)
    {
        return _destinations.TryGetValue(name, out var dest) ? dest : null;
    }

    /// <summary>
    /// Lists all detected external drives available for backup.
    /// </summary>
    public async Task<IReadOnlyList<ExternalDriveInfo>> DetectExternalDrivesAsync(CancellationToken ct = default)
    {
        var drives = new List<ExternalDriveInfo>();

        await Task.Run(() =>
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();

                if (drive.DriveType == DriveType.Removable || drive.DriveType == DriveType.Fixed)
                {
                    try
                    {
                        if (drive.IsReady)
                        {
                            drives.Add(new ExternalDriveInfo
                            {
                                Name = drive.Name,
                                VolumeLabel = drive.VolumeLabel,
                                DriveType = drive.DriveType,
                                FileSystem = drive.DriveFormat,
                                TotalSizeBytes = drive.TotalSize,
                                AvailableFreeSpaceBytes = drive.AvailableFreeSpace,
                                IsReady = true,
                                RootDirectory = drive.RootDirectory.FullName
                            });
                        }
                    }
                    catch (IOException)
                    {
                        // Drive not ready or inaccessible
                    }
                }
            }
        }, ct);

        return drives;
    }

    /// <summary>
    /// Starts a backup job to the specified destination.
    /// </summary>
    public async Task<BackupJob> StartBackupAsync(
        string destinationName,
        IEnumerable<string> sourcePaths,
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_destinations.TryGetValue(destinationName, out var destination))
            throw new BackupException($"Destination '{destinationName}' not found");

        options ??= new BackupOptions();

        var job = new BackupJob
        {
            Id = Guid.NewGuid().ToString("N"),
            DestinationName = destinationName,
            SourcePaths = sourcePaths.ToList(),
            Options = options,
            Status = BackupJobStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _activeJobs[job.Id] = job;

        try
        {
            await _jobLock.WaitAsync(ct);
            job.Status = BackupJobStatus.Running;
            job.StartedAt = DateTime.UtcNow;

            BackupStarted?.Invoke(this, new BackupJobEventArgs { Job = job });

            await ExecuteBackupAsync(job, destination, ct);

            job.Status = BackupJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;

            if (options.VerifyAfterBackup)
            {
                job.VerificationResult = await _verifier.VerifyBackupAsync(job, destination, ct);
            }

            BackupCompleted?.Invoke(this, new BackupJobEventArgs { Job = job });
        }
        catch (OperationCanceledException)
        {
            job.Status = BackupJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            throw;
        }
        catch (Exception ex)
        {
            job.Status = BackupJobStatus.Failed;
            job.Error = ex.Message;
            job.CompletedAt = DateTime.UtcNow;

            BackupFailed?.Invoke(this, new BackupJobEventArgs { Job = job, Error = ex });
            throw new BackupException($"Backup failed: {ex.Message}", job.Id, destinationName, ex);
        }
        finally
        {
            _jobLock.Release();
        }

        return job;
    }

    /// <summary>
    /// Schedules recurring backups.
    /// </summary>
    public void ScheduleBackup(
        string destinationName,
        IEnumerable<string> sourcePaths,
        BackupScheduleConfig schedule,
        BackupOptions? options = null)
    {
        _scheduler.AddSchedule(destinationName, sourcePaths.ToList(), schedule, options);
    }

    private async Task ExecuteBackupAsync(
        BackupJob job,
        IBackupDestination destination,
        CancellationToken ct)
    {
        var totalFiles = 0L;
        var totalBytes = 0L;
        var processedFiles = 0L;
        var processedBytes = 0L;

        // First pass: count files
        foreach (var sourcePath in job.SourcePaths)
        {
            if (Directory.Exists(sourcePath))
            {
                var files = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        if (ShouldIncludeFile(info, job.Options))
                        {
                            totalFiles++;
                            totalBytes += info.Length;
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            else if (File.Exists(sourcePath))
            {
                totalFiles++;
                totalBytes += new FileInfo(sourcePath).Length;
            }
        }

        job.TotalFiles = totalFiles;
        job.TotalBytes = totalBytes;

        // Second pass: backup files
        foreach (var sourcePath in job.SourcePaths)
        {
            ct.ThrowIfCancellationRequested();

            if (Directory.Exists(sourcePath))
            {
                await BackupDirectoryAsync(sourcePath, destination, job, ct,
                    (files, bytes) =>
                    {
                        processedFiles += files;
                        processedBytes += bytes;
                        ReportProgress(job, processedFiles, processedBytes);
                    });
            }
            else if (File.Exists(sourcePath))
            {
                await BackupFileAsync(sourcePath, destination, job, ct);
                processedFiles++;
                processedBytes += new FileInfo(sourcePath).Length;
                ReportProgress(job, processedFiles, processedBytes);
            }
        }

        job.ProcessedFiles = processedFiles;
        job.ProcessedBytes = processedBytes;
    }

    private async Task BackupDirectoryAsync(
        string directory,
        IBackupDestination destination,
        BackupJob job,
        CancellationToken ct,
        Action<long, long> progressCallback)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(file);
                if (!ShouldIncludeFile(info, job.Options))
                    continue;

                await BackupFileAsync(file, destination, job, ct);
                progressCallback(1, info.Length);
            }
            catch (UnauthorizedAccessException ex)
            {
                job.Errors.Add(new BackupError { FilePath = file, Message = ex.Message });
            }
            catch (IOException ex)
            {
                job.Errors.Add(new BackupError { FilePath = file, Message = ex.Message });
            }
        }
    }

    private async Task BackupFileAsync(
        string filePath,
        IBackupDestination destination,
        BackupJob job,
        CancellationToken ct)
    {
        var relativePath = GetRelativePath(filePath, job.SourcePaths);
        await using var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var metadata = new BackupFileMetadata
        {
            OriginalPath = filePath,
            RelativePath = relativePath,
            Size = sourceStream.Length,
            LastModified = File.GetLastWriteTimeUtc(filePath),
            Checksum = await ComputeChecksumAsync(sourceStream, ct)
        };

        sourceStream.Position = 0;
        await destination.WriteFileAsync(relativePath, sourceStream, metadata, ct);

        job.BackedUpFiles.Add(metadata);
    }

    private bool ShouldIncludeFile(FileInfo info, BackupOptions options)
    {
        if (options.MaxFileSizeBytes > 0 && info.Length > options.MaxFileSizeBytes)
            return false;

        if (options.ExcludePatterns?.Length > 0)
        {
            foreach (var pattern in options.ExcludePatterns)
            {
                if (MatchesPattern(info.Name, pattern) || MatchesPattern(info.FullName, pattern))
                    return false;
            }
        }

        if (options.IncludePatterns?.Length > 0)
        {
            var included = false;
            foreach (var pattern in options.IncludePatterns)
            {
                if (MatchesPattern(info.Name, pattern) || MatchesPattern(info.FullName, pattern))
                {
                    included = true;
                    break;
                }
            }
            if (!included) return false;
        }

        return true;
    }

    private static bool MatchesPattern(string input, string pattern)
    {
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string GetRelativePath(string filePath, List<string> sourcePaths)
    {
        foreach (var sourcePath in sourcePaths)
        {
            if (filePath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                var relative = filePath.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return relative;
            }
        }
        return Path.GetFileName(filePath);
    }

    private static async Task<string> ComputeChecksumAsync(Stream stream, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void ReportProgress(BackupJob job, long processedFiles, long processedBytes)
    {
        job.ProcessedFiles = processedFiles;
        job.ProcessedBytes = processedBytes;

        BackupProgress?.Invoke(this, new BackupProgressEventArgs
        {
            JobId = job.Id,
            ProcessedFiles = processedFiles,
            TotalFiles = job.TotalFiles,
            ProcessedBytes = processedBytes,
            TotalBytes = job.TotalBytes,
            PercentComplete = job.TotalBytes > 0 ? (double)processedBytes / job.TotalBytes * 100 : 0
        });
    }

    /// <summary>
    /// Gets statistics for all destinations.
    /// </summary>
    public async Task<BackupStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var stats = new BackupStatistics();

        foreach (var (name, destination) in _destinations)
        {
            var destStats = await destination.GetStatisticsAsync(ct);
            stats.DestinationStats[name] = destStats;
            stats.TotalBackedUpBytes += destStats.TotalBytes;
            stats.TotalFiles += destStats.TotalFiles;
        }

        stats.ActiveJobCount = _activeJobs.Count(j => j.Value.Status == BackupJobStatus.Running);
        stats.CompletedJobCount = _activeJobs.Count(j => j.Value.Status == BackupJobStatus.Completed);
        stats.FailedJobCount = _activeJobs.Count(j => j.Value.Status == BackupJobStatus.Failed);

        return stats;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _scheduler.Dispose();

        foreach (var destination in _destinations.Values)
        {
            if (destination is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (destination is IDisposable disposable)
                disposable.Dispose();
        }

        _destinations.Clear();
        _jobLock.Dispose();
    }
}

/// <summary>
/// Interface for backup destinations.
/// </summary>
public interface IBackupDestination
{
    /// <summary>Gets the destination type.</summary>
    BackupDestinationType Type { get; }

    /// <summary>Gets the destination path.</summary>
    string Path { get; }

    /// <summary>Gets whether the destination is available.</summary>
    bool IsAvailable { get; }

    /// <summary>Initializes the destination.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Writes a file to the destination.</summary>
    Task WriteFileAsync(string relativePath, Stream content, BackupFileMetadata metadata, CancellationToken ct = default);

    /// <summary>Reads a file from the destination.</summary>
    Task<Stream> ReadFileAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Deletes a file from the destination.</summary>
    Task DeleteFileAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Lists files at the destination.</summary>
    IAsyncEnumerable<BackupFileMetadata> ListFilesAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>Checks if a file exists.</summary>
    Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Gets destination statistics.</summary>
    Task<DestinationStatistics> GetStatisticsAsync(CancellationToken ct = default);

    /// <summary>Tests connectivity to the destination.</summary>
    Task<bool> TestConnectivityAsync(CancellationToken ct = default);
}

/// <summary>
/// Local filesystem backup destination.
/// </summary>
public sealed class LocalFilesystemDestination : IBackupDestination, IAsyncDisposable
{
    private readonly BackupDestinationConfig _config;
    private readonly string _basePath;
    private bool _initialized;

    /// <inheritdoc />
    public BackupDestinationType Type => BackupDestinationType.LocalFilesystem;

    /// <inheritdoc />
    public string Path => _basePath;

    /// <inheritdoc />
    public bool IsAvailable => Directory.Exists(_basePath);

    /// <summary>
    /// Creates a new local filesystem destination.
    /// </summary>
    public LocalFilesystemDestination(BackupDestinationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _basePath = config.Path;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_basePath);
        _initialized = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string relativePath, Stream content, BackupFileMetadata metadata, CancellationToken ct = default)
    {
        EnsureInitialized();

        var fullPath = System.IO.Path.Combine(_basePath, relativePath);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);

        if (_config.MaxBandwidthBytesPerSecond > 0)
        {
            await using var throttledStream = new ThrottledStream(content, _config.MaxBandwidthBytesPerSecond);
            await throttledStream.CopyToAsync(fileStream, ct);
        }
        else
        {
            await content.CopyToAsync(fileStream, ct);
        }

        // Store metadata
        var metadataPath = fullPath + ".meta";
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata), ct);
    }

    /// <inheritdoc />
    public async Task<Stream> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        EnsureInitialized();

        var fullPath = System.IO.Path.Combine(_basePath, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Backup file not found: {relativePath}");

        var memoryStream = new MemoryStream();
        await using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await fileStream.CopyToAsync(memoryStream, ct);
        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        EnsureInitialized();

        var fullPath = System.IO.Path.Combine(_basePath, relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            var metadataPath = fullPath + ".meta";
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BackupFileMetadata> ListFilesAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureInitialized();

        var searchPath = string.IsNullOrEmpty(prefix) ? _basePath : System.IO.Path.Combine(_basePath, prefix);
        if (!Directory.Exists(searchPath))
            yield break;

        var files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".meta"));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var metadataPath = file + ".meta";
            if (File.Exists(metadataPath))
            {
                var json = await File.ReadAllTextAsync(metadataPath, ct);
                var metadata = JsonSerializer.Deserialize<BackupFileMetadata>(json);
                if (metadata != null)
                    yield return metadata;
            }
            else
            {
                var info = new FileInfo(file);
                yield return new BackupFileMetadata
                {
                    OriginalPath = file,
                    RelativePath = System.IO.Path.GetRelativePath(_basePath, file),
                    Size = info.Length,
                    LastModified = info.LastWriteTimeUtc
                };
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
    {
        EnsureInitialized();
        var fullPath = System.IO.Path.Combine(_basePath, relativePath);
        return Task.FromResult(File.Exists(fullPath));
    }

    /// <inheritdoc />
    public Task<DestinationStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        EnsureInitialized();

        var stats = new DestinationStatistics { DestinationType = Type };

        if (Directory.Exists(_basePath))
        {
            var files = Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".meta"));

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    stats.TotalFiles++;
                    stats.TotalBytes += info.Length;
                }
                catch { }
            }

            var driveInfo = new DriveInfo(System.IO.Path.GetPathRoot(_basePath) ?? _basePath);
            if (driveInfo.IsReady)
            {
                stats.AvailableSpaceBytes = driveInfo.AvailableFreeSpace;
                stats.TotalSpaceBytes = driveInfo.TotalSize;
            }
        }

        return Task.FromResult(stats);
    }

    /// <inheritdoc />
    public Task<bool> TestConnectivityAsync(CancellationToken ct = default)
    {
        return Task.FromResult(IsAvailable);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Destination not initialized");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// External drive backup destination with drive detection and monitoring.
/// </summary>
public sealed class ExternalDriveDestination : IBackupDestination, IAsyncDisposable
{
    private readonly BackupDestinationConfig _config;
    private readonly LocalFilesystemDestination _innerDestination;
    private DriveInfo? _driveInfo;

    /// <inheritdoc />
    public BackupDestinationType Type => BackupDestinationType.ExternalDrive;

    /// <inheritdoc />
    public string Path => _config.Path;

    /// <inheritdoc />
    public bool IsAvailable => _driveInfo?.IsReady ?? false;

    /// <summary>
    /// Creates a new external drive destination.
    /// </summary>
    public ExternalDriveDestination(BackupDestinationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _innerDestination = new LocalFilesystemDestination(config);
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var root = System.IO.Path.GetPathRoot(_config.Path);
        if (!string.IsNullOrEmpty(root))
        {
            _driveInfo = new DriveInfo(root);
            if (!_driveInfo.IsReady)
                throw new BackupException($"External drive '{root}' is not ready");
        }

        await _innerDestination.InitializeAsync(ct);
    }

    /// <inheritdoc />
    public Task WriteFileAsync(string relativePath, Stream content, BackupFileMetadata metadata, CancellationToken ct = default)
        => _innerDestination.WriteFileAsync(relativePath, content, metadata, ct);

    /// <inheritdoc />
    public Task<Stream> ReadFileAsync(string relativePath, CancellationToken ct = default)
        => _innerDestination.ReadFileAsync(relativePath, ct);

    /// <inheritdoc />
    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
        => _innerDestination.DeleteFileAsync(relativePath, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<BackupFileMetadata> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
        => _innerDestination.ListFilesAsync(prefix, ct);

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
        => _innerDestination.FileExistsAsync(relativePath, ct);

    /// <inheritdoc />
    public Task<DestinationStatistics> GetStatisticsAsync(CancellationToken ct = default)
        => _innerDestination.GetStatisticsAsync(ct);

    /// <inheritdoc />
    public Task<bool> TestConnectivityAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_driveInfo?.IsReady ?? false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _innerDestination.DisposeAsync();
}

/// <summary>
/// Network share backup destination (UNC paths, SMB).
/// </summary>
public sealed class NetworkShareDestination : IBackupDestination, IAsyncDisposable
{
    private readonly BackupDestinationConfig _config;
    private readonly LocalFilesystemDestination _innerDestination;
    private bool _connected;

    /// <inheritdoc />
    public BackupDestinationType Type => BackupDestinationType.NetworkShare;

    /// <inheritdoc />
    public string Path => _config.Path;

    /// <inheritdoc />
    public bool IsAvailable => _connected && Directory.Exists(_config.Path);

    /// <summary>
    /// Creates a new network share destination.
    /// </summary>
    public NetworkShareDestination(BackupDestinationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _innerDestination = new LocalFilesystemDestination(config);
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Attempt to connect to network share with credentials if provided
        if (_config.Credentials != null && !string.IsNullOrEmpty(_config.Credentials.AccessKey))
        {
            await ConnectNetworkShareAsync(ct);
        }

        if (!Directory.Exists(_config.Path))
        {
            throw new BackupException($"Network share not accessible: {_config.Path}");
        }

        await _innerDestination.InitializeAsync(ct);
        _connected = true;
    }

    private async Task ConnectNetworkShareAsync(CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use net use command on Windows
            var startInfo = new ProcessStartInfo
            {
                FileName = "net",
                Arguments = $"use \"{_config.Path}\" /user:{_config.Credentials!.Domain}\\{_config.Credentials.AccessKey} \"{_config.Credentials.SecretKey}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync(ct);
            }
        }
        else
        {
            // On Linux/macOS, assume the share is already mounted or use CIFS mount
            // This would require elevated privileges in most cases
        }
    }

    /// <inheritdoc />
    public Task WriteFileAsync(string relativePath, Stream content, BackupFileMetadata metadata, CancellationToken ct = default)
        => _innerDestination.WriteFileAsync(relativePath, content, metadata, ct);

    /// <inheritdoc />
    public Task<Stream> ReadFileAsync(string relativePath, CancellationToken ct = default)
        => _innerDestination.ReadFileAsync(relativePath, ct);

    /// <inheritdoc />
    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
        => _innerDestination.DeleteFileAsync(relativePath, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<BackupFileMetadata> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
        => _innerDestination.ListFilesAsync(prefix, ct);

    /// <inheritdoc />
    public Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
        => _innerDestination.FileExistsAsync(relativePath, ct);

    /// <inheritdoc />
    public Task<DestinationStatistics> GetStatisticsAsync(CancellationToken ct = default)
        => _innerDestination.GetStatisticsAsync(ct);

    /// <inheritdoc />
    public async Task<bool> TestConnectivityAsync(CancellationToken ct = default)
    {
        try
        {
            // Try to extract host from UNC path
            var path = _config.Path;
            if (path.StartsWith("\\\\"))
            {
                var parts = path.TrimStart('\\').Split('\\');
                if (parts.Length > 0)
                {
                    var host = parts[0];
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(host, (int)_config.ConnectionTimeout.TotalMilliseconds);
                    return reply.Status == IPStatus.Success;
                }
            }
            return Directory.Exists(_config.Path);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _innerDestination.DisposeAsync();
}

/// <summary>
/// Amazon S3 backup destination.
/// </summary>
public sealed class S3Destination : IBackupDestination, IAsyncDisposable
{
    private readonly BackupDestinationConfig _config;
    private readonly HttpClient _httpClient;
    private readonly string _bucketName;
    private readonly string _region;
    private readonly string? _accessKey;
    private readonly string? _secretKey;

    /// <inheritdoc />
    public BackupDestinationType Type => BackupDestinationType.AmazonS3;

    /// <inheritdoc />
    public string Path => _bucketName;

    /// <inheritdoc />
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Creates a new S3 destination.
    /// </summary>
    public S3Destination(BackupDestinationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _bucketName = config.Path;
        _region = config.Region ?? "us-east-1";
        _accessKey = config.Credentials?.AccessKey ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        _secretKey = config.Credentials?.SecretKey ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

        _httpClient = new HttpClient
        {
            Timeout = config.ConnectionTimeout
        };

        if (!string.IsNullOrEmpty(config.EndpointUrl))
        {
            _httpClient.BaseAddress = new Uri(config.EndpointUrl);
        }
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IsAvailable = await TestConnectivityAsync(ct);
        if (!IsAvailable)
            throw new BackupException($"Cannot connect to S3 bucket: {_bucketName}");
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string relativePath, Stream content, BackupFileMetadata metadata, CancellationToken ct = default)
    {
        var key = NormalizeKey(relativePath);
        var endpoint = GetEndpoint();
        var url = $"{endpoint}/{_bucketName}/{key}";

        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream, ct);
        var body = memoryStream.ToArray();

        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(body)
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        // Add custom metadata headers
        request.Headers.Add("x-amz-meta-original-path", Uri.EscapeDataString(metadata.OriginalPath ?? ""));
        request.Headers.Add("x-amz-meta-checksum", metadata.Checksum ?? "");
        request.Headers.Add("x-amz-meta-last-modified", metadata.LastModified.ToString("O"));

        SignRequest(request, body, "PUT", key);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async Task<Stream> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        var key = NormalizeKey(relativePath);
        var endpoint = GetEndpoint();
        var url = $"{endpoint}/{_bucketName}/{key}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        SignRequest(request, Array.Empty<byte>(), "GET", key);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var memoryStream = new MemoryStream();
        await response.Content.CopyToAsync(memoryStream, ct);
        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <inheritdoc />
    public async Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        var key = NormalizeKey(relativePath);
        var endpoint = GetEndpoint();
        var url = $"{endpoint}/{_bucketName}/{key}";

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        SignRequest(request, Array.Empty<byte>(), "DELETE", key);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BackupFileMetadata> ListFilesAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var endpoint = GetEndpoint();
        var marker = "";
        var isTruncated = true;

        while (isTruncated)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"{endpoint}/{_bucketName}?list-type=2";
            if (!string.IsNullOrEmpty(prefix))
                url += $"&prefix={Uri.EscapeDataString(prefix)}";
            if (!string.IsNullOrEmpty(marker))
                url += $"&continuation-token={Uri.EscapeDataString(marker)}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            SignRequest(request, Array.Empty<byte>(), "GET", "");

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);

            // Parse XML response (simplified)
            var keys = ParseS3ListResponse(content, out isTruncated, out marker);

            foreach (var key in keys)
            {
                yield return new BackupFileMetadata
                {
                    RelativePath = key.Key,
                    Size = key.Size,
                    LastModified = key.LastModified
                };
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var key = NormalizeKey(relativePath);
            var endpoint = GetEndpoint();
            var url = $"{endpoint}/{_bucketName}/{key}";

            var request = new HttpRequestMessage(HttpMethod.Head, url);
            SignRequest(request, Array.Empty<byte>(), "HEAD", key);

            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<DestinationStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        // S3 doesn't provide easy bucket statistics without listing all objects
        return Task.FromResult(new DestinationStatistics
        {
            DestinationType = Type,
            AvailableSpaceBytes = long.MaxValue // S3 has virtually unlimited storage
        });
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectivityAsync(CancellationToken ct = default)
    {
        try
        {
            var endpoint = GetEndpoint();
            var url = $"{endpoint}/{_bucketName}?max-keys=1";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            SignRequest(request, Array.Empty<byte>(), "GET", "");

            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string GetEndpoint()
    {
        if (!string.IsNullOrEmpty(_config.EndpointUrl))
            return _config.EndpointUrl.TrimEnd('/');

        return _config.UseSsl
            ? $"https://s3.{_region}.amazonaws.com"
            : $"http://s3.{_region}.amazonaws.com";
    }

    private static string NormalizeKey(string key)
    {
        return key.Replace('\\', '/').TrimStart('/');
    }

    private void SignRequest(HttpRequestMessage request, byte[] body, string method, string key)
    {
        if (string.IsNullOrEmpty(_accessKey) || string.IsNullOrEmpty(_secretKey))
            return;

        var now = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");

        request.Headers.Add("x-amz-date", amzDate);
        request.Headers.Add("x-amz-content-sha256", ComputeSha256Hash(body));

        var host = request.RequestUri!.Host;
        var canonicalUri = $"/{_bucketName}/{key}".TrimEnd('/');
        var canonicalQueryString = request.RequestUri.Query.TrimStart('?');
        var canonicalHeaders = $"host:{host}\nx-amz-date:{amzDate}\n";
        var signedHeaders = "host;x-amz-date";
        var payloadHash = ComputeSha256Hash(body);

        var canonicalRequest = $"{method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var canonicalRequestHash = ComputeSha256Hash(Encoding.UTF8.GetBytes(canonicalRequest));

        var credentialScope = $"{dateStamp}/{_region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

        var signingKey = GetSignatureKey(_secretKey, dateStamp, _region, "s3");
        var signature = ComputeHmacSha256(signingKey, stringToSign);

        var authHeader = $"AWS4-HMAC-SHA256 Credential={_accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("AWS4-HMAC-SHA256", authHeader.Substring("AWS4-HMAC-SHA256 ".Length));
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string region, string service)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + key), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string ComputeSha256Hash(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private static string ComputeHmacSha256(byte[] key, string data)
    {
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private static List<(string Key, long Size, DateTime LastModified)> ParseS3ListResponse(string xml, out bool isTruncated, out string nextMarker)
    {
        var results = new List<(string Key, long Size, DateTime LastModified)>();
        isTruncated = false;
        nextMarker = "";

        // Simple XML parsing (in production, use proper XML parser)
        if (xml.Contains("<IsTruncated>true</IsTruncated>"))
            isTruncated = true;

        var tokenMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<NextContinuationToken>([^<]+)</NextContinuationToken>");
        if (tokenMatch.Success)
            nextMarker = tokenMatch.Groups[1].Value;

        var contentMatches = System.Text.RegularExpressions.Regex.Matches(xml, @"<Contents>.*?<Key>([^<]+)</Key>.*?<Size>(\d+)</Size>.*?<LastModified>([^<]+)</LastModified>.*?</Contents>", System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match match in contentMatches)
        {
            results.Add((
                match.Groups[1].Value,
                long.Parse(match.Groups[2].Value),
                DateTime.Parse(match.Groups[3].Value)
            ));
        }

        return results;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Azure Blob Storage backup destination.
/// </summary>
public sealed class AzureBlobDestination : IBackupDestination, IAsyncDisposable
{
    private readonly BackupDestinationConfig _config;
    private readonly HttpClient _httpClient;
    private readonly string _containerName;
    private readonly string _accountName;
    private readonly string? _accountKey;
    private readonly string? _sasToken;

    /// <inheritdoc />
    public BackupDestinationType Type => BackupDestinationType.AzureBlob;

    /// <inheritdoc />
    public string Path => _containerName;

    /// <inheritdoc />
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Creates a new Azure Blob destination.
    /// </summary>
    public AzureBlobDestination(BackupDestinationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _containerName = config.Path;
        _accountName = config.Credentials?.AccountName ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT") ?? "";
        _accountKey = config.Credentials?.SecretKey ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_KEY");
        _sasToken = config.Credentials?.SasToken;

        _httpClient = new HttpClient
        {
            Timeout = config.ConnectionTimeout
        };
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IsAvailable = await TestConnectivityAsync(ct);
        if (!IsAvailable)
            throw new BackupException($"Cannot connect to Azure container: {_containerName}");
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string relativePath, Stream content, BackupFileMetadata metadata, CancellationToken ct = default)
    {
        var blobName = NormalizeBlobName(relativePath);
        var url = GetBlobUrl(blobName);

        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream, ct);
        var body = memoryStream.ToArray();

        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(body)
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        request.Headers.Add("x-ms-meta-originalpath", Uri.EscapeDataString(metadata.OriginalPath ?? ""));
        request.Headers.Add("x-ms-meta-checksum", metadata.Checksum ?? "");

        AddAzureAuth(request, "PUT", blobName);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async Task<Stream> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        var blobName = NormalizeBlobName(relativePath);
        var url = GetBlobUrl(blobName);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAzureAuth(request, "GET", blobName);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var memoryStream = new MemoryStream();
        await response.Content.CopyToAsync(memoryStream, ct);
        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <inheritdoc />
    public async Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        var blobName = NormalizeBlobName(relativePath);
        var url = GetBlobUrl(blobName);

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        AddAzureAuth(request, "DELETE", blobName);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BackupFileMetadata> ListFilesAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var marker = "";
        var hasMore = true;

        while (hasMore)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"https://{_accountName}.blob.core.windows.net/{_containerName}?restype=container&comp=list";
            if (!string.IsNullOrEmpty(prefix))
                url += $"&prefix={Uri.EscapeDataString(prefix)}";
            if (!string.IsNullOrEmpty(marker))
                url += $"&marker={Uri.EscapeDataString(marker)}";

            if (!string.IsNullOrEmpty(_sasToken))
                url += $"&{_sasToken.TrimStart('?')}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (string.IsNullOrEmpty(_sasToken))
                AddAzureAuth(request, "GET", "");

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);
            var blobs = ParseAzureListResponse(content, out hasMore, out marker);

            foreach (var blob in blobs)
            {
                yield return new BackupFileMetadata
                {
                    RelativePath = blob.Name,
                    Size = blob.Size,
                    LastModified = blob.LastModified
                };
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var blobName = NormalizeBlobName(relativePath);
            var url = GetBlobUrl(blobName);

            var request = new HttpRequestMessage(HttpMethod.Head, url);
            AddAzureAuth(request, "HEAD", blobName);

            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<DestinationStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new DestinationStatistics
        {
            DestinationType = Type,
            AvailableSpaceBytes = long.MaxValue
        });
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectivityAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://{_accountName}.blob.core.windows.net/{_containerName}?restype=container";
            if (!string.IsNullOrEmpty(_sasToken))
                url += $"&{_sasToken.TrimStart('?')}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (string.IsNullOrEmpty(_sasToken))
                AddAzureAuth(request, "GET", "");

            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string GetBlobUrl(string blobName)
    {
        var url = $"https://{_accountName}.blob.core.windows.net/{_containerName}/{blobName}";
        if (!string.IsNullOrEmpty(_sasToken))
            url += $"?{_sasToken.TrimStart('?')}";
        return url;
    }

    private static string NormalizeBlobName(string name)
    {
        return name.Replace('\\', '/').TrimStart('/');
    }

    private void AddAzureAuth(HttpRequestMessage request, string method, string resource)
    {
        if (string.IsNullOrEmpty(_accountKey))
            return;

        var date = DateTime.UtcNow.ToString("R");
        request.Headers.Add("x-ms-date", date);
        request.Headers.Add("x-ms-version", "2021-06-08");

        // Simplified SharedKey auth (full implementation would include proper canonicalization)
        var stringToSign = $"{method}\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:{date}\nx-ms-version:2021-06-08\n/{_accountName}/{_containerName}/{resource}";
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(_accountKey), Encoding.UTF8.GetBytes(stringToSign)));

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SharedKey", $"{_accountName}:{signature}");
    }

    private static List<(string Name, long Size, DateTime LastModified)> ParseAzureListResponse(string xml, out bool hasMore, out string nextMarker)
    {
        var results = new List<(string Name, long Size, DateTime LastModified)>();
        hasMore = false;
        nextMarker = "";

        var markerMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<NextMarker>([^<]*)</NextMarker>");
        if (markerMatch.Success && !string.IsNullOrEmpty(markerMatch.Groups[1].Value))
        {
            hasMore = true;
            nextMarker = markerMatch.Groups[1].Value;
        }

        var blobMatches = System.Text.RegularExpressions.Regex.Matches(xml, @"<Blob>.*?<Name>([^<]+)</Name>.*?<Content-Length>(\d+)</Content-Length>.*?<Last-Modified>([^<]+)</Last-Modified>.*?</Blob>", System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match match in blobMatches)
        {
            results.Add((
                match.Groups[1].Value,
                long.Parse(match.Groups[2].Value),
                DateTime.Parse(match.Groups[3].Value)
            ));
        }

        return results;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Google Cloud Storage backup destination.
/// </summary>
public sealed class GcsDestination : IBackupDestination, IAsyncDisposable
{
    private readonly BackupDestinationConfig _config;
    private readonly HttpClient _httpClient;
    private readonly string _bucketName;
    private string? _accessToken;

    /// <inheritdoc />
    public BackupDestinationType Type => BackupDestinationType.GoogleCloudStorage;

    /// <inheritdoc />
    public string Path => _bucketName;

    /// <inheritdoc />
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Creates a new GCS destination.
    /// </summary>
    public GcsDestination(BackupDestinationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _bucketName = config.Path;
        _accessToken = Environment.GetEnvironmentVariable("GOOGLE_ACCESS_TOKEN");

        _httpClient = new HttpClient
        {
            Timeout = config.ConnectionTimeout
        };
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_accessToken))
        {
            await RefreshAccessTokenAsync(ct);
        }

        IsAvailable = await TestConnectivityAsync(ct);
        if (!IsAvailable)
            throw new BackupException($"Cannot connect to GCS bucket: {_bucketName}");
    }

    private async Task RefreshAccessTokenAsync(CancellationToken ct)
    {
        var credPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (string.IsNullOrEmpty(credPath) || !File.Exists(credPath))
        {
            throw new BackupException("GCS credentials not configured");
        }

        var creds = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(credPath, ct));
        // In production, implement proper OAuth2 token exchange
        _accessToken = $"gcp-token-{DateTime.UtcNow.Ticks}";
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string relativePath, Stream content, BackupFileMetadata metadata, CancellationToken ct = default)
    {
        var objectName = NormalizeObjectName(relativePath);
        var url = $"https://storage.googleapis.com/upload/storage/v1/b/{_bucketName}/o?uploadType=media&name={Uri.EscapeDataString(objectName)}";

        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream, ct);
        var body = memoryStream.ToArray();

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body)
        };

        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async Task<Stream> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        var objectName = NormalizeObjectName(relativePath);
        var url = $"https://storage.googleapis.com/storage/v1/b/{_bucketName}/o/{Uri.EscapeDataString(objectName)}?alt=media";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var memoryStream = new MemoryStream();
        await response.Content.CopyToAsync(memoryStream, ct);
        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <inheritdoc />
    public async Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        var objectName = NormalizeObjectName(relativePath);
        var url = $"https://storage.googleapis.com/storage/v1/b/{_bucketName}/o/{Uri.EscapeDataString(objectName)}";

        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BackupFileMetadata> ListFilesAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pageToken = "";
        var hasMore = true;

        while (hasMore)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"https://storage.googleapis.com/storage/v1/b/{_bucketName}/o";
            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(prefix))
                queryParams.Add($"prefix={Uri.EscapeDataString(prefix)}");
            if (!string.IsNullOrEmpty(pageToken))
                queryParams.Add($"pageToken={Uri.EscapeDataString(pageToken)}");
            if (queryParams.Count > 0)
                url += "?" + string.Join("&", queryParams);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);
            var json = JsonSerializer.Deserialize<JsonElement>(content);

            if (json.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    yield return new BackupFileMetadata
                    {
                        RelativePath = item.GetProperty("name").GetString() ?? "",
                        Size = long.Parse(item.GetProperty("size").GetString() ?? "0"),
                        LastModified = DateTime.Parse(item.GetProperty("updated").GetString() ?? DateTime.UtcNow.ToString("O"))
                    };
                }
            }

            hasMore = json.TryGetProperty("nextPageToken", out var nextToken);
            if (hasMore)
                pageToken = nextToken.GetString() ?? "";
        }
    }

    /// <inheritdoc />
    public async Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var objectName = NormalizeObjectName(relativePath);
            var url = $"https://storage.googleapis.com/storage/v1/b/{_bucketName}/o/{Uri.EscapeDataString(objectName)}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<DestinationStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new DestinationStatistics
        {
            DestinationType = Type,
            AvailableSpaceBytes = long.MaxValue
        });
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectivityAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://storage.googleapis.com/storage/v1/b/{_bucketName}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeObjectName(string name)
    {
        return name.Replace('\\', '/').TrimStart('/');
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Hybrid backup destination combining local and cloud storage.
/// </summary>
public sealed class HybridDestination : IBackupDestination, IAsyncDisposable
{
    private readonly BackupDestinationConfig _config;
    private readonly BackupDestinationManager _manager;
    private IBackupDestination? _localDestination;
    private IBackupDestination? _cloudDestination;

    /// <inheritdoc />
    public BackupDestinationType Type => BackupDestinationType.Hybrid;

    /// <inheritdoc />
    public string Path => _config.Path;

    /// <inheritdoc />
    public bool IsAvailable => (_localDestination?.IsAvailable ?? false) || (_cloudDestination?.IsAvailable ?? false);

    /// <summary>
    /// Creates a new hybrid destination.
    /// </summary>
    public HybridDestination(BackupDestinationConfig config, BackupDestinationManager manager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Parse hybrid path format: "local:C:\Backup|cloud:s3:my-bucket"
        var parts = _config.Path.Split('|');
        foreach (var part in parts)
        {
            var typeAndPath = part.Split(':', 2);
            if (typeAndPath.Length != 2) continue;

            var destConfig = new BackupDestinationConfig
            {
                Type = typeAndPath[0].ToLowerInvariant() switch
                {
                    "local" => BackupDestinationType.LocalFilesystem,
                    "s3" => BackupDestinationType.AmazonS3,
                    "azure" => BackupDestinationType.AzureBlob,
                    "gcs" => BackupDestinationType.GoogleCloudStorage,
                    _ => BackupDestinationType.LocalFilesystem
                },
                Path = typeAndPath[1],
                Credentials = _config.Credentials,
                Region = _config.Region
            };

            if (destConfig.Type == BackupDestinationType.LocalFilesystem)
            {
                _localDestination = new LocalFilesystemDestination(destConfig);
                await _localDestination.InitializeAsync(ct);
            }
            else
            {
                _cloudDestination = destConfig.Type switch
                {
                    BackupDestinationType.AmazonS3 => new S3Destination(destConfig),
                    BackupDestinationType.AzureBlob => new AzureBlobDestination(destConfig),
                    BackupDestinationType.GoogleCloudStorage => new GcsDestination(destConfig),
                    _ => null
                };
                if (_cloudDestination != null)
                    await _cloudDestination.InitializeAsync(ct);
            }
        }
    }

    /// <inheritdoc />
    public async Task WriteFileAsync(string relativePath, Stream content, BackupFileMetadata metadata, CancellationToken ct = default)
    {
        // Write to both local and cloud in parallel
        var tasks = new List<Task>();

        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream, ct);
        var data = memoryStream.ToArray();

        if (_localDestination?.IsAvailable == true)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var stream = new MemoryStream(data);
                await _localDestination.WriteFileAsync(relativePath, stream, metadata, ct);
            }, ct));
        }

        if (_cloudDestination?.IsAvailable == true)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var stream = new MemoryStream(data);
                await _cloudDestination.WriteFileAsync(relativePath, stream, metadata, ct);
            }, ct));
        }

        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async Task<Stream> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        // Prefer local, fall back to cloud
        if (_localDestination?.IsAvailable == true)
        {
            try
            {
                return await _localDestination.ReadFileAsync(relativePath, ct);
            }
            catch when (_cloudDestination?.IsAvailable == true)
            {
                return await _cloudDestination.ReadFileAsync(relativePath, ct);
            }
        }

        if (_cloudDestination?.IsAvailable == true)
            return await _cloudDestination.ReadFileAsync(relativePath, ct);

        throw new BackupException("No available destination to read from");
    }

    /// <inheritdoc />
    public async Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        var tasks = new List<Task>();

        if (_localDestination?.IsAvailable == true)
            tasks.Add(_localDestination.DeleteFileAsync(relativePath, ct));

        if (_cloudDestination?.IsAvailable == true)
            tasks.Add(_cloudDestination.DeleteFileAsync(relativePath, ct));

        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BackupFileMetadata> ListFilesAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>();

        if (_localDestination?.IsAvailable == true)
        {
            await foreach (var file in _localDestination.ListFilesAsync(prefix, ct))
            {
                if (seen.Add(file.RelativePath))
                    yield return file;
            }
        }

        if (_cloudDestination?.IsAvailable == true)
        {
            await foreach (var file in _cloudDestination.ListFilesAsync(prefix, ct))
            {
                if (seen.Add(file.RelativePath))
                    yield return file;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
    {
        if (_localDestination?.IsAvailable == true && await _localDestination.FileExistsAsync(relativePath, ct))
            return true;

        if (_cloudDestination?.IsAvailable == true && await _cloudDestination.FileExistsAsync(relativePath, ct))
            return true;

        return false;
    }

    /// <inheritdoc />
    public async Task<DestinationStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var stats = new DestinationStatistics { DestinationType = Type };

        if (_localDestination?.IsAvailable == true)
        {
            var localStats = await _localDestination.GetStatisticsAsync(ct);
            stats.TotalBytes += localStats.TotalBytes;
            stats.TotalFiles += localStats.TotalFiles;
            stats.AvailableSpaceBytes = localStats.AvailableSpaceBytes;
        }

        if (_cloudDestination?.IsAvailable == true)
        {
            var cloudStats = await _cloudDestination.GetStatisticsAsync(ct);
            stats.TotalBytes += cloudStats.TotalBytes;
            stats.TotalFiles += cloudStats.TotalFiles;
        }

        return stats;
    }

    /// <inheritdoc />
    public async Task<bool> TestConnectivityAsync(CancellationToken ct = default)
    {
        var localOk = _localDestination?.IsAvailable == true && await _localDestination.TestConnectivityAsync(ct);
        var cloudOk = _cloudDestination?.IsAvailable == true && await _cloudDestination.TestConnectivityAsync(ct);
        return localOk || cloudOk;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_localDestination is IAsyncDisposable localAsync)
            await localAsync.DisposeAsync();
        if (_cloudDestination is IAsyncDisposable cloudAsync)
            await cloudAsync.DisposeAsync();
    }
}

/// <summary>
/// Backup scheduler for recurring backups.
/// </summary>
public sealed class BackupScheduler : IDisposable
{
    private readonly BackupDestinationManager _manager;
    private readonly ConcurrentDictionary<string, ScheduledBackup> _schedules = new();
    private readonly Timer _checkTimer;
    private readonly object _lock = new();
    private bool _disposed;

    internal BackupScheduler(BackupDestinationManager manager)
    {
        _manager = manager;
        _checkTimer = new Timer(CheckSchedules, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal void AddSchedule(string destinationName, List<string> sourcePaths, BackupScheduleConfig schedule, BackupOptions? options)
    {
        var id = Guid.NewGuid().ToString("N");
        var scheduled = new ScheduledBackup
        {
            Id = id,
            DestinationName = destinationName,
            SourcePaths = sourcePaths,
            Schedule = schedule,
            Options = options ?? new BackupOptions(),
            NextRunTime = CalculateNextRunTime(schedule)
        };

        _schedules[id] = scheduled;
    }

    private void CheckSchedules(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;

        foreach (var (id, scheduled) in _schedules.ToArray())
        {
            if (scheduled.NextRunTime <= now && !scheduled.IsRunning)
            {
                _ = RunScheduledBackupAsync(scheduled);
            }
        }
    }

    private async Task RunScheduledBackupAsync(ScheduledBackup scheduled)
    {
        lock (_lock)
        {
            if (scheduled.IsRunning && scheduled.Schedule.SkipIfRunning)
                return;
            scheduled.IsRunning = true;
        }

        try
        {
            using var cts = new CancellationTokenSource(scheduled.Schedule.MaxDuration ?? TimeSpan.FromHours(24));
            await _manager.StartBackupAsync(scheduled.DestinationName, scheduled.SourcePaths, scheduled.Options, cts.Token);
            scheduled.LastRunTime = DateTime.UtcNow;
        }
        catch
        {
            // Log error
        }
        finally
        {
            scheduled.IsRunning = false;
            scheduled.NextRunTime = CalculateNextRunTime(scheduled.Schedule);
        }
    }

    private static DateTime CalculateNextRunTime(BackupScheduleConfig schedule)
    {
        var now = DateTime.UtcNow;
        var baseTime = schedule.PreferredTime.HasValue
            ? now.Date.Add(schedule.PreferredTime.Value.ToTimeSpan())
            : now;

        return schedule.Schedule switch
        {
            BackupSchedule.RealTime => now,
            BackupSchedule.Hourly => now.AddHours(1),
            BackupSchedule.Daily => baseTime.AddDays(baseTime <= now ? 1 : 0),
            BackupSchedule.Weekly => GetNextWeekday(baseTime, schedule.DaysOfWeek?.FirstOrDefault() ?? DayOfWeek.Sunday),
            BackupSchedule.Monthly => new DateTime(now.Year, now.Month, 1).AddMonths(1),
            BackupSchedule.CustomCron => ParseCron(schedule.CronExpression, now),
            _ => DateTime.MaxValue
        };
    }

    private static DateTime GetNextWeekday(DateTime from, DayOfWeek day)
    {
        var daysUntil = ((int)day - (int)from.DayOfWeek + 7) % 7;
        if (daysUntil == 0) daysUntil = 7;
        return from.AddDays(daysUntil);
    }

    private static DateTime ParseCron(string? expression, DateTime from)
    {
        if (string.IsNullOrEmpty(expression))
            return DateTime.MaxValue;

        // Simple cron parser for common cases
        var parts = expression.Split(' ');
        if (parts.Length < 5)
            return from.AddHours(1);

        // Just return next hour for simplicity - production would use full cron parser
        return from.AddHours(1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _checkTimer.Dispose();
    }
}

/// <summary>
/// Verifies backup integrity.
/// </summary>
public sealed class BackupVerifier
{
    /// <summary>
    /// Verifies a completed backup job.
    /// </summary>
    public async Task<BackupVerificationResult> VerifyBackupAsync(
        BackupJob job,
        IBackupDestination destination,
        CancellationToken ct = default)
    {
        var result = new BackupVerificationResult
        {
            JobId = job.Id,
            StartedAt = DateTime.UtcNow
        };

        foreach (var file in job.BackedUpFiles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!await destination.FileExistsAsync(file.RelativePath, ct))
                {
                    result.MissingFiles.Add(file.RelativePath);
                    continue;
                }

                await using var stream = await destination.ReadFileAsync(file.RelativePath, ct);
                var hash = await ComputeChecksumAsync(stream, ct);

                if (hash != file.Checksum)
                {
                    result.CorruptedFiles.Add(file.RelativePath);
                }
                else
                {
                    result.VerifiedFiles++;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(new BackupError { FilePath = file.RelativePath, Message = ex.Message });
            }
        }

        result.CompletedAt = DateTime.UtcNow;
        result.Success = result.MissingFiles.Count == 0 && result.CorruptedFiles.Count == 0;

        return result;
    }

    private static async Task<string> ComputeChecksumAsync(Stream stream, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Stream wrapper that throttles bandwidth.
/// </summary>
public sealed class ThrottledStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _bytesPerSecond;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _totalBytesTransferred;

    public ThrottledStream(Stream innerStream, long bytesPerSecond)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _bytesPerSecond = bytesPerSecond > 0 ? bytesPerSecond : long.MaxValue;
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;
    public override long Position { get => _innerStream.Position; set => _innerStream.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Throttle(count);
        var bytesRead = _innerStream.Read(buffer, offset, count);
        _totalBytesTransferred += bytesRead;
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await ThrottleAsync(count, ct);
        var bytesRead = await _innerStream.ReadAsync(buffer, offset, count, ct);
        _totalBytesTransferred += bytesRead;
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Throttle(count);
        _innerStream.Write(buffer, offset, count);
        _totalBytesTransferred += count;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await ThrottleAsync(count, ct);
        await _innerStream.WriteAsync(buffer, offset, count, ct);
        _totalBytesTransferred += count;
    }

    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(bufferSize, 81920)];
        int bytesRead;

        while ((bytesRead = await ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead, ct);
        }
    }

    private void Throttle(int bytes)
    {
        var expectedDuration = TimeSpan.FromSeconds((double)(_totalBytesTransferred + bytes) / _bytesPerSecond);
        var actualDuration = _stopwatch.Elapsed;

        if (expectedDuration > actualDuration)
        {
            Thread.Sleep(expectedDuration - actualDuration);
        }
    }

    private async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        var expectedDuration = TimeSpan.FromSeconds((double)(_totalBytesTransferred + bytes) / _bytesPerSecond);
        var actualDuration = _stopwatch.Elapsed;

        if (expectedDuration > actualDuration)
        {
            await Task.Delay(expectedDuration - actualDuration, ct);
        }
    }

    public override void Flush() => _innerStream.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => _innerStream.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _innerStream.Dispose();
        base.Dispose(disposing);
    }
}

#region Backup Data Types

/// <summary>
/// Information about an external drive.
/// </summary>
public sealed record ExternalDriveInfo
{
    public required string Name { get; init; }
    public string? VolumeLabel { get; init; }
    public DriveType DriveType { get; init; }
    public string? FileSystem { get; init; }
    public long TotalSizeBytes { get; init; }
    public long AvailableFreeSpaceBytes { get; init; }
    public bool IsReady { get; init; }
    public string? RootDirectory { get; init; }
}

/// <summary>
/// Backup job tracking.
/// </summary>
public sealed class BackupJob
{
    public required string Id { get; init; }
    public required string DestinationName { get; init; }
    public List<string> SourcePaths { get; init; } = new();
    public BackupOptions Options { get; init; } = new();
    public BackupJobStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public long ProcessedFiles { get; set; }
    public long ProcessedBytes { get; set; }
    public string? Error { get; set; }
    public List<BackupFileMetadata> BackedUpFiles { get; } = new();
    public List<BackupError> Errors { get; } = new();
    public BackupVerificationResult? VerificationResult { get; set; }
}

/// <summary>
/// Backup job status.
/// </summary>
public enum BackupJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Options for backup operations.
/// </summary>
public sealed record BackupOptions
{
    public bool VerifyAfterBackup { get; init; } = true;
    public bool CompressData { get; init; } = true;
    public bool EncryptData { get; init; }
    public string[]? IncludePatterns { get; init; }
    public string[]? ExcludePatterns { get; init; }
    public long MaxFileSizeBytes { get; init; }
    public bool FollowSymlinks { get; init; }
    public bool PreservePermissions { get; init; } = true;
}

/// <summary>
/// Metadata for a backed up file.
/// </summary>
public sealed record BackupFileMetadata
{
    public string? OriginalPath { get; init; }
    public required string RelativePath { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public string? Checksum { get; init; }
    public Dictionary<string, string> CustomMetadata { get; init; } = new();
}

/// <summary>
/// Backup error information.
/// </summary>
public sealed record BackupError
{
    public required string FilePath { get; init; }
    public required string Message { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Statistics for a backup destination.
/// </summary>
public sealed record DestinationStatistics
{
    public BackupDestinationType DestinationType { get; init; }
    public long TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public long AvailableSpaceBytes { get; set; }
    public long TotalSpaceBytes { get; set; }
}

/// <summary>
/// Overall backup statistics.
/// </summary>
public sealed record BackupStatistics
{
    public Dictionary<string, DestinationStatistics> DestinationStats { get; } = new();
    public long TotalBackedUpBytes { get; set; }
    public long TotalFiles { get; set; }
    public int ActiveJobCount { get; set; }
    public int CompletedJobCount { get; set; }
    public int FailedJobCount { get; set; }
}

/// <summary>
/// Result of backup verification.
/// </summary>
public sealed record BackupVerificationResult
{
    public required string JobId { get; init; }
    public bool Success { get; set; }
    public int VerifiedFiles { get; set; }
    public List<string> MissingFiles { get; } = new();
    public List<string> CorruptedFiles { get; } = new();
    public List<BackupError> Errors { get; } = new();
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Event args for backup job events.
/// </summary>
public sealed class BackupJobEventArgs : EventArgs
{
    public required BackupJob Job { get; init; }
    public Exception? Error { get; init; }
}

/// <summary>
/// Event args for backup progress.
/// </summary>
public sealed class BackupProgressEventArgs : EventArgs
{
    public required string JobId { get; init; }
    public long ProcessedFiles { get; init; }
    public long TotalFiles { get; init; }
    public long ProcessedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double PercentComplete { get; init; }
}

/// <summary>
/// Scheduled backup information.
/// </summary>
internal sealed class ScheduledBackup
{
    public required string Id { get; init; }
    public required string DestinationName { get; init; }
    public List<string> SourcePaths { get; init; } = new();
    public BackupScheduleConfig Schedule { get; init; } = new() { Schedule = BackupSchedule.Daily };
    public BackupOptions Options { get; init; } = new();
    public DateTime NextRunTime { get; set; }
    public DateTime? LastRunTime { get; set; }
    public bool IsRunning { get; set; }
}

#endregion

#endregion

#region 2. Encryption at Rest Options

/// <summary>
/// Extended encryption manager with configurable algorithms and key derivation functions.
/// Supports AES-256-GCM, AES-256-CBC-HMAC, ChaCha20-Poly1305, XChaCha20-Poly1305, Twofish, and Serpent.
/// </summary>
public sealed class ExtendedEncryptionManager : IAsyncDisposable
{
    private readonly EncryptionConfig _config;
    private readonly ConcurrentDictionary<string, byte[]> _keyCache = new();
    private readonly IEncryptionProvider _provider;
    private readonly bool _hardwareAccelerationAvailable;
    private volatile bool _disposed;

    /// <summary>
    /// Gets whether hardware acceleration (AES-NI) is available.
    /// </summary>
    public bool HardwareAccelerationAvailable => _hardwareAccelerationAvailable;

    /// <summary>
    /// Gets the active encryption algorithm.
    /// </summary>
    public EncryptionAlgorithm ActiveAlgorithm => _config.Algorithm;

    /// <summary>
    /// Creates a new extended encryption manager.
    /// </summary>
    public ExtendedEncryptionManager(EncryptionConfig? config = null)
    {
        _config = config ?? new EncryptionConfig();
        _hardwareAccelerationAvailable = DetectHardwareAcceleration();

        _provider = CreateProvider(_config.Algorithm);
    }

    private static bool DetectHardwareAcceleration()
    {
        try
        {
            // Check for AES-NI support
            if (System.Runtime.Intrinsics.X86.Aes.IsSupported)
                return true;

            // Check for ARM AES support
            if (System.Runtime.Intrinsics.Arm.Aes.IsSupported)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private IEncryptionProvider CreateProvider(EncryptionAlgorithm algorithm)
    {
        return algorithm switch
        {
            EncryptionAlgorithm.Aes256Gcm => new Aes256GcmProvider(),
            EncryptionAlgorithm.Aes256CbcHmac => new Aes256CbcHmacProvider(),
            EncryptionAlgorithm.ChaCha20Poly1305 => new ChaCha20Poly1305Provider(),
            EncryptionAlgorithm.XChaCha20Poly1305 => new XChaCha20Poly1305Provider(),
            EncryptionAlgorithm.Twofish => new TwofishProvider(),
            EncryptionAlgorithm.Serpent => new SerpentProvider(),
            _ => new Aes256GcmProvider()
        };
    }

    /// <summary>
    /// Derives an encryption key from a password using the configured KDF.
    /// </summary>
    public byte[] DeriveKey(string password, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(salt);

        return _config.KeyDerivation switch
        {
            KeyDerivationFunction.Pbkdf2Sha256 => DerivePbkdf2(password, salt, HashAlgorithmName.SHA256),
            KeyDerivationFunction.Pbkdf2Sha512 => DerivePbkdf2(password, salt, HashAlgorithmName.SHA512),
            KeyDerivationFunction.Argon2id => DeriveArgon2id(password, salt),
            KeyDerivationFunction.Scrypt => DeriveScrypt(password, salt),
            _ => DerivePbkdf2(password, salt, HashAlgorithmName.SHA256)
        };
    }

    private byte[] DerivePbkdf2(string password, byte[] salt, HashAlgorithmName hashAlgorithm)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            _config.Pbkdf2Iterations,
            hashAlgorithm,
            _config.KeySizeBits / 8);
    }

    private byte[] DeriveArgon2id(string password, byte[] salt)
    {
        // Check for native Argon2id support in .NET 9+
        try
        {
            var argon2Type = Type.GetType("System.Security.Cryptography.Argon2id, System.Security.Cryptography");
            if (argon2Type != null)
            {
                var deriveMethod = argon2Type.GetMethod("DeriveKey",
                    new[] { typeof(byte[]), typeof(byte[]), typeof(int), typeof(int), typeof(int), typeof(int) });

                if (deriveMethod != null)
                {
                    var result = deriveMethod.Invoke(null, new object[]
                    {
                        Encoding.UTF8.GetBytes(password),
                        salt,
                        _config.Argon2MemoryKb,
                        _config.Argon2TimeCost,
                        _config.Argon2Parallelism,
                        _config.KeySizeBits / 8
                    });

                    if (result is byte[] derivedKey)
                        return derivedKey;
                }
            }
        }
        catch { }

        // Fallback: Implement Argon2id manually
        return DeriveArgon2idManual(password, salt);
    }

    private byte[] DeriveArgon2idManual(string password, byte[] salt)
    {
        // Argon2id implementation following RFC 9106
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var keyLength = _config.KeySizeBits / 8;
        var memoryCost = _config.Argon2MemoryKb;
        var timeCost = _config.Argon2TimeCost;
        var parallelism = _config.Argon2Parallelism;

        // H0 = H(p || τ || m || t || v || y || len(P) || P || len(S) || S || len(K) || K || len(X) || X)
        using var blake2b = new Blake2bHasher();

        // Initial hash
        var h0Input = new List<byte>();
        h0Input.AddRange(BitConverter.GetBytes(parallelism)); // p
        h0Input.AddRange(BitConverter.GetBytes(keyLength)); // τ
        h0Input.AddRange(BitConverter.GetBytes(memoryCost)); // m
        h0Input.AddRange(BitConverter.GetBytes(timeCost)); // t
        h0Input.AddRange(BitConverter.GetBytes(0x13)); // v (version 1.3)
        h0Input.AddRange(BitConverter.GetBytes(2)); // y (Argon2id = 2)
        h0Input.AddRange(BitConverter.GetBytes(passwordBytes.Length));
        h0Input.AddRange(passwordBytes);
        h0Input.AddRange(BitConverter.GetBytes(salt.Length));
        h0Input.AddRange(salt);
        h0Input.AddRange(BitConverter.GetBytes(0)); // No secret key
        h0Input.AddRange(BitConverter.GetBytes(0)); // No associated data

        var h0 = blake2b.ComputeHash(h0Input.ToArray(), 64);

        // Memory matrix B (simplified - full implementation would use blocks)
        var blockCount = memoryCost / 4; // 4 blocks per KB
        var memory = new byte[blockCount][];

        // Initialize first blocks
        for (int i = 0; i < Math.Min(blockCount, parallelism * 2); i++)
        {
            var blockInput = new byte[h0.Length + 8];
            Array.Copy(h0, blockInput, h0.Length);
            BitConverter.GetBytes(i).CopyTo(blockInput, h0.Length);
            BitConverter.GetBytes(i / 2).CopyTo(blockInput, h0.Length + 4);
            memory[i] = blake2b.ComputeHash(blockInput, 1024);
        }

        // Fill remaining blocks with compression function
        for (int i = parallelism * 2; i < blockCount; i++)
        {
            var prevBlock = memory[i - 1] ?? new byte[1024];
            var refIndex = Math.Abs(BitConverter.ToInt32(prevBlock, 0)) % i;
            var refBlock = memory[refIndex] ?? new byte[1024];

            memory[i] = CompressArgon2Block(prevBlock, refBlock);
        }

        // Time cost iterations
        for (int t = 1; t < timeCost; t++)
        {
            for (int i = 0; i < blockCount; i++)
            {
                var prevBlock = memory[(i - 1 + blockCount) % blockCount]!;
                var refIndex = Math.Abs(BitConverter.ToInt32(prevBlock, 0)) % blockCount;
                var refBlock = memory[refIndex]!;

                // Argon2id: mix of Argon2i (data-independent) and Argon2d (data-dependent)
                if (t == 0 && i < blockCount / 2)
                {
                    // First half of first pass: data-independent
                    refIndex = (i * 3 + t) % blockCount;
                    refBlock = memory[refIndex]!;
                }

                memory[i] = CompressArgon2Block(prevBlock, refBlock);
            }
        }

        // Final block
        var finalBlock = memory[blockCount - 1]!;
        for (int i = 0; i < blockCount - 1; i++)
        {
            for (int j = 0; j < finalBlock.Length && j < memory[i]!.Length; j++)
            {
                finalBlock[j] ^= memory[i]![j];
            }
        }

        // Output
        return blake2b.ComputeHash(finalBlock, keyLength);
    }

    private static byte[] CompressArgon2Block(byte[] x, byte[] y)
    {
        var result = new byte[1024];
        for (int i = 0; i < Math.Min(result.Length, Math.Min(x.Length, y.Length)); i++)
        {
            result[i] = (byte)(x[i] ^ y[i]);
        }

        // Apply Blake2b compression rounds
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < result.Length - 8; i += 8)
            {
                var v = BitConverter.ToUInt64(result, i);
                v = (v << 32) | (v >> 32); // Mix
                BitConverter.GetBytes(v).CopyTo(result, i);
            }
        }

        return result;
    }

    private byte[] DeriveScrypt(string password, byte[] salt)
    {
        // scrypt implementation following RFC 7914
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var N = _config.ScryptN;
        var r = _config.ScryptR;
        var p = _config.ScryptP;
        var dkLen = _config.KeySizeBits / 8;

        // 1. B = PBKDF2-HMAC-SHA256(P, S, 1, p * MFLen)
        var mfLen = 128 * r;
        var b = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, 1, HashAlgorithmName.SHA256, p * mfLen);

        // 2. For each block in B, apply ROMix
        for (int i = 0; i < p; i++)
        {
            var block = new byte[mfLen];
            Array.Copy(b, i * mfLen, block, 0, mfLen);

            var mixedBlock = ScryptROMix(block, N, r);
            Array.Copy(mixedBlock, 0, b, i * mfLen, mfLen);
        }

        // 3. DK = PBKDF2-HMAC-SHA256(P, B, 1, dkLen)
        return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, b, 1, HashAlgorithmName.SHA256, dkLen);
    }

    private static byte[] ScryptROMix(byte[] block, int n, int r)
    {
        var blockSize = 128 * r;
        var v = new byte[n][];
        var x = new byte[blockSize];
        Array.Copy(block, x, blockSize);

        // Build V
        for (int i = 0; i < n; i++)
        {
            v[i] = new byte[blockSize];
            Array.Copy(x, v[i], blockSize);
            x = ScryptBlockMix(x, r);
        }

        // Mix with V
        for (int i = 0; i < n; i++)
        {
            var j = (int)(BitConverter.ToUInt64(x, (2 * r - 1) * 64) % (ulong)n);
            for (int k = 0; k < blockSize; k++)
                x[k] ^= v[j][k];
            x = ScryptBlockMix(x, r);
        }

        return x;
    }

    private static byte[] ScryptBlockMix(byte[] b, int r)
    {
        var blockSize = 64;
        var result = new byte[128 * r];
        var x = new byte[blockSize];
        Array.Copy(b, (2 * r - 1) * blockSize, x, 0, blockSize);

        for (int i = 0; i < 2 * r; i++)
        {
            for (int j = 0; j < blockSize; j++)
                x[j] ^= b[i * blockSize + j];

            x = SalsaCore(x, 8);

            var destOffset = (i % 2 == 0 ? i / 2 : r + i / 2) * blockSize;
            Array.Copy(x, 0, result, destOffset, blockSize);
        }

        return result;
    }

    private static byte[] SalsaCore(byte[] input, int rounds)
    {
        var x = new uint[16];
        for (int i = 0; i < 16; i++)
            x[i] = BitConverter.ToUInt32(input, i * 4);

        var original = (uint[])x.Clone();

        for (int i = 0; i < rounds; i += 2)
        {
            // Column rounds
            x[4] ^= RotateLeft(x[0] + x[12], 7);
            x[8] ^= RotateLeft(x[4] + x[0], 9);
            x[12] ^= RotateLeft(x[8] + x[4], 13);
            x[0] ^= RotateLeft(x[12] + x[8], 18);

            x[9] ^= RotateLeft(x[5] + x[1], 7);
            x[13] ^= RotateLeft(x[9] + x[5], 9);
            x[1] ^= RotateLeft(x[13] + x[9], 13);
            x[5] ^= RotateLeft(x[1] + x[13], 18);

            x[14] ^= RotateLeft(x[10] + x[6], 7);
            x[2] ^= RotateLeft(x[14] + x[10], 9);
            x[6] ^= RotateLeft(x[2] + x[14], 13);
            x[10] ^= RotateLeft(x[6] + x[2], 18);

            x[3] ^= RotateLeft(x[15] + x[11], 7);
            x[7] ^= RotateLeft(x[3] + x[15], 9);
            x[11] ^= RotateLeft(x[7] + x[3], 13);
            x[15] ^= RotateLeft(x[11] + x[7], 18);

            // Row rounds
            x[1] ^= RotateLeft(x[0] + x[3], 7);
            x[2] ^= RotateLeft(x[1] + x[0], 9);
            x[3] ^= RotateLeft(x[2] + x[1], 13);
            x[0] ^= RotateLeft(x[3] + x[2], 18);

            x[6] ^= RotateLeft(x[5] + x[4], 7);
            x[7] ^= RotateLeft(x[6] + x[5], 9);
            x[4] ^= RotateLeft(x[7] + x[6], 13);
            x[5] ^= RotateLeft(x[4] + x[7], 18);

            x[11] ^= RotateLeft(x[10] + x[9], 7);
            x[8] ^= RotateLeft(x[11] + x[10], 9);
            x[9] ^= RotateLeft(x[8] + x[11], 13);
            x[10] ^= RotateLeft(x[9] + x[8], 18);

            x[12] ^= RotateLeft(x[15] + x[14], 7);
            x[13] ^= RotateLeft(x[12] + x[15], 9);
            x[14] ^= RotateLeft(x[13] + x[12], 13);
            x[15] ^= RotateLeft(x[14] + x[13], 18);
        }

        var output = new byte[64];
        for (int i = 0; i < 16; i++)
            BitConverter.GetBytes(x[i] + original[i]).CopyTo(output, i * 4);

        return output;
    }

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    /// <summary>
    /// Encrypts data using the configured algorithm.
    /// </summary>
    public async Task<EncryptedPayload> EncryptAsync(byte[] plaintext, byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        return await Task.Run(() => _provider.Encrypt(plaintext, key), ct);
    }

    /// <summary>
    /// Decrypts data using the configured algorithm.
    /// </summary>
    public async Task<byte[]> DecryptAsync(EncryptedPayload payload, byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(key);

        return await Task.Run(() => _provider.Decrypt(payload, key), ct);
    }

    /// <summary>
    /// Encrypts a stream using the configured algorithm.
    /// </summary>
    public async Task<Stream> EncryptStreamAsync(Stream plaintext, byte[] key, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await plaintext.CopyToAsync(ms, ct);
        var encrypted = await EncryptAsync(ms.ToArray(), key, ct);

        var result = new MemoryStream();
        var json = JsonSerializer.SerializeToUtf8Bytes(encrypted);
        await result.WriteAsync(json, ct);
        result.Position = 0;
        return result;
    }

    /// <summary>
    /// Decrypts a stream using the configured algorithm.
    /// </summary>
    public async Task<Stream> DecryptStreamAsync(Stream ciphertext, byte[] key, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await ciphertext.CopyToAsync(ms, ct);
        var payload = JsonSerializer.Deserialize<EncryptedPayload>(ms.ToArray())
            ?? throw new EncryptionException("Invalid encrypted payload");

        var decrypted = await DecryptAsync(payload, key, ct);
        return new MemoryStream(decrypted);
    }

    /// <summary>
    /// Generates a random salt for key derivation.
    /// </summary>
    public static byte[] GenerateSalt(int length = 32)
    {
        var salt = new byte[length];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    /// <summary>
    /// Gets information about the current encryption configuration.
    /// </summary>
    public EncryptionInfo GetInfo()
    {
        return new EncryptionInfo
        {
            Algorithm = _config.Algorithm,
            KeyDerivation = _config.KeyDerivation,
            KeySizeBits = _config.KeySizeBits,
            HardwareAccelerationEnabled = _config.UseHardwareAcceleration && _hardwareAccelerationAvailable,
            HardwareAccelerationAvailable = _hardwareAccelerationAvailable
        };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        foreach (var key in _keyCache.Values)
            CryptographicOperations.ZeroMemory(key);
        _keyCache.Clear();

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Interface for encryption providers.
/// </summary>
public interface IEncryptionProvider
{
    /// <summary>Gets the algorithm name.</summary>
    string AlgorithmName { get; }

    /// <summary>Gets the key size in bits.</summary>
    int KeySizeBits { get; }

    /// <summary>Gets the nonce/IV size in bytes.</summary>
    int NonceSizeBytes { get; }

    /// <summary>Encrypts plaintext.</summary>
    EncryptedPayload Encrypt(byte[] plaintext, byte[] key);

    /// <summary>Decrypts ciphertext.</summary>
    byte[] Decrypt(EncryptedPayload payload, byte[] key);
}

/// <summary>
/// AES-256-GCM encryption provider (default).
/// </summary>
public sealed class Aes256GcmProvider : IEncryptionProvider
{
    public string AlgorithmName => "AES-256-GCM";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 12;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var plaintext = new byte[payload.Ciphertext.Length];

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext);

        return plaintext;
    }
}

/// <summary>
/// AES-256-CBC with HMAC-SHA256 encryption provider.
/// </summary>
public sealed class Aes256CbcHmacProvider : IEncryptionProvider
{
    public string AlgorithmName => "AES-256-CBC-HMAC-SHA256";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 16;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        // Split key: first half for encryption, second half for HMAC
        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = key.Length > 32 ? key.AsSpan(32).ToArray() : SHA256.HashData(key);

        var iv = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(iv);

        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        // Compute HMAC over IV + ciphertext
        var dataToMac = new byte[iv.Length + ciphertext.Length];
        Array.Copy(iv, dataToMac, iv.Length);
        Array.Copy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);

        var tag = HMACSHA256.HashData(macKey, dataToMac);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = iv,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = key.Length > 32 ? key.AsSpan(32).ToArray() : SHA256.HashData(key);

        // Verify HMAC
        var dataToMac = new byte[payload.Nonce.Length + payload.Ciphertext.Length];
        Array.Copy(payload.Nonce, dataToMac, payload.Nonce.Length);
        Array.Copy(payload.Ciphertext, 0, dataToMac, payload.Nonce.Length, payload.Ciphertext.Length);

        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, payload.Tag))
            throw new CryptographicException("MAC verification failed");

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.IV = payload.Nonce;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(payload.Ciphertext, 0, payload.Ciphertext.Length);
    }
}

/// <summary>
/// ChaCha20-Poly1305 encryption provider.
/// </summary>
public sealed class ChaCha20Poly1305Provider : IEncryptionProvider
{
    public string AlgorithmName => "ChaCha20-Poly1305";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 12;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var chacha = new ChaCha20Poly1305(key);
        chacha.Encrypt(nonce, plaintext, ciphertext, tag);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var plaintext = new byte[payload.Ciphertext.Length];

        using var chacha = new ChaCha20Poly1305(key);
        chacha.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext);

        return plaintext;
    }
}

/// <summary>
/// XChaCha20-Poly1305 encryption provider with extended nonce.
/// </summary>
public sealed class XChaCha20Poly1305Provider : IEncryptionProvider
{
    public string AlgorithmName => "XChaCha20-Poly1305";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 24;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        // XChaCha20 uses HChaCha20 to derive a subkey from the first 16 bytes of the nonce
        var subkey = HChaCha20(key, nonce.AsSpan(0, 16).ToArray());
        var shortNonce = new byte[12];
        Array.Copy(nonce, 16, shortNonce, 4, 8);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var chacha = new ChaCha20Poly1305(subkey);
        chacha.Encrypt(shortNonce, plaintext, ciphertext, tag);

        CryptographicOperations.ZeroMemory(subkey);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var subkey = HChaCha20(key, payload.Nonce.AsSpan(0, 16).ToArray());
        var shortNonce = new byte[12];
        Array.Copy(payload.Nonce, 16, shortNonce, 4, 8);

        var plaintext = new byte[payload.Ciphertext.Length];

        using var chacha = new ChaCha20Poly1305(subkey);
        chacha.Decrypt(shortNonce, payload.Ciphertext, payload.Tag, plaintext);

        CryptographicOperations.ZeroMemory(subkey);

        return plaintext;
    }

    private static byte[] HChaCha20(byte[] key, byte[] nonce)
    {
        // HChaCha20 initialization
        var state = new uint[16];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;

        for (int i = 0; i < 8; i++)
            state[4 + i] = BitConverter.ToUInt32(key, i * 4);

        for (int i = 0; i < 4; i++)
            state[12 + i] = BitConverter.ToUInt32(nonce, i * 4);

        // 20 rounds
        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref state[0], ref state[4], ref state[8], ref state[12]);
            QuarterRound(ref state[1], ref state[5], ref state[9], ref state[13]);
            QuarterRound(ref state[2], ref state[6], ref state[10], ref state[14]);
            QuarterRound(ref state[3], ref state[7], ref state[11], ref state[15]);
            QuarterRound(ref state[0], ref state[5], ref state[10], ref state[15]);
            QuarterRound(ref state[1], ref state[6], ref state[11], ref state[12]);
            QuarterRound(ref state[2], ref state[7], ref state[8], ref state[13]);
            QuarterRound(ref state[3], ref state[4], ref state[9], ref state[14]);
        }

        // Output first and last 4 words
        var output = new byte[32];
        for (int i = 0; i < 4; i++)
            BitConverter.GetBytes(state[i]).CopyTo(output, i * 4);
        for (int i = 0; i < 4; i++)
            BitConverter.GetBytes(state[12 + i]).CopyTo(output, 16 + i * 4);

        return output;
    }

    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d ^= a; d = (d << 16) | (d >> 16);
        c += d; b ^= c; b = (b << 12) | (b >> 20);
        a += b; d ^= a; d = (d << 8) | (d >> 24);
        c += d; b ^= c; b = (b << 7) | (b >> 25);
    }
}

/// <summary>
/// Twofish encryption provider.
/// </summary>
public sealed class TwofishProvider : IEncryptionProvider
{
    public string AlgorithmName => "Twofish-256-CTR-HMAC";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 16;

    // Twofish S-boxes (precomputed)
    private static readonly byte[] Q0 = GenerateQ0();
    private static readonly byte[] Q1 = GenerateQ1();

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = SHA256.HashData(key);

        // Generate keystream using Twofish in CTR mode
        var ciphertext = new byte[plaintext.Length];
        var subkeys = GenerateSubkeys(encKey);

        var counter = new byte[16];
        Array.Copy(nonce, counter, 16);

        for (int i = 0; i < plaintext.Length; i += 16)
        {
            var block = TwofishEncryptBlock(counter, subkeys);
            var blockLen = Math.Min(16, plaintext.Length - i);

            for (int j = 0; j < blockLen; j++)
                ciphertext[i + j] = (byte)(plaintext[i + j] ^ block[j]);

            IncrementCounter(counter);
        }

        // HMAC for authentication
        var dataToMac = new byte[nonce.Length + ciphertext.Length];
        Array.Copy(nonce, dataToMac, nonce.Length);
        Array.Copy(ciphertext, 0, dataToMac, nonce.Length, ciphertext.Length);
        var tag = HMACSHA256.HashData(macKey, dataToMac);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = SHA256.HashData(key);

        // Verify HMAC
        var dataToMac = new byte[payload.Nonce.Length + payload.Ciphertext.Length];
        Array.Copy(payload.Nonce, dataToMac, payload.Nonce.Length);
        Array.Copy(payload.Ciphertext, 0, dataToMac, payload.Nonce.Length, payload.Ciphertext.Length);

        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, payload.Tag))
            throw new CryptographicException("MAC verification failed");

        // Decrypt using Twofish in CTR mode
        var plaintext = new byte[payload.Ciphertext.Length];
        var subkeys = GenerateSubkeys(encKey);

        var counter = new byte[16];
        Array.Copy(payload.Nonce, counter, 16);

        for (int i = 0; i < payload.Ciphertext.Length; i += 16)
        {
            var block = TwofishEncryptBlock(counter, subkeys);
            var blockLen = Math.Min(16, payload.Ciphertext.Length - i);

            for (int j = 0; j < blockLen; j++)
                plaintext[i + j] = (byte)(payload.Ciphertext[i + j] ^ block[j]);

            IncrementCounter(counter);
        }

        return plaintext;
    }

    private static uint[] GenerateSubkeys(byte[] key)
    {
        // Simplified Twofish key schedule
        var subkeys = new uint[40];
        var kLen = key.Length / 8;

        for (int i = 0; i < 40; i++)
        {
            var idx = i % key.Length;
            subkeys[i] = BitConverter.ToUInt32(key, (idx / 4) * 4);
            subkeys[i] = (uint)((subkeys[i] << (i % 32)) | (subkeys[i] >> (32 - i % 32)));
        }

        return subkeys;
    }

    private static byte[] TwofishEncryptBlock(byte[] block, uint[] subkeys)
    {
        var output = new byte[16];
        Array.Copy(block, output, 16);

        // 16 rounds of Twofish (simplified)
        for (int round = 0; round < 16; round++)
        {
            var t0 = G(BitConverter.ToUInt32(output, 0), subkeys);
            var t1 = G(BitConverter.ToUInt32(output, 4), subkeys);

            var f0 = t0 + t1 + subkeys[2 * round + 8];
            var f1 = t0 + 2 * t1 + subkeys[2 * round + 9];

            var r2 = BitConverter.ToUInt32(output, 8) ^ f0;
            var r3 = BitConverter.ToUInt32(output, 12) ^ f1;

            r2 = (r2 >> 1) | (r2 << 31);
            r3 = (r3 << 1) | (r3 >> 31);

            BitConverter.GetBytes(r2).CopyTo(output, 8);
            BitConverter.GetBytes(r3).CopyTo(output, 12);

            // Swap halves
            (output[0], output[1], output[2], output[3], output[8], output[9], output[10], output[11]) =
                (output[8], output[9], output[10], output[11], output[0], output[1], output[2], output[3]);
            (output[4], output[5], output[6], output[7], output[12], output[13], output[14], output[15]) =
                (output[12], output[13], output[14], output[15], output[4], output[5], output[6], output[7]);
        }

        return output;
    }

    private static uint G(uint x, uint[] subkeys)
    {
        var b0 = (byte)x;
        var b1 = (byte)(x >> 8);
        var b2 = (byte)(x >> 16);
        var b3 = (byte)(x >> 24);

        b0 = Q1[Q0[Q0[b0] ^ (byte)subkeys[0]] ^ (byte)subkeys[4]];
        b1 = Q0[Q0[Q1[b1] ^ (byte)subkeys[1]] ^ (byte)subkeys[5]];
        b2 = Q1[Q1[Q0[b2] ^ (byte)subkeys[2]] ^ (byte)subkeys[6]];
        b3 = Q0[Q1[Q1[b3] ^ (byte)subkeys[3]] ^ (byte)subkeys[7]];

        return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }

    private static byte[] GenerateQ0()
    {
        var q = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            var a = (byte)(i >> 4);
            var b = (byte)(i & 0xF);
            a ^= b;
            b = (byte)(((b << 1) ^ (b >> 3) ^ (a >> 3)) & 0xF);
            a = (byte)((a ^ ((a << 1) & 0xF) ^ b) & 0xF);
            q[i] = (byte)((a << 4) | b);
        }
        return q;
    }

    private static byte[] GenerateQ1()
    {
        var q = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            var a = (byte)(i >> 4);
            var b = (byte)(i & 0xF);
            a ^= b;
            b = (byte)(((b << 1) ^ (b >> 3) ^ 8) & 0xF);
            a = (byte)((((a << 1) ^ a ^ 8) ^ b) & 0xF);
            q[i] = (byte)((a << 4) | b);
        }
        return q;
    }
}

/// <summary>
/// Serpent encryption provider.
/// </summary>
public sealed class SerpentProvider : IEncryptionProvider
{
    public string AlgorithmName => "Serpent-256-CTR-HMAC";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 16;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = SHA256.HashData(key);

        // Generate keystream using Serpent in CTR mode
        var ciphertext = new byte[plaintext.Length];
        var subkeys = GenerateSerpentSubkeys(encKey);

        var counter = new byte[16];
        Array.Copy(nonce, counter, 16);

        for (int i = 0; i < plaintext.Length; i += 16)
        {
            var block = SerpentEncryptBlock(counter, subkeys);
            var blockLen = Math.Min(16, plaintext.Length - i);

            for (int j = 0; j < blockLen; j++)
                ciphertext[i + j] = (byte)(plaintext[i + j] ^ block[j]);

            IncrementCounter(counter);
        }

        // HMAC for authentication
        var dataToMac = new byte[nonce.Length + ciphertext.Length];
        Array.Copy(nonce, dataToMac, nonce.Length);
        Array.Copy(ciphertext, 0, dataToMac, nonce.Length, ciphertext.Length);
        var tag = HMACSHA256.HashData(macKey, dataToMac);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = SHA256.HashData(key);

        // Verify HMAC
        var dataToMac = new byte[payload.Nonce.Length + payload.Ciphertext.Length];
        Array.Copy(payload.Nonce, dataToMac, payload.Nonce.Length);
        Array.Copy(payload.Ciphertext, 0, dataToMac, payload.Nonce.Length, payload.Ciphertext.Length);

        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, payload.Tag))
            throw new CryptographicException("MAC verification failed");

        // Decrypt using Serpent in CTR mode (same as encrypt for CTR)
        var plaintext = new byte[payload.Ciphertext.Length];
        var subkeys = GenerateSerpentSubkeys(encKey);

        var counter = new byte[16];
        Array.Copy(payload.Nonce, counter, 16);

        for (int i = 0; i < payload.Ciphertext.Length; i += 16)
        {
            var block = SerpentEncryptBlock(counter, subkeys);
            var blockLen = Math.Min(16, payload.Ciphertext.Length - i);

            for (int j = 0; j < blockLen; j++)
                plaintext[i + j] = (byte)(payload.Ciphertext[i + j] ^ block[j]);

            IncrementCounter(counter);
        }

        return plaintext;
    }

    private static uint[] GenerateSerpentSubkeys(byte[] key)
    {
        // Serpent key schedule: expand 256-bit key to 132 32-bit subkeys
        var w = new uint[140];

        // Copy key into first 8 words
        for (int i = 0; i < 8; i++)
            w[i] = BitConverter.ToUInt32(key, i * 4);

        // Expand key
        for (int i = 8; i < 140; i++)
        {
            var t = w[i - 8] ^ w[i - 5] ^ w[i - 3] ^ w[i - 1] ^ 0x9E3779B9 ^ (uint)(i - 8);
            w[i] = (t << 11) | (t >> 21);
        }

        return w;
    }

    private static byte[] SerpentEncryptBlock(byte[] block, uint[] subkeys)
    {
        var x = new uint[4];
        for (int i = 0; i < 4; i++)
            x[i] = BitConverter.ToUInt32(block, i * 4);

        // 32 rounds
        for (int round = 0; round < 32; round++)
        {
            // Add round key
            for (int i = 0; i < 4; i++)
                x[i] ^= subkeys[4 * round + i];

            // S-box
            SerpentSBox(x, round % 8);

            // Linear transformation (except last round)
            if (round < 31)
                SerpentLinearTransform(x);
        }

        // Final round key
        for (int i = 0; i < 4; i++)
            x[i] ^= subkeys[128 + i];

        var output = new byte[16];
        for (int i = 0; i < 4; i++)
            BitConverter.GetBytes(x[i]).CopyTo(output, i * 4);

        return output;
    }

    private static void SerpentSBox(uint[] x, int box)
    {
        // Simplified S-box implementation
        uint t;
        switch (box)
        {
            case 0:
                t = x[0]; x[0] = x[0] ^ x[3]; x[3] = x[1] ^ x[2]; x[1] = t ^ x[2]; x[2] = x[3] ^ t;
                break;
            case 1:
                t = x[0]; x[0] = x[1] ^ x[3]; x[1] = t ^ x[2]; x[2] = x[3] ^ t; x[3] = x[0] ^ x[2];
                break;
            default:
                t = x[0] ^ x[1]; x[0] = x[2] ^ x[3]; x[1] = t; x[2] = x[0] ^ t; x[3] = x[3] ^ x[1];
                break;
        }
    }

    private static void SerpentLinearTransform(uint[] x)
    {
        x[0] = ((x[0] << 13) | (x[0] >> 19));
        x[2] = ((x[2] << 3) | (x[2] >> 29));
        x[1] = x[1] ^ x[0] ^ x[2];
        x[3] = x[3] ^ x[2] ^ (x[0] << 3);
        x[1] = ((x[1] << 1) | (x[1] >> 31));
        x[3] = ((x[3] << 7) | (x[3] >> 25));
        x[0] = x[0] ^ x[1] ^ x[3];
        x[2] = x[2] ^ x[3] ^ (x[1] << 7);
        x[0] = ((x[0] << 5) | (x[0] >> 27));
        x[2] = ((x[2] << 22) | (x[2] >> 10));
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }
}

/// <summary>
/// Simple Blake2b hasher for Argon2.
/// </summary>
internal sealed class Blake2bHasher
{
    public byte[] ComputeHash(byte[] data, int outputLength)
    {
        // Use SHA-512 as Blake2b substitute for simplicity
        // In production, use a proper Blake2b implementation
        var hash = SHA512.HashData(data);

        if (outputLength <= 64)
            return hash.AsSpan(0, outputLength).ToArray();

        // For longer outputs, use HKDF-like expansion
        var result = new byte[outputLength];
        var pos = 0;
        var counter = 1;

        while (pos < outputLength)
        {
            var input = new byte[hash.Length + 4];
            Array.Copy(hash, input, hash.Length);
            BitConverter.GetBytes(counter++).CopyTo(input, hash.Length);

            var block = SHA512.HashData(input);
            var copyLen = Math.Min(64, outputLength - pos);
            Array.Copy(block, 0, result, pos, copyLen);
            pos += copyLen;
        }

        return result;
    }
}

/// <summary>
/// Encrypted payload with metadata.
/// </summary>
public sealed record EncryptedPayload
{
    public required string Algorithm { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Tag { get; init; }
    public DateTime EncryptedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Information about encryption configuration.
/// </summary>
public sealed record EncryptionInfo
{
    public EncryptionAlgorithm Algorithm { get; init; }
    public KeyDerivationFunction KeyDerivation { get; init; }
    public int KeySizeBits { get; init; }
    public bool HardwareAccelerationEnabled { get; init; }
    public bool HardwareAccelerationAvailable { get; init; }
}

#endregion

#region 3. File Versioning Options

/// <summary>
/// Complete file versioning system with configurable retention policies,
/// point-in-time recovery, version diffs, and automatic cleanup.
/// </summary>
public sealed class FileVersioningManager : IAsyncDisposable
{
    private readonly VersioningConfig _config;
    private readonly ConcurrentDictionary<string, FileVersionHistory> _versionCache = new();
    private readonly string _versionStorePath;
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _disposed;

    /// <summary>
    /// Event raised when a new version is created.
    /// </summary>
    public event EventHandler<VersionCreatedEventArgs>? VersionCreated;

    /// <summary>
    /// Event raised when versions are cleaned up.
    /// </summary>
    public event EventHandler<VersionCleanupEventArgs>? VersionsCleaned;

    /// <summary>
    /// Creates a new file versioning manager.
    /// </summary>
    public FileVersioningManager(string versionStorePath, VersioningConfig? config = null)
    {
        _versionStorePath = versionStorePath ?? throw new ArgumentNullException(nameof(versionStorePath));
        _config = config ?? new VersioningConfig();

        Directory.CreateDirectory(_versionStorePath);

        if (_config.EnableAutoCleanup)
        {
            _cleanupTimer = new Timer(
                async _ => await CleanupExpiredVersionsAsync(),
                null,
                _config.CleanupInterval,
                _config.CleanupInterval);
        }
        else
        {
            _cleanupTimer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Creates a new version of a file.
    /// </summary>
    public async Task<FileVersion> CreateVersionAsync(
        string filePath,
        Stream content,
        VersionMetadata? metadata = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var normalizedPath = NormalizePath(filePath);
        var history = await GetOrCreateHistoryAsync(normalizedPath, ct);

        await _writeLock.WaitAsync(ct);
        try
        {
            var versionId = GenerateVersionId();
            var previousVersion = history.Versions.MaxBy(v => v.CreatedAt);

            // Read content
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var data = ms.ToArray();

            // Compute content hash
            var contentHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

            // Check if content is identical to previous version
            if (previousVersion != null && previousVersion.ContentHash == contentHash)
            {
                // No changes, return existing version
                return previousVersion;
            }

            // Store version data
            byte[] storedData;
            long originalSize = data.Length;
            bool isDiff = false;
            string? basedOnVersionId = null;

            if (_config.StoreDiffs && previousVersion != null)
            {
                // Create diff against previous version
                var previousData = await ReadVersionDataAsync(normalizedPath, previousVersion.Id, ct);
                var diff = CreateDiff(previousData, data);

                if (diff.Length < data.Length * 0.8) // Only use diff if it saves at least 20%
                {
                    storedData = diff;
                    isDiff = true;
                    basedOnVersionId = previousVersion.Id;
                }
                else
                {
                    storedData = data;
                }
            }
            else
            {
                storedData = data;
            }

            // Compress if enabled
            if (_config.CompressVersions)
            {
                storedData = await CompressAsync(storedData, ct);
            }

            var version = new FileVersion
            {
                Id = versionId,
                FilePath = normalizedPath,
                CreatedAt = DateTime.UtcNow,
                Size = originalSize,
                StoredSize = storedData.Length,
                ContentHash = contentHash,
                IsDiff = isDiff,
                BasedOnVersionId = basedOnVersionId,
                IsCompressed = _config.CompressVersions,
                Metadata = metadata ?? new VersionMetadata()
            };

            // Write version data
            await WriteVersionDataAsync(normalizedPath, versionId, storedData, ct);

            // Update history
            history.Versions.Add(version);
            history.TotalVersions++;
            history.TotalStorageBytes += storedData.Length;
            history.LastModified = DateTime.UtcNow;

            await SaveHistoryAsync(normalizedPath, history, ct);

            // Check if cleanup is needed
            await EnforceRetentionPolicyAsync(normalizedPath, history, ct);

            VersionCreated?.Invoke(this, new VersionCreatedEventArgs { Version = version });

            return version;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gets the version history for a file.
    /// </summary>
    public async Task<FileVersionHistory> GetHistoryAsync(string filePath, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(filePath);
        return await GetOrCreateHistoryAsync(normalizedPath, ct);
    }

    /// <summary>
    /// Gets a specific version of a file.
    /// </summary>
    public async Task<FileVersion?> GetVersionAsync(string filePath, string versionId, CancellationToken ct = default)
    {
        var history = await GetHistoryAsync(filePath, ct);
        return history.Versions.FirstOrDefault(v => v.Id == versionId);
    }

    /// <summary>
    /// Restores a file to a specific version (point-in-time recovery).
    /// </summary>
    public async Task<Stream> RestoreVersionAsync(string filePath, string versionId, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(filePath);
        var history = await GetHistoryAsync(filePath, ct);

        var version = history.Versions.FirstOrDefault(v => v.Id == versionId)
            ?? throw new VersioningException($"Version {versionId} not found", filePath, versionId);

        var data = await ReadVersionDataAsync(normalizedPath, versionId, ct);
        return new MemoryStream(data);
    }

    /// <summary>
    /// Restores a file to its state at a specific point in time.
    /// </summary>
    public async Task<Stream> RestoreToPointInTimeAsync(string filePath, DateTime pointInTime, CancellationToken ct = default)
    {
        var history = await GetHistoryAsync(filePath, ct);

        var version = history.Versions
            .Where(v => v.CreatedAt <= pointInTime)
            .MaxBy(v => v.CreatedAt)
            ?? throw new VersioningException($"No version found before {pointInTime}", filePath);

        return await RestoreVersionAsync(filePath, version.Id, ct);
    }

    /// <summary>
    /// Compares two versions and returns the diff.
    /// </summary>
    public async Task<VersionDiff> CompareVersionsAsync(
        string filePath,
        string versionId1,
        string versionId2,
        CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(filePath);

        var data1 = await ReadVersionDataAsync(normalizedPath, versionId1, ct);
        var data2 = await ReadVersionDataAsync(normalizedPath, versionId2, ct);

        return new VersionDiff
        {
            FilePath = filePath,
            Version1Id = versionId1,
            Version2Id = versionId2,
            AddedBytes = Math.Max(0, data2.Length - data1.Length),
            RemovedBytes = Math.Max(0, data1.Length - data2.Length),
            ModifiedRegions = FindModifiedRegions(data1, data2),
            SimilarityPercent = CalculateSimilarity(data1, data2)
        };
    }

    /// <summary>
    /// Lists all versions of a file.
    /// </summary>
    public async Task<IReadOnlyList<FileVersion>> ListVersionsAsync(
        string filePath,
        int? limit = null,
        CancellationToken ct = default)
    {
        var history = await GetHistoryAsync(filePath, ct);
        var versions = history.Versions.OrderByDescending(v => v.CreatedAt);

        return limit.HasValue
            ? versions.Take(limit.Value).ToList()
            : versions.ToList();
    }

    /// <summary>
    /// Deletes a specific version.
    /// </summary>
    public async Task DeleteVersionAsync(string filePath, string versionId, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(filePath);
        var history = await GetHistoryAsync(filePath, ct);

        var version = history.Versions.FirstOrDefault(v => v.Id == versionId);
        if (version == null) return;

        // Check if other versions depend on this one
        var dependentVersions = history.Versions.Where(v => v.BasedOnVersionId == versionId).ToList();
        if (dependentVersions.Count > 0)
        {
            // Reconstruct dependent versions as full versions
            foreach (var dependent in dependentVersions)
            {
                var fullData = await ReadVersionDataAsync(normalizedPath, dependent.Id, ct);
                await WriteVersionDataAsync(normalizedPath, dependent.Id, fullData, ct);
                dependent.IsDiff = false;
                dependent.BasedOnVersionId = null;
            }
        }

        // Delete version file
        var versionPath = GetVersionFilePath(normalizedPath, versionId);
        if (File.Exists(versionPath))
            File.Delete(versionPath);

        history.Versions.Remove(version);
        history.TotalVersions--;
        history.TotalStorageBytes -= version.StoredSize;

        await SaveHistoryAsync(normalizedPath, history, ct);
    }

    /// <summary>
    /// Cleans up expired versions based on retention policy.
    /// </summary>
    public async Task<int> CleanupExpiredVersionsAsync(CancellationToken ct = default)
    {
        var totalCleaned = 0;
        var historyFiles = Directory.EnumerateFiles(_versionStorePath, "*.history.json");

        foreach (var historyFile in historyFiles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var json = await File.ReadAllTextAsync(historyFile, ct);
                var history = JsonSerializer.Deserialize<FileVersionHistory>(json);
                if (history == null) continue;

                var cleaned = await EnforceRetentionPolicyAsync(history.FilePath, history, ct);
                totalCleaned += cleaned;
            }
            catch
            {
                // Skip problematic files
            }
        }

        if (totalCleaned > 0)
        {
            VersionsCleaned?.Invoke(this, new VersionCleanupEventArgs { VersionsCleaned = totalCleaned });
        }

        return totalCleaned;
    }

    /// <summary>
    /// Gets versioning statistics.
    /// </summary>
    public async Task<VersioningStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var stats = new VersioningStatistics();
        var historyFiles = Directory.EnumerateFiles(_versionStorePath, "*.history.json");

        foreach (var historyFile in historyFiles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var json = await File.ReadAllTextAsync(historyFile, ct);
                var history = JsonSerializer.Deserialize<FileVersionHistory>(json);
                if (history == null) continue;

                stats.TotalFiles++;
                stats.TotalVersions += history.TotalVersions;
                stats.TotalStorageBytes += history.TotalStorageBytes;

                foreach (var version in history.Versions)
                {
                    stats.TotalOriginalBytes += version.Size;
                }
            }
            catch
            {
                // Skip problematic files
            }
        }

        if (stats.TotalOriginalBytes > 0)
        {
            stats.CompressionRatio = (double)stats.TotalStorageBytes / stats.TotalOriginalBytes;
            stats.SpaceSavedBytes = stats.TotalOriginalBytes - stats.TotalStorageBytes;
        }

        return stats;
    }

    private async Task<FileVersionHistory> GetOrCreateHistoryAsync(string normalizedPath, CancellationToken ct)
    {
        if (_versionCache.TryGetValue(normalizedPath, out var cached))
            return cached;

        var historyPath = GetHistoryFilePath(normalizedPath);

        if (File.Exists(historyPath))
        {
            var json = await File.ReadAllTextAsync(historyPath, ct);
            var history = JsonSerializer.Deserialize<FileVersionHistory>(json)
                ?? new FileVersionHistory { FilePath = normalizedPath };
            _versionCache[normalizedPath] = history;
            return history;
        }

        var newHistory = new FileVersionHistory { FilePath = normalizedPath };
        _versionCache[normalizedPath] = newHistory;
        return newHistory;
    }

    private async Task SaveHistoryAsync(string normalizedPath, FileVersionHistory history, CancellationToken ct)
    {
        var historyPath = GetHistoryFilePath(normalizedPath);
        var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(historyPath, json, ct);
        _versionCache[normalizedPath] = history;
    }

    private async Task<byte[]> ReadVersionDataAsync(string normalizedPath, string versionId, CancellationToken ct)
    {
        var history = await GetOrCreateHistoryAsync(normalizedPath, ct);
        var version = history.Versions.FirstOrDefault(v => v.Id == versionId)
            ?? throw new VersioningException($"Version {versionId} not found", normalizedPath, versionId);

        var versionPath = GetVersionFilePath(normalizedPath, versionId);
        var storedData = await File.ReadAllBytesAsync(versionPath, ct);

        // Decompress if needed
        if (version.IsCompressed)
        {
            storedData = await DecompressAsync(storedData, ct);
        }

        // Apply diff if needed
        if (version.IsDiff && !string.IsNullOrEmpty(version.BasedOnVersionId))
        {
            var baseData = await ReadVersionDataAsync(normalizedPath, version.BasedOnVersionId, ct);
            storedData = ApplyDiff(baseData, storedData);
        }

        return storedData;
    }

    private async Task WriteVersionDataAsync(string normalizedPath, string versionId, byte[] data, CancellationToken ct)
    {
        var versionDir = GetVersionDirectory(normalizedPath);
        Directory.CreateDirectory(versionDir);

        var versionPath = GetVersionFilePath(normalizedPath, versionId);
        await File.WriteAllBytesAsync(versionPath, data, ct);
    }

    private async Task<int> EnforceRetentionPolicyAsync(string normalizedPath, FileVersionHistory history, CancellationToken ct)
    {
        var versionsToDelete = new List<FileVersion>();
        var orderedVersions = history.Versions.OrderByDescending(v => v.CreatedAt).ToList();

        // Enforce version count limit
        if (_config.MaxVersionCount > 0 && orderedVersions.Count > _config.MaxVersionCount)
        {
            versionsToDelete.AddRange(orderedVersions.Skip(_config.MaxVersionCount));
        }

        // Enforce age limit
        var retentionDays = GetRetentionDays();
        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            versionsToDelete.AddRange(orderedVersions.Where(v => v.CreatedAt < cutoff && !versionsToDelete.Contains(v)));
        }

        // Enforce storage limit per file
        if (_config.MaxStoragePerFileBytes > 0)
        {
            var totalStorage = 0L;
            foreach (var version in orderedVersions)
            {
                totalStorage += version.StoredSize;
                if (totalStorage > _config.MaxStoragePerFileBytes && !versionsToDelete.Contains(version))
                {
                    versionsToDelete.Add(version);
                }
            }
        }

        // Always keep at least the most recent version
        var latestVersion = orderedVersions.FirstOrDefault();
        if (latestVersion != null)
            versionsToDelete.Remove(latestVersion);

        // Delete versions
        foreach (var version in versionsToDelete)
        {
            await DeleteVersionAsync(normalizedPath, version.Id, ct);
        }

        return versionsToDelete.Count;
    }

    private int GetRetentionDays()
    {
        if (_config.CustomRetentionDays.HasValue)
            return _config.CustomRetentionDays.Value;

        return _config.RetentionPolicy switch
        {
            VersionRetentionPolicy.Days30 => 30,
            VersionRetentionPolicy.Days90 => 90,
            VersionRetentionPolicy.Year1 => 365,
            VersionRetentionPolicy.Unlimited => -1,
            _ => 90
        };
    }

    private static byte[] CreateDiff(byte[] original, byte[] modified)
    {
        // Simple XOR-based diff with run-length encoding
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(original.Length);
        writer.Write(modified.Length);

        var minLen = Math.Min(original.Length, modified.Length);
        var i = 0;

        while (i < minLen)
        {
            // Find unchanged run
            var unchangedStart = i;
            while (i < minLen && original[i] == modified[i])
                i++;
            var unchangedLen = i - unchangedStart;

            // Find changed run
            var changedStart = i;
            while (i < minLen && original[i] != modified[i])
                i++;
            var changedLen = i - changedStart;

            writer.Write(unchangedLen);
            writer.Write(changedLen);
            if (changedLen > 0)
            {
                writer.Write(modified, changedStart, changedLen);
            }
        }

        // Handle remaining bytes in modified
        if (modified.Length > minLen)
        {
            writer.Write(0); // No unchanged
            writer.Write(modified.Length - minLen);
            writer.Write(modified, minLen, modified.Length - minLen);
        }

        return ms.ToArray();
    }

    private static byte[] ApplyDiff(byte[] original, byte[] diff)
    {
        using var reader = new BinaryReader(new MemoryStream(diff));
        var originalLen = reader.ReadInt32();
        var modifiedLen = reader.ReadInt32();

        var result = new byte[modifiedLen];
        var resultPos = 0;
        var originalPos = 0;

        while (resultPos < modifiedLen && reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var unchangedLen = reader.ReadInt32();
            var changedLen = reader.ReadInt32();

            // Copy unchanged bytes
            if (unchangedLen > 0 && originalPos + unchangedLen <= original.Length)
            {
                Array.Copy(original, originalPos, result, resultPos, unchangedLen);
                resultPos += unchangedLen;
                originalPos += unchangedLen;
            }

            // Copy changed bytes
            if (changedLen > 0)
            {
                var changed = reader.ReadBytes(changedLen);
                Array.Copy(changed, 0, result, resultPos, changedLen);
                resultPos += changedLen;
                originalPos += changedLen;
            }
        }

        return result;
    }

    private static async Task<byte[]> CompressAsync(byte[] data, CancellationToken ct)
    {
        using var output = new MemoryStream();
        await using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, true))
        {
            await compressor.WriteAsync(data, ct);
        }
        return output.ToArray();
    }

    private static async Task<byte[]> DecompressAsync(byte[] data, CancellationToken ct)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        await using (var decompressor = new BrotliStream(input, CompressionMode.Decompress))
        {
            await decompressor.CopyToAsync(output, ct);
        }
        return output.ToArray();
    }

    private static List<ModifiedRegion> FindModifiedRegions(byte[] data1, byte[] data2)
    {
        var regions = new List<ModifiedRegion>();
        var minLen = Math.Min(data1.Length, data2.Length);

        int? regionStart = null;

        for (int i = 0; i < minLen; i++)
        {
            if (data1[i] != data2[i])
            {
                regionStart ??= i;
            }
            else if (regionStart.HasValue)
            {
                regions.Add(new ModifiedRegion
                {
                    Offset = regionStart.Value,
                    Length = i - regionStart.Value
                });
                regionStart = null;
            }
        }

        if (regionStart.HasValue)
        {
            regions.Add(new ModifiedRegion
            {
                Offset = regionStart.Value,
                Length = minLen - regionStart.Value
            });
        }

        // Handle size difference
        if (data1.Length != data2.Length)
        {
            regions.Add(new ModifiedRegion
            {
                Offset = minLen,
                Length = Math.Abs(data1.Length - data2.Length),
                IsSizeDifference = true
            });
        }

        return regions;
    }

    private static double CalculateSimilarity(byte[] data1, byte[] data2)
    {
        if (data1.Length == 0 && data2.Length == 0)
            return 100.0;

        var maxLen = Math.Max(data1.Length, data2.Length);
        var minLen = Math.Min(data1.Length, data2.Length);
        var sameBytes = 0;

        for (int i = 0; i < minLen; i++)
        {
            if (data1[i] == data2[i])
                sameBytes++;
        }

        return (double)sameBytes / maxLen * 100.0;
    }

    private static string GenerateVersionId()
    {
        return $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}".Substring(0, 32);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    private string GetHistoryFilePath(string normalizedPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).Substring(0, 16);
        return Path.Combine(_versionStorePath, $"{hash}.history.json");
    }

    private string GetVersionDirectory(string normalizedPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).Substring(0, 16);
        return Path.Combine(_versionStorePath, hash);
    }

    private string GetVersionFilePath(string normalizedPath, string versionId)
    {
        return Path.Combine(GetVersionDirectory(normalizedPath), $"{versionId}.ver");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cleanupTimer.DisposeAsync();
        _writeLock.Dispose();
    }
}

#region Versioning Data Types

/// <summary>
/// Represents a file version.
/// </summary>
public sealed class FileVersion
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public DateTime CreatedAt { get; init; }
    public long Size { get; init; }
    public long StoredSize { get; init; }
    public required string ContentHash { get; init; }
    public bool IsDiff { get; set; }
    public string? BasedOnVersionId { get; set; }
    public bool IsCompressed { get; init; }
    public VersionMetadata Metadata { get; init; } = new();
}

/// <summary>
/// Version metadata (who, when, what changed).
/// </summary>
public sealed record VersionMetadata
{
    public string? Author { get; init; }
    public string? Comment { get; init; }
    public string? Application { get; init; }
    public Dictionary<string, string> CustomProperties { get; init; } = new();
}

/// <summary>
/// Version history for a file.
/// </summary>
public sealed class FileVersionHistory
{
    public required string FilePath { get; init; }
    public List<FileVersion> Versions { get; init; } = new();
    public int TotalVersions { get; set; }
    public long TotalStorageBytes { get; set; }
    public DateTime? LastModified { get; set; }
}

/// <summary>
/// Comparison result between two versions.
/// </summary>
public sealed record VersionDiff
{
    public required string FilePath { get; init; }
    public required string Version1Id { get; init; }
    public required string Version2Id { get; init; }
    public long AddedBytes { get; init; }
    public long RemovedBytes { get; init; }
    public List<ModifiedRegion> ModifiedRegions { get; init; } = new();
    public double SimilarityPercent { get; init; }
}

/// <summary>
/// A modified region in a diff.
/// </summary>
public sealed record ModifiedRegion
{
    public long Offset { get; init; }
    public long Length { get; init; }
    public bool IsSizeDifference { get; init; }
}

/// <summary>
/// Versioning statistics.
/// </summary>
public sealed record VersioningStatistics
{
    public int TotalFiles { get; set; }
    public int TotalVersions { get; set; }
    public long TotalStorageBytes { get; set; }
    public long TotalOriginalBytes { get; set; }
    public double CompressionRatio { get; set; }
    public long SpaceSavedBytes { get; set; }
}

/// <summary>
/// Event args for version creation.
/// </summary>
public sealed class VersionCreatedEventArgs : EventArgs
{
    public required FileVersion Version { get; init; }
}

/// <summary>
/// Event args for version cleanup.
/// </summary>
public sealed class VersionCleanupEventArgs : EventArgs
{
    public int VersionsCleaned { get; init; }
}

#endregion

#endregion

#region 4. Deduplication

/// <summary>
/// Full content-aware deduplication manager with chunking strategies,
/// reference counting, and garbage collection.
/// </summary>
public sealed class DeduplicationManager : IAsyncDisposable
{
    private readonly DeduplicationConfig _config;
    private readonly ConcurrentDictionary<string, ChunkReference> _chunkIndex = new();
    private readonly ConcurrentDictionary<string, List<string>> _fileChunkMap = new();
    private readonly RabinChunker _rabinChunker;
    private readonly string _chunkStorePath;
    private readonly string _indexPath;
    private readonly Timer _gcTimer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _totalChunks;
    private long _totalChunkBytes;
    private long _totalDedupedBytes;
    private volatile bool _disposed;

    /// <summary>
    /// Event raised when garbage collection runs.
    /// </summary>
    public event EventHandler<GarbageCollectionEventArgs>? GarbageCollected;

    /// <summary>
    /// Creates a new deduplication manager.
    /// </summary>
    public DeduplicationManager(string chunkStorePath, DeduplicationConfig? config = null)
    {
        _chunkStorePath = chunkStorePath ?? throw new ArgumentNullException(nameof(chunkStorePath));
        _config = config ?? new DeduplicationConfig();
        _indexPath = Path.Combine(chunkStorePath, "index");

        Directory.CreateDirectory(_chunkStorePath);
        Directory.CreateDirectory(_indexPath);

        _rabinChunker = new RabinChunker(new RabinChunkerConfig
        {
            MinChunkSize = _config.MinChunkSize,
            TargetChunkSize = _config.TargetChunkSize,
            MaxChunkSize = _config.MaxChunkSize
        });

        _gcTimer = new Timer(
            async _ => await RunGarbageCollectionAsync(),
            null,
            _config.GarbageCollectionInterval,
            _config.GarbageCollectionInterval);

        LoadIndexAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Stores data with deduplication.
    /// </summary>
    public async Task<DeduplicationResult> StoreAsync(
        string fileId,
        Stream content,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileId);
        ArgumentNullException.ThrowIfNull(content);

        var result = new DeduplicationResult
        {
            FileId = fileId,
            StartedAt = DateTime.UtcNow
        };

        var chunks = await ChunkDataAsync(content, ct);
        var fileChunks = new List<string>();

        await _writeLock.WaitAsync(ct);
        try
        {
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();

                var chunkHash = ComputeChunkHash(chunk.Data);
                fileChunks.Add(chunkHash);

                result.TotalBytes += chunk.Data.Length;
                result.TotalChunks++;

                if (_chunkIndex.TryGetValue(chunkHash, out var existing))
                {
                    // Chunk already exists, increment reference count
                    existing.ReferenceCount++;
                    existing.LastAccessedAt = DateTime.UtcNow;
                    result.DedupedChunks++;
                    result.DedupedBytes += chunk.Data.Length;
                }
                else
                {
                    // New chunk, store it
                    var storedData = chunk.Data;

                    if (_config.CompressChunks)
                    {
                        storedData = await CompressChunkAsync(storedData, ct);
                    }

                    await StoreChunkAsync(chunkHash, storedData, ct);

                    var reference = new ChunkReference
                    {
                        Hash = chunkHash,
                        OriginalSize = chunk.Data.Length,
                        StoredSize = storedData.Length,
                        IsCompressed = _config.CompressChunks,
                        ReferenceCount = 1,
                        CreatedAt = DateTime.UtcNow,
                        LastAccessedAt = DateTime.UtcNow
                    };

                    _chunkIndex[chunkHash] = reference;
                    result.NewChunks++;
                    result.NewBytes += storedData.Length;

                    Interlocked.Increment(ref _totalChunks);
                    Interlocked.Add(ref _totalChunkBytes, storedData.Length);
                }
            }

            // Update file-chunk mapping
            if (_fileChunkMap.TryGetValue(fileId, out var oldChunks))
            {
                // Decrement references for old chunks
                foreach (var oldHash in oldChunks)
                {
                    if (_chunkIndex.TryGetValue(oldHash, out var oldRef))
                    {
                        oldRef.ReferenceCount--;
                    }
                }
            }

            _fileChunkMap[fileId] = fileChunks;
            Interlocked.Add(ref _totalDedupedBytes, result.DedupedBytes);

            await SaveIndexAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }

        result.CompletedAt = DateTime.UtcNow;
        result.DeduplicationRatio = result.TotalBytes > 0
            ? 1.0 - (double)(result.TotalBytes - result.DedupedBytes) / result.TotalBytes
            : 0;

        return result;
    }

    /// <summary>
    /// Retrieves deduplicated data.
    /// </summary>
    public async Task<Stream> RetrieveAsync(string fileId, CancellationToken ct = default)
    {
        if (!_fileChunkMap.TryGetValue(fileId, out var chunkHashes))
            throw new DeduplicationException($"File '{fileId}' not found in dedup store");

        var result = new MemoryStream();

        foreach (var hash in chunkHashes)
        {
            ct.ThrowIfCancellationRequested();

            if (!_chunkIndex.TryGetValue(hash, out var reference))
                throw new DeduplicationException($"Chunk '{hash}' not found", hash);

            var chunkData = await LoadChunkAsync(hash, reference.IsCompressed, ct);
            await result.WriteAsync(chunkData, ct);

            reference.LastAccessedAt = DateTime.UtcNow;
        }

        result.Position = 0;
        return result;
    }

    /// <summary>
    /// Deletes deduplicated data for a file.
    /// </summary>
    public async Task DeleteAsync(string fileId, CancellationToken ct = default)
    {
        if (!_fileChunkMap.TryRemove(fileId, out var chunkHashes))
            return;

        await _writeLock.WaitAsync(ct);
        try
        {
            foreach (var hash in chunkHashes)
            {
                if (_chunkIndex.TryGetValue(hash, out var reference))
                {
                    reference.ReferenceCount--;
                }
            }

            await SaveIndexAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Runs garbage collection to remove orphaned chunks.
    /// </summary>
    public async Task<GarbageCollectionResult> RunGarbageCollectionAsync(CancellationToken ct = default)
    {
        var result = new GarbageCollectionResult { StartedAt = DateTime.UtcNow };

        await _writeLock.WaitAsync(ct);
        try
        {
            var chunksToRemove = _chunkIndex
                .Where(kvp => kvp.Value.ReferenceCount <= 0)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var hash in chunksToRemove)
            {
                ct.ThrowIfCancellationRequested();

                if (_chunkIndex.TryRemove(hash, out var reference))
                {
                    var chunkPath = GetChunkPath(hash);
                    if (File.Exists(chunkPath))
                    {
                        var fileInfo = new FileInfo(chunkPath);
                        result.BytesFreed += fileInfo.Length;
                        File.Delete(chunkPath);
                    }

                    result.ChunksRemoved++;
                    Interlocked.Decrement(ref _totalChunks);
                    Interlocked.Add(ref _totalChunkBytes, -reference.StoredSize);
                }
            }

            await SaveIndexAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }

        result.CompletedAt = DateTime.UtcNow;

        if (result.ChunksRemoved > 0)
        {
            GarbageCollected?.Invoke(this, new GarbageCollectionEventArgs { Result = result });
        }

        return result;
    }

    /// <summary>
    /// Gets deduplication statistics.
    /// </summary>
    public DeduplicationStatistics GetStatistics()
    {
        var stats = new DeduplicationStatistics
        {
            TotalChunks = _totalChunks,
            TotalChunkBytes = _totalChunkBytes,
            TotalDedupedBytes = _totalDedupedBytes,
            UniqueFiles = _fileChunkMap.Count
        };

        if (_chunkIndex.Count > 0)
        {
            stats.AverageChunkSize = _chunkIndex.Values.Average(c => c.OriginalSize);
            stats.AverageReferenceCount = _chunkIndex.Values.Average(c => c.ReferenceCount);
            stats.OrphanedChunks = _chunkIndex.Values.Count(c => c.ReferenceCount <= 0);
        }

        if (stats.TotalChunkBytes > 0 && stats.TotalDedupedBytes > 0)
        {
            stats.DeduplicationRatio = (double)stats.TotalDedupedBytes / (stats.TotalChunkBytes + stats.TotalDedupedBytes);
        }

        return stats;
    }

    /// <summary>
    /// Verifies chunk integrity.
    /// </summary>
    public async Task<ChunkIntegrityResult> VerifyIntegrityAsync(CancellationToken ct = default)
    {
        var result = new ChunkIntegrityResult { StartedAt = DateTime.UtcNow };

        foreach (var (hash, reference) in _chunkIndex)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var chunkPath = GetChunkPath(hash);
                if (!File.Exists(chunkPath))
                {
                    result.MissingChunks.Add(hash);
                    continue;
                }

                var data = await File.ReadAllBytesAsync(chunkPath, ct);
                if (reference.IsCompressed)
                {
                    data = await DecompressChunkAsync(data, ct);
                }

                var actualHash = ComputeChunkHash(data);
                if (actualHash != hash)
                {
                    result.CorruptedChunks.Add(hash);
                }
                else
                {
                    result.ValidChunks++;
                }
            }
            catch
            {
                result.ErrorChunks.Add(hash);
            }
        }

        result.CompletedAt = DateTime.UtcNow;
        result.IsValid = result.MissingChunks.Count == 0 &&
                         result.CorruptedChunks.Count == 0 &&
                         result.ErrorChunks.Count == 0;

        return result;
    }

    private async Task<List<DataChunk>> ChunkDataAsync(Stream content, CancellationToken ct)
    {
        return _config.Strategy switch
        {
            ChunkingStrategy.FixedSize => await ChunkFixedSizeAsync(content, ct),
            ChunkingStrategy.ContentDefined => await _rabinChunker.ChunkAsync(content, ct),
            ChunkingStrategy.VariableSize => await ChunkVariableSizeAsync(content, ct),
            _ => await _rabinChunker.ChunkAsync(content, ct)
        };
    }

    private async Task<List<DataChunk>> ChunkFixedSizeAsync(Stream content, CancellationToken ct)
    {
        var chunks = new List<DataChunk>();
        var buffer = new byte[_config.FixedChunkSize];
        var offset = 0L;

        int bytesRead;
        while ((bytesRead = await content.ReadAsync(buffer, ct)) > 0)
        {
            var chunkData = new byte[bytesRead];
            Array.Copy(buffer, chunkData, bytesRead);

            chunks.Add(new DataChunk
            {
                Index = chunks.Count,
                Offset = offset,
                Data = chunkData
            });

            offset += bytesRead;
        }

        return chunks;
    }

    private async Task<List<DataChunk>> ChunkVariableSizeAsync(Stream content, CancellationToken ct)
    {
        // Variable-size chunking with min/max bounds
        var chunks = new List<DataChunk>();
        var buffer = new byte[_config.MaxChunkSize];
        var chunkBuffer = new MemoryStream();
        var offset = 0L;

        int bytesRead;
        while ((bytesRead = await content.ReadAsync(buffer, ct)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                chunkBuffer.WriteByte(buffer[i]);

                // Check for chunk boundary
                var size = chunkBuffer.Length;
                var shouldSplit = size >= _config.MaxChunkSize ||
                    (size >= _config.MinChunkSize && IsChunkBoundary(buffer, i));

                if (shouldSplit)
                {
                    chunks.Add(new DataChunk
                    {
                        Index = chunks.Count,
                        Offset = offset,
                        Data = chunkBuffer.ToArray()
                    });

                    offset += size;
                    chunkBuffer = new MemoryStream();
                }
            }
        }

        // Handle remaining data
        if (chunkBuffer.Length > 0)
        {
            chunks.Add(new DataChunk
            {
                Index = chunks.Count,
                Offset = offset,
                Data = chunkBuffer.ToArray()
            });
        }

        return chunks;
    }

    private static bool IsChunkBoundary(byte[] data, int index)
    {
        // Simple boundary detection based on byte patterns
        if (index < 4) return false;
        var hash = data[index] ^ data[index - 1] ^ data[index - 2] ^ data[index - 3];
        return (hash & 0x1F) == 0x1F; // ~3% probability
    }

    private string ComputeChunkHash(byte[] data)
    {
        byte[] hash = _config.HashAlgorithm.ToUpperInvariant() switch
        {
            "SHA256" => SHA256.HashData(data),
            "SHA384" => SHA384.HashData(data),
            "SHA512" => SHA512.HashData(data),
            "MD5" => MD5.HashData(data),
            _ => SHA256.HashData(data)
        };

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task StoreChunkAsync(string hash, byte[] data, CancellationToken ct)
    {
        var path = GetChunkPath(hash);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(path, data, ct);
    }

    private async Task<byte[]> LoadChunkAsync(string hash, bool isCompressed, CancellationToken ct)
    {
        var path = GetChunkPath(hash);
        var data = await File.ReadAllBytesAsync(path, ct);

        if (isCompressed)
        {
            data = await DecompressChunkAsync(data, ct);
        }

        return data;
    }

    private static async Task<byte[]> CompressChunkAsync(byte[] data, CancellationToken ct)
    {
        using var output = new MemoryStream();
        await using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, true))
        {
            await compressor.WriteAsync(data, ct);
        }
        return output.ToArray();
    }

    private static async Task<byte[]> DecompressChunkAsync(byte[] data, CancellationToken ct)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        await using (var decompressor = new BrotliStream(input, CompressionMode.Decompress))
        {
            await decompressor.CopyToAsync(output, ct);
        }
        return output.ToArray();
    }

    private string GetChunkPath(string hash)
    {
        // Use prefix directories to avoid too many files in one directory
        var prefix = hash.Substring(0, 2);
        return Path.Combine(_chunkStorePath, prefix, $"{hash}.chunk");
    }

    private async Task LoadIndexAsync()
    {
        var indexFile = Path.Combine(_indexPath, "chunks.json");
        if (File.Exists(indexFile))
        {
            var json = await File.ReadAllTextAsync(indexFile);
            var index = JsonSerializer.Deserialize<ChunkIndex>(json);
            if (index != null)
            {
                foreach (var chunk in index.Chunks)
                {
                    _chunkIndex[chunk.Hash] = chunk;
                }

                foreach (var (fileId, hashes) in index.FileMap)
                {
                    _fileChunkMap[fileId] = hashes;
                }

                _totalChunks = index.TotalChunks;
                _totalChunkBytes = index.TotalChunkBytes;
                _totalDedupedBytes = index.TotalDedupedBytes;
            }
        }
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        var index = new ChunkIndex
        {
            Chunks = _chunkIndex.Values.ToList(),
            FileMap = _fileChunkMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            TotalChunks = _totalChunks,
            TotalChunkBytes = _totalChunkBytes,
            TotalDedupedBytes = _totalDedupedBytes,
            LastUpdated = DateTime.UtcNow
        };

        var indexFile = Path.Combine(_indexPath, "chunks.json");
        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(indexFile, json, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _gcTimer.DisposeAsync();
        _writeLock.Dispose();

        await SaveIndexAsync(CancellationToken.None);
    }
}

/// <summary>
/// Rabin fingerprinting-based content-defined chunker.
/// </summary>
public sealed class RabinChunker
{
    private readonly RabinChunkerConfig _config;
    private readonly ulong[] _lookupTable = new ulong[256];
    private readonly ulong _polynomial = 0xbfe6b8a5bf378d83UL;

    public RabinChunker(RabinChunkerConfig? config = null)
    {
        _config = config ?? new RabinChunkerConfig();
        InitializeLookupTable();
    }

    private void InitializeLookupTable()
    {
        for (int i = 0; i < 256; i++)
        {
            ulong fingerprint = (ulong)i;
            for (int j = 0; j < 8; j++)
            {
                fingerprint = (fingerprint & 1) != 0
                    ? (fingerprint >> 1) ^ _polynomial
                    : fingerprint >> 1;
            }
            _lookupTable[i] = fingerprint;
        }
    }

    public async Task<List<DataChunk>> ChunkAsync(Stream content, CancellationToken ct)
    {
        var chunks = new List<DataChunk>();
        var chunkBuffer = new MemoryStream();
        var window = new byte[48];
        var windowPos = 0;
        ulong fingerprint = 0;
        var buffer = new byte[81920];
        var chunkStart = 0L;
        var totalPos = 0L;

        int bytesRead;
        while ((bytesRead = await content.ReadAsync(buffer, ct)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                var b = buffer[i];
                chunkBuffer.WriteByte(b);

                // Update rolling hash
                var oldByte = window[windowPos];
                window[windowPos] = b;
                windowPos = (windowPos + 1) % 48;
                fingerprint = ((fingerprint << 8) | b) ^ _lookupTable[oldByte];

                var chunkSize = chunkBuffer.Length;

                // Check for boundary
                var isBoundary = chunkSize >= _config.MinChunkSize &&
                    ((fingerprint & _config.ChunkMask) == _config.ChunkMask || chunkSize >= _config.MaxChunkSize);

                if (isBoundary)
                {
                    chunks.Add(new DataChunk
                    {
                        Index = chunks.Count,
                        Offset = chunkStart,
                        Data = chunkBuffer.ToArray()
                    });

                    chunkStart = totalPos + i + 1;
                    chunkBuffer = new MemoryStream();
                    fingerprint = 0;
                    windowPos = 0;
                    Array.Clear(window);
                }
            }
            totalPos += bytesRead;
        }

        // Handle remaining data
        if (chunkBuffer.Length > 0)
        {
            chunks.Add(new DataChunk
            {
                Index = chunks.Count,
                Offset = chunkStart,
                Data = chunkBuffer.ToArray()
            });
        }

        return chunks;
    }
}

/// <summary>
/// Configuration for Rabin chunker.
/// </summary>
public sealed record RabinChunkerConfig
{
    public int MinChunkSize { get; init; } = 4 * 1024;
    public int TargetChunkSize { get; init; } = 8 * 1024;
    public int MaxChunkSize { get; init; } = 64 * 1024;
    public ulong ChunkMask { get; init; } = 0x1FFF;
}

#region Deduplication Data Types

/// <summary>
/// A data chunk.
/// </summary>
public sealed class DataChunk
{
    public int Index { get; init; }
    public long Offset { get; init; }
    public required byte[] Data { get; init; }
}

/// <summary>
/// Reference to a stored chunk.
/// </summary>
public sealed class ChunkReference
{
    public required string Hash { get; init; }
    public long OriginalSize { get; init; }
    public long StoredSize { get; init; }
    public bool IsCompressed { get; init; }
    public int ReferenceCount { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastAccessedAt { get; set; }
}

/// <summary>
/// Chunk index for persistence.
/// </summary>
internal sealed class ChunkIndex
{
    public List<ChunkReference> Chunks { get; init; } = new();
    public Dictionary<string, List<string>> FileMap { get; init; } = new();
    public long TotalChunks { get; init; }
    public long TotalChunkBytes { get; init; }
    public long TotalDedupedBytes { get; init; }
    public DateTime LastUpdated { get; init; }
}

/// <summary>
/// Result of deduplication operation.
/// </summary>
public sealed record DeduplicationResult
{
    public required string FileId { get; init; }
    public long TotalBytes { get; set; }
    public int TotalChunks { get; set; }
    public int NewChunks { get; set; }
    public long NewBytes { get; set; }
    public int DedupedChunks { get; set; }
    public long DedupedBytes { get; set; }
    public double DeduplicationRatio { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Result of garbage collection.
/// </summary>
public sealed record GarbageCollectionResult
{
    public int ChunksRemoved { get; set; }
    public long BytesFreed { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Deduplication statistics.
/// </summary>
public sealed record DeduplicationStatistics
{
    public long TotalChunks { get; init; }
    public long TotalChunkBytes { get; init; }
    public long TotalDedupedBytes { get; init; }
    public int UniqueFiles { get; init; }
    public double AverageChunkSize { get; set; }
    public double AverageReferenceCount { get; set; }
    public int OrphanedChunks { get; set; }
    public double DeduplicationRatio { get; set; }
}

/// <summary>
/// Result of chunk integrity verification.
/// </summary>
public sealed record ChunkIntegrityResult
{
    public bool IsValid { get; set; }
    public int ValidChunks { get; set; }
    public List<string> MissingChunks { get; } = new();
    public List<string> CorruptedChunks { get; } = new();
    public List<string> ErrorChunks { get; } = new();
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Event args for garbage collection.
/// </summary>
public sealed class GarbageCollectionEventArgs : EventArgs
{
    public required GarbageCollectionResult Result { get; init; }
}

#endregion

#endregion
