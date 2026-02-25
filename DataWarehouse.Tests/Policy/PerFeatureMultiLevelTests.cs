using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Per-feature multi-level resolution tests covering all 7 feature categories across all 5 PolicyLevel values
/// with all cascade strategies. Verifies INTG-02 per-feature coverage.
/// </summary>
[Trait("Category", "Integration")]
public class PerFeatureMultiLevelTests
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

    // =========================================================================
    // 1. SECURITY FEATURES (~40 tests)
    // =========================================================================

    #region Security: Override cascade — Block override wins

    [Theory]
    [InlineData("encryption")]
    [InlineData("access_control")]
    [InlineData("auth_model")]
    [InlineData("key_management")]
    [InlineData("fips_mode")]
    [InlineData("zero_trust")]
    [InlineData("tamper_detection")]
    [InlineData("tls_policy")]
    public async Task Security_Override_BlockOverrideWinsOverVde(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", MakePolicy(featureId, PolicyLevel.VDE, intensity: 40));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1", MakePolicy(featureId, PolicyLevel.Block, intensity: 95));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(95);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
    }

    #endregion

    #region Security: Enforce cascade — VDE Enforce wins over Block Override

    [Theory]
    [InlineData("encryption")]
    [InlineData("access_control")]
    [InlineData("auth_model")]
    [InlineData("key_management")]
    [InlineData("fips_mode")]
    [InlineData("zero_trust")]
    [InlineData("tamper_detection")]
    [InlineData("tls_policy")]
    public async Task Security_Enforce_VdeEnforceWinsOverBlockOverride(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 80, cascade: CascadeStrategy.Enforce));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy(featureId, PolicyLevel.Block, intensity: 30, cascade: CascadeStrategy.Override));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(80);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    #endregion

    #region Security: MostRestrictive cascade — Tightest wins

    [Theory]
    [InlineData("encryption")]
    [InlineData("access_control")]
    [InlineData("auth_model")]
    [InlineData("key_management")]
    [InlineData("fips_mode")]
    [InlineData("zero_trust")]
    [InlineData("tamper_detection")]
    [InlineData("tls_policy")]
    public async Task Security_MostRestrictive_TightestWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 90, cascade: CascadeStrategy.MostRestrictive));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1",
            MakePolicy(featureId, PolicyLevel.Container, intensity: 60, cascade: CascadeStrategy.MostRestrictive));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        // MostRestrictive picks the lowest intensity (most restrictive/tightest)
        result.EffectiveIntensity.Should().Be(60);
        result.AppliedCascade.Should().Be(CascadeStrategy.MostRestrictive);
    }

    #endregion

    #region Security: Inherit cascade — Child inherits VDE

    [Theory]
    [InlineData("encryption")]
    [InlineData("access_control")]
    [InlineData("auth_model")]
    [InlineData("key_management")]
    [InlineData("fips_mode")]
    [InlineData("zero_trust")]
    [InlineData("tamper_detection")]
    [InlineData("tls_policy")]
    public async Task Security_Inherit_ChildInheritsVde(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 75, cascade: CascadeStrategy.Inherit));

        var engine = CreateEngine(store);
        // Resolve at Container level — no explicit Container policy, should inherit VDE
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(75);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    #endregion

    // =========================================================================
    // 2. PERFORMANCE FEATURES (~30 tests)
    // =========================================================================

    #region Performance: Override cascade

    [Theory]
    [InlineData("compression")]
    [InlineData("deduplication")]
    [InlineData("tiering")]
    [InlineData("cache_strategy")]
    [InlineData("indexing")]
    public async Task Performance_Override_BlockOverrideWinsOverVde(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", MakePolicy(featureId, PolicyLevel.VDE, intensity: 40));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1", MakePolicy(featureId, PolicyLevel.Block, intensity: 85));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(85);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
    }

    #endregion

    #region Performance: Enforce cascade

    [Theory]
    [InlineData("compression")]
    [InlineData("deduplication")]
    [InlineData("tiering")]
    [InlineData("cache_strategy")]
    [InlineData("indexing")]
    public async Task Performance_Enforce_VdeEnforceWinsOverBlockOverride(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 70, cascade: CascadeStrategy.Enforce));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy(featureId, PolicyLevel.Block, intensity: 20, cascade: CascadeStrategy.Override));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(70);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    #endregion

    #region Performance: MostRestrictive cascade

    [Theory]
    [InlineData("compression")]
    [InlineData("deduplication")]
    [InlineData("tiering")]
    [InlineData("cache_strategy")]
    [InlineData("indexing")]
    public async Task Performance_MostRestrictive_TightestWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 80, cascade: CascadeStrategy.MostRestrictive));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1",
            MakePolicy(featureId, PolicyLevel.Container, intensity: 50, cascade: CascadeStrategy.MostRestrictive));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        // MostRestrictive picks the lowest intensity (most restrictive)
        result.EffectiveIntensity.Should().Be(50);
        result.AppliedCascade.Should().Be(CascadeStrategy.MostRestrictive);
    }

    #endregion

    #region Performance: Inherit cascade

    [Theory]
    [InlineData("compression")]
    [InlineData("deduplication")]
    [InlineData("tiering")]
    [InlineData("cache_strategy")]
    [InlineData("indexing")]
    public async Task Performance_Inherit_ChildInheritsVde(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 65, cascade: CascadeStrategy.Inherit));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(65);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    #endregion

    #region Performance: Speed profile high-intensity defaults

    [Theory]
    [InlineData("compression", 30)]
    [InlineData("encryption", 30)]
    [InlineData("replication", 20)]
    public async Task Performance_SpeedProfile_ProducesHighThroughputDefaults(string featureId, int expectedIntensity)
    {
        var store = CreateStore();
        var profile = OperationalProfile.Speed();
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(expectedIntensity);
    }

    #endregion

    #region Performance: Paranoid profile reduces perf features

    [Theory]
    [InlineData("compression", 90)]
    [InlineData("encryption", 100)]
    [InlineData("replication", 100)]
    public async Task Performance_ParanoidProfile_IncreasesSecurityReducesThroughput(string featureId, int expectedIntensity)
    {
        var store = CreateStore();
        var profile = OperationalProfile.Paranoid();
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(expectedIntensity);
    }

    #endregion

    // =========================================================================
    // 3. STORAGE FEATURES (~30 tests)
    // =========================================================================

    #region Storage: Override cascade

    [Theory]
    [InlineData("replication")]
    [InlineData("snapshot_policy")]
    [InlineData("erasure_coding")]
    [InlineData("storage_backend")]
    [InlineData("branching")]
    public async Task Storage_Override_BlockOverrideWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", MakePolicy(featureId, PolicyLevel.VDE, intensity: 35));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1", MakePolicy(featureId, PolicyLevel.Block, intensity: 90));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(90);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
    }

    #endregion

    #region Storage: Enforce cascade

    [Theory]
    [InlineData("replication")]
    [InlineData("snapshot_policy")]
    [InlineData("erasure_coding")]
    [InlineData("storage_backend")]
    [InlineData("branching")]
    public async Task Storage_Enforce_VdeEnforceWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 75, cascade: CascadeStrategy.Enforce));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy(featureId, PolicyLevel.Block, intensity: 20, cascade: CascadeStrategy.Override));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(75);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    #endregion

    #region Storage: MostRestrictive cascade

    [Theory]
    [InlineData("replication")]
    [InlineData("snapshot_policy")]
    [InlineData("erasure_coding")]
    [InlineData("storage_backend")]
    [InlineData("branching")]
    public async Task Storage_MostRestrictive_TightestWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 85, cascade: CascadeStrategy.MostRestrictive));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1",
            MakePolicy(featureId, PolicyLevel.Container, intensity: 55, cascade: CascadeStrategy.MostRestrictive));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        // MostRestrictive picks the lowest intensity (most restrictive)
        result.EffectiveIntensity.Should().Be(55);
    }

    #endregion

    #region Storage: Inherit cascade

    [Theory]
    [InlineData("replication")]
    [InlineData("snapshot_policy")]
    [InlineData("erasure_coding")]
    [InlineData("storage_backend")]
    [InlineData("branching")]
    public async Task Storage_Inherit_ChildInheritsVde(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 70, cascade: CascadeStrategy.Inherit));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(70);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    #endregion

    #region Storage: Merge cascade — parameters combined

    [Theory]
    [InlineData("replication")]
    [InlineData("snapshot_policy")]
    [InlineData("erasure_coding")]
    [InlineData("storage_backend")]
    [InlineData("branching")]
    public async Task Storage_Merge_ParametersCombined(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 50, cascade: CascadeStrategy.Merge,
                customParams: new Dictionary<string, string> { ["block_size"] = "4096", ["mode"] = "sync" }));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1",
            MakePolicy(featureId, PolicyLevel.Container, intensity: 60, cascade: CascadeStrategy.Merge,
                customParams: new Dictionary<string, string> { ["thin_provisioning"] = "true" }));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.MergedParameters.Should().ContainKey("block_size");
        result.MergedParameters["block_size"].Should().Be("4096");
        result.MergedParameters.Should().ContainKey("thin_provisioning");
        result.MergedParameters["thin_provisioning"].Should().Be("true");
        result.MergedParameters.Should().ContainKey("mode");
    }

    #endregion

    // =========================================================================
    // 4. AI FEATURES (~25 tests)
    // =========================================================================

    #region AI: Override cascade

    [Theory]
    [InlineData("anomaly_detection")]
    [InlineData("cost_routing")]
    [InlineData("lineage_tracking")]
    [InlineData("threat_detection")]
    [InlineData("data_classification")]
    public async Task AI_Override_BlockOverrideWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", MakePolicy(featureId, PolicyLevel.VDE, intensity: 30, ai: AiAutonomyLevel.Suggest));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy(featureId, PolicyLevel.Block, intensity: 80, ai: AiAutonomyLevel.AutoNotify));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(80);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoNotify);
    }

    #endregion

    #region AI: Enforce cascade

    [Theory]
    [InlineData("anomaly_detection")]
    [InlineData("cost_routing")]
    [InlineData("lineage_tracking")]
    [InlineData("threat_detection")]
    [InlineData("data_classification")]
    public async Task AI_Enforce_VdeEnforceWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 60, cascade: CascadeStrategy.Enforce, ai: AiAutonomyLevel.ManualOnly));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy(featureId, PolicyLevel.Block, intensity: 90, cascade: CascadeStrategy.Override, ai: AiAutonomyLevel.AutoSilent));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(60);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    #endregion

    #region AI: MostRestrictive cascade

    [Theory]
    [InlineData("anomaly_detection")]
    [InlineData("cost_routing")]
    [InlineData("lineage_tracking")]
    [InlineData("threat_detection")]
    [InlineData("data_classification")]
    public async Task AI_MostRestrictive_TightestWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 70, cascade: CascadeStrategy.MostRestrictive, ai: AiAutonomyLevel.Suggest));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1",
            MakePolicy(featureId, PolicyLevel.Container, intensity: 40, cascade: CascadeStrategy.MostRestrictive, ai: AiAutonomyLevel.AutoNotify));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        // MostRestrictive picks the lowest intensity and lowest AI autonomy
        result.EffectiveIntensity.Should().Be(40);
    }

    #endregion

    #region AI: Inherit cascade

    [Theory]
    [InlineData("anomaly_detection")]
    [InlineData("cost_routing")]
    [InlineData("lineage_tracking")]
    [InlineData("threat_detection")]
    [InlineData("data_classification")]
    public async Task AI_Inherit_ChildInheritsVde(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 55, cascade: CascadeStrategy.Inherit, ai: AiAutonomyLevel.Suggest));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(55);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    #endregion

    #region AI: ManualOnly at VDE enforced down to Block

    [Theory]
    [InlineData("anomaly_detection")]
    [InlineData("cost_routing")]
    [InlineData("lineage_tracking")]
    [InlineData("threat_detection")]
    [InlineData("data_classification")]
    public async Task AI_ManualOnlyAtVde_EnforcedToBlock(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 50, cascade: CascadeStrategy.Enforce, ai: AiAutonomyLevel.ManualOnly));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy(featureId, PolicyLevel.Block, intensity: 80, cascade: CascadeStrategy.Override, ai: AiAutonomyLevel.AutoSilent));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    #endregion

    #region AI: AutoSilent from Speed profile

    [Fact]
    public async Task AI_SpeedProfile_AutoSilentAvailable()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Speed();
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var result = await engine.ResolveAsync("compression", ctx);

        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoSilent);
    }

    #endregion

    // =========================================================================
    // 5. COMPLIANCE FEATURES (~25 tests)
    // =========================================================================

    #region Compliance: Override cascade

    [Theory]
    [InlineData("audit_logging")]
    [InlineData("compliance_recording")]
    [InlineData("retention_enforcement")]
    [InlineData("billing")]
    [InlineData("usage_analytics")]
    public async Task Compliance_Override_BlockOverrideWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", MakePolicy(featureId, PolicyLevel.VDE, intensity: 40));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1", MakePolicy(featureId, PolicyLevel.Block, intensity: 95));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(95);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
    }

    #endregion

    #region Compliance: Enforce cascade

    [Theory]
    [InlineData("audit_logging")]
    [InlineData("compliance_recording")]
    [InlineData("retention_enforcement")]
    [InlineData("billing")]
    [InlineData("usage_analytics")]
    public async Task Compliance_Enforce_VdeEnforceWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 90, cascade: CascadeStrategy.Enforce));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy(featureId, PolicyLevel.Block, intensity: 20, cascade: CascadeStrategy.Override));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(90);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    #endregion

    #region Compliance: MostRestrictive cascade

    [Theory]
    [InlineData("audit_logging")]
    [InlineData("compliance_recording")]
    [InlineData("retention_enforcement")]
    [InlineData("billing")]
    [InlineData("usage_analytics")]
    public async Task Compliance_MostRestrictive_TightestWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 85, cascade: CascadeStrategy.MostRestrictive));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1",
            MakePolicy(featureId, PolicyLevel.Container, intensity: 55, cascade: CascadeStrategy.MostRestrictive));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        // MostRestrictive picks the lowest intensity (most restrictive)
        result.EffectiveIntensity.Should().Be(55);
    }

    #endregion

    #region Compliance: Inherit cascade

    [Theory]
    [InlineData("audit_logging")]
    [InlineData("compliance_recording")]
    [InlineData("retention_enforcement")]
    [InlineData("billing")]
    [InlineData("usage_analytics")]
    public async Task Compliance_Inherit_ChildInheritsVde(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 80, cascade: CascadeStrategy.Inherit));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(80);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    #endregion

    #region Compliance: Strict/Paranoid profiles enforce compliance features

    [Fact]
    public async Task Compliance_StrictProfile_EnforcesEncryption()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Strict();
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(90);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public async Task Compliance_ParanoidProfile_EnforcesMaxSecurity()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Paranoid();
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(100);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    #endregion

    // =========================================================================
    // 6. PRIVACY FEATURES (~20 tests)
    // =========================================================================

    #region Privacy: Override cascade

    [Theory]
    [InlineData("redaction")]
    [InlineData("tokenization")]
    [InlineData("masking")]
    [InlineData("consent_check")]
    public async Task Privacy_Override_BlockOverrideWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", MakePolicy(featureId, PolicyLevel.VDE, intensity: 40));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1", MakePolicy(featureId, PolicyLevel.Block, intensity: 92));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(92);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
    }

    #endregion

    #region Privacy: Enforce cascade

    [Theory]
    [InlineData("redaction")]
    [InlineData("tokenization")]
    [InlineData("masking")]
    [InlineData("consent_check")]
    public async Task Privacy_Enforce_VdeEnforceWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 85, cascade: CascadeStrategy.Enforce));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy(featureId, PolicyLevel.Block, intensity: 20, cascade: CascadeStrategy.Override));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(85);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    #endregion

    #region Privacy: MostRestrictive cascade

    [Theory]
    [InlineData("redaction")]
    [InlineData("tokenization")]
    [InlineData("masking")]
    [InlineData("consent_check")]
    public async Task Privacy_MostRestrictive_TightestWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 80, cascade: CascadeStrategy.MostRestrictive));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1",
            MakePolicy(featureId, PolicyLevel.Container, intensity: 45, cascade: CascadeStrategy.MostRestrictive));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        // MostRestrictive picks the lowest intensity (most restrictive)
        result.EffectiveIntensity.Should().Be(45);
    }

    #endregion

    #region Privacy: Inherit cascade

    [Theory]
    [InlineData("redaction")]
    [InlineData("tokenization")]
    [InlineData("masking")]
    [InlineData("consent_check")]
    public async Task Privacy_Inherit_ChildInheritsVde(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 70, cascade: CascadeStrategy.Inherit));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(70);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    #endregion

    // =========================================================================
    // 7. OBSERVABILITY FEATURES (~15 tests)
    // =========================================================================

    #region Observability: Override cascade

    [Theory]
    [InlineData("metrics_collection")]
    [InlineData("telemetry")]
    [InlineData("health_check")]
    public async Task Observability_Override_BlockOverrideWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", MakePolicy(featureId, PolicyLevel.VDE, intensity: 30));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1", MakePolicy(featureId, PolicyLevel.Block, intensity: 88));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(88);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
    }

    #endregion

    #region Observability: Enforce cascade

    [Theory]
    [InlineData("metrics_collection")]
    [InlineData("telemetry")]
    [InlineData("health_check")]
    public async Task Observability_Enforce_VdeEnforceWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 65, cascade: CascadeStrategy.Enforce));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy(featureId, PolicyLevel.Block, intensity: 10, cascade: CascadeStrategy.Override));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(65);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    #endregion

    #region Observability: MostRestrictive cascade

    [Theory]
    [InlineData("metrics_collection")]
    [InlineData("telemetry")]
    [InlineData("health_check")]
    public async Task Observability_MostRestrictive_TightestWins(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 75, cascade: CascadeStrategy.MostRestrictive));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1",
            MakePolicy(featureId, PolicyLevel.Container, intensity: 45, cascade: CascadeStrategy.MostRestrictive));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        // MostRestrictive picks the lowest intensity (most restrictive)
        result.EffectiveIntensity.Should().Be(45);
    }

    #endregion

    #region Observability: Inherit cascade

    [Theory]
    [InlineData("metrics_collection")]
    [InlineData("telemetry")]
    [InlineData("health_check")]
    public async Task Observability_Inherit_ChildInheritsVde(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 60, cascade: CascadeStrategy.Inherit));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(60);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    #endregion

    // =========================================================================
    // 8. CROSS-LEVEL CONSISTENCY (~15 tests)
    // =========================================================================

    #region Cross-level: All 5 levels simultaneously

    [Theory]
    [InlineData("encryption")]
    [InlineData("compression")]
    [InlineData("replication")]
    public async Task CrossLevel_AllFiveLevelsSet_MostSpecificWinsWithOverride(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", MakePolicy(featureId, PolicyLevel.VDE, intensity: 10));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1", MakePolicy(featureId, PolicyLevel.Container, intensity: 20));
        await store.SetAsync(featureId, PolicyLevel.Object, "/v1/c1/o1", MakePolicy(featureId, PolicyLevel.Object, intensity: 30));
        await store.SetAsync(featureId, PolicyLevel.Chunk, "/v1/c1/o1/ch1", MakePolicy(featureId, PolicyLevel.Chunk, intensity: 40));
        await store.SetAsync(featureId, PolicyLevel.Block, "/v1/c1/o1/ch1/b1", MakePolicy(featureId, PolicyLevel.Block, intensity: 50));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(50);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
        result.ResolutionChain.Should().HaveCount(5);
    }

    #endregion

    #region Cross-level: Different paths resolve independently

    [Theory]
    [InlineData("encryption")]
    [InlineData("compression")]
    [InlineData("replication")]
    public async Task CrossLevel_DifferentPaths_ResolveIndependently(string featureId)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", MakePolicy(featureId, PolicyLevel.VDE, intensity: 40));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1", MakePolicy(featureId, PolicyLevel.Container, intensity: 80));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c2", MakePolicy(featureId, PolicyLevel.Container, intensity: 20));

        var engine = CreateEngine(store);

        var result1 = await engine.ResolveAsync(featureId, new PolicyResolutionContext { Path = "/v1/c1" });
        var result2 = await engine.ResolveAsync(featureId, new PolicyResolutionContext { Path = "/v1/c2" });

        result1.EffectiveIntensity.Should().Be(80);
        result2.EffectiveIntensity.Should().Be(20);
    }

    #endregion

    #region Cross-level: All 5 CascadeStrategy values x 3 representative features

    [Theory]
    [InlineData("encryption", CascadeStrategy.Override)]
    [InlineData("encryption", CascadeStrategy.Inherit)]
    [InlineData("encryption", CascadeStrategy.MostRestrictive)]
    [InlineData("encryption", CascadeStrategy.Enforce)]
    [InlineData("encryption", CascadeStrategy.Merge)]
    [InlineData("compression", CascadeStrategy.Override)]
    [InlineData("compression", CascadeStrategy.Inherit)]
    [InlineData("compression", CascadeStrategy.MostRestrictive)]
    [InlineData("compression", CascadeStrategy.Enforce)]
    [InlineData("compression", CascadeStrategy.Merge)]
    [InlineData("replication", CascadeStrategy.Override)]
    [InlineData("replication", CascadeStrategy.Inherit)]
    [InlineData("replication", CascadeStrategy.MostRestrictive)]
    [InlineData("replication", CascadeStrategy.Enforce)]
    [InlineData("replication", CascadeStrategy.Merge)]
    public async Task CrossLevel_AllCascadeStrategies_ProduceValidResult(string featureId, CascadeStrategy cascade)
    {
        var store = CreateStore();
        await store.SetAsync(featureId, PolicyLevel.VDE, "/v1",
            MakePolicy(featureId, PolicyLevel.VDE, intensity: 60, cascade: cascade,
                customParams: new Dictionary<string, string> { ["key"] = "vdeValue" }));
        await store.SetAsync(featureId, PolicyLevel.Container, "/v1/c1",
            MakePolicy(featureId, PolicyLevel.Container, intensity: 80, cascade: cascade,
                customParams: new Dictionary<string, string> { ["key"] = "containerValue" }));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.FeatureId.Should().Be(featureId);
        result.EffectiveIntensity.Should().BeInRange(0, 100);
        result.SnapshotTimestamp.Should().BeAfter(DateTimeOffset.MinValue);
    }

    #endregion

    // =========================================================================
    // ADDITIONAL: Resolution at each individual level
    // =========================================================================

    #region Per-level resolution for representative features

    [Theory]
    [InlineData("encryption", PolicyLevel.VDE, "/v1")]
    [InlineData("encryption", PolicyLevel.Container, "/v1/c1")]
    [InlineData("encryption", PolicyLevel.Object, "/v1/c1/o1")]
    [InlineData("encryption", PolicyLevel.Chunk, "/v1/c1/o1/ch1")]
    [InlineData("encryption", PolicyLevel.Block, "/v1/c1/o1/ch1/b1")]
    [InlineData("compression", PolicyLevel.VDE, "/v1")]
    [InlineData("compression", PolicyLevel.Container, "/v1/c1")]
    [InlineData("compression", PolicyLevel.Object, "/v1/c1/o1")]
    [InlineData("compression", PolicyLevel.Chunk, "/v1/c1/o1/ch1")]
    [InlineData("compression", PolicyLevel.Block, "/v1/c1/o1/ch1/b1")]
    [InlineData("replication", PolicyLevel.VDE, "/v1")]
    [InlineData("replication", PolicyLevel.Container, "/v1/c1")]
    [InlineData("replication", PolicyLevel.Object, "/v1/c1/o1")]
    [InlineData("replication", PolicyLevel.Chunk, "/v1/c1/o1/ch1")]
    [InlineData("replication", PolicyLevel.Block, "/v1/c1/o1/ch1/b1")]
    [InlineData("audit_logging", PolicyLevel.VDE, "/v1")]
    [InlineData("audit_logging", PolicyLevel.Container, "/v1/c1")]
    [InlineData("audit_logging", PolicyLevel.Object, "/v1/c1/o1")]
    [InlineData("audit_logging", PolicyLevel.Chunk, "/v1/c1/o1/ch1")]
    [InlineData("audit_logging", PolicyLevel.Block, "/v1/c1/o1/ch1/b1")]
    public async Task PerLevel_PolicySetAtLevel_ResolvesCorrectly(string featureId, PolicyLevel level, string path)
    {
        var store = CreateStore();
        var policy = MakePolicy(featureId, level, intensity: 77);
        await store.SetAsync(featureId, level, path, policy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = path };
        var result = await engine.ResolveAsync(featureId, ctx);

        result.EffectiveIntensity.Should().Be(77);
        result.DecidedAtLevel.Should().Be(level);
    }

    #endregion
}
