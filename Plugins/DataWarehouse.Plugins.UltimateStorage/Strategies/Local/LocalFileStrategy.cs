using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Local
{
    /// <summary>
    /// Local filesystem storage strategy with production-ready features:
    /// - Atomic writes using temp files and rename
    /// - Media type detection (HDD, SSD, NVMe, USB, etc.)
    /// - Optimal buffer sizing based on media type
    /// - Path traversal protection
    /// - File locking for concurrent access
    /// - Sequential vs random access optimization
    /// </summary>
    public class LocalFileStrategy : UltimateStorageStrategyBase
    {
        private readonly SemaphoreSlim _writeLock = new(10, 10); // Allow 10 concurrent writes
        private string _basePath = string.Empty;
        private MediaType _detectedMediaType = MediaType.Unknown;
        private bool _useAtomicWrites = true;
        private bool _useWriteThrough = false;
        private int? _bufferSizeOverride = null;
        private AccessPattern? _forceAccessPattern = null;

        public override string StrategyId => "local-file";
        public override string Name => "Local Filesystem Storage";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = false,
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = null, // No practical limit
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong
        };

        /// <summary>
        /// Initializes the local file storage strategy.
        /// </summary>
        protected override Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load configuration
            _basePath = GetConfiguration<string>("BasePath", Directory.GetCurrentDirectory());
            _useAtomicWrites = GetConfiguration<bool>("UseAtomicWrites", true);
            _useWriteThrough = GetConfiguration<bool>("UseWriteThrough", false);
            _bufferSizeOverride = GetConfiguration<int?>("BufferSize", null);
            _forceAccessPattern = GetConfiguration<AccessPattern?>("ForceAccessPattern", null);

            // Ensure base path exists
            if (!Directory.Exists(_basePath))
            {
                Directory.CreateDirectory(_basePath);
            }

            // Detect media type for optimization
            _detectedMediaType = DetectMediaType(_basePath);

            return Task.CompletedTask;
        }

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);

            var filePath = GetFilePath(key);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await _writeLock.WaitAsync(ct);
            try
            {
                var bufferSize = GetOptimalBufferSize();
                var startPosition = data.CanSeek ? data.Position : 0;
                var startTime = DateTime.UtcNow;

                if (_useAtomicWrites)
                {
                    // Atomic write: temp file + rename
                    var tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
                    try
                    {
                        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                            FileShare.None, bufferSize, GetFileOptions()))
                        {
                            await data.CopyToAsync(fs, bufferSize, ct);
                            await fs.FlushAsync(ct);
                        }

                        // Atomic rename
                        File.Move(tempPath, filePath, overwrite: true);
                    }
                    catch
                    {
                        // Clean up temp file on failure
                        try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
                        throw;
                    }
                }
                else
                {
                    // Direct write (non-atomic)
                    await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize, GetFileOptions());
                    await data.CopyToAsync(fs, bufferSize, ct);
                    await fs.FlushAsync(ct);
                }

                var fileInfo = new FileInfo(filePath);
                var bytesWritten = fileInfo.Length;

                // Update statistics
                IncrementBytesStored(bytesWritten);
                IncrementOperationCounter(StorageOperationType.Store);

                // Store custom metadata in companion .meta file if provided
                if (metadata != null && metadata.Count > 0)
                {
                    await StoreMetadataFileAsync(filePath, metadata, ct);
                }

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = bytesWritten,
                    Created = fileInfo.CreationTimeUtc,
                    Modified = fileInfo.LastWriteTimeUtc,
                    ETag = GenerateETag(fileInfo),
                    ContentType = GetContentType(key),
                    CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                    Tier = Tier
                };
            }
            finally
            {
                _writeLock.Release();
            }
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Object not found: {key}", filePath);
            }

            var bufferSize = GetOptimalBufferSize();
            var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize, GetFileOptions());

            // Update statistics
            var fileInfo = new FileInfo(filePath);
            IncrementBytesRetrieved(fileInfo.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            // For sequential media (tape, optical), read entire file into memory
            if (GetOptimalAccessPattern() == AccessPattern.Sequential)
            {
                var ms = new MemoryStream();
                try
                {
                    await fs.CopyToAsync(ms, bufferSize, ct);
                    await fs.DisposeAsync();
                    ms.Position = 0;
                    return ms;
                }
                catch
                {
                    await ms.DisposeAsync();
                    await fs.DisposeAsync();
                    throw;
                }
            }

            return fs;
        }

        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var filePath = GetFilePath(key);
            if (File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                var size = fileInfo.Length;

                File.Delete(filePath);

                // Delete companion metadata file if it exists
                var metaPath = filePath + ".meta";
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                }

                // Update statistics
                IncrementBytesDeleted(size);
                IncrementOperationCounter(StorageOperationType.Delete);
            }

            return Task.CompletedTask;
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var filePath = GetFilePath(key);
            var exists = File.Exists(filePath);

            IncrementOperationCounter(StorageOperationType.Exists);

            return Task.FromResult(exists);
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);

            var searchPath = string.IsNullOrEmpty(prefix) ? _basePath : Path.Combine(_basePath, prefix);

            if (!Directory.Exists(searchPath))
            {
                // Treat prefix as a pattern
                searchPath = _basePath;
            }

            var files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                // Skip metadata files
                if (filePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip temp files
                if (filePath.Contains(".tmp.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(_basePath, filePath);

                if (!string.IsNullOrEmpty(prefix) && !relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileInfo = new FileInfo(filePath);
                var key = relativePath.Replace(Path.DirectorySeparatorChar, '/');

                // Load custom metadata if available
                var customMetadata = await LoadMetadataFileAsync(filePath, ct);

                yield return new StorageObjectMetadata
                {
                    Key = key,
                    Size = fileInfo.Length,
                    Created = fileInfo.CreationTimeUtc,
                    Modified = fileInfo.LastWriteTimeUtc,
                    ETag = GenerateETag(fileInfo),
                    ContentType = GetContentType(key),
                    CustomMetadata = customMetadata,
                    Tier = Tier
                };

                await Task.Yield();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Object not found: {key}", filePath);
            }

            var fileInfo = new FileInfo(filePath);

            // Load custom metadata if available
            var customMetadata = await LoadMetadataFileAsync(filePath, ct);

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = fileInfo.Length,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc,
                ETag = GenerateETag(fileInfo),
                ContentType = GetContentType(key),
                CustomMetadata = customMetadata,
                Tier = Tier
            };
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_basePath) ?? _basePath);

                var status = driveInfo.IsReady ? HealthStatus.Healthy : HealthStatus.Unhealthy;
                var message = driveInfo.IsReady
                    ? $"Drive {driveInfo.Name} is ready ({driveInfo.DriveType}, {driveInfo.DriveFormat})"
                    : $"Drive {driveInfo.Name} is not ready";

                return Task.FromResult(new StorageHealthInfo
                {
                    Status = status,
                    LatencyMs = 0, // Will be populated by base class
                    AvailableCapacity = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null,
                    TotalCapacity = driveInfo.IsReady ? driveInfo.TotalSize : null,
                    UsedCapacity = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : null,
                    Message = message,
                    CheckedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new StorageHealthInfo
                {
                    Status = HealthStatus.Unknown,
                    Message = $"Failed to check health: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                });
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_basePath) ?? _basePath);
                return Task.FromResult<long?>(driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null);
            }
            catch
            {
                return Task.FromResult<long?>(null);
            }
        }

        #endregion

        #region Helper Methods

        private string GetFilePath(string key)
        {
            var relativePath = key.Replace('/', Path.DirectorySeparatorChar);

            // Handle Windows absolute paths (e.g., C:/path)
            if (OperatingSystem.IsWindows() && key.Length >= 3 &&
                char.IsLetter(key[0]) && key[1] == ':')
            {
                var windowsPath = relativePath;
                var normalizedBase = Path.GetFullPath(_basePath);
                var normalizedTarget = Path.GetFullPath(windowsPath);

                if (!normalizedTarget.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException(
                        $"Security violation: Path traversal attempt detected. Access denied to: {key}");
                }
                return windowsPath;
            }

            var fullPath = Path.GetFullPath(Path.Combine(_basePath, relativePath));
            var normalizedBasePath = Path.GetFullPath(_basePath);

            // Security: Prevent path traversal attacks
            if (!fullPath.StartsWith(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    $"Security violation: Path traversal attempt detected. Access denied to: {key}");
            }

            return fullPath;
        }

        private int GetOptimalBufferSize()
        {
            if (_bufferSizeOverride.HasValue)
            {
                return _bufferSizeOverride.Value;
            }

            return _detectedMediaType switch
            {
                MediaType.SSD or MediaType.NVMe => 128 * 1024,      // 128KB for SSDs
                MediaType.HDD => 64 * 1024,                          // 64KB for HDDs
                MediaType.USB or MediaType.SDCard => 32 * 1024,     // 32KB for removable
                MediaType.Optical => 2 * 1024 * 1024,                // 2MB for optical (large sequential reads)
                MediaType.Tape => 4 * 1024 * 1024,                   // 4MB for tape
                MediaType.Network => 256 * 1024,                     // 256KB for network
                _ => 81920                                           // Default .NET buffer size
            };
        }

        private FileOptions GetFileOptions()
        {
            var options = FileOptions.Asynchronous;

            if (GetOptimalAccessPattern() == AccessPattern.Sequential)
            {
                options |= FileOptions.SequentialScan;
            }
            else
            {
                options |= FileOptions.RandomAccess;
            }

            if (_useWriteThrough)
            {
                options |= FileOptions.WriteThrough;
            }

            return options;
        }

        private AccessPattern GetOptimalAccessPattern()
        {
            if (_forceAccessPattern.HasValue)
            {
                return _forceAccessPattern.Value;
            }

            return _detectedMediaType switch
            {
                MediaType.Tape or MediaType.Optical => AccessPattern.Sequential,
                _ => AccessPattern.Random
            };
        }

        private static MediaType DetectMediaType(string path)
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);

                // Check drive type first
                if (driveInfo.DriveType == DriveType.Network)
                    return MediaType.Network;
                if (driveInfo.DriveType == DriveType.CDRom)
                    return MediaType.Optical;
                if (driveInfo.DriveType == DriveType.Removable)
                    return MediaType.USB; // Could be USB or SD card

                // For fixed drives, try to detect SSD vs HDD
                if (driveInfo.DriveType == DriveType.Fixed)
                {
                    // Check if it's an NVMe path (heuristic)
                    if (path.Contains("nvme", StringComparison.OrdinalIgnoreCase))
                        return MediaType.NVMe;

                    // Default to SSD for modern systems
                    return MediaType.SSD;
                }

                return MediaType.Unknown;
            }
            catch
            {
                return MediaType.Unknown;
            }
        }

        private string GenerateETag(FileInfo fileInfo)
        {
            // Simple ETag based on last write time and size
            var hash = HashCode.Combine(fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);
            return hash.ToString("x");
        }

        private string? GetContentType(string key)
        {
            var extension = Path.GetExtension(key).ToLowerInvariant();
            return extension switch
            {
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".html" or ".htm" => "text/html",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                _ => "application/octet-stream"
            };
        }

        private async Task StoreMetadataFileAsync(string filePath, IDictionary<string, string> metadata, CancellationToken ct)
        {
            try
            {
                var metaPath = filePath + ".meta";
                var lines = metadata.Select(kvp => $"{kvp.Key}={kvp.Value}");
                await File.WriteAllLinesAsync(metaPath, lines, ct);
            }
            catch
            {
                // Ignore metadata storage failures
            }
        }

        private async Task<IReadOnlyDictionary<string, string>?> LoadMetadataFileAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var metaPath = filePath + ".meta";
                if (!File.Exists(metaPath))
                {
                    return null;
                }

                var lines = await File.ReadAllLinesAsync(metaPath, ct);
                var metadata = new Dictionary<string, string>();

                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        metadata[parts[0]] = parts[1];
                    }
                }

                return metadata.Count > 0 ? metadata : null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _writeLock?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Detected storage media type.
    /// </summary>
    public enum MediaType
    {
        Unknown,
        HDD,
        SSD,
        NVMe,
        USB,
        SDCard,
        Optical,
        Tape,
        Network,
        Floppy
    }

    /// <summary>
    /// Access pattern for storage operations.
    /// </summary>
    public enum AccessPattern
    {
        Sequential,
        Random
    }

    #endregion
}
