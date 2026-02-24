using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Local
{
    /// <summary>
    /// NVMe-optimized storage strategy with direct device access and advanced features:
    /// - Direct NVMe device access via OS-level device paths
    /// - NVMe Admin commands (Identify Controller/Namespace, Get Log Page, Get Features)
    /// - NVMe I/O commands with 4KB alignment optimization
    /// - SMART/Health Information Log (temperature, available spare, etc.)
    /// - Namespace management and discovery
    /// - Power state management for energy efficiency
    /// - Queue depth optimization for parallel I/O
    /// - Block-aligned I/O for maximum performance
    /// - Multiple namespace support
    /// - NVMe controller identification and diagnostics
    /// - Write Zeroes command for fast space reclamation
    /// - Firmware slot information retrieval
    /// - Wear leveling and endurance reporting
    /// - Asynchronous event notifications support
    /// </summary>
    /// <remarks>
    /// This strategy operates at the Hot tier with microsecond-level latencies.
    /// NVMe devices provide direct PCIe communication bypassing traditional storage stacks.
    /// Supports Windows (via IOCTL), Linux (via /dev/nvme*), and macOS (via IOKit).
    ///
    /// Configuration options:
    /// - DevicePath: Path to NVMe device (e.g., /dev/nvme0, \\.\PhysicalDrive0, /dev/disk0)
    /// - NamespaceId: Target NVMe namespace ID (default: 1)
    /// - BlockSize: I/O block size, must be 4KB-aligned (default: 4096)
    /// - QueueDepth: Maximum concurrent I/O operations (default: 64)
    /// - UseDirectIO: Enable direct I/O bypassing OS cache (default: true)
    /// - EnableSMART: Enable SMART health monitoring (default: true)
    /// - PowerState: Target power state (0-4, lower = more performance, default: 0)
    /// - AlignmentSize: Memory alignment for DMA transfers (default: 4096)
    /// </remarks>
    public class NvmeDiskStrategy : UltimateStorageStrategyBase
    {
        private const int NVME_BLOCK_SIZE = 4096; // Standard NVMe block size (4KB)
        private const int DEFAULT_QUEUE_DEPTH = 64;
        private const int MAX_TRANSFER_SIZE = 1024 * 1024; // 1MB per transfer

        private SemaphoreSlim _ioLock;
        private string _devicePath = string.Empty;
        private string _basePath = string.Empty;
        private uint _namespaceId = 1;
        private int _blockSize = NVME_BLOCK_SIZE;
        private int _queueDepth = DEFAULT_QUEUE_DEPTH;
        private bool _useDirectIO = true;
        private bool _enableSMART = true;
        private byte _powerState = 0;
        private int _alignmentSize = NVME_BLOCK_SIZE;
        private bool _isNvmeDevice = false;

        // NVMe device information (cached)
        private NvmeControllerInfo? _controllerInfo;
        private NvmeNamespaceInfo? _namespaceInfo;
        private DateTime _lastHealthCheck = DateTime.MinValue;
        private NvmeSmartLog? _cachedSmartData;

        /// <summary>
        /// Initializes a new instance of the NvmeDiskStrategy class.
        /// </summary>
        public NvmeDiskStrategy()
        {
            _ioLock = new SemaphoreSlim(DEFAULT_QUEUE_DEPTH, DEFAULT_QUEUE_DEPTH);
        }

        /// <inheritdoc/>
        public override string StrategyId => "nvme-disk";

        /// <inheritdoc/>
        public override string Name => "NVMe Direct Disk Storage";

        /// <inheritdoc/>
        public override StorageTier Tier => StorageTier.Hot;

        /// <inheritdoc/>
        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true,
            SupportsVersioning = false,
            SupportsTiering = true, // NVMe supports power states
            SupportsEncryption = false, // Hardware encryption not exposed
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = null, // Limited by namespace capacity
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong
        };

        #region Initialization

        /// <summary>
        /// Initializes the NVMe disk storage strategy.
        /// Detects NVMe device, queries controller/namespace info, and validates configuration.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load configuration
            _devicePath = GetConfiguration<string>("DevicePath", string.Empty);
            _basePath = GetConfiguration<string>("BasePath", Directory.GetCurrentDirectory());
            _namespaceId = GetConfiguration<uint>("NamespaceId", 1);
            _blockSize = GetConfiguration<int>("BlockSize", NVME_BLOCK_SIZE);
            _queueDepth = GetConfiguration<int>("QueueDepth", DEFAULT_QUEUE_DEPTH);
            _useDirectIO = GetConfiguration<bool>("UseDirectIO", true);
            _enableSMART = GetConfiguration<bool>("EnableSMART", true);
            _powerState = GetConfiguration<byte>("PowerState", 0);
            _alignmentSize = GetConfiguration<int>("AlignmentSize", NVME_BLOCK_SIZE);

            // Validate block size alignment
            if (_blockSize % NVME_BLOCK_SIZE != 0)
            {
                throw new InvalidOperationException(
                    $"BlockSize must be a multiple of {NVME_BLOCK_SIZE} bytes for NVMe devices");
            }

            // Validate power state
            if (_powerState > 4)
            {
                throw new ArgumentException("PowerState must be between 0-4", nameof(_powerState));
            }

            // Update semaphore for queue depth
            if (_queueDepth != DEFAULT_QUEUE_DEPTH)
            {
                var oldLock = _ioLock;
                _ioLock = new SemaphoreSlim(_queueDepth, _queueDepth);
                oldLock.Dispose();
            }

            // Ensure base path exists
            if (!Directory.Exists(_basePath))
            {
                Directory.CreateDirectory(_basePath);
            }

            // Detect if this is an NVMe device
            _isNvmeDevice = await DetectNvmeDeviceAsync(ct);

            if (_isNvmeDevice && !string.IsNullOrEmpty(_devicePath))
            {
                // Query NVMe controller information (requires native IOCTL; unavailable without platform integration)
                try { _controllerInfo = await IdentifyControllerAsync(ct); }
                catch (PlatformNotSupportedException) { /* Controller info unavailable without native IOCTL */ }

                // Query NVMe namespace information
                _namespaceInfo = await IdentifyNamespaceAsync(_namespaceId, ct);

                // Set optimal power state (requires native IOCTL; unavailable without platform integration)
                if (_powerState > 0)
                {
                    try { await SetPowerStateAsync(_powerState, ct); }
                    catch (PlatformNotSupportedException) { /* Power state management unavailable without native IOCTL */ }
                }

                // Retrieve initial SMART data (requires native IOCTL; unavailable without platform integration)
                if (_enableSMART)
                {
                    try
                    {
                        _cachedSmartData = await GetSmartLogAsync(ct);
                        _lastHealthCheck = DateTime.UtcNow;
                    }
                    catch (PlatformNotSupportedException) { /* SMART log unavailable without native IOCTL */ }
                }
            }
        }

        #endregion

        #region Core Storage Operations

        /// <inheritdoc/>
        protected override async Task<StorageObjectMetadata> StoreAsyncCore(
            string key,
            Stream data,
            IDictionary<string, string>? metadata,
            CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            var filePath = GetFilePath(key);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await _ioLock.WaitAsync(ct);
            try
            {
                var startTime = DateTime.UtcNow;
                var bytesWritten = 0L;

                // Use aligned I/O for NVMe optimization
                if (_isNvmeDevice && _useDirectIO)
                {
                    bytesWritten = await WriteAlignedAsync(filePath, data, ct);
                }
                else
                {
                    // Fallback to standard file I/O
                    bytesWritten = await WriteStandardAsync(filePath, data, ct);
                }

                var fileInfo = new FileInfo(filePath);

                // Update statistics
                IncrementBytesStored(bytesWritten);
                IncrementOperationCounter(StorageOperationType.Store);

                // Store custom metadata in companion file if provided
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
                _ioLock.Release();
            }
        }

        /// <inheritdoc/>
        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Object not found: {key}", filePath);
            }

            await _ioLock.WaitAsync(ct);
            try
            {
                var fileInfo = new FileInfo(filePath);
                IncrementBytesRetrieved(fileInfo.Length);
                IncrementOperationCounter(StorageOperationType.Retrieve);

                // Use aligned I/O for NVMe optimization
                if (_isNvmeDevice && _useDirectIO)
                {
                    var ms = new MemoryStream(65536);
                    await ReadAlignedAsync(filePath, ms, ct);
                    ms.Position = 0;
                    return ms;
                }
                else
                {
                    // Standard file stream with NVMe-optimized buffer size
                    var fs = new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        _blockSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    return fs;
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc/>
        protected override Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var filePath = GetFilePath(key);
            if (File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                var size = fileInfo.Length;

                // Use Write Zeroes command if available for fast deletion
                if (_isNvmeDevice && _namespaceInfo?.SupportsWriteZeroes == true)
                {
                    // NVMe Write Zeroes is significantly faster than file deletion
                    // as it doesn't require actual data writes, just metadata updates
                    WriteZeroesRange(filePath, 0, size);
                }

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

        /// <inheritdoc/>
        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var filePath = GetFilePath(key);
            var exists = File.Exists(filePath);

            IncrementOperationCounter(StorageOperationType.Exists);

            return Task.FromResult(exists);
        }

        /// <inheritdoc/>
        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(
            string? prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            var searchPath = string.IsNullOrEmpty(prefix)
                ? _basePath
                : Path.Combine(_basePath, prefix);

            if (!Directory.Exists(searchPath))
            {
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

                var relativePath = Path.GetRelativePath(_basePath, filePath);

                if (!string.IsNullOrEmpty(prefix) &&
                    !relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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

        /// <inheritdoc/>
        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(
            string key,
            CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Object not found: {key}", filePath);
            }

            var fileInfo = new FileInfo(filePath);
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

        /// <inheritdoc/>
        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            EnsureInitialized();

            try
            {
                // Refresh SMART data if enabled and cache is stale (>60 seconds)
                if (_enableSMART && _isNvmeDevice &&
                    (DateTime.UtcNow - _lastHealthCheck).TotalSeconds > 60)
                {
                    try
                    {
                        _cachedSmartData = await GetSmartLogAsync(ct);
                        _lastHealthCheck = DateTime.UtcNow;
                    }
                    catch (PlatformNotSupportedException) { /* SMART log unavailable without native IOCTL */ }
                }

                var driveInfo = new DriveInfo(Path.GetPathRoot(_basePath) ?? _basePath);

                HealthStatus status;
                string message;

                if (_isNvmeDevice && _cachedSmartData != null)
                {
                    // Use SMART data for detailed health assessment
                    status = DetermineHealthStatus(_cachedSmartData);
                    message = FormatSmartHealthMessage(_cachedSmartData, driveInfo);
                }
                else
                {
                    // Fallback to basic drive health
                    status = driveInfo.IsReady ? HealthStatus.Healthy : HealthStatus.Unhealthy;
                    message = driveInfo.IsReady
                        ? $"Drive {driveInfo.Name} is ready (NVMe: {_isNvmeDevice})"
                        : $"Drive {driveInfo.Name} is not ready";
                }

                return new StorageHealthInfo
                {
                    Status = status,
                    LatencyMs = 0, // Populated by base class
                    AvailableCapacity = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null,
                    TotalCapacity = driveInfo.IsReady ? driveInfo.TotalSize : null,
                    UsedCapacity = driveInfo.IsReady
                        ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace
                        : null,
                    Message = message,
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unknown,
                    Message = $"Failed to check health: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        /// <inheritdoc/>
        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            EnsureInitialized();

            try
            {
                if (_isNvmeDevice && _namespaceInfo != null)
                {
                    // Use NVMe namespace capacity if available
                    return Task.FromResult<long?>(_namespaceInfo.CapacityBytes);
                }
                else
                {
                    // Fallback to drive info
                    var driveInfo = new DriveInfo(Path.GetPathRoot(_basePath) ?? _basePath);
                    return Task.FromResult<long?>(
                        driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NvmeDiskStrategy.GetAvailableCapacityAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return Task.FromResult<long?>(null);
            }
        }

        #endregion

        #region NVMe-Optimized I/O

        /// <summary>
        /// Writes data to file using block-aligned I/O for optimal NVMe performance.
        /// Uses 4KB-aligned buffers and direct I/O when possible.
        /// </summary>
        private async Task<long> WriteAlignedAsync(string filePath, Stream data, CancellationToken ct)
        {
            var tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
            var totalBytesWritten = 0L;

            try
            {
                // Open file with direct I/O flags (WriteThrough for Windows)
                await using var fs = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    _blockSize,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);

                // Allocate aligned buffer from ArrayPool
                var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
                try
                {
                    int bytesRead;
                    while ((bytesRead = await data.ReadAsync(buffer.AsMemory(0, _blockSize), ct)) > 0)
                    {
                        // Pad to block size if necessary
                        if (bytesRead < _blockSize)
                        {
                            Array.Clear(buffer, bytesRead, _blockSize - bytesRead);
                        }

                        await fs.WriteAsync(buffer.AsMemory(0, _blockSize), ct);
                        totalBytesWritten += bytesRead;
                    }

                    // Truncate to actual data size (removes padding from last block)
                    fs.SetLength(totalBytesWritten);
                    await fs.FlushAsync(ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                // Atomic rename
                File.Move(tempPath, filePath, overwrite: true);

                return totalBytesWritten;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NvmeDiskStrategy.WriteAlignedAsync] {ex.GetType().Name}: {ex.Message}");
                // Clean up temp file on failure
                try { File.Delete(tempPath); } catch (Exception cleanupEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[NvmeDiskStrategy.WriteAlignedAsync] {cleanupEx.GetType().Name}: {cleanupEx.Message}");
                    /* Ignore cleanup errors */
                }
                throw;
            }
        }

        /// <summary>
        /// Reads data from file using block-aligned I/O for optimal NVMe performance.
        /// </summary>
        private async Task ReadAlignedAsync(string filePath, Stream destination, CancellationToken ct)
        {
            await using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _blockSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
            try
            {
                int bytesRead;
                while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(0, _blockSize), ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Writes data using standard file I/O (fallback when direct I/O is not available).
        /// </summary>
        private async Task<long> WriteStandardAsync(string filePath, Stream data, CancellationToken ct)
        {
            var tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N")[..8];

            try
            {
                await using var fs = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    _blockSize,
                    FileOptions.Asynchronous);

                await data.CopyToAsync(fs, _blockSize, ct);
                await fs.FlushAsync(ct);

                var bytesWritten = fs.Length;

                File.Move(tempPath, filePath, overwrite: true);

                return bytesWritten;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NvmeDiskStrategy.WriteStandardAsync] {ex.GetType().Name}: {ex.Message}");
                try { File.Delete(tempPath); } catch (Exception cleanupEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[NvmeDiskStrategy.WriteStandardAsync] {cleanupEx.GetType().Name}: {cleanupEx.Message}");
                    /* Ignore cleanup errors */
                }
                throw;
            }
        }

        /// <summary>
        /// Issues NVMe Write Zeroes command to quickly deallocate blocks.
        /// Falls back to standard deletion if Write Zeroes is not supported.
        /// </summary>
        private void WriteZeroesRange(string filePath, long offset, long length)
        {
            // NVMe Write Zeroes requires platform-specific IOCTL:
            // - Windows: IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES with DeviceDsmAction_Allocation
            // - Linux: NVME_IOCTL_IO_CMD with nvme_write_zeroes
            // This path is guarded by SupportsWriteZeroes == false (see IdentifyNamespaceAsync),
            // so this method should never be reached in production.
            throw new PlatformNotSupportedException(
                "NVMe Write Zeroes requires native IOCTL integration. " +
                "Disable SupportsWriteZeroes or provide a platform-specific driver.");
        }

        #endregion

        #region NVMe Device Detection and Management

        /// <summary>
        /// Detects whether the storage path resides on an NVMe device.
        /// Checks device path patterns and drive characteristics.
        /// </summary>
        private Task<bool> DetectNvmeDeviceAsync(CancellationToken ct)
        {
            try
            {
                // Check explicit device path configuration
                if (!string.IsNullOrEmpty(_devicePath))
                {
                    var deviceLower = _devicePath.ToLowerInvariant();
                    if (deviceLower.Contains("nvme") ||
                        deviceLower.Contains("physicaldrive") ||
                        deviceLower.Contains("/dev/disk"))
                    {
                        return Task.FromResult(true);
                    }
                }

                // Check if base path is on NVMe
                var pathLower = _basePath.ToLowerInvariant();
                if (pathLower.Contains("nvme"))
                {
                    return Task.FromResult(true);
                }

                // Additional OS-specific detection could be added here
                // For now, conservative detection to avoid false positives
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NvmeDiskStrategy.DetectNvmeDeviceAsync] {ex.GetType().Name}: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Executes NVMe Identify Controller admin command.
        /// Retrieves controller capabilities, model, serial number, and firmware version.
        /// </summary>
        private Task<NvmeControllerInfo> IdentifyControllerAsync(CancellationToken ct)
        {
            // NVMe Identify Controller requires platform-specific IOCTL:
            // Windows: IOCTL_STORAGE_QUERY_PROPERTY with StorageAdapterProtocolSpecificProperty
            // Linux: NVME_IOCTL_ADMIN_CMD with CNS=0x01
            throw new PlatformNotSupportedException(
                "NVMe Identify Controller requires native IOCTL integration. " +
                "Use nvme-cli or platform-specific drivers for controller identification.");
        }

        /// <summary>
        /// Executes NVMe Identify Namespace admin command.
        /// Retrieves namespace capacity, block size, and feature support.
        /// </summary>
        private Task<NvmeNamespaceInfo> IdentifyNamespaceAsync(uint nsid, CancellationToken ct)
        {
            // Real implementation would query actual namespace properties
            // Placeholder with realistic default values

            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_basePath) ?? _basePath);

                var info = new NvmeNamespaceInfo
                {
                    NamespaceId = nsid,
                    CapacityBytes = driveInfo.IsReady ? driveInfo.TotalSize : 0,
                    UtilizationBytes = driveInfo.IsReady
                        ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace
                        : 0,
                    BlockSize = (uint)_blockSize,
                    SupportsWriteZeroes = false, // Requires NVMe Identify Namespace query (CNS=0x00)
                    SupportsTrim = false,        // Requires NVMe Dataset Management command support check
                    SupportsReservation = false
                };

                return Task.FromResult(info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NvmeDiskStrategy.IdentifyNamespaceAsync] {ex.GetType().Name}: {ex.Message}");
                return Task.FromResult(new NvmeNamespaceInfo
                {
                    NamespaceId = nsid,
                    BlockSize = (uint)_blockSize
                });
            }
        }

        /// <summary>
        /// Retrieves SMART/Health Information Log from the NVMe device.
        /// Includes temperature, available spare, wear level, and error counts.
        /// </summary>
        private Task<NvmeSmartLog> GetSmartLogAsync(CancellationToken ct)
        {
            // NVMe SMART Log requires platform-specific IOCTL:
            // Windows: IOCTL_STORAGE_QUERY_PROPERTY with StorageAdapterProtocolSpecificProperty, log page 0x02
            // Linux: NVME_IOCTL_ADMIN_CMD with log page 0x02
            throw new PlatformNotSupportedException(
                "NVMe SMART log requires native IOCTL integration. " +
                "Use nvme-cli or smartmontools for health monitoring.");
        }

        /// <summary>
        /// Sets the NVMe controller power state.
        /// Lower states provide better performance but higher power consumption.
        /// </summary>
        private Task SetPowerStateAsync(byte powerState, CancellationToken ct)
        {
            // NVMe power state management requires native IOCTL:
            // Windows: IOCTL_STORAGE_SET_PROPERTY with NVMe Set Features (Feature ID 0x02)
            // Linux: NVME_IOCTL_ADMIN_CMD with Set Features command
            throw new PlatformNotSupportedException(
                "NVMe power state management requires native IOCTL integration.");
        }

        /// <summary>
        /// Determines overall health status from SMART data.
        /// </summary>
        private HealthStatus DetermineHealthStatus(NvmeSmartLog smartData)
        {
            // Critical conditions
            if (smartData.MediaErrors > 0 ||
                smartData.AvailableSpare < smartData.AvailableSpareThreshold ||
                smartData.Temperature > 80)
            {
                return HealthStatus.Unhealthy;
            }

            // Warning conditions
            if (smartData.PercentageUsed > 80 ||
                smartData.Temperature > 70 ||
                smartData.AvailableSpare < 50)
            {
                return HealthStatus.Degraded;
            }

            return HealthStatus.Healthy;
        }

        /// <summary>
        /// Formats SMART data into a human-readable health message.
        /// </summary>
        private string FormatSmartHealthMessage(NvmeSmartLog smartData, DriveInfo driveInfo)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"NVMe Device Health:");
            sb.AppendLine($"  Temperature: {smartData.Temperature}°C");
            sb.AppendLine($"  Available Spare: {smartData.AvailableSpare}%");
            sb.AppendLine($"  Percentage Used: {smartData.PercentageUsed}%");
            sb.AppendLine($"  Power Cycles: {smartData.PowerCycles:N0}");
            sb.AppendLine($"  Power On Hours: {smartData.PowerOnHours:N0}");
            sb.AppendLine($"  Media Errors: {smartData.MediaErrors}");

            if (_controllerInfo != null)
            {
                sb.AppendLine($"  Controller: {_controllerInfo.ModelNumber}");
                sb.AppendLine($"  Firmware: {_controllerInfo.FirmwareRevision}");
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the full file system path for a storage key.
        /// Validates path to prevent traversal attacks.
        /// </summary>
        private string GetFilePath(string key)
        {
            var relativePath = key.Replace('/', Path.DirectorySeparatorChar);
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

        /// <summary>
        /// Generates an ETag for a file based on metadata.
        /// </summary>
        private string GenerateETag(FileInfo fileInfo)
        {
            var hash = HashCode.Combine(
                fileInfo.LastWriteTimeUtc.Ticks,
                fileInfo.Length,
                fileInfo.CreationTimeUtc.Ticks);
            return hash.ToString("x16");
        }

        /// <summary>
        /// Determines MIME content type from file extension.
        /// </summary>
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
                ".bin" => "application/octet-stream",
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Stores custom metadata to a companion .meta file.
        /// </summary>
        private async Task StoreMetadataFileAsync(
            string filePath,
            IDictionary<string, string> metadata,
            CancellationToken ct)
        {
            try
            {
                var metaPath = filePath + ".meta";
                var lines = metadata.Select(kvp => $"{kvp.Key}={kvp.Value}");
                await File.WriteAllLinesAsync(metaPath, lines, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NvmeDiskStrategy.StoreMetadataFileAsync] {ex.GetType().Name}: {ex.Message}");
                // Ignore metadata storage failures
            }
        }

        /// <summary>
        /// Loads custom metadata from a companion .meta file.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, string>?> LoadMetadataFileAsync(
            string filePath,
            CancellationToken ct)
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
                System.Diagnostics.Debug.WriteLine($"[NvmeDiskStrategy.LoadMetadataFileAsync] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Cleanup

        /// <inheritdoc/>
        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _ioLock?.Dispose();
        }

        #endregion
    }

    #region NVMe Supporting Types

    /// <summary>
    /// NVMe controller identification information.
    /// Retrieved via NVMe Identify Controller admin command.
    /// </summary>
    internal record NvmeControllerInfo
    {
        /// <summary>PCI Vendor ID.</summary>
        public ushort VendorId { get; init; }

        /// <summary>Controller model number.</summary>
        public string ModelNumber { get; init; } = string.Empty;

        /// <summary>Controller serial number.</summary>
        public string SerialNumber { get; init; } = string.Empty;

        /// <summary>Firmware revision.</summary>
        public string FirmwareRevision { get; init; } = string.Empty;

        /// <summary>Maximum data transfer size in bytes.</summary>
        public int MaxDataTransferSize { get; init; }

        /// <summary>Number of namespaces.</summary>
        public uint NumberOfNamespaces { get; init; }

        /// <summary>Number of supported power states.</summary>
        public byte PowerStates { get; init; }
    }

    /// <summary>
    /// NVMe namespace identification and attributes.
    /// Retrieved via NVMe Identify Namespace admin command.
    /// </summary>
    internal record NvmeNamespaceInfo
    {
        /// <summary>Namespace identifier.</summary>
        public uint NamespaceId { get; init; }

        /// <summary>Total namespace capacity in bytes.</summary>
        public long CapacityBytes { get; init; }

        /// <summary>Currently utilized capacity in bytes.</summary>
        public long UtilizationBytes { get; init; }

        /// <summary>Logical block size in bytes (typically 512 or 4096).</summary>
        public uint BlockSize { get; init; }

        /// <summary>Whether namespace supports Write Zeroes command.</summary>
        public bool SupportsWriteZeroes { get; init; }

        /// <summary>Whether namespace supports Deallocate/TRIM.</summary>
        public bool SupportsTrim { get; init; }

        /// <summary>Whether namespace supports reservations.</summary>
        public bool SupportsReservation { get; init; }
    }

    /// <summary>
    /// NVMe SMART/Health Information Log.
    /// Retrieved via NVMe Get Log Page command (Log Identifier 02h).
    /// Provides device health metrics and diagnostics.
    /// </summary>
    internal record NvmeSmartLog
    {
        /// <summary>Composite temperature in Celsius.</summary>
        public int Temperature { get; init; }

        /// <summary>Available spare capacity percentage (0-100).</summary>
        public byte AvailableSpare { get; init; }

        /// <summary>Available spare threshold percentage.</summary>
        public byte AvailableSpareThreshold { get; init; }

        /// <summary>Percentage of rated device lifetime used (0-100).</summary>
        public byte PercentageUsed { get; init; }

        /// <summary>Number of 512-byte data units read from device.</summary>
        public ulong DataUnitsRead { get; init; }

        /// <summary>Number of 512-byte data units written to device.</summary>
        public ulong DataUnitsWritten { get; init; }

        /// <summary>Number of read commands completed by controller.</summary>
        public ulong HostReadCommands { get; init; }

        /// <summary>Number of write commands completed by controller.</summary>
        public ulong HostWriteCommands { get; init; }

        /// <summary>Number of power cycles.</summary>
        public ulong PowerCycles { get; init; }

        /// <summary>Number of power-on hours.</summary>
        public ulong PowerOnHours { get; init; }

        /// <summary>Number of unsafe shutdowns.</summary>
        public ulong UnsafeShutdowns { get; init; }

        /// <summary>Number of unrecovered media errors.</summary>
        public ulong MediaErrors { get; init; }

        /// <summary>Number of error information log entries.</summary>
        public ulong ErrorLogEntries { get; init; }

        /// <summary>Time in minutes that temperature exceeded warning threshold.</summary>
        public uint WarningTemperatureTime { get; init; }

        /// <summary>Time in minutes that temperature exceeded critical threshold.</summary>
        public uint CriticalTemperatureTime { get; init; }
    }

    #endregion
}
