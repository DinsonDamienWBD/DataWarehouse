using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Plugins
{
    /// <summary>
    /// Built-in in-memory storage plugin for testing and development.
    ///
    /// Features:
    /// - Volatile storage (data lost on shutdown)
    /// - Thread-safe concurrent access
    /// - No persistence
    /// - No security features
    /// - Fast for testing concepts and functionality
    ///
    /// NOTE: This is intentionally basic. For production, use a proper
    /// storage plugin (Local, S3, Azure Blob, etc.)
    /// </summary>
    public sealed class InMemoryStoragePlugin : StorageProviderPluginBase, IListableStorage
    {
        private readonly ConcurrentDictionary<string, StoredBlob> _storage = new();

        public override string Id => "datawarehouse.kernel.storage.inmemory";
        public override string Name => "In-Memory Storage";
        public override string Version => "1.0.0";
        public override string Scheme => "memory";

        /// <summary>
        /// Number of items currently stored.
        /// </summary>
        public int Count => _storage.Count;

        /// <summary>
        /// Total size of all stored data in bytes.
        /// </summary>
        public long TotalSizeBytes => _storage.Values.Sum(b => b.Data.LongLength);

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { DisplayName = "Save", Description = "Store data in memory" },
                new() { DisplayName = "Load", Description = "Retrieve data from memory" },
                new() { DisplayName = "Delete", Description = "Remove data from memory" },
                new() { DisplayName = "List", Description = "Enumerate stored items" }
            };
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Description"] = "Volatile in-memory storage for testing. Data is lost on shutdown.";
            metadata["Volatile"] = true;
            metadata["MaxItemSize"] = int.MaxValue;
            metadata["SupportsConcurrency"] = true;
            metadata["SupportsListing"] = true;
            metadata["PreferredMode"] = "Workstation";
            return metadata;
        }

        /// <summary>
        /// Save data to memory.
        /// </summary>
        public override async Task SaveAsync(Uri uri, Stream data)
        {
            ArgumentNullException.ThrowIfNull(uri);
            ArgumentNullException.ThrowIfNull(data);

            var key = GetKey(uri);

            using var ms = new MemoryStream();
            await data.CopyToAsync(ms);
            var bytes = ms.ToArray();

            _storage[key] = new StoredBlob
            {
                Key = key,
                Uri = uri,
                Data = bytes,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Load data from memory.
        /// </summary>
        public override Task<Stream> LoadAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var key = GetKey(uri);

            if (!_storage.TryGetValue(key, out var blob))
            {
                throw new FileNotFoundException($"Item not found in memory storage: {key}");
            }

            blob.LastAccessedAt = DateTime.UtcNow;
            return Task.FromResult<Stream>(new MemoryStream(blob.Data));
        }

        /// <summary>
        /// Delete data from memory.
        /// </summary>
        public override Task DeleteAsync(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            var key = GetKey(uri);
            _storage.TryRemove(key, out _);

            return Task.CompletedTask;
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
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public DateTime CreatedAt { get; init; }
            public DateTime LastAccessedAt { get; set; }
        }
    }

    /// <summary>
    /// In-memory storage statistics.
    /// </summary>
    public class InMemoryStorageStats
    {
        public int ItemCount { get; init; }
        public long TotalSizeBytes { get; init; }
        public DateTime? OldestItem { get; init; }
        public DateTime? NewestItem { get; init; }
    }
}
