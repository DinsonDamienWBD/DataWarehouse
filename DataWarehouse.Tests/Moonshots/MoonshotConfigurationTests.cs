using System.Collections.ObjectModel;
using DataWarehouse.SDK.Moonshots;
using Xunit;

namespace DataWarehouse.Tests.Moonshots;

/// <summary>
/// Tests for moonshot configuration defaults, hierarchy merge logic,
/// override policy enforcement, and configuration validation.
/// </summary>
public sealed class MoonshotConfigurationTests
{
    [Fact]
    public void ProductionDefaults_AllMoonshotsEnabled()
    {
        // Act
        var config = MoonshotConfigurationDefaults.CreateProductionDefaults();

        // Assert: all 10 moonshots are present and enabled
        Assert.Equal(10, config.Moonshots.Count);
        foreach (var id in Enum.GetValues<MoonshotId>())
        {
            Assert.True(config.IsEnabled(id), $"Moonshot {id} should be enabled in production defaults");
        }
    }

    [Fact]
    public void MergeConfig_LockedMoonshot_ParentWins()
    {
        // Arrange: CompliancePassports is Locked + Enabled at Instance level
        var parent = MoonshotConfigurationDefaults.CreateProductionDefaults();
        Assert.Equal(MoonshotOverridePolicy.Locked, parent.Moonshots[MoonshotId.CompliancePassports].OverridePolicy);

        // Child (User level) tries to disable CompliancePassports
        var childMoonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>
        {
            [MoonshotId.CompliancePassports] = parent.Moonshots[MoonshotId.CompliancePassports] with
            {
                Enabled = false,
                DefinedAt = ConfigHierarchyLevel.User
            }
        };

        var child = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(childMoonshots),
            Level = ConfigHierarchyLevel.User,
            UserId = "user-1"
        };

        // Act
        var merged = parent.MergeWith(child);

        // Assert: Locked parent wins -- CompliancePassports still enabled
        Assert.True(merged.IsEnabled(MoonshotId.CompliancePassports),
            "Locked moonshot should not be overridden by user-level child");
    }

    [Fact]
    public void MergeConfig_UserOverridable_ChildWins()
    {
        // Arrange: UniversalTags is UserOverridable + Enabled
        var parent = MoonshotConfigurationDefaults.CreateProductionDefaults();
        Assert.Equal(MoonshotOverridePolicy.UserOverridable, parent.Moonshots[MoonshotId.UniversalTags].OverridePolicy);

        // Child (User level) disables UniversalTags
        var childMoonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>
        {
            [MoonshotId.UniversalTags] = parent.Moonshots[MoonshotId.UniversalTags] with
            {
                Enabled = false,
                DefinedAt = ConfigHierarchyLevel.User
            }
        };

        var child = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(childMoonshots),
            Level = ConfigHierarchyLevel.User,
            UserId = "user-1"
        };

        // Act
        var merged = parent.MergeWith(child);

        // Assert: UserOverridable allows user to disable
        Assert.False(merged.IsEnabled(MoonshotId.UniversalTags),
            "UserOverridable moonshot should allow user-level override to disable");
    }

    [Fact]
    public void MergeConfig_TenantOverridable_TenantCanOverride()
    {
        // Arrange: ChaosVaccination is TenantOverridable
        var parent = MoonshotConfigurationDefaults.CreateProductionDefaults();
        Assert.Equal(MoonshotOverridePolicy.TenantOverridable, parent.Moonshots[MoonshotId.ChaosVaccination].OverridePolicy);

        // Tenant-level child disables ChaosVaccination
        var childMoonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>
        {
            [MoonshotId.ChaosVaccination] = parent.Moonshots[MoonshotId.ChaosVaccination] with
            {
                Enabled = false,
                DefinedAt = ConfigHierarchyLevel.Tenant
            }
        };

        var child = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(childMoonshots),
            Level = ConfigHierarchyLevel.Tenant,
            TenantId = "tenant-1"
        };

        // Act
        var merged = parent.MergeWith(child);

        // Assert: Tenant can override TenantOverridable
        Assert.False(merged.IsEnabled(MoonshotId.ChaosVaccination),
            "TenantOverridable moonshot should allow tenant-level override to disable");
    }

    [Fact]
    public void MergeConfig_TenantOverridable_UserCannotOverride()
    {
        // Arrange: ChaosVaccination is TenantOverridable, Tenant leaves enabled
        var parent = MoonshotConfigurationDefaults.CreateProductionDefaults();

        // First merge: Tenant layer (leaves ChaosVaccination enabled -- no override)
        var tenantMoonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>();
        var tenantConfig = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(tenantMoonshots),
            Level = ConfigHierarchyLevel.Tenant,
            TenantId = "tenant-1"
        };

        var afterTenant = parent.MergeWith(tenantConfig);
        Assert.True(afterTenant.IsEnabled(MoonshotId.ChaosVaccination));

        // User-level child tries to disable ChaosVaccination
        var userMoonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>
        {
            [MoonshotId.ChaosVaccination] = afterTenant.Moonshots[MoonshotId.ChaosVaccination] with
            {
                Enabled = false,
                DefinedAt = ConfigHierarchyLevel.User
            }
        };

        var userConfig = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(userMoonshots),
            Level = ConfigHierarchyLevel.User,
            UserId = "user-1"
        };

        // Act
        var merged = afterTenant.MergeWith(userConfig);

        // Assert: User cannot override TenantOverridable (only Tenant can)
        Assert.True(merged.IsEnabled(MoonshotId.ChaosVaccination),
            "User should not be able to override TenantOverridable moonshot");
    }

    [Fact]
    public void Validator_DependencyViolation_ReportsError()
    {
        // Arrange: SovereigntyMesh enabled, CompliancePassports disabled (violates dependency)
        var moonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>();
        foreach (var kvp in MoonshotConfigurationDefaults.CreateProductionDefaults().Moonshots)
        {
            if (kvp.Key == MoonshotId.CompliancePassports)
                moonshots[kvp.Key] = kvp.Value with { Enabled = false };
            else
                moonshots[kvp.Key] = kvp.Value;
        }

        var config = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(moonshots),
            Level = ConfigHierarchyLevel.Instance
        };

        var validator = new MoonshotConfigurationValidator();

        // Act
        var result = validator.Validate(config);

        // Assert: SovereigntyMesh depends on CompliancePassports
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == MoonshotConfigurationValidator.CodeDepMissing &&
            e.AffectedMoonshot == MoonshotId.SovereigntyMesh);
    }

    [Fact]
    public void Validator_ValidConfig_NoErrors()
    {
        // Arrange: production defaults should be valid
        var config = MoonshotConfigurationDefaults.CreateProductionDefaults();
        var validator = new MoonshotConfigurationValidator();

        // Act
        var result = validator.Validate(config);

        // Assert
        Assert.True(result.IsValid, $"Production defaults should be valid, errors: {string.Join(", ", result.Errors.Select(e => e.Message))}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_InvalidBlastRadius_ReportsError()
    {
        // Arrange: ChaosVaccination with MaxBlastRadius = 200 (valid range 1-100)
        var productionConfig = MoonshotConfigurationDefaults.CreateProductionDefaults();
        var moonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>();

        foreach (var kvp in productionConfig.Moonshots)
        {
            if (kvp.Key == MoonshotId.ChaosVaccination)
            {
                var invalidSettings = new Dictionary<string, string>(kvp.Value.Settings)
                {
                    ["MaxBlastRadius"] = "200"
                };
                moonshots[kvp.Key] = kvp.Value with
                {
                    Settings = new ReadOnlyDictionary<string, string>(invalidSettings)
                };
            }
            else
            {
                moonshots[kvp.Key] = kvp.Value;
            }
        }

        var config = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(moonshots),
            Level = ConfigHierarchyLevel.Instance
        };

        var validator = new MoonshotConfigurationValidator();

        // Act
        var result = validator.Validate(config);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == MoonshotConfigurationValidator.CodeInvalidRange &&
            e.AffectedMoonshot == MoonshotId.ChaosVaccination);
    }
}
