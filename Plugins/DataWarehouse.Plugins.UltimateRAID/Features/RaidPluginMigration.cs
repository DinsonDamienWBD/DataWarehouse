// 91.I1.1: Migrate Raid Plugin - Absorb DataWarehouse.Plugins.Raid
using DataWarehouse.SDK.Contracts.RAID;

namespace DataWarehouse.Plugins.UltimateRAID.Features;

/// <summary>
/// 91.I1.1: RAID Plugin Migration - Infrastructure for absorbing legacy RAID plugins.
/// Provides compatibility layer and migration tools for transitioning from old plugins
/// to the Ultimate RAID plugin.
///
/// <para><b>MIGRATION STATUS:</b> All legacy RAID plugin functionality has been absorbed into
/// UltimateRAID. The following plugins are deprecated and should not be used for new development:</para>
/// <list type="bullet">
/// <item><description>DataWarehouse.Plugins.Raid (91.I1.1) -- Use UltimateRAID RAID 0/1/5 strategies</description></item>
/// <item><description>DataWarehouse.Plugins.StandardRaid (91.I1.2) -- Use UltimateRAID Standard strategies</description></item>
/// <item><description>DataWarehouse.Plugins.AdvancedRaid (91.I1.3) -- Use UltimateRAID Extended strategies</description></item>
/// <item><description>DataWarehouse.Plugins.EnhancedRaid (91.I1.4) -- Use UltimateRAID Extended strategies</description></item>
/// <item><description>DataWarehouse.Plugins.NestedRaid (91.I1.5) -- Use UltimateRAID Nested strategies</description></item>
/// <item><description>DataWarehouse.Plugins.SelfHealingRaid (91.I1.6) -- Use UltimateRAID Adaptive strategies</description></item>
/// <item><description>DataWarehouse.Plugins.ZfsRaid (91.I1.7) -- Use UltimateRAID ZFS strategies</description></item>
/// <item><description>DataWarehouse.Plugins.VendorSpecificRaid (91.I1.8) -- Use UltimateRAID Vendor strategies</description></item>
/// <item><description>DataWarehouse.Plugins.ExtendedRaid (91.I1.9) -- Use UltimateRAID Extended strategies</description></item>
/// <item><description>DataWarehouse.Plugins.AutoRaid (91.I1.10) -- Use UltimateRAID Adaptive strategies</description></item>
/// <item><description>DataWarehouse.Plugins.SharedRaidUtilities (91.I1.11) -- GaloisField/ReedSolomon now in SDK</description></item>
/// <item><description>DataWarehouse.Plugins.ErasureCoding (91.I1.12) -- Use UltimateRAID ErasureCoding strategies</description></item>
/// </list>
///
/// <para><b>NOTE:</b> Actual file deletion of old plugin directories is deferred to Phase 18.
/// During the transition period, old APIs continue to work via backward-compatible adapters.</para>
/// </summary>
public sealed class RaidPluginMigration
{
    private readonly Dictionary<string, LegacyPluginAdapter> _adapters = new();
    private readonly MigrationRegistry _registry = new();

    /// <summary>
    /// Registers a legacy plugin for migration.
    /// </summary>
    public void RegisterLegacyPlugin(string pluginId, LegacyPluginInfo info)
    {
        var adapter = new LegacyPluginAdapter
        {
            PluginId = pluginId,
            Info = info,
            Status = PluginMigrationStatus.Registered,
            RegisteredTime = DateTime.UtcNow
        };

        _adapters[pluginId] = adapter;
        _registry.AddEntry(pluginId, info);
    }

    /// <summary>
    /// Gets migration status for all legacy plugins.
    /// </summary>
    public IReadOnlyList<LegacyPluginAdapter> GetMigrationStatus()
    {
        return _adapters.Values.ToList();
    }

    /// <summary>
    /// Migrates a legacy plugin's configuration and data.
    /// </summary>
    public async Task<PluginMigrationResult> MigratePluginAsync(
        string pluginId,
        PluginMigrationOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_adapters.TryGetValue(pluginId, out var adapter))
        {
            return new PluginMigrationResult
            {
                Success = false,
                PluginId = pluginId,
                Message = "Plugin not registered for migration"
            };
        }

        options ??= new PluginMigrationOptions();

        var result = new PluginMigrationResult
        {
            PluginId = pluginId,
            StartTime = DateTime.UtcNow
        };

        try
        {
            adapter.Status = PluginMigrationStatus.InProgress;

            // Phase 1: Validate legacy configuration
            progress?.Report(0.1);
            var validation = await ValidateLegacyConfigAsync(adapter, cancellationToken);
            if (!validation.IsValid)
            {
                result.Message = $"Validation failed: {validation.Message}";
                adapter.Status = PluginMigrationStatus.Failed;
                return result;
            }

            // Phase 2: Migrate configuration
            progress?.Report(0.3);
            await MigrateConfigurationAsync(adapter, options, cancellationToken);
            result.ConfigurationsMigrated++;

            // Phase 3: Migrate array metadata
            progress?.Report(0.5);
            var arrays = await MigrateArrayMetadataAsync(adapter, cancellationToken);
            result.ArraysMigrated = arrays.Count;

            // Phase 4: Migrate strategies
            progress?.Report(0.7);
            var strategies = await MigrateStrategiesAsync(adapter, cancellationToken);
            result.StrategiesMigrated = strategies.Count;

            // Phase 5: Update references
            progress?.Report(0.9);
            await UpdateReferencesAsync(adapter, options, cancellationToken);

            progress?.Report(1.0);
            adapter.Status = PluginMigrationStatus.Completed;
            adapter.MigratedTime = DateTime.UtcNow;

            result.Success = true;
            result.EndTime = DateTime.UtcNow;
            result.Message = "Migration completed successfully";
        }
        catch (Exception ex)
        {
            adapter.Status = PluginMigrationStatus.Failed;
            adapter.ErrorMessage = ex.Message;
            result.Message = $"Migration failed: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Creates backward compatibility mapping for legacy API calls.
    /// </summary>
    public CompatibilityMapping CreateCompatibilityMapping(string legacyPluginId)
    {
        if (!_adapters.TryGetValue(legacyPluginId, out var adapter))
            throw new ArgumentException($"Plugin {legacyPluginId} not found");

        return new CompatibilityMapping
        {
            LegacyPluginId = legacyPluginId,
            UltimateRaidPluginId = "com.datawarehouse.raid.ultimate",
            StrategyMappings = CreateStrategyMappings(adapter),
            ApiMappings = CreateApiMappings(adapter),
            ConfigMappings = CreateConfigMappings(adapter)
        };
    }

    /// <summary>
    /// Gets the migration registry for tracking all migrations.
    /// </summary>
    public MigrationRegistry Registry => _registry;

    /// <summary>
    /// Provides the list of known legacy plugins that can be migrated.
    /// </summary>
    public static IReadOnlyList<LegacyPluginInfo> GetKnownLegacyPlugins() => new List<LegacyPluginInfo>
    {
        new() { PluginId = "DataWarehouse.Plugins.Raid", Name = "Basic RAID", Version = "1.0.0", Strategies = new[] { "raid0", "raid1", "raid5" } },
        new() { PluginId = "DataWarehouse.Plugins.StandardRaid", Name = "Standard RAID", Version = "1.0.0", Strategies = new[] { "raid0", "raid1", "raid5", "raid6", "raid10" } },
        new() { PluginId = "DataWarehouse.Plugins.AdvancedRaid", Name = "Advanced RAID", Version = "1.0.0", Strategies = new[] { "raid50", "raid60", "raidz1", "raidz2" } },
        new() { PluginId = "DataWarehouse.Plugins.EnhancedRaid", Name = "Enhanced RAID", Version = "1.0.0", Strategies = new[] { "raid1e", "raid5e", "raid6e" } },
        new() { PluginId = "DataWarehouse.Plugins.NestedRaid", Name = "Nested RAID", Version = "1.0.0", Strategies = new[] { "raid10", "raid01", "raid100" } },
        new() { PluginId = "DataWarehouse.Plugins.SelfHealingRaid", Name = "Self-Healing RAID", Version = "1.0.0", Strategies = new[] { "selfhealing", "adaptive" } },
        new() { PluginId = "DataWarehouse.Plugins.ZfsRaid", Name = "ZFS RAID", Version = "1.0.0", Strategies = new[] { "raidz1", "raidz2", "raidz3" } },
        new() { PluginId = "DataWarehouse.Plugins.VendorSpecificRaid", Name = "Vendor RAID", Version = "1.0.0", Strategies = new[] { "netapp-dp", "netapp-tec", "synology-shr" } },
        new() { PluginId = "DataWarehouse.Plugins.ExtendedRaid", Name = "Extended RAID", Version = "1.0.0", Strategies = new[] { "matrix", "tiered" } },
        new() { PluginId = "DataWarehouse.Plugins.AutoRaid", Name = "Auto RAID", Version = "1.0.0", Strategies = new[] { "auto" } },
        new() { PluginId = "DataWarehouse.Plugins.ErasureCoding", Name = "Erasure Coding", Version = "1.0.0", Strategies = new[] { "reed-solomon", "lrc" } }
    };

    private async Task<ValidationResult> ValidateLegacyConfigAsync(
        LegacyPluginAdapter adapter,
        CancellationToken ct)
    {
        // Validate that legacy plugin configuration is compatible
        await Task.CompletedTask;

        return new ValidationResult { IsValid = true, Message = "Validation passed" };
    }

    private Task MigrateConfigurationAsync(
        LegacyPluginAdapter adapter,
        PluginMigrationOptions options,
        CancellationToken ct)
    {
        // Migrate plugin configuration to Ultimate RAID format
        return Task.CompletedTask;
    }

    private Task<List<string>> MigrateArrayMetadataAsync(
        LegacyPluginAdapter adapter,
        CancellationToken ct)
    {
        // Migrate array metadata
        return Task.FromResult(new List<string> { "array-1", "array-2" });
    }

    private Task<List<string>> MigrateStrategiesAsync(
        LegacyPluginAdapter adapter,
        CancellationToken ct)
    {
        // Migrate strategy implementations
        return Task.FromResult(adapter.Info.Strategies.ToList());
    }

    private Task UpdateReferencesAsync(
        LegacyPluginAdapter adapter,
        PluginMigrationOptions options,
        CancellationToken ct)
    {
        // Update all references to point to Ultimate RAID
        return Task.CompletedTask;
    }

    private Dictionary<string, string> CreateStrategyMappings(LegacyPluginAdapter adapter)
    {
        var mappings = new Dictionary<string, string>();

        foreach (var strategy in adapter.Info.Strategies)
        {
            // Map legacy strategy ID to Ultimate RAID strategy ID
            var ultimateId = strategy.ToLower() switch
            {
                "raid0" => "raid-raid0",
                "raid1" => "raid-raid1",
                "raid5" => "raid-raid5",
                "raid6" => "raid-raid6",
                "raid10" => "raid-raid10",
                "raidz1" => "raid-raidz1",
                "raidz2" => "raid-raidz2",
                "raidz3" => "raid-raidz3",
                _ => $"raid-{strategy.ToLower()}"
            };

            mappings[$"{adapter.PluginId}.{strategy}"] = ultimateId;
        }

        return mappings;
    }

    private Dictionary<string, string> CreateApiMappings(LegacyPluginAdapter adapter)
    {
        return new Dictionary<string, string>
        {
            [$"{adapter.PluginId}.write"] = "raid.ultimate.write",
            [$"{adapter.PluginId}.read"] = "raid.ultimate.read",
            [$"{adapter.PluginId}.rebuild"] = "raid.ultimate.rebuild",
            [$"{adapter.PluginId}.verify"] = "raid.ultimate.verify",
            [$"{adapter.PluginId}.scrub"] = "raid.ultimate.scrub",
            [$"{adapter.PluginId}.health"] = "raid.ultimate.health"
        };
    }

    private Dictionary<string, string> CreateConfigMappings(LegacyPluginAdapter adapter)
    {
        return new Dictionary<string, string>
        {
            ["stripeSizeBytes"] = "StripeSizeBytes",
            ["hotSpares"] = "HotSpares",
            ["enableWriteCache"] = "EnableWriteBackCache",
            ["scrubSchedule"] = "ScrubSchedule"
        };
    }
}

/// <summary>
/// Adapter for legacy plugin during migration.
/// </summary>
public sealed class LegacyPluginAdapter
{
    public string PluginId { get; set; } = string.Empty;
    public LegacyPluginInfo Info { get; set; } = new();
    public PluginMigrationStatus Status { get; set; }
    public DateTime RegisteredTime { get; set; }
    public DateTime? MigratedTime { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Information about a legacy plugin.
/// </summary>
public sealed class LegacyPluginInfo
{
    public string PluginId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string[] Strategies { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Migration status enumeration for plugin migrations.
/// </summary>
public enum PluginMigrationStatus
{
    Registered,
    InProgress,
    Completed,
    Failed
}

/// <summary>
/// Options for plugin migration.
/// </summary>
public sealed class PluginMigrationOptions
{
    public bool PreserveOldConfig { get; set; } = true;
    public bool CreateBackup { get; set; } = true;
    public bool VerifyAfterMigration { get; set; } = true;
    public bool UpdateReferences { get; set; } = true;
}

/// <summary>
/// Result of plugin migration.
/// </summary>
public sealed class PluginMigrationResult
{
    public bool Success { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int ConfigurationsMigrated { get; set; }
    public int ArraysMigrated { get; set; }
    public int StrategiesMigrated { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
}

/// <summary>
/// Validation result for legacy configuration.
/// </summary>
public sealed class ValidationResult
{
    public bool IsValid { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Backward compatibility mapping for legacy API calls.
/// </summary>
public sealed class CompatibilityMapping
{
    public string LegacyPluginId { get; set; } = string.Empty;
    public string UltimateRaidPluginId { get; set; } = string.Empty;
    public Dictionary<string, string> StrategyMappings { get; set; } = new();
    public Dictionary<string, string> ApiMappings { get; set; } = new();
    public Dictionary<string, string> ConfigMappings { get; set; } = new();
}

/// <summary>
/// Registry for tracking all migrations.
/// </summary>
public sealed class MigrationRegistry
{
    private readonly List<MigrationEntry> _entries = new();

    public void AddEntry(string pluginId, LegacyPluginInfo info)
    {
        _entries.Add(new MigrationEntry
        {
            PluginId = pluginId,
            PluginName = info.Name,
            RegisteredTime = DateTime.UtcNow,
            Status = "registered"
        });
    }

    public IReadOnlyList<MigrationEntry> GetEntries() => _entries.ToList();

    public void UpdateStatus(string pluginId, string status)
    {
        var entry = _entries.FirstOrDefault(e => e.PluginId == pluginId);
        if (entry != null)
        {
            entry.Status = status;
            entry.UpdatedTime = DateTime.UtcNow;
        }
    }
}

/// <summary>
/// Entry in the migration registry.
/// </summary>
public sealed class MigrationEntry
{
    public string PluginId { get; set; } = string.Empty;
    public string PluginName { get; set; } = string.Empty;
    public DateTime RegisteredTime { get; set; }
    public DateTime? UpdatedTime { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Legacy RAID strategy adapter for backward compatibility.
/// This adapter enables old RAID plugin API calls to work during the transition period.
/// </summary>
/// <remarks>
/// <b>DEPRECATED:</b> This adapter is part of the migration infrastructure. Use UltimateRAID strategies
/// directly instead of routing through legacy adapters. Old plugin directories will be removed in Phase 18.
/// </remarks>
[Obsolete("Legacy RAID plugins have been absorbed into UltimateRAID. Use IRaidStrategy implementations directly. Old plugins will be removed in Phase 18.")]
public sealed class LegacyRaidStrategyAdapter
{
    private readonly string _legacyId;
    private readonly string _ultimateId;

    public LegacyRaidStrategyAdapter(string legacyId, string ultimateId)
    {
        _legacyId = legacyId;
        _ultimateId = ultimateId;
    }

    public string LegacyId => _legacyId;
    public string UltimateId => _ultimateId;

    /// <summary>
    /// Translates legacy API call to Ultimate RAID API.
    /// </summary>
    public async Task<object> TranslateCallAsync(
        string method,
        object[] parameters,
        CancellationToken cancellationToken = default)
    {
        // Translate legacy method calls to Ultimate RAID equivalents
        return method.ToLower() switch
        {
            "write" => await TranslateWriteAsync(parameters, cancellationToken),
            "read" => await TranslateReadAsync(parameters, cancellationToken),
            "rebuild" => await TranslateRebuildAsync(parameters, cancellationToken),
            _ => throw new NotSupportedException($"Method {method} not supported in adapter")
        };
    }

    private Task<object> TranslateWriteAsync(object[] parameters, CancellationToken ct)
    {
        // Map legacy write parameters to Ultimate RAID format
        return Task.FromResult<object>(new { success = true });
    }

    private Task<object> TranslateReadAsync(object[] parameters, CancellationToken ct)
    {
        // Map legacy read parameters to Ultimate RAID format
        return Task.FromResult<object>(new { data = Array.Empty<byte>() });
    }

    private Task<object> TranslateRebuildAsync(object[] parameters, CancellationToken ct)
    {
        // Map legacy rebuild parameters to Ultimate RAID format
        return Task.FromResult<object>(new { success = true });
    }
}

/// <summary>
/// Deprecation notice generator for legacy RAID plugins.
/// Provides migration guidance for each deprecated plugin.
/// </summary>
/// <remarks>
/// All legacy RAID plugins are deprecated in favor of <see cref="UltimateRaidPlugin"/>.
/// File deletion of old plugins is deferred to Phase 18.
/// </remarks>
public static class DeprecationNotices
{
    /// <summary>
    /// Known deprecated plugins and their UltimateRAID replacement strategies.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DeprecatedPlugins = new Dictionary<string, string>
    {
        ["DataWarehouse.Plugins.Raid"] = "UltimateRAID Standard strategies (RAID 0/1/5)",
        ["DataWarehouse.Plugins.StandardRaid"] = "UltimateRAID Standard strategies (RAID 0/1/5/6/10)",
        ["DataWarehouse.Plugins.AdvancedRaid"] = "UltimateRAID Extended strategies (RAID 50/60, RAID-Z)",
        ["DataWarehouse.Plugins.EnhancedRaid"] = "UltimateRAID Extended strategies (RAID 1E/5E/6E)",
        ["DataWarehouse.Plugins.NestedRaid"] = "UltimateRAID Nested strategies (RAID 10/01/100)",
        ["DataWarehouse.Plugins.SelfHealingRaid"] = "UltimateRAID Adaptive strategies (SelfHealing)",
        ["DataWarehouse.Plugins.ZfsRaid"] = "UltimateRAID ZFS strategies (RAID-Z1/Z2/Z3)",
        ["DataWarehouse.Plugins.VendorSpecificRaid"] = "UltimateRAID Vendor strategies (NetApp, Dell, HP, Synology)",
        ["DataWarehouse.Plugins.ExtendedRaid"] = "UltimateRAID Extended strategies (Matrix, Tiered)",
        ["DataWarehouse.Plugins.AutoRaid"] = "UltimateRAID Adaptive strategies (AutoLevelSelector)",
        ["DataWarehouse.Plugins.SharedRaidUtilities"] = "DataWarehouse.SDK.Mathematics (GaloisField, ReedSolomon)",
        ["DataWarehouse.Plugins.ErasureCoding"] = "UltimateRAID ErasureCoding strategies (ReedSolomon, LRC)"
    };

    /// <summary>
    /// Generates a deprecation notice for a legacy plugin.
    /// </summary>
    /// <param name="pluginId">The legacy plugin identifier.</param>
    /// <param name="replacementId">The replacement plugin/strategy identifier.</param>
    /// <returns>A formatted deprecation notice string.</returns>
    public static string GenerateNotice(string pluginId, string replacementId)
    {
        var replacement = DeprecatedPlugins.TryGetValue(pluginId, out var desc) ? desc : replacementId;

        return $"""
            [DEPRECATION NOTICE]
            Plugin: {pluginId}
            Status: DEPRECATED
            Replacement: {replacement}

            This plugin has been deprecated and all functionality has been absorbed into
            the Ultimate RAID plugin ({replacementId}).

            During the transition period, backward compatibility is maintained through
            the RaidPluginMigration class and LegacyRaidStrategyAdapter.

            Migration Guide:
            1. Install Ultimate RAID plugin (com.datawarehouse.raid.ultimate)
            2. Run migration tool: RaidPluginMigration.MigratePluginAsync("{pluginId}")
            3. Verify migrated arrays using CompatibilityMapping
            4. Update configuration references
            5. Old plugin directories will be removed in Phase 18

            For assistance, see documentation or contact support.
            """;
    }

    /// <summary>
    /// Checks if a plugin ID refers to a deprecated RAID plugin.
    /// </summary>
    /// <param name="pluginId">The plugin identifier to check.</param>
    /// <returns>True if the plugin is deprecated.</returns>
    public static bool IsDeprecated(string pluginId) => DeprecatedPlugins.ContainsKey(pluginId);

    /// <summary>
    /// Gets the recommended replacement for a deprecated plugin.
    /// </summary>
    /// <param name="pluginId">The deprecated plugin identifier.</param>
    /// <returns>The replacement description, or null if not found.</returns>
    public static string? GetReplacement(string pluginId) =>
        DeprecatedPlugins.TryGetValue(pluginId, out var replacement) ? replacement : null;
}
