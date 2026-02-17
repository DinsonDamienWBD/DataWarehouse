# FIX-13 Storage Chain Feature Extraction

## Source: Legacy Storage Chain Classes (PluginBase.cs lines 1433-1975)

### 1. ListableStoragePluginBase (lines 1433-1447)

**Features:**
- `IAsyncEnumerable<StorageListItem> ListFilesAsync(string prefix, CancellationToken ct)` - list files matching prefix

**Assessment:** Already covered by new StoragePluginBase's `ListAsync` method. No additional porting needed.

### 2. TieredStoragePluginBase (lines 1453-1476)

**Features:**
- `Task<string> MoveToTierAsync(Manifest manifest, StorageTier targetTier)` - move blob to tier
- `Task<StorageTier> GetCurrentTierAsync(Uri uri)` - get current tier

**Assessment:** Tiering is a separate concern. The new hierarchy already has `PredictAccessPatternAsync` for tiering hints. Full tiering should be an opt-in feature.

### 3. CacheableStoragePluginBase (lines 1485-1713)

**Features:**
- `ConcurrentDictionary<string, CacheEntryMetadata> _cacheMetadata` - cache metadata store
- `Timer? _cleanupTimer` - automatic cleanup
- `CacheOptions DefaultCacheOptions` - configurable TTL (1hr), max entries (10K), LRU eviction
- `SaveWithTtlAsync(Uri, Stream, TimeSpan, CancellationToken)` - save with TTL
- `GetTtlAsync(Uri, CancellationToken)` - get remaining TTL
- `SetTtlAsync(Uri, TimeSpan, CancellationToken)` - update TTL
- `InvalidatePatternAsync(string pattern, CancellationToken)` - regex-based invalidation
- `GetCacheStatisticsAsync(CancellationToken)` - cache stats (total, size, expired, hits)
- `CleanupExpiredAsync(CancellationToken)` - remove expired entries
- `TouchAsync(Uri, CancellationToken)` - update access time, increment hit count
- `RemoveTtlAsync(Uri, CancellationToken)` - make entry permanent
- `InvalidateByTagAsync(string tag, CancellationToken)` - tag-based invalidation
- Dispose: cleanup timer

### 4. IndexableStoragePluginBase (lines 1720-1975)

**Features:**
- `ConcurrentDictionary<string, Dictionary<string, object>> _indexStore` - in-memory index
- `MaxIndexStoreSize` - bounded (100K default)
- `IndexDocumentAsync(string id, Dictionary<string, object> metadata)` - add doc to index
- `RemoveFromIndexAsync(string id)` - remove from index
- `SearchIndexAsync(string query, int limit)` - text search (case-insensitive Contains)
- `QueryByMetadataAsync(Dictionary<string, object> criteria)` - exact-match metadata query
- `GetIndexStatisticsAsync()` - index stats (doc count, term count, size)
- `OptimizeIndexAsync()` - no-op (for derived classes)
- `RebuildIndexAsync()` - clear + re-index all files
- IMetadataIndex implementation: `IndexManifestAsync`, `SearchAsync`, `EnumerateAllAsync`, `UpdateLastAccessAsync`, `GetManifestAsync`, `ExecuteQueryAsync`

## Port Strategy

Port to new `StoragePluginBase` as **opt-in protected methods**:

### Caching (from CacheableStoragePluginBase)

Add opt-in caching region:
1. `EnableCaching(CacheConfiguration config)` - activate caching with options
2. `InvalidateCacheAsync(string key, CancellationToken)` - invalidate single key
3. `InvalidateCacheByPatternAsync(string pattern, CancellationToken)` - pattern invalidation
4. `InvalidateCacheByTagAsync(string tag, CancellationToken)` - tag invalidation
5. `GetCacheStatisticsAsync(CancellationToken)` - cache stats
6. `CleanupExpiredCacheAsync(CancellationToken)` - cleanup expired
7. Internal: ConcurrentDictionary for cache metadata, Timer for auto-cleanup

### Indexing (from IndexableStoragePluginBase)

Add opt-in indexing region:
1. `EnableIndexing(IndexConfiguration config)` - activate indexing with options
2. `IndexDocumentAsync(string id, Dictionary<string, object> metadata, CancellationToken)` - add to index
3. `RemoveFromIndexAsync(string id, CancellationToken)` - remove from index
4. `SearchIndexAsync(string query, int limit, CancellationToken)` - text search
5. `QueryByMetadataAsync(Dictionary<string, object> criteria, CancellationToken)` - metadata query
6. `GetIndexStatisticsAsync(CancellationToken)` - index stats
7. `RebuildIndexAsync(CancellationToken)` - rebuild
8. Internal: ConcurrentDictionary for index store, bounded

### Design Decisions

- All features are **opt-in** via `Enable*` methods (not enabled by default)
- Plugins must call `EnableCaching`/`EnableIndexing` in their constructor or InitializeAsync
- Configuration objects allow customizing TTL, max entries, eviction policy, etc.
- Thread-safe via ConcurrentDictionary
- Bounded collections (configurable limits)
- Dispose cleanup for timers
