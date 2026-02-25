using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Tests for cross-feature policy interactions verifying that policies for different features
/// resolve independently but operational profiles produce coherent sets (INTG-03).
/// </summary>
[Trait("Category", "Integration")]
public class CrossFeatureInteractionTests
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

    // =============================================
    // 1. Profile coherence (~15 tests)
    // =============================================

    [Fact]
    public async Task SpeedProfile_EncryptionLow_CompressionLow_AiAutoSilent()
    {
        var profile = OperationalProfile.Speed();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveIntensity.Should().Be(30);
        result["compression"].EffectiveIntensity.Should().Be(30);
        result["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoSilent);
    }

    [Fact]
    public async Task ParanoidProfile_EncryptionHigh_AiManualOnly_CompressionEnforced()
    {
        var profile = OperationalProfile.Paranoid();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveIntensity.Should().Be(100);
        result["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result["compression"].EffectiveIntensity.Should().Be(90);
        result["replication"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public async Task StandardProfile_ReturnsCoherentSet()
    {
        var profile = OperationalProfile.Standard();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result.Should().ContainKey("compression");
        result.Should().ContainKey("encryption");
        result.Should().ContainKey("replication");
        result.Count.Should().Be(3);
        // Standard: moderate security
        result["encryption"].EffectiveIntensity.Should().Be(70);
        result["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.Suggest);
    }

    [Fact]
    public async Task BalancedProfile_ReturnsCoherentSet()
    {
        var profile = OperationalProfile.Balanced();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result.Count.Should().Be(3);
        result["encryption"].EffectiveIntensity.Should().Be(50);
        result["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.SuggestExplain);
        result["compression"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoNotify);
    }

    [Fact]
    public async Task StrictProfile_ReturnsCoherentSet()
    {
        var profile = OperationalProfile.Strict();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result.Count.Should().Be(3);
        result["encryption"].EffectiveIntensity.Should().Be(90);
        result["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result["compression"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.Suggest);
    }

    [Fact]
    public async Task SpeedProfile_ReturnsCoherentSet_AllFeaturesPresent()
    {
        var profile = OperationalProfile.Speed();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result.Count.Should().Be(3);
        result.Should().ContainKey("compression");
        result.Should().ContainKey("encryption");
        result.Should().ContainKey("replication");
    }

    [Fact]
    public async Task ParanoidProfile_AllFeaturesEnforced()
    {
        var profile = OperationalProfile.Paranoid();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        foreach (var kvp in result)
        {
            kvp.Value.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly,
                $"feature {kvp.Key} should be ManualOnly in Paranoid");
        }
    }

    [Fact]
    public async Task SpeedProfile_NoFeatureManualOnly()
    {
        var profile = OperationalProfile.Speed();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        foreach (var kvp in result)
        {
            kvp.Value.EffectiveAiAutonomy.Should().NotBe(AiAutonomyLevel.ManualOnly,
                $"feature {kvp.Key} should not be ManualOnly in Speed");
        }
    }

    [Fact]
    public async Task CustomProfile_MixedSettings_ResolvesEachFeatureIndependently()
    {
        var profile = new OperationalProfile
        {
            Preset = OperationalProfilePreset.Custom,
            Name = "Custom",
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE, intensity: 100, ai: AiAutonomyLevel.ManualOnly),
                ["compression"] = MakePolicy("compression", PolicyLevel.VDE, intensity: 20, ai: AiAutonomyLevel.AutoSilent),
                ["replication"] = MakePolicy("replication", PolicyLevel.VDE, intensity: 60, ai: AiAutonomyLevel.SuggestExplain)
            }
        };
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveIntensity.Should().Be(100);
        result["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result["compression"].EffectiveIntensity.Should().Be(20);
        result["compression"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoSilent);
        result["replication"].EffectiveIntensity.Should().Be(60);
    }

    [Fact]
    public async Task AllSixPresets_ProduceNonEmptyCoherentSets()
    {
        var presets = new[]
        {
            OperationalProfile.Speed(),
            OperationalProfile.Balanced(),
            OperationalProfile.Standard(),
            OperationalProfile.Strict(),
            OperationalProfile.Paranoid(),
            new OperationalProfile
            {
                Preset = OperationalProfilePreset.Custom,
                Name = "Custom",
                FeaturePolicies = new Dictionary<string, FeaturePolicy>
                {
                    ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE, intensity: 55)
                }
            }
        };

        foreach (var profile in presets)
        {
            var engine = CreateEngine(profile: profile);
            var ctx = new PolicyResolutionContext { Path = "/vde1" };
            var result = await engine.ResolveAllAsync(ctx);

            result.Count.Should().BeGreaterThan(0, $"profile {profile.Name} should return features");
            foreach (var kvp in result)
            {
                kvp.Value.EffectiveIntensity.Should().BeInRange(0, 100,
                    $"feature {kvp.Key} in {profile.Name}");
            }
        }
    }

    [Fact]
    public async Task ParanoidProfile_NoAutoSilentAllowed()
    {
        var profile = OperationalProfile.Paranoid();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        foreach (var kvp in result)
        {
            kvp.Value.EffectiveAiAutonomy.Should().NotBe(AiAutonomyLevel.AutoSilent,
                $"Paranoid profile must not have AutoSilent on {kvp.Key}");
        }
    }

    [Fact]
    public async Task StrictProfile_EncryptionEnforced()
    {
        var profile = OperationalProfile.Strict();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public async Task BalancedProfile_ReplicationInherits()
    {
        var profile = OperationalProfile.Balanced();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["replication"].EffectiveIntensity.Should().Be(50);
    }

    [Fact]
    public async Task SpeedProfile_CompressionAndEncryptionLowIntensity()
    {
        var profile = OperationalProfile.Speed();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["compression"].EffectiveIntensity.Should().BeLessThanOrEqualTo(50);
        result["encryption"].EffectiveIntensity.Should().BeLessThanOrEqualTo(50);
    }

    [Fact]
    public async Task ParanoidProfile_AllIntensitiesHigh()
    {
        var profile = OperationalProfile.Paranoid();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        foreach (var kvp in result)
        {
            kvp.Value.EffectiveIntensity.Should().BeGreaterThanOrEqualTo(90,
                $"feature {kvp.Key} should have high intensity in Paranoid");
        }
    }

    // =============================================
    // 2. Cross-feature cascade independence (~10 tests)
    // =============================================

    [Fact]
    public async Task OverrideEncryption_DoesNotAffectCompression()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Standard();
        var encPolicy = MakePolicy("encryption", PolicyLevel.Block, intensity: 99);
        await store.SetAsync("encryption", PolicyLevel.Block, "/v/c/o/ch/b", encPolicy);

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v/c/o/ch/b" };

        var encResult = await engine.ResolveAsync("encryption", ctx);
        var cmpResult = await engine.ResolveAsync("compression", ctx);

        encResult.EffectiveIntensity.Should().Be(99);
        cmpResult.EffectiveIntensity.Should().Be(60); // Standard profile default
    }

    [Fact]
    public async Task EnforceAccessControl_DoesNotEnforceAiAutonomy()
    {
        var store = CreateStore();
        var profile = new OperationalProfile
        {
            Name = "Test",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["access_control"] = MakePolicy("access_control", PolicyLevel.VDE, intensity: 100,
                    cascade: CascadeStrategy.Enforce, ai: AiAutonomyLevel.ManualOnly),
                ["ai_autonomy"] = MakePolicy("ai_autonomy", PolicyLevel.VDE, intensity: 50,
                    cascade: CascadeStrategy.Inherit, ai: AiAutonomyLevel.AutoNotify)
            }
        };

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var acResult = await engine.ResolveAsync("access_control", ctx);
        var aiResult = await engine.ResolveAsync("ai_autonomy", ctx);

        acResult.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
        aiResult.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoNotify);
    }

    [Fact]
    public async Task MostRestrictiveEncryption_DoesNotAffectStorage()
    {
        var store = CreateStore();
        var profile = new OperationalProfile
        {
            Name = "Test",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE, intensity: 90,
                    cascade: CascadeStrategy.MostRestrictive),
                ["storage"] = MakePolicy("storage", PolicyLevel.VDE, intensity: 40,
                    cascade: CascadeStrategy.Override)
            }
        };

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var encResult = await engine.ResolveAsync("encryption", ctx);
        var storResult = await engine.ResolveAsync("storage", ctx);

        encResult.AppliedCascade.Should().Be(CascadeStrategy.MostRestrictive);
        storResult.EffectiveIntensity.Should().Be(40);
    }

    [Fact]
    public async Task MergeOnOneFeature_DoesNotContaminateAnother()
    {
        var store = CreateStore();
        var mergePolicy = MakePolicy("replication", PolicyLevel.VDE, intensity: 50,
            cascade: CascadeStrategy.Merge,
            customParams: new Dictionary<string, string> { ["key1"] = "val1" });
        var overridePolicy = MakePolicy("encryption", PolicyLevel.VDE, intensity: 80,
            cascade: CascadeStrategy.Override);

        await store.SetAsync("replication", PolicyLevel.VDE, "/v1", mergePolicy);
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1", overridePolicy);

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var repResult = await engine.ResolveAsync("replication", ctx);
        var encResult = await engine.ResolveAsync("encryption", ctx);

        repResult.AppliedCascade.Should().Be(CascadeStrategy.Merge);
        encResult.AppliedCascade.Should().Be(CascadeStrategy.Override);
    }

    [Fact]
    public async Task DifferentCascades_SameLevel_IndependentResolution()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.Container, "/v/c",
            MakePolicy("encryption", PolicyLevel.Container, intensity: 95, cascade: CascadeStrategy.Enforce));
        await store.SetAsync("compression", PolicyLevel.Container, "/v/c",
            MakePolicy("compression", PolicyLevel.Container, intensity: 30, cascade: CascadeStrategy.Inherit));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v/c" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        var cmp = await engine.ResolveAsync("compression", ctx);

        enc.EffectiveIntensity.Should().Be(95);
        enc.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
        cmp.EffectiveIntensity.Should().Be(30);
        cmp.DecidedAtLevel.Should().Be(PolicyLevel.Container);
    }

    [Fact]
    public async Task OverrideBlockEncryption_BlockCompression_Independent()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.Block, "/a/b/c/d/e",
            MakePolicy("encryption", PolicyLevel.Block, intensity: 100));
        await store.SetAsync("compression", PolicyLevel.Block, "/a/b/c/d/e",
            MakePolicy("compression", PolicyLevel.Block, intensity: 10));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/a/b/c/d/e" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        var cmp = await engine.ResolveAsync("compression", ctx);

        enc.EffectiveIntensity.Should().Be(100);
        cmp.EffectiveIntensity.Should().Be(10);
    }

    [Fact]
    public async Task TwoFeatures_DifferentLevels_NoBleed()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 80));
        await store.SetAsync("compression", PolicyLevel.Object, "/v1/c1/o1",
            MakePolicy("compression", PolicyLevel.Object, intensity: 25));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        var cmp = await engine.ResolveAsync("compression", ctx);

        enc.EffectiveIntensity.Should().Be(80);
        enc.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
        cmp.EffectiveIntensity.Should().Be(25);
        cmp.DecidedAtLevel.Should().Be(PolicyLevel.Object);
    }

    [Fact]
    public async Task InheritEncryption_OverrideCompression_IndependentPaths()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 70, cascade: CascadeStrategy.Inherit));
        await store.SetAsync("compression", PolicyLevel.Container, "/v1/c1",
            MakePolicy("compression", PolicyLevel.Container, intensity: 45, cascade: CascadeStrategy.Override));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        var cmp = await engine.ResolveAsync("compression", ctx);

        enc.EffectiveIntensity.Should().Be(70);
        cmp.EffectiveIntensity.Should().Be(45);
    }

    [Fact]
    public async Task ThreeFeatures_ThreeCascades_AllIndependent()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 90, cascade: CascadeStrategy.Enforce));
        await store.SetAsync("compression", PolicyLevel.VDE, "/v1",
            MakePolicy("compression", PolicyLevel.VDE, intensity: 40, cascade: CascadeStrategy.Merge,
                customParams: new Dictionary<string, string> { ["algo"] = "lz4" }));
        await store.SetAsync("replication", PolicyLevel.VDE, "/v1",
            MakePolicy("replication", PolicyLevel.VDE, intensity: 60, cascade: CascadeStrategy.Override));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        var cmp = await engine.ResolveAsync("compression", ctx);
        var rep = await engine.ResolveAsync("replication", ctx);

        enc.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
        cmp.AppliedCascade.Should().Be(CascadeStrategy.Merge);
        rep.AppliedCascade.Should().Be(CascadeStrategy.Override);
    }

    [Fact]
    public async Task FeatureModification_DoesNotAffectOtherFeature()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 50));
        await store.SetAsync("compression", PolicyLevel.VDE, "/v1",
            MakePolicy("compression", PolicyLevel.VDE, intensity: 60));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        // Resolve both
        var encBefore = await engine.ResolveAsync("encryption", ctx);
        var cmpBefore = await engine.ResolveAsync("compression", ctx);

        // Modify encryption only
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 99));

        var encAfter = await engine.ResolveAsync("encryption", ctx);
        var cmpAfter = await engine.ResolveAsync("compression", ctx);

        encAfter.EffectiveIntensity.Should().Be(99);
        cmpAfter.EffectiveIntensity.Should().Be(60); // unchanged
    }

    // =============================================
    // 3. Multi-feature override scenarios (~15 tests)
    // =============================================

    [Fact]
    public async Task Override3Features_ContainerLevel_AllResolved()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Standard();
        await store.SetAsync("encryption", PolicyLevel.Container, "/v/c",
            MakePolicy("encryption", PolicyLevel.Container, intensity: 88));
        await store.SetAsync("compression", PolicyLevel.Container, "/v/c",
            MakePolicy("compression", PolicyLevel.Container, intensity: 22));
        await store.SetAsync("replication", PolicyLevel.Container, "/v/c",
            MakePolicy("replication", PolicyLevel.Container, intensity: 77));

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v/c/o1" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        var cmp = await engine.ResolveAsync("compression", ctx);
        var rep = await engine.ResolveAsync("replication", ctx);

        enc.EffectiveIntensity.Should().Be(88);
        cmp.EffectiveIntensity.Should().Be(22);
        rep.EffectiveIntensity.Should().Be(77);
    }

    [Fact]
    public async Task DifferentCascadeStrategies_SameResolveAllCall()
    {
        var store = CreateStore();
        var profile = new OperationalProfile
        {
            Name = "MultiCascade",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE, intensity: 80, cascade: CascadeStrategy.Enforce),
                ["compression"] = MakePolicy("compression", PolicyLevel.VDE, intensity: 50, cascade: CascadeStrategy.Inherit),
                ["replication"] = MakePolicy("replication", PolicyLevel.VDE, intensity: 60, cascade: CascadeStrategy.Override)
            }
        };

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };
        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].AppliedCascade.Should().Be(CascadeStrategy.Enforce);
        // Inherit resolves to the effective cascade of the winning policy
        result["compression"].EffectiveIntensity.Should().Be(50);
        result["replication"].EffectiveIntensity.Should().Be(60);
    }

    [Fact]
    public async Task RemoveOverride_OtherOverridesUnaffected()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 90));
        await store.SetAsync("compression", PolicyLevel.VDE, "/v1",
            MakePolicy("compression", PolicyLevel.VDE, intensity: 40));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        // Remove encryption override by setting default
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 50));

        var enc = await engine.ResolveAsync("encryption", ctx);
        var cmp = await engine.ResolveAsync("compression", ctx);

        enc.EffectiveIntensity.Should().Be(50);
        cmp.EffectiveIntensity.Should().Be(40); // unchanged
    }

    [Fact]
    public async Task BulkSet_10Features_ResolveAllReturnsCorrect()
    {
        var store = CreateStore();
        var features = new Dictionary<string, FeaturePolicy>();
        for (int i = 0; i < 10; i++)
        {
            string featureId = $"feature_{i}";
            int intensity = 10 + i * 9; // 10, 19, 28, ... 91
            var policy = MakePolicy(featureId, PolicyLevel.VDE, intensity: intensity);
            await store.SetAsync(featureId, PolicyLevel.VDE, "/v1", policy);
            features[featureId] = policy;
        }

        var profile = new OperationalProfile
        {
            Name = "BulkTest",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = features
        };
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var result = await engine.ResolveAllAsync(ctx);

        result.Count.Should().Be(10);
        for (int i = 0; i < 10; i++)
        {
            string featureId = $"feature_{i}";
            int expectedIntensity = 10 + i * 9;
            result[featureId].EffectiveIntensity.Should().Be(expectedIntensity,
                $"feature_{i} should have intensity {expectedIntensity}");
        }
    }

    [Fact]
    public async Task OverrideAtVDE_ThenContainer_ResolvesCorrectly()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Standard();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 70));
        await store.SetAsync("encryption", PolicyLevel.Container, "/v1/c1",
            MakePolicy("encryption", PolicyLevel.Container, intensity: 90));

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        enc.EffectiveIntensity.Should().Be(90);
        enc.DecidedAtLevel.Should().Be(PolicyLevel.Container);
    }

    [Fact]
    public async Task MultiFeature_MultiLevel_AllCorrect()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 70));
        await store.SetAsync("compression", PolicyLevel.Container, "/v1/c1",
            MakePolicy("compression", PolicyLevel.Container, intensity: 30));
        await store.SetAsync("replication", PolicyLevel.Object, "/v1/c1/o1",
            MakePolicy("replication", PolicyLevel.Object, intensity: 80));

        var profile = new OperationalProfile
        {
            Name = "Multi",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE, intensity: 50),
                ["compression"] = MakePolicy("compression", PolicyLevel.VDE, intensity: 50),
                ["replication"] = MakePolicy("replication", PolicyLevel.VDE, intensity: 50)
            }
        };
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveIntensity.Should().Be(70);
        result["compression"].EffectiveIntensity.Should().Be(30);
        result["replication"].EffectiveIntensity.Should().Be(80);
    }

    [Fact]
    public async Task Override5Features_VaryingLevels_CorrectResolution()
    {
        var store = CreateStore();
        await store.SetAsync("feat_a", PolicyLevel.VDE, "/v1",
            MakePolicy("feat_a", PolicyLevel.VDE, intensity: 10));
        await store.SetAsync("feat_b", PolicyLevel.Container, "/v1/c1",
            MakePolicy("feat_b", PolicyLevel.Container, intensity: 20));
        await store.SetAsync("feat_c", PolicyLevel.Object, "/v1/c1/o1",
            MakePolicy("feat_c", PolicyLevel.Object, intensity: 30));
        await store.SetAsync("feat_d", PolicyLevel.Chunk, "/v1/c1/o1/ch1",
            MakePolicy("feat_d", PolicyLevel.Chunk, intensity: 40));
        await store.SetAsync("feat_e", PolicyLevel.Block, "/v1/c1/o1/ch1/b1",
            MakePolicy("feat_e", PolicyLevel.Block, intensity: 50));

        var features = new Dictionary<string, FeaturePolicy>();
        foreach (var id in new[] { "feat_a", "feat_b", "feat_c", "feat_d", "feat_e" })
            features[id] = MakePolicy(id, PolicyLevel.VDE, intensity: 1);

        var profile = new OperationalProfile
        {
            Name = "FiveLevel",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = features
        };
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1/o1/ch1/b1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["feat_a"].EffectiveIntensity.Should().Be(10);
        result["feat_b"].EffectiveIntensity.Should().Be(20);
        result["feat_c"].EffectiveIntensity.Should().Be(30);
        result["feat_d"].EffectiveIntensity.Should().Be(40);
        result["feat_e"].EffectiveIntensity.Should().Be(50);
    }

    [Fact]
    public async Task OverrideReplication_KeepEncryptionDefaults()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Standard();
        await store.SetAsync("replication", PolicyLevel.Container, "/v1/c1",
            MakePolicy("replication", PolicyLevel.Container, intensity: 99));

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        var rep = await engine.ResolveAsync("replication", ctx);

        enc.EffectiveIntensity.Should().Be(70); // Standard profile default
        rep.EffectiveIntensity.Should().Be(99);
    }

    [Fact]
    public async Task SameFeature_OverrideTwice_LatestWins()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 50));
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 99));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        enc.EffectiveIntensity.Should().Be(99);
    }

    [Fact]
    public async Task ResolveAll_ProfileSwitch_AllFeaturesUpdate()
    {
        var store = CreateStore();
        var engine = CreateEngine(store, profile: OperationalProfile.Speed());
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var speedResult = await engine.ResolveAllAsync(ctx);
        speedResult["encryption"].EffectiveIntensity.Should().Be(30);

        await engine.SetActiveProfileAsync(OperationalProfile.Paranoid());

        var paranoidResult = await engine.ResolveAllAsync(ctx);
        paranoidResult["encryption"].EffectiveIntensity.Should().Be(100);
        paranoidResult["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public async Task MultiFeature_ChainLength_Varies()
    {
        var store = CreateStore();
        await store.SetAsync("enc", PolicyLevel.VDE, "/v1",
            MakePolicy("enc", PolicyLevel.VDE, intensity: 70));
        await store.SetAsync("enc", PolicyLevel.Container, "/v1/c1",
            MakePolicy("enc", PolicyLevel.Container, intensity: 80));
        await store.SetAsync("cmp", PolicyLevel.VDE, "/v1",
            MakePolicy("cmp", PolicyLevel.VDE, intensity: 40));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };

        var enc = await engine.ResolveAsync("enc", ctx);
        var cmp = await engine.ResolveAsync("cmp", ctx);

        enc.ResolutionChain.Should().HaveCount(2);
        cmp.ResolutionChain.Should().HaveCount(1);
    }

    [Fact]
    public async Task MultiOverride_AiAutonomy_PerFeature()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, ai: AiAutonomyLevel.ManualOnly));
        await store.SetAsync("compression", PolicyLevel.VDE, "/v1",
            MakePolicy("compression", PolicyLevel.VDE, ai: AiAutonomyLevel.AutoSilent));
        await store.SetAsync("replication", PolicyLevel.VDE, "/v1",
            MakePolicy("replication", PolicyLevel.VDE, ai: AiAutonomyLevel.SuggestExplain));

        var profile = new OperationalProfile
        {
            Name = "AiTest",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE),
                ["compression"] = MakePolicy("compression", PolicyLevel.VDE),
                ["replication"] = MakePolicy("replication", PolicyLevel.VDE)
            }
        };
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result["compression"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoSilent);
        result["replication"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.SuggestExplain);
    }

    [Fact]
    public async Task BulkOverride_CustomParams_Independent()
    {
        var store = CreateStore();
        await store.SetAsync("feat_x", PolicyLevel.VDE, "/v1",
            MakePolicy("feat_x", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["k1"] = "v1" },
                cascade: CascadeStrategy.Merge));
        await store.SetAsync("feat_y", PolicyLevel.VDE, "/v1",
            MakePolicy("feat_y", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["k2"] = "v2" },
                cascade: CascadeStrategy.Merge));

        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var x = await engine.ResolveAsync("feat_x", ctx);
        var y = await engine.ResolveAsync("feat_y", ctx);

        x.MergedParameters.Should().ContainKey("k1");
        x.MergedParameters.Should().NotContainKey("k2");
        y.MergedParameters.Should().ContainKey("k2");
        y.MergedParameters.Should().NotContainKey("k1");
    }

    // =============================================
    // 4. Security-AI interaction (~10 tests)
    // =============================================

    [Fact]
    public async Task ParanoidProfile_ForcesManualOnlyAi()
    {
        var profile = OperationalProfile.Paranoid();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        foreach (var kvp in result)
        {
            kvp.Value.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly,
                $"{kvp.Key} in Paranoid should be ManualOnly");
        }
    }

    [Fact]
    public async Task EnforceAccessControl_InheritAiAutonomy_BothResolved()
    {
        var store = CreateStore();
        var profile = new OperationalProfile
        {
            Name = "SecurityAI",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["access_control"] = MakePolicy("access_control", PolicyLevel.VDE, intensity: 100,
                    cascade: CascadeStrategy.Enforce, ai: AiAutonomyLevel.ManualOnly),
                ["ai_autonomy"] = MakePolicy("ai_autonomy", PolicyLevel.VDE, intensity: 50,
                    cascade: CascadeStrategy.Inherit, ai: AiAutonomyLevel.AutoNotify)
            }
        };

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var ac = await engine.ResolveAsync("access_control", ctx);
        var ai = await engine.ResolveAsync("ai_autonomy", ctx);

        ac.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
        ac.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        ai.AppliedCascade.Should().Be(CascadeStrategy.Inherit);
        ai.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoNotify);
    }

    [Fact]
    public async Task ComplianceAndAi_IndependentResolution()
    {
        var store = CreateStore();
        var profile = new OperationalProfile
        {
            Name = "ComplianceAI",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["compliance"] = MakePolicy("compliance", PolicyLevel.VDE, intensity: 100,
                    cascade: CascadeStrategy.Enforce, ai: AiAutonomyLevel.ManualOnly),
                ["ai_features"] = MakePolicy("ai_features", PolicyLevel.VDE, intensity: 70,
                    cascade: CascadeStrategy.Override, ai: AiAutonomyLevel.SuggestExplain)
            }
        };

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["compliance"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result["ai_features"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.SuggestExplain);
    }

    [Fact]
    public async Task SecurityHigh_AiLow_NoContradiction()
    {
        var store = CreateStore();
        var profile = new OperationalProfile
        {
            Name = "SecurityHigh",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE, intensity: 100,
                    cascade: CascadeStrategy.Enforce, ai: AiAutonomyLevel.ManualOnly),
                ["ai_tuning"] = MakePolicy("ai_tuning", PolicyLevel.VDE, intensity: 30,
                    cascade: CascadeStrategy.Override, ai: AiAutonomyLevel.AutoSilent)
            }
        };

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveIntensity.Should().Be(100);
        result["ai_tuning"].EffectiveIntensity.Should().Be(30);
    }

    [Fact]
    public async Task StrictProfile_EncryptionManualOnly_CompressionSuggest()
    {
        var profile = OperationalProfile.Strict();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result["compression"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.Suggest);
    }

    [Fact]
    public async Task SecurityOverride_DoesNotEscalateAiAutonomy()
    {
        var store = CreateStore();
        await store.SetAsync("security", PolicyLevel.VDE, "/v1",
            MakePolicy("security", PolicyLevel.VDE, intensity: 100, ai: AiAutonomyLevel.ManualOnly));

        var profile = new OperationalProfile
        {
            Name = "NoEscalation",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["security"] = MakePolicy("security", PolicyLevel.VDE, intensity: 50),
                ["optimization"] = MakePolicy("optimization", PolicyLevel.VDE, intensity: 50, ai: AiAutonomyLevel.AutoNotify)
            }
        };
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var sec = await engine.ResolveAsync("security", ctx);
        var opt = await engine.ResolveAsync("optimization", ctx);

        sec.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        opt.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoNotify);
    }

    [Fact]
    public async Task MultipleSecurityFeatures_AllManualOnly_InParanoid()
    {
        var profile = OperationalProfile.Paranoid();
        var engine = CreateEngine(profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result["compression"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
        result["replication"].EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public async Task OverrideAiFeature_SecurityUnchanged()
    {
        var store = CreateStore();
        await store.SetAsync("ai_config", PolicyLevel.VDE, "/v1",
            MakePolicy("ai_config", PolicyLevel.VDE, ai: AiAutonomyLevel.AutoSilent));
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 95, ai: AiAutonomyLevel.ManualOnly));

        var profile = new OperationalProfile
        {
            Name = "Test",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["ai_config"] = MakePolicy("ai_config", PolicyLevel.VDE),
                ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE)
            }
        };
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var ai = await engine.ResolveAsync("ai_config", ctx);
        var enc = await engine.ResolveAsync("encryption", ctx);

        ai.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.AutoSilent);
        enc.EffectiveAiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Fact]
    public async Task HighSecurityOverride_LowAiOverride_BothApplied()
    {
        var store = CreateStore();
        await store.SetAsync("encryption", PolicyLevel.Container, "/v/c",
            MakePolicy("encryption", PolicyLevel.Container, intensity: 100));
        await store.SetAsync("ai_level", PolicyLevel.Container, "/v/c",
            MakePolicy("ai_level", PolicyLevel.Container, intensity: 10));

        var profile = new OperationalProfile
        {
            Name = "Combo",
            Preset = OperationalProfilePreset.Custom,
            FeaturePolicies = new Dictionary<string, FeaturePolicy>
            {
                ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE),
                ["ai_level"] = MakePolicy("ai_level", PolicyLevel.VDE)
            }
        };
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v/c" };

        var enc = await engine.ResolveAsync("encryption", ctx);
        var ai = await engine.ResolveAsync("ai_level", ctx);

        enc.EffectiveIntensity.Should().Be(100);
        ai.EffectiveIntensity.Should().Be(10);
    }

    // =============================================
    // 5. Edge cases (~5 tests)
    // =============================================

    [Fact]
    public async Task EmptyStore_ResolveAll_ReturnsProfileDefaults()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Standard();
        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var result = await engine.ResolveAllAsync(ctx);

        result.Count.Should().Be(3);
        result["encryption"].EffectiveIntensity.Should().Be(70);
        result["compression"].EffectiveIntensity.Should().Be(60);
        result["replication"].EffectiveIntensity.Should().Be(60);
    }

    [Fact]
    public async Task SingleFeatureOverridden_OthersReturnProfileDefaults()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Standard();
        await store.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, intensity: 99));

        var engine = CreateEngine(store, profile: profile);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var result = await engine.ResolveAllAsync(ctx);

        result["encryption"].EffectiveIntensity.Should().Be(99);
        result["compression"].EffectiveIntensity.Should().Be(60); // Standard default
        result["replication"].EffectiveIntensity.Should().Be(60); // Standard default
    }

    [Fact]
    public async Task ProfileSwitch_AllFeaturesUpdateAtomically()
    {
        var store = CreateStore();
        var engine = CreateEngine(store, profile: OperationalProfile.Speed());
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        var beforeSwitch = await engine.ResolveAllAsync(ctx);
        beforeSwitch["encryption"].EffectiveIntensity.Should().Be(30);

        await engine.SetActiveProfileAsync(OperationalProfile.Paranoid());

        var afterSwitch = await engine.ResolveAllAsync(ctx);
        afterSwitch["encryption"].EffectiveIntensity.Should().Be(100);
        afterSwitch["compression"].EffectiveIntensity.Should().Be(90);
        afterSwitch["replication"].EffectiveIntensity.Should().Be(100);
    }

    [Fact]
    public async Task ConcurrentResolveAll_ReturnsConsistentSnapshots()
    {
        var store = CreateStore();
        var profile = OperationalProfile.Standard();
        var engine = CreateEngine(store, profile: profile);

        var tasks = new List<Task<IReadOnlyDictionary<string, IEffectivePolicy>>>();
        for (int i = 0; i < 10; i++)
        {
            var ctx = new PolicyResolutionContext { Path = "/v1" };
            tasks.Add(engine.ResolveAllAsync(ctx));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            result.Count.Should().Be(3);
            result["encryption"].EffectiveIntensity.Should().Be(70);
            result["compression"].EffectiveIntensity.Should().Be(60);
            result["replication"].EffectiveIntensity.Should().Be(60);
        }
    }

    [Fact]
    public async Task EmptyStore_NoProfile_ResolveAll_ReturnsDefaults()
    {
        var store = CreateStore();
        var engine = CreateEngine(store);
        var ctx = new PolicyResolutionContext { Path = "/v1" };

        // Standard profile (default) has 3 features
        var result = await engine.ResolveAllAsync(ctx);

        result.Count.Should().Be(3);
        foreach (var kvp in result)
        {
            kvp.Value.EffectiveIntensity.Should().BeInRange(0, 100);
        }
    }
}
