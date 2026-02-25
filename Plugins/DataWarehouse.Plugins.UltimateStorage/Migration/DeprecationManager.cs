using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Migration;

/// <summary>
/// Manages deprecation of individual storage plugins and provides forwarding to UltimateStorage.
/// Tracks which plugins are deprecated, emits warnings when deprecated plugins are used,
/// and manages grace periods for migration.
/// </summary>
/// <remarks>
/// This manager implements a graceful deprecation strategy:
/// - Phase 1: Warning - Plugin works but emits deprecation warnings
/// - Phase 2: Grace Period - Plugin still works with prominent warnings
/// - Phase 3: Disabled - Plugin refuses to operate, must migrate
/// - Phase 4: Removed - Plugin no longer available
/// </remarks>
public sealed class DeprecationManager
{
    private readonly BoundedDictionary<string, DeprecationInfo> _deprecatedPlugins = new BoundedDictionary<string, DeprecationInfo>(1000);
    private readonly BoundedDictionary<string, long> _deprecationWarningCounts = new BoundedDictionary<string, long>(1000);
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the DeprecationManager.
    /// Loads the default deprecation schedule.
    /// </summary>
    public DeprecationManager()
    {
        LoadDefaultDeprecationSchedule();
    }

    /// <summary>
    /// Gets all deprecated plugins.
    /// </summary>
    public IReadOnlyCollection<DeprecationInfo> DeprecatedPlugins =>
        _deprecatedPlugins.Values.ToList().AsReadOnly();

    /// <summary>
    /// Checks if a plugin is deprecated.
    /// </summary>
    /// <param name="pluginId">The plugin ID to check.</param>
    /// <returns>True if the plugin is deprecated.</returns>
    public bool IsDeprecated(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _deprecatedPlugins.ContainsKey(pluginId);
    }

    /// <summary>
    /// Gets deprecation information for a plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>Deprecation info if deprecated, null otherwise.</returns>
    public DeprecationInfo? GetDeprecationInfo(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _deprecatedPlugins.TryGetValue(pluginId, out var info) ? info : null;
    }

    /// <summary>
    /// Gets the current deprecation phase for a plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <returns>Current deprecation phase.</returns>
    public DeprecationPhase GetDeprecationPhase(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (!_deprecatedPlugins.TryGetValue(pluginId, out var info))
        {
            return DeprecationPhase.Active; // Not deprecated
        }

        var now = DateTime.UtcNow;

        if (now >= info.RemovalDate)
        {
            return DeprecationPhase.Removed;
        }

        if (now >= info.DisabledDate)
        {
            return DeprecationPhase.Disabled;
        }

        if (now >= info.GracePeriodStart)
        {
            return DeprecationPhase.GracePeriod;
        }

        return DeprecationPhase.Warning;
    }

    /// <summary>
    /// Emits a deprecation warning when a deprecated plugin is used.
    /// </summary>
    /// <param name="pluginId">The deprecated plugin ID.</param>
    /// <param name="context">Optional context for logging.</param>
    /// <returns>Deprecation warning message.</returns>
    public string EmitDeprecationWarning(string pluginId, IKernelContext? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (!_deprecatedPlugins.TryGetValue(pluginId, out var info))
        {
            return string.Empty;
        }

        // Increment warning count
        _deprecationWarningCounts.AddOrUpdate(pluginId, 1, (_, count) => count + 1);

        var phase = GetDeprecationPhase(pluginId);
        var message = FormatDeprecationWarning(info, phase);

        // Log warning if context provided
        context?.LogWarning(message);

        return message;
    }

    /// <summary>
    /// Checks if a plugin should be blocked from execution due to deprecation.
    /// </summary>
    /// <param name="pluginId">The plugin ID to check.</param>
    /// <returns>True if the plugin should be blocked.</returns>
    public bool ShouldBlockExecution(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var phase = GetDeprecationPhase(pluginId);
        return phase is DeprecationPhase.Disabled or DeprecationPhase.Removed;
    }

    /// <summary>
    /// Gets the recommended migration target for a deprecated plugin.
    /// </summary>
    /// <param name="pluginId">The deprecated plugin ID.</param>
    /// <returns>Migration target information.</returns>
    public MigrationTarget? GetMigrationTarget(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (!_deprecatedPlugins.TryGetValue(pluginId, out var info))
        {
            return null;
        }

        return info.MigrationTarget;
    }

    /// <summary>
    /// Provides automatic forwarding from a deprecated plugin to UltimateStorage.
    /// </summary>
    /// <param name="pluginId">The deprecated plugin ID.</param>
    /// <param name="operation">The operation being forwarded.</param>
    /// <param name="args">Operation arguments.</param>
    /// <returns>Forwarding configuration.</returns>
    public ForwardingConfig? GetForwardingConfig(string pluginId, string operation, Dictionary<string, object> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(args);

        if (!_deprecatedPlugins.TryGetValue(pluginId, out var info) || info.MigrationTarget == null)
        {
            return null;
        }

        var config = new ForwardingConfig
        {
            SourcePluginId = pluginId,
            TargetPluginId = "com.datawarehouse.storage.ultimate",
            TargetStrategyId = info.MigrationTarget.StrategyId,
            Operation = operation,
            TransformedArgs = TransformArgumentsForForwarding(pluginId, args)
        };

        return config;
    }

    /// <summary>
    /// Marks a plugin as deprecated with a specified schedule.
    /// </summary>
    /// <param name="pluginId">The plugin ID to deprecate.</param>
    /// <param name="info">Deprecation information.</param>
    public void MarkAsDeprecated(string pluginId, DeprecationInfo info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(info);

        lock (_lock)
        {
            _deprecatedPlugins[pluginId] = info;
        }
    }

    /// <summary>
    /// Gets statistics about deprecation warnings emitted.
    /// </summary>
    /// <returns>Dictionary of plugin ID to warning count.</returns>
    public IReadOnlyDictionary<string, long> GetWarningStatistics()
    {
        return new Dictionary<string, long>(_deprecationWarningCounts);
    }

    /// <summary>
    /// Generates a deprecation report for all deprecated plugins.
    /// </summary>
    /// <returns>Formatted deprecation report.</returns>
    public string GenerateDeprecationReport()
    {
        var report = new System.Text.StringBuilder();

        report.AppendLine("# Deprecation Report");
        report.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        report.AppendLine();

        foreach (var (pluginId, info) in _deprecatedPlugins.OrderBy(kvp => kvp.Value.DeprecationDate))
        {
            var phase = GetDeprecationPhase(pluginId);
            var warningCount = _deprecationWarningCounts.TryGetValue(pluginId, out var count) ? count : 0;

            report.AppendLine($"## {pluginId}");
            report.AppendLine($"**Status**: {phase}");
            report.AppendLine($"**Deprecated**: {info.DeprecationDate:yyyy-MM-dd}");
            report.AppendLine($"**Grace Period Starts**: {info.GracePeriodStart:yyyy-MM-dd}");
            report.AppendLine($"**Disabled**: {info.DisabledDate:yyyy-MM-dd}");
            report.AppendLine($"**Removed**: {info.RemovalDate:yyyy-MM-dd}");
            report.AppendLine($"**Warnings Emitted**: {warningCount}");
            report.AppendLine($"**Reason**: {info.Reason}");

            if (info.MigrationTarget != null)
            {
                report.AppendLine($"**Migration Target**: {info.MigrationTarget.StrategyId}");
            }

            report.AppendLine();
        }

        return report.ToString();
    }

    #region Private Helper Methods

    /// <summary>
    /// Loads the default deprecation schedule for known plugins.
    /// </summary>
    private void LoadDefaultDeprecationSchedule()
    {
        var baseDate = new DateTime(2026, 2, 5);

        // FTP - deprecated due to security concerns
        MarkAsDeprecated("com.datawarehouse.storage.ftp", new DeprecationInfo
        {
            PluginId = "com.datawarehouse.storage.ftp",
            DeprecationDate = baseDate,
            GracePeriodStart = baseDate.AddMonths(3),
            DisabledDate = baseDate.AddMonths(6),
            RemovalDate = baseDate.AddMonths(12),
            Reason = "FTP is insecure. Migrate to SFTP strategy.",
            MigrationTarget = new MigrationTarget
            {
                TargetType = "UltimateStorage",
                StrategyId = "sftp",
                MigrationGuideUrl = "https://docs.datawarehouse.com/migration/ftp-to-sftp"
            }
        });

        // Tape storage - deprecated due to low usage
        MarkAsDeprecated("com.datawarehouse.storage.tape", new DeprecationInfo
        {
            PluginId = "com.datawarehouse.storage.tape",
            DeprecationDate = baseDate,
            GracePeriodStart = baseDate.AddMonths(6),
            DisabledDate = baseDate.AddMonths(12),
            RemovalDate = baseDate.AddMonths(18),
            Reason = "Low usage. Migrate to tape-lto strategy if still needed.",
            MigrationTarget = new MigrationTarget
            {
                TargetType = "UltimateStorage",
                StrategyId = "tape-lto",
                MigrationGuideUrl = "https://docs.datawarehouse.com/migration/tape"
            }
        });

        // Add more deprecated plugins as needed
    }

    /// <summary>
    /// Formats a deprecation warning message based on the current phase.
    /// </summary>
    private static string FormatDeprecationWarning(DeprecationInfo info, DeprecationPhase phase)
    {
        return phase switch
        {
            DeprecationPhase.Warning =>
                $"[DEPRECATION WARNING] Plugin '{info.PluginId}' is deprecated. " +
                $"Reason: {info.Reason} " +
                $"Please migrate to UltimateStorage strategy '{info.MigrationTarget?.StrategyId}' before {info.GracePeriodStart:yyyy-MM-dd}.",

            DeprecationPhase.GracePeriod =>
                $"[DEPRECATION - GRACE PERIOD] Plugin '{info.PluginId}' is in grace period. " +
                $"Reason: {info.Reason} " +
                $"This plugin will be disabled on {info.DisabledDate:yyyy-MM-dd}. " +
                $"URGENT: Migrate to UltimateStorage strategy '{info.MigrationTarget?.StrategyId}' immediately.",

            DeprecationPhase.Disabled =>
                $"[DEPRECATION - DISABLED] Plugin '{info.PluginId}' has been disabled. " +
                $"Reason: {info.Reason} " +
                $"You MUST migrate to UltimateStorage strategy '{info.MigrationTarget?.StrategyId}'. " +
                $"This plugin will be removed on {info.RemovalDate:yyyy-MM-dd}.",

            DeprecationPhase.Removed =>
                $"[DEPRECATION - REMOVED] Plugin '{info.PluginId}' has been removed. " +
                $"Use UltimateStorage strategy '{info.MigrationTarget?.StrategyId}' instead.",

            _ => string.Empty
        };
    }

    /// <summary>
    /// Transforms arguments from legacy plugin format to UltimateStorage format.
    /// </summary>
    private static Dictionary<string, object> TransformArgumentsForForwarding(
        string pluginId,
        Dictionary<string, object> args)
    {
        var transformed = new Dictionary<string, object>(args, StringComparer.OrdinalIgnoreCase);

        // Apply plugin-specific transformations
        var mappings = MigrationGuide.MapConfigurationKeys(pluginId, args);
        foreach (var (key, value) in mappings)
        {
            transformed[key] = value;
        }

        return transformed;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Information about a deprecated plugin.
/// </summary>
public sealed class DeprecationInfo
{
    /// <summary>Plugin unique identifier.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>Date when deprecation was announced.</summary>
    public DateTime DeprecationDate { get; set; }

    /// <summary>Date when grace period starts (more prominent warnings).</summary>
    public DateTime GracePeriodStart { get; set; }

    /// <summary>Date when plugin will be disabled.</summary>
    public DateTime DisabledDate { get; set; }

    /// <summary>Date when plugin will be completely removed.</summary>
    public DateTime RemovalDate { get; set; }

    /// <summary>Reason for deprecation.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Migration target information.</summary>
    public MigrationTarget? MigrationTarget { get; set; }
}

/// <summary>
/// Migration target information for a deprecated plugin.
/// </summary>
public sealed class MigrationTarget
{
    /// <summary>Type of migration target (e.g., "UltimateStorage").</summary>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>Target strategy ID in UltimateStorage.</summary>
    public string StrategyId { get; set; } = string.Empty;

    /// <summary>URL to migration guide documentation.</summary>
    public string MigrationGuideUrl { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for forwarding operations from deprecated plugin to UltimateStorage.
/// </summary>
public sealed class ForwardingConfig
{
    /// <summary>Source plugin ID (deprecated).</summary>
    public string SourcePluginId { get; set; } = string.Empty;

    /// <summary>Target plugin ID (UltimateStorage).</summary>
    public string TargetPluginId { get; set; } = string.Empty;

    /// <summary>Target strategy ID.</summary>
    public string TargetStrategyId { get; set; } = string.Empty;

    /// <summary>Operation being forwarded.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Transformed arguments for target plugin.</summary>
    public Dictionary<string, object> TransformedArgs { get; set; } = new();
}

/// <summary>
/// Deprecation lifecycle phases.
/// </summary>
public enum DeprecationPhase
{
    /// <summary>Plugin is active and not deprecated.</summary>
    Active = 0,

    /// <summary>Plugin emits deprecation warnings but works normally.</summary>
    Warning = 1,

    /// <summary>Plugin is in grace period with prominent warnings.</summary>
    GracePeriod = 2,

    /// <summary>Plugin is disabled and refuses to operate.</summary>
    Disabled = 3,

    /// <summary>Plugin has been completely removed.</summary>
    Removed = 4
}

#endregion
