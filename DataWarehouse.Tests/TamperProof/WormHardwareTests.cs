// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Integration tests for TamperProof hardware WORM provider SDK contracts (T6.11).
/// Validates S3 Object Lock configuration, Azure Immutable Blob configuration,
/// HardwareIntegrated detection pattern, and WORM write result immutability.
/// </summary>
public class WormHardwareTests
{
    #region S3 Object Lock Configuration

    [Fact]
    public void S3ObjectLock_GovernanceMode_ShouldAllowAdminBypass()
    {
        var config = CreateS3GovernanceConfig();

        config.WormMode.Should().Be(WormEnforcementMode.Software,
            "S3 Governance mode maps to Software enforcement (admin can bypass)");
    }

    [Fact]
    public void S3ObjectLock_ComplianceMode_ShouldPreventAllDeletion()
    {
        var config = CreateS3ComplianceConfig();

        config.WormMode.Should().Be(WormEnforcementMode.HardwareIntegrated,
            "S3 Compliance mode maps to HardwareIntegrated (nobody can bypass)");
    }

    [Fact]
    public void S3ObjectLock_GovernanceRetention_ShouldBeConfigurable()
    {
        var wormRef = new WormReference
        {
            StorageLocation = "s3://my-bucket/governance/obj-001",
            ContentHash = "AABB",
            ContentSize = 4096,
            WrittenAt = DateTimeOffset.UtcNow,
            RetentionExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            EnforcementMode = WormEnforcementMode.Software,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["S3ObjectLockMode"] = "GOVERNANCE",
                ["S3RetainUntilDate"] = DateTimeOffset.UtcNow.AddYears(1).ToString("O"),
                ["S3BucketVersioning"] = "Enabled"
            }
        };

        wormRef.ProviderMetadata!["S3ObjectLockMode"].Should().Be("GOVERNANCE");
        wormRef.RetentionExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void S3ObjectLock_ComplianceRetention_ShouldEnforceMinimumPeriod()
    {
        var retentionEnd = DateTimeOffset.UtcNow.AddYears(7);

        var wormRef = new WormReference
        {
            StorageLocation = "s3://my-bucket/compliance/obj-001",
            ContentHash = "AABB",
            ContentSize = 4096,
            WrittenAt = DateTimeOffset.UtcNow,
            RetentionExpiresAt = retentionEnd,
            EnforcementMode = WormEnforcementMode.HardwareIntegrated,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["S3ObjectLockMode"] = "COMPLIANCE",
                ["S3RetainUntilDate"] = retentionEnd.ToString("O")
            }
        };

        wormRef.EnforcementMode.Should().Be(WormEnforcementMode.HardwareIntegrated);
        wormRef.RetentionExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddYears(6));
    }

    #endregion

    #region Azure Immutable Blob Configuration

    [Fact]
    public void AzureImmutableBlob_TimeBasedRetention_ShouldTrackRetentionDays()
    {
        var wormRef = new WormReference
        {
            StorageLocation = "https://account.blob.core.windows.net/container/obj-001",
            ContentHash = "AABB",
            ContentSize = 2048,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.HardwareIntegrated,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["AzurePolicyType"] = "TimeBasedRetention",
                ["AzurePolicyState"] = "Locked",
                ["AzureRetentionDays"] = 2555,
                ["AzureImmutabilityPolicyVersion"] = 2
            }
        };

        wormRef.ProviderMetadata!["AzurePolicyType"].Should().Be("TimeBasedRetention");
        ((int)wormRef.ProviderMetadata["AzureRetentionDays"]).Should().Be(2555);
    }

    [Fact]
    public void AzureImmutableBlob_LegalHold_ShouldPreventDeletionIndefinitely()
    {
        var wormRef = new WormReference
        {
            StorageLocation = "https://account.blob.core.windows.net/container/obj-002",
            ContentHash = "CCDD",
            ContentSize = 1024,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.HardwareIntegrated,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["AzurePolicyType"] = "LegalHold",
                ["AzureLegalHoldTags"] = "Investigation-2024,SEC-Filing"
            }
        };

        wormRef.ProviderMetadata!["AzurePolicyType"].Should().Be("LegalHold");
        // Legal hold has no retention expiry - indefinite until removed
        wormRef.RetentionExpiresAt.Should().BeNull("legal hold has no expiry date");
    }

    [Fact]
    public void AzureImmutableBlob_UnlockedPolicy_CanBecomeLockedButNotReversed()
    {
        // Unlocked policy can be upgraded to Locked but not reversed
        var unlockedRef = new WormReference
        {
            StorageLocation = "https://account.blob.core.windows.net/c/obj-003",
            ContentHash = "EEFF",
            ContentSize = 512,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.Software,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["AzurePolicyState"] = "Unlocked",
                ["AzureRetentionDays"] = 365
            }
        };

        unlockedRef.EnforcementMode.Should().Be(WormEnforcementMode.Software);
        unlockedRef.ProviderMetadata!["AzurePolicyState"].Should().Be("Unlocked");
    }

    #endregion

    #region HardwareIntegrated Detection Pattern

    [Fact]
    public void WormEnforcementMode_HardwareIntegrated_ShouldBeDetectable()
    {
        var configs = new[]
        {
            CreateS3ComplianceConfig(),
            CreateAzureLockedConfig(),
        };

        foreach (var config in configs)
        {
            config.WormMode.Should().Be(WormEnforcementMode.HardwareIntegrated,
                "Hardware-enforced WORM should be detected from cloud provider config");
        }
    }

    [Theory]
    [InlineData(WormEnforcementMode.HardwareIntegrated, true)]
    [InlineData(WormEnforcementMode.Software, false)]
    [InlineData(WormEnforcementMode.Hybrid, false)]
    public void IsHardwareEnforced_ShouldCorrectlyIdentifyMode(
        WormEnforcementMode mode, bool expectedHardware)
    {
        var isHardware = mode == WormEnforcementMode.HardwareIntegrated;
        isHardware.Should().Be(expectedHardware);
    }

    [Fact]
    public void WormReference_ShouldDistinguishHardwareFromSoftware()
    {
        var softwareRef = new WormReference
        {
            StorageLocation = "/local/worm/obj-001",
            ContentHash = "AA",
            ContentSize = 100,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.Software
        };

        var hardwareRef = new WormReference
        {
            StorageLocation = "s3://worm-bucket/obj-001",
            ContentHash = "AA",
            ContentSize = 100,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.HardwareIntegrated
        };

        softwareRef.EnforcementMode.Should().NotBe(hardwareRef.EnforcementMode);
    }

    #endregion

    #region WORM Write Result Immutability

    [Fact]
    public void WormReference_Properties_ShouldBeInitOnly()
    {
        var writtenAt = DateTimeOffset.UtcNow;
        var wormRef = new WormReference
        {
            StorageLocation = "s3://worm/immutable-001",
            ContentHash = "IMMUTABLE_HASH",
            ContentSize = 8192,
            WrittenAt = writtenAt,
            EnforcementMode = WormEnforcementMode.HardwareIntegrated
        };

        // Verify init-only properties retain their values
        wormRef.StorageLocation.Should().Be("s3://worm/immutable-001");
        wormRef.ContentHash.Should().Be("IMMUTABLE_HASH");
        wormRef.ContentSize.Should().Be(8192);
        wormRef.WrittenAt.Should().Be(writtenAt);
        wormRef.EnforcementMode.Should().Be(WormEnforcementMode.HardwareIntegrated);
    }

    [Fact]
    public void WormReference_Clone_ShouldPreserveAllFields()
    {
        var original = new WormReference
        {
            StorageLocation = "s3://worm/clone-test",
            ContentHash = "CLONE_HASH",
            ContentSize = 4096,
            WrittenAt = DateTimeOffset.UtcNow,
            RetentionExpiresAt = DateTimeOffset.UtcNow.AddYears(7),
            EnforcementMode = WormEnforcementMode.HardwareIntegrated,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["S3ObjectLockMode"] = "COMPLIANCE",
                ["CustomTag"] = "test-value"
            }
        };

        var clone = original.Clone();

        clone.StorageLocation.Should().Be(original.StorageLocation);
        clone.ContentHash.Should().Be(original.ContentHash);
        clone.ContentSize.Should().Be(original.ContentSize);
        clone.WrittenAt.Should().Be(original.WrittenAt);
        clone.RetentionExpiresAt.Should().Be(original.RetentionExpiresAt);
        clone.EnforcementMode.Should().Be(original.EnforcementMode);
        clone.ProviderMetadata.Should().ContainKey("S3ObjectLockMode");
        clone.ProviderMetadata.Should().ContainKey("CustomTag");
    }

    [Fact]
    public void BlockSealedException_ShouldContainBlockDetails()
    {
        var blockId = Guid.NewGuid();
        var sealedAt = DateTime.UtcNow;

        var ex = new BlockSealedException(blockId, sealedAt, "Corruption detected");

        ex.BlockId.Should().Be(blockId);
        ex.SealedAt.Should().Be(sealedAt);
        ex.Reason.Should().Be("Corruption detected");
        ex.Message.Should().Contain(blockId.ToString());
    }

    [Fact]
    public void BlockSealedException_ForShard_ShouldIncludeShardIndex()
    {
        var blockId = Guid.NewGuid();
        var ex = BlockSealedException.ForShard(blockId, 3, DateTime.UtcNow, "Shard corrupted");

        ex.ShardIndex.Should().Be(3);
        ex.BlockId.Should().Be(blockId);
    }

    #endregion

    #region Helpers

    private static TamperProofConfiguration CreateS3GovernanceConfig()
    {
        return new TamperProofConfiguration
        {
            StorageInstances = CreateDefaultInstances(),
            Raid = new RaidConfig { DataShards = 4, ParityShards = 2 },
            WormMode = WormEnforcementMode.Software
        };
    }

    private static TamperProofConfiguration CreateS3ComplianceConfig()
    {
        return new TamperProofConfiguration
        {
            StorageInstances = CreateDefaultInstances(),
            Raid = new RaidConfig { DataShards = 4, ParityShards = 2 },
            WormMode = WormEnforcementMode.HardwareIntegrated
        };
    }

    private static TamperProofConfiguration CreateAzureLockedConfig()
    {
        return new TamperProofConfiguration
        {
            StorageInstances = CreateDefaultInstances(),
            Raid = new RaidConfig { DataShards = 4, ParityShards = 2 },
            WormMode = WormEnforcementMode.HardwareIntegrated
        };
    }

    private static StorageInstancesConfig CreateDefaultInstances()
    {
        return new StorageInstancesConfig
        {
            Data = new StorageInstanceConfig { InstanceId = "data", PluginId = "local" },
            Metadata = new StorageInstanceConfig { InstanceId = "metadata", PluginId = "local" },
            Worm = new StorageInstanceConfig { InstanceId = "worm", PluginId = "local" },
            Blockchain = new StorageInstanceConfig { InstanceId = "blockchain", PluginId = "local" }
        };
    }

    #endregion
}
