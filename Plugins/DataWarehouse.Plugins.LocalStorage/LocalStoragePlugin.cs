using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.LocalStorage
{
    /// <summary>
    /// Local storage plugin that is agnostic of the underlying storage media.
    /// Works with any block device: HDD, SSD, NVMe, USB drives, SD cards, tape, optical media, etc.
    ///
    /// Features:
    /// - Media-agnostic: works with any mounted filesystem
    /// - Automatic media type detection for optimization hints
    /// - Sequential vs random access optimization based on media type
    /// - Configurable buffer sizes based on media characteristics
    /// - Support for removable media with eject/mount detection
    /// - Atomic write operations using temp files and rename
    /// - Concurrent access with file locking
    /// - Multi-instance support with role-based selection
    /// - TTL-based caching support
    /// - Document indexing and search
    ///
    /// Message Commands:
    /// - storage.local.save: Save data to local storage
    /// - storage.local.load: Load data from local storage
    /// - storage.local.delete: Delete data from local storage
    /// - storage.local.exists: Check if file exists
    /// - storage.local.list: List files in directory
    /// - storage.local.info: Get storage media information
    /// - storage.instance.*: Multi-instance management
    /// </summary>
    public sealed class LocalStoragePlugin : HybridStoragePluginBase<LocalStorageConfig>
    {
        private readonly SemaphoreSlim _writeLock = new(10, 10); // Allow 10 concurrent writes
        private MediaType _detectedMediaType = MediaType.Unknown;

        public override string Id => "datawarehouse.plugins.storage.local";
        public override string Name => "Local Storage";
        public override string Version => "2.0.0";
        public override string Scheme => "file";
        public override string StorageCategory => "Local";

        /// <summary>
        /// Creates a local storage plugin with optional configuration.
        /// </summary>
        public LocalStoragePlugin(LocalStorageConfig? config = null)
            : base(config ?? new LocalStorageConfig())
        {
            if (!string.IsNullOrEmpty(_config.BasePath))
            {
                Directory.CreateDirectory(_config.BasePath);
                _detectedMediaType = DetectMediaType(_config.BasePath);
            }
        }

        /// <summary>
        /// The detected media type for the storage path.
        /// </summary>
        public MediaType DetectedMediaType => _detectedMediaType;

        /// <summary>
        /// Creates a connection (file handle wrapper) for the given configuration.
        /// </summary>
        protected override Task<object> CreateConnectionAsync(LocalStorageConfig config)
        {
            var basePath = config.BasePath ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(basePath))
            {
                Directory.CreateDirectory(basePath);
            }

            return Task.FromResult<object>(new LocalStorageConnection
            {
                BasePath = basePath,
                Config = config,
                DetectedMediaType = DetectMediaType(basePath)
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            var capabilities = base.GetCapabilities();
            capabilities.AddRange(new[]
            {
                new PluginCapabilityDescriptor { Name = "storage.local.save", DisplayName = "Save", Description = "Store data to local filesystem" },
                new PluginCapabilityDescriptor { Name = "storage.local.load", DisplayName = "Load", Description = "Retrieve data from local filesystem" },
                new PluginCapabilityDescriptor { Name = "storage.local.delete", DisplayName = "Delete", Description = "Remove data from local filesystem" },
                new PluginCapabilityDescriptor { Name = "storage.local.exists", DisplayName = "Exists", Description = "Check if file exists" },
                new PluginCapabilityDescriptor { Name = "storage.local.list", DisplayName = "List", Description = "List files in directory" },
                new PluginCapabilityDescriptor { Name = "storage.local.info", DisplayName = "Info", Description = "Get storage media information" },
                new PluginCapabilityDescriptor { Name = "storage.local.copy", DisplayName = "Copy", Description = "Copy file within storage" },
                new PluginCapabilityDescriptor { Name = "storage.local.move", DisplayName = "Move", Description = "Move/rename file" }
            });
            return capabilities;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "Media-agnostic local storage supporting any filesystem type.";
            metadata["BasePath"] = _config.BasePath ?? "current directory";
            metadata["MediaType"] = _detectedMediaType.ToString();
            metadata["AtomicWrites"] = _config.UseAtomicWrites;
            metadata["AccessPattern"] = GetOptimalAccessPattern(_config, _detectedMediaType).ToString();
            metadata["SupportsConcurrency"] = true;
            metadata["SupportsListing"] = true;
            return metadata;
        }

        /// <summary>
        /// Handles incoming messages for this plugin.
        /// </summary>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            // Handle local storage specific messages
            var response = message.Type switch
            {
                "storage.local.save" => await HandleSaveAsync(message),
                "storage.local.load" => await HandleLoadAsync(message),
                "storage.local.delete" => await HandleDeleteAsync(message),
                "storage.local.exists" => await HandleExistsAsync(message),
                "storage.local.info" => HandleInfo(message),
                "storage.local.copy" => await HandleCopyAsync(message),
                "storage.local.move" => await HandleMoveAsync(message),
                _ => null
            };

            // If not handled, delegate to base for multi-instance management
            if (response == null)
            {
                await base.OnMessageAsync(message);
            }
        }

        private async Task<MessageResponse> HandleSaveAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj) ||
                !payload.TryGetValue("data", out var dataObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri' and 'data'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var data = dataObj switch
            {
                Stream s => s,
                byte[] b => new MemoryStream(b),
                string str => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(str)),
                _ => throw new ArgumentException("Data must be Stream, byte[], or string")
            };

            // Optional: get instanceId for multi-instance support
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            await SaveAsync(uri, data, instanceId);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Success = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleLoadAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var stream = await LoadAsync(uri, instanceId);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Data = ms.ToArray(), InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleDeleteAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            await DeleteAsync(uri, instanceId);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Deleted = true, InstanceId = instanceId });
        }

        private async Task<MessageResponse> HandleExistsAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var exists = await ExistsAsync(uri, instanceId);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Exists = exists, InstanceId = instanceId });
        }

        private MessageResponse HandleInfo(PluginMessage message)
        {
            var driveInfo = GetDriveInfo();
            return MessageResponse.Ok(new
            {
                MediaType = _detectedMediaType.ToString(),
                AccessPattern = GetOptimalAccessPattern(_config, _detectedMediaType).ToString(),
                DriveInfo = driveInfo,
                Instances = _connectionRegistry.Count
            });
        }

        private async Task<MessageResponse> HandleCopyAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("source", out var sourceObj) ||
                !payload.TryGetValue("destination", out var destObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'source' and 'destination'");
            }

            var sourceUri = sourceObj is Uri su ? su : new Uri(sourceObj.ToString()!);
            var destUri = destObj is Uri du ? du : new Uri(destObj.ToString()!);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var basePath = GetBasePath(instanceId);
            var sourcePath = GetFilePath(sourceUri, basePath);
            var destPath = GetFilePath(destUri, basePath);

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            await Task.Run(() => File.Copy(sourcePath, destPath, overwrite: true));
            return MessageResponse.Ok(new { Source = sourceUri.ToString(), Destination = destUri.ToString(), Success = true });
        }

        private async Task<MessageResponse> HandleMoveAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("source", out var sourceObj) ||
                !payload.TryGetValue("destination", out var destObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'source' and 'destination'");
            }

            var sourceUri = sourceObj is Uri su ? su : new Uri(sourceObj.ToString()!);
            var destUri = destObj is Uri du ? du : new Uri(destObj.ToString()!);
            var instanceId = payload.TryGetValue("instanceId", out var instId) ? instId?.ToString() : null;

            var basePath = GetBasePath(instanceId);
            var sourcePath = GetFilePath(sourceUri, basePath);
            var destPath = GetFilePath(destUri, basePath);

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            await Task.Run(() => File.Move(sourcePath, destPath, overwrite: true));
            return MessageResponse.Ok(new { Source = sourceUri.ToString(), Destination = destUri.ToString(), Success = true });
        }

        #region Storage Operations with Instance Support

        /// <summary>
        /// Save data to storage, optionally targeting a specific instance.
        /// </summary>
        public async Task SaveAsync(Uri uri, Stream data, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var basePath = GetBasePath(instanceId);
            var config = GetConfig(instanceId);
            var filePath = GetFilePath(uri, basePath);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await _writeLock.WaitAsync();
            try
            {
                var bufferSize = GetOptimalBufferSize(config);

                if (config.UseAtomicWrites)
                {
                    // Atomic write using temp file and rename
                    var tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
                    try
                    {
                        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                            FileShare.None, bufferSize, GetFileOptions(config, GetMediaType(instanceId))))
                        {
                            await data.CopyToAsync(fs);
                            await fs.FlushAsync();
                        }

                        // Atomic rename
                        File.Move(tempPath, filePath, overwrite: true);
                    }
                    catch
                    {
                        try { File.Delete(tempPath); } catch { }
                        throw;
                    }
                }
                else
                {
                    await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize, GetFileOptions(config, GetMediaType(instanceId)));
                    await data.CopyToAsync(fs);
                    await fs.FlushAsync();
                }

                // Auto-index if enabled
                if (config.EnableIndexing)
                {
                    var fileInfo = new FileInfo(filePath);
                    await IndexDocumentAsync(uri.ToString(), new Dictionary<string, object>
                    {
                        ["uri"] = uri.ToString(),
                        ["path"] = filePath,
                        ["size"] = fileInfo.Length,
                        ["lastModified"] = fileInfo.LastWriteTimeUtc,
                        ["instanceId"] = instanceId ?? "default"
                    });
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public override Task SaveAsync(Uri uri, Stream data) => SaveAsync(uri, data, null);

        /// <summary>
        /// Load data from storage, optionally from a specific instance.
        /// </summary>
        public async Task<Stream> LoadAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var basePath = GetBasePath(instanceId);
            var config = GetConfig(instanceId);
            var filePath = GetFilePath(uri, basePath);

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            var bufferSize = GetOptimalBufferSize(config);

            // Update last access for caching
            await TouchAsync(uri);

            // Return buffered stream for optimal read performance
            var mediaType = GetMediaType(instanceId);
            var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize, GetFileOptions(config, mediaType));

            // For sequential media (tape, optical), read entire file into memory
            if (GetOptimalAccessPattern(config, mediaType) == AccessPattern.Sequential)
            {
                var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                await fs.DisposeAsync();
                ms.Position = 0;
                return ms;
            }

            return fs;
        }

        public override Task<Stream> LoadAsync(Uri uri) => LoadAsync(uri, null);

        /// <summary>
        /// Delete data from storage, optionally from a specific instance.
        /// </summary>
        public Task DeleteAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var basePath = GetBasePath(instanceId);
            var filePath = GetFilePath(uri, basePath);
            if (File.Exists(filePath))
                File.Delete(filePath);

            // Remove from index
            _ = RemoveFromIndexAsync(uri.ToString());

            return Task.CompletedTask;
        }

        public override Task DeleteAsync(Uri uri) => DeleteAsync(uri, null);

        /// <summary>
        /// Check if data exists in storage, optionally in a specific instance.
        /// </summary>
        public Task<bool> ExistsAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);
            var basePath = GetBasePath(instanceId);
            var filePath = GetFilePath(uri, basePath);
            return Task.FromResult(File.Exists(filePath));
        }

        public override Task<bool> ExistsAsync(Uri uri) => ExistsAsync(uri, null);

        public override async IAsyncEnumerable<StorageListItem> ListFilesAsync(
            string prefix = "",
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var basePath = _config.BasePath ?? Directory.GetCurrentDirectory();
            var searchPath = string.IsNullOrEmpty(prefix) ? basePath : Path.Combine(basePath, prefix);

            if (!Directory.Exists(searchPath))
            {
                // Try treating prefix as a file pattern
                searchPath = basePath;
            }

            var files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested)
                    yield break;

                var relativePath = Path.GetRelativePath(basePath, file);
                if (!string.IsNullOrEmpty(prefix) && !relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var uri = new Uri($"file:///{relativePath.Replace(Path.DirectorySeparatorChar, '/')}");
                var fileInfo = new FileInfo(file);
                yield return new StorageListItem(uri, fileInfo.Length);

                await Task.Yield();
            }
        }

        #endregion

        #region Helper Methods

        private string GetBasePath(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _config.BasePath ?? Directory.GetCurrentDirectory();

            var instance = _connectionRegistry.Get(instanceId);
            return instance?.Config.BasePath ?? _config.BasePath ?? Directory.GetCurrentDirectory();
        }

        private LocalStorageConfig GetConfig(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _config;

            var instance = _connectionRegistry.Get(instanceId);
            return instance?.Config ?? _config;
        }

        private MediaType GetMediaType(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _detectedMediaType;

            var instance = _connectionRegistry.Get(instanceId);
            if (instance?.Metadata.TryGetValue("mediaType", out var mt) == true && mt is MediaType mediaType)
                return mediaType;

            return _detectedMediaType;
        }

        private string GetFilePath(Uri uri, string basePath)
        {
            var relativePath = uri.LocalPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

            // Handle Windows drive letters (e.g., file:///C:/path)
            if (OperatingSystem.IsWindows() && uri.LocalPath.Length >= 3 &&
                char.IsLetter(uri.LocalPath[1]) && uri.LocalPath[2] == ':')
            {
                // For absolute Windows paths, still validate against BasePath if set
                var windowsPath = uri.LocalPath.TrimStart('/');
                var normalizedBase = Path.GetFullPath(basePath);
                var normalizedTarget = Path.GetFullPath(windowsPath);
                if (!normalizedTarget.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException(
                        $"Security violation: Path traversal attempt detected. Access denied.");
                }
                return windowsPath;
            }

            var fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
            var normalizedBasePath = Path.GetFullPath(basePath);

            // Security: Prevent path traversal attacks
            if (!fullPath.StartsWith(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    $"Security violation: Path traversal attempt detected. Access denied.");
            }
            return fullPath;
        }

        private int GetOptimalBufferSize(LocalStorageConfig config)
        {
            if (config.BufferSize.HasValue)
                return config.BufferSize.Value;

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

        private FileOptions GetFileOptions(LocalStorageConfig config, MediaType mediaType)
        {
            var options = FileOptions.Asynchronous;

            if (GetOptimalAccessPattern(config, mediaType) == AccessPattern.Sequential)
                options |= FileOptions.SequentialScan;
            else
                options |= FileOptions.RandomAccess;

            if (config.UseWriteThrough)
                options |= FileOptions.WriteThrough;

            return options;
        }

        private AccessPattern GetOptimalAccessPattern(LocalStorageConfig config, MediaType mediaType)
        {
            if (config.ForceAccessPattern.HasValue)
                return config.ForceAccessPattern.Value;

            return mediaType switch
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
                // This is a heuristic - actual detection requires platform-specific APIs
                if (driveInfo.DriveType == DriveType.Fixed)
                {
                    // Check if it's an NVMe path (heuristic)
                    if (path.Contains("nvme", StringComparison.OrdinalIgnoreCase))
                        return MediaType.NVMe;

                    // Default to SSD for modern systems - could be enhanced with WMI/ioctl
                    return MediaType.SSD;
                }

                return MediaType.Unknown;
            }
            catch
            {
                return MediaType.Unknown;
            }
        }

        private object? GetDriveInfo()
        {
            try
            {
                var path = _config.BasePath ?? Directory.GetCurrentDirectory();
                var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);

                return new
                {
                    Name = driveInfo.Name,
                    DriveType = driveInfo.DriveType.ToString(),
                    DriveFormat = driveInfo.DriveFormat,
                    TotalSize = driveInfo.TotalSize,
                    AvailableFreeSpace = driveInfo.AvailableFreeSpace,
                    IsReady = driveInfo.IsReady
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }

    /// <summary>
    /// Internal connection wrapper for local storage instances.
    /// </summary>
    internal class LocalStorageConnection
    {
        public required string BasePath { get; init; }
        public required LocalStorageConfig Config { get; init; }
        public MediaType DetectedMediaType { get; init; }
    }

    /// <summary>
    /// Configuration for local storage.
    /// </summary>
    public class LocalStorageConfig : StorageConfigBase
    {
        /// <summary>
        /// Base path for storage (null = current directory).
        /// </summary>
        public string? BasePath { get; set; }

        /// <summary>
        /// Use atomic writes (temp file + rename).
        /// </summary>
        public bool UseAtomicWrites { get; set; } = true;

        /// <summary>
        /// Use write-through mode (bypass OS cache).
        /// </summary>
        public bool UseWriteThrough { get; set; }

        /// <summary>
        /// Override automatic buffer size detection.
        /// </summary>
        public int? BufferSize { get; set; }

        /// <summary>
        /// Force specific access pattern (override auto-detection).
        /// </summary>
        public AccessPattern? ForceAccessPattern { get; set; }

        /// <summary>
        /// Creates default configuration for current directory.
        /// </summary>
        public static LocalStorageConfig Default => new();

        /// <summary>
        /// Creates configuration for a specific path.
        /// </summary>
        public static LocalStorageConfig ForPath(string path) => new() { BasePath = path };

        /// <summary>
        /// Creates configuration for a specific path with instance settings.
        /// </summary>
        public static LocalStorageConfig ForPath(string path, string instanceId, bool enableCaching = true) => new()
        {
            BasePath = path,
            InstanceId = instanceId,
            EnableCaching = enableCaching
        };

        /// <summary>
        /// Creates configuration optimized for tape storage.
        /// </summary>
        public static LocalStorageConfig ForTape(string path) => new()
        {
            BasePath = path,
            ForceAccessPattern = AccessPattern.Sequential,
            BufferSize = 4 * 1024 * 1024
        };

        /// <summary>
        /// Creates configuration optimized for optical media.
        /// </summary>
        public static LocalStorageConfig ForOptical(string path) => new()
        {
            BasePath = path,
            ForceAccessPattern = AccessPattern.Sequential,
            BufferSize = 2 * 1024 * 1024,
            UseAtomicWrites = false // Can't rename on read-only media
        };
    }

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
}
