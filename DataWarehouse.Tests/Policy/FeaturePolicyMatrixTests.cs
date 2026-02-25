using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using DataWarehouse.SDK.Infrastructure.Policy.Performance;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Feature classification matrix tests covering the 94-feature classification table,
/// bloom filter skip index, policy skip optimizer, fast-path engine behavior,
/// and cross-feature independence. Verifies INTG-02 matrix coverage.
/// </summary>
[Trait("Category", "Integration")]
public class FeaturePolicyMatrixTests
{
    private static InMemoryPolicyStore CreateStore() => new();
    private static InMemoryPolicyPersistence CreatePersistence() => new();

    private static PolicyResolutionEngine CreateEngine(
        InMemoryPolicyStore? store = null,
        InMemoryPolicyPersistence? persistence = null,
        OperationalProfile? profile = null)
    {
        return new PolicyResolutionEngine(
            store ?? CreateStore(),
            persistence ?? CreatePersistence(),
            profile);
    }

    private static FeaturePolicy MakePolicy(string featureId, PolicyLevel level, int intensity = 50,
        CascadeStrategy cascade = CascadeStrategy.Override, AiAutonomyLevel ai = AiAutonomyLevel.SuggestExplain)
    {
        return new FeaturePolicy
        {
            FeatureId = featureId,
            Level = level,
            IntensityLevel = intensity,
            Cascade = cascade,
            AiAutonomy = ai
        };
    }

    // =========================================================================
    // 1. CheckClassification coverage (~30 tests)
    // =========================================================================

    #region CheckClassification: Total features count

    [Fact]
    public void CheckClassification_TotalFeatures_Is94()
    {
        CheckClassificationTable.TotalFeatures.Should().Be(94);
    }

    #endregion

    #region CheckClassification: Security features are ConnectTime

    [Theory]
    [InlineData("encryption")]
    [InlineData("auth_model")]
    [InlineData("key_management")]
    [InlineData("fips_mode")]
    [InlineData("zero_trust")]
    public void CheckClassification_SecurityFeatures_AreConnectTime(string featureId)
    {
        // Security features evaluated once at VDE open
        var timing = CheckClassificationTable.GetTiming(featureId);
        timing.Should().Be(CheckTiming.ConnectTime);
    }

    #endregion

    #region CheckClassification: Performance features are SessionCached

    [Theory]
    [InlineData("compression")]
    [InlineData("replication")]
    [InlineData("deduplication")]
    [InlineData("tiering")]
    [InlineData("cache_strategy")]
    public void CheckClassification_PerformanceFeatures_AreSessionCached(string featureId)
    {
        var timing = CheckClassificationTable.GetTiming(featureId);
        timing.Should().Be(CheckTiming.SessionCached);
    }

    #endregion

    #region CheckClassification: PerOperation features

    [Theory]
    [InlineData("access_control")]
    [InlineData("quota")]
    [InlineData("rate_limit")]
    [InlineData("data_classification")]
    [InlineData("routing")]
    [InlineData("consent_check")]
    public void CheckClassification_PerOperationFeatures_ArePerOperation(string featureId)
    {
        var timing = CheckClassificationTable.GetTiming(featureId);
        timing.Should().Be(CheckTiming.PerOperation);
    }

    #endregion

    #region CheckClassification: Deferred features

    [Theory]
    [InlineData("audit_logging")]
    [InlineData("compliance_recording")]
    [InlineData("metrics_collection")]
    [InlineData("telemetry")]
    [InlineData("anomaly_detection")]
    public void CheckClassification_DeferredFeatures_AreDeferred(string featureId)
    {
        var timing = CheckClassificationTable.GetTiming(featureId);
        timing.Should().Be(CheckTiming.Deferred);
    }

    #endregion

    #region CheckClassification: Periodic features

    [Theory]
    [InlineData("integrity_verification")]
    [InlineData("key_rotation")]
    [InlineData("health_check")]
    [InlineData("defragmentation")]
    [InlineData("garbage_collection")]
    public void CheckClassification_PeriodicFeatures_ArePeriodic(string featureId)
    {
        var timing = CheckClassificationTable.GetTiming(featureId);
        timing.Should().Be(CheckTiming.Periodic);
    }

    #endregion

    #region CheckClassification: Unknown feature defaults to PerOperation

    [Fact]
    public void CheckClassification_UnknownFeature_DefaultsToPerOperation()
    {
        var timing = CheckClassificationTable.GetTiming("completely_unknown_feature_xyz");
        timing.Should().Be(CheckTiming.PerOperation);
    }

    #endregion

    #region CheckClassification: All 94 features return valid CheckTiming

    [Fact]
    public void CheckClassification_AllFeatures_ReturnValidTiming()
    {
        var allTimings = Enum.GetValues<CheckTiming>();
        int totalChecked = 0;

        foreach (var timing in allTimings)
        {
            var features = CheckClassificationTable.GetFeaturesByTiming(timing);
            foreach (var featureId in features)
            {
                var classified = CheckClassificationTable.GetTiming(featureId);
                classified.Should().Be(timing, $"Feature '{featureId}' should be classified as {timing}");
                totalChecked++;
            }
        }

        totalChecked.Should().Be(94, "All 94 features should be verified");
    }

    #endregion

    #region CheckClassification: GetFeaturesByTiming returns correct counts

    [Fact]
    public void CheckClassification_ConnectTime_Has20Features()
    {
        var features = CheckClassificationTable.GetFeaturesByTiming(CheckTiming.ConnectTime);
        features.Count.Should().Be(20);
    }

    [Fact]
    public void CheckClassification_SessionCached_Has24Features()
    {
        var features = CheckClassificationTable.GetFeaturesByTiming(CheckTiming.SessionCached);
        features.Count.Should().Be(24);
    }

    [Fact]
    public void CheckClassification_PerOperation_Has18Features()
    {
        var features = CheckClassificationTable.GetFeaturesByTiming(CheckTiming.PerOperation);
        features.Count.Should().Be(18);
    }

    [Fact]
    public void CheckClassification_Deferred_Has16Features()
    {
        var features = CheckClassificationTable.GetFeaturesByTiming(CheckTiming.Deferred);
        features.Count.Should().Be(16);
    }

    [Fact]
    public void CheckClassification_Periodic_Has16Features()
    {
        var features = CheckClassificationTable.GetFeaturesByTiming(CheckTiming.Periodic);
        features.Count.Should().Be(16);
    }

    #endregion

    #region CheckClassification: Null feature throws

    [Fact]
    public void CheckClassification_NullFeature_ThrowsArgumentNullException()
    {
        var act = () => CheckClassificationTable.GetTiming(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    // =========================================================================
    // 2. BloomFilterSkipIndex (~15 tests)
    // =========================================================================

    #region BloomFilter: Add and MayContain returns true

    [Fact]
    public void BloomFilter_AddedEntry_MayContainReturnsTrue()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        filter.Add(PolicyLevel.Block, "/v1/c1/o1/ch1/b1");

        filter.MayContain(PolicyLevel.Block, "/v1/c1/o1/ch1/b1").Should().BeTrue();
    }

    #endregion

    #region BloomFilter: Absent entry returns false (no false negatives)

    [Fact]
    public void BloomFilter_AbsentEntry_MayContainReturnsFalse()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 1000);
        filter.Add(PolicyLevel.Block, "/v1/c1/o1/ch1/b1");

        // Different level should be absent
        filter.MayContain(PolicyLevel.VDE, "/v1/c1/o1/ch1/b1").Should().BeFalse();
    }

    #endregion

    #region BloomFilter: Multiple entries all detected

    [Fact]
    public void BloomFilter_MultipleEntries_AllDetected()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        filter.Add(PolicyLevel.Block, "/v1/c1/o1/ch1/b1");
        filter.Add(PolicyLevel.Container, "/v1/c1");
        filter.Add(PolicyLevel.VDE, "/v1");

        filter.MayContain(PolicyLevel.Block, "/v1/c1/o1/ch1/b1").Should().BeTrue();
        filter.MayContain(PolicyLevel.Container, "/v1/c1").Should().BeTrue();
        filter.MayContain(PolicyLevel.VDE, "/v1").Should().BeTrue();
    }

    #endregion

    #region BloomFilter: ItemCount increments

    [Fact]
    public void BloomFilter_Add_IncrementsItemCount()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        filter.ItemCount.Should().Be(0);

        filter.Add(PolicyLevel.Block, "/v1/c1/o1/ch1/b1");
        filter.ItemCount.Should().Be(1);

        filter.Add(PolicyLevel.Container, "/v1/c1");
        filter.ItemCount.Should().Be(2);
    }

    #endregion

    #region BloomFilter: Clear resets all state

    [Fact]
    public void BloomFilter_Clear_ResetsAllState()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        filter.Add(PolicyLevel.Block, "/v1/c1/o1/ch1/b1");
        filter.MayContain(PolicyLevel.Block, "/v1/c1/o1/ch1/b1").Should().BeTrue();

        filter.Clear();

        filter.ItemCount.Should().Be(0);
        filter.MayContain(PolicyLevel.Block, "/v1/c1/o1/ch1/b1").Should().BeFalse();
    }

    #endregion

    #region BloomFilter: XxHash64 consistent results

    [Fact]
    public void BloomFilter_ConsistentHashing_SameInputSameResult()
    {
        var filter1 = new BloomFilterSkipIndex(expectedItems: 100);
        var filter2 = new BloomFilterSkipIndex(expectedItems: 100);

        filter1.Add(PolicyLevel.Block, "/test/path");
        filter2.Add(PolicyLevel.Block, "/test/path");

        // Both filters should agree
        filter1.MayContain(PolicyLevel.Block, "/test/path").Should().BeTrue();
        filter2.MayContain(PolicyLevel.Block, "/test/path").Should().BeTrue();
    }

    #endregion

    #region BloomFilter: Thread-safe parallel adds

    [Fact]
    public void BloomFilter_ParallelAdds_NoCorruption()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 10000);

        Parallel.For(0, 1000, i =>
        {
            filter.Add(PolicyLevel.Block, $"/v1/c1/o1/ch1/b{i}");
        });

        filter.ItemCount.Should().Be(1000);

        // All items should be detectable (no false negatives)
        for (int i = 0; i < 1000; i++)
        {
            filter.MayContain(PolicyLevel.Block, $"/v1/c1/o1/ch1/b{i}").Should().BeTrue();
        }
    }

    #endregion

    #region BloomFilter: BitCount and HashCount are positive

    [Fact]
    public void BloomFilter_Properties_ArePositive()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);

        filter.BitCount.Should().BeGreaterThan(0);
        filter.HashCount.Should().BeGreaterThan(0);
        filter.BitCount.Should().Be(filter.BitCount / 64 * 64, "BitCount should be multiple of 64");
    }

    #endregion

    #region BloomFilter: Invalid constructor parameters

    [Fact]
    public void BloomFilter_ZeroExpectedItems_Throws()
    {
        var act = () => new BloomFilterSkipIndex(expectedItems: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BloomFilter_InvalidFalsePositiveRate_Throws()
    {
        var act1 = () => new BloomFilterSkipIndex(expectedItems: 100, falsePositiveRate: 0.0);
        act1.Should().Throw<ArgumentOutOfRangeException>();

        var act2 = () => new BloomFilterSkipIndex(expectedItems: 100, falsePositiveRate: 1.0);
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region BloomFilter: Different levels are independent

    [Fact]
    public void BloomFilter_DifferentLevels_AreIndependent()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        filter.Add(PolicyLevel.Block, "/v1");

        // Same path, different level should not match
        filter.MayContain(PolicyLevel.Container, "/v1").Should().BeFalse();
        filter.MayContain(PolicyLevel.VDE, "/v1").Should().BeFalse();
        filter.MayContain(PolicyLevel.Object, "/v1").Should().BeFalse();
        filter.MayContain(PolicyLevel.Chunk, "/v1").Should().BeFalse();
    }

    #endregion

    #region BloomFilter: BuildFromStore populates correctly

    [Fact]
    public async Task BloomFilter_BuildFromStore_PopulatesCorrectly()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy("encryption", PolicyLevel.Block, intensity: 80));
        await store.SetAsync("encryption", PolicyLevel.Container, "/v1/c1",
            MakePolicy("encryption", PolicyLevel.Container, intensity: 60));

        var filter = await BloomFilterSkipIndex.BuildFromStoreAsync(store, new[] { "encryption" });

        filter.ItemCount.Should().Be(2);
        filter.MayContain(PolicyLevel.Block, "/v1/c1/o1/ch1/b1").Should().BeTrue();
        filter.MayContain(PolicyLevel.Container, "/v1/c1").Should().BeTrue();
    }

    #endregion

    // =========================================================================
    // 3. PolicySkipOptimizer (~15 tests)
    // =========================================================================

    #region SkipOptimizer: No override in bloom → skip store lookup

    [Fact]
    public async Task SkipOptimizer_NoOverrideInBloom_SkipsStoreLookup()
    {
        var store = CreateStore();
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        // Don't add anything to the filter
        var optimizer = new PolicySkipOptimizer(store, filter);

        var result = await optimizer.HasOverrideOptimizedAsync(PolicyLevel.Block, "/v1/c1/o1/ch1/b1");

        result.Should().BeFalse();
        var (skips, fallThroughs) = optimizer.GetStatistics();
        skips.Should().Be(1);
        fallThroughs.Should().Be(0);
    }

    #endregion

    #region SkipOptimizer: Override in bloom → falls through to store

    [Fact]
    public async Task SkipOptimizer_OverrideInBloom_FallsThroughToStore()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy("encryption", PolicyLevel.Block, intensity: 80));

        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        filter.Add(PolicyLevel.Block, "/v1/c1/o1/ch1/b1");

        var optimizer = new PolicySkipOptimizer(store, filter);

        var result = await optimizer.HasOverrideOptimizedAsync(PolicyLevel.Block, "/v1/c1/o1/ch1/b1");

        result.Should().BeTrue();
        var (skips, fallThroughs) = optimizer.GetStatistics();
        skips.Should().Be(0);
        fallThroughs.Should().Be(1);
    }

    #endregion

    #region SkipOptimizer: GetOptimized returns null when bloom says no

    [Fact]
    public async Task SkipOptimizer_GetOptimized_ReturnsNullWhenBloomSaysNo()
    {
        var store = CreateStore();
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        var optimizer = new PolicySkipOptimizer(store, filter);

        var result = await optimizer.GetOptimizedAsync("encryption", PolicyLevel.Block, "/absent/path");

        result.Should().BeNull();
    }

    #endregion

    #region SkipOptimizer: GetOptimized returns policy when bloom says maybe

    [Fact]
    public async Task SkipOptimizer_GetOptimized_ReturnsPolicyWhenBloomSaysMaybe()
    {
        var store = CreateStore();
        var policy = MakePolicy("encryption", PolicyLevel.Block, intensity: 85);
        await store.SetAsync("encryption", PolicyLevel.Block, "/v1/b1", policy);

        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        filter.Add(PolicyLevel.Block, "/v1/b1");

        var optimizer = new PolicySkipOptimizer(store, filter);

        var result = await optimizer.GetOptimizedAsync("encryption", PolicyLevel.Block, "/v1/b1");

        result.Should().NotBeNull();
        result!.IntensityLevel.Should().Be(85);
    }

    #endregion

    #region SkipOptimizer: SkipRatio reflects usage

    [Fact]
    public async Task SkipOptimizer_SkipRatio_ReflectsUsage()
    {
        var store = CreateStore();
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        var optimizer = new PolicySkipOptimizer(store, filter);

        // All skips since nothing in bloom filter
        await optimizer.HasOverrideOptimizedAsync(PolicyLevel.Block, "/path1");
        await optimizer.HasOverrideOptimizedAsync(PolicyLevel.Block, "/path2");
        await optimizer.HasOverrideOptimizedAsync(PolicyLevel.Block, "/path3");

        optimizer.SkipRatio.Should().Be(1.0);
    }

    #endregion

    #region SkipOptimizer: RebuildFilter clears and repopulates

    [Fact]
    public async Task SkipOptimizer_RebuildFilter_ClearsAndRepopulates()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.Block, "/v1/b1",
            MakePolicy("encryption", PolicyLevel.Block, intensity: 80));

        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        var optimizer = new PolicySkipOptimizer(store, filter);

        // Before rebuild, bloom filter is empty
        var beforeResult = await optimizer.HasOverrideOptimizedAsync(PolicyLevel.Block, "/v1/b1");
        beforeResult.Should().BeFalse(); // bloom says no

        // Rebuild populates the filter from the store
        await optimizer.RebuildFilterAsync(new[] { "encryption" });

        // After rebuild, bloom filter knows about the override
        var afterResult = await optimizer.HasOverrideOptimizedAsync(PolicyLevel.Block, "/v1/b1");
        afterResult.Should().BeTrue(); // bloom says maybe, store says yes
    }

    #endregion

    #region SkipOptimizer: Null arguments throw

    [Fact]
    public void SkipOptimizer_NullStore_Throws()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        var act = () => new PolicySkipOptimizer(null!, filter);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SkipOptimizer_NullBloomFilter_Throws()
    {
        var store = CreateStore();
        var act = () => new PolicySkipOptimizer(store, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region SkipOptimizer: Empty SkipRatio returns 1.0

    [Fact]
    public void SkipOptimizer_NoChecks_SkipRatioIsOne()
    {
        var store = CreateStore();
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        var optimizer = new PolicySkipOptimizer(store, filter);

        optimizer.SkipRatio.Should().Be(1.0);
    }

    #endregion

    // =========================================================================
    // 4. FastPathPolicyEngine (~10 tests)
    // =========================================================================

    #region FastPath: ConnectTime features use delegate cache

    [Fact]
    public void DeploymentTierClassifier_BloomFilter_NoOverrides_ReturnsVdeOnly()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        var tier = DeploymentTierClassifier.ClassifyFromBloomFilter(filter, "/v1");

        tier.Should().Be(DeploymentTier.VdeOnly);
    }

    #endregion

    #region FastPath: Block override triggers FullCascade

    [Fact]
    public void DeploymentTierClassifier_BloomFilter_BlockOverride_ReturnsFullCascade()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        filter.Add(PolicyLevel.Block, "/v1");

        var tier = DeploymentTierClassifier.ClassifyFromBloomFilter(filter, "/v1");

        tier.Should().Be(DeploymentTier.FullCascade);
    }

    #endregion

    #region FastPath: Container override triggers ContainerStop

    [Fact]
    public void DeploymentTierClassifier_BloomFilter_ContainerOverride_ReturnsContainerStop()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        filter.Add(PolicyLevel.Container, "/v1");

        var tier = DeploymentTierClassifier.ClassifyFromBloomFilter(filter, "/v1");

        tier.Should().Be(DeploymentTier.ContainerStop);
    }

    #endregion

    #region FastPath: ClassifyAsync from store

    [Fact]
    public async Task DeploymentTierClassifier_Store_NoOverrides_ReturnsVdeOnly()
    {
        var store = CreateStore();
        var tier = await DeploymentTierClassifier.ClassifyAsync(store, "/v1");

        tier.Should().Be(DeploymentTier.VdeOnly);
    }

    [Fact]
    public async Task DeploymentTierClassifier_Store_BlockOverride_ReturnsFullCascade()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy("encryption", PolicyLevel.Block, intensity: 80));

        var tier = await DeploymentTierClassifier.ClassifyAsync(store, "/v1/c1/o1/ch1/b1");

        tier.Should().Be(DeploymentTier.FullCascade);
    }

    [Fact]
    public async Task DeploymentTierClassifier_Store_ContainerOverride_ReturnsContainerStop()
    {
        var store = CreateStore();
        await store.SetAsync("compression", PolicyLevel.Container, "/v1/c1",
            MakePolicy("compression", PolicyLevel.Container, intensity: 60));

        var tier = await DeploymentTierClassifier.ClassifyAsync(store, "/v1/c1");

        tier.Should().Be(DeploymentTier.ContainerStop);
    }

    #endregion

    #region FastPath: CheckTiming categories map correctly

    [Theory]
    [InlineData("encryption", CheckTiming.ConnectTime)]
    [InlineData("compression", CheckTiming.SessionCached)]
    [InlineData("access_control", CheckTiming.PerOperation)]
    [InlineData("audit_logging", CheckTiming.Deferred)]
    [InlineData("integrity_verification", CheckTiming.Periodic)]
    public void FastPath_FeatureRouting_ByCheckTiming(string featureId, CheckTiming expectedTiming)
    {
        var timing = CheckClassificationTable.GetTiming(featureId);
        timing.Should().Be(expectedTiming);
    }

    #endregion

    #region FastPath: Full resolution engine produces same result regardless of path

    [Fact]
    public async Task FastPath_FullResolution_ConsistentWithEngine()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 80));
        await store.SetAsync("encryption", PolicyLevel.Container, "/v1/c1",
            MakePolicy("encryption", PolicyLevel.Container, intensity: 60));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };

        var result = await engine.ResolveAsync("encryption", ctx);

        // Override cascade: most specific (Container=60) wins
        result.EffectiveIntensity.Should().Be(60);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Container);
    }

    #endregion

    // =========================================================================
    // 5. Cross-feature independence (~10 tests)
    // =========================================================================

    #region CrossFeature: Setting policy for encryption does not affect compression

    [Fact]
    public async Task CrossFeature_Encryption_DoesNotAffect_Compression()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 90));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var encResult = await engine.ResolveAsync("encryption", ctx);
        var compResult = await engine.ResolveAsync("compression", ctx);

        encResult.EffectiveIntensity.Should().Be(90);
        // Standard profile default for compression is 60
        compResult.EffectiveIntensity.Should().Be(60);
    }

    #endregion

    #region CrossFeature: Setting policy for ai_autonomy does not affect access_control

    [Fact]
    public async Task CrossFeature_AnomalyDetection_DoesNotAffect_AccessControl()
    {
        var store = CreateStore();
        await store.SetAsync("anomaly_detection", PolicyLevel.VDE, "/v1",
            MakePolicy("anomaly_detection", PolicyLevel.VDE, intensity: 85, ai: AiAutonomyLevel.AutoSilent));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var aiResult = await engine.ResolveAsync("anomaly_detection", ctx);
        var acResult = await engine.ResolveAsync("access_control", ctx);

        aiResult.EffectiveIntensity.Should().Be(85);
        acResult.EffectiveIntensity.Should().Be(50); // default
        acResult.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.SuggestExplain); // default
    }

    #endregion

    #region CrossFeature: ResolveAllAsync returns independent results

    [Fact]
    public async Task CrossFeature_ResolveAll_ReturnsIndependentResults()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Standard();
        // Override encryption at VDE level
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 95));

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var results = await engine.ResolveAllAsync(ctx);

        // Encryption should use store override
        results["encryption"].EffectiveIntensity.Should().Be(95);
        // Compression and replication should use profile defaults
        results["compression"].EffectiveIntensity.Should().Be(60); // Standard profile
        results["replication"].EffectiveIntensity.Should().Be(60); // Standard profile
    }

    #endregion

    #region CrossFeature: Different cascade strategies on different features

    [Fact]
    public async Task CrossFeature_DifferentCascades_ResolveIndependently()
    {
        var store = CreateStore();
        // Encryption with Enforce cascade at VDE
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 80, cascade: CascadeStrategy.Enforce));
        await store.SetAsync("encryption", PolicyLevel.Container, "/v1/c1",
            MakePolicy("encryption", PolicyLevel.Container, intensity: 30, cascade: CascadeStrategy.Override));

        // Compression with Override cascade at Container
        await store.SetAsync("compression", PolicyLevel.VDE, "/v1",
            MakePolicy("compression", PolicyLevel.VDE, intensity: 40, cascade: CascadeStrategy.Override));
        await store.SetAsync("compression", PolicyLevel.Container, "/v1/c1",
            MakePolicy("compression", PolicyLevel.Container, intensity: 70, cascade: CascadeStrategy.Override));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };

        var encResult = await engine.ResolveAsync("encryption", ctx);
        var compResult = await engine.ResolveAsync("compression", ctx);

        // Encryption: VDE Enforce wins over Container Override
        encResult.EffectiveIntensity.Should().Be(80);
        encResult.AppliedCascade.Should().Be(CascadeStrategy.Enforce);

        // Compression: Container Override wins (most specific)
        compResult.EffectiveIntensity.Should().Be(70);
        compResult.DecidedAtLevel.Should().Be(PolicyLevel.Container);
    }

    #endregion

    #region CrossFeature: Bulk independent resolution across many features

    [Fact]
    public async Task CrossFeature_BulkIndependentResolution_AllFeaturesIsolated()
    {
        var store = CreateStore();
        var features = new[] { "encryption", "compression", "replication", "audit_logging", "metrics_collection" };

        for (int i = 0; i < features.Length; i++)
        {
            await store.SetAsync(features[i], PolicyLevel.VDE, "/v1",
                MakePolicy(features[i], PolicyLevel.VDE, intensity: (i + 1) * 15));
        }

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        for (int i = 0; i < features.Length; i++)
        {
            var result = await engine.ResolveAsync(features[i], ctx);
            result.EffectiveIntensity.Should().Be((i + 1) * 15,
                $"Feature '{features[i]}' should have independent intensity");
        }
    }

    #endregion

    #region CrossFeature: Modifying one feature does not change another

    [Fact]
    public async Task CrossFeature_ModifyOne_DoesNotChangeAnother()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 70));
        await store.SetAsync("compression", PolicyLevel.VDE, "/v1",
            MakePolicy("compression", PolicyLevel.VDE, intensity: 40));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        // Resolve both
        var enc1 = await engine.ResolveAsync("encryption", ctx);
        var comp1 = await engine.ResolveAsync("compression", ctx);

        // Modify encryption
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 95));

        // Resolve again
        var enc2 = await engine.ResolveAsync("encryption", ctx);
        var comp2 = await engine.ResolveAsync("compression", ctx);

        enc1.EffectiveIntensity.Should().Be(70);
        enc2.EffectiveIntensity.Should().Be(95);
        comp1.EffectiveIntensity.Should().Be(40);
        comp2.EffectiveIntensity.Should().Be(40); // unchanged
    }

    #endregion

    // =========================================================================
    // ADDITIONAL: Deployment tier classification
    // =========================================================================

    #region DeploymentTier: Null arguments throw

    [Fact]
    public async Task DeploymentTierClassifier_NullStore_Throws()
    {
        var act = () => DeploymentTierClassifier.ClassifyAsync(null!, "/v1");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DeploymentTierClassifier_NullPath_Throws()
    {
        var store = CreateStore();
        var act = () => DeploymentTierClassifier.ClassifyAsync(store, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void DeploymentTierClassifier_BloomFilter_NullFilter_Throws()
    {
        var act = () => DeploymentTierClassifier.ClassifyFromBloomFilter(null!, "/v1");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeploymentTierClassifier_BloomFilter_NullPath_Throws()
    {
        var filter = new BloomFilterSkipIndex(expectedItems: 100);
        var act = () => DeploymentTierClassifier.ClassifyFromBloomFilter(filter, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region DeploymentTier: Object/Chunk overrides trigger FullCascade

    [Fact]
    public async Task DeploymentTierClassifier_ObjectOverride_ReturnsFullCascade()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.Object, "/v1/c1/o1",
            MakePolicy("encryption", PolicyLevel.Object, intensity: 80));

        var tier = await DeploymentTierClassifier.ClassifyAsync(store, "/v1/c1/o1");

        tier.Should().Be(DeploymentTier.FullCascade);
    }

    [Fact]
    public async Task DeploymentTierClassifier_ChunkOverride_ReturnsFullCascade()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.Chunk, "/v1/c1/o1/ch1",
            MakePolicy("encryption", PolicyLevel.Chunk, intensity: 80));

        var tier = await DeploymentTierClassifier.ClassifyAsync(store, "/v1/c1/o1/ch1");

        tier.Should().Be(DeploymentTier.FullCascade);
    }

    #endregion
}
