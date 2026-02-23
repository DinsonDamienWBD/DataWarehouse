using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Tests for cascade strategy implementations (CASC-02, CASC-05) and category defaults.
/// Each strategy is tested via CascadeStrategies static methods and end-to-end via the engine.
/// </summary>
[Trait("Category", "Unit")]
public class CascadeStrategyTests
{
    private static FeaturePolicy MakePolicy(PolicyLevel level, int intensity,
        CascadeStrategy cascade = CascadeStrategy.Override,
        AiAutonomyLevel ai = AiAutonomyLevel.SuggestExplain,
        Dictionary<string, string>? customParams = null)
    {
        return new FeaturePolicy
        {
            FeatureId = "test",
            Level = level,
            IntensityLevel = intensity,
            Cascade = cascade,
            AiAutonomy = ai,
            CustomParameters = customParams
        };
    }

    // --- Inherit ---

    [Fact]
    public void Inherit_SingleEntry_ReturnsEntry()
    {
        var chain = new[] { MakePolicy(PolicyLevel.VDE, 75) };
        var (intensity, ai, mergedParams, decidedAt) = CascadeStrategies.Inherit(chain);

        intensity.Should().Be(75);
        decidedAt.Should().Be(PolicyLevel.VDE);
    }

    [Fact]
    public void Inherit_MultipleEntries_ReturnsMostSpecific()
    {
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Object, 60, ai: AiAutonomyLevel.AutoNotify),
            MakePolicy(PolicyLevel.Container, 70, ai: AiAutonomyLevel.Suggest),
            MakePolicy(PolicyLevel.VDE, 80, ai: AiAutonomyLevel.ManualOnly)
        };

        var (intensity, ai, _, decidedAt) = CascadeStrategies.Inherit(chain);

        intensity.Should().Be(60); // most-specific (Object) wins
        ai.Should().Be(AiAutonomyLevel.AutoNotify);
        decidedAt.Should().Be(PolicyLevel.Object);
    }

    // --- Override ---

    [Fact]
    public void Override_IgnoresParentValues()
    {
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Container, 40,
                customParams: new Dictionary<string, string> { ["key1"] = "child" }),
            MakePolicy(PolicyLevel.VDE, 80,
                customParams: new Dictionary<string, string> { ["key1"] = "parent", ["key2"] = "parentOnly" })
        };

        var (intensity, _, mergedParams, _) = CascadeStrategies.Override(chain);

        intensity.Should().Be(40); // most-specific wins
        mergedParams.Should().ContainKey("key1").WhoseValue.Should().Be("child");
        mergedParams.Should().NotContainKey("key2"); // parent value discarded
    }

    [Fact]
    public void Override_SingleEntry_ReturnsEntry()
    {
        var chain = new[] { MakePolicy(PolicyLevel.Block, 22) };
        var (intensity, _, _, decidedAt) = CascadeStrategies.Override(chain);

        intensity.Should().Be(22);
        decidedAt.Should().Be(PolicyLevel.Block);
    }

    // --- MostRestrictive ---

    [Fact]
    public void MostRestrictive_PicksLowestIntensity()
    {
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Object, 80),
            MakePolicy(PolicyLevel.Container, 30),
            MakePolicy(PolicyLevel.VDE, 60)
        };

        var (intensity, _, _, decidedAt) = CascadeStrategies.MostRestrictive(chain);

        intensity.Should().Be(30);
        decidedAt.Should().Be(PolicyLevel.Container);
    }

    [Fact]
    public void MostRestrictive_PicksMostRestrictiveAiAutonomy()
    {
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Object, 50, ai: AiAutonomyLevel.AutoSilent),
            MakePolicy(PolicyLevel.VDE, 50, ai: AiAutonomyLevel.ManualOnly)
        };

        var (_, ai, _, _) = CascadeStrategies.MostRestrictive(chain);

        ai.Should().Be(AiAutonomyLevel.ManualOnly); // lowest enum = most restrictive
    }

    [Fact]
    public void MostRestrictive_MultipleEntries_ReturnsCorrectDecidedAtLevel()
    {
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Block, 50),
            MakePolicy(PolicyLevel.Chunk, 20), // lowest intensity
            MakePolicy(PolicyLevel.Object, 40),
            MakePolicy(PolicyLevel.VDE, 60)
        };

        var (intensity, _, _, decidedAt) = CascadeStrategies.MostRestrictive(chain);

        intensity.Should().Be(20);
        decidedAt.Should().Be(PolicyLevel.Chunk);
    }

    // --- Enforce (CASC-05 adversarial) ---

    [Fact]
    public void Enforce_HigherLevelWinsOverLowerOverride()
    {
        // VDE Enforce at intensity 100 vs Container Override at intensity 30
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Container, 30, cascade: CascadeStrategy.Override),
            MakePolicy(PolicyLevel.VDE, 100, cascade: CascadeStrategy.Enforce)
        };

        var (intensity, _, _, decidedAt) = CascadeStrategies.Enforce(chain);

        intensity.Should().Be(100); // VDE Enforce wins unconditionally
        decidedAt.Should().Be(PolicyLevel.VDE);
    }

    [Fact]
    public void Enforce_VdeEnforce_OverridesContainerOverride()
    {
        // CASC-05 key adversarial scenario
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Object, 10, cascade: CascadeStrategy.Override),
            MakePolicy(PolicyLevel.Container, 30, cascade: CascadeStrategy.Override),
            MakePolicy(PolicyLevel.VDE, 100, cascade: CascadeStrategy.Enforce,
                customParams: new Dictionary<string, string> { ["enforced"] = "true" })
        };

        var (intensity, _, mergedParams, decidedAt) = CascadeStrategies.Enforce(chain);

        intensity.Should().Be(100);
        decidedAt.Should().Be(PolicyLevel.VDE);
        mergedParams.Should().ContainKey("enforced").WhoseValue.Should().Be("true");
    }

    [Fact]
    public void Enforce_NoEnforceInChain_FallsBackToOverride()
    {
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Container, 40, cascade: CascadeStrategy.Override),
            MakePolicy(PolicyLevel.VDE, 80, cascade: CascadeStrategy.Override)
        };

        var (intensity, _, _, decidedAt) = CascadeStrategies.Enforce(chain);

        // Falls back to Override: most-specific wins
        intensity.Should().Be(40);
        decidedAt.Should().Be(PolicyLevel.Container);
    }

    [Fact]
    public void Enforce_BlockLevelEnforce_StillApplied()
    {
        // Even Block-level Enforce works if it's the highest-level Enforce in chain
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Block, 99, cascade: CascadeStrategy.Enforce)
        };

        var (intensity, _, _, decidedAt) = CascadeStrategies.Enforce(chain);

        intensity.Should().Be(99);
        decidedAt.Should().Be(PolicyLevel.Block);
    }

    // --- Merge ---

    [Fact]
    public void Merge_CombinesCustomParameters()
    {
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Object, 50,
                customParams: new Dictionary<string, string> { ["algo"] = "aes256" }),
            MakePolicy(PolicyLevel.VDE, 50,
                customParams: new Dictionary<string, string> { ["mode"] = "gcm", ["algo"] = "aes128" })
        };

        var (_, _, mergedParams, _) = CascadeStrategies.Merge(chain);

        mergedParams["algo"].Should().Be("aes256"); // child overwrites parent
        mergedParams["mode"].Should().Be("gcm");    // parent key preserved
    }

    [Fact]
    public void Merge_ChildOverwritesParentKeys()
    {
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Container, 50,
                customParams: new Dictionary<string, string> { ["x"] = "child", ["y"] = "child" }),
            MakePolicy(PolicyLevel.VDE, 50,
                customParams: new Dictionary<string, string> { ["x"] = "parent", ["z"] = "parent" })
        };

        var (_, _, mergedParams, _) = CascadeStrategies.Merge(chain);

        mergedParams["x"].Should().Be("child");
        mergedParams["y"].Should().Be("child");
        mergedParams["z"].Should().Be("parent");
    }

    [Fact]
    public void Merge_EmptyParameters_NoError()
    {
        var chain = new[]
        {
            MakePolicy(PolicyLevel.Object, 50, customParams: null),
            MakePolicy(PolicyLevel.VDE, 50, customParams: null)
        };

        var (_, _, mergedParams, _) = CascadeStrategies.Merge(chain);

        mergedParams.Should().BeEmpty();
    }

    // --- Category Defaults (CASC-02) ---

    [Fact]
    public void CategoryDefaults_Security_MostRestrictive()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("security").Should().Be(CascadeStrategy.MostRestrictive);
    }

    [Fact]
    public void CategoryDefaults_Performance_Override()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("performance").Should().Be(CascadeStrategy.Override);
    }

    [Fact]
    public void CategoryDefaults_Governance_Merge()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("governance").Should().Be(CascadeStrategy.Merge);
    }

    [Fact]
    public void CategoryDefaults_Compliance_Enforce()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("compliance").Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public void CategoryDefaults_Unknown_DefaultsToInherit()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("totally_unknown_category").Should().Be(CascadeStrategy.Inherit);
    }

    [Fact]
    public void CategoryDefaults_UserOverride_TakesPrecedence()
    {
        var overrides = new Dictionary<string, CascadeStrategy>
        {
            ["security"] = CascadeStrategy.Override // override built-in MostRestrictive
        };

        var defaults = new PolicyCategoryDefaults(overrides);

        defaults.GetDefaultStrategy("security").Should().Be(CascadeStrategy.Override);
        // Non-overridden categories stay at built-in defaults
        defaults.GetDefaultStrategy("compliance").Should().Be(CascadeStrategy.Enforce);
    }

    // --- End-to-end: Enforce at VDE overrides Override at Container (CASC-05) ---

    [Fact]
    public async Task Engine_EnforceAtVde_OverridesOverrideAtContainer()
    {
        var store = new InMemoryPolicyStore();

        // VDE: Enforce at intensity 100
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1",
            MakePolicy(PolicyLevel.VDE, 100, cascade: CascadeStrategy.Enforce));

        // Container: Override at intensity 30
        await store.SetAsync("encryption", PolicyLevel.Container, "/vde1/cont1",
            MakePolicy(PolicyLevel.Container, 30, cascade: CascadeStrategy.Override));

        var engine = new PolicyResolutionEngine(store, new InMemoryPolicyPersistence());
        var ctx = new PolicyResolutionContext { Path = "/vde1/cont1/obj1" };

        var result = await engine.ResolveAsync("encryption", ctx);

        // VDE Enforce MUST win over Container Override
        result.EffectiveIntensity.Should().Be(100);
        result.DecidedAtLevel.Should().Be(PolicyLevel.VDE);
        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    // --- Compression category default Override ---

    [Fact]
    public void CategoryDefaults_Compression_Override()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("compression").Should().Be(CascadeStrategy.Override);
    }

    // --- Category prefix matching ---

    [Fact]
    public void CategoryDefaults_PrefixMatching_SecurityDotEncryption()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("security.encryption").Should().Be(CascadeStrategy.MostRestrictive);
    }

    // --- Encryption direct match ---

    [Fact]
    public void CategoryDefaults_Encryption_MostRestrictive()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("encryption").Should().Be(CascadeStrategy.MostRestrictive);
    }

    // --- Audit Enforce ---

    [Fact]
    public void CategoryDefaults_Audit_Enforce()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("audit").Should().Be(CascadeStrategy.Enforce);
    }
}
