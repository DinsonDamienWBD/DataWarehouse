using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Local
{
    /// <summary>
    /// RAM Disk storage strategy providing ultra-fast in-memory storage with production features:
    /// - Ultra-low latency (microseconds) for all operations
    /// - ConcurrentDictionary-based storage for thread-safe access
    /// - Configurable size limits with automatic eviction
    /// - TTL/expiration support for cache-like use cases
    /// - Memory pressure handling with LRU eviction
    /// - Optional snapshot/restore persistence to disk
    /// - Full statistics tracking and health monitoring
    /// </summary>
    public class RamDiskStrategy : UltimateStorageStrategyBase
    {
        private readonly BoundedDictionary<string, RamDiskEntry> _storage = new BoundedDictionary<string, RamDiskEntry>(1000);
        private readonly BoundedDictionary<string, LinkedListNode<string>> _lruIndex = new BoundedDictionary<string, LinkedListNode<string>>(1000);
        private readonly LinkedList<string> _lruList = new();
        private readonly SemaphoreSlim _lruLock = new(1, 1);
        private readonly SemaphoreSlim _snapshotLock = new(1, 1);
        private readonly object _memoryLock = new();
        private Timer? _expirationTimer;
        private Timer? _memoryPressureTimer;
        private Timer? _autoSnapshotTimer;

        private long _maxMemoryBytes = 1L * 1024 * 1024 * 1024; // Default 1 GB
        private long _currentMemoryBytes = 0;
        private bool _enableTtl = true;
        private TimeSpan _defaultTtl = TimeSpan.FromHours(1);
        private bool _enableLruEviction = true;
        private double _evictionThresholdPercent = 90.0;
        private string? _snapshotPath = null;
        private bool _autoSnapshot = false;
        private TimeSpan _autoSnapshotInterval = TimeSpan.FromMinutes(5);
        private bool _disposed = false;

        public override string StrategyId => "ram-disk";
        public override string Name => "RAM Disk In-Memory Storage";
        public override StorageTier Tier => StorageTier.RamDisk;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = false,
            SupportsCompression = false,
            SupportsMultipart = false,
            MaxObjectSize = _maxMemoryBytes,
            MaxObjects = null, // Limited by memory
            ConsistencyModel = ConsistencyModel.Strong
        };

        /// <summary>
        /// Initializes the RAM disk storage strategy.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load configuration
            _maxMemoryBytes = GetConfiguration<long>("MaxMemoryBytes", 1L * 1024 * 1024 * 1024);
            _enableTtl = GetConfiguration<bool>("EnableTtl", true);
            _defaultTtl = GetConfiguration<TimeSpan>("DefaultTtl", TimeSpan.FromHours(1));
            _enableLruEviction = GetConfiguration<bool>("EnableLruEviction", true);
            _evictionThresholdPercent = GetConfiguration<double>("EvictionThresholdPercent", 90.0);
            _snapshotPath = GetConfiguration<string?>("SnapshotPath", null);
            _autoSnapshot = GetConfiguration<bool>("AutoSnapshot", false);
            _autoSnapshotInterval = GetConfiguration<TimeSpan>("AutoSnapshotInterval", TimeSpan.FromMinutes(5));

            // Validate configuration
            if (_maxMemoryBytes <= 0)
            {
                throw new InvalidOperationException("MaxMemoryBytes must be greater than 0");
            }

            if (_evictionThresholdPercent < 0 || _evictionThresholdPercent > 100)
            {
                throw new InvalidOperationException("EvictionThresholdPercent must be between 0 and 100");
            }

            // Start expiration cleanup timer if TTL is enabled
            if (_enableTtl)
            {
                var expirationInterval = TimeSpan.FromSeconds(30);
                _expirationTimer = new Timer(
                    _ => _ = Task.Run(async () =>
                    {
                        try { await CleanupExpiredEntriesAsync().ConfigureAwait(false); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RamDiskStrategy.CleanupExpired] {ex.GetType().Name}: {ex.Message}"); }
                    }),
                    null,
                    expirationInterval,
                    expirationInterval);
            }

            // Start memory pressure monitoring timer if LRU eviction is enabled
            if (_enableLruEviction)
            {
                var pressureInterval = TimeSpan.FromSeconds(10);
                _memoryPressureTimer = new Timer(
                    _ => _ = Task.Run(async () =>
                    {
                        try { await CheckMemoryPressureAsync().ConfigureAwait(false); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RamDiskStrategy.CheckMemoryPressure] {ex.GetType().Name}: {ex.Message}"); }
                    }),
                    null,
                    pressureInterval,
                    pressureInterval);
            }

            // Load snapshot if path is provided
            if (!string.IsNullOrEmpty(_snapshotPath) && File.Exists(_snapshotPath))
            {
                await RestoreFromSnapshotAsync(ct).ConfigureAwait(false);
            }

            // Start auto-snapshot timer if enabled
            if (_autoSnapshot && !string.IsNullOrEmpty(_snapshotPath))
            {
                _autoSnapshotTimer = new Timer(
                    _ => _ = Task.Run(async () => await SaveSnapshotAsync(CancellationToken.None).ConfigureAwait(false)),
                    null,
                    _autoSnapshotInterval,
                    _autoSnapshotInterval);
            }
        }

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            ValidateKey(key);
            ValidateStream(data);

            // Read data into memory
            var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            var dataBytes = ms.ToArray();
            var size = dataBytes.LongLength;

            // Check memory limit
            if (Interlocked.Read(ref _currentMemoryBytes) + size > _maxMemoryBytes)
            {
                // Try to evict entries to make space
                if (_enableLruEviction)
                {
                    await EvictEntriesAsync(size, ct);
                }

                // Check again after eviction
                if (Interlocked.Read(ref _currentMemoryBytes) + size > _maxMemoryBytes)
                {
                    throw new InvalidOperationException(
                        $"RAM disk capacity exceeded. Current: {_currentMemoryBytes} bytes, " +
                        $"Requested: {size} bytes, Max: {_maxMemoryBytes} bytes");
                }
            }

            var now = DateTime.UtcNow;
            var expiresAt = _enableTtl ? now.Add(_defaultTtl) : DateTime.MaxValue;

            // Check for custom TTL in metadata
            if (metadata?.TryGetValue("ttl", out var ttlString) == true &&
                int.TryParse(ttlString, out var ttlSeconds))
            {
                expiresAt = now.AddSeconds(ttlSeconds);
            }

            var entry = new RamDiskEntry
            {
                Key = key,
                Data = dataBytes,
                Size = size,
                Created = now,
                Modified = now,
                ExpiresAt = expiresAt,
                CustomMetadata = metadata != null ? new Dictionary<string, string>(metadata) : null,
                AccessCount = 0,
                LastAccessed = now
            };

            // Atomically check-update-store to avoid non-atomic memory accounting
            lock (_memoryLock)
            {
                if (_storage.TryGetValue(key, out var oldEntry))
                {
                    Interlocked.Add(ref _currentMemoryBytes, -oldEntry.Size);
                }

                // Store entry
                _storage[key] = entry;
                Interlocked.Add(ref _currentMemoryBytes, size);
            }

            // Update LRU index
            await UpdateLruAsync(key, ct);

            // Update statistics
            IncrementBytesStored(size);
            IncrementOperationCounter(StorageOperationType.Store);

            return CreateMetadata(entry);
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            if (!_storage.TryGetValue(key, out var entry))
            {
                throw new FileNotFoundException($"Object not found: {key}");
            }

            // Check expiration
            if (entry.ExpiresAt < DateTime.UtcNow)
            {
                // Remove expired entry
                await DeleteAsync(key, ct);
                throw new FileNotFoundException($"Object expired: {key}");
            }

            // Update access tracking
            entry.LastAccessed = DateTime.UtcNow;
            Interlocked.Increment(ref entry.AccessCount);

            // Update LRU
            await UpdateLruAsync(key, ct);

            // Update statistics
            IncrementBytesRetrieved(entry.Size);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            // Return a copy of the data
            return new MemoryStream(entry.Data, writable: false);
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            if (_storage.TryRemove(key, out var entry))
            {
                Interlocked.Add(ref _currentMemoryBytes, -entry.Size);

                // Remove from LRU index
                await RemoveLruAsync(key, ct);

                // Update statistics
                IncrementBytesDeleted(entry.Size);
                IncrementOperationCounter(StorageOperationType.Delete);
            }
        }

        protected override Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            var exists = _storage.TryGetValue(key, out var entry) && entry.ExpiresAt >= DateTime.UtcNow;

            IncrementOperationCounter(StorageOperationType.Exists);

            return Task.FromResult(exists);
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(
            string? prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
            IncrementOperationCounter(StorageOperationType.List);

            var now = DateTime.UtcNow;
            var entries = _storage.Values.Where(e =>
                e.ExpiresAt >= now &&
                (string.IsNullOrEmpty(prefix) || e.Key.StartsWith(prefix, StringComparison.Ordinal)));

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                yield return CreateMetadata(entry);
                await Task.Yield();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            ValidateKey(key);

            if (!_storage.TryGetValue(key, out var entry))
            {
                throw new FileNotFoundException($"Object not found: {key}");
            }

            // Check expiration
            if (entry.ExpiresAt < DateTime.UtcNow)
            {
                await DeleteAsync(key, ct);
                throw new FileNotFoundException($"Object expired: {key}");
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return CreateMetadata(entry);
        }

        protected override Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            var currentMemory = Interlocked.Read(ref _currentMemoryBytes);
            var usagePercent = (currentMemory * 100.0) / _maxMemoryBytes;

            var status = usagePercent switch
            {
                < 80 => HealthStatus.Healthy,
                < 95 => HealthStatus.Degraded,
                _ => HealthStatus.Unhealthy
            };

            var message = $"RAM Disk: {_storage.Count} objects, " +
                         $"{currentMemory / (1024.0 * 1024.0):F2} MB / {_maxMemoryBytes / (1024.0 * 1024.0):F2} MB " +
                         $"({usagePercent:F1}% used)";

            return Task.FromResult(new StorageHealthInfo
            {
                Status = status,
                LatencyMs = 0, // Will be populated by base class
                AvailableCapacity = _maxMemoryBytes - currentMemory,
                TotalCapacity = _maxMemoryBytes,
                UsedCapacity = currentMemory,
                Message = message,
                CheckedAt = DateTime.UtcNow
            });
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            var currentMemory = Interlocked.Read(ref _currentMemoryBytes);
            return Task.FromResult<long?>(_maxMemoryBytes - currentMemory);
        }

        #endregion

        #region LRU Management

        private async Task UpdateLruAsync(string key, CancellationToken ct)
        {
            if (!_enableLruEviction)
                return;

            await _lruLock.WaitAsync(ct);
            try
            {
                // Remove existing node if present
                if (_lruIndex.TryGetValue(key, out var existingNode))
                {
                    _lruList.Remove(existingNode);
                }

                // Add to end (most recently used)
                var newNode = _lruList.AddLast(key);
                _lruIndex[key] = newNode;
            }
            finally
            {
                _lruLock.Release();
            }
        }

        private async Task RemoveLruAsync(string key, CancellationToken ct)
        {
            if (!_enableLruEviction)
                return;

            await _lruLock.WaitAsync(ct);
            try
            {
                if (_lruIndex.TryRemove(key, out var node))
                {
                    _lruList.Remove(node);
                }
            }
            finally
            {
                _lruLock.Release();
            }
        }

        #endregion

        #region Memory Pressure and Eviction

        private async Task CheckMemoryPressureAsync()
        {
            if (!_enableLruEviction || _disposed)
                return;

            var currentMemory = Interlocked.Read(ref _currentMemoryBytes);
            var usagePercent = (currentMemory * 100.0) / _maxMemoryBytes;

            if (usagePercent >= _evictionThresholdPercent)
            {
                // Calculate how much memory to free (bring usage down to 80%)
                var targetMemory = (long)(_maxMemoryBytes * 0.8);
                var memoryToFree = currentMemory - targetMemory;

                await EvictEntriesAsync(memoryToFree, CancellationToken.None);
            }
        }

        private async Task EvictEntriesAsync(long bytesToFree, CancellationToken ct)
        {
            if (!_enableLruEviction)
                return;

            var freedBytes = 0L;
            var keysToEvict = new List<string>();

            await _lruLock.WaitAsync(ct);
            try
            {
                // Evict from least recently used
                var node = _lruList.First;
                while (node != null && freedBytes < bytesToFree)
                {
                    var key = node.Value;
                    if (_storage.TryGetValue(key, out var entry))
                    {
                        keysToEvict.Add(key);
                        freedBytes += entry.Size;
                    }
                    node = node.Next;
                }
            }
            finally
            {
                _lruLock.Release();
            }

            // Evict outside the lock
            foreach (var key in keysToEvict)
            {
                await DeleteAsync(key, ct);
            }
        }

        #endregion

        #region Expiration Cleanup

        private async Task CleanupExpiredEntriesAsync()
        {
            if (!_enableTtl || _disposed)
                return;

            var now = DateTime.UtcNow;
            var expiredKeys = _storage
                .Where(kvp => kvp.Value.ExpiresAt < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                await DeleteAsync(key, CancellationToken.None);
            }
        }

        #endregion

        #region Snapshot and Restore

        /// <summary>
        /// Saves the current RAM disk state to a snapshot file on disk.
        /// </summary>
        public async Task SaveSnapshotAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(_snapshotPath))
            {
                throw new InvalidOperationException("SnapshotPath is not configured");
            }

            await _snapshotLock.WaitAsync(ct);
            try
            {
                var tempPath = _snapshotPath + ".tmp";
                var directory = Path.GetDirectoryName(_snapshotPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var writer = new BinaryWriter(fs);

                // Write header
                writer.Write("RAMDISK_SNAPSHOT".ToCharArray());
                writer.Write(1); // Version

                // Filter out expired entries before writing count to avoid count/data mismatch
                var validEntries = _storage.Where(kvp => kvp.Value.ExpiresAt >= DateTime.UtcNow).ToList();

                // Write entry count (only valid, non-expired entries)
                writer.Write(validEntries.Count);

                // Write entries
                foreach (var kvp in validEntries)
                {
                    var entry = kvp.Value;


                    writer.Write(entry.Key);
                    writer.Write(entry.Size);
                    writer.Write(entry.Data.Length);
                    writer.Write(entry.Data);
                    writer.Write(entry.Created.ToBinary());
                    writer.Write(entry.Modified.ToBinary());
                    writer.Write(entry.ExpiresAt.ToBinary());

                    // Write metadata
                    var metadataCount = entry.CustomMetadata?.Count ?? 0;
                    writer.Write(metadataCount);
                    if (entry.CustomMetadata != null)
                    {
                        foreach (var meta in entry.CustomMetadata)
                        {
                            writer.Write(meta.Key);
                            writer.Write(meta.Value);
                        }
                    }
                }

                await fs.FlushAsync(ct);

                // Atomic rename
                File.Move(tempPath, _snapshotPath, overwrite: true);
            }
            finally
            {
                _snapshotLock.Release();
            }
        }

        /// <summary>
        /// Restores RAM disk state from a snapshot file.
        /// </summary>
        public async Task RestoreFromSnapshotAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(_snapshotPath))
            {
                throw new InvalidOperationException("SnapshotPath is not configured");
            }

            if (!File.Exists(_snapshotPath))
            {
                throw new FileNotFoundException($"Snapshot file not found: {_snapshotPath}");
            }

            await _snapshotLock.WaitAsync(ct);
            try
            {
                await using var fs = new FileStream(_snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(fs);

                // Read and validate header
                var header = new string(reader.ReadChars("RAMDISK_SNAPSHOT".Length));
                if (header != "RAMDISK_SNAPSHOT")
                {
                    throw new InvalidDataException("Invalid snapshot file format");
                }

                var version = reader.ReadInt32();
                if (version != 1)
                {
                    throw new InvalidDataException($"Unsupported snapshot version: {version}");
                }

                // Clear existing data
                _storage.Clear();
                Interlocked.Exchange(ref _currentMemoryBytes, 0);

                // Read entry count
                var entryCount = reader.ReadInt32();

                // Read entries
                for (var i = 0; i < entryCount; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var key = reader.ReadString();
                    var size = reader.ReadInt64();
                    var dataLength = reader.ReadInt32();
                    if (dataLength < 0 || dataLength > 1_073_741_824) // 1GB max per entry
                        throw new InvalidDataException($"Invalid data length in snapshot: {dataLength}");
                    var data = reader.ReadBytes(dataLength);
                    var created = DateTime.FromBinary(reader.ReadInt64());
                    var modified = DateTime.FromBinary(reader.ReadInt64());
                    var expiresAt = DateTime.FromBinary(reader.ReadInt64());

                    // Read metadata
                    var metadataCount = reader.ReadInt32();
                    Dictionary<string, string>? metadata = null;
                    if (metadataCount > 0)
                    {
                        metadata = new Dictionary<string, string>();
                        for (var j = 0; j < metadataCount; j++)
                        {
                            var metaKey = reader.ReadString();
                            var metaValue = reader.ReadString();
                            metadata[metaKey] = metaValue;
                        }
                    }

                    // Skip expired entries
                    if (expiresAt < DateTime.UtcNow)
                        continue;

                    var entry = new RamDiskEntry
                    {
                        Key = key,
                        Data = data,
                        Size = size,
                        Created = created,
                        Modified = modified,
                        ExpiresAt = expiresAt,
                        CustomMetadata = metadata,
                        AccessCount = 0,
                        LastAccessed = DateTime.UtcNow
                    };

                    _storage[key] = entry;
                    Interlocked.Add(ref _currentMemoryBytes, size);

                    // Update LRU
                    await UpdateLruAsync(key, ct);
                }
            }
            finally
            {
                _snapshotLock.Release();
            }
        }

        #endregion

        #region Helper Methods

        private StorageObjectMetadata CreateMetadata(RamDiskEntry entry)
        {
            return new StorageObjectMetadata
            {
                Key = entry.Key,
                Size = entry.Size,
                Created = entry.Created,
                Modified = entry.Modified,
                ETag = GenerateETag(entry),
                ContentType = GetContentType(entry.Key),
                CustomMetadata = entry.CustomMetadata,
                Tier = Tier
            };
        }

        private string GenerateETag(RamDiskEntry entry)
        {
            var hash = HashCode.Combine(entry.Modified.Ticks, entry.Size, entry.Key);
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

        #endregion

        #region Cleanup

        protected override async ValueTask DisposeCoreAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Save snapshot if configured and auto-snapshot is enabled
            if (_autoSnapshot && !string.IsNullOrEmpty(_snapshotPath))
            {
                try
                {
                    await SaveSnapshotAsync(CancellationToken.None);
                }
                catch
                {

                    // Ignore errors during disposal
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }

            // Dispose timers
            _expirationTimer?.Dispose();
            _memoryPressureTimer?.Dispose();
            _autoSnapshotTimer?.Dispose();

            // Dispose locks
            _lruLock?.Dispose();
            _snapshotLock?.Dispose();

            // Clear storage
            _storage.Clear();
            _lruIndex.Clear();
            _lruList.Clear();
            Interlocked.Exchange(ref _currentMemoryBytes, 0);

            await base.DisposeCoreAsync();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Represents an entry stored in the RAM disk.
    /// </summary>
    internal class RamDiskEntry
    {
        public string Key { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long Size { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public DateTime ExpiresAt { get; set; }
        public Dictionary<string, string>? CustomMetadata { get; set; }
        public long AccessCount;
        public DateTime LastAccessed { get; set; }
    }

    #endregion
}
