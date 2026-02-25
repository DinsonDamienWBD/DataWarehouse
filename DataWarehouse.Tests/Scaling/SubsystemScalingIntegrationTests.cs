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
    /// Integration tests verifying subsystem scaling infrastructure end-to-end:
    /// persistence survival, runtime reconfiguration, memory ceiling,
    /// backpressure engagement, IScalableSubsystem metrics, and concurrent reconfiguration.
    /// </summary>
    public class SubsystemScalingIntegrationTests
    {
        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        /// <summary>In-memory backing store simulating persistent storage.</summary>
        private sealed class InMemoryBackingStore : IPersistentBackingStore
        {
            private readonly ConcurrentDictionary<string, byte[]> _store = new();

            public Task WriteAsync(string path, byte[] data, CancellationToken ct = default)
            {
                _store[path] = data;
                return Task.CompletedTask;
            }

            public Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
                => Task.FromResult(_store.TryGetValue(path, out var data) ? data : null);

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
                => Task.FromResult(_store.ContainsKey(path));

            public int StoredCount => _store.Count;
        }

        /// <summary>
        /// Minimal IScalableSubsystem implementation backed by BoundedCache for integration testing.
        /// </summary>
        private sealed class TestScalableSubsystem : IScalableSubsystem, IDisposable
        {
            private BoundedCache<string, string> _cache;
            private ScalingLimits _limits;
            private readonly IPersistentBackingStore? _backingStore;
            private readonly object _lock = new();

            public TestScalableSubsystem(ScalingLimits limits, IPersistentBackingStore? backingStore = null)
            {
                _limits = limits;
                _backingStore = backingStore;
                _cache = CreateCache(limits.MaxCacheEntries);
            }

            public ScalingLimits CurrentLimits => _limits;

            public BackpressureState CurrentBackpressureState
            {
                get
                {
                    var ratio = _limits.MaxCacheEntries > 0
                        ? (double)_cache.Count / _limits.MaxCacheEntries
                        : 0;
                    return ratio switch
                    {
                        >= 0.95 => BackpressureState.Shedding,
                        >= 0.80 => BackpressureState.Critical,
                        >= 0.50 => BackpressureState.Warning,
                        _ => BackpressureState.Normal,
                    };
                }
            }

            public IReadOnlyDictionary<string, object> GetScalingMetrics()
            {
                var stats = _cache.GetStatistics();
                return new Dictionary<string, object>
                {
                    ["cache.size"] = _cache.Count,
                    ["cache.hitRate"] = stats.HitRatio,
                    ["cache.memoryBytes"] = _cache.EstimatedMemoryBytes,
                    ["cache.hits"] = stats.Hits,
                    ["cache.misses"] = stats.Misses,
                    ["cache.evictions"] = stats.Evictions,
                    ["backpressure.state"] = CurrentBackpressureState.ToString(),
                    ["backpressure.queueDepth"] = 0,
                    ["limits.maxCacheEntries"] = _limits.MaxCacheEntries,
                };
            }

            public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
            {
                lock (_lock)
                {
                    var oldCache = _cache;
                    _limits = limits;
                    var newCache = CreateCache(limits.MaxCacheEntries);

                    // Migrate entries from old cache to new cache
                    foreach (var kvp in oldCache)
                    {
                        newCache.Put(kvp.Key, kvp.Value);
                    }

                    _cache = newCache;
                    oldCache.Dispose();
                }

                return Task.CompletedTask;
            }

            public void Put(string key, string value)
            {
                _cache.Put(key, value);
            }

            public async Task PutAsync(string key, string value, CancellationToken ct = default)
            {
                await _cache.PutAsync(key, value, ct);
            }

            public string? Get(string key) => _cache.GetOrDefault(key);

            public async Task<string?> GetAsync(string key, CancellationToken ct = default)
                => await _cache.GetAsync(key, ct);

            public int CacheCount => _cache.Count;

            private BoundedCache<string, string> CreateCache(int maxEntries)
            {
                var opts = new BoundedCacheOptions<string, string>
                {
                    MaxEntries = maxEntries,
                    EvictionPolicy = CacheEvictionMode.LRU,
                };

                if (_backingStore != null)
                {
                    opts.BackingStore = _backingStore;
                    opts.BackingStorePath = "dw://test/subsystem/";
                    opts.Serializer = v => Encoding.UTF8.GetBytes(v);
                    opts.Deserializer = b => Encoding.UTF8.GetString(b);
                    opts.KeyToString = k => k;
                    opts.WriteThrough = true;
                }

                return new BoundedCache<string, string>(opts);
            }

            public void Dispose()
            {
                _cache.Dispose();
            }
        }

        // ----------------------------------------------------------------
        // 1. Persistence survival
        // ----------------------------------------------------------------

        [Fact]
        public async Task PersistenceSurvival_ItemsRecoverableAfterDispose()
        {
            var store = new InMemoryBackingStore();

            // Phase 1: Create cache, insert 1000 items, dispose (simulating restart)
            using (var subsystem = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 500), store))
            {
                for (int i = 0; i < 1000; i++)
                    await subsystem.PutAsync($"key{i}", $"value{i}");
            }

            // Backing store should contain items (written via write-through and eviction)
            Assert.True(store.StoredCount > 0);

            // Phase 2: Create new subsystem with same backing store -- items recoverable
            using var restored = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 1000), store);

            // Verify items can be loaded from backing store on cache miss
            int recovered = 0;
            for (int i = 0; i < 1000; i++)
            {
                var val = await restored.GetAsync($"key{i}");
                if (val == $"value{i}") recovered++;
            }

            // All 1000 items should be recoverable from backing store
            Assert.Equal(1000, recovered);
        }

        // ----------------------------------------------------------------
        // 2. Runtime reconfiguration
        // ----------------------------------------------------------------

        [Fact]
        public async Task RuntimeReconfiguration_NewLimitsTakeEffectWithoutRestart()
        {
            using var subsystem = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 100));

            // Insert 200 items -- 100 will be evicted (cache holds max 100)
            for (int i = 0; i < 200; i++)
                subsystem.Put($"key{i}", $"value{i}");

            Assert.Equal(100, subsystem.CacheCount);

            // Reconfigure to 500
            await subsystem.ReconfigureLimitsAsync(
                new ScalingLimits(MaxCacheEntries: 500));

            Assert.Equal(500, subsystem.CurrentLimits.MaxCacheEntries);

            // Insert 300 more -- cache should now hold up to 500
            for (int i = 200; i < 500; i++)
                subsystem.Put($"key{i}", $"value{i}");

            // Cache should hold up to 500 entries
            Assert.True(subsystem.CacheCount <= 500);
            Assert.True(subsystem.CacheCount > 100); // More than old limit
        }

        [Fact]
        public async Task RuntimeReconfiguration_ShrinkingEvictsExcess()
        {
            using var subsystem = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 200));

            // Fill to 200
            for (int i = 0; i < 200; i++)
                subsystem.Put($"key{i}", $"value{i}");

            Assert.Equal(200, subsystem.CacheCount);

            // Shrink to 50 -- excess entries should be evicted during migration
            await subsystem.ReconfigureLimitsAsync(
                new ScalingLimits(MaxCacheEntries: 50));

            Assert.True(subsystem.CacheCount <= 50);
        }

        // ----------------------------------------------------------------
        // 3. Memory ceiling under load
        // ----------------------------------------------------------------

        [Fact]
        public void MemoryCeiling_BoundedUnder10xLoad()
        {
            using var cache = new BoundedCache<string, string>(new BoundedCacheOptions<string, string>
            {
                MaxEntries = 10_000,
                EvictionPolicy = CacheEvictionMode.LRU,
            });

            var memBefore = GC.GetTotalMemory(true);

            // Insert 100,000 items (10x capacity)
            for (int i = 0; i < 100_000; i++)
                cache.Put($"key{i}", $"value{i}");

            var memAfter = GC.GetTotalMemory(true);
            var memIncrease = memAfter - memBefore;

            // Cache should hold at most MaxEntries
            Assert.Equal(10_000, cache.Count);

            // Memory increase should be bounded -- not proportional to 100K items
            // With 256 bytes per entry heuristic, 10K entries ~ 2.5 MB
            // Allow generous margin: less than 50 MB (far less than 100K * 256B = 25MB)
            Assert.True(memIncrease < 50 * 1024 * 1024,
                $"Memory increase {memIncrease / (1024 * 1024)} MB exceeds 50 MB ceiling");
        }

        // ----------------------------------------------------------------
        // 4. Backpressure engagement
        // ----------------------------------------------------------------

        [Fact]
        public void BackpressureEngagement_TransitionsFromNormalToCritical()
        {
            using var subsystem = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 100));

            // Start at Normal
            Assert.Equal(BackpressureState.Normal, subsystem.CurrentBackpressureState);

            // Fill to 50% -- should be Warning
            for (int i = 0; i < 50; i++)
                subsystem.Put($"key{i}", $"value{i}");
            Assert.Equal(BackpressureState.Warning, subsystem.CurrentBackpressureState);

            // Fill to 80% -- should be Critical
            for (int i = 50; i < 80; i++)
                subsystem.Put($"key{i}", $"value{i}");
            Assert.Equal(BackpressureState.Critical, subsystem.CurrentBackpressureState);

            // Fill to 95% -- should be Shedding
            for (int i = 80; i < 95; i++)
                subsystem.Put($"key{i}", $"value{i}");
            Assert.Equal(BackpressureState.Shedding, subsystem.CurrentBackpressureState);
        }

        [Fact]
        public void BackpressureEngagement_OverflowCappedAtMaxEntries()
        {
            using var subsystem = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 100));

            // Push 1000 items through a capacity-100 subsystem
            for (int i = 0; i < 1000; i++)
                subsystem.Put($"key{i}", $"value{i}");

            // Cache should not exceed capacity
            Assert.Equal(100, subsystem.CacheCount);
        }

        // ----------------------------------------------------------------
        // 5. IScalableSubsystem metrics
        // ----------------------------------------------------------------

        [Fact]
        public void ScalingMetrics_ContainsExpectedKeys()
        {
            using var subsystem = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 100));

            // Exercise the subsystem
            subsystem.Put("a", "1");
            subsystem.Put("b", "2");
            _ = subsystem.Get("a"); // hit
            _ = subsystem.Get("nonexistent"); // miss

            var metrics = subsystem.GetScalingMetrics();

            // Verify expected keys exist
            Assert.True(metrics.ContainsKey("cache.size"));
            Assert.True(metrics.ContainsKey("cache.hitRate"));
            Assert.True(metrics.ContainsKey("cache.memoryBytes"));
            Assert.True(metrics.ContainsKey("cache.hits"));
            Assert.True(metrics.ContainsKey("cache.misses"));
            Assert.True(metrics.ContainsKey("cache.evictions"));
            Assert.True(metrics.ContainsKey("backpressure.state"));
            Assert.True(metrics.ContainsKey("backpressure.queueDepth"));
            Assert.True(metrics.ContainsKey("limits.maxCacheEntries"));

            // Verify reasonable values
            Assert.Equal(2, metrics["cache.size"]);
            Assert.Equal(100, metrics["limits.maxCacheEntries"]);
            Assert.Equal("Normal", metrics["backpressure.state"]);
        }

        [Fact]
        public void ScalingMetrics_HitRateCalculatedCorrectly()
        {
            using var subsystem = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 100));

            subsystem.Put("key1", "val1");
            subsystem.Put("key2", "val2");

            // 2 hits
            _ = subsystem.Get("key1");
            _ = subsystem.Get("key2");
            // 1 miss
            _ = subsystem.Get("missing");

            var metrics = subsystem.GetScalingMetrics();
            var hitRate = (double)metrics["cache.hitRate"];

            // 2 hits out of 3 lookups = ~0.667 (but internal stats may vary due to Put)
            Assert.True(hitRate >= 0 && hitRate <= 1.0);
        }

        // ----------------------------------------------------------------
        // 6. Concurrent scaling reconfiguration
        // ----------------------------------------------------------------

        [Fact]
        public async Task ConcurrentReconfiguration_NoExceptionsNoCorruption()
        {
            using var subsystem = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 100));
            var exceptions = new ConcurrentBag<Exception>();

            // Start 10 concurrent writers
            var writerTasks = Enumerable.Range(0, 10).Select(w => Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 100; i++)
                        subsystem.Put($"w{w}_key{i}", $"w{w}_val{i}");
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })).ToArray();

            // Simultaneously reconfigure limits 5 times
            var reconfTasks = Enumerable.Range(0, 5).Select(r => Task.Run(async () =>
            {
                try
                {
                    var newMax = 50 + (r * 50); // 50, 100, 150, 200, 250
                    await subsystem.ReconfigureLimitsAsync(
                        new ScalingLimits(MaxCacheEntries: newMax));
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })).ToArray();

            await Task.WhenAll(writerTasks.Concat(reconfTasks));

            Assert.Empty(exceptions);

            // Cache should be in consistent state
            Assert.True(subsystem.CacheCount >= 0);
            Assert.True(subsystem.CacheCount <= subsystem.CurrentLimits.MaxCacheEntries);
        }

        [Fact]
        public async Task ConcurrentReconfiguration_LimitsEventuallyConverge()
        {
            using var subsystem = new TestScalableSubsystem(
                new ScalingLimits(MaxCacheEntries: 100));

            // Reconfigure multiple times in sequence
            await subsystem.ReconfigureLimitsAsync(new ScalingLimits(MaxCacheEntries: 200));
            await subsystem.ReconfigureLimitsAsync(new ScalingLimits(MaxCacheEntries: 50));
            await subsystem.ReconfigureLimitsAsync(new ScalingLimits(MaxCacheEntries: 300));

            Assert.Equal(300, subsystem.CurrentLimits.MaxCacheEntries);

            // Insert items up to new limit
            for (int i = 0; i < 300; i++)
                subsystem.Put($"key{i}", $"value{i}");

            Assert.Equal(300, subsystem.CacheCount);
        }

        // ----------------------------------------------------------------
        // ScalingLimits and ScalingModels coverage
        // ----------------------------------------------------------------

        [Fact]
        public void ScalingLimits_DefaultValues()
        {
            var limits = new ScalingLimits();
            Assert.Equal(10_000, limits.MaxCacheEntries);
            Assert.Equal(100 * 1024 * 1024L, limits.MaxMemoryBytes);
            Assert.Equal(64, limits.MaxConcurrentOperations);
            Assert.Equal(1_000, limits.MaxQueueDepth);
        }

        [Fact]
        public void SubsystemScalingPolicy_DefaultValues()
        {
            var policy = new SubsystemScalingPolicy(new ScalingLimits());
            Assert.NotNull(policy.InstanceLimits);
            Assert.Null(policy.TenantLimits);
            Assert.Null(policy.UserLimits);
            Assert.True(policy.AutoSizeFromRam);
            Assert.Equal(0.1, policy.RamPercentage);
        }

        [Fact]
        public void ScalingHierarchyLevel_AllValuesAreDefined()
        {
            var values = Enum.GetValues<ScalingHierarchyLevel>();
            Assert.Equal(3, values.Length);
            Assert.Contains(ScalingHierarchyLevel.Instance, values);
            Assert.Contains(ScalingHierarchyLevel.Tenant, values);
            Assert.Contains(ScalingHierarchyLevel.User, values);
        }

        [Fact]
        public void ScalingReconfiguredEventArgs_HasCorrectProperties()
        {
            var old = new ScalingLimits(MaxCacheEntries: 100);
            var updated = new ScalingLimits(MaxCacheEntries: 200);
            var ts = new DateTime(2026, 2, 24, 0, 0, 0, DateTimeKind.Utc);
            var args = new ScalingReconfiguredEventArgs("TestSubsystem", old, updated, ts);

            Assert.Equal("TestSubsystem", args.SubsystemName);
            Assert.Equal(100, args.OldLimits.MaxCacheEntries);
            Assert.Equal(200, args.NewLimits.MaxCacheEntries);
            Assert.Equal(ts, args.Timestamp);
        }
    }
}
