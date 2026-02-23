using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;
using Xunit;

namespace DataWarehouse.Tests.Scaling
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="BoundedCache{TKey,TValue}"/>
    /// covering LRU, ARC, and TTL eviction, backing store integration,
    /// write-through, auto-sizing, thread safety, statistics, and OnEvicted events.
    /// </summary>
    public class BoundedCacheTests
    {
        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static BoundedCacheOptions<string, string> LruOptions(int max = 5)
            => new()
            {
                MaxEntries = max,
                EvictionPolicy = CacheEvictionMode.LRU,
            };

        private static BoundedCacheOptions<string, string> ArcOptions(int max = 5)
            => new()
            {
                MaxEntries = max,
                EvictionPolicy = CacheEvictionMode.ARC,
            };

        private static BoundedCacheOptions<string, string> TtlOptions(TimeSpan ttl, int max = 1000)
            => new()
            {
                MaxEntries = max,
                EvictionPolicy = CacheEvictionMode.TTL,
                DefaultTtl = ttl,
            };

        private static BoundedCacheOptions<string, string> BackedOptions(
            InMemoryBackingStore store, int max = 5, bool writeThrough = false)
            => new()
            {
                MaxEntries = max,
                EvictionPolicy = CacheEvictionMode.LRU,
                BackingStore = store,
                BackingStorePath = "dw://test/cache/",
                Serializer = v => Encoding.UTF8.GetBytes(v),
                Deserializer = b => Encoding.UTF8.GetString(b),
                KeyToString = k => k,
                WriteThrough = writeThrough,
            };

        // ----------------------------------------------------------------
        // 1. LRU eviction
        // ----------------------------------------------------------------

        [Fact]
        public void LRU_OldestItemEvictedWhenCapacityExceeded()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(3));

            cache.Put("a", "1");
            cache.Put("b", "2");
            cache.Put("c", "3");
            Assert.Equal(3, cache.Count);

            // Insert 4th item -- should evict "a" (oldest/LRU)
            cache.Put("d", "4");
            Assert.Equal(3, cache.Count);
            Assert.Null(cache.GetOrDefault("a"));
            Assert.Equal("4", cache.GetOrDefault("d"));
        }

        [Fact]
        public void LRU_AccessPromotesItemPreventingEviction()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(3));

            cache.Put("a", "1");
            cache.Put("b", "2");
            cache.Put("c", "3");

            // Access "a" to promote it
            _ = cache.GetOrDefault("a");

            // Insert "d" -- "b" should be evicted (now the LRU), not "a"
            cache.Put("d", "4");
            Assert.Equal("1", cache.GetOrDefault("a"));
            Assert.Null(cache.GetOrDefault("b"));
        }

        [Fact]
        public void LRU_UpdateExistingKeyDoesNotEvict()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(3));

            cache.Put("a", "1");
            cache.Put("b", "2");
            cache.Put("c", "3");

            // Update existing key -- should not evict anything
            cache.Put("a", "updated");
            Assert.Equal(3, cache.Count);
            Assert.Equal("updated", cache.GetOrDefault("a"));
        }

        // ----------------------------------------------------------------
        // 2. ARC eviction
        // ----------------------------------------------------------------

        [Fact]
        public void ARC_BasicInsertAndRetrieve()
        {
            using var cache = new BoundedCache<string, string>(ArcOptions(5));

            cache.Put("a", "1");
            cache.Put("b", "2");
            Assert.Equal("1", cache.GetOrDefault("a"));
            Assert.Equal("2", cache.GetOrDefault("b"));
            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public void ARC_EvictsWhenCapacityExceeded()
        {
            using var cache = new BoundedCache<string, string>(ArcOptions(3));

            cache.Put("a", "1");
            cache.Put("b", "2");
            cache.Put("c", "3");

            // At capacity -- inserting 4th should evict one
            cache.Put("d", "4");
            Assert.True(cache.Count <= 3);
            Assert.Equal("4", cache.GetOrDefault("d"));
        }

        [Fact]
        public void ARC_GhostListAdaptation_RecencyPattern()
        {
            // Access pattern that favors recency: sequential unique keys
            // should increase T1 target via B1 hits
            using var cache = new BoundedCache<string, string>(ArcOptions(10));

            // Fill cache
            for (int i = 0; i < 10; i++)
                cache.Put($"k{i}", $"v{i}");

            // Overflow with new keys (causes evictions to B1 ghost list)
            for (int i = 10; i < 20; i++)
                cache.Put($"k{i}", $"v{i}");

            // Now re-insert keys that should be in B1 ghost list
            // This exercises the B1 ghost hit path which increases p (favors T1)
            for (int i = 0; i < 5; i++)
                cache.Put($"k{i}", $"v{i}_readd");

            // Verify readded keys are accessible
            for (int i = 0; i < 5; i++)
                Assert.Equal($"v{i}_readd", cache.GetOrDefault($"k{i}"));
        }

        [Fact]
        public void ARC_FrequencyPattern_PromotesToT2()
        {
            // Accessing the same key twice promotes from T1 to T2
            using var cache = new BoundedCache<string, string>(ArcOptions(10));

            cache.Put("freq", "value");
            // First access in T1, second read promotes to T2
            _ = cache.GetOrDefault("freq");

            // Fill to capacity with other keys
            for (int i = 0; i < 9; i++)
                cache.Put($"other{i}", $"v{i}");

            // Overflow -- "freq" should survive because it's in T2 (frequency)
            for (int i = 9; i < 15; i++)
                cache.Put($"other{i}", $"v{i}");

            Assert.Equal("value", cache.GetOrDefault("freq"));
        }

        [Fact]
        public void ARC_TryRemoveFromT1AndT2()
        {
            using var cache = new BoundedCache<string, string>(ArcOptions(10));

            cache.Put("t1key", "t1val");
            cache.Put("t2key", "t2val");
            // Promote t2key to T2 by accessing it
            _ = cache.GetOrDefault("t2key");

            Assert.True(cache.TryRemove("t1key", out var v1));
            Assert.Equal("t1val", v1);

            Assert.True(cache.TryRemove("t2key", out var v2));
            Assert.Equal("t2val", v2);

            Assert.False(cache.TryRemove("nonexistent", out _));
        }

        // ----------------------------------------------------------------
        // 3. TTL eviction
        // ----------------------------------------------------------------

        [Fact]
        public void TTL_ItemAvailableImmediatelyAfterInsert()
        {
            using var cache = new BoundedCache<string, string>(TtlOptions(TimeSpan.FromSeconds(10)));

            cache.Put("key", "value");
            Assert.Equal("value", cache.GetOrDefault("key"));
        }

        [Fact]
        public async Task TTL_ItemExpiredAfterTtlElapsed()
        {
            using var cache = new BoundedCache<string, string>(TtlOptions(TimeSpan.FromMilliseconds(100)));

            cache.Put("key", "value");
            Assert.Equal("value", cache.GetOrDefault("key"));

            // Wait for TTL to expire
            await Task.Delay(200);

            // Lazy eviction on access should return null
            Assert.Null(cache.GetOrDefault("key"));
        }

        [Fact]
        public async Task TTL_BackgroundCleanupRemovesExpiredItems()
        {
            // Background timer fires at max(TTL/4, 1s) -- use 100ms TTL for fast test
            // Timer fires at 1s minimum, so we need to wait for cleanup
            using var cache = new BoundedCache<string, string>(TtlOptions(TimeSpan.FromMilliseconds(100)));

            cache.Put("expired1", "val1");
            cache.Put("expired2", "val2");
            Assert.Equal(2, cache.Count);

            // Wait for expiry + cleanup timer interval (1s min)
            await Task.Delay(1500);

            // Background cleanup should have removed expired entries
            Assert.Equal(0, cache.Count);
        }

        // ----------------------------------------------------------------
        // 4. Backing store integration
        // ----------------------------------------------------------------

        [Fact]
        public async Task BackingStore_EvictedItemsStoredInBackingStore()
        {
            var store = new InMemoryBackingStore();
            using var cache = new BoundedCache<string, string>(BackedOptions(store, max: 3, writeThrough: true));

            // Insert 3 items with write-through
            await cache.PutAsync("a", "1");
            await cache.PutAsync("b", "2");
            await cache.PutAsync("c", "3");

            // All 3 should be in backing store (write-through)
            Assert.NotNull(await store.ReadAsync("dw://test/cache/a"));
            Assert.NotNull(await store.ReadAsync("dw://test/cache/b"));

            // Insert 4th -- "a" evicted
            await cache.PutAsync("d", "4");

            // "a" should be in backing store (written on eviction and write-through)
            var data = await store.ReadAsync("dw://test/cache/a");
            Assert.NotNull(data);
            Assert.Equal("1", Encoding.UTF8.GetString(data!));
        }

        [Fact]
        public async Task BackingStore_CacheMissFallsBackToBackingStore()
        {
            var store = new InMemoryBackingStore();
            using var cache = new BoundedCache<string, string>(BackedOptions(store, max: 2));

            // Pre-populate backing store directly
            await store.WriteAsync("dw://test/cache/preloaded", Encoding.UTF8.GetBytes("from_store"));

            // GetAsync should fall back to backing store on cache miss
            var value = await cache.GetAsync("preloaded");
            Assert.Equal("from_store", value);

            // After lazy-load, should be in cache
            Assert.Equal("from_store", cache.GetOrDefault("preloaded"));
        }

        // ----------------------------------------------------------------
        // 5. Write-through
        // ----------------------------------------------------------------

        [Fact]
        public async Task WriteThrough_PutAsyncWritesToBackingStoreImmediately()
        {
            var store = new InMemoryBackingStore();
            using var cache = new BoundedCache<string, string>(BackedOptions(store, max: 10, writeThrough: true));

            await cache.PutAsync("key1", "val1");
            await cache.PutAsync("key2", "val2");

            // Both should be in backing store immediately
            var d1 = await store.ReadAsync("dw://test/cache/key1");
            var d2 = await store.ReadAsync("dw://test/cache/key2");
            Assert.NotNull(d1);
            Assert.NotNull(d2);
            Assert.Equal("val1", Encoding.UTF8.GetString(d1!));
            Assert.Equal("val2", Encoding.UTF8.GetString(d2!));
        }

        [Fact]
        public async Task WriteThrough_DisabledDoesNotWriteOnPut()
        {
            var store = new InMemoryBackingStore();
            var opts = BackedOptions(store, max: 10, writeThrough: false);
            using var cache = new BoundedCache<string, string>(opts);

            await cache.PutAsync("key1", "val1");

            // Should NOT be in backing store (write-through disabled)
            var data = await store.ReadAsync("dw://test/cache/key1");
            Assert.Null(data);
        }

        // ----------------------------------------------------------------
        // 6. Auto-sizing
        // ----------------------------------------------------------------

        [Fact]
        public void AutoSizing_MaxEntriesComputedFromRam()
        {
            var opts = new BoundedCacheOptions<string, string>
            {
                AutoSizeFromRam = true,
                RamPercentage = 0.1,
                EvictionPolicy = CacheEvictionMode.LRU,
            };
            using var cache = new BoundedCache<string, string>(opts);

            // Auto-sized MaxEntries should be > 0 and proportional to available RAM
            // We can verify by inserting many items -- cache should accept them
            // up to the auto-computed limit
            cache.Put("test", "value");
            Assert.Equal("value", cache.GetOrDefault("test"));
            // Count should be at least 1 (auto-size computed a positive MaxEntries)
            Assert.True(cache.Count >= 1);
        }

        // ----------------------------------------------------------------
        // 7. Thread safety
        // ----------------------------------------------------------------

        [Fact]
        public async Task ThreadSafety_ConcurrentGetPutRemoveNoExceptions()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(100));
            var random = new Random(42);
            var exceptions = new ConcurrentBag<Exception>();

            var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            {
                try
                {
                    var key = $"key{i % 20}";
                    var value = $"value{i}";

                    cache.Put(key, value);
                    _ = cache.GetOrDefault(key);
                    cache.TryRemove($"key{(i + 5) % 20}", out _);
                    _ = cache.ContainsKey(key);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);
            // Cache should be in consistent state
            Assert.True(cache.Count >= 0 && cache.Count <= 100);
        }

        [Fact]
        public async Task ThreadSafety_ARC_ConcurrentAccessNoExceptions()
        {
            using var cache = new BoundedCache<string, string>(ArcOptions(50));
            var exceptions = new ConcurrentBag<Exception>();

            var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            {
                try
                {
                    var key = $"key{i % 30}";
                    cache.Put(key, $"value{i}");
                    _ = cache.GetOrDefault(key);
                    _ = cache.ContainsKey($"key{(i + 10) % 30}");
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);
            Assert.True(cache.Count >= 0 && cache.Count <= 50);
        }

        // ----------------------------------------------------------------
        // 8. Statistics
        // ----------------------------------------------------------------

        [Fact]
        public void Statistics_HitMissCountsIncrementCorrectly()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(10));

            cache.Put("a", "1");
            cache.Put("b", "2");

            // Hits
            _ = cache.GetOrDefault("a"); // hit
            _ = cache.GetOrDefault("b"); // hit

            // Misses
            _ = cache.GetOrDefault("nonexistent1"); // miss
            _ = cache.GetOrDefault("nonexistent2"); // miss
            _ = cache.GetOrDefault("nonexistent3"); // miss

            var stats = cache.GetStatistics();
            Assert.Equal(2, stats.Hits);
            Assert.Equal(3, stats.Misses);
        }

        [Fact]
        public void Statistics_EvictionCountMatchesExpected()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(3));

            cache.Put("a", "1");
            cache.Put("b", "2");
            cache.Put("c", "3");
            // 3 evictions expected (one for each new entry past capacity)
            cache.Put("d", "4"); // evicts "a"
            cache.Put("e", "5"); // evicts "b"
            cache.Put("f", "6"); // evicts "c"

            var stats = cache.GetStatistics();
            Assert.Equal(3, stats.Evictions);
        }

        [Fact]
        public void Statistics_EstimatedMemoryBytesProportionalToCount()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(100));

            for (int i = 0; i < 50; i++)
                cache.Put($"k{i}", $"v{i}");

            Assert.Equal(50, cache.Count);
            // 256 bytes per entry heuristic
            Assert.Equal(50L * 256, cache.EstimatedMemoryBytes);
        }

        // ----------------------------------------------------------------
        // 9. OnEvicted event
        // ----------------------------------------------------------------

        [Fact]
        public void OnEvicted_FiresWithCorrectKeyValue()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(2));

            var evicted = new List<(string key, string value)>();
            cache.OnEvicted += (key, value) => evicted.Add((key, value));

            cache.Put("a", "1");
            cache.Put("b", "2");
            cache.Put("c", "3"); // should evict "a"

            Assert.Single(evicted);
            Assert.Equal("a", evicted[0].key);
            Assert.Equal("1", evicted[0].value);
        }

        [Fact]
        public void OnEvicted_FiresForMultipleEvictions()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(2));

            var evictedKeys = new List<string>();
            cache.OnEvicted += (key, _) => evictedKeys.Add(key);

            cache.Put("a", "1");
            cache.Put("b", "2");
            cache.Put("c", "3"); // evicts "a"
            cache.Put("d", "4"); // evicts "b"

            Assert.Equal(2, evictedKeys.Count);
            Assert.Contains("a", evictedKeys);
            Assert.Contains("b", evictedKeys);
        }

        // ----------------------------------------------------------------
        // Additional edge cases
        // ----------------------------------------------------------------

        [Fact]
        public void ContainsKey_ReturnsTrueForExistingKey()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(5));
            cache.Put("exists", "val");
            Assert.True(cache.ContainsKey("exists"));
            Assert.False(cache.ContainsKey("missing"));
        }

        [Fact]
        public void Enumeration_ReturnsAllEntries()
        {
            using var cache = new BoundedCache<string, string>(LruOptions(10));
            cache.Put("a", "1");
            cache.Put("b", "2");
            cache.Put("c", "3");

            var items = cache.ToList();
            Assert.Equal(3, items.Count);
            Assert.Contains(items, kvp => kvp.Key == "a" && kvp.Value == "1");
            Assert.Contains(items, kvp => kvp.Key == "b" && kvp.Value == "2");
            Assert.Contains(items, kvp => kvp.Key == "c" && kvp.Value == "3");
        }

        [Fact]
        public void ARC_Enumeration_ReturnsAllEntries()
        {
            using var cache = new BoundedCache<string, string>(ArcOptions(10));
            cache.Put("x", "10");
            cache.Put("y", "20");
            // Access "x" to promote to T2
            _ = cache.GetOrDefault("x");

            var items = cache.ToList();
            Assert.Equal(2, items.Count);
            Assert.Contains(items, kvp => kvp.Key == "x" && kvp.Value == "10");
            Assert.Contains(items, kvp => kvp.Key == "y" && kvp.Value == "20");
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var cache = new BoundedCache<string, string>(LruOptions(5));
            cache.Put("key", "val");
            cache.Dispose();
            cache.Dispose(); // Should not throw
            Assert.True(true); // Verify no exception on double dispose
        }

        [Fact]
        public async Task DisposeAsync_Works()
        {
            var cache = new BoundedCache<string, string>(TtlOptions(TimeSpan.FromSeconds(5)));
            cache.Put("key", "val");
            Assert.Equal(1, cache.Count);
            await cache.DisposeAsync();
            Assert.True(true); // Verify no exception on async dispose
        }

        // ----------------------------------------------------------------
        // In-memory backing store for tests
        // ----------------------------------------------------------------

        internal sealed class InMemoryBackingStore : IPersistentBackingStore
        {
            private readonly ConcurrentDictionary<string, byte[]> _store = new();

            public Task WriteAsync(string path, byte[] data, CancellationToken ct = default)
            {
                _store[path] = data;
                return Task.CompletedTask;
            }

            public Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
            {
                return Task.FromResult(_store.TryGetValue(path, out var data) ? data : null);
            }

            public Task DeleteAsync(string path, CancellationToken ct = default)
            {
                _store.TryRemove(path, out _);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
            {
                var matches = _store.Keys.Where(k => k.StartsWith(prefix)).ToList();
                return Task.FromResult<IReadOnlyList<string>>(matches);
            }

            public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
            {
                return Task.FromResult(_store.ContainsKey(path));
            }

            public int StoredCount => _store.Count;
        }
    }
}
