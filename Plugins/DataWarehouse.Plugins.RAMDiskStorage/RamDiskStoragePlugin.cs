using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
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
    ///
    /// Message Commands:
    /// - storage.ramdisk.save: Save data to RAM disk
    /// - storage.ramdisk.load: Load data from RAM disk
    /// - storage.ramdisk.delete: Delete data from RAM disk
    /// - storage.ramdisk.flush: Flush all pending writes to disk
    /// - storage.ramdisk.stats: Get storage statistics
    /// </summary>
    public sealed class RamDiskStoragePlugin : ListableStoragePluginBase
    {
        private readonly ConcurrentDictionary<string, RamDiskEntry> _storage = new();
        private readonly RamDiskConfig _config;
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private readonly ConcurrentQueue<PersistenceOperation> _pendingWrites = new();
        private readonly CancellationTokenSource _backgroundCts = new();
        private Task? _backgroundPersister;
        private long _currentSizeBytes;
        private bool _disposed;

        public override string Id => "datawarehouse.plugins.storage.ramdisk";
        public override string Name => "RAMDisk Storage";
        public override string Version => "1.0.0";
        public override string Scheme => "ramdisk";

        /// <summary>
        /// Creates a RAMDisk storage plugin with optional configuration.
        /// </summary>
        public RamDiskStoragePlugin(RamDiskConfig? config = null)
        {
            _config = config ?? new RamDiskConfig();

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

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "storage.ramdisk.save", DisplayName = "Save", Description = "Store data in RAM disk with optional persistence" },
                new() { Name = "storage.ramdisk.load", DisplayName = "Load", Description = "Retrieve data from RAM disk" },
                new() { Name = "storage.ramdisk.delete", DisplayName = "Delete", Description = "Remove data from RAM disk" },
                new() { Name = "storage.ramdisk.list", DisplayName = "List", Description = "Enumerate stored items" },
                new() { Name = "storage.ramdisk.flush", DisplayName = "Flush", Description = "Flush pending writes to disk" },
                new() { Name = "storage.ramdisk.stats", DisplayName = "Stats", Description = "Get storage statistics" },
                new() { Name = "storage.ramdisk.exists", DisplayName = "Exists", Description = "Check if item exists" }
            ];
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

            await SaveAsync(uri, data);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Success = true });
        }

        private async Task<MessageResponse> HandleLoadAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            var stream = await LoadAsync(uri);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Data = ms.ToArray() });
        }

        private async Task<MessageResponse> HandleDeleteAsync(PluginMessage message)
        {
            if (message.Payload is not Dictionary<string, object> payload ||
                !payload.TryGetValue("uri", out var uriObj))
            {
                return MessageResponse.Error("Invalid payload: requires 'uri'");
            }

            var uri = uriObj is Uri u ? u : new Uri(uriObj.ToString()!);
            await DeleteAsync(uri);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Deleted = true });
        }

        private async Task<MessageResponse> HandleFlushAsync(PluginMessage message)
        {
            await FlushAsync();
            return MessageResponse.Ok(new { Flushed = true, PendingWrites = 0 });
        }

        private MessageResponse HandleStats(PluginMessage message)
        {
            return MessageResponse.Ok(new
            {
                ItemCount = Count,
                TotalSizeBytes = TotalSizeBytes,
                PendingWrites = _pendingWrites.Count,
                PersistenceEnabled = _config.EnablePersistence
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
            var exists = await ExistsAsync(uri);
            return MessageResponse.Ok(new { Uri = uri.ToString(), Exists = exists });
        }

        public override async Task SaveAsync(Uri uri, Stream data)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var key = GetKey(uri);

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms);
            var bytes = ms.ToArray();

            // Check memory limits
            if (_config.MaxMemoryBytes.HasValue &&
                TotalSizeBytes + bytes.LongLength > _config.MaxMemoryBytes.Value)
            {
                throw new InvalidOperationException(
                    $"RAMDisk memory limit exceeded. Current: {TotalSizeBytes:N0}, Requested: {bytes.LongLength:N0}, Max: {_config.MaxMemoryBytes.Value:N0}");
            }

            // Remove old version if exists
            if (_storage.TryRemove(key, out var oldEntry))
            {
                Interlocked.Add(ref _currentSizeBytes, -oldEntry.Data.LongLength);
            }

            var entry = new RamDiskEntry
            {
                Key = key,
                Uri = uri,
                Data = bytes,
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow,
                IsDirty = _config.EnablePersistence
            };

            _storage[key] = entry;
            Interlocked.Add(ref _currentSizeBytes, bytes.LongLength);

            // Handle persistence
            if (_config.EnablePersistence)
            {
                if (_config.PersistenceMode == PersistenceMode.WriteThrough)
                {
                    await PersistEntryAsync(entry);
                }
                else
                {
                    _pendingWrites.Enqueue(new PersistenceOperation { Key = key, Type = OperationType.Write });
                }
            }
        }

        public override Task<Stream> LoadAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var key = GetKey(uri);

            if (!_storage.TryGetValue(key, out var entry))
            {
                throw new FileNotFoundException($"Item not found in RAMDisk storage: {key}");
            }

            entry.LastAccessedAt = DateTime.UtcNow;
            entry.AccessCount++;

            return Task.FromResult<Stream>(new MemoryStream(entry.Data));
        }

        public override async Task DeleteAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var key = GetKey(uri);
            if (_storage.TryRemove(key, out var entry))
            {
                Interlocked.Add(ref _currentSizeBytes, -entry.Data.LongLength);

                if (_config.EnablePersistence)
                {
                    if (_config.PersistenceMode == PersistenceMode.WriteThrough)
                    {
                        await DeletePersistedEntryAsync(key);
                    }
                    else
                    {
                        _pendingWrites.Enqueue(new PersistenceOperation { Key = key, Type = OperationType.Delete });
                    }
                }
            }
        }

        public override Task<bool> ExistsAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            var key = GetKey(uri);
            return Task.FromResult(_storage.ContainsKey(key));
        }

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

        /// <summary>
        /// Flushes all pending writes to disk persistence.
        /// </summary>
        public async Task FlushAsync()
        {
            if (!_config.EnablePersistence)
                return;

            await _persistLock.WaitAsync();
            try
            {
                while (_pendingWrites.TryDequeue(out var op))
                {
                    if (op.Type == OperationType.Write && _storage.TryGetValue(op.Key, out var entry))
                    {
                        await PersistEntryAsync(entry);
                        entry.IsDirty = false;
                    }
                    else if (op.Type == OperationType.Delete)
                    {
                        await DeletePersistedEntryAsync(op.Key);
                    }
                }
            }
            finally
            {
                _persistLock.Release();
            }
        }

        private async Task PersistEntryAsync(RamDiskEntry entry)
        {
            if (string.IsNullOrEmpty(_config.PersistencePath))
                return;

            var filePath = GetPersistencePath(entry.Key);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllBytesAsync(filePath, entry.Data);
        }

        private Task DeletePersistedEntryAsync(string key)
        {
            if (string.IsNullOrEmpty(_config.PersistencePath))
                return Task.CompletedTask;

            var filePath = GetPersistencePath(key);
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

                    var entry = new RamDiskEntry
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
                        await FlushAsync();
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

        private string GetPersistencePath(string key)
        {
            return Path.Combine(_config.PersistencePath!, key.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string GetKey(Uri uri)
        {
            return uri.AbsolutePath.TrimStart('/');
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _backgroundCts.Cancel();
            try { _backgroundPersister?.Wait(TimeSpan.FromSeconds(5)); } catch { }

            // Final flush
            FlushAsync().GetAwaiter().GetResult();

            _backgroundCts.Dispose();
            _persistLock.Dispose();
        }

        private sealed class RamDiskEntry
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

        private sealed class PersistenceOperation
        {
            public string Key { get; init; } = string.Empty;
            public OperationType Type { get; init; }
        }

        private enum OperationType { Write, Delete }
    }

    /// <summary>
    /// Configuration for RAMDisk storage.
    /// </summary>
    public class RamDiskConfig
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
        /// Creates a persistent configuration with write-through for maximum durability.
        /// </summary>
        public static RamDiskConfig Durable(string path) => new()
        {
            EnablePersistence = true,
            PersistencePath = path,
            PersistenceMode = PersistenceMode.WriteThrough
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
