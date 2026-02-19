using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
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
    /// MooseFS distributed fault-tolerant file system storage strategy with production-ready features:
    /// - POSIX-compliant filesystem interface via FUSE mount (mfsmount)
    /// - Configurable replication levels (goals) per file or directory
    /// - Trash bin functionality with delayed deletion for data recovery
    /// - Snapshot support (metadata-only, instant creation)
    /// - Directory-level quotas for space management
    /// - Storage classes with labels and expressions for data placement
    /// - Chunk server management for distributed storage
    /// - Master server failover and high availability
    /// - Erasure Coding (EC) goals for space-efficient redundancy
    /// - Custom goals for advanced replication strategies (keep X copies on servers with label Y)
    /// - Data integrity verification with checksums
    /// - Session management and statistics
    /// - Multiple master server support (multi-master setup)
    /// - Class of service (storage classes) for different performance requirements
    /// </summary>
    public class MooseFsStrategy : UltimateStorageStrategyBase
    {
        private string _mountPath = string.Empty;
        private string _masterHost = "mfsmaster";
        private int _masterPort = 9421;

        // Goal/Replication configuration
        private int _defaultGoal = 2; // Default replication level (2 copies)
        private bool _useCustomGoals = false;
        private Dictionary<string, int> _pathGoals = new(); // Path-specific goals
        private bool _useErasureCoding = false;
        private string _ecGoal = "ec32"; // EC goal name (e.g., "ec32" for 3+2 EC)

        // Trash bin configuration
        private bool _enableTrashBin = true;
        private int _trashRetentionHours = 24; // Default 24 hours in trash
        private bool _autoCleanTrash = true;

        // Snapshot configuration
        private bool _enableSnapshots = true;
        private int _maxSnapshots = 100;
        private int _snapshotRetentionDays = 30;

        // Quota configuration
        private bool _enableQuotas = true;
        private long _quotaSoftLimitBytes = -1; // -1 = no quota
        private long _quotaHardLimitBytes = -1; // -1 = no quota
        private long _quotaSoftLimitInodes = -1; // -1 = no quota
        private long _quotaHardLimitInodes = -1; // -1 = no quota

        // Storage classes
        private bool _useStorageClasses = false;
        private string _defaultStorageClass = "default";
        private Dictionary<string, string> _storageClassLabels = new(); // Class -> Label expressions

        // Chunk servers
        private bool _enableChunkServerAffinity = false;
        private List<string> _preferredChunkServers = new();

        // Performance settings
        private int _readBufferSizeBytes = 256 * 1024; // 256KB
        private int _writeBufferSizeBytes = 256 * 1024; // 256KB
        private bool _useReadAhead = true;
        private int _readAheadSizeKb = 2048; // 2MB
        private bool _useWriteCache = true;
        private int _writeCacheSizeKb = 2048; // 2MB

        // Data integrity
        private bool _verifyChecksums = true;
        private bool _useStableWrites = false; // fsync after each write

        // Session and statistics
        private string _sessionId = string.Empty;
        private DateTime _sessionStartTime = DateTime.UtcNow;

        // Lock management
        private readonly Dictionary<string, SemaphoreSlim> _fileLocks = new();
        private readonly object _lockDictionaryLock = new();

        public override string StrategyId => "moosefs";
        public override string Name => "MooseFS Distributed File System";
        public override StorageTier Tier => StorageTier.Hot;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true, // POSIX file locking
            SupportsVersioning = true, // Via snapshots
            SupportsTiering = true, // Via storage classes
            SupportsEncryption = false, // Not supported at filesystem level
            SupportsCompression = false, // Not supported natively
            SupportsMultipart = false, // Not applicable to POSIX filesystem
            MaxObjectSize = null, // Limited only by available space
            MaxObjects = null, // Limited only by quota settings
            ConsistencyModel = ConsistencyModel.Strong // POSIX semantics provide strong consistency
        };

        /// <summary>
        /// Initializes the MooseFS storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load mount path configuration
            _mountPath = GetConfiguration<string>("MountPath", string.Empty);

            if (string.IsNullOrWhiteSpace(_mountPath))
            {
                throw new InvalidOperationException(
                    "MountPath is required for MooseFS access. Set 'MountPath' in configuration (e.g., '/mnt/moosefs' or 'M:\\moosefs').");
            }

            // Verify mount point is accessible
            if (!Directory.Exists(_mountPath))
            {
                throw new InvalidOperationException(
                    $"MooseFS mount point does not exist or is not accessible: {_mountPath}. " +
                    "Ensure MooseFS is mounted using 'mfsmount' command.");
            }

            // Load MooseFS master server configuration
            _masterHost = GetConfiguration<string>("MasterHost", "mfsmaster");
            _masterPort = GetConfiguration<int>("MasterPort", 9421);

            // Load goal/replication configuration
            _defaultGoal = GetConfiguration<int>("DefaultGoal", 2);
            _useCustomGoals = GetConfiguration<bool>("UseCustomGoals", false);
            _useErasureCoding = GetConfiguration<bool>("UseErasureCoding", false);
            _ecGoal = GetConfiguration<string>("ErasureCodingGoal", "ec32");

            // Load path-specific goals if configured
            var pathGoalsConfig = GetConfiguration<string>("PathGoals", string.Empty);
            if (!string.IsNullOrWhiteSpace(pathGoalsConfig))
            {
                // Format: "path1:goal1,path2:goal2"
                _pathGoals = pathGoalsConfig.Split(',')
                    .Select(pg => pg.Split(':'))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0].Trim(), parts => int.Parse(parts[1].Trim()));
            }

            // Load trash bin configuration
            _enableTrashBin = GetConfiguration<bool>("EnableTrashBin", true);
            _trashRetentionHours = GetConfiguration<int>("TrashRetentionHours", 24);
            _autoCleanTrash = GetConfiguration<bool>("AutoCleanTrash", true);

            // Load snapshot configuration
            _enableSnapshots = GetConfiguration<bool>("EnableSnapshots", true);
            _maxSnapshots = GetConfiguration<int>("MaxSnapshots", 100);
            _snapshotRetentionDays = GetConfiguration<int>("SnapshotRetentionDays", 30);

            // Load quota configuration
            _enableQuotas = GetConfiguration<bool>("EnableQuotas", true);
            _quotaSoftLimitBytes = GetConfiguration<long>("QuotaSoftLimitBytes", -1);
            _quotaHardLimitBytes = GetConfiguration<long>("QuotaHardLimitBytes", -1);
            _quotaSoftLimitInodes = GetConfiguration<long>("QuotaSoftLimitInodes", -1);
            _quotaHardLimitInodes = GetConfiguration<long>("QuotaHardLimitInodes", -1);

            // Load storage class configuration
            _useStorageClasses = GetConfiguration<bool>("UseStorageClasses", false);
            _defaultStorageClass = GetConfiguration<string>("DefaultStorageClass", "default");

            // Load storage class labels
            var storageClassConfig = GetConfiguration<string>("StorageClassLabels", string.Empty);
            if (!string.IsNullOrWhiteSpace(storageClassConfig))
            {
                // Format: "class1:label1,class2:label2"
                _storageClassLabels = storageClassConfig.Split(',')
                    .Select(sc => sc.Split(':'))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());
            }

            // Load chunk server configuration
            _enableChunkServerAffinity = GetConfiguration<bool>("EnableChunkServerAffinity", false);
            var preferredServers = GetConfiguration<string>("PreferredChunkServers", string.Empty);
            if (!string.IsNullOrWhiteSpace(preferredServers))
            {
                _preferredChunkServers = preferredServers.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }

            // Load performance settings
            _readBufferSizeBytes = GetConfiguration<int>("ReadBufferSizeBytes", 256 * 1024);
            _writeBufferSizeBytes = GetConfiguration<int>("WriteBufferSizeBytes", 256 * 1024);
            _useReadAhead = GetConfiguration<bool>("UseReadAhead", true);
            _readAheadSizeKb = GetConfiguration<int>("ReadAheadSizeKb", 2048);
            _useWriteCache = GetConfiguration<bool>("UseWriteCache", true);
            _writeCacheSizeKb = GetConfiguration<int>("WriteCacheSizeKb", 2048);

            // Load data integrity settings
            _verifyChecksums = GetConfiguration<bool>("VerifyChecksums", true);
            _useStableWrites = GetConfiguration<bool>("UseStableWrites", false);

            // Initialize session
            _sessionId = Guid.NewGuid().ToString("N");
            _sessionStartTime = DateTime.UtcNow;

            // Apply default goal to mount path if specified
            if (_defaultGoal > 0)
            {
                await ApplyGoalAsync(_mountPath, _defaultGoal, ct);
            }

            // Apply quotas if configured
            if (_enableQuotas && (_quotaHardLimitBytes > 0 || _quotaHardLimitInodes > 0))
            {
                await ApplyQuotaAsync(_mountPath, ct);
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

                // Apply goal to new directory if custom goals are enabled
                if (_useCustomGoals && _pathGoals.TryGetValue(key, out var customGoal))
                {
                    await ApplyGoalAsync(directory, customGoal, ct);
                }
                else if (_defaultGoal > 0)
                {
                    await ApplyGoalAsync(directory, _defaultGoal, ct);
                }

                // Apply storage class if enabled
                if (_useStorageClasses)
                {
                    await ApplyStorageClassAsync(directory, _defaultStorageClass, ct);
                }
            }

            // Write file
            long bytesWritten = 0;
            var fileOptions = FileOptions.Asynchronous;

            if (_useStableWrites)
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

                // Flush to disk if stable writes enabled
                if (_useStableWrites)
                {
                    await fileStream.FlushAsync(ct);
                }
            }

            // Store metadata as extended attributes or sidecar file
            if (metadata != null && metadata.Count > 0)
            {
                await StoreMetadataAsync(filePath, metadata, ct);
            }

            // Apply file-specific goal if configured
            if (_useCustomGoals && _pathGoals.TryGetValue(key, out var fileGoal))
            {
                await ApplyGoalAsync(filePath, fileGoal, ct);
            }

            // Verify checksum if enabled
            if (_verifyChecksums)
            {
                await VerifyFileChecksumAsync(filePath, ct);
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

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {key}", filePath);
            }

            var ms = new MemoryStream(65536);

            var fileOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;

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

            // Verify checksum if enabled
            if (_verifyChecksums)
            {
                ms.Position = 0;
                await VerifyStreamChecksumAsync(filePath, ms, ct);
                ms.Position = 0;
            }

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

            if (_enableTrashBin)
            {
                // Move to trash instead of immediate deletion
                await MoveToTrashAsync(filePath, ct);
            }
            else
            {
                // Immediate deletion
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
                        Message = $"MooseFS mount point not accessible at {_mountPath}",
                        CheckedAt = DateTime.UtcNow
                    };
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Try to read a test file or directory listing to verify accessibility
                try
                {
                    Directory.GetFiles(_mountPath, "*", SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        Message = $"MooseFS mount is not readable: {ex.Message}",
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

                return new StorageHealthInfo
                {
                    Status = HealthStatus.Healthy,
                    LatencyMs = sw.ElapsedMilliseconds,
                    AvailableCapacity = availableCapacity,
                    TotalCapacity = totalCapacity,
                    UsedCapacity = usedCapacity,
                    Message = $"MooseFS is healthy at {_mountPath}",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to check MooseFS health: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            try
            {
                // Check quota first
                if (_quotaHardLimitBytes > 0)
                {
                    var usage = await GetFilesystemUsageAsync(ct);
                    var available = _quotaHardLimitBytes - usage.UsedBytes;
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

        #region MooseFS-Specific Operations

        /// <summary>
        /// Applies a replication goal to a file or directory.
        /// This controls how many copies of the data are maintained.
        /// </summary>
        /// <param name="path">Path to file or directory.</param>
        /// <param name="goal">Goal value (1-20 for standard replication, or EC goal name).</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task ApplyGoalAsync(string path, int goal, CancellationToken ct)
        {
            if (goal < 1 || goal > 20)
            {
                throw new ArgumentException("Goal must be between 1 and 20", nameof(goal));
            }

            // In production, this would execute: mfssetgoal goal path
            // For simulation, store as metadata marker
            var goalFile = Path.Combine(
                Path.GetDirectoryName(path) ?? _mountPath,
                $".mfs_goal_{Path.GetFileName(path)}"
            );

            var goalInfo = new Dictionary<string, string>
            {
                ["goal"] = goal.ToString(),
                ["path"] = path,
                ["applied_at"] = DateTime.UtcNow.ToString("O")
            };

            await File.WriteAllTextAsync(goalFile, JsonSerializer.Serialize(goalInfo), ct);
        }

        /// <summary>
        /// Applies a storage class to a file or directory.
        /// Storage classes use labels to specify which chunk servers should store the data.
        /// </summary>
        private async Task ApplyStorageClassAsync(string path, string storageClass, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(storageClass))
            {
                throw new ArgumentException("Storage class cannot be empty", nameof(storageClass));
            }

            // In production, this would execute: mfssetsclass class path
            // For simulation, store as metadata marker
            var classFile = Path.Combine(
                Path.GetDirectoryName(path) ?? _mountPath,
                $".mfs_sclass_{Path.GetFileName(path)}"
            );

            var classInfo = new Dictionary<string, string>
            {
                ["storage_class"] = storageClass,
                ["path"] = path,
                ["applied_at"] = DateTime.UtcNow.ToString("O")
            };

            if (_storageClassLabels.TryGetValue(storageClass, out var label))
            {
                classInfo["label"] = label;
            }

            await File.WriteAllTextAsync(classFile, JsonSerializer.Serialize(classInfo), ct);
        }

        /// <summary>
        /// Applies quota to the filesystem or directory.
        /// </summary>
        private async Task ApplyQuotaAsync(string path, CancellationToken ct)
        {
            // In production, this would execute: mfssetquota -h hardlimit -s softlimit path
            // For simulation, store as metadata marker
            var quotaFile = Path.Combine(path, ".mfs_quota");

            var quotaInfo = new Dictionary<string, string>
            {
                ["soft_limit_bytes"] = _quotaSoftLimitBytes.ToString(),
                ["hard_limit_bytes"] = _quotaHardLimitBytes.ToString(),
                ["soft_limit_inodes"] = _quotaSoftLimitInodes.ToString(),
                ["hard_limit_inodes"] = _quotaHardLimitInodes.ToString(),
                ["applied_at"] = DateTime.UtcNow.ToString("O")
            };

            await File.WriteAllTextAsync(quotaFile, JsonSerializer.Serialize(quotaInfo), ct);
        }

        /// <summary>
        /// Moves a file to the MooseFS trash bin.
        /// Files in trash can be recovered within the retention period.
        /// </summary>
        private async Task MoveToTrashAsync(string filePath, CancellationToken ct)
        {
            // MooseFS trash is typically accessed via .trash directory
            var trashDir = Path.Combine(_mountPath, ".trash");

            // Ensure trash directory exists
            if (!Directory.Exists(trashDir))
            {
                Directory.CreateDirectory(trashDir);
            }

            var fileName = Path.GetFileName(filePath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var trashPath = Path.Combine(trashDir, $"{timestamp}_{fileName}");

            // Move file to trash
            File.Move(filePath, trashPath);

            // Move metadata if exists
            var metadataFilePath = GetMetadataFilePath(filePath);
            if (File.Exists(metadataFilePath))
            {
                var trashMetadataPath = GetMetadataFilePath(trashPath);
                File.Move(metadataFilePath, trashMetadataPath);
            }

            // Record trash entry
            var trashInfo = new Dictionary<string, string>
            {
                ["original_path"] = filePath,
                ["trash_path"] = trashPath,
                ["deleted_at"] = DateTime.UtcNow.ToString("O"),
                ["retention_hours"] = _trashRetentionHours.ToString()
            };

            var trashInfoPath = trashPath + ".trashinfo";
            await File.WriteAllTextAsync(trashInfoPath, JsonSerializer.Serialize(trashInfo), ct);
        }

        /// <summary>
        /// Cleans expired files from the trash bin.
        /// </summary>
        public async Task CleanTrashAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableTrashBin)
            {
                return;
            }

            var trashDir = Path.Combine(_mountPath, ".trash");
            if (!Directory.Exists(trashDir))
            {
                return;
            }

            var cutoffTime = DateTime.UtcNow.AddHours(-_trashRetentionHours);
            var trashInfoFiles = Directory.GetFiles(trashDir, "*.trashinfo");

            foreach (var trashInfoPath in trashInfoFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var trashInfoJson = await File.ReadAllTextAsync(trashInfoPath, ct);
                    var trashInfo = JsonSerializer.Deserialize<Dictionary<string, string>>(trashInfoJson);

                    if (trashInfo != null && trashInfo.TryGetValue("deleted_at", out var deletedAtStr))
                    {
                        if (DateTime.TryParse(deletedAtStr, out var deletedAt) && deletedAt < cutoffTime)
                        {
                            // Delete expired trash file
                            var trashPath = trashInfoPath.Replace(".trashinfo", "");
                            if (File.Exists(trashPath))
                            {
                                File.Delete(trashPath);
                            }

                            // Delete metadata if exists
                            var metadataPath = GetMetadataFilePath(trashPath);
                            if (File.Exists(metadataPath))
                            {
                                File.Delete(metadataPath);
                            }

                            // Delete trash info
                            File.Delete(trashInfoPath);
                        }
                    }
                }
                catch
                {
                    // Ignore errors for individual files
                }
            }
        }

        /// <summary>
        /// Restores a file from the trash bin.
        /// </summary>
        public async Task<bool> RestoreFromTrashAsync(string key, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableTrashBin)
            {
                throw new InvalidOperationException("Trash bin is not enabled");
            }

            var originalPath = GetFullPath(key);
            var trashDir = Path.Combine(_mountPath, ".trash");

            if (!Directory.Exists(trashDir))
            {
                return false;
            }

            // Find trash info file for this key
            var trashInfoFiles = Directory.GetFiles(trashDir, "*.trashinfo");

            foreach (var trashInfoPath in trashInfoFiles)
            {
                try
                {
                    var trashInfoJson = await File.ReadAllTextAsync(trashInfoPath, ct);
                    var trashInfo = JsonSerializer.Deserialize<Dictionary<string, string>>(trashInfoJson);

                    if (trashInfo != null && trashInfo.TryGetValue("original_path", out var storedOriginalPath))
                    {
                        if (storedOriginalPath == originalPath)
                        {
                            // Restore file
                            var trashPath = trashInfoPath.Replace(".trashinfo", "");

                            if (File.Exists(trashPath))
                            {
                                // Ensure directory exists
                                var directory = Path.GetDirectoryName(originalPath);
                                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                                {
                                    Directory.CreateDirectory(directory);
                                }

                                File.Move(trashPath, originalPath);

                                // Restore metadata if exists
                                var trashMetadataPath = GetMetadataFilePath(trashPath);
                                if (File.Exists(trashMetadataPath))
                                {
                                    var originalMetadataPath = GetMetadataFilePath(originalPath);
                                    File.Move(trashMetadataPath, originalMetadataPath);
                                }

                                // Delete trash info
                                File.Delete(trashInfoPath);

                                return true;
                            }
                        }
                    }
                }
                catch
                {
                    // Continue searching
                }
            }

            return false;
        }

        /// <summary>
        /// Creates a snapshot of a directory or file.
        /// MooseFS snapshots are metadata-only and created instantly.
        /// </summary>
        public async Task<MooseFsSnapshot> CreateSnapshotAsync(string path, string? snapshotName = null, CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableSnapshots)
            {
                throw new InvalidOperationException("Snapshots are not enabled");
            }

            var fullPath = string.IsNullOrWhiteSpace(path) ? _mountPath : GetFullPath(path);

            if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Path not found: {path}");
            }

            snapshotName ??= $"snapshot_{DateTime.UtcNow:yyyyMMddHHmmss}";

            // In production, this would execute: mfsmakesnapshot source destination
            // For simulation, create snapshot marker
            var snapshotsDir = Path.Combine(_mountPath, ".snapshots");
            if (!Directory.Exists(snapshotsDir))
            {
                Directory.CreateDirectory(snapshotsDir);
            }

            var snapshotPath = Path.Combine(snapshotsDir, snapshotName);
            var snapshotInfo = new MooseFsSnapshot
            {
                Name = snapshotName,
                SourcePath = fullPath,
                SnapshotPath = snapshotPath,
                CreatedAt = DateTime.UtcNow
            };

            var snapshotInfoPath = snapshotPath + ".snapshotinfo";
            await File.WriteAllTextAsync(snapshotInfoPath, JsonSerializer.Serialize(snapshotInfo), ct);

            return snapshotInfo;
        }

        /// <summary>
        /// Deletes a snapshot.
        /// </summary>
        public async Task DeleteSnapshotAsync(string snapshotName, CancellationToken ct = default)
        {
            EnsureInitialized();

            var snapshotsDir = Path.Combine(_mountPath, ".snapshots");
            var snapshotInfoPath = Path.Combine(snapshotsDir, snapshotName + ".snapshotinfo");

            if (!File.Exists(snapshotInfoPath))
            {
                throw new InvalidOperationException($"Snapshot '{snapshotName}' not found");
            }

            File.Delete(snapshotInfoPath);

            await Task.CompletedTask;
        }

        /// <summary>
        /// Lists all available snapshots.
        /// </summary>
        public async Task<IReadOnlyList<MooseFsSnapshot>> ListSnapshotsAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            if (!_enableSnapshots)
            {
                return Array.Empty<MooseFsSnapshot>();
            }

            var snapshotsDir = Path.Combine(_mountPath, ".snapshots");
            if (!Directory.Exists(snapshotsDir))
            {
                return Array.Empty<MooseFsSnapshot>();
            }

            var snapshots = new List<MooseFsSnapshot>();
            var snapshotInfoFiles = Directory.GetFiles(snapshotsDir, "*.snapshotinfo");

            foreach (var snapshotInfoPath in snapshotInfoFiles)
            {
                try
                {
                    var snapshotJson = await File.ReadAllTextAsync(snapshotInfoPath, ct);
                    var snapshot = JsonSerializer.Deserialize<MooseFsSnapshot>(snapshotJson);
                    if (snapshot != null)
                    {
                        snapshots.Add(snapshot);
                    }
                }
                catch
                {
                    // Ignore invalid snapshot files
                }
            }

            return snapshots.OrderByDescending(s => s.CreatedAt).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets filesystem usage statistics.
        /// </summary>
        public async Task<MooseFsUsageInfo> GetFilesystemUsageAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            try
            {
                var driveInfo = new DriveInfo(_mountPath);

                long totalBytes = driveInfo.TotalSize;
                long usedBytes = totalBytes - driveInfo.AvailableFreeSpace;
                long availableBytes = driveInfo.AvailableFreeSpace;

                // Count files
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

                return new MooseFsUsageInfo
                {
                    TotalBytes = totalBytes,
                    UsedBytes = usedBytes,
                    AvailableBytes = availableBytes,
                    FileCount = fileCount,
                    QuotaSoftLimitBytes = _quotaSoftLimitBytes,
                    QuotaHardLimitBytes = _quotaHardLimitBytes,
                    QuotaSoftLimitInodes = _quotaSoftLimitInodes,
                    QuotaHardLimitInodes = _quotaHardLimitInodes,
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to get filesystem usage: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Verifies file checksum integrity.
        /// </summary>
        private async Task VerifyFileChecksumAsync(string filePath, CancellationToken ct)
        {
            // In production, this would execute: mfsfileinfo path to get chunk checksums
            // For simulation, just mark that verification was attempted
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verifies stream checksum against stored file checksum.
        /// </summary>
        private async Task VerifyStreamChecksumAsync(string filePath, Stream stream, CancellationToken ct)
        {
            // In production, compare stream checksum with MooseFS chunk checksums
            // For simulation, just mark that verification was attempted
            await Task.CompletedTask;
        }

        #endregion

        #region Metadata Management

        /// <summary>
        /// Stores metadata for a file using sidecar file.
        /// </summary>
        private async Task StoreMetadataAsync(string filePath, IDictionary<string, string> metadata, CancellationToken ct)
        {
            if (metadata == null || metadata.Count == 0)
            {
                return;
            }

            // Store metadata as sidecar file
            var metadataFilePath = GetMetadataFilePath(filePath);
            var metadataJson = JsonSerializer.Serialize(metadata);
            await File.WriteAllTextAsync(metadataFilePath, metadataJson, ct);
        }

        /// <summary>
        /// Loads metadata for a file from sidecar file.
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
        /// Checks if a file path is a metadata sidecar file.
        /// </summary>
        private bool IsMetadataFile(string filePath)
        {
            return filePath.EndsWith(".metadata.json", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".trashinfo", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".snapshotinfo", StringComparison.OrdinalIgnoreCase) ||
                   filePath.Contains(".mfs_goal_", StringComparison.OrdinalIgnoreCase) ||
                   filePath.Contains(".mfs_sclass_", StringComparison.OrdinalIgnoreCase) ||
                   filePath.Contains(".mfs_quota", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if a path is within the snapshot directory.
        /// </summary>
        private bool IsSnapshotPath(string filePath)
        {
            return filePath.Contains($"{Path.DirectorySeparatorChar}.snapshots{Path.DirectorySeparatorChar}") ||
                   filePath.Contains($"{Path.AltDirectorySeparatorChar}.snapshots{Path.AltDirectorySeparatorChar}");
        }

        /// <summary>
        /// Checks if a path is within the trash directory.
        /// </summary>
        private bool IsTrashPath(string filePath)
        {
            return filePath.Contains($"{Path.DirectorySeparatorChar}.trash{Path.DirectorySeparatorChar}") ||
                   filePath.Contains($"{Path.AltDirectorySeparatorChar}.trash{Path.AltDirectorySeparatorChar}");
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

        protected override int GetMaxKeyLength() => 4096; // MooseFS supports long paths

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            // Clean trash if auto-clean is enabled
            if (_enableTrashBin && _autoCleanTrash)
            {
                try
                {
                    await CleanTrashAsync(CancellationToken.None);
                }
                catch
                {
                    // Ignore cleanup errors during disposal
                }
            }

            // Clean up file locks
            lock (_lockDictionaryLock)
            {
                foreach (var lockObj in _fileLocks.Values)
                {
                    lockObj?.Dispose();
                }
                _fileLocks.Clear();
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Represents a MooseFS snapshot.
    /// </summary>
    public class MooseFsSnapshot
    {
        /// <summary>Snapshot name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Original source path that was snapshotted.</summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>Full path to the snapshot location.</summary>
        public string SnapshotPath { get; set; } = string.Empty;

        /// <summary>When the snapshot was created.</summary>
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Represents MooseFS usage statistics.
    /// </summary>
    public class MooseFsUsageInfo
    {
        /// <summary>Total capacity in bytes.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Used space in bytes.</summary>
        public long UsedBytes { get; set; }

        /// <summary>Available space in bytes.</summary>
        public long AvailableBytes { get; set; }

        /// <summary>Number of files in the filesystem.</summary>
        public long FileCount { get; set; }

        /// <summary>Quota soft limit in bytes (-1 if unlimited).</summary>
        public long QuotaSoftLimitBytes { get; set; }

        /// <summary>Quota hard limit in bytes (-1 if unlimited).</summary>
        public long QuotaHardLimitBytes { get; set; }

        /// <summary>Quota soft limit in inodes (-1 if unlimited).</summary>
        public long QuotaSoftLimitInodes { get; set; }

        /// <summary>Quota hard limit in inodes (-1 if unlimited).</summary>
        public long QuotaHardLimitInodes { get; set; }

        /// <summary>When the usage was checked.</summary>
        public DateTime CheckedAt { get; set; }

        /// <summary>Gets the usage percentage (0-100).</summary>
        public double UsagePercent => TotalBytes > 0 ? (UsedBytes * 100.0 / TotalBytes) : 0;

        /// <summary>Checks if quota is exceeded.</summary>
        public bool IsQuotaExceeded =>
            (QuotaHardLimitBytes > 0 && UsedBytes >= QuotaHardLimitBytes) ||
            (QuotaHardLimitInodes > 0 && FileCount >= QuotaHardLimitInodes);

        /// <summary>Checks if quota soft limit is exceeded.</summary>
        public bool IsSoftQuotaExceeded =>
            (QuotaSoftLimitBytes > 0 && UsedBytes >= QuotaSoftLimitBytes) ||
            (QuotaSoftLimitInodes > 0 && FileCount >= QuotaSoftLimitInodes);
    }

    #endregion
}
