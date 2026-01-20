using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Storage;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.RAMDiskStorage
{
    /// <summary>
    /// RAMDisk storage plugin with optional disk persistence.
    ///
    /// Features:
    /// - High-speed in-memory storage using RAM disk semantics
    /// - Optional asynchronous persistence to disk for durability
    /// - Configurable write-back/write-through modes
    /// - Automatic recovery from persistence on startup
    /// - Memory-mapped file support for large datasets
    /// - Thread-safe concurrent access
    /// - Multi-instance support with role-based selection
    /// - TTL-based caching support
    /// - Document indexing and search
    ///
    /// Message Commands:
    /// - storage.ramdisk.save: Save data to RAM disk
    /// - storage.ramdisk.load: Load data from RAM disk
    /// - storage.ramdisk.delete: Delete data from RAM disk
    /// - storage.ramdisk.flush: Flush all pending writes to disk
    /// - storage.ramdisk.stats: Get storage statistics
    /// - storage.ramdisk.exists: Check if item exists
    /// - storage.instance.*: Multi-instance management
    /// </summary>
    public sealed class RamDiskStoragePlugin : HybridStoragePluginBase<RamDiskConfig>
    {
        private readonly ConcurrentDictionary<string, RamDiskEntryData> _storage = new();
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private readonly ConcurrentQueue<PersistenceOperation> _pendingWrites = new();
        private readonly CancellationTokenSource _backgroundCts = new();
        private Task? _backgroundPersister;
        private long _currentSizeBytes;
        private bool _disposed;

        public override string Id => "datawarehouse.plugins.storage.ramdisk";
        public override string Name => "RAMDisk Storage";
        public override string Version => "2.0.0";
        public override string Scheme => "ramdisk";
        public override string StorageCategory => "Memory";

        /// <summary>
        /// Creates a RAMDisk storage plugin with optional configuration.
        /// </summary>
        public RamDiskStoragePlugin(RamDiskConfig? config = null)
            : base(config ?? new RamDiskConfig())
        {
            if (_config.EnablePersistence && !string.IsNullOrEmpty(_config.PersistencePath))
            {
                Directory.CreateDirectory(_config.PersistencePath);
                _ = RecoverFromPersistenceAsync();
                StartBackgroundPersister();
            }
        }

        /// <summary>
        /// Number of items currently stored.
        /// </summary>
        public int Count => _storage.Count;

        /// <summary>
        /// Total size of all stored data in bytes.
        /// </summary>
        public long TotalSizeBytes => Interlocked.Read(ref _currentSizeBytes);

        /// <summary>
        /// Creates a connection for the given configuration.
        /// </summary>
        protected override Task<object> CreateConnectionAsync(RamDiskConfig config)
        {
            var storage = new ConcurrentDictionary<string, RamDiskEntryData>();

            // Recover from persistence if enabled
            if (config.EnablePersistence && !string.IsNullOrEmpty(config.PersistencePath))
            {
                Directory.CreateDirectory(config.PersistencePath);
            }

            return Task.FromResult<object>(new RamDiskConnection
            {
                Config = config,
                Storage = storage
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            var capabilities = base.GetCapabilities();
            capabilities.AddRange(new[]
            {
                new PluginCapabilityDescriptor { Name = "storage.ramdisk.save", DisplayName = "Save", Description = "Store data in RAM disk with optional persistence" },
                new PluginCapabilityDescriptor { Name = "storage.ramdisk.load", DisplayName = "Load", Description = "Retrieve data from RAM disk" },
                new PluginCapabilityDescriptor { Name = "storage.ramdisk.delete", DisplayName = "Delete", Description = "Remove data from RAM disk" },
                new PluginCapabilityDescriptor { Name = "storage.ramdisk.list", DisplayName = "List", Description = "Enumerate stored items" },
                new PluginCapabilityDescriptor { Name = "storage.ramdisk.flush", DisplayName = "Flush", Description = "Flush pending writes to disk" },
                new PluginCapabilityDescriptor { Name = "storage.ramdisk.stats", DisplayName = "Stats", Description = "Get storage statistics" },
                new PluginCapabilityDescriptor { Name = "storage.ramdisk.exists", DisplayName = "Exists", Description = "Check if item exists" }
            });
            return capabilities;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "High-speed RAM disk storage with optional disk persistence.";
            metadata["PersistenceEnabled"] = _config.EnablePersistence;
            metadata["PersistenceMode"] = _config.PersistenceMode.ToString();
            metadata["MaxMemoryBytes"] = _config.MaxMemoryBytes ?? -1;
            metadata["SupportsConcurrency"] = true;
            metadata["SupportsListing"] = true;
            metadata["SupportsRecovery"] = _config.EnablePersistence;
            return metadata;
        }

        /// <summary>
        /// Handles incoming messages for this plugin.
        /// </summary>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            var response = message.Type switch
            {
                "storage.ramdisk.save" => await HandleSaveAsync(message),
                "storage.ramdisk.load" => await HandleLoadAsync(message),
                "storage.ramdisk.delete" => await HandleDeleteAsync(message),
                "storage.ramdisk.flush" => await HandleFlushAsync(message),
                "storage.ramdisk.stats" => HandleStats(message),
                "storage.ramdisk.exists" => await HandleExistsAsync(message),
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

        private async Task<MessageResponse> HandleFlushAsync(PluginMessage message)
        {
            var instanceId = (message.Payload as Dictionary<string, object>)?.TryGetValue("instanceId", out var instId) == true
                ? instId?.ToString()
                : null;

            await FlushAsync(instanceId);
            return MessageResponse.Ok(new { Flushed = true, PendingWrites = 0, InstanceId = instanceId });
        }

        private MessageResponse HandleStats(PluginMessage message)
        {
            var instanceId = (message.Payload as Dictionary<string, object>)?.TryGetValue("instanceId", out var instId) == true
                ? instId?.ToString()
                : null;

            var storage = GetStorage(instanceId);
            var config = GetConfig(instanceId);

            return MessageResponse.Ok(new
            {
                ItemCount = storage.Count,
                TotalSizeBytes = TotalSizeBytes,
                PendingWrites = _pendingWrites.Count,
                PersistenceEnabled = config.EnablePersistence,
                InstanceId = instanceId,
                Instances = _connectionRegistry.Count
            });
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

        #region Storage Operations with Instance Support

        /// <summary>
        /// Save data to storage, optionally targeting a specific instance.
        /// </summary>
        public async Task SaveAsync(Uri uri, Stream data, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var key = GetKey(uri);
            var storage = GetStorage(instanceId);
            var config = GetConfig(instanceId);

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms);
            var bytes = ms.ToArray();

            // Check memory limits
            if (config.MaxMemoryBytes.HasValue &&
                TotalSizeBytes + bytes.LongLength > config.MaxMemoryBytes.Value)
            {
                throw new InvalidOperationException(
                    $"RAMDisk memory limit exceeded. Current: {TotalSizeBytes:N0}, Requested: {bytes.LongLength:N0}, Max: {config.MaxMemoryBytes.Value:N0}");
            }

            // Remove old version if exists
            if (storage.TryRemove(key, out var oldEntry))
            {
                Interlocked.Add(ref _currentSizeBytes, -oldEntry.Data.LongLength);
            }

            var entry = new RamDiskEntryData
            {
                Key = key,
                Uri = uri,
                Data = bytes,
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow,
                IsDirty = config.EnablePersistence
            };

            storage[key] = entry;
            Interlocked.Add(ref _currentSizeBytes, bytes.LongLength);

            // Handle persistence
            if (config.EnablePersistence)
            {
                if (config.PersistenceMode == PersistenceMode.WriteThrough)
                {
                    await PersistEntryAsync(entry, config);
                }
                else
                {
                    _pendingWrites.Enqueue(new PersistenceOperation { Key = key, Type = OperationType.Write, InstanceId = instanceId });
                }
            }

            // Auto-index if enabled
            if (config.EnableIndexing)
            {
                await IndexDocumentAsync(uri.ToString(), new Dictionary<string, object>
                {
                    ["uri"] = uri.ToString(),
                    ["key"] = key,
                    ["size"] = bytes.LongLength,
                    ["instanceId"] = instanceId ?? "default"
                });
            }
        }

        public override Task SaveAsync(Uri uri, Stream data) => SaveAsync(uri, data, null);

        /// <summary>
        /// Load data from storage, optionally from a specific instance.
        /// </summary>
        public Task<Stream> LoadAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var key = GetKey(uri);
            var storage = GetStorage(instanceId);

            if (!storage.TryGetValue(key, out var entry))
            {
                throw new FileNotFoundException($"Item not found in RAMDisk storage: {key}");
            }

            entry.LastAccessedAt = DateTime.UtcNow;
            entry.AccessCount++;

            // Update last access for caching
            _ = TouchAsync(uri);

            return Task.FromResult<Stream>(new MemoryStream(entry.Data));
        }

        public override Task<Stream> LoadAsync(Uri uri) => LoadAsync(uri, null);

        /// <summary>
        /// Delete data from storage, optionally from a specific instance.
        /// </summary>
        public async Task DeleteAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var key = GetKey(uri);
            var storage = GetStorage(instanceId);
            var config = GetConfig(instanceId);

            if (storage.TryRemove(key, out var entry))
            {
                Interlocked.Add(ref _currentSizeBytes, -entry.Data.LongLength);

                if (config.EnablePersistence)
                {
                    if (config.PersistenceMode == PersistenceMode.WriteThrough)
                    {
                        await DeletePersistedEntryAsync(key, config);
                    }
                    else
                    {
                        _pendingWrites.Enqueue(new PersistenceOperation { Key = key, Type = OperationType.Delete, InstanceId = instanceId });
                    }
                }
            }

            // Remove from index
            _ = RemoveFromIndexAsync(uri.ToString());
        }

        public override Task DeleteAsync(Uri uri) => DeleteAsync(uri, null);

        /// <summary>
        /// Check if data exists in storage, optionally in a specific instance.
        /// </summary>
        public Task<bool> ExistsAsync(Uri uri, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(uri);
            var key = GetKey(uri);
            var storage = GetStorage(instanceId);
            return Task.FromResult(storage.ContainsKey(key));
        }

        public override Task<bool> ExistsAsync(Uri uri) => ExistsAsync(uri, null);

        public override async IAsyncEnumerable<StorageListItem> ListFilesAsync(
            string prefix = "",
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kvp in _storage)
            {
                if (ct.IsCancellationRequested)
                    yield break;

                if (string.IsNullOrEmpty(prefix) || kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new StorageListItem(kvp.Value.Uri, kvp.Value.Data.LongLength);
                }

                await Task.Yield();
            }
        }

        #endregion

        #region Helper Methods

        private ConcurrentDictionary<string, RamDiskEntryData> GetStorage(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _storage;

            var instance = _connectionRegistry.Get(instanceId);
            if (instance?.Connection is RamDiskConnection conn)
                return conn.Storage;

            return _storage;
        }

        private RamDiskConfig GetConfig(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
                return _config;

            var instance = _connectionRegistry.Get(instanceId);
            return instance?.Config ?? _config;
        }

        #endregion

        #region Persistence

        /// <summary>
        /// Flushes all pending writes to disk persistence.
        /// </summary>
        public async Task FlushAsync(string? instanceId = null)
        {
            var config = GetConfig(instanceId);
            if (!config.EnablePersistence)
                return;

            var storage = GetStorage(instanceId);

            await _persistLock.WaitAsync();
            try
            {
                while (_pendingWrites.TryDequeue(out var op))
                {
                    // Skip operations for other instances
                    if (op.InstanceId != instanceId)
                    {
                        // Re-enqueue for later
                        _pendingWrites.Enqueue(op);
                        continue;
                    }

                    if (op.Type == OperationType.Write && storage.TryGetValue(op.Key, out var entry))
                    {
                        await PersistEntryAsync(entry, config);
                        entry.IsDirty = false;
                    }
                    else if (op.Type == OperationType.Delete)
                    {
                        await DeletePersistedEntryAsync(op.Key, config);
                    }
                }
            }
            finally
            {
                _persistLock.Release();
            }
        }

        private async Task PersistEntryAsync(RamDiskEntryData entry, RamDiskConfig config)
        {
            if (string.IsNullOrEmpty(config.PersistencePath))
                return;

            var filePath = GetPersistencePath(entry.Key, config);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllBytesAsync(filePath, entry.Data);
        }

        private Task DeletePersistedEntryAsync(string key, RamDiskConfig config)
        {
            if (string.IsNullOrEmpty(config.PersistencePath))
                return Task.CompletedTask;

            var filePath = GetPersistencePath(key, config);
            if (File.Exists(filePath))
                File.Delete(filePath);

            return Task.CompletedTask;
        }

        private async Task RecoverFromPersistenceAsync()
        {
            if (string.IsNullOrEmpty(_config.PersistencePath) || !Directory.Exists(_config.PersistencePath))
                return;

            var files = Directory.GetFiles(_config.PersistencePath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try
                {
                    var relativePath = Path.GetRelativePath(_config.PersistencePath, file);
                    var key = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                    var bytes = await File.ReadAllBytesAsync(file);

                    var entry = new RamDiskEntryData
                    {
                        Key = key,
                        Uri = new Uri($"ramdisk:///{key}"),
                        Data = bytes,
                        CreatedAt = File.GetCreationTimeUtc(file),
                        LastModifiedAt = File.GetLastWriteTimeUtc(file),
                        IsDirty = false
                    };

                    _storage[key] = entry;
                    Interlocked.Add(ref _currentSizeBytes, bytes.LongLength);
                }
                catch
                {
                    // Skip corrupted files during recovery
                }
            }
        }

        private void StartBackgroundPersister()
        {
            _backgroundPersister = Task.Run(async () =>
            {
                while (!_backgroundCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_config.FlushIntervalMs, _backgroundCts.Token);
                        await FlushAsync(null);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Log and continue
                    }
                }
            }, _backgroundCts.Token);
        }

        private string GetPersistencePath(string key, RamDiskConfig config)
        {
            return Path.Combine(config.PersistencePath!, key.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string GetKey(Uri uri)
        {
            return uri.AbsolutePath.TrimStart('/');
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _backgroundCts.Cancel();
            try { _backgroundPersister?.Wait(TimeSpan.FromSeconds(5)); } catch { }

            // Final flush
            FlushAsync(null).GetAwaiter().GetResult();

            _backgroundCts.Dispose();
            _persistLock.Dispose();
        }

        #endregion

        #region Internal Types

        private sealed class PersistenceOperation
        {
            public string Key { get; init; } = string.Empty;
            public OperationType Type { get; init; }
            public string? InstanceId { get; init; }
        }

        private enum OperationType { Write, Delete }

        #endregion
    }

    /// <summary>
    /// Internal connection wrapper for RAMDisk instances.
    /// </summary>
    internal class RamDiskConnection
    {
        public required RamDiskConfig Config { get; init; }
        public required ConcurrentDictionary<string, RamDiskEntryData> Storage { get; init; }
    }

    /// <summary>
    /// Represents a single entry in the RAMDisk storage.
    /// </summary>
    internal sealed class RamDiskEntryData
    {
        public string Key { get; init; } = string.Empty;
        public Uri Uri { get; init; } = null!;
        public byte[] Data { get; init; } = [];
        public DateTime CreatedAt { get; init; }
        public DateTime LastModifiedAt { get; set; }
        public DateTime LastAccessedAt { get; set; }
        public long AccessCount { get; set; }
        public bool IsDirty { get; set; }
    }

    /// <summary>
    /// Configuration for RAMDisk storage.
    /// </summary>
    public class RamDiskConfig : StorageConfigBase
    {
        /// <summary>
        /// Maximum memory allowed in bytes (null = unlimited).
        /// </summary>
        public long? MaxMemoryBytes { get; set; }

        /// <summary>
        /// Enable disk persistence for durability.
        /// </summary>
        public bool EnablePersistence { get; set; }

        /// <summary>
        /// Path for disk persistence.
        /// </summary>
        public string? PersistencePath { get; set; }

        /// <summary>
        /// Persistence mode (WriteThrough or WriteBack).
        /// </summary>
        public PersistenceMode PersistenceMode { get; set; } = PersistenceMode.WriteBack;

        /// <summary>
        /// Interval in milliseconds for flushing dirty entries (WriteBack mode).
        /// </summary>
        public int FlushIntervalMs { get; set; } = 5000;

        /// <summary>
        /// Creates a volatile (no persistence) configuration.
        /// </summary>
        public static RamDiskConfig Volatile => new();

        /// <summary>
        /// Creates a persistent configuration with write-back.
        /// </summary>
        public static RamDiskConfig Persistent(string path) => new()
        {
            EnablePersistence = true,
            PersistencePath = path,
            PersistenceMode = PersistenceMode.WriteBack
        };

        /// <summary>
        /// Creates a persistent configuration with write-back and instance ID.
        /// </summary>
        public static RamDiskConfig Persistent(string path, string instanceId, bool enableCaching = true) => new()
        {
            EnablePersistence = true,
            PersistencePath = path,
            PersistenceMode = PersistenceMode.WriteBack,
            InstanceId = instanceId,
            EnableCaching = enableCaching
        };

        /// <summary>
        /// Creates a persistent configuration with write-through for maximum durability.
        /// </summary>
        public static RamDiskConfig Durable(string path) => new()
        {
            EnablePersistence = true,
            PersistencePath = path,
            PersistenceMode = PersistenceMode.WriteThrough
        };

        /// <summary>
        /// Creates a persistent configuration with write-through and instance ID.
        /// </summary>
        public static RamDiskConfig Durable(string path, string instanceId) => new()
        {
            EnablePersistence = true,
            PersistencePath = path,
            PersistenceMode = PersistenceMode.WriteThrough,
            InstanceId = instanceId
        };
    }

    /// <summary>
    /// Persistence mode for RAMDisk storage.
    /// </summary>
    public enum PersistenceMode
    {
        /// <summary>Write to memory and disk synchronously.</summary>
        WriteThrough,
        /// <summary>Write to memory immediately, persist to disk asynchronously.</summary>
        WriteBack
    }
}
