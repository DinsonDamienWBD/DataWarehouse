using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataWarehouse.Plugins.Backup.Providers;

/// <summary>
/// Synthetic full backup provider that creates full backups by merging
/// a base full backup with subsequent incremental backups, without
/// requiring a full re-scan of source data.
/// </summary>
public sealed class SyntheticFullBackupProvider : IBackupProvider, ISyntheticFullBackupProvider
{
    private readonly IBackupDestination _destination;
    private readonly SyntheticFullBackupConfig _config;
    private readonly ConcurrentDictionary<string, SyntheticFileEntry> _catalog = new();
    private readonly ConcurrentDictionary<long, BackupChainEntry> _backupChain = new();
    private readonly SemaphoreSlim _backupLock = new(1, 1);
    private readonly string _statePath;
    private long _sequence;
    private long _lastFullSequence;
    private volatile bool _disposed;

    public string ProviderId => "synthetic-full-backup-provider";
    public string Name => "Synthetic Full Backup Provider";
    public BackupProviderType ProviderType => BackupProviderType.SyntheticFull;
    public bool IsMonitoring => false;

    public event EventHandler<BackupProgressEventArgs>? ProgressChanged;
    public event EventHandler<BackupCompletedEventArgs>? BackupCompleted;

    public SyntheticFullBackupProvider(
        IBackupDestination destination,
        string statePath,
        SyntheticFullBackupConfig? config = null)
    {
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _statePath = statePath ?? throw new ArgumentNullException(nameof(statePath));
        _config = config ?? new SyntheticFullBackupConfig();

        Directory.CreateDirectory(_statePath);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _destination.InitializeAsync(ct);
        await LoadStateAsync();
    }

    public async Task<BackupResult> PerformFullBackupAsync(
        IEnumerable<string> paths,
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        var backupId = $"synthetic-full-{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _sequence)}";
        var result = new BackupResult
        {
            BackupId = backupId,
            BackupType = BackupProviderType.Full,
            Sequence = _sequence,
            StartedAt = DateTime.UtcNow
        };

        await _backupLock.WaitAsync(ct);
        try
        {
            var allFiles = new List<string>();
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                    allFiles.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
                else if (File.Exists(path))
                    allFiles.Add(path);
            }

            result.TotalFiles = allFiles.Count;
            var processedFiles = 0;

            foreach (var filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                if (!ShouldBackupFile(filePath, options)) continue;

                try
                {
                    await BackupFileAsync(filePath, backupId, result, ct);
                    processedFiles++;

                    RaiseProgressChanged(backupId, processedFiles, result.TotalFiles, result.TotalBytes, 0);
                }
                catch (Exception ex)
                {
                    result.Errors.Add(new BackupError { FilePath = filePath, Message = ex.Message });
                }
            }

            // Record this as a full backup in the chain
            _lastFullSequence = _sequence;
            _backupChain[_sequence] = new BackupChainEntry
            {
                Sequence = _sequence,
                BackupId = backupId,
                Type = BackupProviderType.Full,
                CreatedAt = DateTime.UtcNow,
                FileCount = processedFiles,
                TotalBytes = result.TotalBytes
            };

            result.Success = result.Errors.Count == 0;
            result.CompletedAt = DateTime.UtcNow;

            await SaveStateAsync(ct);
        }
        finally
        {
            _backupLock.Release();
        }

        RaiseBackupCompleted(result);
        return result;
    }

    public async Task<BackupResult> PerformIncrementalBackupAsync(
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        var backupId = $"synthetic-incr-{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _sequence)}";
        var result = new BackupResult
        {
            BackupId = backupId,
            BackupType = BackupProviderType.Incremental,
            Sequence = _sequence,
            StartedAt = DateTime.UtcNow
        };

        await _backupLock.WaitAsync(ct);
        try
        {
            var changedFiles = GetChangedFiles();

            foreach (var (filePath, changeType) in changedFiles)
            {
                ct.ThrowIfCancellationRequested();

                if (!ShouldBackupFile(filePath, options)) continue;

                try
                {
                    if (changeType == FileChangeType.Deleted)
                    {
                        _catalog.TryRemove(filePath, out _);
                        result.TotalFiles++;
                        continue;
                    }

                    await BackupFileAsync(filePath, backupId, result, ct);
                }
                catch (Exception ex)
                {
                    result.Errors.Add(new BackupError { FilePath = filePath, Message = ex.Message });
                }
            }

            // Record incremental in the chain
            _backupChain[_sequence] = new BackupChainEntry
            {
                Sequence = _sequence,
                BackupId = backupId,
                Type = BackupProviderType.Incremental,
                CreatedAt = DateTime.UtcNow,
                BaseSequence = _lastFullSequence,
                FileCount = (int)result.TotalFiles,
                TotalBytes = result.TotalBytes
            };

            result.Success = result.Errors.Count == 0;
            result.CompletedAt = DateTime.UtcNow;

            await SaveStateAsync(ct);
        }
        finally
        {
            _backupLock.Release();
        }

        RaiseBackupCompleted(result);
        return result;
    }

    public async Task<SyntheticFullBackupResult> CreateSyntheticFullBackupAsync(
        CancellationToken ct = default)
    {
        var result = new SyntheticFullBackupResult
        {
            StartedAt = DateTime.UtcNow,
            BasedOnFullSequence = _lastFullSequence,
            IncrementalCount = GetIncrementalCount()
        };

        if (result.IncrementalCount == 0)
        {
            // No incrementals to merge
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        var syntheticBackupId = $"synthetic-merged-{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _sequence)}";

        await _backupLock.WaitAsync(ct);
        try
        {
            // Get all incrementals since last full
            var incrementals = _backupChain
                .Where(kv => kv.Key > _lastFullSequence && kv.Value.Type == BackupProviderType.Incremental)
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value)
                .ToList();

            // Build final file catalog by applying incrementals in order
            var finalCatalog = new Dictionary<string, SyntheticFileEntry>();

            // Start with base full backup catalog
            var baseFullEntry = _backupChain.GetValueOrDefault(_lastFullSequence);
            if (baseFullEntry != null)
            {
                foreach (var entry in _catalog.Values.Where(e => e.BackupSequence == _lastFullSequence))
                {
                    finalCatalog[entry.FilePath] = entry;
                }
            }

            // Apply each incremental
            foreach (var incremental in incrementals)
            {
                foreach (var entry in _catalog.Values.Where(e => e.BackupSequence == incremental.Sequence))
                {
                    finalCatalog[entry.FilePath] = entry;
                }
            }

            // Write synthetic full backup
            foreach (var (filePath, entry) in finalCatalog)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // Copy from original backup location to synthetic full
                    await using var sourceStream = await _destination.ReadFileAsync(entry.BackupPath, ct);

                    var relativePath = GetRelativePath(filePath);
                    var metadata = new BackupFileMetadata
                    {
                        OriginalPath = filePath,
                        RelativePath = relativePath,
                        Size = entry.Size,
                        Checksum = entry.Checksum
                    };

                    await _destination.WriteFileAsync($"{syntheticBackupId}/{relativePath}", sourceStream, metadata, ct);

                    result.FilesProcessed++;
                    result.BytesProcessed += entry.Size;

                    RaiseProgressChanged(syntheticBackupId, result.FilesProcessed, finalCatalog.Count,
                        result.BytesProcessed, finalCatalog.Values.Sum(e => e.Size));
                }
                catch (Exception ex)
                {
                    // Log error but continue
                    Console.Error.WriteLine($"Error processing {filePath}: {ex.Message}");
                }
            }

            // Update tracking
            _lastFullSequence = _sequence;
            _backupChain[_sequence] = new BackupChainEntry
            {
                Sequence = _sequence,
                BackupId = syntheticBackupId,
                Type = BackupProviderType.SyntheticFull,
                CreatedAt = DateTime.UtcNow,
                FileCount = result.FilesProcessed,
                TotalBytes = result.BytesProcessed,
                MergedFrom = incrementals.Select(i => i.Sequence).ToList()
            };

            // Clean up old incrementals if configured
            if (_config.DeleteMergedIncrementals)
            {
                await DeleteOldIncrementalsAsync(incrementals.Select(i => i.Sequence), ct);
            }

            await SaveStateAsync(ct);
        }
        finally
        {
            _backupLock.Release();
        }

        result.CompletedAt = DateTime.UtcNow;
        return result;
    }

    public int GetIncrementalCount()
    {
        return _backupChain.Count(kv =>
            kv.Key > _lastFullSequence && kv.Value.Type == BackupProviderType.Incremental);
    }

    public async Task<RestoreResult> RestoreAsync(
        string backupId,
        string? targetPath = null,
        DateTime? pointInTime = null,
        CancellationToken ct = default)
    {
        try
        {
            // Find the backup chain entry
            var entry = _backupChain.Values.FirstOrDefault(e => e.BackupId == backupId);

            if (entry == null)
            {
                return new RestoreResult
                {
                    Success = false,
                    BackupId = backupId,
                    Error = "Backup not found",
                    RestoredAt = DateTime.UtcNow
                };
            }

            long bytesRestored = 0;
            long filesRestored = 0;

            // If it's a synthetic full, restore directly
            if (entry.Type == BackupProviderType.Full || entry.Type == BackupProviderType.SyntheticFull)
            {
                await foreach (var file in _destination.ListFilesAsync(backupId, ct))
                {
                    ct.ThrowIfCancellationRequested();

                    await using var stream = await _destination.ReadFileAsync(file.RelativePath, ct);
                    var restorePath = targetPath != null
                        ? Path.Combine(targetPath, file.RelativePath.Replace($"{backupId}/", ""))
                        : file.OriginalPath ?? file.RelativePath;

                    var dir = Path.GetDirectoryName(restorePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    await using var outStream = new FileStream(restorePath, FileMode.Create, FileAccess.Write);
                    await stream.CopyToAsync(outStream, ct);

                    bytesRestored += file.Size;
                    filesRestored++;
                }
            }
            else
            {
                // Need to reconstruct from full + incrementals
                var restored = await RestoreWithChainAsync(entry.Sequence, targetPath, ct);
                bytesRestored = restored.bytesRestored;
                filesRestored = restored.filesRestored;
            }

            return new RestoreResult
            {
                Success = true,
                BackupId = backupId,
                TargetPath = targetPath,
                PointInTime = pointInTime,
                BytesRestored = bytesRestored,
                FilesRestored = filesRestored,
                RestoredAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new RestoreResult
            {
                Success = false,
                BackupId = backupId,
                Error = ex.Message,
                RestoredAt = DateTime.UtcNow
            };
        }
    }

    public BackupStatistics GetStatistics()
    {
        var lastFull = _backupChain.Values
            .Where(e => e.Type == BackupProviderType.Full || e.Type == BackupProviderType.SyntheticFull)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefault();

        var lastIncr = _backupChain.Values
            .Where(e => e.Type == BackupProviderType.Incremental)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefault();

        return new BackupStatistics
        {
            TrackedFiles = _catalog.Count,
            LastBackupSequence = _sequence,
            LastFullBackupTime = lastFull?.CreatedAt,
            LastIncrementalBackupTime = lastIncr?.CreatedAt,
            TotalBytesBackedUp = _catalog.Values.Sum(e => e.Size)
        };
    }

    public Task<BackupMetadata?> GetBackupMetadataAsync(string backupId, CancellationToken ct = default)
    {
        var entry = _backupChain.Values.FirstOrDefault(e => e.BackupId == backupId);
        if (entry == null) return Task.FromResult<BackupMetadata?>(null);

        return Task.FromResult<BackupMetadata?>(new BackupMetadata
        {
            Id = entry.BackupId,
            Type = entry.Type,
            Sequence = entry.Sequence,
            CreatedAt = entry.CreatedAt,
            TotalFiles = entry.FileCount,
            TotalBytes = entry.TotalBytes
        });
    }

    public async IAsyncEnumerable<BackupMetadata> ListBackupsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in _backupChain.Values.OrderByDescending(e => e.Sequence))
        {
            ct.ThrowIfCancellationRequested();

            yield return new BackupMetadata
            {
                Id = entry.BackupId,
                Type = entry.Type,
                Sequence = entry.Sequence,
                CreatedAt = entry.CreatedAt,
                TotalFiles = entry.FileCount,
                TotalBytes = entry.TotalBytes
            };
        }
    }

    private async Task BackupFileAsync(
        string filePath,
        string backupId,
        BackupResult result,
        CancellationToken ct)
    {
        var fileInfo = new FileInfo(filePath);
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var checksum = await ComputeChecksumAsync(fileStream, ct);
        fileStream.Position = 0;

        var relativePath = GetRelativePath(filePath);
        var backupPath = $"{backupId}/{relativePath}";

        var metadata = new BackupFileMetadata
        {
            OriginalPath = filePath,
            RelativePath = relativePath,
            Size = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            Checksum = checksum
        };

        await _destination.WriteFileAsync(backupPath, fileStream, metadata, ct);

        // Update catalog
        _catalog[filePath] = new SyntheticFileEntry
        {
            FilePath = filePath,
            BackupPath = backupPath,
            Size = fileInfo.Length,
            ModifiedTime = fileInfo.LastWriteTimeUtc,
            Checksum = checksum,
            BackupSequence = _sequence
        };

        result.TotalFiles++;
        result.TotalBytes += fileInfo.Length;
    }

    private IEnumerable<(string path, FileChangeType type)> GetChangedFiles()
    {
        var changes = new List<(string path, FileChangeType type)>();

        foreach (var (path, entry) in _catalog)
        {
            if (!File.Exists(path))
            {
                changes.Add((path, FileChangeType.Deleted));
                continue;
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.LastWriteTimeUtc > entry.ModifiedTime || fileInfo.Length != entry.Size)
            {
                changes.Add((path, FileChangeType.Modified));
            }
        }

        return changes;
    }

    private async Task<(long bytesRestored, long filesRestored)> RestoreWithChainAsync(
        long targetSequence,
        string? targetPath,
        CancellationToken ct)
    {
        // Build file catalog at target sequence
        var fileCatalog = new Dictionary<string, SyntheticFileEntry>();

        // Start with base full
        foreach (var entry in _catalog.Values.Where(e => e.BackupSequence == _lastFullSequence))
        {
            fileCatalog[entry.FilePath] = entry;
        }

        // Apply incrementals up to target sequence
        var incrementals = _backupChain
            .Where(kv => kv.Key > _lastFullSequence && kv.Key <= targetSequence)
            .OrderBy(kv => kv.Key);

        foreach (var (seq, _) in incrementals)
        {
            foreach (var entry in _catalog.Values.Where(e => e.BackupSequence == seq))
            {
                fileCatalog[entry.FilePath] = entry;
            }
        }

        // Restore files
        long bytesRestored = 0;
        long filesRestored = 0;

        foreach (var (filePath, entry) in fileCatalog)
        {
            ct.ThrowIfCancellationRequested();

            await using var stream = await _destination.ReadFileAsync(entry.BackupPath, ct);
            var restorePath = targetPath != null
                ? Path.Combine(targetPath, GetRelativePath(filePath))
                : filePath;

            var dir = Path.GetDirectoryName(restorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await using var outStream = new FileStream(restorePath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(outStream, ct);

            bytesRestored += entry.Size;
            filesRestored++;
        }

        return (bytesRestored, filesRestored);
    }

    private async Task DeleteOldIncrementalsAsync(IEnumerable<long> sequences, CancellationToken ct)
    {
        foreach (var seq in sequences)
        {
            if (_backupChain.TryGetValue(seq, out var entry))
            {
                try
                {
                    await foreach (var file in _destination.ListFilesAsync(entry.BackupId, ct))
                    {
                        await _destination.DeleteFileAsync(file.RelativePath, ct);
                    }
                    _backupChain.TryRemove(seq, out _);
                }
                catch
                {
                    // Log error but continue
                }
            }
        }
    }

    private static async Task<string> ComputeChecksumAsync(Stream stream, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetRelativePath(string filePath)
    {
        return filePath.Replace('\\', '/').TrimStart('/');
    }

    private bool ShouldBackupFile(string path, BackupOptions? options)
    {
        if (options == null) return true;

        if (options.MaxFileSizeBytes > 0)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > options.MaxFileSizeBytes) return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Filter evaluation failed for {path}: {ex.Message}");
                return false;
            }
        }

        return true;
    }

    private void RaiseProgressChanged(string backupId, long files, long totalFiles, long bytes, long totalBytes)
    {
        ProgressChanged?.Invoke(this, new BackupProgressEventArgs
        {
            BackupId = backupId,
            ProcessedFiles = files,
            TotalFiles = totalFiles,
            ProcessedBytes = bytes,
            TotalBytes = totalBytes
        });
    }

    private void RaiseBackupCompleted(BackupResult result)
    {
        BackupCompleted?.Invoke(this, new BackupCompletedEventArgs { Result = result });
    }

    private async Task LoadStateAsync()
    {
        var stateFile = Path.Combine(_statePath, "synthetic_state.json");
        if (File.Exists(stateFile))
        {
            var json = await File.ReadAllTextAsync(stateFile);
            var state = JsonSerializer.Deserialize<SyntheticStateData>(json);
            if (state != null)
            {
                _sequence = state.Sequence;
                _lastFullSequence = state.LastFullSequence;

                foreach (var entry in state.Catalog)
                    _catalog[entry.FilePath] = entry;

                foreach (var entry in state.BackupChain)
                    _backupChain[entry.Sequence] = entry;
            }
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        var state = new SyntheticStateData
        {
            Sequence = _sequence,
            LastFullSequence = _lastFullSequence,
            Catalog = _catalog.Values.ToList(),
            BackupChain = _backupChain.Values.ToList()
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var stateFile = Path.Combine(_statePath, "synthetic_state.json");
        await File.WriteAllTextAsync(stateFile, json, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await SaveStateAsync(CancellationToken.None);
        _backupLock.Dispose();
    }
}

// Internal types

internal sealed class SyntheticFileEntry
{
    public required string FilePath { get; init; }
    public required string BackupPath { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedTime { get; init; }
    public required string Checksum { get; init; }
    public long BackupSequence { get; init; }
}

internal sealed class BackupChainEntry
{
    public long Sequence { get; init; }
    public required string BackupId { get; init; }
    public BackupProviderType Type { get; init; }
    public DateTime CreatedAt { get; init; }
    public long? BaseSequence { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public List<long>? MergedFrom { get; init; }
}

internal sealed class SyntheticStateData
{
    public long Sequence { get; init; }
    public long LastFullSequence { get; init; }
    public List<SyntheticFileEntry> Catalog { get; init; } = new();
    public List<BackupChainEntry> BackupChain { get; init; } = new();
}

/// <summary>
/// Configuration for synthetic full backup provider.
/// </summary>
public sealed record SyntheticFullBackupConfig
{
    /// <summary>Gets or sets the interval for automatic synthetic full creation.</summary>
    public TimeSpan? AutoSyntheticInterval { get; init; }

    /// <summary>Gets or sets the max number of incrementals before auto synthetic.</summary>
    public int MaxIncrementalsBeforeSynthetic { get; init; } = 10;

    /// <summary>Gets or sets whether to delete merged incrementals.</summary>
    public bool DeleteMergedIncrementals { get; init; }

    /// <summary>Gets or sets whether to verify after synthetic creation.</summary>
    public bool VerifyAfterSynthetic { get; init; } = true;
}
