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
                new() { Name = "Save", Description = "Store data in memory" },
                new() { Name = "Load", Description = "Retrieve data from memory" },
                new() { Name = "Delete", Description = "Remove data from memory" },
                new() { Name = "List", Description = "Enumerate stored items" }
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
                Data = bytes,
                CreatedAt = DateTime.UtcNow,
                ContentType = GetContentTypeFromUri(uri)
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
                    yield return new StorageListItem
                    {
                        Key = kvp.Key,
                        Size = kvp.Value.Data.LongLength,
                        LastModified = kvp.Value.CreatedAt,
                        ContentType = kvp.Value.ContentType
                    };
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
        public StorageStats GetStats()
        {
            return new StorageStats
            {
                ItemCount = _storage.Count,
                TotalSizeBytes = TotalSizeBytes,
                OldestItem = _storage.Values.Min(b => b.CreatedAt),
                NewestItem = _storage.Values.Max(b => b.CreatedAt)
            };
        }

        private static string GetKey(Uri uri)
        {
            // Use the path as key, removing leading slashes
            return uri.AbsolutePath.TrimStart('/');
        }

        private static string GetContentTypeFromUri(Uri uri)
        {
            var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            return extension switch
            {
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".txt" => "text/plain",
                ".html" => "text/html",
                ".css" => "text/css",
                ".js" => "application/javascript",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                _ => "application/octet-stream"
            };
        }

        private sealed class StoredBlob
        {
            public string Key { get; init; } = string.Empty;
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public DateTime CreatedAt { get; init; }
            public DateTime LastAccessedAt { get; set; }
            public string ContentType { get; init; } = "application/octet-stream";
        }
    }

    /// <summary>
    /// Item returned when listing storage contents.
    /// </summary>
    public class StorageListItem
    {
        public string Key { get; init; } = string.Empty;
        public long Size { get; init; }
        public DateTime LastModified { get; init; }
        public string ContentType { get; init; } = "application/octet-stream";
    }

    /// <summary>
    /// Storage statistics.
    /// </summary>
    public class StorageStats
    {
        public int ItemCount { get; init; }
        public long TotalSizeBytes { get; init; }
        public DateTime? OldestItem { get; init; }
        public DateTime? NewestItem { get; init; }
    }
}
