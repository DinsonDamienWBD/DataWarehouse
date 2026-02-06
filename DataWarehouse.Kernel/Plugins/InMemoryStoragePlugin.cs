using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Plugins
{
    /// <summary>
    /// Production-ready in-memory storage plugin with memory limits, LRU eviction,
    /// and memory pressure detection.
    ///
    /// Features:
    /// - Volatile storage (data lost on shutdown)
    /// - Thread-safe concurrent access
    /// - Configurable memory limits (MaxMemoryBytes, MaxItemCount)
    /// - LRU eviction policy when limits are exceeded
    /// - Memory pressure detection and proactive eviction
    /// - Eviction callbacks for custom handling
    /// - Fast for testing, caching, and ephemeral workloads
    ///
    /// Suitable for: Development, testing, caching layers, session storage
    /// </summary>
    public sealed class InMemoryStoragePlugin : StorageProviderPluginBase, IListableStorage
    {
        private readonly ConcurrentDictionary<string, StoredBlob> _storage = new();
        private readonly object _evictionLock = new();
        private readonly InMemoryStorageConfig _config;
        private readonly List<Action<EvictionEvent>> _evictionCallbacks = new();
        private long _currentSizeBytes;
        private DateTime _lastMemoryCheck = DateTime.UtcNow;

        public override string Id => "datawarehouse.kernel.storage.inmemory";
        public override string Name => "In-Memory Storage";
        public override string Version => "2.0.0";
        public override string Scheme => "memory";

        /// <summary>
        /// Creates an in-memory storage plugin with optional configuration.
        /// </summary>
        public InMemoryStoragePlugin(InMemoryStorageConfig? config = null)
        {
            _config = config ?? new InMemoryStorageConfig();
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
        /// Maximum memory allowed (null = unlimited).
        /// </summary>
        public long? MaxMemoryBytes => _config.MaxMemoryBytes;

        /// <summary>
        /// Maximum item count allowed (null = unlimited).
        /// </summary>
        public int? MaxItemCount => _config.MaxItemCount;

        /// <summary>
        /// Current memory utilization percentage (0-100).
        /// </summary>
        public double MemoryUtilization => _config.MaxMemoryBytes.HasValue
            ? (double)TotalSizeBytes / _config.MaxMemoryBytes.Value * 100
            : 0;

        /// <summary>
        /// Whether the storage is under memory pressure.
        /// </summary>
        public bool IsUnderMemoryPressure => MemoryUtilization >= _config.MemoryPressureThreshold;

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { DisplayName = "Save", Description = "Store data in memory with LRU eviction" },
                new() { DisplayName = "Load", Description = "Retrieve data from memory" },
                new() { DisplayName = "Delete", Description = "Remove data from memory" },
                new() { DisplayName = "List", Description = "Enumerate stored items" },
                new() { DisplayName = "Eviction", Description = "LRU eviction when limits exceeded" },
                new() { DisplayName = "MemoryPressure", Description = "Proactive eviction under memory pressure" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "Production-ready in-memory storage with memory limits and LRU eviction.";
            metadata["Volatile"] = true;
            metadata["MaxMemoryBytes"] = _config.MaxMemoryBytes ?? -1;
            metadata["MaxItemCount"] = _config.MaxItemCount ?? -1;
            metadata["EvictionPolicy"] = "LRU";
            metadata["SupportsConcurrency"] = true;
            metadata["SupportsListing"] = true;
            metadata["SupportsEvictionCallbacks"] = true;
            metadata["PreferredMode"] = "All";
            return metadata;
        }

        /// <summary>
        /// Declared capabilities for automatic registration with the Capability Registry.
        /// Allows the Kernel to function as a minimal but complete DataWarehouse.
        /// </summary>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
        {
            get
            {
                return new List<RegisteredCapability>
                {
                    new()
                    {
                        CapabilityId = "storage.memory",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = "In-Memory Storage",
                        Description = "Volatile in-memory storage with LRU eviction. Fast, thread-safe, suitable for testing, caching, and ephemeral workloads.",
                        Category = SDK.Contracts.CapabilityCategory.Storage,
                        SubCategory = "Memory",
                        Tags = ["storage", "memory", "volatile", "cache", "lru", "testing", "kernel"],
                        Metadata = new Dictionary<string, object>
                        {
                            ["volatile"] = true,
                            ["evictionPolicy"] = "LRU",
                            ["supportsConcurrency"] = true,
                            ["supportsListing"] = true,
                            ["maxMemoryBytes"] = _config.MaxMemoryBytes ?? -1,
                            ["maxItemCount"] = _config.MaxItemCount ?? -1
                        }
                    },
                    new()
                    {
                        CapabilityId = "storage.memory.save",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = "Memory Save",
                        Description = "Store data in memory with automatic LRU eviction when limits exceeded",
                        Category = SDK.Contracts.CapabilityCategory.Storage,
                        SubCategory = "Operations",
                        Tags = ["storage", "save", "write", "memory"]
                    },
                    new()
                    {
                        CapabilityId = "storage.memory.load",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = "Memory Load",
                        Description = "Retrieve data from memory storage",
                        Category = SDK.Contracts.CapabilityCategory.Storage,
                        SubCategory = "Operations",
                        Tags = ["storage", "load", "read", "memory"]
                    },
                    new()
                    {
                        CapabilityId = "storage.memory.delete",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = "Memory Delete",
                        Description = "Remove data from memory storage",
                        Category = SDK.Contracts.CapabilityCategory.Storage,
                        SubCategory = "Operations",
                        Tags = ["storage", "delete", "remove", "memory"]
                    },
                    new()
                    {
                        CapabilityId = "storage.memory.list",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = "Memory List",
                        Description = "Enumerate all items in memory storage",
                        Category = SDK.Contracts.CapabilityCategory.Storage,
                        SubCategory = "Operations",
                        Tags = ["storage", "list", "enumerate", "memory"]
                    },
                    new()
                    {
                        CapabilityId = "storage.memory.eviction",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = "LRU Eviction",
                        Description = "Automatic eviction of least-recently-used items when memory limits exceeded",
                        Category = SDK.Contracts.CapabilityCategory.Storage,
                        SubCategory = "Management",
                        Tags = ["storage", "eviction", "lru", "memory-management"]
                    }
                }.AsReadOnly();
            }
        }

        /// <summary>
        /// Static knowledge about this storage plugin for AI and discovery.
        /// </summary>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new List<KnowledgeObject>
            {
                new()
                {
                    Id = $"{Id}:overview",
                    Topic = "storage.kernel",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = "Kernel's built-in in-memory storage provides volatile, fast storage for testing " +
                                "and development. Data is lost on shutdown. Features include LRU eviction, memory " +
                                "pressure detection, configurable limits, and thread-safe concurrent access. " +
                                "This allows the Kernel to function as a minimal but complete DataWarehouse " +
                                "without requiring external storage plugins.",
                    Tags = ["storage", "memory", "kernel", "volatile", "testing", "development", "lru", "cache"],
                    Payload = new Dictionary<string, object>
                    {
                        ["purpose"] = "Built-in volatile storage for kernel self-sufficiency",
                        ["volatile"] = true,
                        ["evictionPolicy"] = "LRU",
                        ["features"] = new[]
                        {
                            "Thread-safe concurrent access",
                            "Configurable memory limits",
                            "LRU eviction policy",
                            "Memory pressure detection",
                            "Eviction callbacks",
                            "Item listing support"
                        },
                        ["useCases"] = new[]
                        {
                            "Development and testing",
                            "Caching layers",
                            "Session storage",
                            "Ephemeral workloads",
                            "Kernel self-test mode"
                        },
                        ["limitations"] = new[]
                        {
                            "Data lost on shutdown",
                            "Limited by available RAM",
                            "No persistence",
                            "No replication"
                        },
                        ["currentStats"] = new Dictionary<string, object>
                        {
                            ["itemCount"] = Count,
                            ["totalSizeBytes"] = TotalSizeBytes,
                            ["memoryUtilization"] = MemoryUtilization,
                            ["isUnderPressure"] = IsUnderMemoryPressure
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Registers a callback to be invoked when items are evicted.
        /// </summary>
        public void OnEviction(Action<EvictionEvent> callback)
        {
            lock (_evictionCallbacks)
            {
                _evictionCallbacks.Add(callback);
            }
        }

        /// <summary>
        /// Save data to memory with automatic eviction if limits are exceeded.
        /// </summary>
        public override async Task SaveAsync(Uri uri, Stream data)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var key = GetKey(uri);

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var newSize = bytes.LongLength;

            // Check if this single item exceeds max memory
            if (_config.MaxMemoryBytes.HasValue && newSize > _config.MaxMemoryBytes.Value)
            {
                throw new InvalidOperationException(
                    $"Item size ({newSize:N0} bytes) exceeds maximum allowed memory ({_config.MaxMemoryBytes.Value:N0} bytes)");
            }

            // Remove old version if exists (to update size tracking)
            if (_storage.TryRemove(key, out var oldBlob))
            {
                Interlocked.Add(ref _currentSizeBytes, -oldBlob.Data.LongLength);
            }

            // Evict items if necessary to make room
            EnsureCapacity(newSize, 1);

            // Store the new item
            var blob = new StoredBlob
            {
                Key = key,
                Uri = uri,
                Data = bytes,
                CreatedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow
            };

            _storage[key] = blob;
            Interlocked.Add(ref _currentSizeBytes, newSize);

            // Check for system memory pressure periodically
            CheckSystemMemoryPressure();
        }

        /// <summary>
        /// Load data from memory (updates LRU access time).
        /// </summary>
        public override Task<Stream> LoadAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var key = GetKey(uri);

            if (!_storage.TryGetValue(key, out var blob))
            {
                throw new FileNotFoundException($"Item not found in memory storage: {key}");
            }

            // Update LRU access time
            blob.LastAccessedAt = DateTime.UtcNow;
            blob.AccessCount++;

            return Task.FromResult<Stream>(new MemoryStream(blob.Data));
        }

        /// <summary>
        /// Delete data from memory.
        /// </summary>
        public override Task DeleteAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var key = GetKey(uri);
            if (_storage.TryRemove(key, out var blob))
            {
                Interlocked.Add(ref _currentSizeBytes, -blob.Data.LongLength);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Ensures there's capacity for the new data, evicting LRU items if necessary.
        /// </summary>
        private void EnsureCapacity(long requiredBytes, int requiredItems)
        {
            lock (_evictionLock)
            {
                var evictedItems = new List<EvictionEvent>();

                // Evict until we have enough space
                while (NeedsEviction(requiredBytes, requiredItems))
                {
                    var lruItem = GetLruItem();
                    if (lruItem == null)
                        break; // No more items to evict

                    if (_storage.TryRemove(lruItem.Key, out var evicted))
                    {
                        Interlocked.Add(ref _currentSizeBytes, -evicted.Data.LongLength);

                        evictedItems.Add(new EvictionEvent
                        {
                            Key = evicted.Key,
                            Uri = evicted.Uri,
                            SizeBytes = evicted.Data.LongLength,
                            Reason = DetermineEvictionReason(),
                            EvictedAt = DateTime.UtcNow,
                            Age = DateTime.UtcNow - evicted.CreatedAt,
                            TimeSinceLastAccess = DateTime.UtcNow - evicted.LastAccessedAt
                        });
                    }
                }

                // Notify callbacks
                if (evictedItems.Count > 0)
                {
                    NotifyEvictionCallbacks(evictedItems);
                }
            }
        }

        private bool NeedsEviction(long requiredBytes, int requiredItems)
        {
            // Check memory limit
            if (_config.MaxMemoryBytes.HasValue)
            {
                if (TotalSizeBytes + requiredBytes > _config.MaxMemoryBytes.Value)
                    return true;
            }

            // Check item count limit
            if (_config.MaxItemCount.HasValue)
            {
                if (_storage.Count + requiredItems > _config.MaxItemCount.Value)
                    return true;
            }

            return false;
        }

        private StoredBlob? GetLruItem()
        {
            // Find the least recently used item
            return _storage.Values
                .OrderBy(b => b.LastAccessedAt)
                .ThenBy(b => b.AccessCount)
                .FirstOrDefault();
        }

        private EvictionReason DetermineEvictionReason()
        {
            if (_config.MaxMemoryBytes.HasValue && TotalSizeBytes >= _config.MaxMemoryBytes.Value * 0.95)
                return EvictionReason.MemoryLimit;

            if (_config.MaxItemCount.HasValue && _storage.Count >= _config.MaxItemCount.Value)
                return EvictionReason.ItemCountLimit;

            if (IsUnderMemoryPressure)
                return EvictionReason.MemoryPressure;

            return EvictionReason.Manual;
        }

        private void NotifyEvictionCallbacks(List<EvictionEvent> events)
        {
            List<Action<EvictionEvent>> callbacks;
            lock (_evictionCallbacks)
            {
                callbacks = _evictionCallbacks.ToList();
            }

            foreach (var evt in events)
            {
                foreach (var callback in callbacks)
                {
                    try
                    {
                        callback(evt);
                    }
                    catch
                    {
                        // Ignore callback errors
                    }
                }
            }
        }

        private void CheckSystemMemoryPressure()
        {
            // Check system memory every 10 seconds
            if ((DateTime.UtcNow - _lastMemoryCheck).TotalSeconds < 10)
                return;

            _lastMemoryCheck = DateTime.UtcNow;

            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                var memoryLoad = (double)gcInfo.MemoryLoadBytes / gcInfo.HighMemoryLoadThresholdBytes;

                // If system memory is under pressure, proactively evict
                if (memoryLoad > 0.9 && _storage.Count > 0)
                {
                    var targetEviction = Math.Max(1, _storage.Count / 10); // Evict 10%
                    EvictLruItems(targetEviction, EvictionReason.SystemMemoryPressure);
                }
            }
            catch
            {
                // GC info may not be available on all platforms
            }
        }

        /// <summary>
        /// Manually evicts the specified number of LRU items.
        /// </summary>
        public int EvictLruItems(int count, EvictionReason reason = EvictionReason.Manual)
        {
            var evictedItems = new List<EvictionEvent>();

            lock (_evictionLock)
            {
                var toEvict = _storage.Values
                    .OrderBy(b => b.LastAccessedAt)
                    .Take(count)
                    .ToList();

                foreach (var item in toEvict)
                {
                    if (_storage.TryRemove(item.Key, out var evicted))
                    {
                        Interlocked.Add(ref _currentSizeBytes, -evicted.Data.LongLength);

                        evictedItems.Add(new EvictionEvent
                        {
                            Key = evicted.Key,
                            Uri = evicted.Uri,
                            SizeBytes = evicted.Data.LongLength,
                            Reason = reason,
                            EvictedAt = DateTime.UtcNow,
                            Age = DateTime.UtcNow - evicted.CreatedAt,
                            TimeSinceLastAccess = DateTime.UtcNow - evicted.LastAccessedAt
                        });
                    }
                }
            }

            NotifyEvictionCallbacks(evictedItems);
            return evictedItems.Count;
        }

        /// <summary>
        /// Evicts items older than the specified age.
        /// </summary>
        public int EvictOlderThan(TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            var evictedItems = new List<EvictionEvent>();

            lock (_evictionLock)
            {
                var toEvict = _storage.Values
                    .Where(b => b.LastAccessedAt < cutoff)
                    .ToList();

                foreach (var item in toEvict)
                {
                    if (_storage.TryRemove(item.Key, out var evicted))
                    {
                        Interlocked.Add(ref _currentSizeBytes, -evicted.Data.LongLength);

                        evictedItems.Add(new EvictionEvent
                        {
                            Key = evicted.Key,
                            Uri = evicted.Uri,
                            SizeBytes = evicted.Data.LongLength,
                            Reason = EvictionReason.Expired,
                            EvictedAt = DateTime.UtcNow,
                            Age = DateTime.UtcNow - evicted.CreatedAt,
                            TimeSinceLastAccess = DateTime.UtcNow - evicted.LastAccessedAt
                        });
                    }
                }
            }

            NotifyEvictionCallbacks(evictedItems);
            return evictedItems.Count;
        }

        /// <summary>
        /// Check if data exists in memory.
        /// </summary>
        public override Task<bool> ExistsAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var key = GetKey(uri);
            return Task.FromResult(_storage.ContainsKey(key));
        }

        /// <summary>
        /// List all stored items.
        /// Returns SDK's StorageListItem (Uri, SizeBytes).
        /// </summary>
        public async IAsyncEnumerable<StorageListItem> ListFilesAsync(
            string prefix = "",
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kvp in _storage)
            {
                if (ct.IsCancellationRequested)
                    yield break;

                if (string.IsNullOrEmpty(prefix) || kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Return SDK's StorageListItem record
                    yield return new StorageListItem(kvp.Value.Uri, kvp.Value.Data.LongLength);
                }

                await Task.Yield(); // Allow cancellation between items
            }
        }

        /// <summary>
        /// Clear all stored data.
        /// </summary>
        public void Clear()
        {
            _storage.Clear();
        }

        /// <summary>
        /// Get statistics about the storage.
        /// </summary>
        public InMemoryStorageStats GetStats()
        {
            var blobs = _storage.Values.ToList();
            return new InMemoryStorageStats
            {
                ItemCount = blobs.Count,
                TotalSizeBytes = blobs.Sum(b => b.Data.LongLength),
                OldestItem = blobs.Count > 0 ? blobs.Min(b => b.CreatedAt) : null,
                NewestItem = blobs.Count > 0 ? blobs.Max(b => b.CreatedAt) : null
            };
        }

        private static string GetKey(Uri uri)
        {
            // Use the path as key, removing leading slashes
            return uri.AbsolutePath.TrimStart('/');
        }

        private sealed class StoredBlob
        {
            public string Key { get; init; } = string.Empty;
            public Uri Uri { get; init; } = null!;
            public byte[] Data { get; init; } = [];
            public DateTime CreatedAt { get; init; }
            public DateTime LastAccessedAt { get; set; }
            public long AccessCount { get; set; }
        }
    }

    /// <summary>
    /// Configuration for in-memory storage.
    /// </summary>
    public class InMemoryStorageConfig
    {
        /// <summary>
        /// Maximum total memory allowed in bytes (null = unlimited).
        /// </summary>
        public long? MaxMemoryBytes { get; set; }

        /// <summary>
        /// Maximum number of items allowed (null = unlimited).
        /// </summary>
        public int? MaxItemCount { get; set; }

        /// <summary>
        /// Memory utilization percentage at which to report memory pressure (0-100).
        /// Default: 80%
        /// </summary>
        public double MemoryPressureThreshold { get; set; } = 80;

        /// <summary>
        /// Creates a default unlimited configuration.
        /// </summary>
        public static InMemoryStorageConfig Unlimited => new();

        /// <summary>
        /// Creates a configuration for a small cache (100MB, 10K items).
        /// </summary>
        public static InMemoryStorageConfig SmallCache => new()
        {
            MaxMemoryBytes = 100 * 1024 * 1024,
            MaxItemCount = 10_000
        };

        /// <summary>
        /// Creates a configuration for a medium cache (1GB, 100K items).
        /// </summary>
        public static InMemoryStorageConfig MediumCache => new()
        {
            MaxMemoryBytes = 1024L * 1024 * 1024,
            MaxItemCount = 100_000
        };

        /// <summary>
        /// Creates a configuration for a large cache (10GB, 1M items).
        /// </summary>
        public static InMemoryStorageConfig LargeCache => new()
        {
            MaxMemoryBytes = 10L * 1024 * 1024 * 1024,
            MaxItemCount = 1_000_000
        };
    }

    /// <summary>
    /// In-memory storage statistics.
    /// </summary>
    public class InMemoryStorageStats
    {
        public int ItemCount { get; init; }
        public long TotalSizeBytes { get; init; }
        public long? MaxMemoryBytes { get; init; }
        public int? MaxItemCount { get; init; }
        public double MemoryUtilization { get; init; }
        public bool IsUnderPressure { get; init; }
        public DateTime? OldestItem { get; init; }
        public DateTime? NewestItem { get; init; }
        public long TotalEvictions { get; init; }
    }

    /// <summary>
    /// Event raised when an item is evicted from storage.
    /// </summary>
    public class EvictionEvent
    {
        public string Key { get; init; } = string.Empty;
        public Uri Uri { get; init; } = null!;
        public long SizeBytes { get; init; }
        public EvictionReason Reason { get; init; }
        public DateTime EvictedAt { get; init; }
        public TimeSpan Age { get; init; }
        public TimeSpan TimeSinceLastAccess { get; init; }
    }

    /// <summary>
    /// Reason for eviction.
    /// </summary>
    public enum EvictionReason
    {
        /// <summary>Manual eviction request.</summary>
        Manual,
        /// <summary>Evicted due to memory limit.</summary>
        MemoryLimit,
        /// <summary>Evicted due to item count limit.</summary>
        ItemCountLimit,
        /// <summary>Evicted due to storage memory pressure.</summary>
        MemoryPressure,
        /// <summary>Evicted due to system memory pressure.</summary>
        SystemMemoryPressure,
        /// <summary>Evicted due to expiration.</summary>
        Expired
    }
}
