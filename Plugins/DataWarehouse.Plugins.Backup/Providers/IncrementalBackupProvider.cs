using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.Backup.Providers;

/// <summary>
/// Incremental backup provider that uses content-defined chunking (Rabin fingerprinting)
/// for efficient deduplication. Supports incremental and differential backups with
/// optional block-level change tracking.
/// </summary>
public sealed class IncrementalBackupProvider : IBackupProvider, IDifferentialBackupProvider
{
    private readonly IBackupDestination _destination;
    private readonly IncrementalBackupConfig _config;
    private readonly RabinChunker _chunker;
    private readonly ConcurrentDictionary<string, string> _chunkStore = new(); // hash -> location
    private readonly ConcurrentDictionary<string, IncrementalBackupEntry> _entries = new();
    private readonly SemaphoreSlim _backupLock = new(1, 1);
    private readonly string _statePath;
    private readonly ILogger<IncrementalBackupProvider>? _logger;
    private long _sequence;
    private DateTime _lastFullBackupTime;
    private volatile bool _disposed;

    public string ProviderId => "incremental-backup-provider";
    public string Name => "Incremental Backup Provider";
    public BackupProviderType ProviderType => BackupProviderType.Incremental;
    public bool IsMonitoring => false;

    public event EventHandler<BackupProgressEventArgs>? ProgressChanged;
    public event EventHandler<BackupCompletedEventArgs>? BackupCompleted;

    public IncrementalBackupProvider(
        IBackupDestination destination,
        string statePath,
        IncrementalBackupConfig? config = null,
        ILogger<IncrementalBackupProvider>? logger = null)
    {
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _statePath = statePath ?? throw new ArgumentNullException(nameof(statePath));
        _config = config ?? new IncrementalBackupConfig();
        _logger = logger;
        _chunker = new RabinChunker(_config.MinChunkSize, _config.TargetChunkSize, _config.MaxChunkSize);

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
        var backupId = $"full-{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _sequence)}";
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
                    var fileResult = await BackupFileWithChunkingAsync(filePath, backupId, ct);
                    result.TotalBytes += fileResult.BytesProcessed;

                    _entries[filePath] = new IncrementalBackupEntry
                    {
                        Path = filePath,
                        Size = fileResult.BytesProcessed,
                        ModifiedTime = File.GetLastWriteTimeUtc(filePath),
                        ChunkHashes = fileResult.ChunkHashes,
                        BackupSequence = _sequence
                    };

                    processedFiles++;
                    RaiseProgressChanged(backupId, processedFiles, result.TotalFiles, result.TotalBytes, 0);
                }
                catch (Exception ex)
                {
                    result.Errors.Add(new BackupError { FilePath = filePath, Message = ex.Message });
                }
            }

            _lastFullBackupTime = DateTime.UtcNow;
            result.Success = result.Errors.Count == 0;
            result.CompletedAt = DateTime.UtcNow;

            await SaveBackupMetadataAsync(backupId, result, ct);
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
        var backupId = $"incremental-{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _sequence)}";
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
            var changedFiles = await GetChangedFilesAsync(ct);

            foreach (var (filePath, changeType) in changedFiles)
            {
                ct.ThrowIfCancellationRequested();

                if (!ShouldBackupFile(filePath, options)) continue;

                try
                {
                    if (changeType == IncrementalChangeType.Deleted)
                    {
                        _entries.TryRemove(filePath, out _);
                        result.TotalFiles++;
                        continue;
                    }

                    var fileResult = await BackupFileWithChunkingAsync(filePath, backupId, ct);
                    result.TotalBytes += fileResult.BytesProcessed;

                    _entries[filePath] = new IncrementalBackupEntry
                    {
                        Path = filePath,
                        Size = fileResult.BytesProcessed,
                        ModifiedTime = File.GetLastWriteTimeUtc(filePath),
                        ChunkHashes = fileResult.ChunkHashes,
                        BackupSequence = _sequence
                    };

                    result.TotalFiles++;
                    RaiseProgressChanged(backupId, result.TotalFiles, 0, result.TotalBytes, 0);
                }
                catch (Exception ex)
                {
                    result.Errors.Add(new BackupError { FilePath = filePath, Message = ex.Message });
                }
            }

            result.Success = result.Errors.Count == 0;
            result.CompletedAt = DateTime.UtcNow;

            await SaveBackupMetadataAsync(backupId, result, ct);
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
        var backupId = $"differential-{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _sequence)}";
        var result = new BackupResult
        {
            BackupId = backupId,
            BackupType = BackupProviderType.Differential,
            Sequence = _sequence,
            StartedAt = DateTime.UtcNow
        };

        await _backupLock.WaitAsync(ct);
        try
        {
            foreach (var (filePath, entry) in _entries)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(filePath))
                {
                    result.Errors.Add(new BackupError { FilePath = filePath, Message = "File no longer exists" });
                    continue;
                }

                if (!ShouldBackupFile(filePath, options)) continue;

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.LastWriteTimeUtc > _lastFullBackupTime)
                {
                    try
                    {
                        var fileResult = await BackupFileWithChunkingAsync(filePath, backupId, ct);
                        result.TotalBytes += fileResult.BytesProcessed;
                        result.TotalFiles++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add(new BackupError { FilePath = filePath, Message = ex.Message });
                    }
                }
            }

            result.Success = result.Errors.Count == 0;
            result.CompletedAt = DateTime.UtcNow;

            await SaveBackupMetadataAsync(backupId, result, ct);
            await SaveStateAsync(ct);
        }
        finally
        {
            _backupLock.Release();
        }

        RaiseBackupCompleted(result);
        return result;
    }

    public async Task<RestoreResult> RestoreAsync(
        string backupId,
        string? targetPath = null,
        DateTime? pointInTime = null,
        CancellationToken ct = default)
    {
        try
        {
            // Load backup metadata
            var metadataPath = $"{backupId}/metadata.json";
            await using var metadataStream = await _destination.ReadFileAsync(metadataPath, ct);
            using var reader = new StreamReader(metadataStream);
            var metadataJson = await reader.ReadToEndAsync(ct);
            var backupMetadata = JsonSerializer.Deserialize<BackupMetadataFile>(metadataJson);

            if (backupMetadata == null)
            {
                return new RestoreResult
                {
                    Success = false,
                    BackupId = backupId,
                    Error = "Failed to load backup metadata",
                    RestoredAt = DateTime.UtcNow
                };
            }

            long bytesRestored = 0;
            long filesRestored = 0;

            foreach (var entry in backupMetadata.Files)
            {
                ct.ThrowIfCancellationRequested();

                // Reconstruct file from chunks
                var fileData = await ReconstructFileFromChunksAsync(entry.ChunkHashes, ct);

                var restorePath = targetPath != null
                    ? Path.Combine(targetPath, entry.RelativePath)
                    : entry.OriginalPath;

                var dir = Path.GetDirectoryName(restorePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllBytesAsync(restorePath, fileData, ct);

                bytesRestored += fileData.Length;
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
            TrackedFiles = _entries.Count,
            LastBackupSequence = _sequence,
            LastFullBackupTime = _lastFullBackupTime,
            TotalBytesBackedUp = _entries.Values.Sum(e => e.Size),
            DeduplicationRatio = CalculateDeduplicationRatio()
        };
    }

    public Task<BackupMetadata?> GetBackupMetadataAsync(string backupId, CancellationToken ct = default)
    {
        // Would load from stored metadata
        return Task.FromResult<BackupMetadata?>(null);
    }

    public async IAsyncEnumerable<BackupMetadata> ListBackupsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var file in _destination.ListFilesAsync(null, ct))
        {
            if (file.RelativePath.EndsWith("metadata.json"))
            {
                var backupId = Path.GetDirectoryName(file.RelativePath) ?? "";
                yield return new BackupMetadata
                {
                    Id = backupId,
                    Type = backupId.StartsWith("full")
                        ? BackupProviderType.Full
                        : backupId.StartsWith("differential")
                            ? BackupProviderType.Differential
                            : BackupProviderType.Incremental,
                    CreatedAt = file.LastModified,
                    TotalBytes = file.Size
                };
            }
        }
    }

    private async Task<ChunkingResult> BackupFileWithChunkingAsync(
        string filePath,
        string backupId,
        CancellationToken ct)
    {
        var result = new ChunkingResult();

        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

        await foreach (var chunk in _chunker.ChunkAsync(fileStream, ct))
        {
            ct.ThrowIfCancellationRequested();

            var chunkHash = Convert.ToHexString(SHA256.HashData(chunk)).ToLowerInvariant();
            result.ChunkHashes.Add(chunkHash);
            result.BytesProcessed += chunk.Length;

            // Deduplication: only store if we don't have this chunk
            if (!_chunkStore.ContainsKey(chunkHash))
            {
                var chunkPath = $"chunks/{chunkHash[..2]}/{chunkHash}";
                await using var chunkStream = new MemoryStream(chunk);
                await _destination.WriteFileAsync(chunkPath, chunkStream, new BackupFileMetadata
                {
                    RelativePath = chunkPath,
                    Size = chunk.Length,
                    Checksum = chunkHash
                }, ct);

                _chunkStore[chunkHash] = chunkPath;
            }
            else
            {
                result.DedupedBytes += chunk.Length;
            }
        }

        return result;
    }

    private async Task<byte[]> ReconstructFileFromChunksAsync(List<string> chunkHashes, CancellationToken ct)
    {
        using var ms = new MemoryStream();

        foreach (var hash in chunkHashes)
        {
            ct.ThrowIfCancellationRequested();

            if (!_chunkStore.TryGetValue(hash, out var chunkPath))
            {
                chunkPath = $"chunks/{hash[..2]}/{hash}";
            }

            await using var chunkStream = await _destination.ReadFileAsync(chunkPath, ct);
            await chunkStream.CopyToAsync(ms, ct);
        }

        return ms.ToArray();
    }

    private async Task<List<(string path, IncrementalChangeType type)>> GetChangedFilesAsync(CancellationToken ct)
    {
        var changes = new List<(string path, IncrementalChangeType type)>();

        foreach (var (path, entry) in _entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                changes.Add((path, IncrementalChangeType.Deleted));
                continue;
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.LastWriteTimeUtc > entry.ModifiedTime || fileInfo.Length != entry.Size)
            {
                changes.Add((path, IncrementalChangeType.Modified));
            }
        }

        return changes;
    }

    private bool ShouldBackupFile(string path, BackupOptions? options)
    {
        if (options == null) return true;

        var fileName = Path.GetFileName(path);

        if (options.ExcludePatterns?.Length > 0)
        {
            foreach (var pattern in options.ExcludePatterns)
            {
                if (MatchesPattern(fileName, pattern)) return false;
            }
        }

        if (options.IncludePatterns?.Length > 0)
        {
            var matched = false;
            foreach (var pattern in options.IncludePatterns)
            {
                if (MatchesPattern(fileName, pattern))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }

        if (options.MaxFileSizeBytes > 0)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > options.MaxFileSizeBytes) return false;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Filter evaluation failed for path: {Path}", path);
                return false;
            }
        }

        return true;
    }

    private static bool MatchesPattern(string input, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private double CalculateDeduplicationRatio()
    {
        var totalOriginal = _entries.Values.Sum(e => e.Size);
        var totalStored = _chunkStore.Count * _config.TargetChunkSize; // Approximation
        return totalOriginal > 0 ? (double)totalStored / totalOriginal : 1.0;
    }

    private async Task SaveBackupMetadataAsync(string backupId, BackupResult result, CancellationToken ct)
    {
        var metadata = new BackupMetadataFile
        {
            BackupId = backupId,
            Type = result.BackupType,
            CreatedAt = result.StartedAt,
            CompletedAt = result.CompletedAt ?? DateTime.UtcNow,
            TotalFiles = result.TotalFiles,
            TotalBytes = result.TotalBytes,
            Files = _entries.Values.Select(e => new BackupFileEntry
            {
                OriginalPath = e.Path,
                RelativePath = e.Path.Replace('\\', '/'),
                Size = e.Size,
                ChunkHashes = e.ChunkHashes
            }).ToList()
        };

        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await _destination.WriteFileAsync($"{backupId}/metadata.json", stream, new BackupFileMetadata
        {
            RelativePath = $"{backupId}/metadata.json",
            Size = stream.Length
        }, ct);
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
        var stateFile = Path.Combine(_statePath, "incremental_state.json");
        if (File.Exists(stateFile))
        {
            var json = await File.ReadAllTextAsync(stateFile);
            var state = JsonSerializer.Deserialize<IncrementalStateData>(json);
            if (state != null)
            {
                _sequence = state.Sequence;
                _lastFullBackupTime = state.LastFullBackupTime;

                foreach (var entry in state.Entries)
                    _entries[entry.Path] = entry;

                foreach (var (hash, path) in state.ChunkStore)
                    _chunkStore[hash] = path;
            }
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        var state = new IncrementalStateData
        {
            Sequence = _sequence,
            LastFullBackupTime = _lastFullBackupTime,
            Entries = _entries.Values.ToList(),
            ChunkStore = _chunkStore.ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var stateFile = Path.Combine(_statePath, "incremental_state.json");
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

internal sealed class ChunkingResult
{
    public List<string> ChunkHashes { get; } = new();
    public long BytesProcessed { get; set; }
    public long DedupedBytes { get; set; }
}

internal sealed class IncrementalBackupEntry
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedTime { get; init; }
    public List<string> ChunkHashes { get; init; } = new();
    public long BackupSequence { get; init; }
}

internal enum IncrementalChangeType
{
    Added,
    Modified,
    Deleted
}

internal sealed class IncrementalStateData
{
    public long Sequence { get; init; }
    public DateTime LastFullBackupTime { get; init; }
    public List<IncrementalBackupEntry> Entries { get; init; } = new();
    public Dictionary<string, string> ChunkStore { get; init; } = new();
}

internal sealed class BackupMetadataFile
{
    public required string BackupId { get; init; }
    public BackupProviderType Type { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public long TotalFiles { get; init; }
    public long TotalBytes { get; init; }
    public List<BackupFileEntry> Files { get; init; } = new();
}

internal sealed class BackupFileEntry
{
    public required string OriginalPath { get; init; }
    public required string RelativePath { get; init; }
    public long Size { get; init; }
    public List<string> ChunkHashes { get; init; } = new();
}

/// <summary>
/// Configuration for incremental backup provider.
/// </summary>
public sealed record IncrementalBackupConfig
{
    /// <summary>Gets or sets the minimum chunk size in bytes.</summary>
    public int MinChunkSize { get; init; } = 4 * 1024;

    /// <summary>Gets or sets the target average chunk size in bytes.</summary>
    public int TargetChunkSize { get; init; } = 8 * 1024;

    /// <summary>Gets or sets the maximum chunk size in bytes.</summary>
    public int MaxChunkSize { get; init; } = 64 * 1024;

    /// <summary>Gets or sets whether to verify chunks after backup.</summary>
    public bool VerifyChunks { get; init; } = true;

    /// <summary>Gets or sets compression algorithm to use.</summary>
    public string CompressionAlgorithm { get; init; } = "zstd";
}

/// <summary>
/// Content-defined chunking using Rabin fingerprinting.
/// </summary>
public sealed class RabinChunker
{
    private readonly int _minSize;
    private readonly int _targetSize;
    private readonly int _maxSize;
    private readonly ulong _polynomial;
    private readonly ulong _windowMask;
    private readonly ulong[] _lookupTable;

    private const int WindowSize = 48;
    private const ulong DefaultPolynomial = 0xbfe6b8a5bf378d83UL;

    public RabinChunker(int minSize = 4096, int targetSize = 8192, int maxSize = 65536)
    {
        _minSize = minSize;
        _targetSize = targetSize;
        _maxSize = maxSize;
        _polynomial = DefaultPolynomial;

        // Calculate mask for target size (power of 2 - 1)
        var bits = (int)Math.Log2(_targetSize) + 1;
        _windowMask = (1UL << bits) - 1;

        _lookupTable = new ulong[256];
        InitializeLookupTable();
    }

    private void InitializeLookupTable()
    {
        for (int i = 0; i < 256; i++)
        {
            ulong hash = (ulong)i;
            for (int j = 0; j < 8; j++)
            {
                if ((hash & 1) != 0)
                    hash = (hash >> 1) ^ _polynomial;
                else
                    hash >>= 1;
            }
            _lookupTable[i] = hash;
        }
    }

    public async IAsyncEnumerable<byte[]> ChunkAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[_maxSize];
        var chunk = new List<byte>(_targetSize);
        ulong fingerprint = 0;
        var window = new byte[WindowSize];
        var windowIndex = 0;

        int bytesRead;
        var totalPosition = 0;

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, _maxSize - chunk.Count)), ct)) > 0)
        {
            for (int i = 0; i < bytesRead; i++)
            {
                ct.ThrowIfCancellationRequested();

                var b = buffer[i];
                chunk.Add(b);

                // Update rolling hash using Rabin fingerprint
                var outByte = window[windowIndex];
                window[windowIndex] = b;
                windowIndex = (windowIndex + 1) % WindowSize;

                fingerprint = ((fingerprint << 8) | b) ^ _lookupTable[outByte];

                totalPosition++;

                // Check for chunk boundary
                var chunkSize = chunk.Count;
                if (chunkSize >= _minSize)
                {
                    var shouldCut = chunkSize >= _maxSize ||
                                    (chunkSize >= _minSize && (fingerprint & _windowMask) == 0);

                    if (shouldCut)
                    {
                        yield return chunk.ToArray();
                        chunk.Clear();
                        fingerprint = 0;
                        Array.Clear(window);
                        windowIndex = 0;
                    }
                }
            }
        }

        // Yield remaining data
        if (chunk.Count > 0)
        {
            yield return chunk.ToArray();
        }
    }
}
