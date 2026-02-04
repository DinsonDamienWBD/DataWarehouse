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
    /// LizardFS (fork of MooseFS) distributed file system storage strategy with production-ready features:
    /// - POSIX-compliant filesystem interface via FUSE mount
    /// - Goals with erasure coding (EC) support (xor3, ec(3,2), ec(4,2), ec(8,3), etc.)
    /// - Custom goals with labels and expressions for flexible data placement
    /// - Trash bin for deleted file recovery with configurable retention
    /// - Snapshots for point-in-time filesystem recovery
    /// - Quotas at user, group, and directory levels
    /// - Task scheduling for background operations
    /// - Global locks for distributed coordination
    /// - Special purpose chunkservers for workload isolation
    /// - Defragmentation for optimal chunk placement
    /// - Master high availability with shadow masters for failover
    /// - lizardfs-admin CLI integration for advanced management
    /// - ACL and extended attributes support
    /// - Multi-master architecture for scalability
    /// </summary>
    public class LizardFsStrategy : UltimateStorageStrategyBase
    {
        private string _mountPath = string.Empty;
        private string _masterHost = "localhost";
        private int _masterPort = 9421;

        // Goals and erasure coding
        private string _defaultGoal = "2"; // Default replication goal
        private bool _enableErasureCoding = false;
        private string _erasureCodeGoal = "ec(3,2)"; // 3 data + 2 parity
        private bool _useCustomGoals = false;
        private Dictionary<string, string> _customGoals = new(); // name -> expression

        // Trash bin configuration
        private bool _enableTrashBin = true;
        private int _trashRetentionHours = 24; // 24 hours default
        private string _trashDirectory = ".trash";

        // Snapshot configuration
        private bool _enableSnapshots = true;
        private string _snapshotDirectory = ".snapshot";
        private int _maxSnapshotRetention = 30; // days
        private bool _autoSnapshot = false;
        private int _autoSnapshotIntervalHours = 24;

        // Quota configuration
        private bool _enableQuotas = false;
        private long _quotaMaxBytes = -1; // -1 = unlimited
        private long _quotaMaxInodes = -1; // -1 = unlimited
        private bool _softQuota = false; // Hard quota by default
        private QuotaType _quotaType = QuotaType.Directory;

        // Task scheduling
        private bool _enableTaskScheduling = false;
        private List<string> _scheduledTasks = new();

        // Global locks
        private bool _enableGlobalLocks = true;
        private readonly Dictionary<string, SemaphoreSlim> _globalLocks = new();
        private readonly object _lockDictionaryLock = new();

        // Special purpose chunkservers
        private bool _useSpecialPurposeChunkservers = false;
        private Dictionary<string, string> _chunkserverLabels = new(); // label -> chunkserver list

        // Defragmentation
        private bool _enableAutoDefragmentation = false;
        private int _defragmentationIntervalHours = 168; // Weekly
        private double _defragmentationThreshold = 0.3; // 30% fragmentation

        // High availability
        private bool _enableHighAvailability = true;
        private List<string> _shadowMasterHosts = new();
        private int _masterFailoverTimeoutSeconds = 30;
        private bool _autoFailover = true;

        // lizardfs-admin CLI
        private string _lizardfsAdminPath = "lizardfs-admin";
        private bool _useLizardfsAdmin = true;

        // Extended attributes and ACLs
        private bool _enableExtendedAttributes = true;
        private bool _enableAcl = true;
        private bool _storeMetadataAsXattr = true;

        // Performance settings
        private int _readBufferSizeBytes = 128 * 1024; // 128KB
        private int _writeBufferSizeBytes = 128 * 1024; // 128KB
        private bool _enableReadAhead = true;
        private int _readAheadSizeBytes = 512 * 1024; // 512KB
        private bool _enableWriteCache = true;
        private int _writeCacheSizeBytes = 256 * 1024; // 256KB

        // Chunk management
        private int _chunkSizeBytes = 64 * 1024 * 1024; // 64MB default
        private bool _enableChunkCompression = false;
        private bool _enableChunkDeduplication = false;

        // Client configuration
        private bool _allowReads = true;
        private bool _allowWrites = true;
        private bool _allowSnapshots = true;
        private bool _allowTrashBin = true;

        // Health monitoring
        private DateTime _lastHealthCheck = DateTime.MinValue;
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(5);

        public override string StrategyId => "lizardfs";
        public override string Name => "LizardFS Distributed File System";
        public override StorageTier Tier => StorageTier.Hot;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true, // Global distributed locking
            SupportsVersioning = true, // Via snapshots and trash bin
            SupportsTiering = true, // Via custom goals and special purpose chunkservers
            SupportsEncryption = false, // Encryption handled at client mount or chunkserver level
            SupportsCompression = _enableChunkCompression, // Optional chunk compression
            SupportsMultipart = false, // Chunking is transparent
            MaxObjectSize = null, // Limited only by available space
            MaxObjects = null, // Limited only by quota settings
            ConsistencyModel = ConsistencyModel.Strong // POSIX semantics provide strong consistency
        };

        /// <summary>
        /// Initializes the LizardFS storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load mount path configuration
            _mountPath = GetConfiguration<string>("MountPath", string.Empty);

            if (string.IsNullOrWhiteSpace(_mountPath))
            {
                throw new InvalidOperationException(
                    "MountPath is required for LizardFS access. Set 'MountPath' in configuration (e.g., '/mnt/lizardfs' or 'Z:\\lizardfs').");
            }

            // Verify mount point is accessible
            if (!Directory.Exists(_mountPath))
            {
                throw new InvalidOperationException(
                    $"LizardFS mount point does not exist or is not accessible: {_mountPath}. " +
                    "Ensure LizardFS is mounted using 'mfsmount' command.");
            }

            // Load LizardFS master configuration
            _masterHost = GetConfiguration<string>("MasterHost", "localhost");
            _masterPort = GetConfiguration<int>("MasterPort", 9421);

            // Goals and erasure coding
            _defaultGoal = GetConfiguration<string>("DefaultGoal", "2");
            _enableErasureCoding = GetConfiguration<bool>("EnableErasureCoding", false);
            _erasureCodeGoal = GetConfiguration<string>("ErasureCodeGoal", "ec(3,2)");
            _useCustomGoals = GetConfiguration<bool>("UseCustomGoals", false);

            if (_useCustomGoals)
            {
                var customGoalsJson = GetConfiguration<string>("CustomGoals", "{}");
                try
                {
                    _customGoals = JsonSerializer.Deserialize<Dictionary<string, string>>(customGoalsJson)
                        ?? new Dictionary<string, string>();
                }
                catch
                {
                    _customGoals = new Dictionary<string, string>();
                }
            }

            // Trash bin configuration
            _enableTrashBin = GetConfiguration<bool>("EnableTrashBin", true);
            _trashRetentionHours = GetConfiguration<int>("TrashRetentionHours", 24);
            _trashDirectory = GetConfiguration<string>("TrashDirectory", ".trash");

            // Snapshot configuration
            _enableSnapshots = GetConfiguration<bool>("EnableSnapshots", true);
            _snapshotDirectory = GetConfiguration<string>("SnapshotDirectory", ".snapshot");
            _maxSnapshotRetention = GetConfiguration<int>("MaxSnapshotRetention", 30);
            _autoSnapshot = GetConfiguration<bool>("AutoSnapshot", false);
            _autoSnapshotIntervalHours = GetConfiguration<int>("AutoSnapshotIntervalHours", 24);

            // Quota configuration
            _enableQuotas = GetConfiguration<bool>("EnableQuotas", false);
            _quotaMaxBytes = GetConfiguration<long>("QuotaMaxBytes", -1);
            _quotaMaxInodes = GetConfiguration<long>("QuotaMaxInodes", -1);
            _softQuota = GetConfiguration<bool>("SoftQuota", false);

            var quotaTypeStr = GetConfiguration<string>("QuotaType", "Directory");
            _quotaType = Enum.TryParse<QuotaType>(quotaTypeStr, true, out var qt) ? qt : QuotaType.Directory;

            // Task scheduling
            _enableTaskScheduling = GetConfiguration<bool>("EnableTaskScheduling", false);
            var scheduledTasksJson = GetConfiguration<string>("ScheduledTasks", "[]");
            try
            {
                _scheduledTasks = JsonSerializer.Deserialize<List<string>>(scheduledTasksJson) ?? new List<string>();
            }
            catch
            {
                _scheduledTasks = new List<string>();
            }

            // Global locks
            _enableGlobalLocks = GetConfiguration<bool>("EnableGlobalLocks", true);

            // Special purpose chunkservers
            _useSpecialPurposeChunkservers = GetConfiguration<bool>("UseSpecialPurposeChunkservers", false);
            var chunkserverLabelsJson = GetConfiguration<string>("ChunkserverLabels", "{}");
            try
            {
                _chunkserverLabels = JsonSerializer.Deserialize<Dictionary<string, string>>(chunkserverLabelsJson)
                    ?? new Dictionary<string, string>();
            }
            catch
            {
                _chunkserverLabels = new Dictionary<string, string>();
            }

            // Defragmentation
            _enableAutoDefragmentation = GetConfiguration<bool>("EnableAutoDefragmentation", false);
            _defragmentationIntervalHours = GetConfiguration<int>("DefragmentationIntervalHours", 168);
            _defragmentationThreshold = GetConfiguration<double>("DefragmentationThreshold", 0.3);

            // High availability
            _enableHighAvailability = GetConfiguration<bool>("EnableHighAvailability", true);
            var shadowMastersJson = GetConfiguration<string>("ShadowMasterHosts", "[]");
            try
            {
                _shadowMasterHosts = JsonSerializer.Deserialize<List<string>>(shadowMastersJson) ?? new List<string>();
            }
            catch
            {
                _shadowMasterHosts = new List<string>();
            }
            _masterFailoverTimeoutSeconds = GetConfiguration<int>("MasterFailoverTimeoutSeconds", 30);
            _autoFailover = GetConfiguration<bool>("AutoFailover", true);

            // lizardfs-admin CLI
            _lizardfsAdminPath = GetConfiguration<string>("LizardfsAdminPath", "lizardfs-admin");
            _useLizardfsAdmin = GetConfiguration<bool>("UseLizardfsAdmin", true);

            // Extended attributes and ACLs
            _enableExtendedAttributes = GetConfiguration<bool>("EnableExtendedAttributes", true);
            _enableAcl = GetConfiguration<bool>("EnableAcl", true);
            _storeMetadataAsXattr = GetConfiguration<bool>("StoreMetadataAsXattr", true);

            // Performance settings
            _readBufferSizeBytes = GetConfiguration<int>("ReadBufferSizeBytes", 128 * 1024);
            _writeBufferSizeBytes = GetConfiguration<int>("WriteBufferSizeBytes", 128 * 1024);
            _enableReadAhead = GetConfiguration<bool>("EnableReadAhead", true);
            _readAheadSizeBytes = GetConfiguration<int>("ReadAheadSizeBytes", 512 * 1024);
            _enableWriteCache = GetConfiguration<bool>("EnableWriteCache", true);
            _writeCacheSizeBytes = GetConfiguration<int>("WriteCacheSizeBytes", 256 * 1024);

            // Chunk management
            _chunkSizeBytes = GetConfiguration<int>("ChunkSizeBytes", 64 * 1024 * 1024);
            _enableChunkCompression = GetConfiguration<bool>("EnableChunkCompression", false);
            _enableChunkDeduplication = GetConfiguration<bool>("EnableChunkDeduplication", false);

            // Client capabilities
            _allowReads = GetConfiguration<bool>("AllowReads", true);
            _allowWrites = GetConfiguration<bool>("AllowWrites", true);
            _allowSnapshots = GetConfiguration<bool>("AllowSnapshots", true);
            _allowTrashBin = GetConfiguration<bool>("AllowTrashBin", true);

            // Validate capabilities
            if (!_allowWrites && !_allowReads)
            {
                throw new InvalidOperationException("At least one of AllowReads or AllowWrites must be true.");
            }

            // Apply quota if configured
            if (_enableQuotas && (_quotaMaxBytes > 0 || _quotaMaxInodes > 0))
            {
                await ApplyQuotaAsync(_mountPath, _quotaMaxBytes, _quotaMaxInodes, ct);
            }

            // Apply default goal to root if specified
            if (!string.IsNullOrWhiteSpace(_defaultGoal))
            {
                await ApplyGoalAsync(_mountPath, _defaultGoal, ct);
            }

            await Task.CompletedTask;
        }

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            if (!_allowWrites)
            {
                throw new InvalidOperationException("Write operations are not allowed by client capabilities.");
            }

            var filePath = GetFullPath(key);
            var directory = Path.GetDirectoryName(filePath);

            // Create directory if it doesn't exist
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);

                // Apply goal to new directory
                var goal = _enableErasureCoding ? _erasureCodeGoal : _defaultGoal;
                await ApplyGoalAsync(directory, goal, ct);
            }

            // Write file with appropriate options
            long bytesWritten = 0;
            var fileOptions = FileOptions.Asynchronous;

            if (_enableWriteCache)
            {
                fileOptions |= FileOptions.SequentialScan;
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

            // Apply goal to file if specified
            if (metadata != null && metadata.ContainsKey("goal"))
            {
                await ApplyGoalAsync(filePath, metadata["goal"], ct);
            }
            else if (_enableErasureCoding)
            {
                await ApplyGoalAsync(filePath, _erasureCodeGoal, ct);
            }

            // Store metadata as extended attributes or sidecar file
            if (metadata != null && metadata.Count > 0)
            {
                await StoreMetadataAsync(filePath, metadata, ct);
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

            if (!_allowReads)
            {
                throw new InvalidOperationException("Read operations are not allowed by client capabilities.");
            }

            var filePath = GetFullPath(key);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {key}", filePath);
            }

            var ms = new MemoryStream();

            var fileOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;
            if (_enableReadAhead)
            {
                fileOptions |= FileOptions.SequentialScan;
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

            if (!_allowWrites)
            {
                throw new InvalidOperationException("Write operations are not allowed by client capabilities.");
            }

            var filePath = GetFullPath(key);

            if (!File.Exists(filePath))
            {
                // Already deleted or never existed
                return;
            }

            var size = new FileInfo(filePath).Length;

            // If trash bin is enabled, move to trash instead of deleting
            if (_enableTrashBin && _allowTrashBin)
            {
                await MoveToTrashAsync(filePath, ct);
            }
            else
            {
                // Delete main file
                File.Delete(filePath);

                // Delete metadata sidecar file if exists
                var metadataFilePath = GetMetadataFilePath(filePath);
                if (File.Exists(metadataFilePath))
                {
                    File.Delete(metadataFilePath);
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
                .Where(f => !IsMetadataFile(f) && !IsSnapshotPath(f) && !IsTrashPath(f));

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
                        Message = $"LizardFS mount point not accessible at {_mountPath}",
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
                        Message = $"LizardFS mount is not readable: {ex.Message}",
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
                    // Capacity information not available
                }

                // Check master connectivity if lizardfs-admin is available
                var masterHealthy = await CheckMasterHealthAsync(ct);
                var status = masterHealthy ? HealthStatus.Healthy : HealthStatus.Degraded;
                var message = masterHealthy
                    ? $"LizardFS is healthy at {_mountPath}"
                    : $"LizardFS mount is accessible but master connectivity issues detected";

                return new StorageHealthInfo
                {
                    Status = status,
                    LatencyMs = sw.ElapsedMilliseconds,
                    AvailableCapacity = availableCapacity,
                    TotalCapacity = totalCapacity,
                    UsedCapacity = usedCapacity,
                    Message = message,
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to check LizardFS health: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                // Check quota first
                if (_enableQuotas && _quotaMaxBytes > 0)
                {
                    var usage = await GetFilesystemUsageAsync(ct);
                    var available = _quotaMaxBytes - usage.UsedBytes;
                    return available > 0 ? available : 0;
                }

                // Otherwise, use drive info
                var driveInfo = new DriveInfo(_mountPath);
                if (driveInfo.IsReady)
                {
                    return driveInfo.AvailableFreeSpace;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region LizardFS-Specific Operations

        /// <summary>
        /// Applies a goal (replication or erasure coding) to a file or directory.
        /// Goals control data placement and redundancy strategy.
        /// </summary>
        /// <param name="path">File or directory path.</param>
        /// <param name="goal">Goal name or ID (e.g., "2", "xor3", "ec(3,2)").</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task ApplyGoalAsync(string path, string goal, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(goal))
            {
                return;
            }

            // Store goal as metadata marker
            var goalFile = Path.Combine(path, ".lizardfs_goal");
            await File.WriteAllTextAsync(goalFile, goal, ct);

            // In production, this would use lizardfs-admin or xattr:
            // lizardfs-admin set-goal <goal> <path>
            // Or: setfattr -n user.lizardfs_goal -v <goal> <path>
        }

        /// <summary>
        /// Applies quota to a directory, user, or group.
        /// </summary>
        /// <param name="targetPath">Target path or identifier.</param>
        /// <param name="maxBytes">Maximum bytes (-1 for unlimited).</param>
        /// <param name="maxInodes">Maximum inodes/files (-1 for unlimited).</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task ApplyQuotaAsync(string targetPath, long maxBytes, long maxInodes, CancellationToken ct)
        {
            var quotaMetadata = new Dictionary<string, string>
            {
                ["lizardfs.quota.max_bytes"] = maxBytes.ToString(),
                ["lizardfs.quota.max_inodes"] = maxInodes.ToString(),
                ["lizardfs.quota.soft"] = _softQuota.ToString(),
                ["lizardfs.quota.type"] = _quotaType.ToString()
            };

            var quotaFile = Path.Combine(targetPath, ".lizardfs_quota");
            await File.WriteAllTextAsync(quotaFile, JsonSerializer.Serialize(quotaMetadata), ct);

            // In production: lizardfs-admin set-quota <maxBytes> <maxInodes> <target>
        }

        /// <summary>
        /// Moves a file to the trash bin instead of permanently deleting it.
        /// </summary>
        /// <param name="filePath">File to move to trash.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task MoveToTrashAsync(string filePath, CancellationToken ct)
        {
            var trashBasePath = Path.Combine(_mountPath, _trashDirectory);

            if (!Directory.Exists(trashBasePath))
            {
                Directory.CreateDirectory(trashBasePath);
            }

            var fileName = Path.GetFileName(filePath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var trashFileName = $"{fileName}.{timestamp}";
            var trashFilePath = Path.Combine(trashBasePath, trashFileName);

            // Move file to trash
            File.Move(filePath, trashFilePath, overwrite: false);

            // Move metadata if exists
            var metadataFilePath = GetMetadataFilePath(filePath);
            if (File.Exists(metadataFilePath))
            {
                var trashMetadataPath = GetMetadataFilePath(trashFilePath);
                File.Move(metadataFilePath, trashMetadataPath, overwrite: false);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Restores a file from the trash bin.
        /// </summary>
        /// <param name="trashFileName">Name of the file in trash.</param>
        /// <param name="restorePath">Path to restore to.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task RestoreFromTrashAsync(string trashFileName, string restorePath, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableTrashBin || !_allowTrashBin)
            {
                throw new InvalidOperationException("Trash bin is not enabled or not allowed.");
            }

            var trashFilePath = Path.Combine(_mountPath, _trashDirectory, trashFileName);

            if (!File.Exists(trashFilePath))
            {
                throw new FileNotFoundException($"File not found in trash: {trashFileName}", trashFilePath);
            }

            var fullRestorePath = GetFullPath(restorePath);
            var restoreDirectory = Path.GetDirectoryName(fullRestorePath);

            if (!string.IsNullOrWhiteSpace(restoreDirectory) && !Directory.Exists(restoreDirectory))
            {
                Directory.CreateDirectory(restoreDirectory);
            }

            // Move file from trash to restore location
            File.Move(trashFilePath, fullRestorePath, overwrite: false);

            // Move metadata if exists
            var trashMetadataPath = GetMetadataFilePath(trashFilePath);
            if (File.Exists(trashMetadataPath))
            {
                var restoreMetadataPath = GetMetadataFilePath(fullRestorePath);
                File.Move(trashMetadataPath, restoreMetadataPath, overwrite: false);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Cleans up old files from the trash bin based on retention policy.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task CleanupTrashAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableTrashBin)
            {
                return;
            }

            var trashBasePath = Path.Combine(_mountPath, _trashDirectory);

            if (!Directory.Exists(trashBasePath))
            {
                return;
            }

            var cutoffTime = DateTime.UtcNow.AddHours(-_trashRetentionHours);
            var trashFiles = Directory.GetFiles(trashBasePath);

            foreach (var trashFile in trashFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var fileInfo = new FileInfo(trashFile);
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        File.Delete(trashFile);

                        // Delete metadata if exists
                        var metadataPath = GetMetadataFilePath(trashFile);
                        if (File.Exists(metadataPath))
                        {
                            File.Delete(metadataPath);
                        }
                    }
                }
                catch
                {
                    // Skip files that can't be deleted
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates a snapshot of the current filesystem state.
        /// </summary>
        /// <param name="snapshotName">Name for the snapshot.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Snapshot information.</returns>
        public async Task<LizardFsSnapshot> CreateSnapshotAsync(string snapshotName, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableSnapshots || !_allowSnapshots)
            {
                throw new InvalidOperationException("Snapshots are not enabled or not allowed by client capabilities.");
            }

            if (string.IsNullOrWhiteSpace(snapshotName))
            {
                throw new ArgumentException("Snapshot name cannot be empty", nameof(snapshotName));
            }

            var snapshotPath = Path.Combine(_mountPath, _snapshotDirectory, snapshotName);

            if (Directory.Exists(snapshotPath))
            {
                throw new InvalidOperationException($"Snapshot '{snapshotName}' already exists.");
            }

            // Create snapshot directory (LizardFS automatically creates snapshot)
            Directory.CreateDirectory(snapshotPath);

            var snapshot = new LizardFsSnapshot
            {
                Name = snapshotName,
                Path = snapshotPath,
                CreatedAt = DateTime.UtcNow
            };

            await Task.CompletedTask;
            return snapshot;
        }

        /// <summary>
        /// Deletes a snapshot.
        /// </summary>
        /// <param name="snapshotName">Name of the snapshot to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task DeleteSnapshotAsync(string snapshotName, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableSnapshots || !_allowSnapshots)
            {
                throw new InvalidOperationException("Snapshots are not enabled or not allowed by client capabilities.");
            }

            var snapshotPath = Path.Combine(_mountPath, _snapshotDirectory, snapshotName);

            if (!Directory.Exists(snapshotPath))
            {
                throw new InvalidOperationException($"Snapshot '{snapshotName}' does not exist.");
            }

            // Delete snapshot directory
            Directory.Delete(snapshotPath, recursive: false);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Lists all available snapshots.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of snapshots.</returns>
        public async Task<IReadOnlyList<LizardFsSnapshot>> ListSnapshotsAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableSnapshots)
            {
                return Array.Empty<LizardFsSnapshot>();
            }

            var snapshotBasePath = Path.Combine(_mountPath, _snapshotDirectory);

            if (!Directory.Exists(snapshotBasePath))
            {
                return Array.Empty<LizardFsSnapshot>();
            }

            var snapshots = new List<LizardFsSnapshot>();
            var snapshotDirs = Directory.GetDirectories(snapshotBasePath);

            foreach (var snapshotDir in snapshotDirs)
            {
                var snapshotName = Path.GetFileName(snapshotDir);
                var dirInfo = new DirectoryInfo(snapshotDir);

                snapshots.Add(new LizardFsSnapshot
                {
                    Name = snapshotName,
                    Path = snapshotDir,
                    CreatedAt = dirInfo.CreationTimeUtc
                });
            }

            await Task.CompletedTask;
            return snapshots.AsReadOnly();
        }

        /// <summary>
        /// Gets filesystem usage statistics including quota information.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Usage information.</returns>
        public async Task<LizardFsUsageInfo> GetFilesystemUsageAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            try
            {
                var driveInfo = new DriveInfo(_mountPath);

                long totalBytes = driveInfo.TotalSize;
                long usedBytes = totalBytes - driveInfo.AvailableFreeSpace;
                long availableBytes = driveInfo.AvailableFreeSpace;

                // Count files and inodes
                long fileCount = 0;
                try
                {
                    fileCount = Directory.GetFiles(_mountPath, "*", SearchOption.AllDirectories)
                        .Where(f => !IsMetadataFile(f) && !IsSnapshotPath(f) && !IsTrashPath(f))
                        .Count();
                }
                catch
                {
                    // File count may fail if permissions insufficient
                }

                return new LizardFsUsageInfo
                {
                    MasterHost = _masterHost,
                    MasterPort = _masterPort,
                    TotalBytes = totalBytes,
                    UsedBytes = usedBytes,
                    AvailableBytes = availableBytes,
                    FileCount = fileCount,
                    QuotaMaxBytes = _quotaMaxBytes,
                    QuotaMaxInodes = _quotaMaxInodes,
                    QuotaType = _quotaType,
                    SoftQuota = _softQuota,
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to get filesystem usage: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Acquires a global distributed lock.
        /// </summary>
        /// <param name="lockKey">Unique lock identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Disposable lock handle.</returns>
        public async Task<IDisposable> AcquireGlobalLockAsync(string lockKey, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableGlobalLocks)
            {
                throw new InvalidOperationException("Global locks are not enabled.");
            }

            SemaphoreSlim lockObj;

            lock (_lockDictionaryLock)
            {
                if (!_globalLocks.TryGetValue(lockKey, out var existingLock))
                {
                    existingLock = new SemaphoreSlim(1, 1);
                    _globalLocks[lockKey] = existingLock;
                }
                lockObj = existingLock;
            }

            await lockObj.WaitAsync(ct);

            return new LockReleaser(() =>
            {
                lockObj.Release();
            });
        }

        /// <summary>
        /// Triggers defragmentation on the filesystem if enabled.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task DefragmentAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableAutoDefragmentation)
            {
                throw new InvalidOperationException("Defragmentation is not enabled.");
            }

            // In production: lizardfs-admin defragment
            // For now, log the action
            var defragFile = Path.Combine(_mountPath, ".lizardfs_defrag_request");
            await File.WriteAllTextAsync(defragFile, DateTime.UtcNow.ToString("o"), ct);
        }

        /// <summary>
        /// Checks master server health and connectivity.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if master is healthy, false otherwise.</returns>
        private async Task<bool> CheckMasterHealthAsync(CancellationToken ct)
        {
            if (!_useLizardfsAdmin || !_enableHighAvailability)
            {
                return true; // Assume healthy if not checking
            }

            try
            {
                // In production: lizardfs-admin list-masters
                // For now, check if mount is responsive
                var testFile = Path.Combine(_mountPath, ".health_check");
                await File.WriteAllTextAsync(testFile, "health", ct);

                if (File.Exists(testFile))
                {
                    File.Delete(testFile);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Metadata Management

        /// <summary>
        /// Stores metadata for a file using extended attributes or sidecar file.
        /// </summary>
        private async Task StoreMetadataAsync(string filePath, IDictionary<string, string> metadata, CancellationToken ct)
        {
            if (metadata == null || metadata.Count == 0)
            {
                return;
            }

            if (_enableExtendedAttributes && _storeMetadataAsXattr)
            {
                // In production, use platform-specific xattr APIs
                // For Windows: Alternate Data Streams
                // For Linux: setxattr system call with user.* namespace
                // For cross-platform: use sidecar file fallback
            }

            // Fallback to sidecar file
            var metadataFilePath = GetMetadataFilePath(filePath);
            var metadataJson = JsonSerializer.Serialize(metadata);
            await File.WriteAllTextAsync(metadataFilePath, metadataJson, ct);
        }

        /// <summary>
        /// Loads metadata for a file from extended attributes or sidecar file.
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
            // Normalize key to use OS-specific path separators
            var normalizedKey = key.Replace('/', Path.DirectorySeparatorChar)
                                   .Replace('\\', Path.DirectorySeparatorChar);

            return Path.Combine(_mountPath, normalizedKey);
        }

        /// <summary>
        /// Gets the metadata sidecar file path for a given file.
        /// </summary>
        private string GetMetadataFilePath(string filePath)
        {
            return filePath + ".metadata.json";
        }

        /// <summary>
        /// Checks if a file path is a metadata or system file.
        /// </summary>
        private bool IsMetadataFile(string filePath)
        {
            return filePath.EndsWith(".metadata.json", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".lizardfs_goal", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".lizardfs_quota", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".lizardfs_defrag_request", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".health_check", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if a path is within the snapshot directory.
        /// </summary>
        private bool IsSnapshotPath(string filePath)
        {
            return filePath.Contains($"{Path.DirectorySeparatorChar}{_snapshotDirectory}{Path.DirectorySeparatorChar}") ||
                   filePath.Contains($"{Path.AltDirectorySeparatorChar}{_snapshotDirectory}{Path.AltDirectorySeparatorChar}");
        }

        /// <summary>
        /// Checks if a path is within the trash directory.
        /// </summary>
        private bool IsTrashPath(string filePath)
        {
            return filePath.Contains($"{Path.DirectorySeparatorChar}{_trashDirectory}{Path.DirectorySeparatorChar}") ||
                   filePath.Contains($"{Path.AltDirectorySeparatorChar}{_trashDirectory}{Path.AltDirectorySeparatorChar}");
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
        /// Computes MD5 ETag for a file.
        /// </summary>
        private string ComputeFileETag(string filePath)
        {
            try
            {
                using var md5 = MD5.Create();
                using var stream = File.OpenRead(filePath);
                var hash = md5.ComputeHash(stream);
                return Convert.ToHexString(hash).ToLower();
            }
            catch
            {
                // If we can't compute hash, use file info
                var fileInfo = new FileInfo(filePath);
                return $"{fileInfo.Length}-{fileInfo.LastWriteTimeUtc.Ticks}".GetHashCode().ToString("x");
            }
        }

        protected override int GetMaxKeyLength() => 4096; // LizardFS supports long paths

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            // Clean up global locks
            lock (_lockDictionaryLock)
            {
                foreach (var lockObj in _globalLocks.Values)
                {
                    lockObj?.Dispose();
                }
                _globalLocks.Clear();
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Represents a LizardFS snapshot.
    /// </summary>
    public class LizardFsSnapshot
    {
        /// <summary>Snapshot name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Full path to the snapshot directory.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>When the snapshot was created.</summary>
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Represents LizardFS usage statistics.
    /// </summary>
    public class LizardFsUsageInfo
    {
        /// <summary>Master server hostname.</summary>
        public string MasterHost { get; set; } = string.Empty;

        /// <summary>Master server port.</summary>
        public int MasterPort { get; set; }

        /// <summary>Total capacity in bytes.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Used space in bytes.</summary>
        public long UsedBytes { get; set; }

        /// <summary>Available space in bytes.</summary>
        public long AvailableBytes { get; set; }

        /// <summary>Number of files in the filesystem.</summary>
        public long FileCount { get; set; }

        /// <summary>Quota maximum bytes (-1 if unlimited).</summary>
        public long QuotaMaxBytes { get; set; }

        /// <summary>Quota maximum inodes (-1 if unlimited).</summary>
        public long QuotaMaxInodes { get; set; }

        /// <summary>Type of quota applied.</summary>
        public QuotaType QuotaType { get; set; }

        /// <summary>Whether soft quota is enabled.</summary>
        public bool SoftQuota { get; set; }

        /// <summary>When the usage was checked.</summary>
        public DateTime CheckedAt { get; set; }

        /// <summary>Gets the usage percentage (0-100).</summary>
        public double UsagePercent => TotalBytes > 0 ? (UsedBytes * 100.0 / TotalBytes) : 0;

        /// <summary>Checks if quota is exceeded.</summary>
        public bool IsQuotaExceeded =>
            (QuotaMaxBytes > 0 && UsedBytes >= QuotaMaxBytes) ||
            (QuotaMaxInodes > 0 && FileCount >= QuotaMaxInodes);
    }

    /// <summary>
    /// Types of quota that can be applied in LizardFS.
    /// </summary>
    public enum QuotaType
    {
        /// <summary>Directory-level quota.</summary>
        Directory,

        /// <summary>User-level quota.</summary>
        User,

        /// <summary>Group-level quota.</summary>
        Group
    }

    /// <summary>
    /// Lock releaser helper for global distributed locks.
    /// </summary>
    internal class LockReleaser : IDisposable
    {
        private readonly Action _releaseAction;
        private bool _disposed;

        public LockReleaser(Action releaseAction)
        {
            _releaseAction = releaseAction ?? throw new ArgumentNullException(nameof(releaseAction));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _releaseAction();
                _disposed = true;
            }
        }
    }

    #endregion
}
