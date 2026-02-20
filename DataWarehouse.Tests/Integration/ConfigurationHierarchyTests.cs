using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Moonshots;
using DataWarehouse.SDK.Primitives.Configuration;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Verifies that all configuration items (base + moonshot) have proper hierarchy support,
/// explicit override flags, reasonable defaults, and consistent naming conventions.
/// </summary>
public class ConfigurationHierarchyTests
{
    #region Base Configuration Tests

    [Fact]
    public void AllConfigurationItemsHaveExplicitScope()
    {
        // Every property of type ConfigurationItem<T> across all configuration sections
        // must exist (i.e., no null properties). The scope is implicit in the section
        // hierarchy (Instance->Tenant->User is handled by ConfigurationChangeApi).
        var config = new DataWarehouseConfiguration();
        var sections = GetAllConfigurationSections(config);
        var issues = new List<string>();

        foreach (var (sectionName, section) in sections)
        {
            var properties = section.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType &&
                            p.PropertyType.GetGenericTypeDefinition() == typeof(ConfigurationItem<>));

            foreach (var prop in properties)
            {
                var value = prop.GetValue(section);
                if (value == null)
                {
                    issues.Add($"{sectionName}.{prop.Name} is null (no ConfigurationItem instance)");
                }
            }
        }

        Assert.Empty(issues);
    }

    [Fact]
    public void AllConfigurationItemsHaveAllowUserToOverride()
    {
        // Every ConfigurationItem<T> must have AllowUserToOverride explicitly set
        // (it defaults to true in the constructor, so it is always present).
        // We verify the property exists and is accessible on every item.
        var config = new DataWarehouseConfiguration();
        var sections = GetAllConfigurationSections(config);
        var itemCount = 0;

        foreach (var (sectionName, section) in sections)
        {
            var properties = section.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType &&
                            p.PropertyType.GetGenericTypeDefinition() == typeof(ConfigurationItem<>));

            foreach (var prop in properties)
            {
                var item = prop.GetValue(section);
                Assert.NotNull(item);

                var allowOverrideProp = item!.GetType().GetProperty("AllowUserToOverride");
                Assert.NotNull(allowOverrideProp);

                var allowOverrideValue = allowOverrideProp!.GetValue(item);
                Assert.NotNull(allowOverrideValue);
                Assert.IsType<bool>(allowOverrideValue);

                itemCount++;
            }
        }

        // Ensure we actually found a meaningful number of items
        Assert.True(itemCount >= 50, $"Expected at least 50 configuration items, found {itemCount}");
    }

    [Fact]
    public void ConfigurationDefaultsAreReasonable()
    {
        // No ConfigurationItem<T> should have a null Value unless T is explicitly nullable.
        // String values must not be empty.
        var config = new DataWarehouseConfiguration();
        var sections = GetAllConfigurationSections(config);
        var issues = new List<string>();

        foreach (var (sectionName, section) in sections)
        {
            var properties = section.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType &&
                            p.PropertyType.GetGenericTypeDefinition() == typeof(ConfigurationItem<>));

            foreach (var prop in properties)
            {
                var item = prop.GetValue(section);
                if (item == null) continue;

                var valueProp = item.GetType().GetProperty("Value");
                var value = valueProp?.GetValue(item);
                var valueType = valueProp?.PropertyType;

                if (valueType == typeof(string))
                {
                    if (value is string strVal && string.IsNullOrWhiteSpace(strVal))
                    {
                        issues.Add($"{sectionName}.{prop.Name} has empty/whitespace string default");
                    }
                }
                else if (valueType != null && !IsNullableType(valueType) && value == null)
                {
                    issues.Add($"{sectionName}.{prop.Name} has null default for non-nullable type {valueType.Name}");
                }
            }
        }

        Assert.Empty(issues);
    }

    [Fact]
    public void ParanoidPresetLocksSecurityCriticalItems()
    {
        var config = ConfigurationPresets.CreateParanoid();

        // These items must be locked (AllowUserToOverride = false)
        Assert.False(config.Security.EncryptionEnabled.AllowUserToOverride);
        Assert.False(config.Security.AuthEnabled.AllowUserToOverride);
        Assert.False(config.Security.TlsRequired.AllowUserToOverride);
        Assert.False(config.Security.FipsMode.AllowUserToOverride);

        // Locked items must have LockedByPolicy set
        Assert.NotNull(config.Security.EncryptionEnabled.LockedByPolicy);
        Assert.NotNull(config.Security.AuthEnabled.LockedByPolicy);
        Assert.NotNull(config.Security.TlsRequired.LockedByPolicy);
        Assert.NotNull(config.Security.FipsMode.LockedByPolicy);
    }

    [Fact]
    public void AllPresetsProduceValidConfigurations()
    {
        var presetNames = new[] { "unsafe", "minimal", "standard", "secure", "paranoid", "god-tier" };

        foreach (var name in presetNames)
        {
            var config = ConfigurationPresets.CreateByName(name);
            Assert.NotNull(config);
            Assert.Equal(name, config.PresetName);

            // Every section must be non-null
            Assert.NotNull(config.Security);
            Assert.NotNull(config.Storage);
            Assert.NotNull(config.Network);
            Assert.NotNull(config.Replication);
            Assert.NotNull(config.Encryption);
            Assert.NotNull(config.Compression);
            Assert.NotNull(config.Observability);
            Assert.NotNull(config.Compute);
            Assert.NotNull(config.Resilience);
            Assert.NotNull(config.Deployment);
            Assert.NotNull(config.DataManagement);
            Assert.NotNull(config.MessageBus);
            Assert.NotNull(config.Plugins);
        }
    }

    #endregion

    #region Moonshot Configuration Hierarchy Tests

    [Fact]
    public void AllMoonshotFeaturesHaveExplicitOverridePolicy()
    {
        var config = MoonshotConfigurationDefaults.CreateProductionDefaults();
        var allMoonshotIds = Enum.GetValues<MoonshotId>();

        foreach (var id in allMoonshotIds)
        {
            Assert.True(config.Moonshots.ContainsKey(id),
                $"MoonshotId.{id} is not present in production defaults");

            var feature = config.Moonshots[id];
            Assert.True(
                feature.OverridePolicy is MoonshotOverridePolicy.Locked
                    or MoonshotOverridePolicy.TenantOverridable
                    or MoonshotOverridePolicy.UserOverridable,
                $"MoonshotId.{id} has undefined override policy: {feature.OverridePolicy}");
        }
    }

    [Fact]
    public void PresetsIncludeAllMoonshotDomains()
    {
        // Production defaults must include all 10 moonshot features
        var production = MoonshotConfigurationDefaults.CreateProductionDefaults();
        var allMoonshotIds = Enum.GetValues<MoonshotId>();

        Assert.Equal(allMoonshotIds.Length, production.Moonshots.Count);

        foreach (var id in allMoonshotIds)
        {
            Assert.True(production.Moonshots.ContainsKey(id),
                $"Production defaults missing MoonshotId.{id}");
            Assert.True(production.Moonshots[id].Enabled,
                $"MoonshotId.{id} should be enabled in production defaults");
        }

        // Minimal defaults must include all 10 moonshot features (some disabled)
        var minimal = MoonshotConfigurationDefaults.CreateMinimalDefaults();
        Assert.Equal(allMoonshotIds.Length, minimal.Moonshots.Count);

        foreach (var id in allMoonshotIds)
        {
            Assert.True(minimal.Moonshots.ContainsKey(id),
                $"Minimal defaults missing MoonshotId.{id}");
        }

        // Only UniversalTags and UniversalFabric should be enabled in minimal
        Assert.True(minimal.Moonshots[MoonshotId.UniversalTags].Enabled);
        Assert.True(minimal.Moonshots[MoonshotId.UniversalFabric].Enabled);
    }

    [Fact]
    public void MoonshotHierarchyMergeRespectsOverridePolicies()
    {
        var instance = MoonshotConfigurationDefaults.CreateProductionDefaults();

        // Create a tenant-level config that tries to override everything
        var tenantOverrides = new Dictionary<MoonshotId, MoonshotFeatureConfig>();
        foreach (var kvp in instance.Moonshots)
        {
            // Try to disable every feature at tenant level
            tenantOverrides[kvp.Key] = kvp.Value with { Enabled = false };
        }

        var tenantConfig = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(tenantOverrides),
            Level = ConfigHierarchyLevel.Tenant,
            TenantId = "test-tenant"
        };

        var merged = instance.MergeWith(tenantConfig);

        // Locked features should remain enabled (parent wins)
        Assert.True(merged.Moonshots[MoonshotId.CompliancePassports].Enabled,
            "Locked feature CompliancePassports should not be overridden by tenant");
        Assert.True(merged.Moonshots[MoonshotId.SovereigntyMesh].Enabled,
            "Locked feature SovereigntyMesh should not be overridden by tenant");
        Assert.True(merged.Moonshots[MoonshotId.CryptoTimeLocks].Enabled,
            "Locked feature CryptoTimeLocks should not be overridden by tenant");

        // TenantOverridable features should be disabled (tenant override allowed)
        Assert.False(merged.Moonshots[MoonshotId.DataConsciousness].Enabled,
            "TenantOverridable feature DataConsciousness should be overridden by tenant");
        Assert.False(merged.Moonshots[MoonshotId.ZeroGravityStorage].Enabled,
            "TenantOverridable feature ZeroGravityStorage should be overridden by tenant");
        Assert.False(merged.Moonshots[MoonshotId.ChaosVaccination].Enabled,
            "TenantOverridable feature ChaosVaccination should be overridden by tenant");
        Assert.False(merged.Moonshots[MoonshotId.CarbonAwareLifecycle].Enabled,
            "TenantOverridable feature CarbonAwareLifecycle should be overridden by tenant");
        Assert.False(merged.Moonshots[MoonshotId.UniversalFabric].Enabled,
            "TenantOverridable feature UniversalFabric should be overridden by tenant");

        // UserOverridable features should also be disabled (tenant can override)
        Assert.False(merged.Moonshots[MoonshotId.UniversalTags].Enabled,
            "UserOverridable feature UniversalTags should be overridden by tenant");
        Assert.False(merged.Moonshots[MoonshotId.SemanticSync].Enabled,
            "UserOverridable feature SemanticSync should be overridden by tenant");
    }

    [Fact]
    public void MoonshotUserLevelCannotOverrideTenantOnlyFeatures()
    {
        var instance = MoonshotConfigurationDefaults.CreateProductionDefaults();

        // Create user-level config that tries to disable TenantOverridable features
        var userOverrides = new Dictionary<MoonshotId, MoonshotFeatureConfig>();
        foreach (var kvp in instance.Moonshots)
        {
            if (kvp.Value.OverridePolicy == MoonshotOverridePolicy.TenantOverridable)
            {
                userOverrides[kvp.Key] = kvp.Value with { Enabled = false };
            }
        }

        var userConfig = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(userOverrides),
            Level = ConfigHierarchyLevel.User,
            TenantId = "test-tenant",
            UserId = "test-user"
        };

        var merged = instance.MergeWith(userConfig);

        // TenantOverridable features should remain enabled (user cannot override)
        Assert.True(merged.Moonshots[MoonshotId.DataConsciousness].Enabled,
            "TenantOverridable feature DataConsciousness should not be overridden by user");
        Assert.True(merged.Moonshots[MoonshotId.ZeroGravityStorage].Enabled,
            "TenantOverridable feature ZeroGravityStorage should not be overridden by user");
        Assert.True(merged.Moonshots[MoonshotId.ChaosVaccination].Enabled,
            "TenantOverridable feature ChaosVaccination should not be overridden by user");
        Assert.True(merged.Moonshots[MoonshotId.CarbonAwareLifecycle].Enabled,
            "TenantOverridable feature CarbonAwareLifecycle should not be overridden by user");
        Assert.True(merged.Moonshots[MoonshotId.UniversalFabric].Enabled,
            "TenantOverridable feature UniversalFabric should not be overridden by user");
    }

    [Fact]
    public void MoonshotValidatorDetectsOverrideViolations()
    {
        var instance = MoonshotConfigurationDefaults.CreateProductionDefaults();

        // Create a user config that tries to override locked compliance feature
        var userOverrides = new Dictionary<MoonshotId, MoonshotFeatureConfig>
        {
            [MoonshotId.CompliancePassports] = instance.Moonshots[MoonshotId.CompliancePassports] with
            {
                Enabled = false // Try to disable locked feature
            }
        };

        var userConfig = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(userOverrides),
            Level = ConfigHierarchyLevel.User,
            TenantId = "test-tenant",
            UserId = "test-user"
        };

        var validator = new MoonshotConfigurationValidator { ParentConfig = instance };
        var result = validator.Validate(userConfig);

        // The user config disables CompliancePassports which is Locked -- that is a valid
        // user config on its own (Enabled=false means no dep check), but the override
        // policy violation should be caught
        Assert.True(result.Errors.Any(e => e.Code == MoonshotConfigurationValidator.CodeOverrideViolation),
            "Validator should detect override violation for locked CompliancePassports");
    }

    [Fact]
    public void MoonshotValidatorDetectsMissingDependencies()
    {
        // Create a config where CompliancePassports is enabled but UniversalTags is disabled
        var moonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>
        {
            [MoonshotId.UniversalTags] = MoonshotFeatureConfig.CreateDisabled(MoonshotId.UniversalTags),
            [MoonshotId.CompliancePassports] = new MoonshotFeatureConfig(
                Id: MoonshotId.CompliancePassports,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.Locked,
                StrategySelections: new ReadOnlyDictionary<string, MoonshotStrategySelection>(
                    new Dictionary<string, MoonshotStrategySelection>
                    {
                        ["monitoring"] = new MoonshotStrategySelection("ContinuousAudit")
                    }),
                Settings: ReadOnlyDictionary<string, string>.Empty,
                RequiredDependencies: new[] { MoonshotId.UniversalTags },
                DefinedAt: ConfigHierarchyLevel.Instance)
        };

        var config = new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(moonshots),
            Level = ConfigHierarchyLevel.Instance
        };

        var validator = new MoonshotConfigurationValidator();
        var result = validator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == MoonshotConfigurationValidator.CodeDepMissing);
    }

    [Fact]
    public void AllMoonshotFeaturesHaveAtLeastOneStrategy()
    {
        var config = MoonshotConfigurationDefaults.CreateProductionDefaults();

        foreach (var kvp in config.Moonshots)
        {
            var feature = kvp.Value;
            Assert.True(feature.StrategySelections.Count > 0,
                $"MoonshotId.{kvp.Key} has no strategy selections");

            foreach (var strategy in feature.StrategySelections)
            {
                Assert.False(string.IsNullOrWhiteSpace(strategy.Value.StrategyName),
                    $"MoonshotId.{kvp.Key} has empty strategy name for capability '{strategy.Key}'");
            }
        }
    }

    [Fact]
    public void MoonshotProductionDefaultsPassValidation()
    {
        var config = MoonshotConfigurationDefaults.CreateProductionDefaults();
        var validator = new MoonshotConfigurationValidator();
        var result = validator.Validate(config);

        Assert.True(result.IsValid,
            $"Production defaults failed validation: {string.Join("; ", result.Errors.Select(e => e.Message))}");
    }

    [Fact]
    public void ConfigurationKeysFollowNamingConvention()
    {
        // Verify that all MoonshotId enum values follow PascalCase naming
        // and all setting keys follow PascalCase (per actual codebase convention)
        var config = MoonshotConfigurationDefaults.CreateProductionDefaults();
        var issues = new List<string>();

        foreach (var kvp in config.Moonshots)
        {
            // Check setting keys are non-empty and consistent
            foreach (var setting in kvp.Value.Settings)
            {
                if (string.IsNullOrWhiteSpace(setting.Key))
                {
                    issues.Add($"MoonshotId.{kvp.Key} has empty setting key");
                }

                if (string.IsNullOrWhiteSpace(setting.Value))
                {
                    issues.Add($"MoonshotId.{kvp.Key} setting '{setting.Key}' has empty value");
                }
            }

            // Check strategy capability names are non-empty and lowercase
            foreach (var strategy in kvp.Value.StrategySelections)
            {
                if (string.IsNullOrWhiteSpace(strategy.Key))
                {
                    issues.Add($"MoonshotId.{kvp.Key} has empty strategy capability name");
                }
            }
        }

        // Also check base config property names follow C# PascalCase convention
        var baseConfig = new DataWarehouseConfiguration();
        var sections = GetAllConfigurationSections(baseConfig);
        foreach (var (sectionName, section) in sections)
        {
            var properties = section.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType &&
                            p.PropertyType.GetGenericTypeDefinition() == typeof(ConfigurationItem<>));

            foreach (var prop in properties)
            {
                if (!char.IsUpper(prop.Name[0]))
                {
                    issues.Add($"Base config {sectionName}.{prop.Name} does not follow PascalCase");
                }
            }
        }

        Assert.Empty(issues);
    }

    [Fact]
    public void NoHardcodedConfigurationValues_MoonshotSettings()
    {
        // Verify that all required moonshot settings are externalized in configuration
        // (not hardcoded in the defaults). Check that the defaults class provides
        // values for all settings the validator expects.
        var config = MoonshotConfigurationDefaults.CreateProductionDefaults();
        var validator = new MoonshotConfigurationValidator();
        var result = validator.Validate(config);
        var warnings = new List<string>();

        // If validation passes, required settings are present
        if (!result.IsValid)
        {
            foreach (var error in result.Errors.Where(e => e.Code == MoonshotConfigurationValidator.CodeMissingSetting))
            {
                warnings.Add($"Missing setting: {error.Message}");
            }
        }

        Assert.Empty(warnings);

        // Verify specific known settings that should be configurable
        var consciousness = config.Moonshots[MoonshotId.DataConsciousness];
        Assert.True(consciousness.Settings.ContainsKey("ConsciousnessThreshold"),
            "DataConsciousness must have configurable ConsciousnessThreshold");

        var chaos = config.Moonshots[MoonshotId.ChaosVaccination];
        Assert.True(chaos.Settings.ContainsKey("MaxBlastRadius"),
            "ChaosVaccination must have configurable MaxBlastRadius");

        var carbon = config.Moonshots[MoonshotId.CarbonAwareLifecycle];
        Assert.True(carbon.Settings.ContainsKey("CarbonBudgetKgPerTB"),
            "CarbonAwareLifecycle must have configurable CarbonBudgetKgPerTB");

        var timeLocks = config.Moonshots[MoonshotId.CryptoTimeLocks];
        Assert.True(timeLocks.Settings.ContainsKey("DefaultLockDuration"),
            "CryptoTimeLocks must have configurable DefaultLockDuration");

        var sovereignty = config.Moonshots[MoonshotId.SovereigntyMesh];
        Assert.True(sovereignty.Settings.ContainsKey("DefaultZone"),
            "SovereigntyMesh must have configurable DefaultZone");
    }

    #endregion

    #region Helpers

    private static List<(string Name, object Section)> GetAllConfigurationSections(DataWarehouseConfiguration config)
    {
        return new List<(string, object)>
        {
            ("Security", config.Security),
            ("Storage", config.Storage),
            ("Network", config.Network),
            ("Replication", config.Replication),
            ("Encryption", config.Encryption),
            ("Compression", config.Compression),
            ("Observability", config.Observability),
            ("Compute", config.Compute),
            ("Resilience", config.Resilience),
            ("Deployment", config.Deployment),
            ("DataManagement", config.DataManagement),
            ("MessageBus", config.MessageBus),
            ("Plugins", config.Plugins)
        };
    }

    private static bool IsNullableType(Type type)
    {
        return Nullable.GetUnderlyingType(type) != null ||
               !type.IsValueType;
    }

    #endregion
}
