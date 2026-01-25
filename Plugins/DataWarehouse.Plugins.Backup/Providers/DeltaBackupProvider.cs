using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.Backup.Providers;

/// <summary>
/// Block-level delta backup provider that tracks changes at the block level
/// for efficient incremental backups of large files. Uses fixed-size blocks
/// with signature-based change detection.
/// </summary>
public sealed class DeltaBackupProvider : BackupProviderBase, IDeltaBackupProvider
{
    private readonly IBackupDestination _destination;
    private readonly DeltaBackupConfig _config;
    private readonly ConcurrentDictionary<string, DeltaFileState> _fileStates = new();
    private readonly string _statePath;
    private long _sequence;

    public override string ProviderId => "delta-backup-provider";
    public override string Name => "Delta Backup Provider";
    public override BackupProviderType ProviderType => BackupProviderType.Delta;

    public DeltaBackupProvider(
        IBackupDestination destination,
        string statePath,
        DeltaBackupConfig? config = null,
        ILogger<DeltaBackupProvider>? logger = null)
        : base(logger)
    {
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _statePath = statePath ?? throw new ArgumentNullException(nameof(statePath));
        _config = config ?? new DeltaBackupConfig();

        Directory.CreateDirectory(_statePath);
    }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        await _destination.InitializeAsync(ct);
        await LoadStateAsync();
    }

    public override async Task<BackupResult> PerformFullBackupAsync(
        IEnumerable<string> paths,
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        var backupId = $"delta-full-{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _sequence)}";
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

            foreach (var filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                if (!ShouldBackupFile(filePath, options)) continue;

                try
                {
                    await BackupFileFullAsync(filePath, backupId, result, ct);
                }
                catch (Exception ex)
                {
                    result.Errors.Add(new BackupError { FilePath = filePath, Message = ex.Message });
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

    public override async Task<BackupResult> PerformIncrementalBackupAsync(
        BackupOptions? options = null,
        CancellationToken ct = default)
    {
        var backupId = $"delta-incremental-{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _sequence)}";
        var result = new BackupResult
        {
            BackupId = backupId,
            BackupType = BackupProviderType.Delta,
            Sequence = _sequence,
            StartedAt = DateTime.UtcNow
        };

        await _backupLock.WaitAsync(ct);
        try
        {
            foreach (var (filePath, state) in _fileStates)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(filePath))
                {
                    // Mark as deleted
                    result.TotalFiles++;
                    continue;
                }

                if (!ShouldBackupFile(filePath, options)) continue;

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.LastWriteTimeUtc > state.LastBackupTime)
                {
                    try
                    {
                        var deltaResult = await PerformBlockLevelBackupAsync(filePath, ct);
                        result.TotalBytes += deltaResult.BytesChanged;
                        result.TotalFiles++;

                        // Write delta to destination
                        if (deltaResult.ChangedBlocks.Count > 0)
                        {
                            await WriteDeltaAsync(filePath, backupId, deltaResult, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add(new BackupError { FilePath = filePath, Message = ex.Message });
                    }
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
            // No previous state - this is a new file
            var fileInfo = new FileInfo(filePath);
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var blockIndex = 0;
            var buffer = new byte[_config.BlockSize];
            int bytesRead;

            while ((bytesRead = await fs.ReadAsync(buffer, ct)) > 0)
            {
                var blockData = bytesRead == _config.BlockSize
                    ? buffer.ToArray()
                    : buffer.AsSpan(0, bytesRead).ToArray();

                result.ChangedBlocks.Add(new ChangedBlock
                {
                    BlockIndex = blockIndex,
                    Offset = blockIndex * _config.BlockSize,
                    Size = bytesRead,
                    Hash = ComputeBlockHash(blockData),
                    Data = blockData
                });

                result.BytesChanged += bytesRead;
                blockIndex++;
            }

            // Create new state
            _fileStates[filePath] = await CreateFileStateAsync(filePath, ct);
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        // Compare with previous state
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var blockIdx = 0;
        var buf = new byte[_config.BlockSize];
        int read;

        while ((read = await fileStream.ReadAsync(buf, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            var blockData = read == _config.BlockSize
                ? buf.ToArray()
                : buf.AsSpan(0, read).ToArray();

            var currentHash = ComputeBlockHash(blockData);

            // Get previous hash if exists
            string? previousHash = null;
            if (previousState.BlockSignatures.Count > blockIdx)
            {
                previousHash = previousState.BlockSignatures[blockIdx].Hash;
            }

            if (currentHash != previousHash)
            {
                result.ChangedBlocks.Add(new ChangedBlock
                {
                    BlockIndex = blockIdx,
                    Offset = blockIdx * _config.BlockSize,
                    Size = read,
                    Hash = currentHash,
                    Data = blockData
                });
                result.BytesChanged += read;
            }
            else
            {
                result.UnchangedBlocks++;
            }

            blockIdx++;
        }

        // Update file state
        _fileStates[filePath] = await CreateFileStateAsync(filePath, ct);
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

    public override async Task<RestoreResult> RestoreAsync(
        string backupId,
        string? targetPath = null,
        DateTime? pointInTime = null,
        CancellationToken ct = default)
    {
        try
        {
            // List all files in the backup
            var files = await _destination.ListFilesAsync(backupId, ct).ToListAsync(ct);
            long bytesRestored = 0;
            long filesRestored = 0;

            var deltaFiles = files.Where(f => f.RelativePath.EndsWith(".delta.json")).ToList();
            var fullFiles = files.Where(f => !f.RelativePath.EndsWith(".delta.json")).ToList();

            // First restore full backups
            foreach (var file in fullFiles)
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

            // Then apply deltas
            foreach (var deltaFile in deltaFiles.OrderBy(f => f.RelativePath))
            {
                ct.ThrowIfCancellationRequested();

                await using var deltaStream = await _destination.ReadFileAsync(deltaFile.RelativePath, ct);
                using var reader = new StreamReader(deltaStream);
                var json = await reader.ReadToEndAsync(ct);
                var delta = JsonSerializer.Deserialize<DeltaFile>(json);

                if (delta != null)
                {
                    var restorePath = targetPath != null
                        ? Path.Combine(targetPath, delta.OriginalPath)
                        : delta.OriginalPath;

                    await ApplyDeltaAsync(restorePath, delta, ct);
                    bytesRestored += delta.Blocks.Sum(b => b.Size);
                }
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

    public override BackupStatistics GetStatistics()
    {
        return new BackupStatistics
        {
            TrackedFiles = _fileStates.Count,
            LastBackupSequence = _sequence,
            TotalBytesBackedUp = _fileStates.Values.Sum(s => s.Size)
        };
    }

    public override Task<BackupMetadata?> GetBackupMetadataAsync(string backupId, CancellationToken ct = default)
    {
        return Task.FromResult<BackupMetadata?>(null);
    }

    public override async IAsyncEnumerable<BackupMetadata> ListBackupsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        await foreach (var file in _destination.ListFilesAsync(null, ct))
        {
            var backupId = file.RelativePath.Split('/').FirstOrDefault();
            if (!string.IsNullOrEmpty(backupId) && backupId.StartsWith("delta-") && seen.Add(backupId))
            {
                yield return new BackupMetadata
                {
                    Id = backupId,
                    Type = backupId.Contains("full") ? BackupProviderType.Full : BackupProviderType.Delta,
                    CreatedAt = file.LastModified
                };
            }
        }
    }

    private async Task BackupFileFullAsync(
        string filePath,
        string backupId,
        BackupResult result,
        CancellationToken ct)
    {
        var fileInfo = new FileInfo(filePath);
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var relativePath = GetRelativePath(filePath);
        var metadata = new BackupFileMetadata
        {
            OriginalPath = filePath,
            RelativePath = relativePath,
            Size = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            Checksum = await ComputeChecksumAsync(fileStream, ct)
        };

        fileStream.Position = 0;
        await _destination.WriteFileAsync($"{backupId}/{relativePath}", fileStream, metadata, ct);

        // Store file state
        _fileStates[filePath] = await CreateFileStateAsync(filePath, ct);

        result.TotalFiles++;
        result.TotalBytes += fileInfo.Length;
    }

    private async Task WriteDeltaAsync(
        string filePath,
        string backupId,
        DeltaBackupResult deltaResult,
        CancellationToken ct)
    {
        var deltaFile = new DeltaFile
        {
            OriginalPath = filePath,
            CreatedAt = DateTime.UtcNow,
            Blocks = deltaResult.ChangedBlocks.Select(b => new DeltaBlock
            {
                BlockIndex = b.BlockIndex,
                Offset = b.Offset,
                Size = b.Size,
                Hash = b.Hash,
                Data = Convert.ToBase64String(b.Data)
            }).ToList()
        };

        var json = JsonSerializer.Serialize(deltaFile, new JsonSerializerOptions { WriteIndented = true });
        var relativePath = GetRelativePath(filePath);
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        await _destination.WriteFileAsync(
            $"{backupId}/{relativePath}.delta.json",
            stream,
            new BackupFileMetadata { RelativePath = relativePath, Size = stream.Length },
            ct);
    }

    private async Task ApplyDeltaAsync(string filePath, DeltaFile delta, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            // Create new file from delta
            await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            foreach (var block in delta.Blocks.OrderBy(b => b.Offset))
            {
                var data = Convert.FromBase64String(block.Data);
                fs.Position = block.Offset;
                await fs.WriteAsync(data, ct);
            }
            return;
        }

        // Apply delta to existing file
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
        foreach (var block in delta.Blocks)
        {
            var data = Convert.FromBase64String(block.Data);
            fileStream.Position = block.Offset;
            await fileStream.WriteAsync(data, ct);
        }
    }

    private async Task<DeltaFileState> CreateFileStateAsync(string filePath, CancellationToken ct)
    {
        var fileInfo = new FileInfo(filePath);
        var signatures = new List<BlockSignature>();

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[_config.BlockSize];
        var blockIndex = 0;
        int bytesRead;

        while ((bytesRead = await fs.ReadAsync(buffer, ct)) > 0)
        {
            var blockData = bytesRead == _config.BlockSize
                ? buffer
                : buffer.AsSpan(0, bytesRead).ToArray();

            signatures.Add(new BlockSignature
            {
                BlockIndex = blockIndex,
                Hash = ComputeBlockHash(blockData),
                WeakHash = ComputeWeakHash(blockData)
            });

            blockIndex++;
        }

        return new DeltaFileState
        {
            FilePath = filePath,
            Size = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            LastBackupTime = DateTime.UtcNow,
            BlockSignatures = signatures
        };
    }

    private static uint ComputeWeakHash(byte[] data)
    {
        // Rolling hash (Adler-32 like)
        uint a = 1, b = 0;
        const uint mod = 65521;

        foreach (var d in data)
        {
            a = (a + d) % mod;
            b = (b + a) % mod;
        }

        return (b << 16) | a;
    }


    private async Task LoadStateAsync()
    {
        var stateFile = Path.Combine(_statePath, "delta_state.json");
        if (File.Exists(stateFile))
        {
            var json = await File.ReadAllTextAsync(stateFile);
            var state = JsonSerializer.Deserialize<DeltaStateData>(json);
            if (state != null)
            {
                _sequence = state.Sequence;
                foreach (var fs in state.FileStates)
                    _fileStates[fs.FilePath] = fs;
            }
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        var state = new DeltaStateData
        {
            Sequence = _sequence,
            FileStates = _fileStates.Values.ToList()
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var stateFile = Path.Combine(_statePath, "delta_state.json");
        await File.WriteAllTextAsync(stateFile, json, ct);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await SaveStateAsync(CancellationToken.None);
    }
}

// Internal types

internal sealed class DeltaFileState
{
    public required string FilePath { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public DateTime LastBackupTime { get; init; }
    public List<BlockSignature> BlockSignatures { get; init; } = new();
}

internal sealed class BlockSignature
{
    public int BlockIndex { get; init; }
    public required string Hash { get; init; }
    public uint WeakHash { get; init; }
}

internal sealed class DeltaStateData
{
    public long Sequence { get; init; }
    public List<DeltaFileState> FileStates { get; init; } = new();
}

internal sealed class DeltaFile
{
    public required string OriginalPath { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<DeltaBlock> Blocks { get; init; } = new();
}

internal sealed class DeltaBlock
{
    public int BlockIndex { get; init; }
    public long Offset { get; init; }
    public int Size { get; init; }
    public required string Hash { get; init; }
    public required string Data { get; init; } // Base64 encoded
}

/// <summary>
/// Configuration for delta backup provider.
/// </summary>
public sealed record DeltaBackupConfig
{
    /// <summary>Gets or sets the block size in bytes.</summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>Gets or sets whether to use weak hash for faster comparison.</summary>
    public bool UseWeakHash { get; init; } = true;

    /// <summary>Gets or sets whether to compress delta blocks.</summary>
    public bool CompressBlocks { get; init; } = true;
}
