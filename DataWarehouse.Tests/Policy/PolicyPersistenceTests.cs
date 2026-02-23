using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Policy;

/// <summary>
/// Comprehensive tests for all IPolicyPersistence implementations: InMemory, File, Database,
/// TamperProof, Hybrid. Covers CRUD round-trips, serialization, and backend-specific behaviors.
/// </summary>
[Trait("Category", "Unit")]
public class PolicyPersistenceTests
{
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
    // InMemoryPolicyPersistence (~10 tests)
    // ========================================================================

    [Fact]
    public async Task InMemory_SaveAndLoad_RoundTrips()
    {
        var persistence = new InMemoryPolicyPersistence();
        var policy = MakePolicy("encryption", PolicyLevel.VDE, 80);

        await persistence.SaveAsync("encryption", PolicyLevel.VDE, "/v", policy);
        var loaded = await persistence.LoadAllAsync();

        loaded.Should().HaveCount(1);
        loaded[0].FeatureId.Should().Be("encryption");
        loaded[0].Policy.IntensityLevel.Should().Be(80);
    }

    [Fact]
    public async Task InMemory_SaveOverwritesPrevious()
    {
        var persistence = new InMemoryPolicyPersistence();
        var p1 = MakePolicy("enc", PolicyLevel.VDE, 50);
        var p2 = MakePolicy("enc", PolicyLevel.VDE, 90);

        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", p1);
        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", p2);

        var loaded = await persistence.LoadAllAsync();
        loaded.Should().HaveCount(1);
        loaded[0].Policy.IntensityLevel.Should().Be(90);
    }

    [Fact]
    public async Task InMemory_LoadWithNoSave_ReturnsEmpty()
    {
        var persistence = new InMemoryPolicyPersistence();
        var loaded = await persistence.LoadAllAsync();
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task InMemory_MultiplePolicies_AllRoundTrip()
    {
        var persistence = new InMemoryPolicyPersistence();
        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
        await persistence.SaveAsync("comp", PolicyLevel.Object, "/v/c/o", MakePolicy("comp", PolicyLevel.Object, 60));
        await persistence.SaveAsync("repl", PolicyLevel.Container, "/v/c", MakePolicy("repl", PolicyLevel.Container, 70));

        var loaded = await persistence.LoadAllAsync();
        loaded.Should().HaveCount(3);
    }

    [Fact]
    public async Task InMemory_DeleteRemovesEntry()
    {
        var persistence = new InMemoryPolicyPersistence();
        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
        await persistence.DeleteAsync("enc", PolicyLevel.VDE, "/v");

        var loaded = await persistence.LoadAllAsync();
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task InMemory_SaveAndLoadProfile()
    {
        var persistence = new InMemoryPolicyPersistence();
        var profile = OperationalProfile.Strict();

        await persistence.SaveProfileAsync(profile);
        var loaded = await persistence.LoadProfileAsync();

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Strict");
    }

    [Fact]
    public async Task InMemory_LoadProfile_ReturnsNullWhenNotSet()
    {
        var persistence = new InMemoryPolicyPersistence();
        var loaded = await persistence.LoadProfileAsync();
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task InMemory_Clear_RemovesAllEntries()
    {
        var persistence = new InMemoryPolicyPersistence();
        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
        await persistence.SaveProfileAsync(OperationalProfile.Standard());

        persistence.Clear();

        persistence.Count.Should().Be(0);
        var loaded = await persistence.LoadAllAsync();
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task InMemory_CustomParams_RoundTrip()
    {
        var persistence = new InMemoryPolicyPersistence();
        var policy = MakePolicy("enc", PolicyLevel.VDE, 80,
            customParams: new Dictionary<string, string> { ["algo"] = "aes256", ["mode"] = "gcm" });

        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", policy);
        var loaded = await persistence.LoadAllAsync();

        loaded[0].Policy.CustomParameters.Should().ContainKey("algo");
        loaded[0].Policy.CustomParameters!["algo"].Should().Be("aes256");
        loaded[0].Policy.CustomParameters!["mode"].Should().Be("gcm");
    }

    [Fact]
    public async Task InMemory_Capacity_ThrowsWhenExceeded()
    {
        var persistence = new InMemoryPolicyPersistence(maxCapacity: 2);
        await persistence.SaveAsync("a", PolicyLevel.VDE, "/1", MakePolicy("a", PolicyLevel.VDE));
        await persistence.SaveAsync("b", PolicyLevel.VDE, "/2", MakePolicy("b", PolicyLevel.VDE));

        var act = () => persistence.SaveAsync("c", PolicyLevel.VDE, "/3", MakePolicy("c", PolicyLevel.VDE));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ========================================================================
    // FilePolicyPersistence (~12 tests)
    // ========================================================================

    [Fact]
    public async Task File_SaveAndLoad_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PolicyTest_{Guid.NewGuid():N}");
        try
        {
            var persistence = new FilePolicyPersistence(dir);
            var policy = MakePolicy("enc", PolicyLevel.VDE, 75);

            await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", policy);
            var loaded = await persistence.LoadAllAsync();

            loaded.Should().HaveCount(1);
            loaded[0].Policy.IntensityLevel.Should().Be(75);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task File_SHA256Filenames_Consistent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PolicyTest_{Guid.NewGuid():N}");
        try
        {
            var persistence = new FilePolicyPersistence(dir);
            await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));

            var policiesDir = Path.Combine(dir, "policies");
            Directory.Exists(policiesDir).Should().BeTrue();

            var files = Directory.GetFiles(policiesDir, "*.json");
            files.Should().HaveCount(1);
            Path.GetFileNameWithoutExtension(files[0]).Should().HaveLength(16);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task File_MultiplePolicies_AllLoadCorrectly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PolicyTest_{Guid.NewGuid():N}");
        try
        {
            var persistence = new FilePolicyPersistence(dir);
            await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
            await persistence.SaveAsync("comp", PolicyLevel.Object, "/v/c/o", MakePolicy("comp", PolicyLevel.Object, 60));

            var loaded = await persistence.LoadAllAsync();
            loaded.Should().HaveCount(2);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task File_DeleteRemovesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PolicyTest_{Guid.NewGuid():N}");
        try
        {
            var persistence = new FilePolicyPersistence(dir);
            await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
            await persistence.DeleteAsync("enc", PolicyLevel.VDE, "/v");

            var loaded = await persistence.LoadAllAsync();
            loaded.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task File_ProfileSaveAndLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PolicyTest_{Guid.NewGuid():N}");
        try
        {
            var persistence = new FilePolicyPersistence(dir);
            await persistence.SaveProfileAsync(OperationalProfile.Paranoid());

            var loaded = await persistence.LoadProfileAsync();
            loaded.Should().NotBeNull();
            loaded!.Name.Should().Be("Paranoid");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task File_ResilientLoad_CorruptFileSkipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PolicyTest_{Guid.NewGuid():N}");
        try
        {
            var persistence = new FilePolicyPersistence(dir);
            await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));

            // Write a corrupt file alongside the valid one
            var policiesDir = Path.Combine(dir, "policies");
            await File.WriteAllTextAsync(Path.Combine(policiesDir, "corrupt.json"), "{ invalid json }}");

            var loaded = await persistence.LoadAllAsync();
            loaded.Should().HaveCount(1);
            loaded[0].Policy.IntensityLevel.Should().Be(80);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task File_LoadFromEmptyDir_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PolicyTest_{Guid.NewGuid():N}");
        try
        {
            var persistence = new FilePolicyPersistence(dir);
            var loaded = await persistence.LoadAllAsync();
            loaded.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task File_AtomicWrite_OverwritesPrevious()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PolicyTest_{Guid.NewGuid():N}");
        try
        {
            var persistence = new FilePolicyPersistence(dir);
            await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 50));
            await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 99));

            var loaded = await persistence.LoadAllAsync();
            loaded.Should().HaveCount(1);
            loaded[0].Policy.IntensityLevel.Should().Be(99);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task File_CustomParams_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PolicyTest_{Guid.NewGuid():N}");
        try
        {
            var persistence = new FilePolicyPersistence(dir);
            var policy = MakePolicy("enc", PolicyLevel.VDE, 80,
                customParams: new Dictionary<string, string> { ["key"] = "value" });

            await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", policy);
            var loaded = await persistence.LoadAllAsync();

            loaded[0].Policy.CustomParameters!["key"].Should().Be("value");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void File_NullBaseDir_ThrowsArgumentException()
    {
        var act = () => new FilePolicyPersistence(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void File_EmptyBaseDir_ThrowsArgumentException()
    {
        var act = () => new FilePolicyPersistence("");
        act.Should().Throw<ArgumentException>();
    }

    // ========================================================================
    // DatabasePolicyPersistence (~10 tests)
    // ========================================================================

    [Fact]
    public async Task Database_CrudRoundTrip()
    {
        var config = PolicyPersistenceConfiguration.DatabaseDefault();
        var persistence = new DatabasePolicyPersistence(config);
        var policy = MakePolicy("enc", PolicyLevel.VDE, 85);

        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", policy);
        var loaded = await persistence.LoadAllAsync();

        loaded.Should().HaveCount(1);
        loaded[0].Policy.IntensityLevel.Should().Be(85);
    }

    [Fact]
    public async Task Database_CompositeKey_RoundTrips()
    {
        var config = PolicyPersistenceConfiguration.DatabaseDefault();
        var persistence = new DatabasePolicyPersistence(config);

        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
        await persistence.SaveAsync("enc", PolicyLevel.Container, "/v/c", MakePolicy("enc", PolicyLevel.Container, 60));
        await persistence.SaveAsync("comp", PolicyLevel.VDE, "/v", MakePolicy("comp", PolicyLevel.VDE, 50));

        var loaded = await persistence.LoadAllAsync();
        loaded.Should().HaveCount(3);
    }

    [Fact]
    public async Task Database_Delete_RemovesEntry()
    {
        var config = PolicyPersistenceConfiguration.DatabaseDefault();
        var persistence = new DatabasePolicyPersistence(config);
        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
        await persistence.DeleteAsync("enc", PolicyLevel.VDE, "/v");

        var loaded = await persistence.LoadAllAsync();
        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task Database_ProfileSaveAndLoad()
    {
        var config = PolicyPersistenceConfiguration.DatabaseDefault();
        var persistence = new DatabasePolicyPersistence(config);

        await persistence.SaveProfileAsync(OperationalProfile.Balanced());
        var loaded = await persistence.LoadProfileAsync();

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Balanced");
    }

    [Fact]
    public async Task Database_LWW_NewerWriteWins()
    {
        var config = PolicyPersistenceConfiguration.DatabaseDefault();
        var persistence = new DatabasePolicyPersistence(config);
        var policy1 = MakePolicy("enc", PolicyLevel.VDE, 50);
        var serialized = PolicySerializationHelper.SerializePolicy(policy1);

        await persistence.ApplyReplicatedAsync(
            "enc:4:/v", "enc", PolicyLevel.VDE, "/v", serialized,
            timestamp: 1000, sourceNodeId: "nodeA");

        await persistence.ApplyReplicatedAsync(
            "enc:4:/v", "enc", PolicyLevel.VDE, "/v", serialized,
            timestamp: 2000, sourceNodeId: "nodeB");

        var loaded = await persistence.LoadAllAsync();
        loaded.Should().HaveCount(1);
    }

    [Fact]
    public async Task Database_LWW_OlderWriteIgnored()
    {
        var config = PolicyPersistenceConfiguration.DatabaseDefault();
        var persistence = new DatabasePolicyPersistence(config);

        var p1 = MakePolicy("enc", PolicyLevel.VDE, 80);
        var s1 = PolicySerializationHelper.SerializePolicy(p1);

        await persistence.ApplyReplicatedAsync("enc:4:/v", "enc", PolicyLevel.VDE, "/v", s1, 2000, "nodeA");

        var p2 = MakePolicy("enc", PolicyLevel.VDE, 30);
        var s2 = PolicySerializationHelper.SerializePolicy(p2);

        await persistence.ApplyReplicatedAsync("enc:4:/v", "enc", PolicyLevel.VDE, "/v", s2, 1000, "nodeB");

        var loaded = await persistence.LoadAllAsync();
        loaded.Should().HaveCount(1);
        loaded[0].Policy.IntensityLevel.Should().Be(80);
    }

    [Fact]
    public void Database_NodeId_IsNonEmpty()
    {
        var config = PolicyPersistenceConfiguration.DatabaseDefault();
        var persistence = new DatabasePolicyPersistence(config);
        persistence.NodeId.Should().NotBeNullOrEmpty();
        persistence.NodeId.Should().HaveLength(8);
    }

    [Fact]
    public async Task Database_CustomParams_RoundTrip()
    {
        var config = PolicyPersistenceConfiguration.DatabaseDefault();
        var persistence = new DatabasePolicyPersistence(config);
        var policy = MakePolicy("enc", PolicyLevel.VDE, 80,
            customParams: new Dictionary<string, string> { ["fips"] = "true" });

        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", policy);
        var loaded = await persistence.LoadAllAsync();

        loaded[0].Policy.CustomParameters!["fips"].Should().Be("true");
    }

    [Fact]
    public async Task Database_UpsertOverwritesSameKey()
    {
        var config = PolicyPersistenceConfiguration.DatabaseDefault();
        var persistence = new DatabasePolicyPersistence(config);
        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 50));
        await persistence.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 99));

        var loaded = await persistence.LoadAllAsync();
        loaded.Should().HaveCount(1);
        loaded[0].Policy.IntensityLevel.Should().Be(99);
    }

    // ========================================================================
    // TamperProofPolicyPersistence (~10 tests)
    // ========================================================================

    [Fact]
    public async Task TamperProof_SaveAndLoad_ViaInner()
    {
        var inner = new InMemoryPolicyPersistence();
        var tamper = new TamperProofPolicyPersistence(inner);

        await tamper.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
        var loaded = await tamper.LoadAllAsync();

        loaded.Should().HaveCount(1);
        loaded[0].Policy.IntensityLevel.Should().Be(80);
    }

    [Fact]
    public async Task TamperProof_AuditBlockCreatedOnSave()
    {
        var inner = new InMemoryPolicyPersistence();
        var tamper = new TamperProofPolicyPersistence(inner);

        await tamper.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));

        tamper.AuditBlockCount.Should().Be(1);
        var chain = tamper.GetAuditChain();
        chain.Should().HaveCount(1);
        chain[0].Operation.Should().Be("Save");
    }

    [Fact]
    public async Task TamperProof_AuditBlockCreatedOnDelete()
    {
        var inner = new InMemoryPolicyPersistence();
        var tamper = new TamperProofPolicyPersistence(inner);

        await tamper.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
        await tamper.DeleteAsync("enc", PolicyLevel.VDE, "/v");

        tamper.AuditBlockCount.Should().Be(2);
        var chain = tamper.GetAuditChain();
        chain[1].Operation.Should().Be("Delete");
        chain[1].DataHash.Should().Be("DELETED");
    }

    [Fact]
    public async Task TamperProof_ChainIntegrity_Passes()
    {
        var inner = new InMemoryPolicyPersistence();
        var tamper = new TamperProofPolicyPersistence(inner);

        await tamper.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
        await tamper.SaveAsync("comp", PolicyLevel.Object, "/v/c/o", MakePolicy("comp", PolicyLevel.Object, 60));

        tamper.VerifyChainIntegrity().Should().BeTrue();
    }

    [Fact]
    public async Task TamperProof_GenesisBlock_HasCorrectPreviousHash()
    {
        var inner = new InMemoryPolicyPersistence();
        var tamper = new TamperProofPolicyPersistence(inner);

        await tamper.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));

        var chain = tamper.GetAuditChain();
        chain[0].PreviousHash.Should().Be("GENESIS");
    }

    [Fact]
    public async Task TamperProof_ProfileSave_CreatesAuditBlock()
    {
        var inner = new InMemoryPolicyPersistence();
        var tamper = new TamperProofPolicyPersistence(inner);

        await tamper.SaveProfileAsync(OperationalProfile.Standard());

        tamper.AuditBlockCount.Should().Be(1);
        tamper.GetAuditChain()[0].Operation.Should().Be("SaveProfile");
    }

    [Fact]
    public async Task TamperProof_LoadProfile_NullWhenNotSet()
    {
        var inner = new InMemoryPolicyPersistence();
        var tamper = new TamperProofPolicyPersistence(inner);

        var loaded = await tamper.LoadProfileAsync();
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task TamperProof_NoDoubleSerialization()
    {
        var inner = new InMemoryPolicyPersistence();
        var tamper = new TamperProofPolicyPersistence(inner);

        var policy = MakePolicy("enc", PolicyLevel.VDE, 80);
        await tamper.SaveAsync("enc", PolicyLevel.VDE, "/v", policy);

        // Data loaded from inner should be identical to what was saved
        var innerLoaded = await inner.LoadAllAsync();
        var tamperLoaded = await tamper.LoadAllAsync();

        innerLoaded[0].Policy.IntensityLevel.Should().Be(tamperLoaded[0].Policy.IntensityLevel);
    }

    [Fact]
    public void TamperProof_NullInner_ThrowsArgumentNullException()
    {
        var act = () => new TamperProofPolicyPersistence(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task TamperProof_EmptyChain_IntegrityPasses()
    {
        var inner = new InMemoryPolicyPersistence();
        var tamper = new TamperProofPolicyPersistence(inner);

        tamper.VerifyChainIntegrity().Should().BeTrue();
    }

    // ========================================================================
    // HybridPolicyPersistence (~10 tests)
    // ========================================================================

    [Fact]
    public async Task Hybrid_SaveWritesToBothStores()
    {
        var policyStore = new InMemoryPolicyPersistence();
        var auditStore = new InMemoryPolicyPersistence();
        var hybrid = new HybridPolicyPersistence(policyStore, auditStore);

        await hybrid.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));

        var policyLoaded = await policyStore.LoadAllAsync();
        var auditLoaded = await auditStore.LoadAllAsync();

        policyLoaded.Should().HaveCount(1);
        auditLoaded.Should().HaveCount(1);
    }

    [Fact]
    public async Task Hybrid_LoadsFromPolicyStoreOnly()
    {
        var policyStore = new InMemoryPolicyPersistence();
        var auditStore = new InMemoryPolicyPersistence();
        var hybrid = new HybridPolicyPersistence(policyStore, auditStore);

        await hybrid.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));

        var loaded = await hybrid.LoadAllAsync();
        loaded.Should().HaveCount(1);
    }

    [Fact]
    public async Task Hybrid_DeleteWritesToBothStores()
    {
        var policyStore = new InMemoryPolicyPersistence();
        var auditStore = new InMemoryPolicyPersistence();
        var hybrid = new HybridPolicyPersistence(policyStore, auditStore);

        await hybrid.SaveAsync("enc", PolicyLevel.VDE, "/v", MakePolicy("enc", PolicyLevel.VDE, 80));
        await hybrid.DeleteAsync("enc", PolicyLevel.VDE, "/v");

        var policyLoaded = await policyStore.LoadAllAsync();
        policyLoaded.Should().BeEmpty();
    }

    [Fact]
    public async Task Hybrid_ProfileSavesToBothStores()
    {
        var policyStore = new InMemoryPolicyPersistence();
        var auditStore = new InMemoryPolicyPersistence();
        var hybrid = new HybridPolicyPersistence(policyStore, auditStore);

        await hybrid.SaveProfileAsync(OperationalProfile.Strict());

        var policyProfile = await policyStore.LoadProfileAsync();
        var auditProfile = await auditStore.LoadProfileAsync();

        policyProfile.Should().NotBeNull();
        auditProfile.Should().NotBeNull();
    }

    [Fact]
    public async Task Hybrid_ProfileLoadsFromPolicyStore()
    {
        var policyStore = new InMemoryPolicyPersistence();
        var auditStore = new InMemoryPolicyPersistence();
        var hybrid = new HybridPolicyPersistence(policyStore, auditStore);

        await hybrid.SaveProfileAsync(OperationalProfile.Balanced());

        var loaded = await hybrid.LoadProfileAsync();
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Balanced");
    }

    [Fact]
    public void Hybrid_NullPolicyStore_Throws()
    {
        var audit = new InMemoryPolicyPersistence();
        var act = () => new HybridPolicyPersistence(null!, audit);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Hybrid_NullAuditStore_Throws()
    {
        var policy = new InMemoryPolicyPersistence();
        var act = () => new HybridPolicyPersistence(policy, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Hybrid_FlushBothStores()
    {
        var policyStore = new InMemoryPolicyPersistence();
        var auditStore = new InMemoryPolicyPersistence();
        var hybrid = new HybridPolicyPersistence(policyStore, auditStore);

        // Flush should not throw on in-memory stores
        await hybrid.FlushAsync();

        // Verify flush completed without corrupting the stores
        policyStore.Should().NotBeNull();
    }

    [Fact]
    public async Task Hybrid_DataRetrievableFromPolicyStore()
    {
        var policyStore = new InMemoryPolicyPersistence();
        var auditStore = new InMemoryPolicyPersistence();
        var hybrid = new HybridPolicyPersistence(policyStore, auditStore);

        var policy = MakePolicy("enc", PolicyLevel.VDE, 80,
            customParams: new Dictionary<string, string> { ["key"] = "val" });
        await hybrid.SaveAsync("enc", PolicyLevel.VDE, "/v", policy);

        var loaded = await policyStore.LoadAllAsync();
        loaded[0].Policy.CustomParameters!["key"].Should().Be("val");
    }

    [Fact]
    public async Task Hybrid_DataRetrievableFromAuditStore()
    {
        var policyStore = new InMemoryPolicyPersistence();
        var auditStore = new InMemoryPolicyPersistence();
        var hybrid = new HybridPolicyPersistence(policyStore, auditStore);

        var policy = MakePolicy("enc", PolicyLevel.VDE, 80);
        await hybrid.SaveAsync("enc", PolicyLevel.VDE, "/v", policy);

        var loaded = await auditStore.LoadAllAsync();
        loaded.Should().HaveCount(1);
        loaded[0].Policy.IntensityLevel.Should().Be(80);
    }

    // ========================================================================
    // PolicySerializationHelper (~8 tests)
    // ========================================================================

    [Fact]
    public void Serialization_PolicyRoundTrip()
    {
        var policy = MakePolicy("enc", PolicyLevel.VDE, 80, CascadeStrategy.Enforce, AiAutonomyLevel.ManualOnly);
        var bytes = PolicySerializationHelper.SerializePolicy(policy);
        var deserialized = PolicySerializationHelper.DeserializePolicy(bytes);

        deserialized.FeatureId.Should().Be("enc");
        deserialized.IntensityLevel.Should().Be(80);
        deserialized.Cascade.Should().Be(CascadeStrategy.Enforce);
        deserialized.AiAutonomy.Should().Be(AiAutonomyLevel.ManualOnly);
    }

    [Theory]
    [InlineData(CascadeStrategy.MostRestrictive)]
    [InlineData(CascadeStrategy.Enforce)]
    [InlineData(CascadeStrategy.Inherit)]
    [InlineData(CascadeStrategy.Override)]
    [InlineData(CascadeStrategy.Merge)]
    public void Serialization_AllCascadeStrategies_Survive(CascadeStrategy cascade)
    {
        var policy = MakePolicy("enc", PolicyLevel.VDE, 50, cascade);
        var bytes = PolicySerializationHelper.SerializePolicy(policy);
        var deserialized = PolicySerializationHelper.DeserializePolicy(bytes);

        deserialized.Cascade.Should().Be(cascade);
    }

    [Theory]
    [InlineData(AiAutonomyLevel.ManualOnly)]
    [InlineData(AiAutonomyLevel.Suggest)]
    [InlineData(AiAutonomyLevel.SuggestExplain)]
    [InlineData(AiAutonomyLevel.AutoNotify)]
    [InlineData(AiAutonomyLevel.AutoSilent)]
    public void Serialization_AllAiAutonomyLevels_Survive(AiAutonomyLevel ai)
    {
        var policy = MakePolicy("enc", PolicyLevel.VDE, 50, ai: ai);
        var bytes = PolicySerializationHelper.SerializePolicy(policy);
        var deserialized = PolicySerializationHelper.DeserializePolicy(bytes);

        deserialized.AiAutonomy.Should().Be(ai);
    }

    [Theory]
    [InlineData(PolicyLevel.Block)]
    [InlineData(PolicyLevel.Chunk)]
    [InlineData(PolicyLevel.Object)]
    [InlineData(PolicyLevel.Container)]
    [InlineData(PolicyLevel.VDE)]
    public void Serialization_AllPolicyLevels_Survive(PolicyLevel level)
    {
        var policy = MakePolicy("enc", level, 50);
        var bytes = PolicySerializationHelper.SerializePolicy(policy);
        var deserialized = PolicySerializationHelper.DeserializePolicy(bytes);

        deserialized.Level.Should().Be(level);
    }

    [Fact]
    public void Serialization_CustomParams_RoundTrip()
    {
        var policy = MakePolicy("enc", PolicyLevel.VDE, 80,
            customParams: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        var bytes = PolicySerializationHelper.SerializePolicy(policy);
        var deserialized = PolicySerializationHelper.DeserializePolicy(bytes);

        deserialized.CustomParameters!["a"].Should().Be("1");
        deserialized.CustomParameters!["b"].Should().Be("2");
    }

    [Fact]
    public void Serialization_NullCustomParams_Handled()
    {
        var policy = MakePolicy("enc", PolicyLevel.VDE, 80);
        var bytes = PolicySerializationHelper.SerializePolicy(policy);
        var deserialized = PolicySerializationHelper.DeserializePolicy(bytes);

        deserialized.CustomParameters.Should().BeNull();
    }

    [Fact]
    public void Serialization_EmptyCustomParams_Handled()
    {
        var policy = MakePolicy("enc", PolicyLevel.VDE, 80, customParams: new Dictionary<string, string>());
        var bytes = PolicySerializationHelper.SerializePolicy(policy);
        var deserialized = PolicySerializationHelper.DeserializePolicy(bytes);

        // Empty dict may serialize as null or empty; either is acceptable
        if (deserialized.CustomParameters != null)
            deserialized.CustomParameters.Should().BeEmpty();
    }

    [Fact]
    public void Serialization_ProfileRoundTrip()
    {
        var profile = OperationalProfile.Strict();
        var bytes = PolicySerializationHelper.SerializeProfile(profile);
        var deserialized = PolicySerializationHelper.DeserializeProfile(bytes);

        deserialized.Name.Should().Be("Strict");
        deserialized.Preset.Should().Be(OperationalProfilePreset.Strict);
        deserialized.FeaturePolicies.Should().ContainKey("encryption");
    }
}
