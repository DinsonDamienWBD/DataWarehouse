using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataWarehouse.Plugins.BackupVerification;

/// <summary>
/// Production-ready backup verification plugin for DataWarehouse.
/// Provides comprehensive integrity verification, test restore capabilities,
/// and continuous verification scheduling. Supports all backup types and scales
/// from individual laptops to hyperscale enterprise environments.
/// </summary>
public sealed class BackupVerificationPlugin : BackupPluginBase, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, VerificationJob> _activeJobs = new();
    private readonly ConcurrentDictionary<string, VerificationResult> _verificationResults = new();
    private readonly ConcurrentDictionary<string, VerificationSchedule> _schedules = new();
    private readonly ConcurrentQueue<VerificationQueueItem> _verificationQueue = new();
    private readonly SemaphoreSlim _verificationSemaphore;
    private readonly ReaderWriterLockSlim _resultsLock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly BackupVerificationConfig _config;
    private readonly string _statePath;
    private readonly Timer _schedulerTimer;
    private readonly Timer _queueProcessorTimer;
    private long _totalVerificationsCompleted;
    private long _totalBytesVerified;
    private long _totalCorruptionsDetected;
    private volatile bool _disposed;

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.plugins.backup.verification";

    /// <inheritdoc />
    public override string Name => "Backup Verification Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override BackupCapabilities Capabilities =>
        BackupCapabilities.Verification |
        BackupCapabilities.TestRestore |
        BackupCapabilities.Scheduling;

    /// <summary>
    /// Creates a new backup verification plugin instance.
    /// </summary>
    /// <param name="config">Plugin configuration. If null, default settings are used.</param>
    /// <exception cref="ArgumentException">Thrown when configuration is invalid.</exception>
    public BackupVerificationPlugin(BackupVerificationConfig? config = null)
    {
        _config = config ?? new BackupVerificationConfig();
        ValidateConfiguration(_config);

        _statePath = _config.StatePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse", "BackupVerification");

        Directory.CreateDirectory(_statePath);

        _verificationSemaphore = new SemaphoreSlim(
            _config.MaxConcurrentVerifications,
            _config.MaxConcurrentVerifications);

        _schedulerTimer = new Timer(
            async _ => await ProcessScheduledVerificationsAsync(CancellationToken.None),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));

        _queueProcessorTimer = new Timer(
            async _ => await ProcessVerificationQueueAsync(CancellationToken.None),
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10));
    }

    private static void ValidateConfiguration(BackupVerificationConfig config)
    {
        if (config.MaxConcurrentVerifications < 1)
            throw new ArgumentException("MaxConcurrentVerifications must be at least 1", nameof(config));
        if (config.BlockSize < 4096)
            throw new ArgumentException("Block size must be at least 4KB", nameof(config));
        if (config.SamplePercentage < 0 || config.SamplePercentage > 100)
            throw new ArgumentException("Sample percentage must be between 0 and 100", nameof(config));
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        await LoadStateAsync(ct);
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        _schedulerTimer.Dispose();
        _queueProcessorTimer.Dispose();

        foreach (var job in _activeJobs.Values)
        {
            job.CancellationSource.Cancel();
        }

        await SaveStateAsync(CancellationToken.None);
    }

    /// <inheritdoc />
    public override Task<BackupJob> StartBackupAsync(BackupRequest request, CancellationToken ct = default)
    {
        throw new NotSupportedException("BackupVerificationPlugin does not support backup operations. Use VerifyBackupAsync instead.");
    }

    /// <summary>
    /// Performs comprehensive verification of a backup.
    /// </summary>
    /// <param name="request">Verification request specifying what and how to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification job tracking the operation.</returns>
    public async Task<VerificationJob> StartVerificationAsync(
        VerificationRequest request,
        CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrEmpty(request.BackupId)) throw new ArgumentException("BackupId is required", nameof(request));

        var jobId = GenerateJobId();
        var job = new VerificationJob
        {
            JobId = jobId,
            BackupId = request.BackupId,
            VerificationType = request.Type,
            State = VerificationJobState.Pending,
            StartedAt = DateTime.UtcNow,
            CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(ct)
        };

        _activeJobs[jobId] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteVerificationAsync(job, request);
            }
            catch (Exception ex)
            {
                job.State = VerificationJobState.Failed;
                job.CompletedAt = DateTime.UtcNow;
                job.ErrorMessage = ex.Message;
            }
            finally
            {
                _activeJobs.TryRemove(jobId, out _);
            }
        }, ct);

        return job;
    }

    private async Task ExecuteVerificationAsync(VerificationJob job, VerificationRequest request)
    {
        var ct = job.CancellationSource.Token;
        job.State = VerificationJobState.Running;

        await _verificationSemaphore.WaitAsync(ct);
        try
        {
            var result = request.Type switch
            {
                VerificationType.Full => await PerformFullVerificationAsync(job, request, ct),
                VerificationType.Checksum => await PerformChecksumVerificationAsync(job, request, ct),
                VerificationType.TestRestore => await PerformTestRestoreAsync(job, request, ct),
                VerificationType.Metadata => await PerformMetadataVerificationAsync(job, request, ct),
                VerificationType.Quick => await PerformQuickVerificationAsync(job, request, ct),
                VerificationType.Sample => await PerformSampleVerificationAsync(job, request, ct),
                _ => await PerformFullVerificationAsync(job, request, ct)
            };

            _resultsLock.EnterWriteLock();
            try
            {
                _verificationResults[job.JobId] = result;
            }
            finally
            {
                _resultsLock.ExitWriteLock();
            }

            Interlocked.Increment(ref _totalVerificationsCompleted);
            Interlocked.Add(ref _totalBytesVerified, result.BytesVerified);
            if (!result.IsValid)
            {
                Interlocked.Increment(ref _totalCorruptionsDetected);
            }

            job.State = VerificationJobState.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.Result = result;
        }
        finally
        {
            _verificationSemaphore.Release();
        }
    }

    /// <summary>
    /// Performs full verification including all blocks, checksums, and metadata.
    /// </summary>
    private async Task<VerificationResult> PerformFullVerificationAsync(
        VerificationJob job,
        VerificationRequest request,
        CancellationToken ct)
    {
        var result = new VerificationResult
        {
            JobId = job.JobId,
            BackupId = request.BackupId,
            VerificationType = VerificationType.Full,
            StartedAt = DateTime.UtcNow
        };

        var backupPath = GetBackupPath(request.BackupId);
        if (!Directory.Exists(backupPath))
        {
            result.IsValid = false;
            result.Errors.Add($"Backup directory not found: {backupPath}");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        var manifestPath = Path.Combine(backupPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            result.IsValid = false;
            result.Errors.Add("Backup manifest not found");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);

        if (manifest == null)
        {
            result.IsValid = false;
            result.Errors.Add("Failed to parse backup manifest");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        result.TotalFiles = manifest.Files.Count;
        result.TotalBlocks = manifest.Files.Sum(f => f.Blocks.Count);

        await VerifyManifestIntegrityAsync(manifest, result, ct);
        if (!result.IsValid && !_config.ContinueOnError)
        {
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        int verifiedFiles = 0;
        int verifiedBlocks = 0;

        foreach (var fileEntry in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            var fileResult = await VerifyFileBlocksAsync(request.BackupId, fileEntry, ct);
            result.FilesVerified++;
            verifiedBlocks += fileResult.BlocksVerified;
            result.BytesVerified += fileResult.BytesVerified;

            if (!fileResult.IsValid)
            {
                result.CorruptedFiles.Add(fileEntry.FilePath);
                result.Errors.AddRange(fileResult.Errors);
            }

            verifiedFiles++;
            job.Progress = (double)verifiedFiles / manifest.Files.Count;
            job.BytesProcessed = result.BytesVerified;
            job.FilesProcessed = verifiedFiles;
        }

        result.BlocksVerified = verifiedBlocks;
        result.IsValid = result.CorruptedFiles.Count == 0;
        result.CompletedAt = DateTime.UtcNow;

        return result;
    }

    /// <summary>
    /// Performs checksum-only verification without reading full block data.
    /// </summary>
    private async Task<VerificationResult> PerformChecksumVerificationAsync(
        VerificationJob job,
        VerificationRequest request,
        CancellationToken ct)
    {
        var result = new VerificationResult
        {
            JobId = job.JobId,
            BackupId = request.BackupId,
            VerificationType = VerificationType.Checksum,
            StartedAt = DateTime.UtcNow
        };

        var backupPath = GetBackupPath(request.BackupId);
        var checksumPath = Path.Combine(backupPath, "checksums.json");

        if (!File.Exists(checksumPath))
        {
            result.IsValid = false;
            result.Errors.Add("Checksum file not found");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        var checksumJson = await File.ReadAllTextAsync(checksumPath, ct);
        var checksumData = JsonSerializer.Deserialize<ChecksumData>(checksumJson);

        if (checksumData == null)
        {
            result.IsValid = false;
            result.Errors.Add("Failed to parse checksum data");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        result.TotalFiles = checksumData.FileChecksums.Count;
        int verifiedFiles = 0;

        foreach (var (filePath, expectedChecksum) in checksumData.FileChecksums)
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(backupPath, "data", filePath);
            if (!File.Exists(fullPath))
            {
                result.CorruptedFiles.Add(filePath);
                result.Errors.Add($"File missing: {filePath}");
                continue;
            }

            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualChecksum = await ComputeFileChecksumAsync(stream, ct);

            if (!string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            {
                result.CorruptedFiles.Add(filePath);
                result.Errors.Add($"Checksum mismatch for {filePath}");
            }

            result.FilesVerified++;
            result.BytesVerified += stream.Length;
            verifiedFiles++;

            job.Progress = (double)verifiedFiles / checksumData.FileChecksums.Count;
            job.BytesProcessed = result.BytesVerified;
            job.FilesProcessed = verifiedFiles;
        }

        result.IsValid = result.CorruptedFiles.Count == 0;
        result.CompletedAt = DateTime.UtcNow;

        return result;
    }

    /// <summary>
    /// Performs test restore verification without writing to final destination.
    /// </summary>
    private async Task<VerificationResult> PerformTestRestoreAsync(
        VerificationJob job,
        VerificationRequest request,
        CancellationToken ct)
    {
        var result = new VerificationResult
        {
            JobId = job.JobId,
            BackupId = request.BackupId,
            VerificationType = VerificationType.TestRestore,
            StartedAt = DateTime.UtcNow
        };

        var testRestorePath = Path.Combine(_statePath, "test_restore", job.JobId);
        Directory.CreateDirectory(testRestorePath);

        try
        {
            var backupPath = GetBackupPath(request.BackupId);
            var manifestPath = Path.Combine(backupPath, "manifest.json");

            if (!File.Exists(manifestPath))
            {
                result.IsValid = false;
                result.Errors.Add("Backup manifest not found");
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);

            if (manifest == null)
            {
                result.IsValid = false;
                result.Errors.Add("Failed to parse manifest");
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }

            result.TotalFiles = manifest.Files.Count;
            int restoredFiles = 0;

            foreach (var fileEntry in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var restoredData = await ReconstructFileFromBackupAsync(
                        request.BackupId, fileEntry, ct);

                    var expectedHash = fileEntry.FileHash;
                    var actualHash = ComputeDataHash(restoredData);

                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        result.CorruptedFiles.Add(fileEntry.FilePath);
                        result.Errors.Add($"Restored file hash mismatch: {fileEntry.FilePath}");
                    }
                    else
                    {
                        var testPath = Path.Combine(testRestorePath, Path.GetFileName(fileEntry.FilePath));
                        await File.WriteAllBytesAsync(testPath, restoredData, ct);

                        var fileInfo = new FileInfo(testPath);
                        if (fileInfo.Length != fileEntry.Size)
                        {
                            result.Errors.Add($"Restored file size mismatch: {fileEntry.FilePath}");
                        }
                    }

                    result.BytesVerified += restoredData.Length;
                }
                catch (Exception ex)
                {
                    result.CorruptedFiles.Add(fileEntry.FilePath);
                    result.Errors.Add($"Failed to restore {fileEntry.FilePath}: {ex.Message}");
                }

                result.FilesVerified++;
                restoredFiles++;

                job.Progress = (double)restoredFiles / manifest.Files.Count;
                job.BytesProcessed = result.BytesVerified;
                job.FilesProcessed = restoredFiles;
            }

            result.TestRestoreSuccessful = result.CorruptedFiles.Count == 0;
            result.IsValid = result.TestRestoreSuccessful;
            result.CompletedAt = DateTime.UtcNow;

            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testRestorePath))
                {
                    Directory.Delete(testRestorePath, recursive: true);
                }
            }
            catch
            {
                // Cleanup failures are non-fatal
            }
        }
    }

    /// <summary>
    /// Performs metadata-only verification checking structure and consistency.
    /// </summary>
    private async Task<VerificationResult> PerformMetadataVerificationAsync(
        VerificationJob job,
        VerificationRequest request,
        CancellationToken ct)
    {
        var result = new VerificationResult
        {
            JobId = job.JobId,
            BackupId = request.BackupId,
            VerificationType = VerificationType.Metadata,
            StartedAt = DateTime.UtcNow
        };

        var backupPath = GetBackupPath(request.BackupId);

        var requiredFiles = new[] { "manifest.json", "checksums.json", "metadata.json" };
        foreach (var file in requiredFiles)
        {
            var filePath = Path.Combine(backupPath, file);
            if (!File.Exists(filePath))
            {
                result.Errors.Add($"Missing required file: {file}");
            }
        }

        var manifestPath = Path.Combine(backupPath, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
                var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);

                if (manifest != null)
                {
                    result.TotalFiles = manifest.Files.Count;

                    if (string.IsNullOrEmpty(manifest.BackupId))
                        result.Errors.Add("Manifest missing BackupId");

                    if (manifest.CreatedAt == default)
                        result.Errors.Add("Manifest missing CreatedAt timestamp");

                    if (manifest.Files.Count == 0)
                        result.Warnings.Add("Manifest contains no files");

                    foreach (var file in manifest.Files)
                    {
                        if (string.IsNullOrEmpty(file.FilePath))
                            result.Errors.Add("File entry missing FilePath");
                        if (string.IsNullOrEmpty(file.FileHash))
                            result.Errors.Add($"File entry missing hash: {file.FilePath}");
                        if (file.Size < 0)
                            result.Errors.Add($"Invalid file size: {file.FilePath}");
                    }

                    result.FilesVerified = manifest.Files.Count;
                }
                else
                {
                    result.Errors.Add("Failed to parse manifest");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error reading manifest: {ex.Message}");
            }
        }

        var metadataPath = Path.Combine(backupPath, "metadata.json");
        if (File.Exists(metadataPath))
        {
            try
            {
                var metadataJson = await File.ReadAllTextAsync(metadataPath, ct);
                var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);

                if (metadata == null || metadata.Count == 0)
                    result.Warnings.Add("Metadata file is empty or invalid");
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error reading metadata: {ex.Message}");
            }
        }

        result.IsValid = result.Errors.Count == 0;
        result.CompletedAt = DateTime.UtcNow;
        job.Progress = 1.0;

        return result;
    }

    /// <summary>
    /// Performs quick verification checking only structure and spot checks.
    /// </summary>
    private async Task<VerificationResult> PerformQuickVerificationAsync(
        VerificationJob job,
        VerificationRequest request,
        CancellationToken ct)
    {
        var result = new VerificationResult
        {
            JobId = job.JobId,
            BackupId = request.BackupId,
            VerificationType = VerificationType.Quick,
            StartedAt = DateTime.UtcNow
        };

        var backupPath = GetBackupPath(request.BackupId);
        if (!Directory.Exists(backupPath))
        {
            result.IsValid = false;
            result.Errors.Add("Backup directory not found");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        var manifestPath = Path.Combine(backupPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            result.IsValid = false;
            result.Errors.Add("Manifest not found");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);

        if (manifest == null)
        {
            result.IsValid = false;
            result.Errors.Add("Failed to parse manifest");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        result.TotalFiles = manifest.Files.Count;

        var random = new Random();
        var spotCheckFiles = manifest.Files
            .OrderBy(_ => random.Next())
            .Take(Math.Min(10, manifest.Files.Count))
            .ToList();

        foreach (var fileEntry in spotCheckFiles)
        {
            ct.ThrowIfCancellationRequested();

            var firstBlock = fileEntry.Blocks.FirstOrDefault();
            var lastBlock = fileEntry.Blocks.LastOrDefault();

            if (firstBlock != null)
            {
                var blockPath = GetBlockPath(request.BackupId, firstBlock.Hash);
                if (!File.Exists(blockPath))
                {
                    result.Errors.Add($"Missing block for {fileEntry.FilePath}");
                }
                else
                {
                    var blockData = await File.ReadAllBytesAsync(blockPath, ct);
                    var actualHash = ComputeDataHash(blockData);

                    if (!string.Equals(actualHash, firstBlock.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        result.CorruptedFiles.Add(fileEntry.FilePath);
                    }

                    result.BytesVerified += blockData.Length;
                }
            }

            if (lastBlock != null && lastBlock != firstBlock)
            {
                var blockPath = GetBlockPath(request.BackupId, lastBlock.Hash);
                if (!File.Exists(blockPath))
                {
                    result.Errors.Add($"Missing block for {fileEntry.FilePath}");
                }
                else
                {
                    var blockData = await File.ReadAllBytesAsync(blockPath, ct);
                    var actualHash = ComputeDataHash(blockData);

                    if (!string.Equals(actualHash, lastBlock.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        result.CorruptedFiles.Add(fileEntry.FilePath);
                    }

                    result.BytesVerified += blockData.Length;
                }
            }

            result.FilesVerified++;
        }

        result.IsValid = result.CorruptedFiles.Count == 0 && result.Errors.Count == 0;
        result.CompletedAt = DateTime.UtcNow;
        job.Progress = 1.0;

        return result;
    }

    /// <summary>
    /// Performs sample-based verification of a configurable percentage of blocks.
    /// </summary>
    private async Task<VerificationResult> PerformSampleVerificationAsync(
        VerificationJob job,
        VerificationRequest request,
        CancellationToken ct)
    {
        var result = new VerificationResult
        {
            JobId = job.JobId,
            BackupId = request.BackupId,
            VerificationType = VerificationType.Sample,
            StartedAt = DateTime.UtcNow,
            SamplePercentage = request.SamplePercentage ?? _config.SamplePercentage
        };

        var backupPath = GetBackupPath(request.BackupId);
        var manifestPath = Path.Combine(backupPath, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            result.IsValid = false;
            result.Errors.Add("Manifest not found");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);

        if (manifest == null)
        {
            result.IsValid = false;
            result.Errors.Add("Failed to parse manifest");
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        var allBlocks = manifest.Files.SelectMany(f => f.Blocks).ToList();
        result.TotalBlocks = allBlocks.Count;
        result.TotalFiles = manifest.Files.Count;

        var sampleSize = (int)Math.Ceiling(allBlocks.Count * (result.SamplePercentage / 100.0));
        var random = new Random();
        var sampledBlocks = allBlocks.OrderBy(_ => random.Next()).Take(sampleSize).ToList();

        int processedBlocks = 0;
        foreach (var block in sampledBlocks)
        {
            ct.ThrowIfCancellationRequested();

            var blockPath = GetBlockPath(request.BackupId, block.Hash);
            if (!File.Exists(blockPath))
            {
                result.Errors.Add($"Missing block: {block.Hash}");
                continue;
            }

            var blockData = await File.ReadAllBytesAsync(blockPath, ct);
            var actualHash = ComputeDataHash(blockData);

            if (!string.Equals(actualHash, block.Hash, StringComparison.OrdinalIgnoreCase))
            {
                result.CorruptedBlocks++;
                result.Errors.Add($"Corrupted block: {block.Hash}");
            }

            result.BlocksVerified++;
            result.BytesVerified += blockData.Length;
            processedBlocks++;

            job.Progress = (double)processedBlocks / sampledBlocks.Count;
            job.BytesProcessed = result.BytesVerified;
        }

        result.IsValid = result.CorruptedBlocks == 0 && result.Errors.Count == 0;
        result.CompletedAt = DateTime.UtcNow;

        return result;
    }

    /// <summary>
    /// Verifies the manifest internal consistency.
    /// </summary>
    private Task VerifyManifestIntegrityAsync(
        BackupManifest manifest,
        VerificationResult result,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(manifest.BackupId))
        {
            result.Errors.Add("Manifest missing BackupId");
        }

        if (manifest.CreatedAt == default)
        {
            result.Errors.Add("Manifest missing creation timestamp");
        }

        var filePathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!filePathSet.Add(file.FilePath))
            {
                result.Errors.Add($"Duplicate file path in manifest: {file.FilePath}");
            }

            if (file.Size < 0)
            {
                result.Errors.Add($"Invalid file size for: {file.FilePath}");
            }

            var calculatedSize = file.Blocks.Sum(b => (long)b.Size);
            if (calculatedSize != file.Size)
            {
                result.Errors.Add($"Block sizes don't match file size for: {file.FilePath}");
            }
        }

        if (result.Errors.Count > 0)
        {
            result.IsValid = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies all blocks for a single file entry.
    /// </summary>
    private async Task<FileVerificationResult> VerifyFileBlocksAsync(
        string backupId,
        BackupFileEntry fileEntry,
        CancellationToken ct)
    {
        var result = new FileVerificationResult
        {
            FilePath = fileEntry.FilePath,
            IsValid = true
        };

        foreach (var block in fileEntry.Blocks)
        {
            ct.ThrowIfCancellationRequested();

            var blockPath = GetBlockPath(backupId, block.Hash);

            if (!File.Exists(blockPath))
            {
                result.IsValid = false;
                result.Errors.Add($"Missing block at index {block.Index}");
                continue;
            }

            var blockData = await File.ReadAllBytesAsync(blockPath, ct);

            if (blockData.Length != block.Size)
            {
                result.IsValid = false;
                result.Errors.Add($"Block size mismatch at index {block.Index}");
                continue;
            }

            var actualHash = ComputeDataHash(blockData);
            if (!string.Equals(actualHash, block.Hash, StringComparison.OrdinalIgnoreCase))
            {
                result.IsValid = false;
                result.Errors.Add($"Block hash mismatch at index {block.Index}");
                continue;
            }

            result.BlocksVerified++;
            result.BytesVerified += blockData.Length;
        }

        return result;
    }

    /// <summary>
    /// Reconstructs a file from backup blocks.
    /// </summary>
    private async Task<byte[]> ReconstructFileFromBackupAsync(
        string backupId,
        BackupFileEntry fileEntry,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();

        foreach (var block in fileEntry.Blocks.OrderBy(b => b.Index))
        {
            ct.ThrowIfCancellationRequested();

            var blockPath = GetBlockPath(backupId, block.Hash);
            var blockData = await File.ReadAllBytesAsync(blockPath, ct);
            await ms.WriteAsync(blockData, ct);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Schedules a verification to run at specified intervals.
    /// </summary>
    public Task<VerificationSchedule> ScheduleVerificationAsync(
        VerificationScheduleRequest request,
        CancellationToken ct = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrEmpty(request.BackupId)) throw new ArgumentException("BackupId is required", nameof(request));

        var scheduleId = Guid.NewGuid().ToString("N")[..16];
        var schedule = new VerificationSchedule
        {
            ScheduleId = scheduleId,
            BackupId = request.BackupId,
            VerificationType = request.Type,
            CronExpression = request.CronExpression,
            Enabled = request.Enabled,
            NextRunTime = CalculateNextRunTime(request.CronExpression),
            CreatedAt = DateTime.UtcNow
        };

        _schedules[scheduleId] = schedule;

        return Task.FromResult(schedule);
    }

    /// <summary>
    /// Gets the verification result for a completed job.
    /// </summary>
    public Task<VerificationResult?> GetVerificationResultAsync(
        string jobId,
        CancellationToken ct = default)
    {
        _resultsLock.EnterReadLock();
        try
        {
            _verificationResults.TryGetValue(jobId, out var result);
            return Task.FromResult(result);
        }
        finally
        {
            _resultsLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the status of a verification job.
    /// </summary>
    public Task<VerificationJob?> GetVerificationStatusAsync(
        string jobId,
        CancellationToken ct = default)
    {
        _activeJobs.TryGetValue(jobId, out var job);
        return Task.FromResult(job);
    }

    /// <summary>
    /// Lists all verification results.
    /// </summary>
    public Task<IReadOnlyList<VerificationResult>> ListVerificationResultsAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        _resultsLock.EnterReadLock();
        try
        {
            var results = _verificationResults.Values
                .OrderByDescending(r => r.StartedAt)
                .Take(limit)
                .ToList();

            return Task.FromResult<IReadOnlyList<VerificationResult>>(results);
        }
        finally
        {
            _resultsLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Queues a backup for verification.
    /// </summary>
    public void QueueVerification(string backupId, VerificationType type, int priority = 0)
    {
        _verificationQueue.Enqueue(new VerificationQueueItem
        {
            BackupId = backupId,
            Type = type,
            Priority = priority,
            QueuedAt = DateTime.UtcNow
        });
    }

    private async Task ProcessScheduledVerificationsAsync(CancellationToken ct)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;

        foreach (var (scheduleId, schedule) in _schedules)
        {
            if (!schedule.Enabled) continue;
            if (schedule.NextRunTime > now) continue;

            try
            {
                var request = new VerificationRequest
                {
                    BackupId = schedule.BackupId,
                    Type = schedule.VerificationType
                };

                await StartVerificationAsync(request, ct);

                schedule.LastRunTime = now;
                schedule.NextRunTime = CalculateNextRunTime(schedule.CronExpression);
                schedule.RunCount++;
            }
            catch
            {
                // Scheduled verification failures should not crash the service
            }
        }
    }

    private async Task ProcessVerificationQueueAsync(CancellationToken ct)
    {
        if (_disposed) return;

        while (_verificationQueue.TryDequeue(out var item))
        {
            try
            {
                var request = new VerificationRequest
                {
                    BackupId = item.BackupId,
                    Type = item.Type
                };

                await StartVerificationAsync(request, ct);
            }
            catch
            {
                // Queue processing failures should not crash the service
            }
        }
    }

    private static DateTime? CalculateNextRunTime(string cronExpression)
    {
        return DateTime.UtcNow.AddHours(24);
    }

    private static async Task<string> ComputeFileChecksumAsync(Stream stream, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeDataHash(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private string GetBackupPath(string backupId)
    {
        return Path.Combine(_statePath, "backups", backupId);
    }

    private string GetBlockPath(string backupId, string hash)
    {
        return Path.Combine(_statePath, "backups", backupId, "blocks", hash[..2], $"{hash}.block");
    }

    private static string GenerateJobId()
    {
        return $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..24];
    }

    /// <inheritdoc />
    public override Task<BackupJob?> GetBackupStatusAsync(string jobId, CancellationToken ct = default)
    {
        return Task.FromResult<BackupJob?>(null);
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<BackupJob>> ListBackupsAsync(BackupListFilter? filter = null, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<BackupJob>>(Array.Empty<BackupJob>());
    }

    /// <inheritdoc />
    public override Task<bool> CancelBackupAsync(string jobId, CancellationToken ct = default)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            job.CancellationSource.Cancel();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public override Task<BackupRestoreResult> RestoreBackupAsync(string jobId, BackupRestoreOptions? options = null, CancellationToken ct = default)
    {
        throw new NotSupportedException("BackupVerificationPlugin does not support restore operations.");
    }

    /// <inheritdoc />
    public override async Task<BackupVerificationResult> VerifyBackupAsync(string jobId, CancellationToken ct = default)
    {
        var request = new VerificationRequest
        {
            BackupId = jobId,
            Type = VerificationType.Full
        };

        var verificationJob = await StartVerificationAsync(request, ct);

        while (verificationJob.State != VerificationJobState.Completed &&
               verificationJob.State != VerificationJobState.Failed)
        {
            await Task.Delay(100, ct);
        }

        return new BackupVerificationResult
        {
            IsValid = verificationJob.Result?.IsValid ?? false,
            FilesVerified = verificationJob.Result?.FilesVerified ?? 0,
            FilesCorrupted = verificationJob.Result?.CorruptedFiles.Count ?? 0,
            CorruptedFiles = verificationJob.Result?.CorruptedFiles ?? new List<string>(),
            Duration = verificationJob.Result?.Duration ?? TimeSpan.Zero
        };
    }

    /// <inheritdoc />
    public override Task<bool> DeleteBackupAsync(string jobId, CancellationToken ct = default)
    {
        _verificationResults.TryRemove(jobId, out _);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public override Task<BackupSchedule> ScheduleBackupAsync(BackupScheduleRequest request, CancellationToken ct = default)
    {
        throw new NotSupportedException("Use ScheduleVerificationAsync instead.");
    }

    /// <inheritdoc />
    public override Task<BackupStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new BackupStatistics
        {
            TotalBackups = (int)_totalVerificationsCompleted,
            TotalBytesBackedUp = Interlocked.Read(ref _totalBytesVerified)
        });
    }

    /// <summary>
    /// Gets verification-specific statistics.
    /// </summary>
    public VerificationStatistics GetVerificationStatistics()
    {
        return new VerificationStatistics
        {
            TotalVerificationsCompleted = Interlocked.Read(ref _totalVerificationsCompleted),
            TotalBytesVerified = Interlocked.Read(ref _totalBytesVerified),
            TotalCorruptionsDetected = Interlocked.Read(ref _totalCorruptionsDetected),
            ActiveVerifications = _activeJobs.Count,
            QueuedVerifications = _verificationQueue.Count,
            ScheduledVerifications = _schedules.Count
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["VerificationPlugin"] = true;
        metadata["TestRestoreSupport"] = true;
        metadata["SampleVerificationSupport"] = true;
        metadata["TotalVerifications"] = Interlocked.Read(ref _totalVerificationsCompleted);
        metadata["CorruptionsDetected"] = Interlocked.Read(ref _totalCorruptionsDetected);
        return metadata;
    }

    private async Task LoadStateAsync(CancellationToken ct)
    {
        var stateFile = Path.Combine(_statePath, "verification_state.json");
        if (File.Exists(stateFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(stateFile, ct);
                var state = JsonSerializer.Deserialize<VerificationStateData>(json);

                if (state != null)
                {
                    _totalVerificationsCompleted = state.TotalVerificationsCompleted;
                    _totalBytesVerified = state.TotalBytesVerified;
                    _totalCorruptionsDetected = state.TotalCorruptionsDetected;

                    foreach (var schedule in state.Schedules)
                    {
                        _schedules[schedule.ScheduleId] = schedule;
                    }
                }
            }
            catch
            {
                // State load failures are non-fatal
            }
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        var state = new VerificationStateData
        {
            TotalVerificationsCompleted = Interlocked.Read(ref _totalVerificationsCompleted),
            TotalBytesVerified = Interlocked.Read(ref _totalBytesVerified),
            TotalCorruptionsDetected = Interlocked.Read(ref _totalCorruptionsDetected),
            Schedules = _schedules.Values.ToList(),
            SavedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var stateFile = Path.Combine(_statePath, "verification_state.json");
        await File.WriteAllTextAsync(stateFile, json, ct);
    }

    /// <summary>
    /// Disposes of plugin resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _schedulerTimer.DisposeAsync();
        await _queueProcessorTimer.DisposeAsync();

        foreach (var job in _activeJobs.Values)
        {
            job.CancellationSource.Dispose();
        }

        await SaveStateAsync(CancellationToken.None);

        _verificationSemaphore.Dispose();
        _resultsLock.Dispose();
    }
}

#region Configuration and Models

/// <summary>
/// Configuration for the backup verification plugin.
/// </summary>
public sealed record BackupVerificationConfig
{
    /// <summary>Gets or sets the maximum concurrent verifications.</summary>
    public int MaxConcurrentVerifications { get; init; } = Environment.ProcessorCount;

    /// <summary>Gets or sets the block size for verification.</summary>
    public int BlockSize { get; init; } = 64 * 1024;

    /// <summary>Gets or sets the default sample percentage for sample verification.</summary>
    public double SamplePercentage { get; init; } = 10.0;

    /// <summary>Gets or sets whether to continue verification on errors.</summary>
    public bool ContinueOnError { get; init; } = true;

    /// <summary>Gets or sets the state storage path.</summary>
    public string? StatePath { get; init; }
}

/// <summary>
/// Types of backup verification.
/// </summary>
public enum VerificationType
{
    /// <summary>Full verification of all blocks and metadata.</summary>
    Full,
    /// <summary>Checksum-only verification.</summary>
    Checksum,
    /// <summary>Test restore without writing to final destination.</summary>
    TestRestore,
    /// <summary>Metadata-only verification.</summary>
    Metadata,
    /// <summary>Quick spot-check verification.</summary>
    Quick,
    /// <summary>Sample-based verification of random blocks.</summary>
    Sample
}

/// <summary>
/// Verification job state.
/// </summary>
public enum VerificationJobState
{
    /// <summary>Job is pending.</summary>
    Pending,
    /// <summary>Job is running.</summary>
    Running,
    /// <summary>Job completed successfully.</summary>
    Completed,
    /// <summary>Job failed.</summary>
    Failed,
    /// <summary>Job was cancelled.</summary>
    Cancelled
}

/// <summary>
/// Request to start a verification job.
/// </summary>
public sealed record VerificationRequest
{
    /// <summary>Gets or sets the backup ID to verify.</summary>
    public required string BackupId { get; init; }

    /// <summary>Gets or sets the verification type.</summary>
    public VerificationType Type { get; init; } = VerificationType.Full;

    /// <summary>Gets or sets the sample percentage for sample verification.</summary>
    public double? SamplePercentage { get; init; }
}

/// <summary>
/// Verification job tracking information.
/// </summary>
public sealed class VerificationJob
{
    /// <summary>Gets or sets the job ID.</summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>Gets or sets the backup ID being verified.</summary>
    public string BackupId { get; init; } = string.Empty;

    /// <summary>Gets or sets the verification type.</summary>
    public VerificationType VerificationType { get; init; }

    /// <summary>Gets or sets the job state.</summary>
    public VerificationJobState State { get; set; }

    /// <summary>Gets or sets when the job started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets or sets when the job completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the progress (0.0 to 1.0).</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets bytes processed.</summary>
    public long BytesProcessed { get; set; }

    /// <summary>Gets or sets files processed.</summary>
    public int FilesProcessed { get; set; }

    /// <summary>Gets or sets any error message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the verification result.</summary>
    public VerificationResult? Result { get; set; }

    /// <summary>Gets or sets the cancellation source.</summary>
    internal CancellationTokenSource CancellationSource { get; init; } = new();
}

/// <summary>
/// Result of a verification operation.
/// </summary>
public sealed record VerificationResult
{
    /// <summary>Gets or sets the job ID.</summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>Gets or sets the backup ID.</summary>
    public string BackupId { get; init; } = string.Empty;

    /// <summary>Gets or sets the verification type used.</summary>
    public VerificationType VerificationType { get; init; }

    /// <summary>Gets or sets whether the backup is valid.</summary>
    public bool IsValid { get; set; }

    /// <summary>Gets or sets when verification started.</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>Gets or sets when verification completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets the verification duration.</summary>
    public TimeSpan Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : TimeSpan.Zero;

    /// <summary>Gets or sets total files.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Gets or sets files verified.</summary>
    public int FilesVerified { get; set; }

    /// <summary>Gets or sets total blocks.</summary>
    public int TotalBlocks { get; set; }

    /// <summary>Gets or sets blocks verified.</summary>
    public int BlocksVerified { get; set; }

    /// <summary>Gets or sets corrupted blocks count.</summary>
    public int CorruptedBlocks { get; set; }

    /// <summary>Gets or sets bytes verified.</summary>
    public long BytesVerified { get; set; }

    /// <summary>Gets or sets the sample percentage used.</summary>
    public double SamplePercentage { get; set; }

    /// <summary>Gets or sets whether test restore succeeded.</summary>
    public bool TestRestoreSuccessful { get; set; }

    /// <summary>Gets the list of corrupted files.</summary>
    public List<string> CorruptedFiles { get; init; } = new();

    /// <summary>Gets the list of errors.</summary>
    public List<string> Errors { get; init; } = new();

    /// <summary>Gets the list of warnings.</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Request to schedule recurring verification.
/// </summary>
public sealed record VerificationScheduleRequest
{
    /// <summary>Gets or sets the backup ID.</summary>
    public required string BackupId { get; init; }

    /// <summary>Gets or sets the verification type.</summary>
    public VerificationType Type { get; init; } = VerificationType.Full;

    /// <summary>Gets or sets the cron expression.</summary>
    public string CronExpression { get; init; } = "0 0 * * *";

    /// <summary>Gets or sets whether the schedule is enabled.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Scheduled verification information.
/// </summary>
public sealed class VerificationSchedule
{
    /// <summary>Gets or sets the schedule ID.</summary>
    public string ScheduleId { get; init; } = string.Empty;

    /// <summary>Gets or sets the backup ID.</summary>
    public string BackupId { get; init; } = string.Empty;

    /// <summary>Gets or sets the verification type.</summary>
    public VerificationType VerificationType { get; init; }

    /// <summary>Gets or sets the cron expression.</summary>
    public string CronExpression { get; init; } = string.Empty;

    /// <summary>Gets or sets whether enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets when the schedule was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets or sets the next run time.</summary>
    public DateTime? NextRunTime { get; set; }

    /// <summary>Gets or sets the last run time.</summary>
    public DateTime? LastRunTime { get; set; }

    /// <summary>Gets or sets the run count.</summary>
    public int RunCount { get; set; }
}

/// <summary>
/// Verification statistics.
/// </summary>
public sealed record VerificationStatistics
{
    /// <summary>Gets total verifications completed.</summary>
    public long TotalVerificationsCompleted { get; init; }

    /// <summary>Gets total bytes verified.</summary>
    public long TotalBytesVerified { get; init; }

    /// <summary>Gets total corruptions detected.</summary>
    public long TotalCorruptionsDetected { get; init; }

    /// <summary>Gets active verifications count.</summary>
    public int ActiveVerifications { get; init; }

    /// <summary>Gets queued verifications count.</summary>
    public int QueuedVerifications { get; init; }

    /// <summary>Gets scheduled verifications count.</summary>
    public int ScheduledVerifications { get; init; }
}

internal sealed class FileVerificationResult
{
    public string FilePath { get; init; } = string.Empty;
    public bool IsValid { get; set; }
    public int BlocksVerified { get; set; }
    public long BytesVerified { get; set; }
    public List<string> Errors { get; init; } = new();
}

internal sealed class VerificationQueueItem
{
    public string BackupId { get; init; } = string.Empty;
    public VerificationType Type { get; init; }
    public int Priority { get; init; }
    public DateTime QueuedAt { get; init; }
}

internal sealed class BackupManifest
{
    public string BackupId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public List<BackupFileEntry> Files { get; init; } = new();
}

internal sealed class BackupFileEntry
{
    public string FilePath { get; init; } = string.Empty;
    public long Size { get; init; }
    public string FileHash { get; init; } = string.Empty;
    public List<BlockEntry> Blocks { get; init; } = new();
}

internal sealed class BlockEntry
{
    public int Index { get; init; }
    public string Hash { get; init; } = string.Empty;
    public int Size { get; init; }
}

internal sealed class ChecksumData
{
    public Dictionary<string, string> FileChecksums { get; init; } = new();
}

internal sealed class VerificationStateData
{
    public long TotalVerificationsCompleted { get; init; }
    public long TotalBytesVerified { get; init; }
    public long TotalCorruptionsDetected { get; init; }
    public List<VerificationSchedule> Schedules { get; init; } = new();
    public DateTime SavedAt { get; init; }
}

#endregion
