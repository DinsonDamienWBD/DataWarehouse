using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Configuration;

// ============================================================================
// USER CONFIGURATION SYSTEM
// Central facade for user-facing configuration:
// - Strategy selection per plugin
// - Parameter tuning with validation
// - Configuration export/import
// - Discoverable configurable features
// ============================================================================

#region Supporting Types

/// <summary>
/// A discovered configurable feature from a loaded plugin.
/// </summary>
public sealed record ConfigurableFeature
{
    /// <summary>
    /// Plugin that provides this feature.
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// Human-readable feature name.
    /// </summary>
    public required string FeatureName { get; init; }

    /// <summary>
    /// Description of the feature.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// All configurable parameters for this feature.
    /// </summary>
    public required IReadOnlyList<ConfigurableParameter> Parameters { get; init; }

    /// <summary>
    /// Category for UI grouping.
    /// </summary>
    public string Category { get; init; } = "General";

    /// <summary>
    /// The IUserOverridable instance that backs this feature (if any).
    /// </summary>
    [JsonIgnore]
    public IUserOverridable? OverridableInstance { get; init; }
}

/// <summary>
/// Complete configuration for a feature considering hierarchy.
/// </summary>
public sealed record FeatureConfiguration
{
    /// <summary>
    /// Plugin ID.
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// All effective parameter values.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> EffectiveValues { get; init; }

    /// <summary>
    /// Which level each value comes from.
    /// </summary>
    public required IReadOnlyDictionary<string, ConfigurationLevel> ValueSources { get; init; }

    /// <summary>
    /// Parameter definitions for each value.
    /// </summary>
    public required IReadOnlyList<ConfigurableParameter> Parameters { get; init; }
}

/// <summary>
/// Portable configuration snapshot for export/import.
/// </summary>
public sealed record ConfigurationSnapshot
{
    public string Version { get; init; } = "1.0";
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    public ConfigurationLevel Level { get; init; }
    public string Scope { get; init; } = string.Empty;
    public Dictionary<string, object?> Values { get; init; } = new();
}

#endregion

#region UserConfigurationSystem

/// <summary>
/// Central configuration facade that ties together:
/// - IPluginCapabilityRegistry (discover what's configurable)
/// - ConfigurationHierarchy (resolve effective values)
/// - IMessageBus (publish change notifications)
///
/// Enables strategy selection, parameter tuning, and per-tenant/per-user overrides.
/// </summary>
public sealed class UserConfigurationSystem
{
    private readonly IPluginCapabilityRegistry _capabilityRegistry;
    private readonly ConfigurationHierarchy _hierarchy;
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, IUserOverridable> _overridables = new();
    private readonly ConcurrentDictionary<string, ConfigurableFeature> _features = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates the user configuration system.
    /// </summary>
    /// <param name="capabilityRegistry">Plugin capability registry for feature discovery.</param>
    /// <param name="hierarchy">Configuration hierarchy for value resolution.</param>
    /// <param name="messageBus">Optional message bus for change notifications.</param>
    public UserConfigurationSystem(
        IPluginCapabilityRegistry capabilityRegistry,
        ConfigurationHierarchy hierarchy,
        IMessageBus? messageBus = null)
    {
        _capabilityRegistry = capabilityRegistry ?? throw new ArgumentNullException(nameof(capabilityRegistry));
        _hierarchy = hierarchy ?? throw new ArgumentNullException(nameof(hierarchy));
        _messageBus = messageBus;
    }

    #region Feature Registration and Discovery

    /// <summary>
    /// Registers an IUserOverridable instance for a plugin feature.
    /// Called by plugins during initialization.
    /// </summary>
    public void RegisterOverridable(string pluginId, string featureName, IUserOverridable overridable)
    {
        var key = $"{pluginId}.{featureName}";
        _overridables[key] = overridable;

        var parameters = overridable.GetConfigurableParameters();
        _hierarchy.RegisterParameters(parameters);

        _features[key] = new ConfigurableFeature
        {
            PluginId = pluginId,
            FeatureName = featureName,
            Parameters = parameters,
            OverridableInstance = overridable
        };
    }

    /// <summary>
    /// Registers a configurable feature from attribute-decorated properties.
    /// </summary>
    public void RegisterConfigurableType(string pluginId, string featureName, object instance)
    {
        var parameters = DiscoverAttributeParameters(instance.GetType());
        if (parameters.Count == 0)
            return;

        _hierarchy.RegisterParameters(parameters);

        _features[$"{pluginId}.{featureName}"] = new ConfigurableFeature
        {
            PluginId = pluginId,
            FeatureName = featureName,
            Parameters = parameters
        };
    }

    /// <summary>
    /// Discovers all configurable features from registered plugins and strategies.
    /// </summary>
    public IReadOnlyList<ConfigurableFeature> DiscoverConfigurableFeatures()
    {
        return _features.Values.ToList();
    }

    /// <summary>
    /// Gets configurable features for a specific plugin.
    /// </summary>
    public IReadOnlyList<ConfigurableFeature> GetPluginFeatures(string pluginId)
    {
        return _features.Values
            .Where(f => f.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    #endregion

    #region Strategy Selection

    /// <summary>
    /// Applies a strategy selection for a plugin at the specified hierarchy level.
    /// Example: ApplyStrategySelection("compression", "default_algorithm", "brotli-q6", Tenant, "tenant-123")
    /// </summary>
    /// <param name="pluginId">Plugin ID.</param>
    /// <param name="strategyCategory">Strategy category (e.g., "default_algorithm").</param>
    /// <param name="strategyId">Selected strategy ID (e.g., "brotli-q6").</param>
    /// <param name="level">Configuration level.</param>
    /// <param name="scope">Optional scope (tenantId or userId).</param>
    public async Task ApplyStrategySelectionAsync(
        string pluginId,
        string strategyCategory,
        string strategyId,
        ConfigurationLevel level = ConfigurationLevel.Instance,
        string? scope = null,
        CancellationToken ct = default)
    {
        var key = $"{pluginId}.strategy.{strategyCategory}";
        await _hierarchy.SetConfigurationAsync(level, scope ?? string.Empty, key, strategyId, ct).ConfigureAwait(false);

        if (_messageBus is not null)
        {
            var message = new PluginMessage
            {
                Type = "config.strategy.changed",
                SourcePluginId = "system.configuration",
                Source = "UserConfigurationSystem",
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = pluginId,
                    ["strategyCategory"] = strategyCategory,
                    ["strategyId"] = strategyId,
                    ["level"] = level.ToString(),
                    ["scope"] = scope ?? string.Empty,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                }
            };
            await _messageBus.PublishAsync("config.strategy.changed", message, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the effective strategy selection for a plugin.
    /// </summary>
    public string? GetEffectiveStrategy(
        string pluginId, string strategyCategory,
        string? tenantId = null, string? userId = null)
    {
        var key = $"{pluginId}.strategy.{strategyCategory}";
        return _hierarchy.GetEffectiveValue<string>(key, tenantId, userId);
    }

    #endregion

    #region Parameter Tuning

    /// <summary>
    /// Applies a parameter value at the specified hierarchy level.
    /// Validates against ConfigurableParameter constraints.
    /// </summary>
    /// <param name="pluginId">Plugin ID.</param>
    /// <param name="parameterName">Parameter name.</param>
    /// <param name="value">New value.</param>
    /// <param name="level">Configuration level.</param>
    /// <param name="scope">Optional scope (tenantId or userId).</param>
    /// <exception cref="ConfigurationValidationException">If value fails validation.</exception>
    public async Task ApplyParameterTuningAsync(
        string pluginId,
        string parameterName,
        object value,
        ConfigurationLevel level = ConfigurationLevel.Instance,
        string? scope = null,
        CancellationToken ct = default)
    {
        // Find parameter definition
        var paramDef = FindParameter(pluginId, parameterName);
        if (paramDef is not null)
        {
            // Validate value
            var errors = ParameterValidator.Validate(paramDef, value);
            if (errors.Count > 0)
                throw new ConfigurationValidationException(parameterName, value, errors);

            // Check override permissions
            if (level == ConfigurationLevel.User && !paramDef.AllowUserToOverride)
                throw new ConfigurationValidationException(parameterName, value,
                    $"Parameter '{parameterName}' does not allow user-level overrides.");

            if (level == ConfigurationLevel.Tenant && !paramDef.AllowTenantToOverride)
                throw new ConfigurationValidationException(parameterName, value,
                    $"Parameter '{parameterName}' does not allow tenant-level overrides.");
        }

        var key = $"{pluginId}.param.{parameterName}";
        await _hierarchy.SetConfigurationAsync(level, scope ?? string.Empty, key, value, ct).ConfigureAwait(false);

        // Notify the IUserOverridable instance if registered
        NotifyOverridable(pluginId, parameterName, value, level, scope);

        if (_messageBus is not null)
        {
            var message = new PluginMessage
            {
                Type = "config.parameter.changed",
                SourcePluginId = "system.configuration",
                Source = "UserConfigurationSystem",
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = pluginId,
                    ["parameterName"] = parameterName,
                    ["value"] = value,
                    ["level"] = level.ToString(),
                    ["scope"] = scope ?? string.Empty,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                }
            };
            await _messageBus.PublishAsync("config.parameter.changed", message, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the effective value for a parameter.
    /// </summary>
    public T? GetEffectiveParameterValue<T>(
        string pluginId, string parameterName,
        string? tenantId = null, string? userId = null)
    {
        var key = $"{pluginId}.param.{parameterName}";
        return _hierarchy.GetEffectiveValue<T>(key, tenantId, userId);
    }

    #endregion

    #region Feature Configuration

    /// <summary>
    /// Gets the complete effective configuration for a plugin considering hierarchy.
    /// </summary>
    public FeatureConfiguration GetFeatureConfiguration(
        string pluginId, string? tenantId = null, string? userId = null)
    {
        var features = GetPluginFeatures(pluginId);
        var effectiveValues = new Dictionary<string, object?>();
        var valueSources = new Dictionary<string, ConfigurationLevel>();
        var allParameters = new List<ConfigurableParameter>();

        foreach (var feature in features)
        {
            allParameters.AddRange(feature.Parameters);

            foreach (var param in feature.Parameters)
            {
                var key = $"{pluginId}.param.{param.Name}";
                var value = _hierarchy.GetEffectiveValue<object>(key, tenantId, userId);
                effectiveValues[param.Name] = value ?? param.DefaultValue;

                var level = _hierarchy.GetEffectiveLevel(key, tenantId, userId);
                if (level.HasValue)
                    valueSources[param.Name] = level.Value;
            }
        }

        return new FeatureConfiguration
        {
            PluginId = pluginId,
            EffectiveValues = effectiveValues,
            ValueSources = valueSources,
            Parameters = allParameters
        };
    }

    #endregion

    #region Import/Export

    /// <summary>
    /// Exports configuration at the specified level/scope as JSON.
    /// </summary>
    public string ExportConfiguration(ConfigurationLevel level, string? scope = null)
    {
        var values = _hierarchy.GetLevelConfiguration(level, scope ?? string.Empty);

        var snapshot = new ConfigurationSnapshot
        {
            Level = level,
            Scope = scope ?? string.Empty,
            Values = new Dictionary<string, object?>(values)
        };

        return JsonSerializer.Serialize(snapshot, _jsonOptions);
    }

    /// <summary>
    /// Imports configuration from JSON at the specified level/scope.
    /// </summary>
    public async Task ImportConfigurationAsync(
        string json,
        ConfigurationLevel level,
        string? scope = null,
        CancellationToken ct = default)
    {
        ConfigurationSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ConfigurationSnapshot>(json, _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid configuration JSON.", ex);
        }

        if (snapshot is null)
            throw new InvalidOperationException("Configuration snapshot is null.");

        foreach (var kvp in snapshot.Values)
        {
            await _hierarchy.SetConfigurationAsync(level, scope ?? string.Empty, kvp.Key, kvp.Value, ct)
                .ConfigureAwait(false);
        }

        if (_messageBus is not null)
        {
            var message = new PluginMessage
            {
                Type = "config.imported",
                SourcePluginId = "system.configuration",
                Source = "UserConfigurationSystem",
                Payload = new Dictionary<string, object>
                {
                    ["level"] = level.ToString(),
                    ["scope"] = scope ?? string.Empty,
                    ["entryCount"] = snapshot.Values.Count,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                }
            };
            await _messageBus.PublishAsync("config.imported", message, ct).ConfigureAwait(false);
        }
    }

    #endregion

    #region Private Helpers

    private ConfigurableParameter? FindParameter(string pluginId, string parameterName)
    {
        return _features.Values
            .Where(f => f.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.Parameters)
            .FirstOrDefault(p => p.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase));
    }

    private void NotifyOverridable(string pluginId, string parameterName, object value,
        ConfigurationLevel level, string? scope)
    {
        // Find any IUserOverridable instances for this plugin and notify them
        foreach (var kvp in _overridables)
        {
            if (kvp.Key.StartsWith($"{pluginId}.", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    kvp.Value.ApplyConfiguration(new Dictionary<string, object>
                    {
                        [parameterName] = value
                    });
                }
                catch
                {
                    // Best-effort notification; errors are logged but don't block configuration
                }
            }
        }
    }

    private static IReadOnlyList<ConfigurableParameter> DiscoverAttributeParameters(Type type)
    {
        var parameters = new List<ConfigurableParameter>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = property.GetCustomAttribute<ConfigurableAttribute>();
            if (attr is null)
                continue;

            var paramType = MapPropertyType(property.PropertyType);
            IReadOnlyList<string>? allowedValues = null;
            if (!string.IsNullOrEmpty(attr.AllowedValues))
                allowedValues = attr.AllowedValues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            parameters.Add(new ConfigurableParameter
            {
                Name = property.Name,
                DisplayName = attr.DisplayName ?? property.Name,
                Description = attr.Description ?? string.Empty,
                ParameterType = paramType,
                AllowUserToOverride = attr.AllowUserToOverride,
                AllowTenantToOverride = attr.AllowTenantToOverride,
                Category = attr.Category,
                MinValue = double.IsNaN(attr.Min) ? null : (object)attr.Min,
                MaxValue = double.IsNaN(attr.Max) ? null : (object)attr.Max,
                AllowedValues = allowedValues,
                RegexPattern = attr.RegexPattern,
                RequiresRestart = attr.RequiresRestart,
                DisplayOrder = attr.DisplayOrder,
                Unit = attr.Unit
            });
        }

        return parameters;
    }

    private static ConfigurableParameterType MapPropertyType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(bool)) return ConfigurableParameterType.Bool;
        if (underlying == typeof(int) || underlying == typeof(short) || underlying == typeof(byte))
            return ConfigurableParameterType.Int;
        if (underlying == typeof(long)) return ConfigurableParameterType.Long;
        if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
            return ConfigurableParameterType.Double;
        if (underlying == typeof(TimeSpan)) return ConfigurableParameterType.Duration;
        if (underlying.IsEnum) return ConfigurableParameterType.Enum;
        if (typeof(IEnumerable<string>).IsAssignableFrom(underlying)) return ConfigurableParameterType.StringList;

        return ConfigurableParameterType.String;
    }

    #endregion
}

#endregion
