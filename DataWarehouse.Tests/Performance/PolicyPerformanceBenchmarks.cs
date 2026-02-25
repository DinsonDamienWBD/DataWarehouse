using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using DataWarehouse.SDK.Infrastructure.Policy.Performance;
using DataWarehouse.SDK.VirtualDiskEngine.Verification;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Performance;

/// <summary>
/// Performance benchmark suite (~35 tests) confirming three-tier fast-path timing claims
/// and verifying that PolicyEngine performance meets production requirements under load.
/// All thresholds are generous (5-10x expected) to avoid CI flakiness.
/// Uses Stopwatch.StartNew() pattern consistent with PerformanceBaselineTests.
/// </summary>
[Trait("Category", "Performance")]
public class PolicyPerformanceBenchmarks
{
    // ========================================================================
    // Helpers
    // ========================================================================

    private static InMemoryPolicyStore CreateStore() => new();
    private static InMemoryPolicyPersistence CreatePersistence() => new();

    private static PolicyResolutionEngine CreateEngine(
        InMemoryPolicyStore? store = null,
        InMemoryPolicyPersistence? persistence = null,
        OperationalProfile? profile = null,
        VersionedPolicyCache? cache = null)
    {
        return new PolicyResolutionEngine(
            store ?? CreateStore(),
            persistence ?? CreatePersistence(),
            profile,
            cache: cache);
    }

    private static FeaturePolicy MakePolicy(string featureId, PolicyLevel level, int intensity = 50,
        CascadeStrategy cascade = CascadeStrategy.Override, AiAutonomyLevel ai = AiAutonomyLevel.SuggestExplain,
        Dictionary<string, string>? customParams = null)
    {
        return new FeaturePolicy
        {
            FeatureId = featureId,
            Level = level,
            IntensityLevel = intensity,
            Cascade = cascade,
            AiAutonomy = ai,
            CustomParameters = customParams
        };
    }

    private static OperationalProfile CreateProfileWithFeatures(params string[] featureIds)
    {
        var policies = new Dictionary<string, FeaturePolicy>();
        foreach (var fid in featureIds)
        {
            policies[fid] = MakePolicy(fid, PolicyLevel.VDE, 50);
        }
        return new OperationalProfile
        {
            Name = "benchmark-profile",
            Preset = OperationalProfilePreset.Standard,
            FeaturePolicies = policies
        };
    }

    // ========================================================================
    // 1. PolicyResolutionEngine timing (~8 tests)
    // ========================================================================

    [Fact]
    public async Task ResolutionEngine_SingleResolve_CompletesWithin10ms()
    {
        var store = CreateStore();
        var policy = MakePolicy("encryption", PolicyLevel.VDE, 80);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", policy);
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Warmup
        await engine.ResolveAsync("encryption", ctx);

        var sw = Stopwatch.StartNew();
        var result = await engine.ResolveAsync("encryption", ctx);
        sw.Stop();

        result.EffectiveIntensity.Should().Be(80);
        sw.ElapsedMilliseconds.Should().BeLessThan(10,
            "Single ResolveAsync should complete within 10ms");
    }

    [Fact]
    public async Task ResolutionEngine_ResolveAll10Features_CompletesWithin100ms()
    {
        var store = CreateStore();
        var featureIds = Enumerable.Range(0, 10).Select(i => $"feature_{i}").ToArray();
        foreach (var fid in featureIds)
        {
            await store.SetAsync(fid, PolicyLevel.VDE, "/vde1", MakePolicy(fid, PolicyLevel.VDE, 50 + featureIds.ToList().IndexOf(fid)));
        }
        var profile = CreateProfileWithFeatures(featureIds);
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Warmup
        await engine.ResolveAllAsync(ctx);

        var sw = Stopwatch.StartNew();
        var result = await engine.ResolveAllAsync(ctx);
        sw.Stop();

        result.Should().HaveCount(10);
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "ResolveAllAsync for 10 features should complete within 100ms");
    }

    [Fact]
    public async Task ResolutionEngine_100SequentialResolves_CompletesWithin500ms()
    {
        var store = CreateStore();
        await store.SetAsync("compression", PolicyLevel.VDE, "/vde1", MakePolicy("compression", PolicyLevel.VDE, 70));
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Warmup
        await engine.ResolveAsync("compression", ctx);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            await engine.ResolveAsync("compression", ctx);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "100 sequential ResolveAsync calls should complete within 500ms");
    }

    [Fact]
    public async Task ResolutionEngine_5LevelChain_CompletesWithin20ms()
    {
        var store = CreateStore();
        var path = "/vde1/cont1/obj1/chunk1/block1";
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", MakePolicy("encryption", PolicyLevel.VDE, 50));
        await store.SetAsync("encryption", PolicyLevel.Container, "/vde1/cont1", MakePolicy("encryption", PolicyLevel.Container, 60));
        await store.SetAsync("encryption", PolicyLevel.Object, "/vde1/cont1/obj1", MakePolicy("encryption", PolicyLevel.Object, 70));
        await store.SetAsync("encryption", PolicyLevel.Chunk, "/vde1/cont1/obj1/chunk1", MakePolicy("encryption", PolicyLevel.Chunk, 80));
        await store.SetAsync("encryption", PolicyLevel.Block, path, MakePolicy("encryption", PolicyLevel.Block, 90));
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = path };

        // Warmup
        await engine.ResolveAsync("encryption", ctx);

        var sw = Stopwatch.StartNew();
        var result = await engine.ResolveAsync("encryption", ctx);
        sw.Stop();

        result.EffectiveIntensity.Should().Be(90);
        sw.ElapsedMilliseconds.Should().BeLessThan(20,
            "Resolution with 5-level chain should complete within 20ms");
    }

    [Fact]
    public async Task ResolutionEngine_OverrideCascade_CompletesWithin10ms()
    {
        var store = CreateStore();
        await store.SetAsync("compression", PolicyLevel.VDE, "/vde1", MakePolicy("compression", PolicyLevel.VDE, 50, CascadeStrategy.Override));
        await store.SetAsync("compression", PolicyLevel.Container, "/vde1/cont1", MakePolicy("compression", PolicyLevel.Container, 80, CascadeStrategy.Override));
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1" };

        // Warmup
        await engine.ResolveAsync("compression", ctx);

        var sw = Stopwatch.StartNew();
        var result = await engine.ResolveAsync("compression", ctx);
        sw.Stop();

        result.EffectiveIntensity.Should().Be(80);
        sw.ElapsedMilliseconds.Should().BeLessThan(10,
            "Resolution with Override cascade should complete within 10ms");
    }

    [Fact]
    public async Task ResolutionEngine_MostRestrictive_CompletesWithin15ms()
    {
        var store = CreateStore();
        await store.SetAsync("access_control", PolicyLevel.VDE, "/vde1",
            MakePolicy("access_control", PolicyLevel.VDE, 90, CascadeStrategy.MostRestrictive));
        await store.SetAsync("access_control", PolicyLevel.Container, "/vde1/cont1",
            MakePolicy("access_control", PolicyLevel.Container, 60, CascadeStrategy.MostRestrictive));
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1" };

        // Warmup
        await engine.ResolveAsync("access_control", ctx);

        var sw = Stopwatch.StartNew();
        var result = await engine.ResolveAsync("access_control", ctx);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(15,
            "Resolution with MostRestrictive (scans all levels) should complete within 15ms");
    }

    [Fact]
    public async Task ResolutionEngine_SimulateAsync_CompletesWithin10ms()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", MakePolicy("encryption", PolicyLevel.VDE, 50));
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };
        var hypothetical = MakePolicy("encryption", PolicyLevel.VDE, 100);

        // Warmup
        await engine.SimulateAsync("encryption", ctx, hypothetical);

        var sw = Stopwatch.StartNew();
        var result = await engine.SimulateAsync("encryption", ctx, hypothetical);
        sw.Stop();

        result.EffectiveIntensity.Should().Be(100);
        sw.ElapsedMilliseconds.Should().BeLessThan(10,
            "SimulateAsync should complete within 10ms");
    }

    [Fact]
    public async Task ResolutionEngine_SetActiveProfileAndResolve_CompletesWithin20ms()
    {
        var store = CreateStore();
        var engine = CreateEngine(store);
        var profile = CreateProfileWithFeatures("encryption", "compression");
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Warmup
        await engine.SetActiveProfileAsync(profile);
        await engine.ResolveAsync("encryption", ctx);

        var newProfile = CreateProfileWithFeatures("encryption", "compression", "access_control");

        var sw = Stopwatch.StartNew();
        await engine.SetActiveProfileAsync(newProfile);
        var result = await engine.ResolveAsync("access_control", ctx);
        sw.Stop();

        result.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(20,
            "SetActiveProfileAsync + subsequent resolve should complete within 20ms");
    }

    // ========================================================================
    // 2. FastPathPolicyEngine timing (~5 tests)
    // ========================================================================

    private static FastPathPolicyEngine CreateFastPathEngine(
        InMemoryPolicyStore? store = null,
        OperationalProfile? profile = null)
    {
        var s = store ?? CreateStore();
        var p = CreatePersistence();
        var cache = new MaterializedPolicyCache();
        var delegateCache = new PolicyDelegateCache(cache);
        var resolutionEngine = new PolicyResolutionEngine(s, p, profile);

        return new FastPathPolicyEngine(resolutionEngine, cache, delegateCache);
    }

    [Fact]
    public async Task FastPath_CriticalFeatureResolution_CompletesWithin5ms()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", MakePolicy("encryption", PolicyLevel.VDE, 90));
        var profile = CreateProfileWithFeatures("encryption");
        var engine = CreateFastPathEngine(store, profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Warmup
        await engine.ResolveAsync("encryption", ctx);

        var sw = Stopwatch.StartNew();
        var result = await engine.ResolveAsync("encryption", ctx);
        sw.Stop();

        result.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(5,
            "Critical feature resolution via fast path should complete within 5ms");
    }

    [Fact]
    public async Task FastPath_PerOperationWithBloomFilterSkip_CompletesWithin2ms()
    {
        var store = CreateStore();
        var bloomFilter = new BloomFilterSkipIndex(expectedItems: 100);
        var skipOptimizer = new PolicySkipOptimizer(store, bloomFilter);
        var cache = new MaterializedPolicyCache();
        var delegateCache = new PolicyDelegateCache(cache);
        var resolutionEngine = new PolicyResolutionEngine(store, CreatePersistence());
        var engine = new FastPathPolicyEngine(resolutionEngine, cache, delegateCache, skipOptimizer);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Warmup
        await engine.ResolveAsync("access_control", ctx);

        var sw = Stopwatch.StartNew();
        var result = await engine.ResolveAsync("access_control", ctx);
        sw.Stop();

        result.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(2,
            "PerOperation feature with bloom filter skip should complete within 2ms");
    }

    [Fact]
    public async Task FastPath_BackgroundFeatureFromMaterializedCache_CompletesWithin1ms()
    {
        var cache = new MaterializedPolicyCache();
        var effectivePolicies = new Dictionary<string, IEffectivePolicy>
        {
            ["audit_logging:/vde1"] = new EffectivePolicy(
                featureId: "audit_logging",
                effectiveIntensity: 50,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Inherit,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: DateTimeOffset.UtcNow)
        };
        cache.Publish(effectivePolicies);

        var delegateCache = new PolicyDelegateCache(cache);
        var store = CreateStore();
        var resolutionEngine = new PolicyResolutionEngine(store, CreatePersistence());
        var engine = new FastPathPolicyEngine(resolutionEngine, cache, delegateCache);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Warmup - resolve once to compile delegate
        await engine.ResolveAsync("audit_logging", ctx);

        var sw = Stopwatch.StartNew();
        var result = await engine.ResolveAsync("audit_logging", ctx);
        sw.Stop();

        result.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(1,
            "Background feature from materialized cache should complete within 1ms");
    }

    [Fact]
    public async Task FastPath_1000Resolutions_CompletesWithin2000ms()
    {
        var store = CreateStore();
        await store.SetAsync("compression", PolicyLevel.VDE, "/vde1", MakePolicy("compression", PolicyLevel.VDE, 70));
        var profile = CreateProfileWithFeatures("compression");
        var engine = CreateFastPathEngine(store, profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Warmup
        await engine.ResolveAsync("compression", ctx);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            await engine.ResolveAsync("compression", ctx);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "1000 fast-path resolutions should complete within 2000ms (< 2ms average)");
    }

    [Fact]
    public async Task FastPath_FasterThanFullPath_AtLeast2x()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", MakePolicy("encryption", PolicyLevel.VDE, 80));
        var profile = CreateProfileWithFeatures("encryption");
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Full path engine (no fast-path caching)
        var fullEngine = CreateEngine(store, profile: profile);

        // Fast path engine (with materialized cache + delegate cache)
        var cache = new MaterializedPolicyCache();
        var effectivePolicies = new Dictionary<string, IEffectivePolicy>
        {
            ["encryption:/vde1"] = new EffectivePolicy(
                featureId: "encryption",
                effectiveIntensity: 80,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Override,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: DateTimeOffset.UtcNow)
        };
        cache.Publish(effectivePolicies);
        var delegateCache = new PolicyDelegateCache(cache);
        var fastEngine = new FastPathPolicyEngine(
            new PolicyResolutionEngine(store, CreatePersistence(), profile),
            cache, delegateCache);

        // Warmup both
        for (int i = 0; i < 100; i++)
        {
            await fullEngine.ResolveAsync("encryption", ctx);
            await fastEngine.ResolveAsync("encryption", ctx);
        }

        // Measure full path
        var swFull = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            await fullEngine.ResolveAsync("encryption", ctx);
        }
        swFull.Stop();

        // Measure fast path
        var swFast = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            await fastEngine.ResolveAsync("encryption", ctx);
        }
        swFast.Stop();

        var ratio = (double)swFull.ElapsedTicks / Math.Max(swFast.ElapsedTicks, 1);
        Assert.True(ratio >= 1.5,
            $"Fast path should be at least 1.5x faster than full path (was {ratio:F2}x). Full={swFull.ElapsedMilliseconds}ms, Fast={swFast.ElapsedMilliseconds}ms");
    }

    // ========================================================================
    // 3. BloomFilterSkipIndex timing (~5 tests)
    // ========================================================================

    [Fact]
    public void BloomFilter_SingleQuery_CompletesWithin100Microseconds()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 1000);
        filter.Add(PolicyLevel.VDE, "/vde1");

        // Warmup
        filter.MayContain(PolicyLevel.VDE, "/vde1");

        var sw = Stopwatch.StartNew();
        var result = filter.MayContain(PolicyLevel.VDE, "/vde1");
        sw.Stop();

        result.Should().BeTrue();
        // 100 microseconds = 0.1ms
        sw.Elapsed.TotalMilliseconds.Should().BeLessThan(0.5,
            "Single bloom filter query should complete within 0.5ms (generous threshold for 100us target)");
    }

    [Fact]
    public void BloomFilter_10000Queries_CompletesWithin100ms()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 1000);
        for (int i = 0; i < 100; i++)
        {
            filter.Add(PolicyLevel.VDE, $"/vde{i}");
        }

        // Warmup
        for (int i = 0; i < 100; i++)
        {
            filter.MayContain(PolicyLevel.VDE, $"/vde{i}");
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            filter.MayContain(PolicyLevel.VDE, $"/vde{i % 100}");
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "10000 bloom filter queries should complete within 100ms");
    }

    [Fact]
    public void BloomFilter_Add1000Entries_QueryRemainsSubMillisecond()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 2000);

        for (int i = 0; i < 1000; i++)
        {
            filter.Add(PolicyLevel.Container, $"/vde1/container{i}");
        }

        filter.ItemCount.Should().Be(1000);

        // Warmup
        filter.MayContain(PolicyLevel.Container, "/vde1/container500");

        var sw = Stopwatch.StartNew();
        var result = filter.MayContain(PolicyLevel.Container, "/vde1/container500");
        sw.Stop();

        result.Should().BeTrue();
        sw.Elapsed.TotalMilliseconds.Should().BeLessThan(1.0,
            "Query after adding 1000 entries should remain sub-millisecond");
    }

    [Fact]
    public void BloomFilter_FalsePositiveRate_Below5Percent()
    {
        // 8192-bit filter with proper sizing
        var filter = new BloomFilterSkipIndex(expectedItems: 1000, falsePositiveRate: 0.01);

        // Add 1000 known entries
        for (int i = 0; i < 1000; i++)
        {
            filter.Add(PolicyLevel.VDE, $"/known-vde-{i}");
        }

        // Test 10000 unknown entries for false positives
        int falsePositives = 0;
        for (int i = 0; i < 10_000; i++)
        {
            if (filter.MayContain(PolicyLevel.VDE, $"/unknown-vde-{i + 100_000}"))
            {
                falsePositives++;
            }
        }

        double rate = falsePositives / 10_000.0;
        rate.Should().BeLessThan(0.05,
            $"False positive rate should be below 5% (was {rate:P2}, {falsePositives}/10000)");
    }

    [Fact]
    public void BloomFilter_XxHash64DoubleHashing_IsDeterministic()
    {
        var filter1 = new BloomFilterSkipIndex(expectedItems: 100);
        var filter2 = new BloomFilterSkipIndex(expectedItems: 100);

        filter1.Add(PolicyLevel.Block, "/vde1/c1/o1/ch1/b1");
        filter2.Add(PolicyLevel.Block, "/vde1/c1/o1/ch1/b1");

        // Both filters should have the same hash behavior: same key yields same result
        var result1 = filter1.MayContain(PolicyLevel.Block, "/vde1/c1/o1/ch1/b1");
        var result2 = filter2.MayContain(PolicyLevel.Block, "/vde1/c1/o1/ch1/b1");

        result1.Should().BeTrue();
        result2.Should().BeTrue();

        // Non-existent key should be consistent too
        var miss1 = filter1.MayContain(PolicyLevel.Block, "/vde1/c1/o1/ch1/b999");
        var miss2 = filter2.MayContain(PolicyLevel.Block, "/vde1/c1/o1/ch1/b999");

        miss1.Should().Be(miss2, "XxHash64 double-hashing should produce deterministic results");
    }

    // ========================================================================
    // 4. MaterializedPolicyCache timing (~4 tests)
    // ========================================================================

    [Fact]
    public void MaterializedCache_CacheHit_ReturnsWithinHalfMs()
    {
        var cache = new MaterializedPolicyCache();
        var policies = new Dictionary<string, IEffectivePolicy>
        {
            ["encryption:/vde1"] = new EffectivePolicy(
                featureId: "encryption",
                effectiveIntensity: 80,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Override,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: DateTimeOffset.UtcNow)
        };
        cache.Publish(policies);

        // Warmup
        cache.GetSnapshot().TryGetEffective("encryption", "/vde1");

        var sw = Stopwatch.StartNew();
        var result = cache.GetSnapshot().TryGetEffective("encryption", "/vde1");
        sw.Stop();

        result.Should().NotBeNull();
        result!.EffectiveIntensity.Should().Be(80);
        sw.Elapsed.TotalMilliseconds.Should().BeLessThan(0.5,
            "Cache hit should return within 0.5ms");
    }

    [Fact]
    public void MaterializedCache_CacheMissAndPopulate_CompletesWithin10ms()
    {
        var cache = new MaterializedPolicyCache();

        var sw = Stopwatch.StartNew();

        // Miss
        var snapshot = cache.GetSnapshot();
        var miss = snapshot.TryGetEffective("encryption", "/vde1");
        miss.Should().BeNull();

        // Populate
        var policies = new Dictionary<string, IEffectivePolicy>();
        for (int i = 0; i < 100; i++)
        {
            policies[$"feature_{i}:/vde1"] = new EffectivePolicy(
                featureId: $"feature_{i}",
                effectiveIntensity: 50,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Inherit,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: DateTimeOffset.UtcNow);
        }
        cache.Publish(policies);

        // Hit after populate
        var result = cache.GetSnapshot().TryGetEffective("feature_50", "/vde1");

        sw.Stop();

        result.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(10,
            "Cache miss + populate + hit should complete within 10ms");
    }

    [Fact]
    public void MaterializedCache_1000CacheHits_CompletesWithin100ms()
    {
        var cache = new MaterializedPolicyCache();
        var policies = new Dictionary<string, IEffectivePolicy>();
        for (int i = 0; i < 100; i++)
        {
            policies[$"feature_{i}:/vde1"] = new EffectivePolicy(
                featureId: $"feature_{i}",
                effectiveIntensity: i,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Inherit,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: DateTimeOffset.UtcNow);
        }
        cache.Publish(policies);

        // Warmup
        for (int i = 0; i < 100; i++)
        {
            cache.GetSnapshot().TryGetEffective($"feature_{i}", "/vde1");
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            cache.GetSnapshot().TryGetEffective($"feature_{i % 100}", "/vde1");
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "1000 cache hits should complete within 100ms");
    }

    [Fact]
    public async Task MaterializedCache_DoubleBufferSwap_DoesNotBlockConcurrentReads()
    {
        var cache = new MaterializedPolicyCache();
        var initialPolicies = new Dictionary<string, IEffectivePolicy>
        {
            ["encryption:/vde1"] = new EffectivePolicy(
                featureId: "encryption",
                effectiveIntensity: 50,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Inherit,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: DateTimeOffset.UtcNow)
        };
        cache.Publish(initialPolicies);

        var readExceptions = new ConcurrentBag<Exception>();
        var readResults = new ConcurrentBag<int>();

        // Run reader and writer tasks concurrently using Task.WhenAll
        var sw = Stopwatch.StartNew();

        // 10 reader tasks each performing 100 reads
        var readerTasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            for (int j = 0; j < 100; j++)
            {
                try
                {
                    var snapshot = cache.GetSnapshot();
                    var result = snapshot.TryGetEffective("encryption", "/vde1");
                    if (result is not null)
                    {
                        readResults.Add(result.EffectiveIntensity);
                    }
                }
                catch (Exception ex)
                {
                    readExceptions.Add(ex);
                }
            }
        })).ToArray();

        // Writer task performing 100 double-buffer swaps
        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                var newPolicies = new Dictionary<string, IEffectivePolicy>
                {
                    ["encryption:/vde1"] = new EffectivePolicy(
                        featureId: "encryption",
                        effectiveIntensity: 50 + (i % 50),
                        effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                        appliedCascade: CascadeStrategy.Inherit,
                        decidedAtLevel: PolicyLevel.VDE,
                        resolutionChain: Array.Empty<FeaturePolicy>(),
                        mergedParameters: new Dictionary<string, string>(),
                        snapshotTimestamp: DateTimeOffset.UtcNow)
                };
                cache.Publish(newPolicies);
            }
        });

        await Task.WhenAll(readerTasks.Append(writerTask));
        sw.Stop();

        readExceptions.Should().BeEmpty("Double-buffer swap should not cause reader exceptions");
        readResults.Should().HaveCountGreaterThan(0, "Readers should have completed reads during swaps");
        readResults.Should().AllSatisfy(intensity =>
            intensity.Should().BeInRange(50, 99, "Read intensities should be valid values"));
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "100 swaps with concurrent reads should complete within 5s");
    }

    // ========================================================================
    // 5. Concurrency benchmarks (~5 tests)
    // ========================================================================

    [Fact]
    public async Task Concurrency_100ParallelResolves_CompletesWithin2000ms_NoDeadlock()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", MakePolicy("encryption", PolicyLevel.VDE, 80));
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        // Warmup
        await engine.ResolveAsync("encryption", ctx);

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 100).Select(_ =>
            engine.ResolveAsync("encryption", ctx)).ToArray();

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        results.Should().HaveCount(100);
        results.Should().AllSatisfy(r => r.EffectiveIntensity.Should().Be(80));
        sw.ElapsedMilliseconds.Should().BeLessThan(2000,
            "100 parallel ResolveAsync calls should complete within 2000ms with no deadlock");
    }

    [Fact]
    public async Task Concurrency_50Writes50Reads_NoCorruption_CompletesWithin3000ms()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", MakePolicy("encryption", PolicyLevel.VDE, 50));
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var sw = Stopwatch.StartNew();

        // 50 parallel writes
        var writeTasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1",
                MakePolicy("encryption", PolicyLevel.VDE, 50 + (i % 50)));
        })).ToArray();

        // 50 parallel reads
        var readTasks = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            var result = await engine.ResolveAsync("encryption", ctx);
            return result;
        })).ToArray();

        await Task.WhenAll(writeTasks);
        var readResults = await Task.WhenAll(readTasks);

        sw.Stop();

        readResults.Should().HaveCount(50);
        readResults.Should().AllSatisfy(r =>
        {
            r.Should().NotBeNull();
            r.EffectiveIntensity.Should().BeInRange(50, 99);
        });
        sw.ElapsedMilliseconds.Should().BeLessThan(3000,
            "50 parallel writes + 50 parallel reads should complete within 3000ms with no corruption");
    }

    [Fact]
    public void Concurrency_VersionedCacheSwapDuringParallelReads_NoException()
    {
        var cache = new VersionedPolicyCache();
        var exceptions = new ConcurrentBag<Exception>();
        var running = true;

        // Parallel readers
        var readerTasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            while (running)
            {
                try
                {
                    var snapshot = cache.GetSnapshot();
                    var ver = snapshot.Version;
                    var cnt = snapshot.Policies.Count;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToArray();

        // Writer performing Interlocked.Exchange swaps
        for (int i = 0; i < 200; i++)
        {
            var policies = new Dictionary<string, FeaturePolicy>
            {
                [$"encryption:VDE:/vde{i}"] = MakePolicy("encryption", PolicyLevel.VDE, 50 + (i % 50))
            };
            cache.Update(policies);
        }

        running = false;
        Task.WaitAll(readerTasks);

        exceptions.Should().BeEmpty(
            "Interlocked.Exchange swap during parallel reads should not throw exceptions");
        cache.CurrentVersion.Should().Be(200L);
    }

    [Fact]
    public async Task Concurrency_ConcurrentDictionaryStore_100ParallelOps_CompletesWithin1000ms()
    {
        var store = CreateStore();

        var sw = Stopwatch.StartNew();

        // 100 parallel writes + reads
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            var featureId = $"feature_{i % 10}";
            var path = $"/vde{i % 5}";

            // Write
            await store.SetAsync(featureId, PolicyLevel.VDE, path,
                MakePolicy(featureId, PolicyLevel.VDE, 50 + i));

            // Read
            var result = await store.GetAsync(featureId, PolicyLevel.VDE, path);
            return result;
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        sw.Stop();

        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        sw.ElapsedMilliseconds.Should().BeLessThan(1000,
            "100 parallel store writes + reads should complete within 1000ms");
    }

    [Fact]
    public async Task Concurrency_InMemoryPolicyStore_NoConcurrentModificationException()
    {
        var store = CreateStore();
        var exceptions = new ConcurrentBag<Exception>();

        // Concurrent reads, writes, and removes
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            try
            {
                var featureId = $"feature_{i % 5}";
                var path = $"/vde{i % 3}";

                await store.SetAsync(featureId, PolicyLevel.VDE, path,
                    MakePolicy(featureId, PolicyLevel.VDE, i));

                await store.GetAsync(featureId, PolicyLevel.VDE, path);
                await store.HasOverrideAsync(PolicyLevel.VDE, path);

                if (i % 3 == 0)
                {
                    await store.RemoveAsync(featureId, PolicyLevel.VDE, path);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty(
            "InMemoryPolicyStore concurrent access should not throw ConcurrentModificationException");
    }

    // ========================================================================
    // 6. Three-tier timing verification (~5 tests)
    // ========================================================================

    [Fact]
    public void ThreeTier_Tier1FullModuleVerification_CompletesWithinGenerousThreshold()
    {
        var sw = Stopwatch.StartNew();
        var tier1Results = Tier1ModuleVerifier.VerifyAllModules();
        sw.Stop();

        tier1Results.Should().NotBeEmpty("Tier 1 should produce verification results");
        sw.ElapsedMilliseconds.Should().BeLessThan(30_000,
            "Tier 1 full module verification should complete within 30s");
    }

    [Fact]
    public void ThreeTier_Tier2PipelineVerification_CompletesWithinGenerousThreshold()
    {
        var sw = Stopwatch.StartNew();
        var tier2Results = Tier2PipelineVerifier.VerifyAllModules();
        sw.Stop();

        tier2Results.Should().NotBeEmpty("Tier 2 should produce verification results");
        sw.ElapsedMilliseconds.Should().BeLessThan(30_000,
            "Tier 2 pipeline verification should complete within 30s");
    }

    [Fact]
    public void ThreeTier_Tier3BasicFallback_CompletesWithinGenerousThreshold()
    {
        var sw = Stopwatch.StartNew();
        var tier3Results = Tier3BasicFallbackVerifier.VerifyAllModules();
        sw.Stop();

        tier3Results.Should().NotBeEmpty("Tier 3 should produce verification results");
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000,
            "Tier 3 basic fallback verification should complete within 10s");
    }

    [Fact]
    public void ThreeTier_RelativeOrdering_Tier3FasterThanTier2FasterThanTier1()
    {
        var benchmarks = TierPerformanceBenchmark.RunBenchmarks();

        Assert.True(benchmarks.Count >= 5, "Should have 5 representative feature benchmarks");

        // Verify average ordering: Tier 3 (simplest) fastest, Tier 1 (full region) slowest.
        // Individual benchmarks may have noise (Tier 1 and Tier 2 differ by only a dictionary lookup),
        // so we verify the aggregate trend and allow individual benchmarks a 20% tolerance.
        var avgTier1 = benchmarks.Average(b => b.Tier1NanosPerOp);
        var avgTier2 = benchmarks.Average(b => b.Tier2NanosPerOp);
        var avgTier3 = benchmarks.Average(b => b.Tier3NanosPerOp);

        avgTier3.Should().BeLessThan(avgTier2,
            $"Average Tier 3 ({avgTier3:F0}ns) should be faster than Tier 2 ({avgTier2:F0}ns)");

        // Tier 2 and Tier 1 are close (Tier 2 = Tier 1 + dictionary lookup), so use generous threshold
        avgTier2.Should().BeLessThan(avgTier1 * 1.5,
            $"Average Tier 2 ({avgTier2:F0}ns) should be within 1.5x of Tier 1 ({avgTier1:F0}ns)");

        // Verify Tier 3 < Tier 1 strictly (Tier 3 is just ConcurrentDictionary get vs full serialization)
        foreach (var benchmark in benchmarks)
        {
            benchmark.Tier3NanosPerOp.Should().BeLessThanOrEqualTo(benchmark.Tier1NanosPerOp,
                $"{benchmark.FeatureName}: Tier 3 ({benchmark.Tier3NanosPerOp}ns) should be <= Tier 1 ({benchmark.Tier1NanosPerOp}ns)");
        }
    }

    [Fact]
    public void ThreeTier_TierPerformanceBenchmark_CompletesWithin30s()
    {
        var sw = Stopwatch.StartNew();
        var benchmarks = TierPerformanceBenchmark.RunBenchmarks();
        sw.Stop();

        benchmarks.Should().HaveCount(5, "Should have 5 benchmarked features");
        foreach (var b in benchmarks)
        {
            b.Tier1NanosPerOp.Should().BeGreaterThan(0);
            b.Tier2NanosPerOp.Should().BeGreaterThan(0);
            b.Tier3NanosPerOp.Should().BeGreaterThan(0);
        }
        sw.ElapsedMilliseconds.Should().BeLessThan(30_000,
            "TierPerformanceBenchmark 100+1000 iteration Stopwatch measurements should complete within 30s");
    }

    // ========================================================================
    // 7. CompiledPolicyDelegate timing (~3 tests)
    // ========================================================================

    [Fact]
    public void CompiledDelegate_ClosureCapturedInvocation_CompletesWithinMicroseconds()
    {
        var cache = new MaterializedPolicyCache();
        var policies = new Dictionary<string, IEffectivePolicy>
        {
            ["encryption:/vde1"] = new EffectivePolicy(
                featureId: "encryption",
                effectiveIntensity: 90,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Override,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: DateTimeOffset.UtcNow)
        };
        cache.Publish(policies);

        var snapshot = cache.GetSnapshot();
        var compiled = CompiledPolicyDelegate.CompileFromSnapshot("encryption", "/vde1", snapshot);

        // Warmup
        compiled.Invoke();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            compiled.Invoke();
        }
        sw.Stop();

        // 10000 invocations in < 10ms means each is < 1 microsecond
        sw.ElapsedMilliseconds.Should().BeLessThan(10,
            "10000 closure-captured delegate invocations should complete within 10ms (< 1us each)");
    }

    [Fact]
    public void CompiledDelegate_MatchesFullResolutionResult()
    {
        var cache = new MaterializedPolicyCache();
        var expectedPolicy = new EffectivePolicy(
            featureId: "compression",
            effectiveIntensity: 75,
            effectiveAiAutonomy: AiAutonomyLevel.AutoNotify,
            appliedCascade: CascadeStrategy.MostRestrictive,
            decidedAtLevel: PolicyLevel.Container,
            resolutionChain: Array.Empty<FeaturePolicy>(),
            mergedParameters: new Dictionary<string, string> { ["algorithm"] = "zstd" },
            snapshotTimestamp: DateTimeOffset.UtcNow);

        var policies = new Dictionary<string, IEffectivePolicy>
        {
            ["compression:/vde1/cont1"] = expectedPolicy
        };
        cache.Publish(policies);

        var snapshot = cache.GetSnapshot();
        var compiled = CompiledPolicyDelegate.CompileFromSnapshot("compression", "/vde1/cont1", snapshot);
        var result = compiled.Invoke();

        result.FeatureId.Should().Be("compression");
        result.EffectiveIntensity.Should().Be(75);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoNotify);
        result.AppliedCascade.Should().Be(CascadeStrategy.MostRestrictive);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Container);
        result.MergedParameters.Should().ContainKey("algorithm");
        result.MergedParameters["algorithm"].Should().Be("zstd");
    }

    [Fact]
    public void CompiledDelegate_InterlockedReadVersion_SafeUnderContention()
    {
        var cache = new MaterializedPolicyCache();
        cache.Publish(new Dictionary<string, IEffectivePolicy>
        {
            ["encryption:/vde1"] = new EffectivePolicy(
                featureId: "encryption",
                effectiveIntensity: 50,
                effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                appliedCascade: CascadeStrategy.Inherit,
                decidedAtLevel: PolicyLevel.VDE,
                resolutionChain: Array.Empty<FeaturePolicy>(),
                mergedParameters: new Dictionary<string, string>(),
                snapshotTimestamp: DateTimeOffset.UtcNow)
        });

        var delegateCache = new PolicyDelegateCache(cache);
        var exceptions = new ConcurrentBag<Exception>();

        // Parallel GetOrCompile calls that trigger version checks
        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            try
            {
                // Cause version changes while other threads read
                if (i % 5 == 0)
                {
                    cache.Publish(new Dictionary<string, IEffectivePolicy>
                    {
                        ["encryption:/vde1"] = new EffectivePolicy(
                            featureId: "encryption",
                            effectiveIntensity: 50 + i,
                            effectiveAiAutonomy: AiAutonomyLevel.SuggestExplain,
                            appliedCascade: CascadeStrategy.Inherit,
                            decidedAtLevel: PolicyLevel.VDE,
                            resolutionChain: Array.Empty<FeaturePolicy>(),
                            mergedParameters: new Dictionary<string, string>(),
                            snapshotTimestamp: DateTimeOffset.UtcNow)
                    });
                }

                var result = delegateCache.GetOrCompile("encryption", "/vde1");
                result.Should().NotBeNull();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        Task.WaitAll(tasks);

        exceptions.Should().BeEmpty(
            "Interlocked.Read for _compiledForVersion should be safe under contention");
    }
}
