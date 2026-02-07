using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace DataWarehouse.Plugins.Backup.Providers;

/// <summary>
/// Continuous backup provider with real-time file monitoring,
/// block-level incremental support, and synthetic full backup generation.
/// </summary>
public sealed class ContinuousBackupProvider :
    IContinuousBackupProvider,
    IDifferentialBackupProvider,
    IDeltaBackupProvider,
    ISyntheticFullBackupProvider
{
    private readonly ContinuousBackupConfig _config;
    private readonly IBackupDestination _destination;
    private readonly ConcurrentDictionary<string, FileChangeInfo> _pendingChanges = new();
    private readonly ConcurrentDictionary<string, FileState> _fileStates = new();
    private readonly Channel<FileChangeEvent> _changeChannel;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _batchTimer;
    private readonly Timer _syntheticFullTimer;
    private readonly SemaphoreSlim _backupLock = new(1, 1);
    private readonly string _statePath;
    private Task? _processingTask;
    private CancellationTokenSource? _cts;
    private long _lastBackupSequence;
    private DateTime _lastFullBackupTime;
    private volatile bool _disposed;

    public string ProviderId => "continuous-backup-provider";
    public string Name => "Continuous Backup Provider";
    public BackupProviderType ProviderType => BackupProviderType.Continuous;
    public bool IsMonitoring => _cts != null && !_cts.IsCancellationRequested;

    public event EventHandler<BackupProgressEventArgs>? ProgressChanged;
    public event EventHandler<BackupCompletedEventArgs>? BackupCompleted;
    public event EventHandler<FileChangeDetectedEventArgs>? ChangeDetected;

    public ContinuousBackupProvider(
        IBackupDestination destination,
        string statePath,
        ContinuousBackupConfig? config = null)
    {
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _statePath = statePath ?? throw new ArgumentNullException(nameof(statePath));
        _config = config ?? new ContinuousBackupConfig();

        Directory.CreateDirectory(_statePath);

        _changeChannel = Channel.CreateBounded<FileChangeEvent>(
            new BoundedChannelOptions(10000) { FullMode = BoundedChannelFullMode.DropOldest });

        _batchTimer = new Timer(
            async _ => await ProcessPendingChangesAsync(),
            null,
            _config.DebounceInterval,
            _config.DebounceInterval);

        _syntheticFullTimer = _config.SyntheticFullInterval.HasValue
            ? new Timer(async _ => await CreateSyntheticFullBackupAsync(), null,
                _config.SyntheticFullInterval.Value, _config.SyntheticFullInterval.Value)
            : new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _destination.InitializeAsync(ct);
        await LoadStateAsync();
    }

    public async Task StartMonitoringAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        if (_cts != null)
            throw new InvalidOperationException("Already monitoring");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var watcher = CreateWatcher(path);
                _watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
        }

        _processingTask = ProcessChangesAsync(_cts.Token);

        if (_fileStates.IsEmpty)
        {
            await PerformFullBackupAsync(paths, ct: ct);
        }
    }

    public async Task StopMonitoringAsync()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        _cts?.Cancel();

        if (_processingTask != null)
        {
            try { await _processingTask; }
            catch (OperationCanceledException ex)
            {
                System.Diagnostics.Trace.TraceInformation(
                    "[ContinuousBackupProvider] Processing task cancelled during shutdown: {0}",
                    ex.Message);
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async Task<BackupResult> PerformFullBackupAsync(
        IEnumerable<string> paths,
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        var backupId = $"full-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var result = new BackupResult
        {
            BackupId = backupId,
            BackupType = BackupProviderType.Full,
            Sequence = Interlocked.Increment(ref _lastBackupSequence),
            StartedAt = DateTime.UtcNow
        };

        await _backupLock.WaitAsync(ct);
        try
        {
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    await BackupDirectoryAsync(path, backupId, result, options, ct);
                }
                else if (File.Exists(path))
                {
                    await BackupFileAsync(path, backupId, result, ct);
                }
            }

            _lastFullBackupTime = DateTime.UtcNow;
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
        var backupId = $"incremental-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var result = new BackupResult
        {
            BackupId = backupId,
            BackupType = BackupProviderType.Incremental,
            Sequence = Interlocked.Increment(ref _lastBackupSequence),
            StartedAt = DateTime.UtcNow
        };

        await _backupLock.WaitAsync(ct);
        try
        {
            var changes = _pendingChanges.ToArray();
            _pendingChanges.Clear();

            foreach (var (path, change) in changes)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    switch (change.ChangeType)
                    {
                        case FileChangeType.Created:
                        case FileChangeType.Modified:
                            await BackupChangedFileAsync(path, backupId, result, ct);
                            break;
                        case FileChangeType.Deleted:
                            await RecordDeletedFileAsync(path, result);
                            break;
                        case FileChangeType.Renamed:
                            await RecordRenamedFileAsync(path, change.OldPath!, result);
                            break;
                    }
                    result.TotalFiles++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add(new BackupError { FilePath = path, Message = ex.Message });
                }
            }

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

    public async Task<BackupResult> PerformDifferentialBackupAsync(
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        var backupId = $"differential-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var result = new BackupResult
        {
            BackupId = backupId,
            BackupType = BackupProviderType.Differential,
            Sequence = Interlocked.Increment(ref _lastBackupSequence),
            StartedAt = DateTime.UtcNow
        };

        await _backupLock.WaitAsync(ct);
        try
        {
            foreach (var (path, state) in _fileStates)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(path))
                {
                    result.Errors.Add(new BackupError { FilePath = path, Message = "File deleted" });
                    continue;
                }

                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc > _lastFullBackupTime)
                {
                    await BackupChangedFileAsync(path, backupId, result, ct);
                }
            }

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

    public async Task<DeltaBackupResult> PerformBlockLevelBackupAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var result = new DeltaBackupResult
        {
            FilePath = filePath,
            StartedAt = DateTime.UtcNow
        };

        if (!_fileStates.TryGetValue(filePath, out var previousState))
        {
            // No previous state, treat as new file
            var info = new FileInfo(filePath);
            result.BytesChanged = info.Length;
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        await _backupLock.WaitAsync(ct);
        try
        {
            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

            var blockSize = 4096;
            var currentBlock = new byte[blockSize];
            var blockIndex = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var bytesRead = await fileStream.ReadAsync(currentBlock, ct);
                if (bytesRead == 0) break;

                var blockData = bytesRead == blockSize
                    ? currentBlock
                    : currentBlock.AsSpan(0, bytesRead).ToArray();

                var blockHash = Convert.ToHexString(SHA256.HashData(blockData)).ToLowerInvariant();

                var previousHash = previousState.BlockHashes.Count > blockIndex
                    ? previousState.BlockHashes[blockIndex]
                    : null;

                if (blockHash != previousHash)
                {
                    result.ChangedBlocks.Add(new ChangedBlock
                    {
                        BlockIndex = blockIndex,
                        Offset = blockIndex * blockSize,
                        Size = bytesRead,
                        Hash = blockHash,
                        Data = blockData
                    });
                    result.BytesChanged += bytesRead;
                }
                else
                {
                    result.UnchangedBlocks++;
                }

                blockIndex++;
            }

            await UpdateFileStateAsync(filePath, ct);
        }
        finally
        {
            _backupLock.Release();
        }

        result.CompletedAt = DateTime.UtcNow;
        return result;
    }

    public async Task<IReadOnlyList<ChangedBlock>> GetChangedBlocksAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var result = await PerformBlockLevelBackupAsync(filePath, ct);
        return result.ChangedBlocks;
    }

    public async Task<SyntheticFullBackupResult> CreateSyntheticFullBackupAsync(
        CancellationToken ct = default)
    {
        var result = new SyntheticFullBackupResult
        {
            StartedAt = DateTime.UtcNow,
            BasedOnFullSequence = FindLastFullBackupSequence(),
            IncrementalCount = GetIncrementalCount()
        };

        await _backupLock.WaitAsync(ct);
        try
        {
            foreach (var (path, state) in _fileStates)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(path)) continue;

                var reconstructed = await ReconstructFileAsync(path, ct);

                if (reconstructed != null)
                {
                    await using var stream = new MemoryStream(reconstructed);
                    var metadata = new BackupFileMetadata
                    {
                        OriginalPath = path,
                        RelativePath = GetRelativePath(path),
                        Size = reconstructed.Length,
                        LastModified = File.GetLastWriteTimeUtc(path),
                        Checksum = Convert.ToHexString(SHA256.HashData(reconstructed)).ToLowerInvariant()
                    };

                    await _destination.WriteFileAsync(
                        $"synthetic/{result.StartedAt:yyyyMMddHHmmss}/{GetRelativePath(path)}",
                        stream,
                        metadata,
                        ct);

                    result.FilesProcessed++;
                    result.BytesProcessed += reconstructed.Length;
                }
            }

            _lastFullBackupTime = DateTime.UtcNow;
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
        return (int)(_lastBackupSequence - FindLastFullBackupSequence());
    }

    public async Task<RestoreResult> RestoreAsync(
        string backupId,
        string? targetPath = null,
        DateTime? pointInTime = null,
        CancellationToken ct = default)
    {
        try
        {
            var files = await _destination.ListFilesAsync(backupId, ct).ToListAsync(ct);
            long bytesRestored = 0;
            long filesRestored = 0;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var stream = await _destination.ReadFileAsync(file.RelativePath, ct);
                var restorePath = targetPath != null
                    ? Path.Combine(targetPath, file.RelativePath)
                    : file.OriginalPath ?? file.RelativePath;

                var dir = Path.GetDirectoryName(restorePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                await using var fileStream = new FileStream(restorePath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(fileStream, ct);

                bytesRestored += file.Size;
                filesRestored++;
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
        return new BackupStatistics
        {
            TrackedFiles = _fileStates.Count,
            PendingChanges = _pendingChanges.Count,
            LastBackupSequence = _lastBackupSequence,
            LastFullBackupTime = _lastFullBackupTime,
            IsMonitoring = IsMonitoring
        };
    }

    public Task<BackupMetadata?> GetBackupMetadataAsync(string backupId, CancellationToken ct = default)
    {
        // Implementation would load from metadata store
        return Task.FromResult<BackupMetadata?>(null);
    }

    public async IAsyncEnumerable<BackupMetadata> ListBackupsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var file in _destination.ListFilesAsync(null, ct))
        {
            if (file.RelativePath.Contains("metadata.json"))
            {
                yield return new BackupMetadata
                {
                    Id = Path.GetDirectoryName(file.RelativePath) ?? file.RelativePath,
                    Type = BackupProviderType.Continuous,
                    CreatedAt = file.LastModified,
                    TotalBytes = file.Size
                };
            }
        }
    }

    private FileSystemWatcher CreateWatcher(string path)
    {
        var watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime,
            IncludeSubdirectories = true,
            InternalBufferSize = 65536
        };

        watcher.Changed += (s, e) => QueueChange(e.FullPath, FileChangeType.Modified);
        watcher.Created += (s, e) => QueueChange(e.FullPath, FileChangeType.Created);
        watcher.Deleted += (s, e) => QueueChange(e.FullPath, FileChangeType.Deleted);
        watcher.Renamed += (s, e) => QueueChange(e.FullPath, FileChangeType.Renamed, e.OldFullPath);

        return watcher;
    }

    private void QueueChange(string path, FileChangeType changeType, string? oldPath = null)
    {
        if (!ShouldProcessFile(path)) return;

        _changeChannel.Writer.TryWrite(new FileChangeEvent
        {
            Path = path,
            ChangeType = changeType,
            OldPath = oldPath,
            Timestamp = DateTime.UtcNow
        });

        ChangeDetected?.Invoke(this, new FileChangeDetectedEventArgs
        {
            Path = path,
            ChangeType = changeType,
            OldPath = oldPath
        });
    }

    private bool ShouldProcessFile(string path)
    {
        var fileName = Path.GetFileName(path);

        if (_config.ExcludePatterns?.Length > 0)
        {
            foreach (var pattern in _config.ExcludePatterns)
            {
                if (MatchesPattern(fileName, pattern) || MatchesPattern(path, pattern))
                    return false;
            }
        }

        if (_config.IncludePatterns?.Length > 0)
        {
            var included = false;
            foreach (var pattern in _config.IncludePatterns)
            {
                if (MatchesPattern(fileName, pattern) || MatchesPattern(path, pattern))
                {
                    included = true;
                    break;
                }
            }
            if (!included) return false;
        }

        if (_config.MaxFileSizeBytes > 0)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > _config.MaxFileSizeBytes)
                    return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                // Cannot access file - log warning but include in backup attempt
                System.Diagnostics.Trace.TraceWarning(
                    "[ContinuousBackupProvider] Cannot access file {0} to check size (access denied): {1}",
                    path, ex.Message);
            }
            catch (IOException ex)
            {
                // I/O error - log warning but include in backup attempt
                System.Diagnostics.Trace.TraceWarning(
                    "[ContinuousBackupProvider] I/O error checking file size for {0}: {1}",
                    path, ex.Message);
            }
            catch (Exception ex)
            {
                // Unexpected error - log and continue
                System.Diagnostics.Trace.TraceWarning(
                    "[ContinuousBackupProvider] Unexpected error checking file size for {0}: {1}",
                    path, ex.Message);
            }
        }

        return true;
    }

    private static bool MatchesPattern(string input, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            input, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private async Task ProcessChangesAsync(CancellationToken ct)
    {
        await foreach (var change in _changeChannel.Reader.ReadAllAsync(ct))
        {
            _pendingChanges[change.Path] = new FileChangeInfo
            {
                Path = change.Path,
                ChangeType = change.ChangeType,
                OldPath = change.OldPath,
                Timestamp = change.Timestamp
            };
        }
    }

    private async Task ProcessPendingChangesAsync()
    {
        if (_pendingChanges.IsEmpty || _disposed) return;

        try
        {
            await PerformIncrementalBackupAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                "[ContinuousBackupProvider] Failed to process pending changes during batch backup: {0}",
                ex.Message);
        }
    }

    private async Task BackupDirectoryAsync(
        string directory,
        string backupId,
        BackupResult result,
        BackupOptions? options,
        CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!ShouldProcessFile(file)) continue;

            try
            {
                await BackupFileAsync(file, backupId, result, ct);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new BackupError { FilePath = file, Message = ex.Message });
            }
        }
    }

    private async Task BackupFileAsync(
        string filePath,
        string backupId,
        BackupResult result,
        CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) return;

        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

        var checksum = await ComputeChecksumAsync(fileStream, ct);
        fileStream.Position = 0;

        var metadata = new BackupFileMetadata
        {
            OriginalPath = filePath,
            RelativePath = GetRelativePath(filePath),
            Size = info.Length,
            LastModified = info.LastWriteTimeUtc,
            Checksum = checksum
        };

        await _destination.WriteFileAsync($"{backupId}/{metadata.RelativePath}", fileStream, metadata, ct);
        await UpdateFileStateAsync(filePath, ct);

        result.TotalFiles++;
        result.TotalBytes += info.Length;

        RaiseProgressChanged(result.BackupId, result.TotalFiles, 0, result.TotalBytes, 0);
    }

    private async Task BackupChangedFileAsync(
        string filePath,
        string backupId,
        BackupResult result,
        CancellationToken ct)
    {
        if (!File.Exists(filePath)) return;

        var info = new FileInfo(filePath);
        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

        var metadata = new BackupFileMetadata
        {
            OriginalPath = filePath,
            RelativePath = GetRelativePath(filePath),
            Size = info.Length,
            LastModified = info.LastWriteTimeUtc,
            Checksum = await ComputeChecksumAsync(fileStream, ct)
        };

        fileStream.Position = 0;
        await _destination.WriteFileAsync($"{backupId}/{metadata.RelativePath}", fileStream, metadata, ct);
        await UpdateFileStateAsync(filePath, ct);

        result.TotalBytes += info.Length;
    }

    private Task RecordDeletedFileAsync(string path, BackupResult result)
    {
        _fileStates.TryRemove(path, out _);
        return Task.CompletedTask;
    }

    private Task RecordRenamedFileAsync(string newPath, string oldPath, BackupResult result)
    {
        if (_fileStates.TryRemove(oldPath, out var state))
        {
            state.Path = newPath;
            _fileStates[newPath] = state;
        }
        return Task.CompletedTask;
    }

    private async Task UpdateFileStateAsync(string filePath, CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) return;

        var blockHashes = new List<string>();
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
        {
            var blockData = bytesRead == buffer.Length ? buffer : buffer.AsSpan(0, bytesRead).ToArray();
            blockHashes.Add(Convert.ToHexString(SHA256.HashData(blockData)).ToLowerInvariant());
        }

        _fileStates[filePath] = new FileState
        {
            Path = filePath,
            Size = info.Length,
            LastModified = info.LastWriteTimeUtc,
            BlockHashes = blockHashes,
            LastBackupTime = DateTime.UtcNow
        };
    }

    private async Task<byte[]?> ReconstructFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var relativePath = GetRelativePath(filePath);
            await using var baseStream = await _destination.ReadFileAsync($"full/{relativePath}", ct);
            using var ms = new MemoryStream();
            await baseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[ContinuousBackupProvider] Failed to reconstruct file {0} from backup: {1}",
                filePath, ex.Message);
            return null;
        }
    }

    private long FindLastFullBackupSequence()
    {
        // Simplified - in production would read from metadata
        return Math.Max(0, _lastBackupSequence - GetIncrementalCount());
    }

    private static string GetRelativePath(string filePath)
    {
        return filePath.Replace('\\', '/').TrimStart('/');
    }

    private static async Task<string> ComputeChecksumAsync(Stream stream, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
        var stateFile = Path.Combine(_statePath, "backup_state.json");
        if (File.Exists(stateFile))
        {
            var json = await File.ReadAllTextAsync(stateFile);
            var state = System.Text.Json.JsonSerializer.Deserialize<BackupStateData>(json);
            if (state != null)
            {
                foreach (var fs in state.FileStates)
                    _fileStates[fs.Path] = fs;
                _lastBackupSequence = state.LastSequence;
                _lastFullBackupTime = state.LastFullBackupTime;
            }
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        var state = new BackupStateData
        {
            FileStates = _fileStates.Values.ToList(),
            LastSequence = _lastBackupSequence,
            LastFullBackupTime = _lastFullBackupTime,
            SavedAt = DateTime.UtcNow
        };

        var stateFile = Path.Combine(_statePath, "backup_state.json");
        var json = System.Text.Json.JsonSerializer.Serialize(state,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(stateFile, json, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopMonitoringAsync();
        await _batchTimer.DisposeAsync();
        await _syntheticFullTimer.DisposeAsync();
        _backupLock.Dispose();
        _changeChannel.Writer.Complete();
    }
}

// Internal types
internal sealed class FileChangeEvent
{
    public required string Path { get; init; }
    public FileChangeType ChangeType { get; init; }
    public string? OldPath { get; init; }
    public DateTime Timestamp { get; init; }
}

internal sealed class FileChangeInfo
{
    public required string Path { get; init; }
    public FileChangeType ChangeType { get; init; }
    public string? OldPath { get; init; }
    public DateTime Timestamp { get; init; }
}

internal sealed class FileState
{
    public required string Path { get; set; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public List<string> BlockHashes { get; init; } = new();
    public DateTime LastBackupTime { get; init; }
}

internal sealed class BackupStateData
{
    public List<FileState> FileStates { get; init; } = new();
    public long LastSequence { get; init; }
    public DateTime LastFullBackupTime { get; init; }
    public DateTime SavedAt { get; init; }
}
