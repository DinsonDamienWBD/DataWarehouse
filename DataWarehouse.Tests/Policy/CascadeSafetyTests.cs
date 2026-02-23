using System.Collections.Immutable;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Safety mechanism tests covering snapshot isolation (CASC-06), circular reference
/// detection (CASC-07), merge conflict resolution (CASC-08), and cascade overrides (CASC-03).
/// </summary>
[Trait("Category", "Unit")]
public class CascadeSafetyTests
{
    private static FeaturePolicy MakePolicy(string featureId, PolicyLevel level, int intensity,
        CascadeStrategy cascade = CascadeStrategy.Override,
        AiAutonomyLevel ai = AiAutonomyLevel.SuggestExplain,
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

    // ========== Snapshot Isolation (CASC-06) ==========

    [Fact]
    public async Task SnapshotIsolation_InFlightOperation_SeesOldPolicy()
    {
        var store = new InMemoryPolicyStore();
        var cache = new VersionedPolicyCache();
        var persistence = new InMemoryPolicyPersistence();

        // Store initial policy
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1",
            MakePolicy("encryption", PolicyLevel.VDE, 50));

        // Update cache from store
        await cache.UpdateFromStoreAsync(store, new[] { "encryption" });

        // Take a snapshot (simulating an in-flight operation)
        var snapshot = cache.GetSnapshot();
        snapshot.Should().NotBeNull();

        var policyBefore = snapshot.TryGetPolicy("encryption", PolicyLevel.VDE, "/vde1");
        policyBefore.Should().NotBeNull();
        policyBefore!.IntensityLevel.Should().Be(50);

        // Modify the store and update the cache (simulating a concurrent write)
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1",
            MakePolicy("encryption", PolicyLevel.VDE, 99));
        await cache.UpdateFromStoreAsync(store, new[] { "encryption" });

        // The old snapshot should still see intensity=50
        var policyAfter = snapshot.TryGetPolicy("encryption", PolicyLevel.VDE, "/vde1");
        policyAfter.Should().NotBeNull();
        policyAfter!.IntensityLevel.Should().Be(50);
    }

    [Fact]
    public async Task SnapshotIsolation_NewOperation_SeesNewPolicy()
    {
        var store = new InMemoryPolicyStore();
        var cache = new VersionedPolicyCache();

        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1",
            MakePolicy("encryption", PolicyLevel.VDE, 50));
        await cache.UpdateFromStoreAsync(store, new[] { "encryption" });

        // Modify store and update cache
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1",
            MakePolicy("encryption", PolicyLevel.VDE, 99));
        await cache.UpdateFromStoreAsync(store, new[] { "encryption" });

        // New snapshot should see intensity=99
        var snapshot = cache.GetSnapshot();
        var policy = snapshot.TryGetPolicy("encryption", PolicyLevel.VDE, "/vde1");
        policy.Should().NotBeNull();
        policy!.IntensityLevel.Should().Be(99);
    }

    [Fact]
    public void VersionedCache_VersionIncrements_OnUpdate()
    {
        var cache = new VersionedPolicyCache();
        var initialVersion = cache.CurrentVersion;
        initialVersion.Should().Be(0);

        cache.Update(ImmutableDictionary<string, FeaturePolicy>.Empty);
        cache.CurrentVersion.Should().Be(1);

        cache.Update(ImmutableDictionary<string, FeaturePolicy>.Empty);
        cache.CurrentVersion.Should().Be(2);
    }

    [Fact]
    public async Task VersionedCache_PreviousSnapshot_StillValid()
    {
        var store = new InMemoryPolicyStore();
        var cache = new VersionedPolicyCache();

        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1",
            MakePolicy("encryption", PolicyLevel.VDE, 50));
        await cache.UpdateFromStoreAsync(store, new[] { "encryption" });

        var firstSnapshot = cache.GetSnapshot();
        firstSnapshot.Version.Should().Be(1);

        // Update cache again
        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1",
            MakePolicy("encryption", PolicyLevel.VDE, 99));
        await cache.UpdateFromStoreAsync(store, new[] { "encryption" });

        var previousSnapshot = cache.GetPreviousSnapshot();
        previousSnapshot.Version.Should().Be(1);

        // Previous snapshot still has the old data
        var policy = previousSnapshot.TryGetPolicy("encryption", PolicyLevel.VDE, "/vde1");
        policy.Should().NotBeNull();
        policy!.IntensityLevel.Should().Be(50);
    }

    // ========== Circular Reference Detection (CASC-07) ==========

    [Fact]
    public async Task CircularReference_DirectCycle_Rejected()
    {
        var store = new InMemoryPolicyStore();

        // Policy B redirects to A
        await store.SetAsync("encryption", PolicyLevel.VDE, "/pathB",
            MakePolicy("encryption", PolicyLevel.VDE, 50,
                customParams: new Dictionary<string, string> { ["redirect"] = "/pathA" }));

        // Policy A redirects to B -> direct cycle
        var policyA = MakePolicy("encryption", PolicyLevel.VDE, 50,
            customParams: new Dictionary<string, string> { ["redirect"] = "/pathB" });

        var act = () => CircularReferenceDetector.ValidateAsync(
            "encryption", PolicyLevel.VDE, "/pathA", policyA, store);

        await act.Should().ThrowAsync<PolicyCircularReferenceException>()
            .Where(ex => ex.PathChain.Contains("/pathA") && ex.PathChain.Contains("/pathB"));
    }

    [Fact]
    public async Task CircularReference_TransitiveCycle_Rejected()
    {
        var store = new InMemoryPolicyStore();

        // B -> C
        await store.SetAsync("encryption", PolicyLevel.VDE, "/pathB",
            MakePolicy("encryption", PolicyLevel.VDE, 50,
                customParams: new Dictionary<string, string> { ["redirect"] = "/pathC" }));

        // C -> A
        await store.SetAsync("encryption", PolicyLevel.VDE, "/pathC",
            MakePolicy("encryption", PolicyLevel.VDE, 50,
                customParams: new Dictionary<string, string> { ["redirect"] = "/pathA" }));

        // A -> B (creates A->B->C->A cycle)
        var policyA = MakePolicy("encryption", PolicyLevel.VDE, 50,
            customParams: new Dictionary<string, string> { ["redirect"] = "/pathB" });

        var act = () => CircularReferenceDetector.ValidateAsync(
            "encryption", PolicyLevel.VDE, "/pathA", policyA, store);

        await act.Should().ThrowAsync<PolicyCircularReferenceException>()
            .Where(ex => ex.PathChain.Count >= 3);
    }

    [Fact]
    public async Task CircularReference_NoCycle_Accepted()
    {
        var store = new InMemoryPolicyStore();

        // B has no redirect
        await store.SetAsync("encryption", PolicyLevel.VDE, "/pathB",
            MakePolicy("encryption", PolicyLevel.VDE, 50));

        // A redirects to B (no cycle)
        var policyA = MakePolicy("encryption", PolicyLevel.VDE, 50,
            customParams: new Dictionary<string, string> { ["redirect"] = "/pathB" });

        var result = await CircularReferenceDetector.ValidateAsync(
            "encryption", PolicyLevel.VDE, "/pathA", policyA, store);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CircularReference_MaxDepthExceeded_Rejected()
    {
        var store = new InMemoryPolicyStore();

        // Create a long chain: path1 -> path2 -> path3 -> ... -> pathN (no actual cycle)
        // Set maxDepth to 3 but chain is 5 hops
        for (var i = 1; i <= 5; i++)
        {
            await store.SetAsync("encryption", PolicyLevel.VDE, $"/path{i}",
                MakePolicy("encryption", PolicyLevel.VDE, 50,
                    customParams: new Dictionary<string, string> { ["redirect"] = $"/path{i + 1}" }));
        }

        // Last node has no redirect (no actual cycle)
        await store.SetAsync("encryption", PolicyLevel.VDE, "/path6",
            MakePolicy("encryption", PolicyLevel.VDE, 50));

        // A redirects to path1, max depth 3 should trigger depth exceeded
        var policyA = MakePolicy("encryption", PolicyLevel.VDE, 50,
            customParams: new Dictionary<string, string> { ["redirect"] = "/path1" });

        var result = await CircularReferenceDetector.ValidateAsync(
            "encryption", PolicyLevel.VDE, "/pathA", policyA, store, maxDepth: 3);

        // Chain is longer than maxDepth, should produce an error
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("exceeds maximum depth");
    }

    [Fact]
    public async Task CircularReference_ErrorMessage_ContainsPathChain()
    {
        var store = new InMemoryPolicyStore();

        await store.SetAsync("encryption", PolicyLevel.VDE, "/pathB",
            MakePolicy("encryption", PolicyLevel.VDE, 50,
                customParams: new Dictionary<string, string> { ["redirect"] = "/pathA" }));

        var policyA = MakePolicy("encryption", PolicyLevel.VDE, 50,
            customParams: new Dictionary<string, string> { ["redirect"] = "/pathB" });

        try
        {
            await CircularReferenceDetector.ValidateAsync(
                "encryption", PolicyLevel.VDE, "/pathA", policyA, store);

            // Should not reach here
            true.Should().BeFalse("Expected PolicyCircularReferenceException");
        }
        catch (PolicyCircularReferenceException ex)
        {
            ex.Message.Should().Contain("/pathA");
            ex.Message.Should().Contain("/pathB");
            ex.PathChain.Should().Contain("/pathA");
            ex.PathChain.Should().Contain("/pathB");
        }
    }

    // ========== Merge Conflict Resolution (CASC-08) ==========

    [Fact]
    public void MergeConflict_MostRestrictive_PicksLowest()
    {
        var resolver = new MergeConflictResolver(
            new Dictionary<string, MergeConflictMode> { ["limit"] = MergeConflictMode.MostRestrictive });

        var result = resolver.Resolve("limit", new[] { "100", "50", "75" });

        result.Should().Be("50");
    }

    [Fact]
    public void MergeConflict_Closest_PicksChild()
    {
        var resolver = new MergeConflictResolver(
            new Dictionary<string, MergeConflictMode> { ["algo"] = MergeConflictMode.Closest });

        var result = resolver.Resolve("algo", new[] { "aes256", "aes128" });

        result.Should().Be("aes256"); // first value (most-specific) wins
    }

    [Fact]
    public void MergeConflict_Union_CombinesAll()
    {
        var resolver = new MergeConflictResolver(
            new Dictionary<string, MergeConflictMode> { ["tags"] = MergeConflictMode.Union });

        var result = resolver.Resolve("tags", new[] { "fast", "secure", "fast" });

        // Distinct values joined by ";"
        result.Should().Be("fast;secure");
    }

    [Fact]
    public void MergeConflict_PerKeyConfig_DifferentModesPerKey()
    {
        var resolver = new MergeConflictResolver(new Dictionary<string, MergeConflictMode>
        {
            ["limit"] = MergeConflictMode.MostRestrictive,
            ["algo"] = MergeConflictMode.Closest,
            ["tags"] = MergeConflictMode.Union
        });

        var level1 = new Dictionary<string, string> { ["limit"] = "100", ["algo"] = "aes256", ["tags"] = "fast" };
        var level2 = new Dictionary<string, string> { ["limit"] = "50", ["algo"] = "aes128", ["tags"] = "secure" };

        var result = resolver.ResolveAll(new[] { level1, level2 });

        result["limit"].Should().Be("50");       // MostRestrictive
        result["algo"].Should().Be("aes256");     // Closest (first)
        result["tags"].Should().Be("fast;secure"); // Union
    }

    [Fact]
    public void MergeConflict_UnknownKey_DefaultsToClosest()
    {
        // No key modes configured, default mode is Closest
        var resolver = new MergeConflictResolver();

        var result = resolver.Resolve("unknown_key", new[] { "child_value", "parent_value" });

        result.Should().Be("child_value"); // Closest = first value
    }

    // ========== User Cascade Overrides (CASC-03) ==========

    [Fact]
    public void CascadeOverride_SetAndRetrieve()
    {
        var store = new CascadeOverrideStore();

        store.SetOverride("encryption", PolicyLevel.VDE, CascadeStrategy.Enforce);

        store.TryGetOverride("encryption", PolicyLevel.VDE, out var strategy).Should().BeTrue();
        strategy.Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public async Task CascadeOverride_OverrideRespectedInResolution()
    {
        var policyStore = new InMemoryPolicyStore();
        var overrideStore = new CascadeOverrideStore();

        // Store a policy at Container level with no explicit cascade (uses Inherit as zero value)
        await policyStore.SetAsync("encryption", PolicyLevel.Container, "/v1/c1",
            MakePolicy("encryption", PolicyLevel.Container, 60, cascade: CascadeStrategy.Inherit));
        await policyStore.SetAsync("encryption", PolicyLevel.VDE, "/v1",
            MakePolicy("encryption", PolicyLevel.VDE, 40, cascade: CascadeStrategy.Inherit));

        // Set user override: Container level should use Override
        overrideStore.SetOverride("encryption", PolicyLevel.Container, CascadeStrategy.Override);

        var engine = new PolicyResolutionEngine(
            policyStore, new InMemoryPolicyPersistence(),
            overrideStore: overrideStore);

        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };
        var result = await engine.ResolveAsync("encryption", ctx);

        // User override Override at Container: most-specific (Container) wins, parent discarded
        result.AppliedCascade.Should().Be(CascadeStrategy.Override);
        result.EffectiveIntensity.Should().Be(60);
    }

    [Fact]
    public void CascadeOverride_Remove_FallsBackToDefault()
    {
        var store = new CascadeOverrideStore();
        store.SetOverride("encryption", PolicyLevel.VDE, CascadeStrategy.Enforce);

        store.RemoveOverride("encryption", PolicyLevel.VDE).Should().BeTrue();
        store.TryGetOverride("encryption", PolicyLevel.VDE, out _).Should().BeFalse();
    }

    [Fact]
    public async Task CascadeOverride_PersistAndReload()
    {
        var persistence = new InMemoryPolicyPersistence();
        var store1 = new CascadeOverrideStore();

        store1.SetOverride("encryption", PolicyLevel.VDE, CascadeStrategy.Enforce);
        store1.SetOverride("compression", PolicyLevel.Container, CascadeStrategy.MostRestrictive);

        await store1.SaveToPersistenceAsync(persistence);

        // Create a new store and load from persistence
        var store2 = new CascadeOverrideStore();
        var loaded = await store2.LoadFromPersistenceAsync(persistence);

        loaded.Should().Be(2);
        store2.TryGetOverride("encryption", PolicyLevel.VDE, out var strategy1).Should().BeTrue();
        strategy1.Should().Be(CascadeStrategy.Enforce);
        store2.TryGetOverride("compression", PolicyLevel.Container, out var strategy2).Should().BeTrue();
        strategy2.Should().Be(CascadeStrategy.MostRestrictive);
    }

    // ========== Engine with VersionedPolicyCache (CASC-06 end-to-end) ==========

    [Fact]
    public async Task Engine_SnapshotIsolation_ResolvesFromCacheSnapshot()
    {
        var store = new InMemoryPolicyStore();
        var cache = new VersionedPolicyCache();
        var persistence = new InMemoryPolicyPersistence();

        await store.SetAsync("encryption", PolicyLevel.VDE, "/vde1",
            MakePolicy("encryption", PolicyLevel.VDE, 75));

        await cache.UpdateFromStoreAsync(store, new[] { "encryption" });

        var engine = new PolicyResolutionEngine(store, persistence, cache: cache);
        var ctx = new PolicyResolutionContext { Path = "/vde1" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.EffectiveIntensity.Should().Be(75);
        result.SnapshotTimestamp.Should().BeAfter(DateTimeOffset.MinValue);
    }

    // ========== Engine ValidatePolicyAsync (CASC-07 integration) ==========

    [Fact]
    public async Task Engine_ValidatePolicy_RejectsCircularReference()
    {
        var store = new InMemoryPolicyStore();
        var persistence = new InMemoryPolicyPersistence();

        await store.SetAsync("encryption", PolicyLevel.VDE, "/pathB",
            MakePolicy("encryption", PolicyLevel.VDE, 50,
                customParams: new Dictionary<string, string> { ["redirect"] = "/pathA" }));

        var engine = new PolicyResolutionEngine(store, persistence);

        var circularPolicy = MakePolicy("encryption", PolicyLevel.VDE, 50,
            customParams: new Dictionary<string, string> { ["redirect"] = "/pathB" });

        var act = () => engine.ValidatePolicyAsync("encryption", PolicyLevel.VDE, "/pathA", circularPolicy);

        await act.Should().ThrowAsync<PolicyCircularReferenceException>();
    }

    // ========== Engine with MergeConflictResolver (CASC-08 integration) ==========

    [Fact]
    public async Task Engine_MergeWithConflictResolver_UsesPerKeyResolution()
    {
        var store = new InMemoryPolicyStore();
        var persistence = new InMemoryPolicyPersistence();

        // VDE with Merge cascade
        await store.SetAsync("governance", PolicyLevel.VDE, "/v1",
            MakePolicy("governance", PolicyLevel.VDE, 50, cascade: CascadeStrategy.Merge,
                customParams: new Dictionary<string, string> { ["limit"] = "100", ["region"] = "us" }));

        // Container with Merge cascade
        await store.SetAsync("governance", PolicyLevel.Container, "/v1/c1",
            MakePolicy("governance", PolicyLevel.Container, 60, cascade: CascadeStrategy.Merge,
                customParams: new Dictionary<string, string> { ["limit"] = "50", ["region"] = "eu" }));

        var resolver = new MergeConflictResolver(new Dictionary<string, MergeConflictMode>
        {
            ["limit"] = MergeConflictMode.MostRestrictive,
            ["region"] = MergeConflictMode.Closest
        });

        var engine = new PolicyResolutionEngine(store, persistence, conflictResolver: resolver);
        var ctx = new PolicyResolutionContext { Path = "/v1/c1" };

        var result = await engine.ResolveAsync("governance", ctx);

        result.MergedParameters["limit"].Should().Be("50");  // MostRestrictive picks lowest
        result.MergedParameters["region"].Should().Be("eu");  // Closest picks child
    }
}
