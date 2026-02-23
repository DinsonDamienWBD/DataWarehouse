using System.Collections.Immutable;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Comprehensive contract tests for PolicyResolutionEngine covering IPolicyEngine contract:
/// ResolveAsync, ResolveAllAsync, SimulateAsync, profile management, and OperationalProfile baseline behavior.
/// Uses InMemoryPolicyStore + InMemoryPolicyPersistence for zero-mock testing.
/// </summary>
[Trait("Category", "Unit")]
public class PolicyEngineContractTests
{
    private static InMemoryPolicyStore CreateStore() => new();
    private static InMemoryPolicyPersistence CreatePersistence() => new();

    private static PolicyResolutionEngine CreateEngine(
        InMemoryPolicyStore? store = null,
        InMemoryPolicyPersistence? persistence = null,
        OperationalProfile? profile = null,
        PolicyCategoryDefaults? categoryDefaults = null,
        CascadeOverrideStore? overrideStore = null,
        VersionedPolicyCache? cache = null,
        MergeConflictResolver? conflictResolver = null)
    {
        return new PolicyResolutionEngine(
            store ?? CreateStore(),
            persistence ?? CreatePersistence(),
            profile,
            categoryDefaults,
            overrideStore,
            cache,
            conflictResolver);
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

    // ========================================================================
    // ResolveAsync contract (~25 tests)
    // ========================================================================

    [Theory]
    [InlineData(PolicyLevel.VDE, "/vde1")]
    [InlineData(PolicyLevel.Container, "/vde1/cont1")]
    [InlineData(PolicyLevel.Object, "/vde1/cont1/obj1")]
    [InlineData(PolicyLevel.Chunk, "/vde1/cont1/obj1/chunk1")]
    [InlineData(PolicyLevel.Block, "/vde1/cont1/obj1/chunk1/block1")]
    public async Task ResolveAsync_EachLevel_ReturnsCorrectLevel(PolicyLevel level, string path)
    {
        var store = CreateStore();
        var policy = MakePolicy("encryption", level, intensity: 75);
        await store.SetAsync("encryption", level, path, policy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = path };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(75);
        result.DecidedAtLevel.Should().Be(level);
    }

    [Fact]
    public async Task ResolveAsync_OverrideCascade_MostSpecificWins()
    {
        var store = CreateStore();
        var vde = MakePolicy("encryption", PolicyLevel.VDE, intensity: 40, cascade: CascadeStrategy.Override);
        var container = MakePolicy("encryption", PolicyLevel.Container, intensity: 80, cascade: CascadeStrategy.Override);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v", vde);
        await store.SetAsync("encryption", PolicyLevel.Container, "/v/c", container);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(80);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Container);
    }

    [Fact]
    public async Task ResolveAsync_InheritCascade_CopiesParentToChild()
    {
        var store = CreateStore();
        var vde = MakePolicy("compression", PolicyLevel.VDE, intensity: 60, cascade: CascadeStrategy.Inherit);
        await store.SetAsync("compression", PolicyLevel.VDE, "/v", vde);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c/o" };

        var result = await engine.ResolveAsync("compression", ctx);

        result.EffectiveIntensity.Should().Be(60);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    [Fact]
    public async Task ResolveAsync_EnforceCascade_HigherLevelWins()
    {
        var store = CreateStore();
        var vde = MakePolicy("encryption", PolicyLevel.VDE, intensity: 90, cascade: CascadeStrategy.Enforce);
        var block = MakePolicy("encryption", PolicyLevel.Block, intensity: 20, cascade: CascadeStrategy.Override);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v", vde);
        await store.SetAsync("encryption", PolicyLevel.Block, "/v/c/o/ch/b", block);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c/o/ch/b" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(90);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public async Task ResolveAsync_MergeCascade_CombinesCustomParams()
    {
        var store = CreateStore();
        var vde = MakePolicy("replication", PolicyLevel.VDE, intensity: 50, cascade: CascadeStrategy.Merge,
            customParams: new Dictionary<string, string> { ["algo"] = "aes128", ["mode"] = "cbc" });
        var container = MakePolicy("replication", PolicyLevel.Container, intensity: 60, cascade: CascadeStrategy.Merge,
            customParams: new Dictionary<string, string> { ["algo"] = "aes256" });
        await store.SetAsync("replication", PolicyLevel.VDE, "/v", vde);
        await store.SetAsync("replication", PolicyLevel.Container, "/v/c", container);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c" };

        var result = await engine.ResolveAsync("replication", ctx);

        result.MergedParameters["algo"].Should().Be("aes256");
        result.MergedParameters["mode"].Should().Be("cbc");
    }

    [Fact]
    public async Task ResolveAsync_MostRestrictiveCascade_PicksLowestIntensity()
    {
        var store = CreateStore();
        var vde = MakePolicy("encryption", PolicyLevel.VDE, intensity: 30,
            cascade: CascadeStrategy.MostRestrictive, ai: AiAutonomyLevel.AutoSilent);
        var container = MakePolicy("encryption", PolicyLevel.Container, intensity: 80,
            cascade: CascadeStrategy.MostRestrictive, ai: AiAutonomyLevel.SuggestExplain);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v", vde);
        await store.SetAsync("encryption", PolicyLevel.Container, "/v/c", container);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(30);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.SuggestExplain);
    }

    [Fact]
    public async Task ResolveAsync_NoStoreEntries_ReturnsProfileDefault()
    {
        var profile = OperationalProfile.Standard();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(70);
    }

    [Fact]
    public async Task ResolveAsync_NullFeatureId_ThrowsArgumentException()
    {
        var engine = CreateEngine();
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var act = () => engine.ResolveAsync(null!, ctx);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ResolveAsync_EmptyFeatureId_ThrowsArgumentException()
    {
        var engine = CreateEngine();
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var act = () => engine.ResolveAsync("", ctx);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(OperationalProfilePreset.Speed, 30)]
    [InlineData(OperationalProfilePreset.Balanced, 50)]
    [InlineData(OperationalProfilePreset.Standard, 70)]
    [InlineData(OperationalProfilePreset.Strict, 90)]
    [InlineData(OperationalProfilePreset.Paranoid, 100)]
    public async Task ResolveAsync_ProfilePresets_ProduceDifferentIntensities(OperationalProfilePreset preset, int expectedEncryptionIntensity)
    {
        var profile = preset switch
        {
            OperationalProfilePreset.Speed => OperationalProfile.Speed(),
            OperationalProfilePreset.Balanced => OperationalProfile.Balanced(),
            OperationalProfilePreset.Standard => OperationalProfile.Standard(),
            OperationalProfilePreset.Strict => OperationalProfile.Strict(),
            OperationalProfilePreset.Paranoid => OperationalProfile.Paranoid(),
            _ => OperationalProfile.Standard()
        };

        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(expectedEncryptionIntensity);
    }

    [Fact]
    public async Task ResolveAsync_BlockOverridesVde_WhenOverrideCascade()
    {
        var store = CreateStore();
        var vde = MakePolicy("compression", PolicyLevel.VDE, intensity: 30, cascade: CascadeStrategy.Override);
        var block = MakePolicy("compression", PolicyLevel.Block, intensity: 95, cascade: CascadeStrategy.Override);
        await store.SetAsync("compression", PolicyLevel.VDE, "/v", vde);
        await store.SetAsync("compression", PolicyLevel.Block, "/v/c/o/ch/b", block);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c/o/ch/b" };

        var result = await engine.ResolveAsync("compression", ctx);

        result.EffectiveIntensity.Should().Be(95);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
    }

    [Fact]
    public async Task ResolveAsync_EnforceAtVde_CannotBeOverriddenByBlock()
    {
        var store = CreateStore();
        var vde = MakePolicy("encryption", PolicyLevel.VDE, intensity: 85, cascade: CascadeStrategy.Enforce);
        var block = MakePolicy("encryption", PolicyLevel.Block, intensity: 10, cascade: CascadeStrategy.Override);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v", vde);
        await store.SetAsync("encryption", PolicyLevel.Block, "/v/c/o/ch/b", block);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c/o/ch/b" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(85);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public async Task ResolveAsync_Inherit_CopiesParentUnchanged()
    {
        var store = CreateStore();
        var vde = MakePolicy("compression", PolicyLevel.VDE, intensity: 45, cascade: CascadeStrategy.Inherit,
            ai: AiAutonomyLevel.AutoNotify, customParams: new Dictionary<string, string> { ["key"] = "val" });
        await store.SetAsync("compression", PolicyLevel.VDE, "/v", vde);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c" };

        var result = await engine.ResolveAsync("compression", ctx);

        result.EffectiveIntensity.Should().Be(45);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoNotify);
    }

    [Fact]
    public async Task ResolveAsync_MergeCustomParams_ParentAndChild()
    {
        var store = CreateStore();
        var vde = MakePolicy("replication", PolicyLevel.VDE, intensity: 50, cascade: CascadeStrategy.Merge,
            customParams: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        var container = MakePolicy("replication", PolicyLevel.Container, intensity: 60, cascade: CascadeStrategy.Merge,
            customParams: new Dictionary<string, string> { ["b"] = "3", ["c"] = "4" });
        await store.SetAsync("replication", PolicyLevel.VDE, "/v", vde);
        await store.SetAsync("replication", PolicyLevel.Container, "/v/c", container);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c" };

        var result = await engine.ResolveAsync("replication", ctx);

        result.MergedParameters["a"].Should().Be("1");
        result.MergedParameters["b"].Should().Be("3");
        result.MergedParameters["c"].Should().Be("4");
    }

    [Fact]
    public async Task ResolveAsync_MostRestrictive_PicksLowestAiAutonomy()
    {
        var store = CreateStore();
        var vde = MakePolicy("encryption", PolicyLevel.VDE, intensity: 80,
            cascade: CascadeStrategy.MostRestrictive, ai: AiAutonomyLevel.ManualOnly);
        var container = MakePolicy("encryption", PolicyLevel.Container, intensity: 90,
            cascade: CascadeStrategy.MostRestrictive, ai: AiAutonomyLevel.AutoSilent);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v", vde);
        await store.SetAsync("encryption", PolicyLevel.Container, "/v/c", container);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result.EffectiveIntensity.Should().Be(80);
    }

    [Fact]
    public async Task ResolveAsync_ResolutionChain_ContainsAllLevelPolicies()
    {
        var store = CreateStore();
        await store.SetAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 30));
        await store.SetAsync("enc", PolicyLevel.Container, "/v/c", MakePolicy("enc", PolicyLevel.Container, 50));
        await store.SetAsync("enc", PolicyLevel.Object, "/v/c/o", MakePolicy("enc", PolicyLevel.Object, 70));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c/o" };

        var result = await engine.ResolveAsync("enc", ctx);

        result.ResolutionChain.Should().HaveCount(3);
        result.ResolutionChain[0].Level.Should().Be(PolicyLevel.Object);
        result.ResolutionChain[1].Level.Should().Be(PolicyLevel.Container);
        result.ResolutionChain[2].Level.Should().Be(PolicyLevel.VDE);
    }

    [Fact]
    public async Task ResolveAsync_SnapshotTimestamp_PopulatedCorrectly()
    {
        var store = CreateStore();
        await store.SetAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 50));

        var engine = CreateEngine(store);
        var before = DateTimeOffset.UtcNow;
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("enc", ctx);

        result.SnapshotTimestamp.Should().BeOnOrAfter(before);
        result.SnapshotTimestamp.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ResolveAsync_NoStoreNoProfile_ReturnsDefault50()
    {
        var engine = CreateEngine();
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("unknown_feature", ctx);

        result.EffectiveIntensity.Should().Be(50);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.SuggestExplain);
        result.AppliedCascade.Should().Be(CascadeStrategy.Inherit);
    }

    [Fact]
    public async Task ResolveAsync_CustomProfile_UsedAsBaseline()
    {
        var profile = new OperationalProfile
        {
            Name = "Custom",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["myFeature"] = MakePolicy("myFeature", PolicyLevel.VDE, intensity: 88)
            }
        };

        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("myFeature", ctx);

        result.EffectiveIntensity.Should().Be(88);
    }

    [Fact]
    public async Task ResolveAsync_StoreOverridesProfileDefault()
    {
        var store = CreateStore();
        var storePolicy = MakePolicy("encryption", PolicyLevel.VDE, intensity: 99);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v", storePolicy);

        var profile = OperationalProfile.Standard(); // encryption=70
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(99);
    }

    // ========================================================================
    // ResolveAllAsync (~10 tests)
    // ========================================================================

    [Fact]
    public async Task ResolveAllAsync_ReturnsAllFeatures()
    {
        var profile = OperationalProfile.Standard();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAllAsync(ctx);

        result.Should().ContainKey("compression");
        result.Should().ContainKey("encryption");
        result.Should().ContainKey("replication");
        result.Count.Should().Be(3);
    }

    [Fact]
    public async Task ResolveAllAsync_EmptyStore_ReturnsProfileDefaults()
    {
        var profile = OperationalProfile.Balanced();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveIntensity.Should().Be(50);
        result["compression"].EffectiveIntensity.Should().Be(50);
    }

    [Fact]
    public async Task ResolveAllAsync_EachMatchesIndividualResolve()
    {
        var store = CreateStore();
        await store.SetAsync("compression", PolicyLevel.VDE, "/v", MakePolicy("compression", PolicyLevel.VDE, 77));

        var profile = OperationalProfile.Standard();
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var allResult = await engine.ResolveAllAsync(ctx);
        var individualResult = await engine.ResolveAsync("compression", ctx);

        allResult["compression"].EffectiveIntensity.Should().Be(individualResult.EffectiveIntensity);
    }

    [Fact]
    public async Task ResolveAllAsync_MultipleFeaturesWithDifferentCascades()
    {
        var store = CreateStore();
        await store.SetAsync("compression", PolicyLevel.VDE, "/v",
            MakePolicy("compression", PolicyLevel.VDE, 60, CascadeStrategy.Override));
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v",
            MakePolicy("encryption", PolicyLevel.VDE, 90, CascadeStrategy.Enforce));

        var profile = OperationalProfile.Standard();
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAllAsync(ctx);

        result["compression"].EffectiveIntensity.Should().Be(60);
        result["encryption"].EffectiveIntensity.Should().Be(90);
    }

    [Fact]
    public async Task ResolveAllAsync_SpeedProfile_HasThreeFeatures()
    {
        var engine = CreateEngine(profile: OperationalProfile.Speed());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAllAsync(ctx);

        result.Count.Should().Be(3);
        result["encryption"].EffectiveIntensity.Should().Be(30);
    }

    [Fact]
    public async Task ResolveAllAsync_ParanoidProfile_HasThreeFeatures()
    {
        var engine = CreateEngine(profile: OperationalProfile.Paranoid());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAllAsync(ctx);

        result.Count.Should().Be(3);
        result["encryption"].EffectiveIntensity.Should().Be(100);
    }

    // ========================================================================
    // SimulateAsync (~10 tests)
    // ========================================================================

    [Fact]
    public async Task SimulateAsync_DoesNotPersist()
    {
        var store = CreateStore();
        await store.SetAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 50));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var hypothetical = MakePolicy("enc", PolicyLevel.VDE, 99);
        var simResult = await engine.SimulateAsync("enc", ctx, hypothetical);
        simResult.EffectiveIntensity.Should().Be(99);

        var realResult = await engine.ResolveAsync("enc", ctx);
        realResult.EffectiveIntensity.Should().Be(50);
    }

    [Fact]
    public async Task SimulateAsync_ShadowsExistingPolicy()
    {
        var store = CreateStore();
        await store.SetAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 50));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var hypothetical = MakePolicy("enc", PolicyLevel.VDE, 80);
        var result = await engine.SimulateAsync("enc", ctx, hypothetical);

        result.EffectiveIntensity.Should().Be(80);
    }

    [Fact]
    public async Task SimulateAsync_EnforceAtParent_StillBlocksOverride()
    {
        var store = CreateStore();
        await store.SetAsync("enc", PolicyLevel.VDE, "/v",
            MakePolicy("enc", PolicyLevel.VDE, 90, CascadeStrategy.Enforce));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c" };

        var hypothetical = MakePolicy("enc", PolicyLevel.Container, 10, CascadeStrategy.Override);
        var result = await engine.SimulateAsync("enc", ctx, hypothetical);

        result.EffectiveIntensity.Should().Be(90);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public async Task SimulateAsync_ReturnsEffectivePolicy()
    {
        var engine = CreateEngine();
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var hypothetical = MakePolicy("comp", PolicyLevel.VDE, 77, ai: AiAutonomyLevel.AutoNotify);
        var result = await engine.SimulateAsync("comp", ctx, hypothetical);

        result.FeatureId.Should().Be("comp");
        result.EffectiveIntensity.Should().Be(77);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoNotify);
    }

    [Fact]
    public async Task SimulateAsync_NullFeatureId_ThrowsArgumentException()
    {
        var engine = CreateEngine();
        var ctx = new PolicyResolutionContext { Path = "/v" };
        var hypothetical = MakePolicy("enc", PolicyLevel.VDE, 50);

        var act = () => engine.SimulateAsync(null!, ctx, hypothetical);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SimulateAsync_NullHypothetical_ThrowsArgumentNullException()
    {
        var engine = CreateEngine();
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var act = () => engine.SimulateAsync("enc", ctx, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SimulateAsync_NoExistingPolicy_UsesHypothetical()
    {
        var engine = CreateEngine();
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var hypothetical = MakePolicy("brand_new", PolicyLevel.VDE, 42);
        var result = await engine.SimulateAsync("brand_new", ctx, hypothetical);

        result.EffectiveIntensity.Should().Be(42);
    }

    // ========================================================================
    // Profile management (~10 tests)
    // ========================================================================

    [Fact]
    public async Task SetActiveProfile_ChangesResolutionBaseline()
    {
        var engine = CreateEngine(profile: OperationalProfile.Speed());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var before = await engine.ResolveAsync("encryption", ctx);
        before.EffectiveIntensity.Should().Be(30);

        await engine.SetActiveProfileAsync(OperationalProfile.Paranoid());

        var after = await engine.ResolveAsync("encryption", ctx);
        after.EffectiveIntensity.Should().Be(100);
    }

    [Fact]
    public async Task GetActiveProfile_ReturnsWhatWasSet()
    {
        var engine = CreateEngine();
        var custom = new OperationalProfile
        {
            Name = "MyProfile",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["myFeature"] = MakePolicy("myFeature", PolicyLevel.VDE, 66)
            }
        };

        await engine.SetActiveProfileAsync(custom);
        var retrieved = await engine.GetActiveProfileAsync();

        retrieved.Name.Should().Be("MyProfile");
        retrieved.FeaturePolicies["myFeature"].IntensityLevel.Should().Be(66);
    }

    [Theory]
    [InlineData(OperationalProfilePreset.Speed, "Speed")]
    [InlineData(OperationalProfilePreset.Balanced, "Balanced")]
    [InlineData(OperationalProfilePreset.Standard, "Standard")]
    [InlineData(OperationalProfilePreset.Strict, "Strict")]
    [InlineData(OperationalProfilePreset.Paranoid, "Paranoid")]
    public async Task ProfilePresets_HaveDifferentDefaults(OperationalProfilePreset preset, string expectedName)
    {
        var profile = preset switch
        {
            OperationalProfilePreset.Speed => OperationalProfile.Speed(),
            OperationalProfilePreset.Balanced => OperationalProfile.Balanced(),
            OperationalProfilePreset.Standard => OperationalProfile.Standard(),
            OperationalProfilePreset.Strict => OperationalProfile.Strict(),
            OperationalProfilePreset.Paranoid => OperationalProfile.Paranoid(),
            _ => OperationalProfile.Standard()
        };

        profile.Name.Should().Be(expectedName);
        profile.FeaturePolicies.Should().ContainKey("encryption");
        profile.FeaturePolicies.Should().ContainKey("compression");
        profile.FeaturePolicies.Should().ContainKey("replication");
    }

    [Fact]
    public async Task CustomProfile_WithExplicitPolicies_UsedAsBaseline()
    {
        var custom = new OperationalProfile
        {
            Name = "Custom",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["myCustom"] = MakePolicy("myCustom", PolicyLevel.VDE, 42, ai: AiAutonomyLevel.ManualOnly)
            }
        };

        var engine = CreateEngine(profile: custom);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("myCustom", ctx);

        result.EffectiveIntensity.Should().Be(42);
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public async Task SetActiveProfile_Null_ThrowsArgumentNullException()
    {
        var engine = CreateEngine();

        var act = () => engine.SetActiveProfileAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ========================================================================
    // OperationalProfile as baseline (~15 tests)
    // ========================================================================

    [Fact]
    public async Task SpeedProfile_HighAiAutonomy()
    {
        var engine = CreateEngine(profile: OperationalProfile.Speed());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoSilent);
    }

    [Fact]
    public async Task ParanoidProfile_ManualOnlyAi()
    {
        var engine = CreateEngine(profile: OperationalProfile.Paranoid());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public async Task ParanoidProfile_MaxSecurity()
    {
        var engine = CreateEngine(profile: OperationalProfile.Paranoid());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(100);
    }

    [Fact]
    public async Task StoreOverride_TakesPrecedenceOverProfileDefault()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v", MakePolicy("encryption", PolicyLevel.VDE, 15));

        var engine = CreateEngine(store, profile: OperationalProfile.Paranoid());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(15);
    }

    [Fact]
    public async Task ProfileSwitch_MidSession_ChangesResultsImmediately()
    {
        var engine = CreateEngine(profile: OperationalProfile.Speed());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var r1 = await engine.ResolveAsync("encryption", ctx);
        r1.EffectiveIntensity.Should().Be(30);

        await engine.SetActiveProfileAsync(OperationalProfile.Strict());

        var r2 = await engine.ResolveAsync("encryption", ctx);
        r2.EffectiveIntensity.Should().Be(90);
    }

    [Fact]
    public async Task SpeedProfile_LowReplication()
    {
        var engine = CreateEngine(profile: OperationalProfile.Speed());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("replication", ctx);
        result.EffectiveIntensity.Should().Be(20);
    }

    [Fact]
    public async Task StrictProfile_EncryptionEnforced()
    {
        var profile = OperationalProfile.Strict();
        profile.FeaturePolicies["encryption"].Cascade.Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public async Task BalancedProfile_ModerateEncryption()
    {
        var engine = CreateEngine(profile: OperationalProfile.Balanced());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("encryption", ctx);
        result.EffectiveIntensity.Should().Be(50);
    }

    [Fact]
    public async Task StandardProfile_ReasonableDefaults()
    {
        var profile = OperationalProfile.Standard();
        profile.FeaturePolicies["encryption"].IntensityLevel.Should().Be(70);
        profile.FeaturePolicies["compression"].IntensityLevel.Should().Be(60);
        profile.FeaturePolicies["replication"].IntensityLevel.Should().Be(60);
    }

    [Fact]
    public async Task SpeedProfile_HighCompression()
    {
        var engine = CreateEngine(profile: OperationalProfile.Speed());
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("compression", ctx);
        result.EffectiveIntensity.Should().Be(30);
    }

    [Fact]
    public async Task ParanoidProfile_EnforcesCascadeOnAllFeatures()
    {
        var profile = OperationalProfile.Paranoid();
        foreach (var kv in profile.FeaturePolicies)
        {
            kv.Value.Cascade.Should().Be(CascadeStrategy.Enforce,
                $"Paranoid profile should enforce cascade for {kv.Key}");
        }
    }
}
