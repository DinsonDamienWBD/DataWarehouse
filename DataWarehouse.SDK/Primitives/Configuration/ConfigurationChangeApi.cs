using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// API for runtime configuration changes with AllowUserToOverride enforcement.
/// ENHANCED: Writes changes back to XML file and logs to audit trail.
/// Publishes ConfigurationChanged events via IMessageBus.
/// </summary>
public class ConfigurationChangeApi
{
    private readonly DataWarehouseConfiguration _config;
    private readonly IMessageBus? _messageBus;
    private readonly string? _configFilePath;
    private readonly ConfigurationAuditLog? _auditLog;

    /// <summary>
    /// Creates a new configuration change API.
    /// </summary>
    /// <param name="config">The live configuration object.</param>
    /// <param name="messageBus">Optional message bus for change notifications.</param>
    /// <param name="configFilePath">Optional config file path for write-back persistence.</param>
    /// <param name="auditLog">Optional audit log for change tracking.</param>
    public ConfigurationChangeApi(
        DataWarehouseConfiguration config,
        IMessageBus? messageBus = null,
        string? configFilePath = null,
        ConfigurationAuditLog? auditLog = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _messageBus = messageBus;
        _configFilePath = configFilePath;
        _auditLog = auditLog;
    }

    /// <summary>
    /// Attempts to update a configuration value.
    /// Respects AllowUserToOverride constraints.
    /// ENHANCED: Writes back to file and logs to audit trail.
    /// </summary>
    /// <param name="path">Configuration path (e.g., "Security.EncryptionEnabled").</param>
    /// <param name="newValue">New value to set.</param>
    /// <param name="changedBy">User/system making the change.</param>
    /// <param name="reason">Optional reason for the change.</param>
    /// <returns>True if change was applied; false if blocked by policy.</returns>
    public async Task<bool> TryUpdateConfigurationAsync(string path, object newValue, string changedBy = "System", string? reason = null)
    {
        var (section, property) = ParsePath(path);
        if (section == null || property == null)
            return false;

        // Use reflection to get the configuration section and property
        var sectionObj = GetSectionByName(_config, section);
        if (sectionObj == null)
            return false;

        var propertyInfo = sectionObj.GetType().GetProperty(property);
        if (propertyInfo == null)
            return false;

        var currentValue = propertyInfo.GetValue(sectionObj);

        // Check if value is a ConfigurationItem<T>
        if (currentValue != null && currentValue.GetType().IsGenericType &&
            currentValue.GetType().GetGenericTypeDefinition() == typeof(ConfigurationItem<>))
        {
            var allowOverrideProperty = currentValue.GetType().GetProperty("AllowUserToOverride");
            var allowOverride = (bool)(allowOverrideProperty?.GetValue(currentValue) ?? true);

            if (!allowOverride)
            {
                // Change blocked by policy
                return false;
            }

            // Update the Value property
            var valueProperty = currentValue.GetType().GetProperty("Value");
            var oldValue = valueProperty?.GetValue(currentValue);

            // Convert newValue to the correct type
            var targetType = valueProperty?.PropertyType;
            if (targetType != null)
            {
                var convertedValue = Convert.ChangeType(newValue, targetType);
                valueProperty?.SetValue(currentValue, convertedValue);
            }
            else
            {
                valueProperty?.SetValue(currentValue, newValue);
            }

            // ENHANCED: Write back to file
            if (_configFilePath != null)
            {
                ConfigurationSerializer.SaveToFile(_config, _configFilePath);
            }

            // ENHANCED: Log to audit trail
            if (_auditLog != null)
            {
                await _auditLog.LogChangeAsync(changedBy, path, oldValue, newValue, reason);
            }

            // Publish change event
            await PublishChangeEventAsync(path, oldValue, newValue, changedBy);
            return true;
        }
        else
        {
            // Direct property (not wrapped in ConfigurationItem)
            var oldValue = propertyInfo.GetValue(sectionObj);
            propertyInfo.SetValue(sectionObj, newValue);

            // ENHANCED: Write back to file
            if (_configFilePath != null)
            {
                ConfigurationSerializer.SaveToFile(_config, _configFilePath);
            }

            // ENHANCED: Log to audit trail
            if (_auditLog != null)
            {
                await _auditLog.LogChangeAsync(changedBy, path, oldValue, newValue, reason);
            }

            await PublishChangeEventAsync(path, oldValue, newValue, changedBy);
            return true;
        }
    }

    private async Task PublishChangeEventAsync(string path, object? oldValue, object newValue, string changedBy)
    {
        if (_messageBus != null)
        {
            await _messageBus.PublishAsync(
                MessageTopics.ConfigChanged,
                new PluginMessage
                {
                    Type = "configuration.changed",
                    Payload = new Dictionary<string, object>
                    {
                        ["Path"] = path,
                        ["OldValue"] = oldValue ?? "null",
                        ["NewValue"] = newValue,
                        ["ChangedBy"] = changedBy,
                        ["Timestamp"] = DateTime.UtcNow
                    }
                });
        }
    }

    private static (string? Section, string? Property) ParsePath(string path)
    {
        var parts = path.Split('.');
        if (parts.Length != 2)
            return (null, null);
        return (parts[0], parts[1]);
    }

    private static object? GetSectionByName(DataWarehouseConfiguration config, string sectionName)
    {
        return sectionName switch
        {
            "Security" => config.Security,
            "Storage" => config.Storage,
            "Network" => config.Network,
            "Replication" => config.Replication,
            "Encryption" => config.Encryption,
            "Compression" => config.Compression,
            "Observability" => config.Observability,
            "Compute" => config.Compute,
            "Resilience" => config.Resilience,
            "Deployment" => config.Deployment,
            "DataManagement" => config.DataManagement,
            "MessageBus" => config.MessageBus,
            "Plugins" => config.Plugins,
            _ => null
        };
    }
}
