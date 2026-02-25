using System.Collections.Immutable;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Edge case tests for PolicyEngine supporting infrastructure:
/// CircularReferenceDetector, MergeConflictResolver, VersionedPolicyCache,
/// CascadeOverrideStore, PolicyCategoryDefaults, PolicyComplianceScorer,
/// PolicyPersistenceComplianceValidator, and PolicyMarketplace.
/// </summary>
[Trait("Category", "Unit")]
public class PolicyCascadeEdgeCaseTests
{
    private static InMemoryPolicyStore CreateStore() => new();
    private static InMemoryPolicyPersistence CreatePersistence() => new();

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
    // CircularReferenceDetector (~8 tests)
    // ========================================================================

    [Fact]
    public async Task CircularRef_DirectCycle_ThrowsException()
    {
        var store = CreateStore();
        // A redirects to B, B redirects to A
        var policyA = MakePolicy("enc", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["redirect"] = "/b" });
        var policyB = MakePolicy("enc", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["redirect"] = "/a" });
        await store.SetAsync("enc", PolicyLevel.VDE, "/b", policyB);

        var act = () => CircularReferenceDetector.ValidateAsync("enc", PolicyLevel.VDE, "/a", policyA, store);

        await act.Should().ThrowAsync<PolicyCircularReferenceException>();
    }

    [Fact]
    public async Task CircularRef_ThreeNodeCycle_ThrowsException()
    {
        var store = CreateStore();
        // A->B->C->A
        var policyB = MakePolicy("enc", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["redirect"] = "/c" });
        var policyC = MakePolicy("enc", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["redirect"] = "/a" });
        await store.SetAsync("enc", PolicyLevel.VDE, "/b", policyB);
        await store.SetAsync("enc", PolicyLevel.VDE, "/c", policyC);

        var policyA = MakePolicy("enc", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["redirect"] = "/b" });

        var act = () => CircularReferenceDetector.ValidateAsync("enc", PolicyLevel.VDE, "/a", policyA, store);

        await act.Should().ThrowAsync<PolicyCircularReferenceException>();
    }

    [Fact]
    public async Task CircularRef_InheritFromCycle_ThrowsException()
    {
        var store = CreateStore();
        var policyB = MakePolicy("enc", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["inherit_from"] = "/a" });
        await store.SetAsync("enc", PolicyLevel.VDE, "/b", policyB);

        var policyA = MakePolicy("enc", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["inherit_from"] = "/b" });

        var act = () => CircularReferenceDetector.ValidateAsync("enc", PolicyLevel.VDE, "/a", policyA, store);

        await act.Should().ThrowAsync<PolicyCircularReferenceException>();
    }

    [Fact]
    public async Task CircularRef_DeepNonCircular_NoCycle()
    {
        var store = CreateStore();
        // Build a chain of 15 hops that does NOT cycle
        for (int i = 1; i <= 15; i++)
        {
            var next = i < 15 ? $"/n{i + 1}" : "/end";
            var redirectParams = i < 15
                ? new Dictionary<string, string> { ["redirect"] = next }
                : null;
            var p = MakePolicy("enc", PolicyLevel.VDE, customParams: redirectParams);
            await store.SetAsync("enc", PolicyLevel.VDE, $"/n{i}", p);
        }
        // End node has no redirect
        await store.SetAsync("enc", PolicyLevel.VDE, "/end", MakePolicy("enc", PolicyLevel.VDE));

        var startPolicy = MakePolicy("enc", PolicyLevel.VDE, customParams: new Dictionary<string, string> { ["redirect"] = "/n1" });
        var result = await CircularReferenceDetector.ValidateAsync("enc", PolicyLevel.VDE, "/start", startPolicy, store);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CircularRef_EmptyChain_NoCycle()
    {
        var store = CreateStore();
        var policy = MakePolicy("enc", PolicyLevel.VDE);

        var result = await CircularReferenceDetector.ValidateAsync("enc", PolicyLevel.VDE, "/v", policy, store);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task CircularRef_EnforceAtBlock_ProducesWarning()
    {
        var store = CreateStore();
        var policy = MakePolicy("enc", PolicyLevel.Block, cascade: CascadeStrategy.Enforce);

        var result = await CircularReferenceDetector.ValidateAsync("enc", PolicyLevel.Block, "/v/c/o/ch/b", policy, store);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CircularRef_NullCustomParams_NoCycle()
    {
        var store = CreateStore();
        var policy = MakePolicy("enc", PolicyLevel.VDE);

        var result = await CircularReferenceDetector.ValidateAsync("enc", PolicyLevel.VDE, "/v", policy, store);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CircularRef_RedirectToNonExistent_NoCycle()
    {
        var store = CreateStore();
        var policy = MakePolicy("enc", PolicyLevel.VDE,
            customParams: new Dictionary<string, string> { ["redirect"] = "/does_not_exist" });

        var result = await CircularReferenceDetector.ValidateAsync("enc", PolicyLevel.VDE, "/v", policy, store);

        result.IsValid.Should().BeTrue();
    }

    // ========================================================================
    // MergeConflictResolver (~10 tests)
    // ========================================================================

    [Fact]
    public void MergeResolver_MostRestrictive_PicksLowerNumericValue()
    {
        var resolver = new MergeConflictResolver(
            new Dictionary<string, MergeConflictMode> { ["intensity"] = MergeConflictMode.MostRestrictive });

        var result = resolver.Resolve("intensity", new[] { "80", "30", "60" });

        result.Should().Be("30");
    }

    [Fact]
    public void MergeResolver_Closest_PicksFirstValue()
    {
        var resolver = new MergeConflictResolver(
            new Dictionary<string, MergeConflictMode> { ["algo"] = MergeConflictMode.Closest });

        var result = resolver.Resolve("algo", new[] { "aes256", "aes128" });

        result.Should().Be("aes256");
    }

    [Fact]
    public void MergeResolver_Union_CombinesAllDistinctValues()
    {
        var resolver = new MergeConflictResolver(
            new Dictionary<string, MergeConflictMode> { ["tags"] = MergeConflictMode.Union });

        var result = resolver.Resolve("tags", new[] { "hipaa", "gdpr", "hipaa" });

        result.Should().Be("hipaa;gdpr");
    }

    [Fact]
    public void MergeResolver_DefaultIsClosest()
    {
        var resolver = new MergeConflictResolver();

        resolver.DefaultMode.Should().Be(MergeConflictMode.Closest);

        var result = resolver.Resolve("unknown", new[] { "first", "second" });
        result.Should().Be("first");
    }

    [Fact]
    public void MergeResolver_MultipleConflictingKeys_ResolvedIndependently()
    {
        var resolver = new MergeConflictResolver(
            new Dictionary<string, MergeConflictMode>
            {
                ["intensity"] = MergeConflictMode.MostRestrictive,
                ["algo"] = MergeConflictMode.Closest,
                ["tags"] = MergeConflictMode.Union
            });

        var params1 = new Dictionary<string, string> { ["intensity"] = "80", ["algo"] = "aes256", ["tags"] = "hipaa" };
        var params2 = new Dictionary<string, string> { ["intensity"] = "30", ["algo"] = "aes128", ["tags"] = "gdpr" };

        var result = resolver.ResolveAll(new[] { params1, params2 });

        result["intensity"].Should().Be("30");
        result["algo"].Should().Be("aes256");
        result["tags"].Should().Be("hipaa;gdpr");
    }

    [Fact]
    public void MergeResolver_SingleValue_ReturnsThatValue()
    {
        var resolver = new MergeConflictResolver();
        var result = resolver.Resolve("key", new[] { "only_value" });
        result.Should().Be("only_value");
    }

    [Fact]
    public void MergeResolver_MostRestrictive_LexicographicForStrings()
    {
        var resolver = new MergeConflictResolver(
            new Dictionary<string, MergeConflictMode> { ["algo"] = MergeConflictMode.MostRestrictive });

        var result = resolver.Resolve("algo", new[] { "zzz", "aaa", "mmm" });

        result.Should().Be("aaa");
    }

    [Fact]
    public void MergeResolver_ResolveAll_NullParams_Ignored()
    {
        var resolver = new MergeConflictResolver();
        var result = resolver.ResolveAll(new List<Dictionary<string, string>?> { null, null });
        result.Should().BeEmpty();
    }

    [Fact]
    public void MergeResolver_ResolveAll_EmptyList_ReturnsEmpty()
    {
        var resolver = new MergeConflictResolver();
        var result = resolver.ResolveAll(new List<Dictionary<string, string>?>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void MergeResolver_EmptyTagKey_ThrowsArgumentException()
    {
        var resolver = new MergeConflictResolver();
        var act = () => resolver.Resolve("", new[] { "value" });
        act.Should().Throw<ArgumentException>();
    }

    // ========================================================================
    // VersionedPolicyCache (~8 tests)
    // ========================================================================

    [Fact]
    public void Cache_GetSnapshot_ReturnsCurrentSnapshot()
    {
        var cache = new VersionedPolicyCache();
        var snapshot = cache.GetSnapshot();

        snapshot.Should().NotBeNull();
        snapshot.Version.Should().Be(0);
    }

    [Fact]
    public void Cache_VersionChange_IncrementsVersion()
    {
        var cache = new VersionedPolicyCache();
        var policies = new Dictionary<string, FeaturePolicy>
        {
            ["enc:VDE:/v"] = MakePolicy("enc", PolicyLevel.VDE, 80)
        };

        cache.Update(policies);

        cache.CurrentVersion.Should().Be(1);
    }

    [Fact]
    public void Cache_Update_InvalidatesOldSnapshot()
    {
        var cache = new VersionedPolicyCache();

        var oldSnapshot = cache.GetSnapshot();
        oldSnapshot.Version.Should().Be(0);

        cache.Update(new Dictionary<string, FeaturePolicy>
        {
            ["enc:VDE:/v"] = MakePolicy("enc", PolicyLevel.VDE, 80)
        });

        var newSnapshot = cache.GetSnapshot();
        newSnapshot.Version.Should().Be(1);
        oldSnapshot.Version.Should().Be(0);
    }

    [Fact]
    public void Cache_CacheMiss_ReturnsNullFromSnapshot()
    {
        var cache = new VersionedPolicyCache();
        var snapshot = cache.GetSnapshot();

        var policy = snapshot.TryGetPolicy("enc", PolicyLevel.VDE, "/v");
        policy.Should().BeNull();
    }

    [Fact]
    public void Cache_CacheHit_ReturnsPolicyFromSnapshot()
    {
        var cache = new VersionedPolicyCache();
        var policy = MakePolicy("enc", PolicyLevel.VDE, 80);
        cache.Update(new Dictionary<string, FeaturePolicy>
        {
            ["enc:VDE:/v"] = policy
        });

        var snapshot = cache.GetSnapshot();
        var retrieved = snapshot.TryGetPolicy("enc", PolicyLevel.VDE, "/v");

        retrieved.Should().NotBeNull();
        retrieved!.IntensityLevel.Should().Be(80);
    }

    [Fact]
    public async Task Cache_UpdateFromStore_PopulatesSnapshot()
    {
        var store = CreateStore();
        await store.SetAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 75));

        var cache = new VersionedPolicyCache();
        await cache.UpdateFromStoreAsync(store, new[] { "enc" });

        var snapshot = cache.GetSnapshot();
        snapshot.Version.Should().Be(1);
        var policy = snapshot.TryGetPolicy("enc", PolicyLevel.VDE, "/v");
        policy.Should().NotBeNull();
    }

    [Fact]
    public void Cache_PreviousSnapshot_AvailableAfterUpdate()
    {
        var cache = new VersionedPolicyCache();
        var v0 = cache.GetSnapshot();

        cache.Update(new Dictionary<string, FeaturePolicy>
        {
            ["enc:VDE:/v"] = MakePolicy("enc", PolicyLevel.VDE, 80)
        });

        var previous = cache.GetPreviousSnapshot();
        previous.Version.Should().Be(0);
    }

    [Fact]
    public void Cache_ParallelReads_DuringWrite_Safe()
    {
        var cache = new VersionedPolicyCache();
        var snapshot = cache.GetSnapshot();

        // Simulate parallel read during write
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            var s = cache.GetSnapshot();
            s.Should().NotBeNull();
        }));

        // Write during reads
        cache.Update(new Dictionary<string, FeaturePolicy>
        {
            ["enc:VDE:/v"] = MakePolicy("enc", PolicyLevel.VDE, 80)
        });

        Task.WhenAll(tasks).Wait();

        // Original snapshot still valid
        snapshot.Version.Should().Be(0);
    }

    // ========================================================================
    // CascadeOverrideStore (~8 tests)
    // ========================================================================

    [Fact]
    public void OverrideStore_CompositeKey_RoundTrips()
    {
        var store = new CascadeOverrideStore();
        store.SetOverride("encryption", PolicyLevel.VDE, CascadeStrategy.Enforce);

        store.TryGetOverride("encryption", PolicyLevel.VDE, out var strategy).Should().BeTrue();
        strategy.Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public async Task OverrideStore_PersistenceRoundTrip()
    {
        var overrideStore = new CascadeOverrideStore();
        overrideStore.SetOverride("enc", PolicyLevel.VDE, CascadeStrategy.Enforce);
        overrideStore.SetOverride("comp", PolicyLevel.Container, CascadeStrategy.MostRestrictive);

        var persistence = CreatePersistence();
        await overrideStore.SaveToPersistenceAsync(persistence);

        var newStore = new CascadeOverrideStore();
        var loaded = await newStore.LoadFromPersistenceAsync(persistence);

        loaded.Should().Be(2);
        newStore.TryGetOverride("enc", PolicyLevel.VDE, out var s1).Should().BeTrue();
        s1.Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public void OverrideStore_RemoveOverride_RestoresDefault()
    {
        var store = new CascadeOverrideStore();
        store.SetOverride("enc", PolicyLevel.VDE, CascadeStrategy.Enforce);
        store.RemoveOverride("enc", PolicyLevel.VDE).Should().BeTrue();

        store.TryGetOverride("enc", PolicyLevel.VDE, out _).Should().BeFalse();
    }

    [Fact]
    public void OverrideStore_RemoveNonExistent_ReturnsFalse()
    {
        var store = new CascadeOverrideStore();
        store.RemoveOverride("nonexistent", PolicyLevel.VDE).Should().BeFalse();
    }

    [Fact]
    public void OverrideStore_GetAllOverrides_ReturnsAll()
    {
        var store = new CascadeOverrideStore();
        store.SetOverride("enc", PolicyLevel.VDE, CascadeStrategy.Enforce);
        store.SetOverride("comp", PolicyLevel.Container, CascadeStrategy.MostRestrictive);

        var all = store.GetAllOverrides();
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task OverrideStore_OverrideTakesPrecedence_InResolution()
    {
        var policyStore = CreateStore();
        var persistence = CreatePersistence();
        var overrideStore = new CascadeOverrideStore();

        // Set a policy with Override cascade
        var policy = MakePolicy("encryption", PolicyLevel.VDE, 80, CascadeStrategy.Override);
        await policyStore.SetAsync("encryption", PolicyLevel.VDE, "/v", policy);

        // Set cascade override to Enforce
        overrideStore.SetOverride("encryption", PolicyLevel.VDE, CascadeStrategy.Enforce);

        var engine = new PolicyResolutionEngine(policyStore, persistence, overrideStore: overrideStore);
        var ctx = new PolicyResolutionContext { Path = "/v" };

        var result = await engine.ResolveAsync("encryption", ctx);

        result.AppliedCascade.Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public void OverrideStore_EmptyFeatureId_ThrowsArgumentException()
    {
        var store = new CascadeOverrideStore();
        var act = () => store.SetOverride("", PolicyLevel.VDE, CascadeStrategy.Enforce);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OverrideStore_Count_TracksCorrectly()
    {
        var store = new CascadeOverrideStore();
        store.Count.Should().Be(0);

        store.SetOverride("enc", PolicyLevel.VDE, CascadeStrategy.Enforce);
        store.Count.Should().Be(1);

        store.RemoveOverride("enc", PolicyLevel.VDE);
        store.Count.Should().Be(0);
    }

    // ========================================================================
    // PolicyCategoryDefaults (~8 tests)
    // ========================================================================

    [Fact]
    public void CategoryDefaults_UnknownFeature_ReturnsInherit()
    {
        var defaults = new PolicyCategoryDefaults();
        var strategy = defaults.GetDefaultStrategy("completely_unknown");

        strategy.Should().Be(CascadeStrategy.Inherit);
    }

    [Fact]
    public void CategoryDefaults_SecurityCategory_ReturnsMostRestrictive()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("security").Should().Be(CascadeStrategy.MostRestrictive);
    }

    [Fact]
    public void CategoryDefaults_EncryptionCategory_ReturnsMostRestrictive()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("encryption").Should().Be(CascadeStrategy.MostRestrictive);
    }

    [Fact]
    public void CategoryDefaults_ComplianceCategory_ReturnsEnforce()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("compliance").Should().Be(CascadeStrategy.Enforce);
    }

    [Fact]
    public void CategoryDefaults_GovernanceCategory_ReturnsMerge()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("governance").Should().Be(CascadeStrategy.Merge);
    }

    [Fact]
    public void CategoryDefaults_CompressionCategory_ReturnsOverride()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("compression").Should().Be(CascadeStrategy.Override);
    }

    [Fact]
    public void CategoryDefaults_DotPrefixLookup_MatchesCategory()
    {
        var defaults = new PolicyCategoryDefaults();
        defaults.GetDefaultStrategy("security.encryption").Should().Be(CascadeStrategy.MostRestrictive);
    }

    [Fact]
    public void CategoryDefaults_UserOverrides_TakePrecedence()
    {
        var overrides = new Dictionary<string, CascadeStrategy>
        {
            ["encryption"] = CascadeStrategy.Override
        };
        var defaults = new PolicyCategoryDefaults(overrides);

        defaults.GetDefaultStrategy("encryption").Should().Be(CascadeStrategy.Override);
    }

    // ========================================================================
    // PolicyComplianceScorer (~10 tests)
    // ========================================================================

    [Fact]
    public void ComplianceScorer_GdprRetentionPolicy_ChecksKeyExistence()
    {
        var policies = new Dictionary<string, FeaturePolicy>
        {
            ["retention"] = MakePolicy("retention", PolicyLevel.VDE, 80,
                customParams: new Dictionary<string, string> { ["retention_policy"] = "7years" })
        };

        var scorer = new PolicyComplianceScorer(policies);
        var report = scorer.ScoreAgainst(RegulatoryTemplate.Gdpr());

        var retentionItem = report.AllItems.First(i => i.Requirement.RequirementId == "GDPR-RET-01");
        retentionItem.Passed.Should().BeTrue();
    }

    [Fact]
    public void ComplianceScorer_HipaaEncryption_ChecksMinIntensity()
    {
        var policies = new Dictionary<string, FeaturePolicy>
        {
            ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE, 90,
                CascadeStrategy.MostRestrictive, AiAutonomyLevel.Suggest)
        };

        var scorer = new PolicyComplianceScorer(policies);
        var report = scorer.ScoreAgainst(RegulatoryTemplate.Hipaa());

        var encItem = report.AllItems.First(i => i.Requirement.RequirementId == "HIPAA-ENC-01");
        encItem.Passed.Should().BeTrue();
    }

    [Fact]
    public void ComplianceScorer_Soc2_ChecksAuditRequirements()
    {
        var policies = new Dictionary<string, FeaturePolicy>
        {
            ["audit"] = MakePolicy("audit", PolicyLevel.VDE, 90)
        };

        var scorer = new PolicyComplianceScorer(policies);
        var report = scorer.ScoreAgainst(RegulatoryTemplate.Soc2());

        var auditItem = report.AllItems.First(i => i.Requirement.RequirementId == "SOC2-AUD-01");
        auditItem.Passed.Should().BeTrue();
    }

    [Fact]
    public void ComplianceScorer_FedRamp_ChecksFipsRequirements()
    {
        var policies = new Dictionary<string, FeaturePolicy>
        {
            ["encryption"] = MakePolicy("encryption", PolicyLevel.VDE, 95,
                CascadeStrategy.Enforce, AiAutonomyLevel.ManualOnly)
        };

        var scorer = new PolicyComplianceScorer(policies);
        var report = scorer.ScoreAgainst(RegulatoryTemplate.FedRamp());

        var encItem = report.AllItems.First(i => i.Requirement.RequirementId == "FEDRAMP-ENC-01");
        encItem.Passed.Should().BeTrue();
    }

    [Fact]
    public void ComplianceScorer_WeightedScoring_CalculatesCorrectly()
    {
        // Create a custom template with known weights
        var template = new RegulatoryTemplate
        {
            FrameworkName = "Test",
            FrameworkVersion = "1.0",
            Description = "Test template",
            Requirements = new[]
            {
                new RegulatoryRequirement
                {
                    RequirementId = "T-01", Description = "Easy", Category = "test",
                    FeatureId = "enc", MinimumIntensity = 50, Weight = 1.0,
                    Remediation = "Fix it"
                },
                new RegulatoryRequirement
                {
                    RequirementId = "T-02", Description = "Hard", Category = "test",
                    FeatureId = "missing", MinimumIntensity = 90, Weight = 1.0,
                    Remediation = "Fix it"
                }
            }
        };

        var policies = new Dictionary<string, FeaturePolicy>
        {
            ["enc"] = MakePolicy("enc", PolicyLevel.VDE, 80)
        };

        var scorer = new PolicyComplianceScorer(policies);
        var report = scorer.ScoreAgainst(template);

        // 1 of 2 passed, equal weights -> 50%
        report.Score.Should().Be(50);
    }

    [Fact]
    public void ComplianceScorer_GradeA_For90Plus()
    {
        var template = new RegulatoryTemplate
        {
            FrameworkName = "Test",
            FrameworkVersion = "1.0",
            Description = "Test",
            Requirements = new[]
            {
                new RegulatoryRequirement
                {
                    RequirementId = "T-01", Description = "Pass", Category = "test",
                    FeatureId = "enc", MinimumIntensity = 50, Weight = 1.0,
                    Remediation = "n/a"
                }
            }
        };

        var policies = new Dictionary<string, FeaturePolicy>
        {
            ["enc"] = MakePolicy("enc", PolicyLevel.VDE, 80)
        };

        var scorer = new PolicyComplianceScorer(policies);
        var report = scorer.ScoreAgainst(template);

        report.Score.Should().Be(100);
        report.Grade.Should().Be("A");
    }

    [Fact]
    public void ComplianceScorer_GradeF_ForZeroScore()
    {
        var template = new RegulatoryTemplate
        {
            FrameworkName = "Test",
            FrameworkVersion = "1.0",
            Description = "Test",
            Requirements = new[]
            {
                new RegulatoryRequirement
                {
                    RequirementId = "T-01", Description = "Fail", Category = "test",
                    FeatureId = "missing", MinimumIntensity = 90, Weight = 1.0,
                    Remediation = "n/a"
                }
            }
        };

        var policies = new Dictionary<string, FeaturePolicy>();
        var scorer = new PolicyComplianceScorer(policies);
        var report = scorer.ScoreAgainst(template);

        report.Score.Should().Be(0);
        report.Grade.Should().Be("F");
    }

    [Fact]
    public void ComplianceScorer_MissingFeature_FailsRequirement()
    {
        var policies = new Dictionary<string, FeaturePolicy>();
        var scorer = new PolicyComplianceScorer(policies);
        var report = scorer.ScoreAgainst(RegulatoryTemplate.Hipaa());

        report.FailedRequirements.Should().Be(report.TotalRequirements);
        report.Gaps.Should().HaveCount(report.TotalRequirements);
    }

    [Fact]
    public void ComplianceScorer_ScoreAll_ReturnsAllFrameworks()
    {
        var policies = OperationalProfile.Strict().FeaturePolicies;
        var reports = PolicyComplianceScorer.ScoreAll(policies);

        reports.Should().HaveCount(4);
        reports.Select(r => r.FrameworkName).Should().Contain("HIPAA");
        reports.Select(r => r.FrameworkName).Should().Contain("GDPR");
        reports.Select(r => r.FrameworkName).Should().Contain("SOC2");
        reports.Select(r => r.FrameworkName).Should().Contain("FedRAMP");
    }

    [Fact]
    public void ComplianceScorer_RemediationMessagesProduced()
    {
        var policies = new Dictionary<string, FeaturePolicy>();
        var scorer = new PolicyComplianceScorer(policies);
        var report = scorer.ScoreAgainst(RegulatoryTemplate.Hipaa());

        foreach (var gap in report.Gaps)
        {
            gap.Remediation.Should().NotBeNullOrEmpty();
            gap.GapDescription.Should().NotBeNullOrEmpty();
        }
    }

    // ========================================================================
    // PolicyPersistenceComplianceValidator (~8 tests)
    // ========================================================================

    [Fact]
    public void PersistenceValidator_HipaaInMemory_Violates()
    {
        var config = new PolicyPersistenceConfiguration
        {
            Backend = PolicyPersistenceBackend.InMemory,
            ActiveComplianceFrameworks = new[] { "HIPAA" }
        };

        var result = PolicyPersistenceComplianceValidator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Rule == "HIPAA-AUDIT-002");
    }

    [Fact]
    public void PersistenceValidator_Soc2InMemory_Violates()
    {
        var config = new PolicyPersistenceConfiguration
        {
            Backend = PolicyPersistenceBackend.InMemory,
            ActiveComplianceFrameworks = new[] { "SOC2" }
        };

        var result = PolicyPersistenceComplianceValidator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Framework == "SOC2");
    }

    [Fact]
    public void PersistenceValidator_GdprInMemory_Violates()
    {
        var config = new PolicyPersistenceConfiguration
        {
            Backend = PolicyPersistenceBackend.InMemory,
            ActiveComplianceFrameworks = new[] { "GDPR" }
        };

        var result = PolicyPersistenceComplianceValidator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Framework == "GDPR");
    }

    [Fact]
    public void PersistenceValidator_FedRampNonTamperProof_Violates()
    {
        var config = new PolicyPersistenceConfiguration
        {
            Backend = PolicyPersistenceBackend.Database,
            ActiveComplianceFrameworks = new[] { "FedRAMP" }
        };

        var result = PolicyPersistenceComplianceValidator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Rule == "FEDRAMP-AUDIT-001");
    }

    [Fact]
    public void PersistenceValidator_TamperProof_PassesAll()
    {
        var config = new PolicyPersistenceConfiguration
        {
            Backend = PolicyPersistenceBackend.TamperProof,
            ActiveComplianceFrameworks = new[] { "HIPAA", "SOC2", "GDPR", "FedRAMP" }
        };

        var result = PolicyPersistenceComplianceValidator.Validate(config);

        result.IsValid.Should().BeTrue();
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void PersistenceValidator_HybridWithTamperProofAudit_PassesFedRamp()
    {
        var config = new PolicyPersistenceConfiguration
        {
            Backend = PolicyPersistenceBackend.Hybrid,
            AuditBackend = PolicyPersistenceBackend.TamperProof,
            ActiveComplianceFrameworks = new[] { "FedRAMP" }
        };

        var result = PolicyPersistenceComplianceValidator.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void PersistenceValidator_ActionableRemediation_InViolations()
    {
        var config = new PolicyPersistenceConfiguration
        {
            Backend = PolicyPersistenceBackend.InMemory,
            ActiveComplianceFrameworks = new[] { "HIPAA" }
        };

        var result = PolicyPersistenceComplianceValidator.Validate(config);

        foreach (var violation in result.Violations)
        {
            violation.Remediation.Should().NotBeNullOrEmpty();
            violation.Message.Should().NotBeNullOrEmpty();
            violation.Rule.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void PersistenceValidator_NoFrameworks_AlwaysValid()
    {
        var config = new PolicyPersistenceConfiguration
        {
            Backend = PolicyPersistenceBackend.InMemory,
            ActiveComplianceFrameworks = Array.Empty<string>()
        };

        var result = PolicyPersistenceComplianceValidator.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    // ========================================================================
    // PolicyMarketplace (~10 tests)
    // ========================================================================

    [Fact]
    public void Marketplace_HipaaTemplate_LoadsCorrectly()
    {
        var template = PolicyMarketplace.HipaaTemplate();

        template.Name.Should().Be("HIPAA-Compliant-Standard");
        template.Policies.Should().HaveCount(3);
        template.Id.Should().Be("00000000-0000-0000-0000-000000000001");
    }

    [Fact]
    public void Marketplace_GdprTemplate_LoadsCorrectly()
    {
        var template = PolicyMarketplace.GdprTemplate();

        template.Name.Should().Be("GDPR-Standard");
        template.Policies.Should().HaveCount(3);
        template.Id.Should().Be("00000000-0000-0000-0000-000000000002");
    }

    [Fact]
    public void Marketplace_HighPerformanceTemplate_LoadsCorrectly()
    {
        var template = PolicyMarketplace.HighPerformanceTemplate();

        template.Name.Should().Be("High-Performance");
        template.Policies.Should().HaveCount(3);
        template.Id.Should().Be("00000000-0000-0000-0000-000000000003");
    }

    [Fact]
    public async Task Marketplace_ExportAndImport_RoundTrips()
    {
        var persistence = new InMemoryPolicyPersistence();
        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/",
            MakePolicy("enc", PolicyLevel.VDE, 80));

        var marketplace = new PolicyMarketplace();
        var template = await marketplace.ExportTemplateAsync(persistence,
            "Test", "Test template", "test@test.com", new Version(1, 0, 0));

        var bytes = marketplace.SerializeTemplate(template);
        bytes.Should().NotBeEmpty();

        var targetPersistence = new InMemoryPolicyPersistence();
        var result = await marketplace.ImportFromBytesAsync(targetPersistence, bytes, overwriteExisting: true);

        result.Success.Should().BeTrue();
        result.PoliciesImported.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Marketplace_SerializeAndDeserialize_ChecksumVerified()
    {
        var marketplace = new PolicyMarketplace();
        var template = PolicyMarketplace.HipaaTemplate();

        var bytes = marketplace.SerializeTemplate(template);
        var deserialized = marketplace.DeserializeTemplate(bytes);

        deserialized.Name.Should().Be(template.Name);
        deserialized.Policies.Should().HaveCount(template.Policies.Count);
    }

    [Fact]
    public void Marketplace_VersionSerializer_StringFormat()
    {
        var marketplace = new PolicyMarketplace();
        var template = PolicyMarketplace.HipaaTemplate();

        var bytes = marketplace.SerializeTemplate(template);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        // Version should be serialized as string, not as object
        json.Should().Contain("\"1.0.0\"");
    }

    [Fact]
    public void Marketplace_DeterministicGuids_ForBuiltInTemplates()
    {
        var hipaa = PolicyMarketplace.HipaaTemplate();
        var gdpr = PolicyMarketplace.GdprTemplate();
        var perf = PolicyMarketplace.HighPerformanceTemplate();

        hipaa.Id.Should().Be("00000000-0000-0000-0000-000000000001");
        gdpr.Id.Should().Be("00000000-0000-0000-0000-000000000002");
        perf.Id.Should().Be("00000000-0000-0000-0000-000000000003");
    }

    [Fact]
    public void Marketplace_CheckCompatibility_Compatible()
    {
        var marketplace = new PolicyMarketplace(new Version(6, 0, 0));
        var template = PolicyMarketplace.HipaaTemplate();

        var compat = marketplace.CheckCompatibility(template);

        compat.IsCompatible.Should().BeTrue();
    }

    [Fact]
    public void Marketplace_CheckCompatibility_Incompatible()
    {
        var marketplace = new PolicyMarketplace(new Version(5, 0, 0));
        var template = PolicyMarketplace.HipaaTemplate(); // requires 6.0.0

        var compat = marketplace.CheckCompatibility(template);

        compat.IsCompatible.Should().BeFalse();
        compat.IncompatibilityReason.Should().Contain("6.0.0");
    }

    [Fact]
    public async Task Marketplace_ImportTemplate_WithProfile()
    {
        var marketplace = new PolicyMarketplace();
        var template = PolicyMarketplace.HipaaTemplate();

        var persistence = new InMemoryPolicyPersistence();
        var result = await marketplace.ImportTemplateAsync(persistence, template, overwriteExisting: true);

        result.Success.Should().BeTrue();
        result.ProfileImported.Should().BeTrue();
        result.PoliciesImported.Should().Be(3);
    }
}
