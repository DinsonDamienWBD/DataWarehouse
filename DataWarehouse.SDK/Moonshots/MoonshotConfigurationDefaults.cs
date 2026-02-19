using System.Collections.ObjectModel;

namespace DataWarehouse.SDK.Moonshots;

/// <summary>
/// Static factory for creating production-default and minimal moonshot configurations.
/// Every moonshot has sensible defaults for immediate use in production deployments.
/// </summary>
public static class MoonshotConfigurationDefaults
{
    /// <summary>
    /// Creates a production-ready configuration with all 10 moonshots enabled at the Instance level.
    /// <para>
    /// Override policies:
    /// <list type="bullet">
    ///   <item><see cref="MoonshotOverridePolicy.Locked"/>: CompliancePassports, SovereigntyMesh, CryptoTimeLocks (cannot be disabled by users)</item>
    ///   <item><see cref="MoonshotOverridePolicy.TenantOverridable"/>: DataConsciousness, ZeroGravityStorage, ChaosVaccination, CarbonAwareLifecycle, UniversalFabric</item>
    ///   <item><see cref="MoonshotOverridePolicy.UserOverridable"/>: UniversalTags, SemanticSync</item>
    /// </list>
    /// </para>
    /// Dependencies: CompliancePassports requires UniversalTags. SovereigntyMesh requires CompliancePassports. SemanticSync requires DataConsciousness.
    /// </summary>
    /// <returns>A fully configured <see cref="MoonshotConfiguration"/> at the Instance level.</returns>
    public static MoonshotConfiguration CreateProductionDefaults()
    {
        var moonshots = new Dictionary<MoonshotId, MoonshotFeatureConfig>
        {
            // 1. UniversalTags: Enabled, UserOverridable
            [MoonshotId.UniversalTags] = new MoonshotFeatureConfig(
                Id: MoonshotId.UniversalTags,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.UserOverridable,
                StrategySelections: CreateStrategies(
                    ("query", new MoonshotStrategySelection("InvertedIndex")),
                    ("versioning", new MoonshotStrategySelection("CrdtVersioning"))),
                Settings: EmptySettings(),
                RequiredDependencies: Array.Empty<MoonshotId>(),
                DefinedAt: ConfigHierarchyLevel.Instance),

            // 2. DataConsciousness: Enabled, TenantOverridable
            [MoonshotId.DataConsciousness] = new MoonshotFeatureConfig(
                Id: MoonshotId.DataConsciousness,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.TenantOverridable,
                StrategySelections: CreateStrategies(
                    ("value", new MoonshotStrategySelection("AiValueScoring")),
                    ("liability", new MoonshotStrategySelection("RegulatoryLiabilityScoring"))),
                Settings: CreateSettings(("ConsciousnessThreshold", "50")),
                RequiredDependencies: Array.Empty<MoonshotId>(),
                DefinedAt: ConfigHierarchyLevel.Instance),

            // 3. CompliancePassports: Enabled, Locked (compliance cannot be disabled by users)
            [MoonshotId.CompliancePassports] = new MoonshotFeatureConfig(
                Id: MoonshotId.CompliancePassports,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.Locked,
                StrategySelections: CreateStrategies(
                    ("monitoring", new MoonshotStrategySelection("ContinuousAudit"))),
                Settings: EmptySettings(),
                RequiredDependencies: new[] { MoonshotId.UniversalTags },
                DefinedAt: ConfigHierarchyLevel.Instance),

            // 4. SovereigntyMesh: Enabled, Locked
            [MoonshotId.SovereigntyMesh] = new MoonshotFeatureConfig(
                Id: MoonshotId.SovereigntyMesh,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.Locked,
                StrategySelections: CreateStrategies(
                    ("enforcement", new MoonshotStrategySelection("DeclarativeZones"))),
                Settings: CreateSettings(("DefaultZone", "none")),
                RequiredDependencies: new[] { MoonshotId.CompliancePassports },
                DefinedAt: ConfigHierarchyLevel.Instance),

            // 5. ZeroGravityStorage: Enabled, TenantOverridable
            [MoonshotId.ZeroGravityStorage] = new MoonshotFeatureConfig(
                Id: MoonshotId.ZeroGravityStorage,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.TenantOverridable,
                StrategySelections: CreateStrategies(
                    ("placement", new MoonshotStrategySelection("CrushPlacement")),
                    ("optimization", new MoonshotStrategySelection("GravityAwareOptimizer"))),
                Settings: EmptySettings(),
                RequiredDependencies: Array.Empty<MoonshotId>(),
                DefinedAt: ConfigHierarchyLevel.Instance),

            // 6. CryptoTimeLocks: Enabled, Locked
            [MoonshotId.CryptoTimeLocks] = new MoonshotFeatureConfig(
                Id: MoonshotId.CryptoTimeLocks,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.Locked,
                StrategySelections: CreateStrategies(
                    ("locking", new MoonshotStrategySelection("SoftwareTimeLock"))),
                Settings: CreateSettings(("DefaultLockDuration", "P30D")),
                RequiredDependencies: Array.Empty<MoonshotId>(),
                DefinedAt: ConfigHierarchyLevel.Instance),

            // 7. SemanticSync: Enabled, UserOverridable
            [MoonshotId.SemanticSync] = new MoonshotFeatureConfig(
                Id: MoonshotId.SemanticSync,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.UserOverridable,
                StrategySelections: CreateStrategies(
                    ("classification", new MoonshotStrategySelection("AiClassifier")),
                    ("fidelity", new MoonshotStrategySelection("BandwidthAwareFidelity"))),
                Settings: EmptySettings(),
                RequiredDependencies: new[] { MoonshotId.DataConsciousness },
                DefinedAt: ConfigHierarchyLevel.Instance),

            // 8. ChaosVaccination: Enabled, TenantOverridable
            [MoonshotId.ChaosVaccination] = new MoonshotFeatureConfig(
                Id: MoonshotId.ChaosVaccination,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.TenantOverridable,
                StrategySelections: CreateStrategies(
                    ("injection", new MoonshotStrategySelection("ControlledInjection"))),
                Settings: CreateSettings(("MaxBlastRadius", "10")),
                RequiredDependencies: Array.Empty<MoonshotId>(),
                DefinedAt: ConfigHierarchyLevel.Instance),

            // 9. CarbonAwareLifecycle: Enabled, TenantOverridable
            [MoonshotId.CarbonAwareLifecycle] = new MoonshotFeatureConfig(
                Id: MoonshotId.CarbonAwareLifecycle,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.TenantOverridable,
                StrategySelections: CreateStrategies(
                    ("measurement", new MoonshotStrategySelection("CarbonIntensityApi"))),
                Settings: CreateSettings(("CarbonBudgetKgPerTB", "100")),
                RequiredDependencies: Array.Empty<MoonshotId>(),
                DefinedAt: ConfigHierarchyLevel.Instance),

            // 10. UniversalFabric: Enabled, TenantOverridable
            [MoonshotId.UniversalFabric] = new MoonshotFeatureConfig(
                Id: MoonshotId.UniversalFabric,
                Enabled: true,
                OverridePolicy: MoonshotOverridePolicy.TenantOverridable,
                StrategySelections: CreateStrategies(
                    ("routing", new MoonshotStrategySelection("DwNamespaceRouter"))),
                Settings: EmptySettings(),
                RequiredDependencies: Array.Empty<MoonshotId>(),
                DefinedAt: ConfigHierarchyLevel.Instance)
        };

        return new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(moonshots),
            Level = ConfigHierarchyLevel.Instance,
            TenantId = null,
            UserId = null
        };
    }

    /// <summary>
    /// Creates a minimal deployment configuration with only UniversalTags and UniversalFabric enabled.
    /// All other moonshots are present but disabled. Suitable for minimum viable deployments.
    /// </summary>
    /// <returns>A minimal <see cref="MoonshotConfiguration"/> at the Instance level.</returns>
    public static MoonshotConfiguration CreateMinimalDefaults()
    {
        var production = CreateProductionDefaults();
        var minimal = new Dictionary<MoonshotId, MoonshotFeatureConfig>();

        foreach (var kvp in production.Moonshots)
        {
            var shouldEnable = kvp.Key is MoonshotId.UniversalTags or MoonshotId.UniversalFabric;
            minimal[kvp.Key] = kvp.Value with { Enabled = shouldEnable };
        }

        return new MoonshotConfiguration
        {
            Moonshots = new ReadOnlyDictionary<MoonshotId, MoonshotFeatureConfig>(minimal),
            Level = ConfigHierarchyLevel.Instance,
            TenantId = null,
            UserId = null
        };
    }

    private static IReadOnlyDictionary<string, MoonshotStrategySelection> CreateStrategies(
        params (string key, MoonshotStrategySelection value)[] entries)
    {
        var dict = new Dictionary<string, MoonshotStrategySelection>(entries.Length);
        foreach (var (key, value) in entries)
            dict[key] = value;
        return new ReadOnlyDictionary<string, MoonshotStrategySelection>(dict);
    }

    private static IReadOnlyDictionary<string, string> CreateSettings(
        params (string key, string value)[] entries)
    {
        var dict = new Dictionary<string, string>(entries.Length);
        foreach (var (key, value) in entries)
            dict[key] = value;
        return new ReadOnlyDictionary<string, string>(dict);
    }

    private static IReadOnlyDictionary<string, string> EmptySettings()
        => ReadOnlyDictionary<string, string>.Empty;
}
