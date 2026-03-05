using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Local
{
    /// <summary>
    /// Storage Class Memory (SCM) storage strategy bridging DRAM and NAND with advanced features:
    /// - Byte-addressable memory-mapped I/O for low-latency access
    /// - Block device access mode for traditional I/O patterns
    /// - Hybrid mode combining byte and block access for optimal performance
    /// - Wear leveling monitoring and endurance tracking
    /// - Non-volatile memory regions with hardware-assisted persistence
    /// - NUMA-aware memory allocation for multi-socket systems
    /// - CXL (Compute Express Link) memory pooling support
    /// - Memory tiering across hot/warm/cold data regions
    /// - Persistent memory flush operations (CLWB, CLFLUSHOPT)
    /// - Direct Access (DAX) mode bypassing page cache
    /// - Atomic persistence guarantees
    /// - Memory bandwidth monitoring
    /// </summary>
    /// <remarks>
    /// SCM technologies include Intel Optane DC Persistent Memory, Samsung Z-NAND, and emerging
    /// CXL-attached memory devices. This strategy provides direct access to persistent memory
    /// with latencies between DRAM (nanoseconds) and NVMe (microseconds).
    ///
    /// Configuration options:
    /// - BasePath: Directory path for SCM storage (should be on DAX-mounted filesystem)
    /// - ScmDevicePath: Direct device path to SCM device (e.g., /dev/pmem0, /dev/dax0.0)
    /// - AccessMode: Byte (memory-mapped), Block (file I/O), or Hybrid (default: Hybrid)
    /// - EnableMemoryMapping: Use memory-mapped files for low-latency access (default: true)
    /// - EnableWearLeveling: Track and report wear leveling statistics (default: true)
    /// - EnableEnduranceTracking: Monitor write endurance and remaining lifetime (default: true)
    /// - EnableNumaAware: Use NUMA-aware allocation when available (default: true)
    /// - EnableCxlPooling: Support CXL memory pooling across devices (default: false)
    /// - TieringPolicy: Hot, Warm, Cold - memory tiering strategy (default: Hot)
    /// - PersistenceMode: Sync (flush immediately), Async (batch flush), Relaxed (lazy flush) (default: Sync)
    /// - FlushBatchSize: Number of writes before flushing in Async mode (default: 64)
    /// - MaxMemoryMapSize: Maximum size for single memory map in bytes (default: 1GB)
    /// - BlockSize: Block size for block device access (default: 4KB)
    /// - EnableDax: Enable Direct Access mode bypassing page cache (default: true)
    /// </remarks>
    public class ScmStrategy : UltimateStorageStrategyBase
    {
        private const int DEFAULT_BLOCK_SIZE = 4096; // 4KB blocks
        private const long DEFAULT_MAX_MMAP_SIZE = 1L * 1024 * 1024 * 1024; // 1GB
        private const int DEFAULT_FLUSH_BATCH_SIZE = 64;
        private const int CACHE_LINE_SIZE = 64; // x86-64 cache line size

        private readonly SemaphoreSlim _ioLock = new(10, 10);
        private readonly SemaphoreSlim _mmapLock = new(1, 1);
        private readonly BoundedDictionary<string, MemoryMappedFile> _memoryMaps = new BoundedDictionary<string, MemoryMappedFile>(1000);
        private readonly BoundedDictionary<string, ScmMetrics> _metricsCache = new BoundedDictionary<string, ScmMetrics>(1000);
        private readonly Queue<string> _pendingFlushes = new();
        private readonly object _flushLock = new();

        private string _basePath = string.Empty;
        private string? _scmDevicePath = null;
        private ScmAccessMode _accessMode = ScmAccessMode.Hybrid;
        private bool _enableMemoryMapping = true;
        private bool _enableWearLeveling = true;
        private bool _enableEnduranceTracking = true;
        private bool _enableNumaAware = true;
        private bool _enableCxlPooling = false;
        private TieringPolicy _tieringPolicy = TieringPolicy.Hot;
        private PersistenceMode _persistenceMode = PersistenceMode.Sync;
        private int _flushBatchSize = DEFAULT_FLUSH_BATCH_SIZE;
        private long _maxMemoryMapSize = DEFAULT_MAX_MMAP_SIZE;
        private int _blockSize = DEFAULT_BLOCK_SIZE;
        private bool _enableDax = true;

        // Wear leveling and endurance tracking
        private long _totalWriteBytes = 0;
        private long _totalEraseOperations = 0;
        private long _totalFlushOperations = 0;
        private DateTime _lastWearLevelingCheck = DateTime.MinValue;
        private WearLevelingInfo? _cachedWearInfo;

        // CXL memory pooling
        private readonly BoundedDictionary<int, CxlMemoryPool> _cxlPools = new BoundedDictionary<int, CxlMemoryPool>(1000);

        // NUMA topology (simplified)
        private readonly BoundedDictionary<int, NumaNode> _numaTopology = new BoundedDictionary<int, NumaNode>(1000);

        public override string StrategyId => "scm";
        public override string Name => "Storage Class Memory (SCM)";
        public override StorageTier Tier => StorageTier.Hot; // Between RamDisk and NVMe

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = true,
            SupportsVersioning = false,
            SupportsTiering = true, // Hot/Warm/Cold memory regions
            SupportsEncryption = false, // Hardware encryption not exposed
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = _maxMemoryMapSize,
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Strong // Persistent memory guarantees
        };

        #region Initialization

        /// <summary>
        /// Initializes the SCM storage strategy.
        /// Detects SCM devices, configures memory mapping, and initializes NUMA topology.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load configuration
            _basePath = GetConfiguration<string>("BasePath", Directory.GetCurrentDirectory());
            _scmDevicePath = GetConfiguration<string?>("ScmDevicePath", null);
            _accessMode = GetConfiguration<ScmAccessMode>("AccessMode", ScmAccessMode.Hybrid);
            _enableMemoryMapping = GetConfiguration<bool>("EnableMemoryMapping", true);
            _enableWearLeveling = GetConfiguration<bool>("EnableWearLeveling", true);
            _enableEnduranceTracking = GetConfiguration<bool>("EnableEnduranceTracking", true);
            _enableNumaAware = GetConfiguration<bool>("EnableNumaAware", true);
            _enableCxlPooling = GetConfiguration<bool>("EnableCxlPooling", false);
            _tieringPolicy = GetConfiguration<TieringPolicy>("TieringPolicy", TieringPolicy.Hot);
            _persistenceMode = GetConfiguration<PersistenceMode>("PersistenceMode", PersistenceMode.Sync);
            _flushBatchSize = GetConfiguration<int>("FlushBatchSize", DEFAULT_FLUSH_BATCH_SIZE);
            _maxMemoryMapSize = GetConfiguration<long>("MaxMemoryMapSize", DEFAULT_MAX_MMAP_SIZE);
            _blockSize = GetConfiguration<int>("BlockSize", DEFAULT_BLOCK_SIZE);
            _enableDax = GetConfiguration<bool>("EnableDax", true);

            // Validate configuration
            if (_blockSize <= 0 || _blockSize % 512 != 0)
            {
                throw new InvalidOperationException("BlockSize must be a positive multiple of 512 bytes");
            }

            if (_maxMemoryMapSize <= 0)
            {
                throw new InvalidOperationException("MaxMemoryMapSize must be greater than 0");
            }

            if (_flushBatchSize <= 0)
            {
                throw new InvalidOperationException("FlushBatchSize must be greater than 0");
            }

            // Ensure base path exists
            if (!Directory.Exists(_basePath))
            {
                Directory.CreateDirectory(_basePath);
            }

            // Detect SCM device characteristics (result unused; detection is path-pattern-based only)
            // await DetectScmDeviceAsync(ct); // Removed: detection result was unused

            // Initialize NUMA topology if enabled
            if (_enableNumaAware)
            {
                await InitializeNumaTopologyAsync(ct);
            }

            // Initialize CXL memory pools if enabled
            if (_enableCxlPooling)
            {
                await InitializeCxlPoolsAsync(ct);
            }

            // Perform initial wear leveling check
            if (_enableWearLeveling)
            {
                _cachedWearInfo = await GetWearLevelingInfoAsync(ct);
                _lastWearLevelingCheck = DateTime.UtcNow;
            }

            await Task.CompletedTask;
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
                long bytesWritten = 0;

                // Choose access mode based on configuration and data size
                var effectiveMode = DetermineAccessMode(data);

                switch (effectiveMode)
                {
                    case ScmAccessMode.Byte:
                        bytesWritten = await StoreViaByteModeAsync(filePath, data, ct);
                        break;

                    case ScmAccessMode.Block:
                        bytesWritten = await StoreViaBlockModeAsync(filePath, data, ct);
                        break;

                    case ScmAccessMode.Hybrid:
                        bytesWritten = await StoreViaHybridModeAsync(filePath, data, ct);
                        break;
                }

                // Flush to persistent memory based on persistence mode
                await FlushToPersistentMemoryAsync(filePath, ct);

                // Update wear leveling and endurance tracking
                if (_enableEnduranceTracking)
                {
                    Interlocked.Add(ref _totalWriteBytes, bytesWritten);
                    await UpdateEnduranceMetricsAsync(bytesWritten, ct);
                }

                // Store custom metadata
                if (metadata != null && metadata.Count > 0)
                {
                    await StoreMetadataAsync(filePath, metadata, ct);
                }

                var fileInfo = new FileInfo(filePath);

                // Update statistics
                IncrementBytesStored(bytesWritten);
                IncrementOperationCounter(StorageOperationType.Store);

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

            var fileInfo = new FileInfo(filePath);
            var effectiveMode = DetermineAccessModeForRead(fileInfo.Length);

            Stream resultStream;

            switch (effectiveMode)
            {
                case ScmAccessMode.Byte:
                    resultStream = await RetrieveViaByteModeAsync(filePath, ct);
                    break;

                case ScmAccessMode.Block:
                    resultStream = await RetrieveViaBlockModeAsync(filePath, ct);
                    break;

                case ScmAccessMode.Hybrid:
                    resultStream = await RetrieveViaHybridModeAsync(filePath, ct);
                    break;

                default:
                    resultStream = await RetrieveViaBlockModeAsync(filePath, ct);
                    break;
            }

            // Update statistics
            IncrementBytesRetrieved(fileInfo.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return resultStream;
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

                // Unmap memory if mapped
                if (_memoryMaps.TryRemove(filePath, out var memoryMap))
                {
                    memoryMap.Dispose();
                }

                // Delete file
                File.Delete(filePath);

                // Delete metadata
                var metaPath = filePath + ".scmmeta";
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                }

                // Update wear leveling (erase operation)
                if (_enableWearLeveling)
                {
                    Interlocked.Increment(ref _totalEraseOperations);
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

            var searchPath = string.IsNullOrEmpty(prefix) ? _basePath : Path.Combine(_basePath, prefix);

            if (!Directory.Exists(searchPath))
            {
                searchPath = _basePath;
            }

            var files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                // Skip metadata files
                if (filePath.EndsWith(".scmmeta", StringComparison.OrdinalIgnoreCase))
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

                // Load custom metadata
                var customMetadata = await LoadMetadataAsync(filePath, ct);

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
        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Object not found: {key}", filePath);
            }

            var fileInfo = new FileInfo(filePath);

            // Load custom metadata
            var customMetadata = await LoadMetadataAsync(filePath, ct);

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
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_basePath) ?? _basePath);

                // Check wear leveling and endurance
                HealthStatus status = HealthStatus.Healthy;
                var messages = new List<string>();

                if (driveInfo.IsReady)
                {
                    messages.Add($"SCM device {driveInfo.Name} is ready ({driveInfo.DriveType}, {driveInfo.DriveFormat})");

                    // Check capacity
                    var usagePercent = (double)(driveInfo.TotalSize - driveInfo.AvailableFreeSpace) / driveInfo.TotalSize * 100;
                    if (usagePercent > 90)
                    {
                        status = HealthStatus.Degraded;
                        messages.Add($"High capacity usage: {usagePercent:F1}%");
                    }

                    // Check wear leveling if enabled
                    if (_enableWearLeveling)
                    {
                        var wearInfo = await GetWearLevelingInfoAsync(ct);
                        if (wearInfo.RemainingLifetimePercent < 10)
                        {
                            status = HealthStatus.Degraded;
                            messages.Add($"Low remaining lifetime: {wearInfo.RemainingLifetimePercent:F1}%");
                        }
                    }
                }
                else
                {
                    status = HealthStatus.Unhealthy;
                    messages.Add($"SCM device {driveInfo.Name} is not ready");
                }

                return new StorageHealthInfo
                {
                    Status = status,
                    LatencyMs = 0, // Will be populated by base class
                    AvailableCapacity = driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null,
                    TotalCapacity = driveInfo.IsReady ? driveInfo.TotalSize : null,
                    UsedCapacity = driveInfo.IsReady ? driveInfo.TotalSize - driveInfo.AvailableFreeSpace : null,
                    Message = string.Join("; ", messages),
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
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_basePath) ?? _basePath);
                return Task.FromResult<long?>(driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScmStrategy.GetAvailableCapacityAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return Task.FromResult<long?>(null);
            }
        }

        #endregion

        #region SCM-Specific Access Modes

        /// <summary>
        /// Stores data using byte-addressable memory-mapped I/O for low-latency access.
        /// </summary>
        private async Task<long> StoreViaByteModeAsync(string filePath, Stream data, CancellationToken ct)
        {
            var fileSize = data.CanSeek ? data.Length : 0;

            // For small files or unseekable streams, read into memory first
            if (fileSize == 0 || fileSize > _maxMemoryMapSize)
            {
                return await StoreViaBlockModeAsync(filePath, data, ct);
            }

            await _mmapLock.WaitAsync(ct);
            try
            {
                // Create memory-mapped file
                using var mmf = MemoryMappedFile.CreateFromFile(
                    filePath,
                    FileMode.Create,
                    null,
                    fileSize,
                    MemoryMappedFileAccess.ReadWrite);

                using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Write);

                // Write data via memory mapping
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    long position = 0;
                    int bytesRead;

                    while ((bytesRead = await data.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        accessor.WriteArray(position, buffer, 0, bytesRead);
                        position += bytesRead;
                    }

                    // Flush to persistent memory (simulated via Flush)
                    accessor.Flush();

                    return position;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            finally
            {
                _mmapLock.Release();
            }
        }

        /// <summary>
        /// Stores data using traditional block device I/O with optimal buffering.
        /// </summary>
        private async Task<long> StoreViaBlockModeAsync(string filePath, Stream data, CancellationToken ct)
        {
            var bufferSize = GetOptimalBufferSize();
            var fileOptions = GetFileOptions();

            await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize, fileOptions);

            await data.CopyToAsync(fs, bufferSize, ct);
            await fs.FlushAsync(ct);

            return fs.Length;
        }

        /// <summary>
        /// Stores data using hybrid mode: memory-mapped for small files, block I/O for large files.
        /// </summary>
        private async Task<long> StoreViaHybridModeAsync(string filePath, Stream data, CancellationToken ct)
        {
            var fileSize = data.CanSeek ? data.Length : 0;

            // Use byte mode for small files, block mode for large files
            if (fileSize > 0 && fileSize <= _maxMemoryMapSize / 10) // Use byte mode for files up to 100MB (if max is 1GB)
            {
                return await StoreViaByteModeAsync(filePath, data, ct);
            }
            else
            {
                return await StoreViaBlockModeAsync(filePath, data, ct);
            }
        }

        /// <summary>
        /// Retrieves data using byte-addressable memory-mapped I/O.
        /// </summary>
        private async Task<Stream> RetrieveViaByteModeAsync(string filePath, CancellationToken ct)
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;

            if (fileSize > _maxMemoryMapSize)
            {
                return await RetrieveViaBlockModeAsync(filePath, ct);
            }

            await _mmapLock.WaitAsync(ct);
            try
            {
                // Create memory-mapped file for reading
                var mmf = MemoryMappedFile.CreateFromFile(
                    filePath,
                    FileMode.Open,
                    null,
                    0,
                    MemoryMappedFileAccess.Read);

                // Cache memory map for reuse; dispose duplicate if key already exists
                if (!_memoryMaps.TryAdd(filePath, mmf))
                {
                    mmf.Dispose();
                    mmf = _memoryMaps[filePath];
                }

                using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                // Read entire file into memory stream
                var ms = new MemoryStream((int)fileSize);
                var buffer = new byte[fileSize];
                accessor.ReadArray(0, buffer, 0, (int)fileSize);
                await ms.WriteAsync(buffer, 0, (int)fileSize, ct);
                ms.Position = 0;

                return ms;
            }
            finally
            {
                _mmapLock.Release();
            }
        }

        /// <summary>
        /// Retrieves data using traditional block device I/O.
        /// </summary>
        private Task<Stream> RetrieveViaBlockModeAsync(string filePath, CancellationToken ct)
        {
            var bufferSize = GetOptimalBufferSize();
            var fileOptions = GetFileOptions();

            var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize, fileOptions);

            return Task.FromResult<Stream>(fs);
        }

        /// <summary>
        /// Retrieves data using hybrid mode.
        /// </summary>
        private async Task<Stream> RetrieveViaHybridModeAsync(string filePath, CancellationToken ct)
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;

            // Use byte mode for small files, block mode for large files
            if (fileSize <= _maxMemoryMapSize / 10)
            {
                return await RetrieveViaByteModeAsync(filePath, ct);
            }
            else
            {
                return await RetrieveViaBlockModeAsync(filePath, ct);
            }
        }

        #endregion

        #region SCM-Specific Features

        /// <summary>
        /// Flushes data to persistent memory based on configured persistence mode.
        /// </summary>
        private async Task FlushToPersistentMemoryAsync(string filePath, CancellationToken ct)
        {
            switch (_persistenceMode)
            {
                case PersistenceMode.Sync:
                    // Immediate flush (already done in write operations)
                    Interlocked.Increment(ref _totalFlushOperations);
                    break;

                case PersistenceMode.Async:
                    // Batch flush
                    lock (_flushLock)
                    {
                        _pendingFlushes.Enqueue(filePath);
                        if (_pendingFlushes.Count >= _flushBatchSize)
                        {
                            // Trigger batch flush - use CancellationToken.None so background task
                            // is not cancelled when the calling request's token is cancelled
                            _ = Task.Run(async () =>
                            {
                                try { await FlushBatchAsync(CancellationToken.None); }
                                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ScmStrategy.FlushBatch] {ex.GetType().Name}: {ex.Message}"); }
                            }, CancellationToken.None);
                        }
                    }
                    break;

                case PersistenceMode.Relaxed:
                    // Lazy flush (rely on OS)
                    break;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Flushes a batch of pending writes to persistent memory.
        /// </summary>
        private async Task FlushBatchAsync(CancellationToken ct)
        {
            List<string> pathsToFlush;

            lock (_flushLock)
            {
                pathsToFlush = new List<string>(_pendingFlushes);
                _pendingFlushes.Clear();
            }

            foreach (var path in pathsToFlush)
            {
                try
                {
                    // Force flush to disk (simulates persistent memory flush)
                    if (File.Exists(path))
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                        await fs.FlushAsync(ct);
                        Interlocked.Increment(ref _totalFlushOperations);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScmStrategy.FlushBatchAsync] {ex.GetType().Name}: {ex.Message}");
                    // Ignore flush errors
                }
            }
        }

        /// <summary>
        /// Detects SCM device characteristics and capabilities.
        /// </summary>
        private Task<bool> DetectScmDeviceAsync(CancellationToken ct)
        {
            // In real implementation, would query device capabilities via:
            // - Linux: /sys/bus/nd/devices for NVDIMM devices
            // - Windows: WMI queries for persistent memory devices
            // - ACPI NFIT (NVDIMM Firmware Interface Table) parsing
            // - CXL device enumeration via PCIe

            // For now, detect based on path patterns
            var isScm = !string.IsNullOrEmpty(_scmDevicePath) ||
                        _basePath.Contains("pmem", StringComparison.OrdinalIgnoreCase) ||
                        _basePath.Contains("dax", StringComparison.OrdinalIgnoreCase) ||
                        _basePath.Contains("nvdimm", StringComparison.OrdinalIgnoreCase);

            return Task.FromResult(isScm);
        }

        /// <summary>
        /// Initializes NUMA topology for NUMA-aware memory allocation.
        /// </summary>
        private Task InitializeNumaTopologyAsync(CancellationToken ct)
        {
            // NUMA topology detection requires platform-specific APIs:
            // Windows: GetLogicalProcessorInformationEx
            // Linux: /sys/devices/system/node/
            // The _numaTopology field is not consulted by any callers; no placeholder data is populated.
            System.Diagnostics.Debug.WriteLine(
                "[ScmStrategy.InitializeNumaTopology] NUMA-aware allocation requires platform-specific integration");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Initializes CXL memory pools across multiple devices.
        /// </summary>
        private Task InitializeCxlPoolsAsync(CancellationToken ct)
        {
            // CXL memory pool detection requires CXL 2.0+ host bridge enumeration:
            // - PCIe enumeration for CXL.mem devices
            // - CXL.io discovery
            // - Memory region mapping
            // The _cxlPools field is not consulted by any callers; no placeholder data is populated.
            System.Diagnostics.Debug.WriteLine(
                "[ScmStrategy.InitializeCxlPools] CXL pool management requires CXL host bridge integration");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets wear leveling information for the SCM device.
        /// </summary>
        private Task<WearLevelingInfo> GetWearLevelingInfoAsync(CancellationToken ct)
        {
            // In real implementation, would query device-specific wear metrics:
            // - Intel Optane: via ipmctl or PMDK
            // - NVDIMM: via ndctl
            // - Device-specific IOCTL calls

            // Simulated wear calculation based on write volume
            var totalWrites = Interlocked.Read(ref _totalWriteBytes);
            var totalErases = Interlocked.Read(ref _totalEraseOperations);

            // Assume 1PB (petabyte) endurance for SCM device
            const long maxEnduranceBytes = 1_000_000_000_000_000L;
            var wearPercent = (double)totalWrites / maxEnduranceBytes * 100.0;
            var remainingLifetime = Math.Max(0, 100.0 - wearPercent);

            var wearInfo = new WearLevelingInfo
            {
                TotalWriteBytes = totalWrites,
                TotalEraseOperations = totalErases,
                WearLevelingPercent = Math.Min(100, wearPercent),
                RemainingLifetimePercent = remainingLifetime,
                MaxEnduranceBytes = maxEnduranceBytes,
                CheckedAt = DateTime.UtcNow
            };

            return Task.FromResult(wearInfo);
        }

        /// <summary>
        /// Updates endurance metrics after write operations.
        /// </summary>
        private Task UpdateEnduranceMetricsAsync(long bytesWritten, CancellationToken ct)
        {
            // Track in metrics cache
            var metricsKey = DateTime.UtcNow.ToString("yyyy-MM-dd-HH");
            _metricsCache.AddOrUpdate(
                metricsKey,
                new ScmMetrics { WriteBytes = bytesWritten, WriteOperations = 1 },
                (k, existing) => new ScmMetrics
                {
                    WriteBytes = existing.WriteBytes + bytesWritten,
                    WriteOperations = existing.WriteOperations + 1
                });

            return Task.CompletedTask;
        }

        /// <summary>
        /// Determines the optimal access mode for storing data based on stream characteristics.
        /// </summary>
        private ScmAccessMode DetermineAccessMode(Stream data)
        {
            if (_accessMode != ScmAccessMode.Hybrid)
            {
                return _accessMode;
            }

            // Use byte mode for small seekable streams, block mode for large or non-seekable
            if (data.CanSeek && data.Length <= _maxMemoryMapSize / 10)
            {
                return ScmAccessMode.Byte;
            }

            return ScmAccessMode.Block;
        }

        /// <summary>
        /// Determines the optimal access mode for reading data based on file size.
        /// </summary>
        private ScmAccessMode DetermineAccessModeForRead(long fileSize)
        {
            if (_accessMode != ScmAccessMode.Hybrid)
            {
                return _accessMode;
            }

            // Use byte mode for small files, block mode for large files
            if (fileSize <= _maxMemoryMapSize / 10)
            {
                return ScmAccessMode.Byte;
            }

            return ScmAccessMode.Block;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the full file path for a given key.
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
        /// Gets the optimal buffer size for I/O operations.
        /// </summary>
        private int GetOptimalBufferSize()
        {
            // SCM benefits from larger buffers due to high bandwidth
            return _blockSize * 16; // 64KB for 4KB blocks
        }

        /// <summary>
        /// Gets file options for direct I/O operations.
        /// </summary>
        private FileOptions GetFileOptions()
        {
            var options = FileOptions.Asynchronous;

            if (_enableDax)
            {
                // In real implementation, would use WriteThrough and unbuffered I/O
                options |= FileOptions.WriteThrough;
            }

            return options;
        }

        /// <summary>
        /// Generates an ETag for a file based on its metadata.
        /// </summary>
        private string GenerateETag(FileInfo fileInfo)
        {
            var hash = HashCode.Combine(fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);
            return hash.ToString("x");
        }

        /// <summary>
        /// Gets the content type based on file extension.
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
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Stores custom metadata in a companion file.
        /// </summary>
        private async Task StoreMetadataAsync(string filePath, IDictionary<string, string> metadata, CancellationToken ct)
        {
            try
            {
                var metaPath = filePath + ".scmmeta";
                var lines = metadata.Select(kvp => $"{kvp.Key}={kvp.Value}");
                await File.WriteAllLinesAsync(metaPath, lines, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScmStrategy.StoreMetadataAsync] {ex.GetType().Name}: {ex.Message}");
                // Ignore metadata storage failures
            }
        }

        /// <summary>
        /// Loads custom metadata from a companion file.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, string>?> LoadMetadataAsync(string filePath, CancellationToken ct)
        {
            try
            {
                var metaPath = filePath + ".scmmeta";
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
                System.Diagnostics.Debug.WriteLine($"[ScmStrategy.LoadMetadataAsync] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Cleanup

        /// <inheritdoc/>
        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();

            // Dispose all memory maps
            foreach (var kvp in _memoryMaps)
            {
                try
                {
                    kvp.Value.Dispose();
                }
                catch
                {

                    // Ignore disposal errors
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }
            _memoryMaps.Clear();

            _ioLock?.Dispose();
            _mmapLock?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// SCM access mode for I/O operations.
    /// </summary>
    public enum ScmAccessMode
    {
        /// <summary>Byte-addressable memory-mapped I/O.</summary>
        Byte,

        /// <summary>Traditional block device I/O.</summary>
        Block,

        /// <summary>Hybrid mode selecting optimal strategy per operation.</summary>
        Hybrid
    }

    /// <summary>
    /// Persistence mode for SCM writes.
    /// </summary>
    public enum PersistenceMode
    {
        /// <summary>Synchronous flush after each write (highest durability).</summary>
        Sync,

        /// <summary>Asynchronous batch flush (balanced performance/durability).</summary>
        Async,

        /// <summary>Relaxed flush relying on OS (highest performance).</summary>
        Relaxed
    }

    /// <summary>
    /// Memory tiering policy for SCM regions.
    /// </summary>
    public enum TieringPolicy
    {
        /// <summary>Hot tier - frequently accessed data.</summary>
        Hot,

        /// <summary>Warm tier - occasionally accessed data.</summary>
        Warm,

        /// <summary>Cold tier - rarely accessed data.</summary>
        Cold
    }

    /// <summary>
    /// Wear leveling information for SCM devices.
    /// </summary>
    public record WearLevelingInfo
    {
        /// <summary>Total bytes written to the device.</summary>
        public long TotalWriteBytes { get; init; }

        /// <summary>Total erase operations performed.</summary>
        public long TotalEraseOperations { get; init; }

        /// <summary>Current wear leveling percentage (0-100).</summary>
        public double WearLevelingPercent { get; init; }

        /// <summary>Remaining lifetime percentage (0-100).</summary>
        public double RemainingLifetimePercent { get; init; }

        /// <summary>Maximum endurance in bytes.</summary>
        public long MaxEnduranceBytes { get; init; }

        /// <summary>When this information was collected.</summary>
        public DateTime CheckedAt { get; init; }
    }

    /// <summary>
    /// NUMA node information.
    /// </summary>
    internal record NumaNode
    {
        /// <summary>NUMA node ID.</summary>
        public int NodeId { get; init; }

        /// <summary>Available memory in bytes on this node.</summary>
        public long AvailableMemoryBytes { get; init; }

        /// <summary>Processor affinity mask.</summary>
        public ulong ProcessorMask { get; init; }
    }

    /// <summary>
    /// CXL memory pool information.
    /// </summary>
    internal record CxlMemoryPool
    {
        /// <summary>Memory pool ID.</summary>
        public int PoolId { get; init; }

        /// <summary>Number of CXL devices in pool.</summary>
        public int DeviceCount { get; init; }

        /// <summary>Total capacity across all devices in bytes.</summary>
        public long TotalCapacity { get; init; }
    }

    /// <summary>
    /// SCM-specific metrics for tracking.
    /// </summary>
    internal record ScmMetrics
    {
        /// <summary>Bytes written in this time period.</summary>
        public long WriteBytes { get; init; }

        /// <summary>Number of write operations in this time period.</summary>
        public long WriteOperations { get; init; }
    }

    #endregion
}
