using System.Collections.Immutable;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Tests for the PolicyResolutionEngine core resolution logic covering
/// CASC-01 (path-level resolution), CASC-04 (empty-level skip), and profile fallback.
/// </summary>
[Trait("Category", "Unit")]
public class CascadeResolutionTests
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

    // --- Path-level resolution (CASC-01) ---

    [Fact]
    public async Task ResolveAsync_VdeLevelPath_ReturnsVdePolicy()
    {
        var store = CreateStore();
        var policy = MakePolicy("encryption", PolicyLevel.VDE, intensity: 80);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/myVde", policy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/myVde" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(80);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
        result.FeatureId.Should().Be("encryption");
    }

    [Fact]
    public async Task ResolveAsync_ContainerLevelPath_ReturnsContainerPolicy()
    {
        var store = CreateStore();
        var vdePolicy = MakePolicy("compression", PolicyLevel.VDE, intensity: 40);
        var containerPolicy = MakePolicy("compression", PolicyLevel.Container, intensity: 70);
        await store.SetAsync("compression", PolicyLevel.VDE, "/vde1", vdePolicy);
        await store.SetAsync("compression", PolicyLevel.Container, "/vde1/cont1", containerPolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1" };

        var result = await engine.ResolveAsync("compression", ctx);

        result.EffectiveIntensity.Should().Be(70);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Container);
    }

    [Fact]
    public async Task ResolveAsync_ObjectLevelPath_ReturnsObjectPolicy()
    {
        var store = CreateStore();
        var objPolicy = MakePolicy("replication", PolicyLevel.Object, intensity: 60);
        await store.SetAsync("replication", PolicyLevel.Object, "/vde1/cont1/obj1", objPolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1/obj1" };

        var result = await engine.ResolveAsync("replication", ctx);

        result.EffectiveIntensity.Should().Be(60);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Object);
    }

    [Fact]
    public async Task ResolveAsync_ChunkLevelPath_ReturnsChunkPolicy()
    {
        var store = CreateStore();
        var chunkPolicy = MakePolicy("encryption", PolicyLevel.Chunk, intensity: 90);
        await store.SetAsync("encryption", PolicyLevel.Chunk, "/vde1/cont1/obj1/chunk1", chunkPolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1/obj1/chunk1" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(90);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Chunk);
    }

    [Fact]
    public async Task ResolveAsync_BlockLevelPath_ReturnsBlockPolicy()
    {
        var store = CreateStore();
        var blockPolicy = MakePolicy("compression", PolicyLevel.Block, intensity: 95);
        await store.SetAsync("compression", PolicyLevel.Block, "/vde1/cont1/obj1/chunk1/block1", blockPolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1/obj1/chunk1/block1" };

        var result = await engine.ResolveAsync("compression", ctx);

        result.EffectiveIntensity.Should().Be(95);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
    }

    // --- Empty-level skip (CASC-04) ---

    [Fact]
    public async Task ResolveAsync_EmptyContainer_SkipsToVde()
    {
        var store = CreateStore();
        // VDE-level set, Container empty, resolve at Object
        var vdePolicy = MakePolicy("encryption", PolicyLevel.VDE, intensity: 70);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", vdePolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1/obj1" };

        var result = await engine.ResolveAsync("encryption", ctx);

        // Should inherit from VDE since Container is empty
        result.EffectiveIntensity.Should().Be(70);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    [Fact]
    public async Task ResolveAsync_EmptyChunkAndObject_SkipsToContainer()
    {
        var store = CreateStore();
        var containerPolicy = MakePolicy("compression", PolicyLevel.Container, intensity: 55);
        await store.SetAsync("compression", PolicyLevel.Container, "/vde1/cont1", containerPolicy);

        var engine = CreateEngine(store);
        // Resolve at Block level, but only Container has a policy
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1/obj1/chunk1/block1" };

        var result = await engine.ResolveAsync("compression", ctx);

        result.EffectiveIntensity.Should().Be(55);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Container);
    }

    [Fact]
    public async Task ResolveAsync_AllLevelsEmpty_FallsBackToProfile()
    {
        var store = CreateStore();
        var profile = new OperationalProfile
        {
            Name = "TestProfile",
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE, intensity: 42)
            }
        };

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(42);
    }

    [Fact]
    public async Task ResolveAsync_NoProfileNoStore_ReturnsDefault()
    {
        var store = CreateStore();
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAsync("unknown_feature", ctx);

        result.EffectiveIntensity.Should().Be(50); // default
        result.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.SuggestExplain);
        result.AppliedCascade.Should().Be(CascadeStrategy.Inherit);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    [Fact]
    public async Task ResolveAsync_ResolutionChain_OrderedMostSpecificFirst()
    {
        var store = CreateStore();
        var vdePolicy = MakePolicy("compression", PolicyLevel.VDE, intensity: 30);
        var containerPolicy = MakePolicy("compression", PolicyLevel.Container, intensity: 60);
        var objectPolicy = MakePolicy("compression", PolicyLevel.Object, intensity: 80);
        await store.SetAsync("compression", PolicyLevel.VDE, "/vde1", vdePolicy);
        await store.SetAsync("compression", PolicyLevel.Container, "/vde1/cont1", containerPolicy);
        await store.SetAsync("compression", PolicyLevel.Object, "/vde1/cont1/obj1", objectPolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1/obj1" };

        var result = await engine.ResolveAsync("compression", ctx);

        // Chain should be: Object (most specific), Container, VDE (least specific)
        result.ResolutionChain.Should().HaveCount(3);
        result.ResolutionChain[0].Level.Should().Be(PolicyLevel.Object);
        result.ResolutionChain[1].Level.Should().Be(PolicyLevel.Container);
        result.ResolutionChain[2].Level.Should().Be(PolicyLevel.VDE);
    }

    [Fact]
    public async Task ResolveAsync_SnapshotTimestamp_IsPopulated()
    {
        var store = CreateStore();
        var policy = MakePolicy("encryption", PolicyLevel.VDE, intensity: 50);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", policy);

        var engine = CreateEngine(store);
        var before = DateTimeOffset.UtcNow;
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.SnapshotTimestamp.Should().BeOnOrAfter(before);
        result.SnapshotTimestamp.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    // --- ResolveAll ---

    [Fact]
    public async Task ResolveAllAsync_ReturnsAllFeatures()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Standard();

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        // Standard profile has compression, encryption, replication
        result.Should().ContainKey("compression");
        result.Should().ContainKey("encryption");
        result.Should().ContainKey("replication");
        result.Count.Should().Be(3);
    }

    // --- Simulate ---

    [Fact]
    public async Task SimulateAsync_DoesNotPersist()
    {
        var store = CreateStore();
        var vdePolicy = MakePolicy("encryption", PolicyLevel.VDE, intensity: 50);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", vdePolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var hypothetical = MakePolicy("encryption", PolicyLevel.VDE, intensity: 99);
        var simResult = await engine.SimulateAsync("encryption", ctx, hypothetical);

        simResult.EffectiveIntensity.Should().Be(99);

        // Original store policy should be unchanged
        var realResult = await engine.ResolveAsync("encryption", ctx);
        realResult.EffectiveIntensity.Should().Be(50);
    }

    // --- Profile round-trip ---

    [Fact]
    public async Task GetSetActiveProfile_RoundTrips()
    {
        var engine = CreateEngine();
        var custom = new OperationalProfile
        {
            Name = "Custom",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["myFeature"] = MakePolicy("myFeature", PolicyLevel.VDE, intensity: 77)
            }
        };

        await engine.SetActiveProfileAsync(custom);
        var retrieved = await engine.GetActiveProfileAsync();

        retrieved.Name.Should().Be("Custom");
        retrieved.FeaturePolicies.Should().ContainKey("myFeature");
        retrieved.FeaturePolicies["myFeature"].IntensityLevel.Should().Be(77);
    }

    // --- Path parsing ---

    [Fact]
    public async Task ResolveAsync_PathParsing_SingleSegment_VdeLevel()
    {
        var store = CreateStore();
        var policy = MakePolicy("encryption", PolicyLevel.VDE, intensity: 33);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1", policy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "vde1" }; // No leading slash

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(33);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
    }

    [Fact]
    public async Task ResolveAsync_PathParsing_FiveSegments_BlockLevel()
    {
        var store = CreateStore();
        var blockPolicy = MakePolicy("encryption", PolicyLevel.Block, intensity: 88);
        await store.SetAsync("encryption", PolicyLevel.Block, "/a/b/c/d/e", blockPolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/a/b/c/d/e" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(88);
        result.DecidedAtLevel.Should().Be(PolicyLevel.Block);
    }

    // --- Merged parameters in chain ---

    [Fact]
    public async Task ResolveAsync_MergedParameters_CombinedFromChain()
    {
        var store = CreateStore();
        // Use Merge cascade explicitly so parameters are combined (child overwrites parent)
        var vdePolicy = MakePolicy("replication", PolicyLevel.VDE, intensity: 50,
            cascade: CascadeStrategy.Merge,
            customParams: new Dictionary<string, string> { ["algo"] = "aes128", ["mode"] = "cbc" });
        var containerPolicy = MakePolicy("replication", PolicyLevel.Container, intensity: 60,
            cascade: CascadeStrategy.Merge,
            customParams: new Dictionary<string, string> { ["algo"] = "aes256" }); // overwrites parent

        await store.SetAsync("replication", PolicyLevel.VDE, "/v1", vdePolicy);
        await store.SetAsync("replication", PolicyLevel.Container, "/v1/c1", containerPolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };

        var result = await engine.ResolveAsync("replication", ctx);

        // Container overwrites algo, mode inherited from VDE via Merge
        result.MergedParameters.Should().ContainKey("algo");
        result.MergedParameters["algo"].Should().Be("aes256");
        result.MergedParameters.Should().ContainKey("mode");
        result.MergedParameters["mode"].Should().Be("cbc");
    }
}
