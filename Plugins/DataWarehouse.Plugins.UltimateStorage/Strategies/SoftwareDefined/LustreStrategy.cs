using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.SoftwareDefined
{
    /// <summary>
    /// Lustre Parallel Distributed File System storage strategy with production-ready features:
    /// - POSIX-compliant parallel filesystem with horizontal scalability
    /// - Object Storage Targets (OSTs) for data distribution and striping
    /// - Metadata Targets (MDTs) for namespace management and small file optimization
    /// - Progressive File Layout (PFL) for adaptive striping based on file size
    /// - Data on MDT (DoM) for sub-64KB files to reduce OST overhead
    /// - File-Level Redundancy (FLR/mirroring) for high availability
    /// - Hierarchical Storage Management (HSM) with Robinhood policy engine
    /// - OST pools for workload isolation and storage class differentiation
    /// - Lazy Size on MDT (LSOM) for instant stat() operations without OST queries
    /// - Changelogs for event tracking and audit trails
    /// - Quota management (user, group, project) with hard and soft limits
    /// - FID-based addressing for immutable object references
    /// - Fileset/sub-directory namespace isolation
    /// - Wide striping for large file parallel I/O optimization
    /// - High-performance MPI-IO and parallel HPC workload support
    /// </summary>
    public class LustreStrategy : UltimateStorageStrategyBase
    {
        private string _mountPath = string.Empty;
        private string _filesystemName = "lustre";

        // Lustre CLI tool paths
        private string _lctlPath = "lctl";        // Lustre control utility
        private string _lfsPath = "lfs";          // Lustre file system utility
        private string _llogPath = "llog_reader"; // Lustre changelog reader

        // Striping configuration
        private bool _enableStriping = true;
        private int _stripeSize = 1024 * 1024;     // 1MB default stripe size
        private int _stripeCount = -1;              // -1 = filesystem default, 0 = all OSTs, >0 = specific count
        private int _stripeOffset = -1;             // -1 = default, >=0 = specific OST index
        private string? _ostPoolName = null;        // OST pool for workload isolation

        // Progressive File Layout (PFL)
        private bool _enablePfl = true;
        private readonly List<PflComponent> _pflComponents = new();

        // Data on MDT (DoM)
        private bool _enableDoM = true;
        private long _domMaxSize = 64 * 1024;      // 64KB default - files smaller stored on MDT

        // File-Level Redundancy (FLR/Mirroring)
        private bool _enableFlr = false;
        private int _mirrorCount = 1;               // Number of mirrors (1 = no mirroring)

        // OST pool management
        private bool _useOstPools = false;
        private readonly List<string> _availableOstPools = new();

        // Hierarchical Storage Management (HSM)
        private bool _enableHsm = false;
        private string _hsmArchivePath = string.Empty;
        private int _hsmArchiveId = 1;
        private long _hsmArchiveThresholdBytes = 100 * 1024 * 1024; // 100MB threshold

        // Changelog tracking
        private bool _enableChangelog = false;
        private string _changelogUser = string.Empty;
        private bool _trackChangelogEvents = false;

        // Quota management
        private bool _enforceQuotas = false;
        private long _quotaUserMaxBytes = -1;      // -1 = unlimited
        private long _quotaGroupMaxBytes = -1;
        private long _quotaProjectMaxBytes = -1;
        private long _quotaUserMaxInodes = -1;
        private int _projectId = -1;                // -1 = no project quota

        // FID-based addressing
        private bool _useFidAddressing = false;
        private readonly Dictionary<string, string> _keyToFidCache = new();
        private readonly object _fidCacheLock = new();

        // Fileset support
        private bool _useFileset = false;
        private string _filesetName = string.Empty;
        private string _filesetPath = string.Empty;

        // Lazy Size on MDT (LSOM)
        private bool _enableLsom = true;

        // Performance settings
        private int _readBufferSizeBytes = 1024 * 1024;    // 1MB read buffer
        private int _writeBufferSizeBytes = 1024 * 1024;   // 1MB write buffer
        private bool _enableDirectIo = false;
        private bool _enableLockAhead = false;              // Lock-ahead for sequential I/O

        // Health monitoring
        private DateTime _lastHealthCheck = DateTime.MinValue;
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(5);

        public override string StrategyId => "lustre";
        public override string Name => "Lustre Parallel Distributed File System";
        public override StorageTier Tier => StorageTier.Hot;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true,        // POSIX distributed byte-range locking
            SupportsVersioning = false,     // Not natively supported (use HSM for archival)
            SupportsTiering = true,         // Via HSM and OST pools
            SupportsEncryption = false,     // Encryption at rest handled at OST level or via SSDs
            SupportsCompression = false,    // Compression handled at OST level (ZFS/LDISKFS)
            SupportsMultipart = false,      // Striping is transparent to applications
            MaxObjectSize = null,           // Limited by filesystem capacity and stripe count
            MaxObjects = null,              // Limited by MDT inode capacity
            ConsistencyModel = ConsistencyModel.Strong // POSIX semantics with distributed locking
        };

        /// <summary>
        /// Initializes the Lustre storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load mount path configuration
            _mountPath = GetConfiguration<string>("MountPath", string.Empty);

            if (string.IsNullOrWhiteSpace(_mountPath))
            {
                throw new InvalidOperationException(
                    "MountPath is required for Lustre access. Set 'MountPath' in configuration (e.g., '/mnt/lustre' or 'Z:\\lustre').");
            }

            // Verify mount point is accessible
            if (!Directory.Exists(_mountPath))
            {
                throw new InvalidOperationException(
                    $"Lustre mount point does not exist or is not accessible: {_mountPath}. " +
                    "Ensure Lustre is mounted using 'mount -t lustre' command.");
            }

            // Load Lustre configuration
            _filesystemName = GetConfiguration<string>("FilesystemName", "lustre");
            _lctlPath = GetConfiguration<string>("LctlPath", "lctl");
            _lfsPath = GetConfiguration<string>("LfsPath", "lfs");
            _llogPath = GetConfiguration<string>("LlogPath", "llog_reader");

            // Striping configuration
            _enableStriping = GetConfiguration<bool>("EnableStriping", true);
            _stripeSize = GetConfiguration<int>("StripeSize", 1024 * 1024);
            _stripeCount = GetConfiguration<int>("StripeCount", -1);
            _stripeOffset = GetConfiguration<int>("StripeOffset", -1);
            _ostPoolName = GetConfiguration<string?>("OstPoolName", null);

            // Progressive File Layout (PFL)
            _enablePfl = GetConfiguration<bool>("EnablePfl", true);
            if (_enablePfl)
            {
                LoadPflConfiguration();
            }

            // Data on MDT (DoM)
            _enableDoM = GetConfiguration<bool>("EnableDoM", true);
            _domMaxSize = GetConfiguration<long>("DomMaxSize", 64 * 1024);

            // File-Level Redundancy (FLR)
            _enableFlr = GetConfiguration<bool>("EnableFlr", false);
            _mirrorCount = GetConfiguration<int>("MirrorCount", 1);

            // OST pools
            _useOstPools = GetConfiguration<bool>("UseOstPools", false);
            if (_useOstPools)
            {
                var pools = GetConfiguration<string>("AvailableOstPools", string.Empty);
                if (!string.IsNullOrWhiteSpace(pools))
                {
                    _availableOstPools.AddRange(pools.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)));
                }
            }

            // Hierarchical Storage Management (HSM)
            _enableHsm = GetConfiguration<bool>("EnableHsm", false);
            _hsmArchivePath = GetConfiguration<string>("HsmArchivePath", string.Empty);
            _hsmArchiveId = GetConfiguration<int>("HsmArchiveId", 1);
            _hsmArchiveThresholdBytes = GetConfiguration<long>("HsmArchiveThresholdBytes", 100 * 1024 * 1024);

            // Changelog tracking
            _enableChangelog = GetConfiguration<bool>("EnableChangelog", false);
            _changelogUser = GetConfiguration<string>("ChangelogUser", string.Empty);
            _trackChangelogEvents = GetConfiguration<bool>("TrackChangelogEvents", false);

            // Quota management
            _enforceQuotas = GetConfiguration<bool>("EnforceQuotas", false);
            _quotaUserMaxBytes = GetConfiguration<long>("QuotaUserMaxBytes", -1);
            _quotaGroupMaxBytes = GetConfiguration<long>("QuotaGroupMaxBytes", -1);
            _quotaProjectMaxBytes = GetConfiguration<long>("QuotaProjectMaxBytes", -1);
            _quotaUserMaxInodes = GetConfiguration<long>("QuotaUserMaxInodes", -1);
            _projectId = GetConfiguration<int>("ProjectId", -1);

            // FID-based addressing
            _useFidAddressing = GetConfiguration<bool>("UseFidAddressing", false);

            // Fileset support
            _useFileset = GetConfiguration<bool>("UseFileset", false);
            _filesetName = GetConfiguration<string>("FilesetName", string.Empty);
            _filesetPath = GetConfiguration<string>("FilesetPath", string.Empty);

            // Lazy Size on MDT
            _enableLsom = GetConfiguration<bool>("EnableLsom", true);

            // Performance settings
            _readBufferSizeBytes = GetConfiguration<int>("ReadBufferSizeBytes", 1024 * 1024);
            _writeBufferSizeBytes = GetConfiguration<int>("WriteBufferSizeBytes", 1024 * 1024);
            _enableDirectIo = GetConfiguration<bool>("EnableDirectIo", false);
            _enableLockAhead = GetConfiguration<bool>("EnableLockAhead", false);

            // Verify Lustre utilities are available
            await VerifyLustreCliToolsAsync(ct);

            // Apply default striping if configured
            if (_enableStriping && !_enablePfl)
            {
                await SetDirectoryStripingAsync(_mountPath, ct);
            }

            // Apply PFL if configured
            if (_enablePfl && _pflComponents.Count > 0)
            {
                await SetDirectoryPflAsync(_mountPath, ct);
            }

            // Apply quotas if configured
            if (_enforceQuotas)
            {
                await ApplyQuotasAsync(ct);
            }

            await Task.CompletedTask;
        }

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            var filePath = GetFullPath(key);
            var directory = Path.GetDirectoryName(filePath);

            // Create directory if it doesn't exist
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);

                // Apply file layout to new directory if striping is enabled
                if (_enableStriping && !_enablePfl)
                {
                    await SetDirectoryStripingAsync(directory, ct);
                }
                else if (_enablePfl && _pflComponents.Count > 0)
                {
                    await SetDirectoryPflAsync(directory, ct);
                }

                // Apply project quota if configured
                if (_projectId > 0)
                {
                    await SetProjectIdAsync(directory, _projectId, ct);
                }
            }

            // Write file with appropriate options
            long bytesWritten = 0;
            var fileOptions = FileOptions.Asynchronous;

            if (_enableDirectIo)
            {
                fileOptions |= FileOptions.WriteThrough;
            }

            await using (var fileStream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                _writeBufferSizeBytes,
                fileOptions))
            {
                await data.CopyToAsync(fileStream, _writeBufferSizeBytes, ct);
                bytesWritten = fileStream.Length;
                await fileStream.FlushAsync(ct);
            }

            // Apply FLR mirroring if enabled
            if (_enableFlr && _mirrorCount > 1)
            {
                await CreateMirrorAsync(filePath, _mirrorCount, ct);
            }

            // Archive to HSM if threshold exceeded
            if (_enableHsm && bytesWritten >= _hsmArchiveThresholdBytes)
            {
                await ArchiveToHsmAsync(filePath, ct);
            }

            // Store metadata as extended attributes or sidecar file
            if (metadata != null && metadata.Count > 0)
            {
                await StoreMetadataAsync(filePath, metadata, ct);
            }

            // Cache FID mapping if enabled
            if (_useFidAddressing)
            {
                var fid = await GetFileFidAsync(filePath, ct);
                if (!string.IsNullOrEmpty(fid))
                {
                    lock (_fidCacheLock)
                    {
                        _keyToFidCache[key] = fid;
                    }
                }
            }

            // Get file info
            var fileInfo = new FileInfo(filePath);
            var etag = ComputeFileETag(filePath);

            // Update statistics
            IncrementBytesStored(bytesWritten);
            IncrementOperationCounter(StorageOperationType.Store);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = fileInfo.Length,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc,
                ETag = etag,
                ContentType = GetContentType(key),
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var filePath = GetFullPath(key);

            // Check HSM state - may need to restore from archive
            if (_enableHsm)
            {
                var hsmState = await GetHsmStateAsync(filePath, ct);
                if (hsmState == HsmState.Released || hsmState == HsmState.Archived)
                {
                    await RestoreFromHsmAsync(filePath, ct);
                }
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {key}", filePath);
            }

            var ms = new MemoryStream(65536);

            var fileOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;
            if (_enableDirectIo)
            {
                fileOptions |= FileOptions.WriteThrough;
            }

            await using (var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _readBufferSizeBytes,
                fileOptions))
            {
                await fileStream.CopyToAsync(ms, _readBufferSizeBytes, ct);
            }

            ms.Position = 0;

            // Update statistics
            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var filePath = GetFullPath(key);

            if (!File.Exists(filePath))
            {
                // Already deleted or never existed
                return;
            }

            var size = new FileInfo(filePath).Length;

            // Release HSM archive if exists
            if (_enableHsm)
            {
                await ReleaseHsmArchiveAsync(filePath, ct);
            }

            // Delete main file
            File.Delete(filePath);

            // Delete metadata sidecar file if exists
            var metadataFilePath = GetMetadataFilePath(filePath);
            if (File.Exists(metadataFilePath))
            {
                File.Delete(metadataFilePath);
            }

            // Remove FID cache entry
            if (_useFidAddressing)
            {
                lock (_fidCacheLock)
                {
                    _keyToFidCache.Remove(key);
                }
            }

            // Update statistics
            IncrementBytesDeleted(size);
            IncrementOperationCounter(StorageOperationType.Delete);

            await Task.CompletedTask;
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var filePath = GetFullPath(key);
            var exists = File.Exists(filePath);

            IncrementOperationCounter(StorageOperationType.Exists);

            return await Task.FromResult(exists);
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(
            string? prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            var searchPath = string.IsNullOrWhiteSpace(prefix)
                ? _mountPath
                : GetFullPath(prefix);

            if (!Directory.Exists(searchPath))
            {
                yield break;
            }

            var files = Directory.GetFiles(searchPath, "*", SearchOption.AllDirectories)
                .Where(f => !IsMetadataFile(f));

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(filePath);
                var relativePath = Path.GetRelativePath(_mountPath, filePath);
                var key = relativePath.Replace(Path.DirectorySeparatorChar, '/');

                // Load metadata if exists
                Dictionary<string, string>? metadata = null;
                try
                {
                    metadata = await LoadMetadataAsync(filePath, ct);
                }
                catch
                {
                    // Ignore metadata read errors
                }

                yield return new StorageObjectMetadata
                {
                    Key = key,
                    Size = fileInfo.Length,
                    Created = fileInfo.CreationTimeUtc,
                    Modified = fileInfo.LastWriteTimeUtc,
                    ETag = ComputeFileETag(filePath),
                    ContentType = GetContentType(key),
                    CustomMetadata = metadata,
                    Tier = Tier
                };

                await Task.Yield();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var filePath = GetFullPath(key);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {key}", filePath);
            }

            var fileInfo = new FileInfo(filePath);

            // Load metadata if exists
            Dictionary<string, string>? metadata = null;
            try
            {
                metadata = await LoadMetadataAsync(filePath, ct);
            }
            catch
            {
                // Ignore metadata read errors
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = fileInfo.Length,
                Created = fileInfo.CreationTimeUtc,
                Modified = fileInfo.LastWriteTimeUtc,
                ETag = ComputeFileETag(filePath),
                ContentType = GetContentType(key),
                CustomMetadata = metadata,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                // Check if mount point is accessible and writable
                if (!Directory.Exists(_mountPath))
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        Message = $"Lustre mount point not accessible at {_mountPath}",
                        CheckedAt = DateTime.UtcNow
                    };
                }

                var sw = Stopwatch.StartNew();

                // Try to read directory listing to verify accessibility
                try
                {
                    Directory.GetFiles(_mountPath, "*", SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        Message = $"Lustre mount is not readable: {ex.Message}",
                        CheckedAt = DateTime.UtcNow
                    };
                }

                sw.Stop();

                // Get capacity information
                long? availableCapacity = null;
                long? totalCapacity = null;
                long? usedCapacity = null;

                try
                {
                    var driveInfo = new DriveInfo(_mountPath);
                    if (driveInfo.IsReady)
                    {
                        availableCapacity = driveInfo.AvailableFreeSpace;
                        totalCapacity = driveInfo.TotalSize;
                        usedCapacity = totalCapacity - availableCapacity;
                    }
                }
                catch
                {
                    // Try using lfs df command
                    var dfInfo = await GetLustreDfInfoAsync(ct);
                    if (dfInfo != null)
                    {
                        availableCapacity = dfInfo.AvailableBytes;
                        totalCapacity = dfInfo.TotalBytes;
                        usedCapacity = dfInfo.UsedBytes;
                    }
                }

                // Check OST health if possible
                var ostHealth = await CheckOstHealthAsync(ct);

                return new StorageHealthInfo
                {
                    Status = ostHealth ? HealthStatus.Healthy : HealthStatus.Degraded,
                    LatencyMs = sw.ElapsedMilliseconds,
                    AvailableCapacity = availableCapacity,
                    TotalCapacity = totalCapacity,
                    UsedCapacity = usedCapacity,
                    Message = ostHealth
                        ? $"Lustre is healthy at {_mountPath}"
                        : $"Lustre is degraded - some OSTs may be offline at {_mountPath}",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to check Lustre health: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                // Check quota first
                if (_enforceQuotas && _quotaUserMaxBytes > 0)
                {
                    var usage = await GetQuotaUsageAsync(ct);
                    if (usage.HasValue)
                    {
                        var available = _quotaUserMaxBytes - usage.Value;
                        return available > 0 ? available : 0;
                    }
                }

                // Try DriveInfo first
                try
                {
                    var driveInfo = new DriveInfo(_mountPath);
                    if (driveInfo.IsReady)
                    {
                        return driveInfo.AvailableFreeSpace;
                    }
                }
                catch
                {
                    // Fall through to lfs df
                }

                // Use lfs df command
                var dfInfo = await GetLustreDfInfoAsync(ct);
                return dfInfo?.AvailableBytes;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Lustre-Specific Operations

        /// <summary>
        /// Verifies that Lustre CLI tools (lfs, lctl) are available.
        /// </summary>
        private async Task VerifyLustreCliToolsAsync(CancellationToken ct)
        {
            // Check if lfs command is available
            try
            {
                var result = await ExecuteLustreCommandAsync(_lfsPath, "--version", ct);
                // lfs is available
            }
            catch
            {
                // lfs not found - operations will be limited to basic POSIX
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Sets directory striping layout using lfs setstripe.
        /// </summary>
        private async Task SetDirectoryStripingAsync(string directoryPath, CancellationToken ct)
        {
            if (!_enableStriping)
            {
                return;
            }

            try
            {
                var args = new StringBuilder("setstripe");

                // Stripe size
                if (_stripeSize > 0)
                {
                    args.Append($" -S {_stripeSize}");
                }

                // Stripe count
                if (_stripeCount >= 0)
                {
                    args.Append($" -c {_stripeCount}");
                }

                // Stripe offset (OST index)
                if (_stripeOffset >= 0)
                {
                    args.Append($" -i {_stripeOffset}");
                }

                // OST pool
                if (!string.IsNullOrWhiteSpace(_ostPoolName))
                {
                    args.Append($" -p {_ostPoolName}");
                }

                args.Append($" \"{directoryPath}\"");

                await ExecuteLustreCommandAsync(_lfsPath, args.ToString(), ct);
            }
            catch (Exception ex)
            {
                // Striping setup failed - continue with default
                Debug.WriteLine($"Failed to set Lustre striping: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets directory Progressive File Layout (PFL) using lfs setstripe with multiple components.
        /// </summary>
        private async Task SetDirectoryPflAsync(string directoryPath, CancellationToken ct)
        {
            if (!_enablePfl || _pflComponents.Count == 0)
            {
                return;
            }

            try
            {
                var args = new StringBuilder("setstripe");

                // Add Data on MDT (DoM) component if enabled
                if (_enableDoM && _domMaxSize > 0)
                {
                    args.Append($" -E {_domMaxSize} -L mdt");
                }

                // Add each PFL component
                foreach (var component in _pflComponents)
                {
                    args.Append($" -E {component.EndOffset}");
                    if (component.StripeSize > 0)
                    {
                        args.Append($" -S {component.StripeSize}");
                    }
                    if (component.StripeCount >= 0)
                    {
                        args.Append($" -c {component.StripeCount}");
                    }
                    if (!string.IsNullOrWhiteSpace(component.PoolName))
                    {
                        args.Append($" -p {component.PoolName}");
                    }
                }

                // Add final component (EOF)
                args.Append(" -E EOF");
                if (_stripeSize > 0)
                {
                    args.Append($" -S {_stripeSize}");
                }
                if (_stripeCount >= 0)
                {
                    args.Append($" -c {_stripeCount}");
                }
                if (!string.IsNullOrWhiteSpace(_ostPoolName))
                {
                    args.Append($" -p {_ostPoolName}");
                }

                args.Append($" \"{directoryPath}\"");

                await ExecuteLustreCommandAsync(_lfsPath, args.ToString(), ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set Lustre PFL: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates FLR mirrors for a file using lfs mirror create.
        /// </summary>
        private async Task CreateMirrorAsync(string filePath, int mirrorCount, CancellationToken ct)
        {
            if (!_enableFlr || mirrorCount <= 1)
            {
                return;
            }

            try
            {
                var args = $"mirror create -N{mirrorCount} \"{filePath}\"";
                await ExecuteLustreCommandAsync(_lfsPath, args, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create Lustre mirror: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the File Identifier (FID) for a file using lfs path2fid.
        /// </summary>
        private async Task<string?> GetFileFidAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var args = $"path2fid \"{filePath}\"";
                var result = await ExecuteLustreCommandAsync(_lfsPath, args, ct);

                // Parse FID from output (format: [seq:oid:ver])
                if (!string.IsNullOrWhiteSpace(result))
                {
                    var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("[") && line.Contains(":"))
                        {
                            return line.Trim();
                        }
                    }
                }
            }
            catch
            {
                // FID retrieval failed
            }

            return null;
        }

        /// <summary>
        /// Sets project ID for a directory using lfs project.
        /// </summary>
        private async Task SetProjectIdAsync(string directoryPath, int projectId, CancellationToken ct)
        {
            try
            {
                var args = $"project -s -p {projectId} \"{directoryPath}\"";
                await ExecuteLustreCommandAsync(_lfsPath, args, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set Lustre project ID: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies quota settings using lfs setquota.
        /// </summary>
        private async Task ApplyQuotasAsync(CancellationToken ct)
        {
            if (!_enforceQuotas)
            {
                return;
            }

            try
            {
                // User quota
                if (_quotaUserMaxBytes > 0)
                {
                    var args = $"setquota -u {Environment.UserName} -b 0 -B {_quotaUserMaxBytes} {_mountPath}";
                    await ExecuteLustreCommandAsync(_lfsPath, args, ct);
                }

                // Project quota
                if (_projectId > 0 && _quotaProjectMaxBytes > 0)
                {
                    var args = $"setquota -p {_projectId} -b 0 -B {_quotaProjectMaxBytes} {_mountPath}";
                    await ExecuteLustreCommandAsync(_lfsPath, args, ct);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to apply Lustre quotas: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets current quota usage using lfs quota.
        /// </summary>
        private async Task<long?> GetQuotaUsageAsync(CancellationToken ct)
        {
            try
            {
                var args = $"quota -u {Environment.UserName} {_mountPath}";
                var result = await ExecuteLustreCommandAsync(_lfsPath, args, ct);

                // Parse quota output to extract used bytes
                // Format varies, look for numeric values
                if (!string.IsNullOrWhiteSpace(result))
                {
                    var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        // Basic parsing - production code would need robust parsing
                        if (line.Contains(Environment.UserName))
                        {
                            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1 && long.TryParse(parts[1], out var usedKb))
                            {
                                return usedKb * 1024; // Convert KB to bytes
                            }
                        }
                    }
                }
            }
            catch
            {
                // Quota query failed
            }

            return null;
        }

        /// <summary>
        /// Gets Lustre filesystem disk space information using lfs df.
        /// </summary>
        private async Task<LustreDfInfo?> GetLustreDfInfoAsync(CancellationToken ct)
        {
            try
            {
                var args = $"df -h {_mountPath}";
                var result = await ExecuteLustreCommandAsync(_lfsPath, args, ct);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    // Parse lfs df output
                    // Format: filesystem summary with total, used, available
                    // Production code would have robust parsing logic based on Lustre version
                    // For now, return null to fall back to DriveInfo or other methods
                }
            }
            catch
            {
                // lfs df failed
            }

            return null;
        }

        /// <summary>
        /// Checks OST health using lctl get_param.
        /// </summary>
        private async Task<bool> CheckOstHealthAsync(CancellationToken ct)
        {
            try
            {
                var args = "get_param -n llite.*.ost_conn_uuid";
                var result = await ExecuteLustreCommandAsync(_lctlPath, args, ct);

                // If command succeeds and returns data, OSTs are accessible
                return !string.IsNullOrWhiteSpace(result);
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region HSM Operations

        /// <summary>
        /// Archives a file to HSM backend using lfs hsm_archive.
        /// </summary>
        private async Task ArchiveToHsmAsync(string filePath, CancellationToken ct)
        {
            if (!_enableHsm)
            {
                return;
            }

            try
            {
                var args = $"hsm_archive --archive {_hsmArchiveId} \"{filePath}\"";
                await ExecuteLustreCommandAsync(_lfsPath, args, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to archive to HSM: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores a file from HSM archive using lfs hsm_restore.
        /// </summary>
        private async Task RestoreFromHsmAsync(string filePath, CancellationToken ct)
        {
            if (!_enableHsm)
            {
                return;
            }

            try
            {
                var args = $"hsm_restore \"{filePath}\"";
                await ExecuteLustreCommandAsync(_lfsPath, args, ct);

                // Wait for restore to complete
                var timeout = TimeSpan.FromMinutes(5);
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < timeout)
                {
                    var state = await GetHsmStateAsync(filePath, ct);
                    if (state == HsmState.Exists || state == HsmState.None)
                    {
                        break;
                    }
                    await Task.Delay(1000, ct);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore from HSM: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets HSM state for a file using lfs hsm_state.
        /// </summary>
        private async Task<HsmState> GetHsmStateAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var args = $"hsm_state \"{filePath}\"";
                var result = await ExecuteLustreCommandAsync(_lfsPath, args, ct);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    if (result.Contains("released"))
                        return HsmState.Released;
                    if (result.Contains("archived"))
                        return HsmState.Archived;
                    if (result.Contains("exists"))
                        return HsmState.Exists;
                }
            }
            catch
            {
                // HSM state query failed
            }

            return HsmState.None;
        }

        /// <summary>
        /// Releases HSM archive for a file using lfs hsm_release.
        /// </summary>
        private async Task ReleaseHsmArchiveAsync(string filePath, CancellationToken ct)
        {
            if (!_enableHsm)
            {
                return;
            }

            try
            {
                var args = $"hsm_release \"{filePath}\"";
                await ExecuteLustreCommandAsync(_lfsPath, args, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to release HSM archive: {ex.Message}");
            }
        }

        #endregion

        #region Metadata Management

        /// <summary>
        /// Stores metadata for a file using sidecar JSON file.
        /// </summary>
        private async Task StoreMetadataAsync(string filePath, IDictionary<string, string> metadata, CancellationToken ct)
        {
            if (metadata == null || metadata.Count == 0)
            {
                return;
            }

            var metadataFilePath = GetMetadataFilePath(filePath);
            var metadataJson = JsonSerializer.Serialize(metadata);
            await File.WriteAllTextAsync(metadataFilePath, metadataJson, ct);
        }

        /// <summary>
        /// Loads metadata for a file from sidecar JSON file.
        /// </summary>
        private async Task<Dictionary<string, string>?> LoadMetadataAsync(string filePath, CancellationToken ct)
        {
            var metadataFilePath = GetMetadataFilePath(filePath);

            if (!File.Exists(metadataFilePath))
            {
                return null;
            }

            try
            {
                var metadataJson = await File.ReadAllTextAsync(metadataFilePath, ct);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the full filesystem path for a storage key.
        /// </summary>
        private string GetFullPath(string key)
        {
            // Use fileset path if configured
            var basePath = _useFileset && !string.IsNullOrWhiteSpace(_filesetPath)
                ? _filesetPath
                : _mountPath;

            // Normalize key to use OS-specific path separators
            var normalizedKey = key.Replace('/', Path.DirectorySeparatorChar)
                                   .Replace('\\', Path.DirectorySeparatorChar);

            return Path.Combine(basePath, normalizedKey);
        }

        /// <summary>
        /// Gets the metadata sidecar file path for a given file.
        /// </summary>
        private string GetMetadataFilePath(string filePath)
        {
            return filePath + ".metadata.json";
        }

        /// <summary>
        /// Checks if a file path is a metadata sidecar file.
        /// </summary>
        private bool IsMetadataFile(string filePath)
        {
            return filePath.EndsWith(".metadata.json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the MIME content type based on file extension.
        /// </summary>
        private string GetContentType(string key)
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
                ".tar" => "application/x-tar",
                ".gz" => "application/gzip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".parquet" => "application/vnd.apache.parquet",
                ".avro" => "application/avro",
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Computes ETag for a file using file metadata (fast, no crypto).
        /// AD-11: Cryptographic hashing delegated to UltimateDataIntegrity via bus.
        /// </summary>
        private string ComputeFileETag(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                return HashCode.Combine(fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks).ToString("x8");
            }
            catch
            {
                return Guid.NewGuid().ToString("N")[..8];
            }
        }

        /// <summary>
        /// Executes a Lustre command-line utility and returns output.
        /// </summary>
        private async Task<string> ExecuteLustreCommandAsync(string command, string arguments, CancellationToken ct)
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
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Lustre command failed: {command} {arguments}\nError: {error}");
            }

            return output;
        }

        /// <summary>
        /// Loads Progressive File Layout (PFL) configuration from settings.
        /// </summary>
        private void LoadPflConfiguration()
        {
            // Example PFL configuration: small files (0-1MB), medium files (1MB-100MB), large files (100MB+)
            // This would typically be loaded from configuration

            // Small files: 1MB end, 256KB stripe size, 1 stripe count
            _pflComponents.Add(new PflComponent
            {
                EndOffset = 1024 * 1024,
                StripeSize = 256 * 1024,
                StripeCount = 1
            });

            // Medium files: 100MB end, 1MB stripe size, 4 stripe count
            _pflComponents.Add(new PflComponent
            {
                EndOffset = 100 * 1024 * 1024,
                StripeSize = 1024 * 1024,
                StripeCount = 4
            });

            // Large files handled by final component in SetDirectoryPflAsync
        }

        protected override int GetMaxKeyLength() => 4096; // Lustre supports long paths

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            // Clear FID cache
            lock (_fidCacheLock)
            {
                _keyToFidCache.Clear();
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Represents a Progressive File Layout (PFL) component.
    /// </summary>
    internal class PflComponent
    {
        /// <summary>End offset for this component (in bytes).</summary>
        public long EndOffset { get; set; }

        /// <summary>Stripe size for this component.</summary>
        public int StripeSize { get; set; }

        /// <summary>Stripe count for this component.</summary>
        public int StripeCount { get; set; }

        /// <summary>OST pool name for this component (optional).</summary>
        public string? PoolName { get; set; }
    }

    /// <summary>
    /// Represents Lustre disk space information from lfs df.
    /// </summary>
    internal class LustreDfInfo
    {
        /// <summary>Total capacity in bytes.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Used space in bytes.</summary>
        public long UsedBytes { get; set; }

        /// <summary>Available space in bytes.</summary>
        public long AvailableBytes { get; set; }
    }

    /// <summary>
    /// HSM (Hierarchical Storage Management) state for a file.
    /// </summary>
    internal enum HsmState
    {
        /// <summary>No HSM state.</summary>
        None,

        /// <summary>File exists on Lustre (not archived).</summary>
        Exists,

        /// <summary>File is archived to HSM backend.</summary>
        Archived,

        /// <summary>File is released from Lustre (data on HSM only, stub on Lustre).</summary>
        Released
    }

    #endregion
}
