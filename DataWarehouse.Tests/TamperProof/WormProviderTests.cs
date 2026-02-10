// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.TamperProof;

/// <summary>
/// Unit tests for TamperProof WORM (Write-Once-Read-Many) storage SDK contracts (T6.3).
/// Validates WormEnforcementMode enum, WormConfiguration construction, write-once semantics,
/// retention period validation, S3 Object Lock, and Azure Immutable Blob configurations.
/// </summary>
public class WormProviderTests
{
    #region WormEnforcementMode Enum

    [Fact]
    public void WormEnforcementMode_ShouldHaveExactlyThreeValues()
    {
        var values = Enum.GetValues<WormEnforcementMode>();
        values.Should().HaveCount(3, "WORM supports Software, HardwareIntegrated, Hybrid modes");
    }

    [Fact]
    public void WormEnforcementMode_ShouldContainAllExpectedValues()
    {
        var values = Enum.GetValues<WormEnforcementMode>();
        values.Should().Contain(WormEnforcementMode.Software);
        values.Should().Contain(WormEnforcementMode.HardwareIntegrated);
        values.Should().Contain(WormEnforcementMode.Hybrid);
    }

    [Theory]
    [InlineData(WormEnforcementMode.Software, "Software")]
    [InlineData(WormEnforcementMode.HardwareIntegrated, "HardwareIntegrated")]
    [InlineData(WormEnforcementMode.Hybrid, "Hybrid")]
    public void WormEnforcementMode_ShouldHaveCorrectNames(WormEnforcementMode mode, string expectedName)
    {
        mode.ToString().Should().Be(expectedName);
    }

    #endregion

    #region WormConfiguration (via TamperProofConfiguration)

    [Fact]
    public void TamperProofConfiguration_ShouldConstructWithDefaultWormSettings()
    {
        var config = CreateTestConfig();

        config.WormMode.Should().Be(WormEnforcementMode.Software, "default WORM mode is Software");
        config.DefaultRetentionPeriod.Should().Be(TimeSpan.FromDays(7 * 365), "default retention is 7 years");
    }

    [Fact]
    public void TamperProofConfiguration_ShouldAcceptCustomRetentionPeriod()
    {
        var config = CreateTestConfig(retentionPeriod: TimeSpan.FromDays(365));

        config.DefaultRetentionPeriod.Should().Be(TimeSpan.FromDays(365));
    }

    [Fact]
    public void TamperProofConfiguration_ShouldAcceptHardwareEnforcementMode()
    {
        var config = CreateTestConfig(wormMode: WormEnforcementMode.HardwareIntegrated);

        config.WormMode.Should().Be(WormEnforcementMode.HardwareIntegrated);
    }

    [Fact]
    public void TamperProofConfiguration_ShouldAcceptHybridEnforcementMode()
    {
        var config = CreateTestConfig(wormMode: WormEnforcementMode.Hybrid);

        config.WormMode.Should().Be(WormEnforcementMode.Hybrid);
    }

    #endregion

    #region Write-Once Semantics (WormReference Immutability)

    [Fact]
    public void WormReference_ShouldBeConstructedWithImmutableProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var wormRef = new WormReference
        {
            StorageLocation = "s3://worm-bucket/objects/obj-001",
            ContentHash = "AABBCCDD",
            ContentSize = 4096,
            WrittenAt = now,
            RetentionExpiresAt = now.AddYears(7),
            EnforcementMode = WormEnforcementMode.HardwareIntegrated
        };

        wormRef.StorageLocation.Should().Be("s3://worm-bucket/objects/obj-001");
        wormRef.ContentHash.Should().Be("AABBCCDD");
        wormRef.ContentSize.Should().Be(4096);
        wormRef.EnforcementMode.Should().Be(WormEnforcementMode.HardwareIntegrated);
        wormRef.RetentionExpiresAt.Should().BeAfter(now);
    }

    [Fact]
    public void WormReference_ShouldValidateSuccessfully()
    {
        var wormRef = new WormReference
        {
            StorageLocation = "s3://bucket/key",
            ContentHash = "AABB",
            ContentSize = 100,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.Software
        };

        var errors = wormRef.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void WormReference_ShouldFailValidationWithoutStorageLocation()
    {
        var wormRef = new WormReference
        {
            StorageLocation = "",
            ContentHash = "AABB",
            ContentSize = 100,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.Software
        };

        var errors = wormRef.Validate();
        errors.Should().Contain(e => e.Contains("StorageLocation"));
    }

    [Fact]
    public void WormReference_ShouldCloneCorrectly()
    {
        var original = new WormReference
        {
            StorageLocation = "s3://bucket/key",
            ContentHash = "AABB",
            ContentSize = 100,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.HardwareIntegrated,
            ProviderMetadata = new Dictionary<string, object> { ["LockId"] = "lock-123" }
        };

        var clone = original.Clone();

        clone.StorageLocation.Should().Be(original.StorageLocation);
        clone.ContentHash.Should().Be(original.ContentHash);
        clone.EnforcementMode.Should().Be(original.EnforcementMode);
        clone.ProviderMetadata.Should().ContainKey("LockId");
    }

    #endregion

    #region Retention Period Validation

    [Fact]
    public void TamperProofConfiguration_ShouldFailValidationWithNegativeRetention()
    {
        var config = CreateTestConfig(retentionPeriod: TimeSpan.FromDays(-1));

        var errors = config.Validate();
        errors.Should().Contain(e => e.Contains("DefaultRetentionPeriod"));
    }

    [Fact]
    public void TamperProofConfiguration_ShouldFailValidationWithZeroRetention()
    {
        var config = CreateTestConfig(retentionPeriod: TimeSpan.Zero);

        var errors = config.Validate();
        errors.Should().Contain(e => e.Contains("DefaultRetentionPeriod"));
    }

    [Fact]
    public void WormRetentionPolicy_Standard_ShouldCreateWithSpecifiedPeriod()
    {
        var policy = WormRetentionPolicy.Standard(TimeSpan.FromDays(365));

        policy.RetentionPeriod.Should().Be(TimeSpan.FromDays(365));
        policy.HasLegalHold.Should().BeFalse();
        policy.LegalHoldIds.Should().BeEmpty();
    }

    [Fact]
    public void WormRetentionPolicy_WithLegalHold_ShouldIncludeHoldInfo()
    {
        var policy = WormRetentionPolicy.WithLegalHold(TimeSpan.FromDays(365 * 10), "HOLD-001");

        policy.RetentionPeriod.Should().Be(TimeSpan.FromDays(365 * 10));
        policy.HasLegalHold.Should().BeTrue();
        policy.LegalHoldIds.Should().Contain("HOLD-001");
    }

    #endregion

    #region S3 Object Lock Configuration (Governance vs Compliance)

    [Fact]
    public void S3ObjectLock_GovernanceMode_ShouldBeRepresentedViaSoftwareEnforcement()
    {
        // S3 Governance mode = admin can bypass, mapped to Software enforcement
        var wormRef = new WormReference
        {
            StorageLocation = "s3://compliance-bucket/gov/obj-001",
            ContentHash = "AABBCCDD",
            ContentSize = 4096,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.Software,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["S3ObjectLockMode"] = "GOVERNANCE",
                ["S3RetainUntilDate"] = DateTimeOffset.UtcNow.AddYears(1).ToString("O")
            }
        };

        wormRef.EnforcementMode.Should().Be(WormEnforcementMode.Software);
        wormRef.ProviderMetadata.Should().ContainKey("S3ObjectLockMode");
        wormRef.ProviderMetadata!["S3ObjectLockMode"].Should().Be("GOVERNANCE");
    }

    [Fact]
    public void S3ObjectLock_ComplianceMode_ShouldBeRepresentedViaHardwareEnforcement()
    {
        // S3 Compliance mode = nobody can bypass, mapped to HardwareIntegrated
        var wormRef = new WormReference
        {
            StorageLocation = "s3://compliance-bucket/comp/obj-001",
            ContentHash = "AABBCCDD",
            ContentSize = 4096,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.HardwareIntegrated,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["S3ObjectLockMode"] = "COMPLIANCE",
                ["S3RetainUntilDate"] = DateTimeOffset.UtcNow.AddYears(7).ToString("O")
            }
        };

        wormRef.EnforcementMode.Should().Be(WormEnforcementMode.HardwareIntegrated);
        wormRef.ProviderMetadata!["S3ObjectLockMode"].Should().Be("COMPLIANCE");
    }

    #endregion

    #region Azure Immutable Blob Configuration (Unlocked vs Locked)

    [Fact]
    public void AzureImmutableBlob_UnlockedPolicy_ShouldBeRepresentedViaSoftwareEnforcement()
    {
        // Azure unlocked = can be deleted by admin, mapped to Software
        var wormRef = new WormReference
        {
            StorageLocation = "https://storageaccount.blob.core.windows.net/immutable/obj-001",
            ContentHash = "AABBCCDD",
            ContentSize = 4096,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.Software,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["AzurePolicyType"] = "TimeBasedRetention",
                ["AzurePolicyState"] = "Unlocked",
                ["AzureRetentionDays"] = 365
            }
        };

        wormRef.ProviderMetadata!["AzurePolicyState"].Should().Be("Unlocked");
        wormRef.EnforcementMode.Should().Be(WormEnforcementMode.Software);
    }

    [Fact]
    public void AzureImmutableBlob_LockedPolicy_ShouldBeRepresentedViaHardwareEnforcement()
    {
        // Azure locked = cannot be deleted even by admin, mapped to HardwareIntegrated
        var wormRef = new WormReference
        {
            StorageLocation = "https://storageaccount.blob.core.windows.net/immutable/obj-002",
            ContentHash = "AABBCCDD",
            ContentSize = 4096,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.HardwareIntegrated,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["AzurePolicyType"] = "TimeBasedRetention",
                ["AzurePolicyState"] = "Locked",
                ["AzureRetentionDays"] = 365 * 7
            }
        };

        wormRef.ProviderMetadata!["AzurePolicyState"].Should().Be("Locked");
        wormRef.EnforcementMode.Should().Be(WormEnforcementMode.HardwareIntegrated);
    }

    [Fact]
    public void AzureImmutableBlob_LegalHold_ShouldBeTrackedInMetadata()
    {
        var wormRef = new WormReference
        {
            StorageLocation = "https://storageaccount.blob.core.windows.net/immutable/obj-003",
            ContentHash = "EEFFAABB",
            ContentSize = 2048,
            WrittenAt = DateTimeOffset.UtcNow,
            EnforcementMode = WormEnforcementMode.HardwareIntegrated,
            ProviderMetadata = new Dictionary<string, object>
            {
                ["AzurePolicyType"] = "LegalHold",
                ["AzureLegalHoldTags"] = "Investigation-2024,GDPR-Hold"
            }
        };

        wormRef.ProviderMetadata!["AzurePolicyType"].Should().Be("LegalHold");
        ((string)wormRef.ProviderMetadata["AzureLegalHoldTags"]).Should().Contain("Investigation-2024");
    }

    #endregion

    #region OrphanedWormStatus Enum

    [Fact]
    public void OrphanedWormStatus_ShouldContainAllExpectedValues()
    {
        var values = Enum.GetValues<OrphanedWormStatus>();
        values.Should().Contain(OrphanedWormStatus.TransactionFailed);
        values.Should().Contain(OrphanedWormStatus.PendingExpiry);
        values.Should().Contain(OrphanedWormStatus.Expired);
        values.Should().Contain(OrphanedWormStatus.Reviewed);
        values.Should().Contain(OrphanedWormStatus.Purged);
        values.Should().Contain(OrphanedWormStatus.LinkedToRetry);
    }

    #endregion

    #region Helpers

    private static TamperProofConfiguration CreateTestConfig(
        WormEnforcementMode wormMode = WormEnforcementMode.Software,
        TimeSpan? retentionPeriod = null)
    {
        return new TamperProofConfiguration
        {
            StorageInstances = new StorageInstancesConfig
            {
                Data = new StorageInstanceConfig { InstanceId = "data", PluginId = "local" },
                Metadata = new StorageInstanceConfig { InstanceId = "metadata", PluginId = "local" },
                Worm = new StorageInstanceConfig { InstanceId = "worm", PluginId = "local" },
                Blockchain = new StorageInstanceConfig { InstanceId = "blockchain", PluginId = "local" }
            },
            Raid = new RaidConfig { DataShards = 4, ParityShards = 2 },
            WormMode = wormMode,
            DefaultRetentionPeriod = retentionPeriod ?? TimeSpan.FromDays(7 * 365)
        };
    }

    #endregion
}
