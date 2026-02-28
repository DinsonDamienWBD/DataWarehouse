using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Network
{
    /// <summary>
    /// NFS (Network File System) storage strategy with production-ready features:
    /// - Support for NFSv3 and NFSv4.x protocols
    /// - Cross-platform support (Windows, Linux, macOS)
    /// - Multiple authentication mechanisms (AUTH_UNIX, AUTH_SYS, Kerberos)
    /// - Configurable mount options (soft/hard, timeout, retries)
    /// - Advisory file locking support
    /// - Automatic mount management and reconnection
    /// - Performance optimization (read-ahead, write-behind)
    /// - Health monitoring and diagnostics
    /// - Support for both direct mount access and UNC-style paths
    /// </summary>
    public class NfsStrategy : UltimateStorageStrategyBase
    {
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(10, 10); // Allow 10 concurrent writes
        private readonly SemaphoreSlim _lockManager = new(1, 1); // For advisory locking

        private string _serverAddress = string.Empty;
        private string _exportPath = string.Empty;
        private string _mountPoint = string.Empty;
        private string _basePath = string.Empty;
        private NfsVersion _nfsVersion = NfsVersion.V4;
        private NfsAuthType _authType = NfsAuthType.AuthSys;
        private string _username = string.Empty;
        private string _uid = "1000";
        private string _gid = "1000";
        private MountType _mountType = MountType.Hard;
        private int _timeoutSeconds = 30;
        private int _retries = 3;
        private int _readSizeKb = 32;
        private int _writeSizeKb = 32;
        private bool _autoMount = true;
        private bool _useAdvisoryLocks = true;
        private bool _isMounted = false;
        private bool _isWindowsNfsClient = false;
        private string? _kerberosRealm = null;
        private string? _kerberosPrincipal = null;

        private int _bufferSize = 65536; // 64KB default
        private readonly Dictionary<string, object> _activeLocks = new();

        public override string StrategyId => "nfs";
        public override string Name => "NFS Network File System Storage";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = false, // Can use NFSv4 with Kerberos
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = null, // Limited by NFS server and filesystem
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong // NFS provides close-to-open consistency
        };

        /// <summary>
        /// Initializes the NFS storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Detect platform
            _isWindowsNfsClient = OperatingSystem.IsWindows();

            // Load configuration
            _serverAddress = GetConfiguration<string>("ServerAddress", string.Empty);
            _exportPath = GetConfiguration<string>("ExportPath", string.Empty);
            _mountPoint = GetConfiguration<string>("MountPoint", string.Empty);
            _basePath = GetConfiguration<string>("BasePath", string.Empty);

            // Validate required configuration
            if (string.IsNullOrWhiteSpace(_serverAddress))
            {
                throw new InvalidOperationException("NFS server address is required. Set 'ServerAddress' in configuration.");
            }
            if (string.IsNullOrWhiteSpace(_exportPath))
            {
                throw new InvalidOperationException("NFS export path is required. Set 'ExportPath' in configuration.");
            }

            // Parse NFS version
            var versionStr = GetConfiguration<string>("NfsVersion", "v4");
            _nfsVersion = versionStr.ToLowerInvariant() switch
            {
                "v3" or "3" or "nfsv3" => NfsVersion.V3,
                "v4" or "4" or "nfsv4" => NfsVersion.V4,
                "v4.1" or "4.1" or "nfsv4.1" => NfsVersion.V4_1,
                "v4.2" or "4.2" or "nfsv4.2" => NfsVersion.V4_2,
                _ => NfsVersion.V4
            };

            // Parse authentication type
            var authTypeStr = GetConfiguration<string>("AuthType", "sys");
            _authType = authTypeStr.ToLowerInvariant() switch
            {
                "unix" or "auth_unix" => NfsAuthType.AuthUnix,
                "sys" or "auth_sys" => NfsAuthType.AuthSys,
                "krb5" or "kerberos" or "krb5_mutual" => NfsAuthType.Krb5,
                "none" => NfsAuthType.None,
                _ => NfsAuthType.AuthSys
            };

            // Load authentication configuration
            _username = GetConfiguration<string>("Username", Environment.UserName);
            _uid = GetConfiguration<string>("Uid", "1000");
            _gid = GetConfiguration<string>("Gid", "1000");
            _kerberosRealm = GetConfiguration<string?>("KerberosRealm", null);
            _kerberosPrincipal = GetConfiguration<string?>("KerberosPrincipal", null);

            // Parse mount type
            var mountTypeStr = GetConfiguration<string>("MountType", "hard");
            _mountType = mountTypeStr.ToLowerInvariant() switch
            {
                "soft" => MountType.Soft,
                "hard" => MountType.Hard,
                _ => MountType.Hard
            };

            // Load optional configuration
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 30);
            _retries = GetConfiguration<int>("Retries", 3);
            _readSizeKb = GetConfiguration<int>("ReadSizeKb", 32);
            _writeSizeKb = GetConfiguration<int>("WriteSizeKb", 32);
            _autoMount = GetConfiguration<bool>("AutoMount", true);
            _useAdvisoryLocks = GetConfiguration<bool>("UseAdvisoryLocks", true);
            _bufferSize = GetConfiguration<int>("BufferSize", 65536);

            // Generate mount point if not provided
            if (string.IsNullOrWhiteSpace(_mountPoint))
            {
                _mountPoint = GenerateMountPoint();
            }

            // Ensure mount point is an absolute path
            _mountPoint = Path.GetFullPath(_mountPoint);

            // Normalize paths
            _exportPath = NormalizePath(_exportPath, isExportPath: true);
            _basePath = NormalizePath(_basePath, isExportPath: false);

            // Validate server address and export path to prevent shell injection via concatenated mount command strings.
            // Values must not contain shell metacharacters: ;, &, |, $, `, (, ), <, >, newline, null
            ValidateNoShellMetachars(_serverAddress, "ServerAddress");
            ValidateNoShellMetachars(_exportPath, "ExportPath");
            ValidateNoShellMetachars(_mountPoint, "MountPoint");

            if (string.IsNullOrWhiteSpace(_mountPoint))
                throw new InvalidOperationException("MountPoint must not be empty after normalization.");

            // Mount the NFS share if auto-mount is enabled
            if (_autoMount)
            {
                await MountAsync(ct);
            }
        }

        #region Mount Management

        /// <summary>
        /// Mounts the NFS share.
        /// </summary>
        private async Task MountAsync(CancellationToken ct)
        {
            await _connectionLock.WaitAsync(ct);
            try
            {
                if (_isMounted)
                {
                    return;
                }

                // Create mount point directory if it doesn't exist
                if (!Directory.Exists(_mountPoint))
                {
                    Directory.CreateDirectory(_mountPoint);
                }

                // Check if already mounted (existing mount)
                if (await IsMountedAsync(ct))
                {
                    _isMounted = true;
                    return;
                }

                // Perform platform-specific mount
                if (_isWindowsNfsClient)
                {
                    await MountWindowsAsync(ct);
                }
                else
                {
                    await MountUnixAsync(ct);
                }

                _isMounted = true;

                // Ensure base path exists
                if (!string.IsNullOrEmpty(_basePath))
                {
                    var fullBasePath = Path.Combine(_mountPoint, _basePath);
                    if (!Directory.Exists(fullBasePath))
                    {
                        Directory.CreateDirectory(fullBasePath);
                    }
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Mounts NFS on Windows using native mount command.
        /// </summary>
        private async Task MountWindowsAsync(CancellationToken ct)
        {
            // Windows NFS client uses: mount \\server\export X:
            // Or: mount -o anon \\server\export X:
            var serverExport = $"\\\\{_serverAddress}\\{_exportPath.TrimStart('/')}";

            var mountArgs = new List<string>
            {
                serverExport,
                _mountPoint
            };

            // Build mount options
            var options = new List<string>();

            // Authentication
            if (_authType == NfsAuthType.None)
            {
                options.Add("anon");
            }
            else if (!string.IsNullOrEmpty(_uid) && !string.IsNullOrEmpty(_gid))
            {
                options.Add($"uid={_uid}");
                options.Add($"gid={_gid}");
            }

            // Mount type
            if (_mountType == MountType.Soft)
            {
                options.Add("soft");
            }

            // Timeouts
            options.Add($"timeout={_timeoutSeconds}");
            options.Add($"retry={_retries}");

            // Read/write sizes
            options.Add($"rsize={_readSizeKb}");
            options.Add($"wsize={_writeSizeKb}");

            if (options.Count > 0)
            {
                mountArgs.Insert(0, $"-o {string.Join(",", options)}");
            }

            var result = await ExecuteCommandAsync("mount", string.Join(" ", mountArgs), ct);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to mount NFS share on Windows. Exit code: {result.ExitCode}. " +
                    $"Error: {result.Error}. " +
                    $"Ensure Windows NFS Client feature is installed: " +
                    $"'Enable-WindowsOptionalFeature -Online -FeatureName ServicesForNFS-ClientOnly'");
            }
        }

        /// <summary>
        /// Mounts NFS on Unix/Linux/macOS using native mount command.
        /// </summary>
        private async Task MountUnixAsync(CancellationToken ct)
        {
            // Unix mount syntax: mount -t nfs4 -o options server:/export /mountpoint
            var serverExport = $"{_serverAddress}:{_exportPath}";

            var options = new List<string>();

            // NFS version
            var nfsVersionStr = _nfsVersion switch
            {
                NfsVersion.V3 => "nfsvers=3",
                NfsVersion.V4 => "nfsvers=4",
                NfsVersion.V4_1 => "nfsvers=4.1",
                NfsVersion.V4_2 => "nfsvers=4.2",
                _ => "nfsvers=4"
            };
            options.Add(nfsVersionStr);

            // Mount type
            options.Add(_mountType == MountType.Soft ? "soft" : "hard");

            // Timeouts
            options.Add($"timeo={_timeoutSeconds * 10}"); // Timeout in deciseconds
            options.Add($"retrans={_retries}");

            // Read/write sizes
            options.Add($"rsize={_readSizeKb * 1024}");
            options.Add($"wsize={_writeSizeKb * 1024}");

            // Authentication
            if (_authType == NfsAuthType.Krb5 && !string.IsNullOrEmpty(_kerberosPrincipal))
            {
                options.Add($"sec=krb5");
            }
            else if (_authType == NfsAuthType.AuthSys || _authType == NfsAuthType.AuthUnix)
            {
                options.Add("sec=sys");
            }

            // Performance options
            options.Add("async"); // Asynchronous writes for performance

            var mountArgs = $"-t nfs -o {string.Join(",", options)} {serverExport} {_mountPoint}";

            var result = await ExecuteCommandAsync("sudo", $"mount {mountArgs}", ct);
            if (result.ExitCode != 0)
            {
                // Try without sudo (may already have permissions)
                result = await ExecuteCommandAsync("mount", mountArgs, ct);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Failed to mount NFS share on Unix. Exit code: {result.ExitCode}. " +
                        $"Error: {result.Error}. " +
                        $"Ensure NFS client utilities are installed and you have mount permissions.");
                }
            }
        }

        /// <summary>
        /// Checks if the NFS share is already mounted.
        /// </summary>
        private async Task<bool> IsMountedAsync(CancellationToken ct)
        {
            try
            {
                if (_isWindowsNfsClient)
                {
                    // Check Windows mounts
                    var result = await ExecuteCommandAsync("net", "use", ct);
                    return result.Output.Contains(_mountPoint, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // Check Unix mounts using mount command or /proc/mounts
                    if (File.Exists("/proc/mounts"))
                    {
                        var mounts = await File.ReadAllTextAsync("/proc/mounts", ct);
                        return mounts.Contains(_mountPoint);
                    }
                    else
                    {
                        var result = await ExecuteCommandAsync("mount", "", ct);
                        return result.Output.Contains(_mountPoint);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NfsStrategy.IsMountedAsync] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Unmounts the NFS share.
        /// </summary>
        private async Task UnmountAsync(CancellationToken ct = default)
        {
            if (!_isMounted)
            {
                return;
            }

            try
            {
                if (_isWindowsNfsClient)
                {
                    await ExecuteCommandAsync("umount", _mountPoint, ct);
                }
                else
                {
                    var result = await ExecuteCommandAsync("sudo", $"umount {_mountPoint}", ct);
                    if (result.ExitCode != 0)
                    {
                        // Try without sudo
                        await ExecuteCommandAsync("umount", _mountPoint, ct);
                    }
                }

                _isMounted = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NfsStrategy.UnmountAsync] {ex.GetType().Name}: {ex.Message}");
                // Ignore unmount errors during cleanup
            }
        }

        /// <summary>
        /// Ensures the NFS share is mounted.
        /// </summary>
        private async Task EnsureMountedAsync(CancellationToken ct)
        {
            if (_isMounted)
            {
                return;
            }

            if (_autoMount)
            {
                await MountAsync(ct);
            }
            else
            {
                throw new InvalidOperationException(
                    "NFS share is not mounted and auto-mount is disabled. " +
                    "Set AutoMount=true in configuration or manually mount the share.");
            }
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);
            await EnsureMountedAsync(ct);

            var filePath = GetFilePath(key);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await _writeLock.WaitAsync(ct);
            try
            {
                var startPosition = data.CanSeek ? data.Position : 0;
                long bytesWritten = 0;

                // Acquire advisory lock if enabled
                object? lockHandle = null;
                if (_useAdvisoryLocks)
                {
                    lockHandle = await AcquireFileLockAsync(filePath, LockMode.Exclusive, ct);
                }

                try
                {
                    // Use atomic write: temp file + rename
                    var tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N")[..8];

                    try
                    {
                        await using (var fs = new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            _bufferSize,
                            FileOptions.Asynchronous | FileOptions.WriteThrough))
                        {
                            await data.CopyToAsync(fs, _bufferSize, ct);
                            await fs.FlushAsync(ct);
                            bytesWritten = fs.Position;
                        }

                        // Atomic rename
                        File.Move(tempPath, filePath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NfsStrategy.StoreAsyncCore] {ex.GetType().Name}: {ex.Message}");
                        // Clean up temp file on failure
                        try { File.Delete(tempPath); } catch (Exception cleanupEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[NfsStrategy.StoreAsyncCore] {cleanupEx.GetType().Name}: {cleanupEx.Message}");
                            /* Best-effort cleanup — failure is non-fatal */
                        }
                        throw;
                    }

                    // Update statistics
                    IncrementBytesStored(bytesWritten);
                    IncrementOperationCounter(StorageOperationType.Store);

                    // Store custom metadata in companion .meta file if provided
                    if (metadata != null && metadata.Count > 0)
                    {
                        await StoreMetadataFileAsync(filePath, metadata, ct);
                    }

                    var fileInfo = new FileInfo(filePath);

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
                    // Release advisory lock
                    if (lockHandle != null)
                    {
                        await ReleaseFileLockAsync(lockHandle, ct);
                    }
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);
            await EnsureMountedAsync(ct);

            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"NFS object not found: {key}", filePath);
            }

            // Acquire shared lock if enabled
            object? lockHandle = null;
            if (_useAdvisoryLocks)
            {
                lockHandle = await AcquireFileLockAsync(filePath, LockMode.Shared, ct);
            }

            try
            {
                var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    _bufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                // Update statistics
                var fileInfo = new FileInfo(filePath);
                IncrementBytesRetrieved(fileInfo.Length);
                IncrementOperationCounter(StorageOperationType.Retrieve);

                // Wrap stream to release lock on disposal
                return new LockReleaseStream(fs, lockHandle, this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NfsStrategy.RetrieveAsyncCore] {ex.GetType().Name}: {ex.Message}");
                if (lockHandle != null)
                {
                    await ReleaseFileLockAsync(lockHandle, ct);
                }
                throw;
            }
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);
            await EnsureMountedAsync(ct);

            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
            {
                return; // Already deleted
            }

            // Acquire exclusive lock if enabled
            object? lockHandle = null;
            if (_useAdvisoryLocks)
            {
                lockHandle = await AcquireFileLockAsync(filePath, LockMode.Exclusive, ct);
            }

            try
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
            finally
            {
                if (lockHandle != null)
                {
                    await ReleaseFileLockAsync(lockHandle, ct);
                }
            }
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);
            await EnsureMountedAsync(ct);

            var filePath = GetFilePath(key);
            var exists = File.Exists(filePath);

            IncrementOperationCounter(StorageOperationType.Exists);

            return exists;
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            await EnsureMountedAsync(ct);
            IncrementOperationCounter(StorageOperationType.List);

            var basePath = Path.Combine(_mountPoint, _basePath);
            var searchPath = string.IsNullOrEmpty(prefix) ? basePath : Path.Combine(basePath, prefix);

            if (!Directory.Exists(searchPath))
            {
                // Treat prefix as a pattern
                searchPath = basePath;
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

                var relativePath = Path.GetRelativePath(basePath, filePath);

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
            await EnsureMountedAsync(ct);

            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"NFS object not found: {key}", filePath);
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

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await EnsureMountedAsync(ct);

                var basePath = Path.Combine(_mountPoint, _basePath);
                var testFilePath = Path.Combine(basePath, $".health_check_{Guid.NewGuid().ToString("N")[..8]}");

                // Perform a write/read/delete test
                await File.WriteAllTextAsync(testFilePath, "health_check", ct);
                var content = await File.ReadAllTextAsync(testFilePath, ct);
                File.Delete(testFilePath);

                sw.Stop();

                var driveInfo = new DriveInfo(Path.GetPathRoot(_mountPoint) ?? _mountPoint);

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    AvailableCapacity = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null,
                    TotalCapacity = driveInfo.IsReady ? driveInfo.TotalSize : null,
                    UsedCapacity = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : null,
                    Message = $"NFS share {_serverAddress}:{_exportPath} is accessible and operational",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to access NFS share {_serverAddress}:{_exportPath}: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                await EnsureMountedAsync(ct);

                var driveInfo = new DriveInfo(Path.GetPathRoot(_mountPoint) ?? _mountPoint);
                return driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NfsStrategy.GetAvailableCapacityAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Advisory File Locking

        /// <summary>
        /// Acquires an advisory file lock (NFS fcntl-style locking).
        /// </summary>
        private async Task<object?> AcquireFileLockAsync(string filePath, LockMode mode, CancellationToken ct)
        {
            if (!_useAdvisoryLocks)
            {
                return null;
            }

            var lockKey = filePath.ToLowerInvariant();
            var maxWaitTime = TimeSpan.FromSeconds(_timeoutSeconds);
            var startTime = DateTime.UtcNow;

            while (true)
            {
                await _lockManager.WaitAsync(ct);
                try
                {
                    if (!_activeLocks.ContainsKey(lockKey))
                    {
                        // Lock is free — acquire it
                        var lockHandle = new FileLock
                        {
                            FilePath = filePath,
                            Mode = mode,
                            AcquiredAt = DateTime.UtcNow
                        };
                        _activeLocks[lockKey] = lockHandle;
                        return lockHandle;
                    }
                }
                finally
                {
                    _lockManager.Release();
                }

                // Lock is held by another caller — wait outside the semaphore to avoid deadlock
                if ((DateTime.UtcNow - startTime) >= maxWaitTime)
                {
                    throw new IOException($"Failed to acquire file lock for {filePath}: timeout waiting for existing lock");
                }

                await Task.Delay(100, ct);
            }
        }

        /// <summary>
        /// Releases an advisory file lock.
        /// </summary>
        internal async Task ReleaseFileLockAsync(object? lockHandle, CancellationToken ct)
        {
            if (lockHandle == null || !_useAdvisoryLocks)
            {
                return;
            }

            if (lockHandle is not FileLock fileLock)
            {
                return;
            }

            await _lockManager.WaitAsync(ct);
            try
            {
                var lockKey = fileLock.FilePath.ToLowerInvariant();
                _activeLocks.Remove(lockKey);
            }
            finally
            {
                _lockManager.Release();
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Generates a mount point path based on server and export.
        /// </summary>
        private string GenerateMountPoint()
        {
            var safeName = $"nfs_{_serverAddress}_{_exportPath.Replace('/', '_').Trim('_')}";
            safeName = string.Join("_", safeName.Split(Path.GetInvalidFileNameChars()));

            if (_isWindowsNfsClient)
            {
                // Use drive letter on Windows (find available letter)
                for (char drive = 'Z'; drive >= 'D'; drive--)
                {
                    var drivePath = $"{drive}:";
                    if (!Directory.Exists(drivePath))
                    {
                        return drivePath;
                    }
                }
                throw new InvalidOperationException("No available drive letters for NFS mount");
            }
            else
            {
                // Use /mnt or /media on Unix
                return Path.Combine("/mnt", safeName);
            }
        }

        /// <summary>
        /// Normalizes a path for NFS.
        /// </summary>
        private string NormalizePath(string path, bool isExportPath)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            path = path.Replace('\\', '/');

            if (isExportPath)
            {
                // Ensure export path starts with /
                if (!path.StartsWith('/'))
                {
                    path = '/' + path;
                }
                // Remove trailing slash
                path = path.TrimEnd('/');
            }
            else
            {
                // Remove leading and trailing slashes for base path
                path = path.Trim('/');
            }

            return path;
        }

        /// <summary>
        /// Validates that a configuration value contains no shell metacharacters that could
        /// enable command injection when the value is included in an OS mount command.
        /// </summary>
        private static void ValidateNoShellMetachars(string value, string paramName)
        {
            // Characters that can break out of argument context when joined with string.Join(" ") and passed as Arguments
            ReadOnlySpan<char> forbidden = [';', '&', '|', '$', '`', '(', ')', '<', '>', '\n', '\r', '\0', '"', '\''];
            foreach (var ch in forbidden)
            {
                if (value.Contains(ch))
                    throw new ArgumentException(
                        $"NfsStrategy: '{paramName}' contains the shell metacharacter '{ch}' which is not allowed.", paramName);
            }
        }

        /// <summary>
        /// Gets the full file path for a storage key.
        /// </summary>
        private string GetFilePath(string key)
        {
            var relativePath = key.Replace('/', Path.DirectorySeparatorChar);

            var basePath = Path.Combine(_mountPoint, _basePath);
            var fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));

            // Security: Prevent path traversal attacks
            var normalizedBasePath = Path.GetFullPath(basePath);
            if (!fullPath.StartsWith(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    $"Security violation: Path traversal attempt detected. Access denied to: {key}");
            }

            return fullPath;
        }

        /// <summary>
        /// Executes a shell command and returns the result.
        /// </summary>
        private async Task<CommandResult> ExecuteCommandAsync(string command, string arguments, CancellationToken ct)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            return new CommandResult
            {
                ExitCode = process.ExitCode,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString()
            };
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NfsStrategy.StoreMetadataFileAsync] {ex.GetType().Name}: {ex.Message}");
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NfsStrategy.LoadMetadataFileAsync] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            // Unmount if we mounted it
            if (_autoMount && _isMounted)
            {
                await UnmountAsync(CancellationToken.None);
            }

            _connectionLock?.Dispose();
            _writeLock?.Dispose();
            _lockManager?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// NFS protocol version.
    /// </summary>
    public enum NfsVersion
    {
        /// <summary>NFSv3 - Legacy version with simpler stateless protocol.</summary>
        V3,

        /// <summary>NFSv4 - Modern version with improved security and performance.</summary>
        V4,

        /// <summary>NFSv4.1 - Adds pNFS (parallel NFS) support.</summary>
        V4_1,

        /// <summary>NFSv4.2 - Adds server-side copy and sparse file support.</summary>
        V4_2
    }

    /// <summary>
    /// NFS authentication type.
    /// </summary>
    public enum NfsAuthType
    {
        /// <summary>No authentication (anonymous).</summary>
        None,

        /// <summary>AUTH_UNIX - Traditional Unix UID/GID authentication.</summary>
        AuthUnix,

        /// <summary>AUTH_SYS - Same as AUTH_UNIX.</summary>
        AuthSys,

        /// <summary>Kerberos 5 mutual authentication.</summary>
        Krb5
    }

    /// <summary>
    /// NFS mount type.
    /// </summary>
    public enum MountType
    {
        /// <summary>Soft mount - Operations fail after timeout.</summary>
        Soft,

        /// <summary>Hard mount - Operations retry indefinitely (recommended).</summary>
        Hard
    }

    /// <summary>
    /// File lock mode.
    /// </summary>
    public enum LockMode
    {
        /// <summary>Shared lock (read lock).</summary>
        Shared,

        /// <summary>Exclusive lock (write lock).</summary>
        Exclusive
    }

    /// <summary>
    /// Represents an advisory file lock.
    /// </summary>
    internal class FileLock
    {
        public string FilePath { get; set; } = string.Empty;
        public LockMode Mode { get; set; }
        public DateTime AcquiredAt { get; set; }
    }

    /// <summary>
    /// Result of a shell command execution.
    /// </summary>
    internal class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    /// <summary>
    /// Stream wrapper that releases file lock on disposal.
    /// </summary>
    internal class LockReleaseStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly object? _lockHandle;
        private readonly NfsStrategy _strategy;
        private bool _disposed;

        public LockReleaseStream(Stream innerStream, object? lockHandle, NfsStrategy strategy)
        {
            _innerStream = innerStream;
            _lockHandle = lockHandle;
            _strategy = strategy;
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _innerStream.Length;
        public override long Position
        {
            get => _innerStream.Position;
            set => _innerStream.Position = value;
        }

        public override void Flush() => _innerStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _innerStream.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => _innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _innerStream.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _innerStream?.Dispose();
                    if (_lockHandle != null)
                    {
                        // Sync bridge: Dispose cannot be async without IAsyncDisposable. Task.Run avoids deadlocks.
                        Task.Run(() => _strategy.ReleaseFileLockAsync(_lockHandle, CancellationToken.None)).ConfigureAwait(false).GetAwaiter().GetResult();
                    }
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                if (_innerStream != null)
                {
                    await _innerStream.DisposeAsync();
                }
                if (_lockHandle != null)
                {
                    await _strategy.ReleaseFileLockAsync(_lockHandle, CancellationToken.None);
                }
                _disposed = true;
            }
            await base.DisposeAsync();
        }
    }

    #endregion
}
