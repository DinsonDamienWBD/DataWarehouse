using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Configuration;

// ============================================================================
// FEATURE TOGGLE REGISTRY
// Runtime feature toggle management with hierarchy-aware evaluation.
// Supports per-feature toggles, percentage rollout, and toggle change events.
// ============================================================================

#region Toggle Definition

/// <summary>
/// Defines a feature toggle with its default state and metadata.
/// </summary>
public sealed record FeatureToggleDefinition
{
    /// <summary>
    /// Unique feature toggle name (e.g., "intelligence.enabled", "compression.enabled").
    /// </summary>
    public required string FeatureName { get; init; }

    /// <summary>
    /// Whether the feature is enabled by default.
    /// </summary>
    public bool DefaultEnabled { get; init; }

    /// <summary>
    /// Human-readable description of the toggle.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Category for grouping (e.g., "Intelligence", "Security", "Storage").
    /// </summary>
    public string Category { get; init; } = "General";

    /// <summary>
    /// When this toggle was registered.
    /// </summary>
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Percentage rollout (0-100). Null means binary toggle (on/off).
    /// When set, the toggle is enabled for this percentage of users based on userId hash.
    /// </summary>
    public int? RolloutPercentage { get; init; }

    /// <summary>
    /// Whether this toggle requires admin privileges to change.
    /// </summary>
    public bool RequiresAdmin { get; init; }

    /// <summary>
    /// Tags for discovery and filtering.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Snapshot of a feature toggle's current state.
/// </summary>
public sealed record FeatureToggleState
{
    public required string FeatureName { get; init; }
    public bool IsEnabled { get; init; }
    public ConfigurationLevel EffectiveLevel { get; init; }
    public int? RolloutPercentage { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = "General";
    public DateTimeOffset LastChanged { get; init; } = DateTimeOffset.UtcNow;
}

#endregion

#region IFeatureToggleRegistry

/// <summary>
/// Central registry for runtime feature toggles.
/// Integrates with ConfigurationHierarchy for per-tenant/per-user evaluation.
/// </summary>
public interface IFeatureToggleRegistry
{
    /// <summary>
    /// Checks whether a feature is enabled for the given context.
    /// Considers hierarchy (User > Tenant > Instance > Default) and percentage rollout.
    /// </summary>
    bool IsEnabled(string featureName, string? tenantId = null, string? userId = null);

    /// <summary>
    /// Registers a new feature toggle with its default state.
    /// </summary>
    void RegisterToggle(string featureName, bool defaultEnabled, string description);

    /// <summary>
    /// Registers a toggle definition with full metadata.
    /// </summary>
    void RegisterToggle(FeatureToggleDefinition definition);

    /// <summary>
    /// Sets a toggle at the specified hierarchy level.
    /// </summary>
    void SetToggle(string featureName, bool enabled, ConfigurationLevel level = ConfigurationLevel.Instance, string? scope = null);

    /// <summary>
    /// Sets a percentage rollout for a toggle.
    /// The toggle will be enabled for the specified percentage of users.
    /// </summary>
    void SetRolloutPercentage(string featureName, int percentage, ConfigurationLevel level = ConfigurationLevel.Instance, string? scope = null);

    /// <summary>
    /// Gets all registered toggle definitions.
    /// </summary>
    IReadOnlyList<FeatureToggleDefinition> GetAllToggles();

    /// <summary>
    /// Gets the current state of a specific toggle.
    /// </summary>
    FeatureToggleState? GetToggleState(string featureName, string? tenantId = null, string? userId = null);

    /// <summary>
    /// Gets all toggle states for the given context.
    /// </summary>
    IReadOnlyList<FeatureToggleState> GetAllToggleStates(string? tenantId = null, string? userId = null);

    /// <summary>
    /// Removes a toggle registration.
    /// </summary>
    bool UnregisterToggle(string featureName);

    /// <summary>
    /// Gets toggles by category.
    /// </summary>
    IReadOnlyList<FeatureToggleDefinition> GetTogglesByCategory(string category);
}

#endregion

#region Implementation

/// <summary>
/// Production implementation of the feature toggle registry.
/// Uses ConfigurationHierarchy for hierarchy-aware toggle evaluation.
/// Publishes "config.toggle.changed" events via message bus.
/// </summary>
public sealed class FeatureToggleRegistry : IFeatureToggleRegistry
{
    private readonly ConfigurationHierarchy _hierarchy;
    private readonly IMessageBus? _messageBus;
    private readonly BoundedDictionary<string, FeatureToggleDefinition> _toggles = new BoundedDictionary<string, FeatureToggleDefinition>(1000);
    private readonly BoundedDictionary<string, DateTimeOffset> _lastChanged = new BoundedDictionary<string, DateTimeOffset>(1000);

    /// <summary>
    /// Creates a feature toggle registry integrated with configuration hierarchy.
    /// </summary>
    /// <param name="hierarchy">Configuration hierarchy for level-aware resolution.</param>
    /// <param name="messageBus">Optional message bus for change notifications.</param>
    public FeatureToggleRegistry(ConfigurationHierarchy hierarchy, IMessageBus? messageBus = null)
    {
        _hierarchy = hierarchy ?? throw new ArgumentNullException(nameof(hierarchy));
        _messageBus = messageBus;
        RegisterBuiltInToggles();
    }

    /// <inheritdoc/>
    public bool IsEnabled(string featureName, string? tenantId = null, string? userId = null)
    {
        if (!_toggles.TryGetValue(featureName, out var definition))
            return false; // Unknown toggles are off by default

        // Check hierarchy for explicit toggle value
        var toggleKey = $"toggle.{featureName}";
        var explicitValue = _hierarchy.GetEffectiveValue<object>(toggleKey, tenantId, userId);

        if (explicitValue is not null)
        {
            if (explicitValue is bool boolVal)
                return boolVal;
            if (bool.TryParse(explicitValue.ToString(), out var parsed))
                return parsed;
        }

        // Check for percentage rollout
        var rolloutKey = $"toggle.{featureName}.rollout";
        var rolloutValue = _hierarchy.GetEffectiveValue<object>(rolloutKey, tenantId, userId);
        int? rolloutPercentage = null;

        if (rolloutValue is not null)
        {
            if (rolloutValue is int intVal)
                rolloutPercentage = intVal;
            else if (int.TryParse(rolloutValue.ToString(), out var parsedInt))
                rolloutPercentage = parsedInt;
        }

        rolloutPercentage ??= definition.RolloutPercentage;

        if (rolloutPercentage.HasValue && !string.IsNullOrEmpty(userId))
        {
            return IsUserInRollout(featureName, userId, rolloutPercentage.Value);
        }

        // Fall back to default
        return definition.DefaultEnabled;
    }

    /// <inheritdoc/>
    public void RegisterToggle(string featureName, bool defaultEnabled, string description)
    {
        RegisterToggle(new FeatureToggleDefinition
        {
            FeatureName = featureName,
            DefaultEnabled = defaultEnabled,
            Description = description
        });
    }

    /// <inheritdoc/>
    public void RegisterToggle(FeatureToggleDefinition definition)
    {
        _toggles[definition.FeatureName] = definition;
    }

    /// <inheritdoc/>
    public void SetToggle(string featureName, bool enabled, ConfigurationLevel level = ConfigurationLevel.Instance, string? scope = null)
    {
        var toggleKey = $"toggle.{featureName}";
        _hierarchy.SetConfiguration(level, scope ?? string.Empty, toggleKey, enabled);
        _lastChanged[featureName] = DateTimeOffset.UtcNow;

        PublishToggleChanged(featureName, enabled, level, scope);
    }

    /// <inheritdoc/>
    public void SetRolloutPercentage(string featureName, int percentage, ConfigurationLevel level = ConfigurationLevel.Instance, string? scope = null)
    {
        if (percentage < 0 || percentage > 100)
            throw new ArgumentOutOfRangeException(nameof(percentage), "Rollout percentage must be between 0 and 100.");

        var rolloutKey = $"toggle.{featureName}.rollout";
        _hierarchy.SetConfiguration(level, scope ?? string.Empty, rolloutKey, percentage);
        _lastChanged[featureName] = DateTimeOffset.UtcNow;

        // Update definition if it exists
        if (_toggles.TryGetValue(featureName, out var existing))
        {
            _toggles[featureName] = existing with { RolloutPercentage = percentage };
        }

        PublishToggleChanged(featureName, percentage > 0, level, scope);
    }

    /// <inheritdoc/>
    public IReadOnlyList<FeatureToggleDefinition> GetAllToggles() =>
        _toggles.Values.ToList();

    /// <inheritdoc/>
    public FeatureToggleState? GetToggleState(string featureName, string? tenantId = null, string? userId = null)
    {
        if (!_toggles.TryGetValue(featureName, out var definition))
            return null;

        var isEnabled = IsEnabled(featureName, tenantId, userId);
        var effectiveLevel = _hierarchy.GetEffectiveLevel($"toggle.{featureName}", tenantId, userId)
            ?? ConfigurationLevel.Instance;

        _lastChanged.TryGetValue(featureName, out var lastChanged);

        return new FeatureToggleState
        {
            FeatureName = featureName,
            IsEnabled = isEnabled,
            EffectiveLevel = effectiveLevel,
            RolloutPercentage = definition.RolloutPercentage,
            Description = definition.Description,
            Category = definition.Category,
            LastChanged = lastChanged != default ? lastChanged : definition.RegisteredAt
        };
    }

    /// <inheritdoc/>
    public IReadOnlyList<FeatureToggleState> GetAllToggleStates(string? tenantId = null, string? userId = null)
    {
        return _toggles.Keys
            .Select(name => GetToggleState(name, tenantId, userId))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
    }

    /// <inheritdoc/>
    public bool UnregisterToggle(string featureName) =>
        _toggles.TryRemove(featureName, out _);

    /// <inheritdoc/>
    public IReadOnlyList<FeatureToggleDefinition> GetTogglesByCategory(string category) =>
        _toggles.Values.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();

    #region Private Helpers

    private void RegisterBuiltInToggles()
    {
        RegisterToggle(new FeatureToggleDefinition
        {
            FeatureName = "intelligence.enabled",
            DefaultEnabled = true,
            Description = "Enables/disables AI intelligence features across the system.",
            Category = "Intelligence"
        });

        RegisterToggle(new FeatureToggleDefinition
        {
            FeatureName = "encryption.at_rest",
            DefaultEnabled = true,
            Description = "Enables/disables encryption for data at rest.",
            Category = "Security"
        });

        RegisterToggle(new FeatureToggleDefinition
        {
            FeatureName = "compression.enabled",
            DefaultEnabled = true,
            Description = "Enables/disables data compression.",
            Category = "Storage"
        });

        RegisterToggle(new FeatureToggleDefinition
        {
            FeatureName = "replication.enabled",
            DefaultEnabled = false,
            Description = "Enables/disables data replication. Requires explicit opt-in.",
            Category = "Storage",
            RequiresAdmin = true
        });

        RegisterToggle(new FeatureToggleDefinition
        {
            FeatureName = "telemetry.enabled",
            DefaultEnabled = true,
            Description = "Enables/disables telemetry and observability data collection.",
            Category = "Observability"
        });

        RegisterToggle(new FeatureToggleDefinition
        {
            FeatureName = "experimental.features",
            DefaultEnabled = false,
            Description = "Enables experimental features. Use with caution in production.",
            Category = "General",
            RequiresAdmin = true
        });
    }

    private static bool IsUserInRollout(string featureName, string userId, int percentage)
    {
        if (percentage >= 100) return true;
        if (percentage <= 0) return false;

        // Deterministic hash: same user+feature always gets the same result
        var input = $"{featureName}:{userId}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hashValue = Math.Abs(BitConverter.ToInt32(hashBytes, 0)) % 100;
        return hashValue < percentage;
    }

    private void PublishToggleChanged(string featureName, bool enabled, ConfigurationLevel level, string? scope)
    {
        if (_messageBus is null)
            return;

        var message = new PluginMessage
        {
            Type = "config.toggle.changed",
            SourcePluginId = "system.configuration",
            Source = "FeatureToggleRegistry",
            Payload = new Dictionary<string, object>
            {
                ["featureName"] = featureName,
                ["enabled"] = enabled,
                ["level"] = level.ToString(),
                ["scope"] = scope ?? string.Empty,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };

        // Fire-and-forget toggle change notification
        _ = _messageBus.PublishAsync("config.toggle.changed", message);
    }

    #endregion
}

#endregion
