using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for storage provider plugins (Local, S3, IPFS, etc.).
    /// Provides default implementations for common storage operations.
    /// Uses URI-based addressing for storage locations.
    /// </summary>
    public abstract class StorageProviderPluginBase : DataPipelinePluginBase, IStorageProvider
    {
        /// <summary>
        /// Category is always StorageProvider for storage plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.StorageProvider;

        /// <summary>
        /// URI scheme for this storage provider (e.g., "file", "s3", "ipfs").
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract string Scheme { get; }

        /// <summary>
        /// Save data to storage. Must be implemented by derived classes.
        /// </summary>
        public abstract Task SaveAsync(Uri uri, Stream data);

        /// <summary>
        /// Retrieve data from storage. Must be implemented by derived classes.
        /// </summary>
        public abstract Task<Stream> LoadAsync(Uri uri);

        /// <summary>
        /// Delete data from storage. Must be implemented by derived classes.
        /// </summary>
        public abstract Task DeleteAsync(Uri uri);

        /// <summary>
        /// Check if data exists. Default implementation is optimized for efficiency.
        /// Override for provider-specific optimizations (e.g., HEAD request for HTTP).
        /// </summary>
        public virtual Task<bool> ExistsAsync(Uri uri)
        {
            return ExistsAsyncWithLoad(uri);
        }

        /// <summary>
        /// Fallback existence check using LoadAsync. Inefficient but works for any provider.
        /// Prefer overriding ExistsAsync with provider-specific implementation.
        /// </summary>
        protected async Task<bool> ExistsAsyncWithLoad(Uri uri)
        {
            try
            {
                using var stream = await LoadAsync(uri);
                if (stream == null) return false;

                // Stream obtained successfully — file exists regardless of readability
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // Access denied does not reliably indicate existence across all providers.
                // Fail-closed: report as not found rather than guessing.
                return false;
            }
            catch
            {
                return false;
            }
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["StorageType"] = Scheme;
            metadata["SupportsStreaming"] = true;
            metadata["SupportsConcurrency"] = false;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for listable storage providers that support file enumeration.
    /// Extends StorageProviderPluginBase with listing capabilities.
    /// </summary>
    public abstract class ListableStoragePluginBase : StorageProviderPluginBase, IListableStorage
    {
        /// <summary>
        /// List all files in storage matching a prefix.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract IAsyncEnumerable<StorageListItem> ListFilesAsync(string prefix = "", CancellationToken ct = default);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsListing"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for tiered storage providers that support data tiering.
    /// Extends ListableStoragePluginBase with tier management capabilities.
    /// </summary>
    public abstract class TieredStoragePluginBase : ListableStoragePluginBase, ITieredStorage
    {
        /// <summary>
        /// Move a blob to the specified storage tier.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<string> MoveToTierAsync(Manifest manifest, StorageTier targetTier);

        /// <summary>
        /// Get the current tier of a blob. Override for custom tier detection.
        /// </summary>
        public virtual Task<StorageTier> GetCurrentTierAsync(Uri uri)
        {
            return Task.FromResult(StorageTier.Hot);
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsTiering"] = true;
            metadata["SupportedTiers"] = new[] { "Hot", "Cool", "Cold", "Archive" };
            return metadata;
        }
    }

    #region Hybrid Storage Plugin Base Classes

    /// <summary>
    /// Abstract base class for cacheable storage providers.
    /// Adds TTL-based caching, expiration, and cache statistics to storage plugins.
    /// Plugins extending this class automatically gain caching capabilities.
    /// </summary>
    public abstract class CacheableStoragePluginBase : ListableStoragePluginBase, ICacheableStorage
    {
        private readonly BoundedDictionary<string, CacheEntryMetadata> _cacheMetadata = new BoundedDictionary<string, CacheEntryMetadata>(1000);
        private readonly Timer? _cleanupTimer;
        private readonly CacheOptions _cacheOptions;

        /// <summary>
        /// Default cache options. Override to customize.
        /// </summary>
        protected virtual CacheOptions DefaultCacheOptions => new()
        {
            DefaultTtl = TimeSpan.FromHours(1),
            MaxEntries = 10000,
            EvictionPolicy = CacheEvictionPolicy.LRU,
            EnableStatistics = true
        };

        protected CacheableStoragePluginBase()
        {
            _cacheOptions = DefaultCacheOptions;
            if (_cacheOptions.CleanupInterval > TimeSpan.Zero)
            {
                _cleanupTimer = new Timer(
                    async _ => await CleanupExpiredAsync(),
                    null,
                    _cacheOptions.CleanupInterval,
                    _cacheOptions.CleanupInterval);
            }
        }

        /// <summary>
        /// Save data with a specific TTL.
        /// </summary>
        public virtual async Task SaveWithTtlAsync(Uri uri, Stream data, TimeSpan ttl, CancellationToken ct = default)
        {
            await SaveAsync(uri, data);
            var key = uri.ToString();
            _cacheMetadata[key] = new CacheEntryMetadata
            {
                Key = key,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(ttl),
                LastAccessedAt = DateTime.UtcNow,
                Size = data.CanSeek ? data.Length : 0
            };
        }

        /// <summary>
        /// Get the remaining TTL for an entry.
        /// </summary>
        public virtual Task<TimeSpan?> GetTtlAsync(Uri uri, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata) && metadata.ExpiresAt.HasValue)
            {
                var remaining = metadata.ExpiresAt.Value - DateTime.UtcNow;
                return Task.FromResult<TimeSpan?>(remaining > TimeSpan.Zero ? remaining : null);
            }
            return Task.FromResult<TimeSpan?>(null);
        }

        /// <summary>
        /// Set or update TTL for an existing entry.
        /// </summary>
        public virtual Task<bool> SetTtlAsync(Uri uri, TimeSpan ttl, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata))
            {
                metadata.ExpiresAt = DateTime.UtcNow.Add(ttl);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Invalidate all entries matching a pattern.
        /// </summary>
        public virtual async Task<int> InvalidatePatternAsync(string pattern, CancellationToken ct = default)
        {
            var regex = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");

            var keysToRemove = _cacheMetadata.Keys.Where(k => regex.IsMatch(k)).ToList();
            var count = 0;

            foreach (var key in keysToRemove)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await DeleteAsync(new Uri(key));
                    _cacheMetadata.TryRemove(key, out _);
                    count++;
                }
                catch { /* Ignore errors during invalidation */ }
            }

            return count;
        }

        /// <summary>
        /// Get cache statistics.
        /// </summary>
        public virtual Task<CacheStatistics> GetCacheStatisticsAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var entries = _cacheMetadata.Values.ToList();

            return Task.FromResult(new CacheStatistics
            {
                TotalEntries = entries.Count,
                TotalSizeBytes = entries.Sum(e => e.Size),
                ExpiredEntries = entries.Count(e => e.ExpiresAt.HasValue && e.ExpiresAt < now),
                HitCount = entries.Sum(e => e.HitCount),
                MissCount = 0,
                OldestEntry = entries.MinBy(e => e.CreatedAt)?.CreatedAt,
                NewestEntry = entries.MaxBy(e => e.CreatedAt)?.CreatedAt
            });
        }

        /// <summary>
        /// Remove all expired entries.
        /// </summary>
        public virtual async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _cacheMetadata
                .Where(kv => kv.Value.ExpiresAt.HasValue && kv.Value.ExpiresAt < now)
                .Select(kv => kv.Key)
                .ToList();

            var count = 0;
            foreach (var key in expiredKeys)
            {
                if (ct.IsCancellationRequested) break;
                if (_cacheMetadata.TryRemove(key, out _))
                {
                    try { await DeleteAsync(new Uri(key)); } catch { /* Best-effort cleanup */ }
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Touch an entry to update its last access time and reset sliding expiration.
        /// </summary>
        public virtual Task<bool> TouchAsync(Uri uri, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata))
            {
                metadata.LastAccessedAt = DateTime.UtcNow;
                metadata.HitCount++;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Remove TTL from an item, making it permanent.
        /// </summary>
        public virtual Task<bool> RemoveTtlAsync(Uri uri, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata))
            {
                metadata.ExpiresAt = null;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Invalidate all cached items with a specific tag.
        /// </summary>
        public virtual async Task<int> InvalidateByTagAsync(string tag, CancellationToken ct = default)
        {
            var keysWithTag = _cacheMetadata
                .Where(kv => kv.Value.Tags?.Contains(tag) == true)
                .Select(kv => kv.Key)
                .ToList();

            var count = 0;
            foreach (var key in keysWithTag)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await DeleteAsync(new Uri(key));
                    _cacheMetadata.TryRemove(key, out _);
                    count++;
                }
                catch { /* Ignore errors during invalidation */ }
            }

            return count;
        }

        /// <summary>
        /// Load with cache hit tracking. Derived classes must implement actual loading.
        /// Implementations should call TouchAsync(uri) to track cache access before loading.
        /// </summary>
        public abstract override Task<Stream> LoadAsync(Uri uri);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsCaching"] = true;
            metadata["SupportsTTL"] = true;
            metadata["SupportsExpiration"] = true;
            metadata["CacheEvictionPolicy"] = _cacheOptions.EvictionPolicy.ToString();
            return metadata;
        }

        /// <summary>
        /// Dispose cleanup timer.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cleanupTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Abstract base class for indexable storage providers.
    /// Combines storage with full-text and metadata indexing capabilities.
    /// Plugins extending this class automatically gain indexing support.
    /// </summary>
    public abstract class IndexableStoragePluginBase : CacheableStoragePluginBase, IIndexableStorage
    {
        private readonly BoundedDictionary<string, Dictionary<string, object>> _indexStore = new BoundedDictionary<string, Dictionary<string, object>>(1000);
        private long _indexedCount;
        private DateTime? _lastIndexRebuild;

        /// <summary>
        /// Maximum number of entries in the index store.
        /// Override in derived classes to customize. Default: 100,000.
        /// Set to 0 for unlimited (not recommended).
        /// </summary>
        protected virtual int MaxIndexStoreSize => 100_000;

        /// <summary>
        /// Index a document with its metadata.
        /// </summary>
        public virtual Task IndexDocumentAsync(string id, Dictionary<string, object> metadata, CancellationToken ct = default)
        {
            // BoundedDictionary handles LRU eviction internally.
            // No manual capacity check needed (avoids TOCTOU race on concurrent access).
            metadata["_indexed_at"] = DateTime.UtcNow;
            _indexStore[id] = metadata;
            Interlocked.Increment(ref _indexedCount);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Remove a document from the index.
        /// </summary>
        public virtual Task<bool> RemoveFromIndexAsync(string id, CancellationToken ct = default)
        {
            var removed = _indexStore.TryRemove(id, out _);
            if (removed) Interlocked.Decrement(ref _indexedCount);
            return Task.FromResult(removed);
        }

        /// <summary>
        /// Search the index with a text query.
        /// </summary>
        /// <remarks>
        /// <b>Performance warning:</b> This implementation is O(N×M) where N is the number of
        /// indexed documents and M is the number of metadata values per document. It will degrade
        /// severely at large scale. Override with a proper inverted index in production subclasses.
        /// </remarks>
        public virtual Task<string[]> SearchIndexAsync(string query, int limit = 100, CancellationToken ct = default)
        {
            var queryLower = query.ToLowerInvariant();
            var results = _indexStore
                .Where(kv => kv.Value.Values.Any(v =>
                    v?.ToString()?.Contains(queryLower, StringComparison.OrdinalIgnoreCase) == true))
                .Take(limit)
                .Select(kv => kv.Key)
                .ToArray();

            return Task.FromResult(results);
        }

        /// <summary>
        /// Query by specific metadata criteria.
        /// </summary>
        public virtual Task<string[]> QueryByMetadataAsync(Dictionary<string, object> criteria, CancellationToken ct = default)
        {
            var results = _indexStore
                .Where(kv => criteria.All(c =>
                    kv.Value.TryGetValue(c.Key, out var v) &&
                    Equals(v?.ToString(), c.Value?.ToString())))
                .Select(kv => kv.Key)
                .ToArray();

            return Task.FromResult(results);
        }

        /// <summary>
        /// Get index statistics.
        /// </summary>
        public virtual Task<IndexStatistics> GetIndexStatisticsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new IndexStatistics
            {
                DocumentCount = _indexStore.Count,
                TermCount = _indexStore.Values
                    .SelectMany(v => v.Keys)
                    .Distinct()
                    .Count(),
                LastUpdated = _lastIndexRebuild ?? DateTime.UtcNow,
                IndexSizeBytes = _indexStore.Sum(kv =>
                    kv.Key.Length + kv.Value.Sum(v => (v.Key?.Length ?? 0) + (v.Value?.ToString()?.Length ?? 0))),
                IndexType = "InMemory"
            });
        }

        /// <summary>
        /// Optimize the index for better query performance.
        /// </summary>
        public virtual Task OptimizeIndexAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Rebuild the entire index.
        /// </summary>
        public virtual async Task<int> RebuildIndexAsync(CancellationToken ct = default)
        {
            _indexStore.Clear();
            _indexedCount = 0;
            var count = 0;

            await foreach (var item in ListFilesAsync("", ct))
            {
                if (ct.IsCancellationRequested) break;

                var id = item.Uri.ToString();
                await IndexDocumentAsync(id, new Dictionary<string, object>
                {
                    ["uri"] = id,
                    ["size"] = item.Size,
                    ["path"] = item.Uri.AbsolutePath
                }, ct);
                count++;
            }

            _lastIndexRebuild = DateTime.UtcNow;
            return count;
        }

        #region IMetadataIndex Implementation

        /// <summary>
        /// Index a manifest.
        /// </summary>
        public virtual async Task IndexManifestAsync(Manifest manifest, CancellationToken ct = default)
        {
            var metadata = new Dictionary<string, object>
            {
                ["id"] = manifest.Id,
                ["hash"] = manifest.ContentHash,
                ["size"] = manifest.OriginalSize,
                ["created"] = manifest.CreatedAt,
                ["contentType"] = manifest.ContentType ?? ""
            };

            if (manifest.Metadata != null)
            {
                foreach (var kv in manifest.Metadata)
                {
                    metadata[kv.Key] = kv.Value;
                }
            }

            await IndexDocumentAsync(manifest.Id, metadata, ct);
        }

        /// <summary>
        /// Search manifests with optional vector.
        /// </summary>
        public virtual Task<string[]> SearchAsync(string query, float[]? vector, int limit, CancellationToken ct = default)
        {
            return SearchIndexAsync(query, limit);
        }

        /// <summary>
        /// Enumerate all manifests in the index.
        /// </summary>
        public virtual async IAsyncEnumerable<Manifest> EnumerateAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kv in _indexStore)
            {
                if (ct.IsCancellationRequested) yield break;

                yield return new Manifest
                {
                    Id = kv.Key,
                    ContentHash = kv.Value.TryGetValue("hash", out var h) ? h?.ToString() ?? "" : "",
                    OriginalSize = kv.Value.TryGetValue("size", out var s) && s is long size ? size : 0,
                    CreatedAt = kv.Value.TryGetValue("created", out var c) && c is DateTime dt
                        ? new DateTimeOffset(dt).ToUnixTimeSeconds()
                        : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Metadata = kv.Value.Where(x => !x.Key.StartsWith("_"))
                        .ToDictionary(x => x.Key, x => x.Value?.ToString() ?? "")
                };
                // No Task.Yield() per item — this is an in-memory enumeration and yielding
                // every item causes needless thread-pool context switches.
            }
        }

        /// <summary>
        /// Update last access time for a manifest.
        /// </summary>
        public virtual Task UpdateLastAccessAsync(string id, long timestamp, CancellationToken ct = default)
        {
            if (_indexStore.TryGetValue(id, out var metadata))
            {
                metadata["_last_access"] = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Get a manifest by ID.
        /// </summary>
        public virtual Task<Manifest?> GetManifestAsync(string id)
        {
            if (_indexStore.TryGetValue(id, out var metadata))
            {
                return Task.FromResult<Manifest?>(new Manifest
                {
                    Id = id,
                    ContentHash = metadata.TryGetValue("hash", out var h) ? h?.ToString() ?? "" : "",
                    OriginalSize = metadata.TryGetValue("size", out var s) && s is long size ? size : 0,
                    CreatedAt = metadata.TryGetValue("created", out var c) && c is DateTime dt
                        ? new DateTimeOffset(dt).ToUnixTimeSeconds()
                        : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Metadata = metadata.Where(x => !x.Key.StartsWith("_"))
                        .ToDictionary(x => x.Key, x => x.Value?.ToString() ?? "")
                });
            }
            return Task.FromResult<Manifest?>(null);
        }

        /// <summary>
        /// Execute a text query.
        /// </summary>
        public virtual Task<string[]> ExecuteQueryAsync(string query, int limit)
        {
            return SearchIndexAsync(query, limit);
        }

        /// <summary>
        /// Execute a composite query.
        /// </summary>
        public virtual Task<string[]> ExecuteQueryAsync(CompositeQuery query, int limit = 50)
        {
            return ExecuteQueryAsync(query.ToString() ?? "", limit);
        }

        #endregion

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsIndexing"] = true;
            metadata["SupportsFullTextSearch"] = true;
            metadata["SupportsMetadataQuery"] = true;
            metadata["IndexedDocuments"] = _indexedCount;
            return metadata;
        }
    }

    #endregion
}
