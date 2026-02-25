using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.UltimateStorage.Migration;

/// <summary>
/// Tracks the removal timeline of deprecated storage plugins.
/// Validates that no references to removed plugins exist in configurations or code.
/// Provides cleanup reports and recommendations.
/// </summary>
/// <remarks>
/// This tracker helps ensure clean removal of deprecated plugins by:
/// - Maintaining a timeline of plugin removal dates
/// - Scanning for references to removed plugins
/// - Generating cleanup reports
/// - Providing recommendations for safe removal
/// - Tracking plugin usage before removal
/// </remarks>
public sealed class PluginRemovalTracker
{
    private readonly Dictionary<string, PluginRemovalInfo> _removalTimeline;
    private readonly List<string> _removedPlugins;
    private readonly Dictionary<string, RemovalValidationResult> _validationCache;
    private readonly object _syncLock = new();

    /// <summary>
    /// Initializes a new instance of the PluginRemovalTracker.
    /// Loads the default removal timeline.
    /// </summary>
    public PluginRemovalTracker()
    {
        _removalTimeline = BuildRemovalTimeline();
        _removedPlugins = [];
        _validationCache = new Dictionary<string, RemovalValidationResult>();
    }

    /// <summary>
    /// Gets the removal timeline for all tracked plugins.
    /// </summary>
    public IReadOnlyDictionary<string, PluginRemovalInfo> RemovalTimeline => _removalTimeline;

    /// <summary>
    /// Checks if a plugin is scheduled for removal.
    /// </summary>
    /// <param name="pluginId">The plugin ID to check.</param>
    /// <returns>True if scheduled for removal.</returns>
    public bool IsScheduledForRemoval(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _removalTimeline.ContainsKey(pluginId);
    }

    /// <summary>
    /// Checks if a plugin has been removed.
    /// </summary>
    /// <param name="pluginId">The plugin ID to check.</param>
    /// <returns>True if removed.</returns>
    public bool IsRemoved(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (!_removalTimeline.TryGetValue(pluginId, out var info))
        {
            return false;
        }

        return DateTime.UtcNow >= info.RemovalDate;
    }

    /// <summary>
    /// Gets removal information for a plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>Removal info if tracked, null otherwise.</returns>
    public PluginRemovalInfo? GetRemovalInfo(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _removalTimeline.TryGetValue(pluginId, out var info) ? info : null;
    }

    /// <summary>
    /// Gets the days remaining until a plugin is removed.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>Days remaining, or -1 if already removed or not tracked.</returns>
    public int GetDaysUntilRemoval(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (!_removalTimeline.TryGetValue(pluginId, out var info))
        {
            return -1;
        }

        var daysRemaining = (info.RemovalDate - DateTime.UtcNow).Days;
        return Math.Max(-1, daysRemaining);
    }

    /// <summary>
    /// Validates that no references to a removed plugin exist in the system.
    /// </summary>
    /// <param name="pluginId">The plugin ID to validate.</param>
    /// <param name="configurations">List of configurations to scan.</param>
    /// <param name="registeredPlugins">List of currently registered plugins.</param>
    /// <returns>Validation result with any found references.</returns>
    public RemovalValidationResult ValidateNoReferences(
        string pluginId,
        IEnumerable<Dictionary<string, object>> configurations,
        IEnumerable<IPlugin> registeredPlugins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(configurations);
        ArgumentNullException.ThrowIfNull(registeredPlugins);

        var result = new RemovalValidationResult
        {
            PluginId = pluginId,
            ValidationDate = DateTime.UtcNow,
            IsClean = true
        };

        // Check if plugin is still registered
        if (registeredPlugins.Any(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase)))
        {
            result.IsClean = false;
            result.FoundReferences.Add(new PluginReference
            {
                ReferenceType = ReferenceType.Registration,
                Location = "Plugin Registry",
                Description = "Plugin is still registered in the system"
            });
        }

        // Scan configurations for references
        var configIndex = 0;
        foreach (var config in configurations)
        {
            var references = ScanConfigurationForReferences(pluginId, config, configIndex);
            if (references.Count > 0)
            {
                result.IsClean = false;
                result.FoundReferences.AddRange(references);
            }
            configIndex++;
        }

        // Cache result
        lock (_syncLock)
        {
            _validationCache[pluginId] = result;
        }

        return result;
    }

    /// <summary>
    /// Generates a comprehensive cleanup report for a plugin removal.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>Formatted cleanup report.</returns>
    public string GenerateCleanupReport(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var sb = new StringBuilder();

        sb.AppendLine($"# Plugin Removal Cleanup Report");
        sb.AppendLine($"Plugin: {pluginId}");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        // Get removal info
        var info = GetRemovalInfo(pluginId);
        if (info == null)
        {
            sb.AppendLine("**Status**: Not tracked for removal");
            return sb.ToString();
        }

        sb.AppendLine($"## Removal Timeline");
        sb.AppendLine($"- **Deprecated**: {info.DeprecationDate:yyyy-MM-dd}");
        sb.AppendLine($"- **Scheduled Removal**: {info.RemovalDate:yyyy-MM-dd}");
        sb.AppendLine($"- **Status**: {(IsRemoved(pluginId) ? "Removed" : "Pending Removal")}");
        sb.AppendLine($"- **Days Until Removal**: {GetDaysUntilRemoval(pluginId)}");
        sb.AppendLine();

        // Validation results
        if (_validationCache.TryGetValue(pluginId, out var validation))
        {
            sb.AppendLine($"## Validation Results");
            sb.AppendLine($"- **Clean**: {(validation.IsClean ? "Yes" : "No")}");
            sb.AppendLine($"- **Last Validated**: {validation.ValidationDate:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();

            if (!validation.IsClean)
            {
                sb.AppendLine($"### Found References ({validation.FoundReferences.Count})");
                sb.AppendLine();

                foreach (var reference in validation.FoundReferences)
                {
                    sb.AppendLine($"- **{reference.ReferenceType}**: {reference.Location}");
                    sb.AppendLine($"  - {reference.Description}");
                }
                sb.AppendLine();
            }
        }

        // Cleanup steps
        sb.AppendLine($"## Cleanup Steps");
        sb.AppendLine();
        sb.AppendLine("1. **Pre-Removal Validation**");
        sb.AppendLine("   - Run validation to find all references");
        sb.AppendLine("   - Ensure all configurations are migrated");
        sb.AppendLine("   - Verify no active usage");
        sb.AppendLine();
        sb.AppendLine("2. **Remove References**");
        sb.AppendLine("   - Update configuration files");
        sb.AppendLine("   - Remove from plugin registry");
        sb.AppendLine("   - Update documentation");
        sb.AppendLine();
        sb.AppendLine("3. **Remove Plugin Files**");
        sb.AppendLine("   - Delete plugin assembly");
        sb.AppendLine("   - Remove plugin directory");
        sb.AppendLine("   - Clean up any plugin-specific data");
        sb.AppendLine();
        sb.AppendLine("4. **Post-Removal Verification**");
        sb.AppendLine("   - Test system functionality");
        sb.AppendLine("   - Verify no broken references");
        sb.AppendLine("   - Monitor for errors");
        sb.AppendLine();

        // Migration target
        if (!string.IsNullOrEmpty(info.MigrationStrategyId))
        {
            sb.AppendLine($"## Migration Target");
            sb.AppendLine($"- **Strategy**: {info.MigrationStrategyId}");
            sb.AppendLine($"- **Plugin**: UltimateStorage");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets a list of plugins that are safe to remove (no references found).
    /// </summary>
    /// <returns>List of plugin IDs safe for removal.</returns>
    public List<string> GetPluginsSafeForRemoval()
    {
        var safePlugins = new List<string>();

        foreach (var (pluginId, info) in _removalTimeline)
        {
            // Must be past removal date
            if (!IsRemoved(pluginId))
            {
                continue;
            }

            // Must have passed validation
            if (_validationCache.TryGetValue(pluginId, out var validation) && validation.IsClean)
            {
                safePlugins.Add(pluginId);
            }
        }

        return safePlugins;
    }

    /// <summary>
    /// Marks a plugin as successfully removed.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    public void MarkAsRemoved(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        lock (_syncLock)
        {
            if (!_removedPlugins.Contains(pluginId))
            {
                _removedPlugins.Add(pluginId);
            }
        }
    }

    /// <summary>
    /// Generates a summary report of all plugin removals.
    /// </summary>
    /// <returns>Formatted summary report.</returns>
    public string GenerateRemovalSummaryReport()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Plugin Removal Summary");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        var now = DateTime.UtcNow;
        var pending = _removalTimeline.Values.Where(i => i.RemovalDate > now).ToList();
        var overdue = _removalTimeline.Values.Where(i => i.RemovalDate <= now).ToList();

        sb.AppendLine($"## Overview");
        sb.AppendLine($"- **Total Tracked**: {_removalTimeline.Count}");
        sb.AppendLine($"- **Pending Removal**: {pending.Count}");
        sb.AppendLine($"- **Overdue for Removal**: {overdue.Count}");
        sb.AppendLine($"- **Successfully Removed**: {_removedPlugins.Count}");
        sb.AppendLine();

        // Pending removals
        if (pending.Count > 0)
        {
            sb.AppendLine("## Pending Removals");
            sb.AppendLine();
            sb.AppendLine("| Plugin ID | Removal Date | Days Remaining |");
            sb.AppendLine("|-----------|--------------|----------------|");

            foreach (var info in pending.OrderBy(i => i.RemovalDate))
            {
                var days = (info.RemovalDate - now).Days;
                sb.AppendLine($"| {info.PluginId} | {info.RemovalDate:yyyy-MM-dd} | {days} |");
            }
            sb.AppendLine();
        }

        // Overdue removals
        if (overdue.Count > 0)
        {
            sb.AppendLine("## Overdue Removals (Action Required)");
            sb.AppendLine();
            sb.AppendLine("| Plugin ID | Removal Date | Days Overdue |");
            sb.AppendLine("|-----------|--------------|--------------|");

            foreach (var info in overdue.OrderBy(i => i.RemovalDate))
            {
                var days = (now - info.RemovalDate).Days;
                sb.AppendLine($"| {info.PluginId} | {info.RemovalDate:yyyy-MM-dd} | {days} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #region Private Helper Methods

    /// <summary>
    /// Builds the removal timeline for tracked plugins.
    /// </summary>
    private static Dictionary<string, PluginRemovalInfo> BuildRemovalTimeline()
    {
        var baseDate = new DateTime(2026, 2, 5);

        return new Dictionary<string, PluginRemovalInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["com.datawarehouse.storage.ftp"] = new PluginRemovalInfo
            {
                PluginId = "com.datawarehouse.storage.ftp",
                DeprecationDate = baseDate,
                RemovalDate = baseDate.AddMonths(12),
                Reason = "Security concerns - FTP is insecure",
                MigrationStrategyId = "sftp",
                RemovalPriority = RemovalPriority.High
            },
            ["com.datawarehouse.storage.tape"] = new PluginRemovalInfo
            {
                PluginId = "com.datawarehouse.storage.tape",
                DeprecationDate = baseDate,
                RemovalDate = baseDate.AddMonths(18),
                Reason = "Low usage - modern alternatives available",
                MigrationStrategyId = "tape-lto",
                RemovalPriority = RemovalPriority.Medium
            },
            ["com.datawarehouse.storage.legacy-filesystem"] = new PluginRemovalInfo
            {
                PluginId = "com.datawarehouse.storage.legacy-filesystem",
                DeprecationDate = baseDate.AddMonths(-6),
                RemovalDate = baseDate.AddMonths(6),
                Reason = "Replaced by improved filesystem strategy",
                MigrationStrategyId = "filesystem",
                RemovalPriority = RemovalPriority.Low
            }
        };
    }

    /// <summary>
    /// Scans a configuration dictionary for references to a plugin.
    /// </summary>
    private static List<PluginReference> ScanConfigurationForReferences(
        string pluginId,
        Dictionary<string, object> config,
        int configIndex)
    {
        var references = new List<PluginReference>();

        foreach (var (key, value) in config)
        {
            var valueStr = value?.ToString() ?? string.Empty;

            if (valueStr.Contains(pluginId, StringComparison.OrdinalIgnoreCase))
            {
                references.Add(new PluginReference
                {
                    ReferenceType = ReferenceType.Configuration,
                    Location = $"Configuration[{configIndex}].{key}",
                    Description = $"Contains reference to '{pluginId}'"
                });
            }
        }

        return references;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Information about a plugin scheduled for removal.
/// </summary>
public sealed class PluginRemovalInfo
{
    /// <summary>Plugin unique identifier.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>Date when plugin was deprecated.</summary>
    public DateTime DeprecationDate { get; set; }

    /// <summary>Date when plugin will be removed.</summary>
    public DateTime RemovalDate { get; set; }

    /// <summary>Reason for removal.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Migration strategy ID in UltimateStorage.</summary>
    public string MigrationStrategyId { get; set; } = string.Empty;

    /// <summary>Priority of this removal.</summary>
    public RemovalPriority RemovalPriority { get; set; }
}

/// <summary>
/// Result of validating plugin removal.
/// </summary>
public sealed class RemovalValidationResult
{
    /// <summary>Plugin ID being validated.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>When validation was performed.</summary>
    public DateTime ValidationDate { get; set; }

    /// <summary>Whether the plugin is clean for removal (no references found).</summary>
    public bool IsClean { get; set; }

    /// <summary>List of found references to the plugin.</summary>
    public List<PluginReference> FoundReferences { get; set; } = [];
}

/// <summary>
/// Represents a reference to a plugin found during validation.
/// </summary>
public sealed class PluginReference
{
    /// <summary>Type of reference.</summary>
    public ReferenceType ReferenceType { get; set; }

    /// <summary>Location where reference was found.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Description of the reference.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Type of plugin reference.
/// </summary>
public enum ReferenceType
{
    /// <summary>Reference in plugin registration.</summary>
    Registration,

    /// <summary>Reference in configuration file.</summary>
    Configuration,

    /// <summary>Reference in code.</summary>
    Code,

    /// <summary>Reference in documentation.</summary>
    Documentation,

    /// <summary>Other type of reference.</summary>
    Other
}

/// <summary>
/// Priority level for plugin removal.
/// </summary>
public enum RemovalPriority
{
    /// <summary>Low priority - can be delayed if needed.</summary>
    Low = 1,

    /// <summary>Medium priority - should be removed on schedule.</summary>
    Medium = 2,

    /// <summary>High priority - must be removed as soon as possible.</summary>
    High = 3,

    /// <summary>Critical priority - immediate removal required.</summary>
    Critical = 4
}

#endregion
