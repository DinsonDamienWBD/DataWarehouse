using DataWarehouse.Shared.Models;
using System.Text.Json;

namespace DataWarehouse.Shared;

/// <summary>
/// Manages instance capabilities and provides methods to query available features.
/// Supports both legacy bool-based feature flags and dynamic HashSet-based features
/// for runtime plugin capability registration.
/// </summary>
public class CapabilityManager
{
    private readonly InstanceManager? _instanceManager;
    private InstanceCapabilities? _capabilities;
    private readonly HashSet<string> _dynamicFeatures = new(StringComparer.OrdinalIgnoreCase);
    private DynamicCommandRegistry? _dynamicRegistry;

    /// <summary>
    /// Event raised when the capabilities change (e.g., when switching instances)
    /// </summary>
    public event EventHandler<InstanceCapabilities>? CapabilitiesChanged;

    /// <summary>
    /// Creates a new CapabilityManager.
    /// </summary>
    public CapabilityManager()
    {
    }

    /// <summary>
    /// Creates a new CapabilityManager with an InstanceManager.
    /// </summary>
    /// <param name="instanceManager">The instance manager to use for refreshing capabilities.</param>
    public CapabilityManager(InstanceManager instanceManager)
    {
        _instanceManager = instanceManager;
    }

    /// <summary>
    /// Gets the current instance capabilities
    /// </summary>
    public InstanceCapabilities? Capabilities => _capabilities;

    /// <summary>
    /// Sets the DynamicCommandRegistry for bidirectional feature/command synchronization.
    /// When set, feature changes also notify the registry.
    /// </summary>
    /// <param name="registry">The dynamic command registry to link.</param>
    public void SetDynamicRegistry(DynamicCommandRegistry registry)
    {
        _dynamicRegistry = registry;
    }

    /// <summary>
    /// Updates the current capabilities, including populating dynamic features.
    /// </summary>
    /// <param name="capabilities">New capabilities to set</param>
    public void UpdateCapabilities(InstanceCapabilities capabilities)
    {
        _capabilities = capabilities;

        // Populate _dynamicFeatures from the DynamicFeatures property
        if (capabilities.DynamicFeatures.Count > 0)
        {
            foreach (var feature in capabilities.DynamicFeatures)
            {
                _dynamicFeatures.Add(feature);
            }
        }

        CapabilitiesChanged?.Invoke(this, capabilities);
    }

    /// <summary>
    /// Checks if the instance has a specific feature.
    /// Checks dynamic features first, then falls back to legacy hardcoded switch.
    /// </summary>
    /// <param name="featureName">Name of the feature to check (e.g., "encryption", "compression")</param>
    /// <returns>True if the feature is available</returns>
    public bool HasFeature(string featureName)
    {
        // Check dynamic features first
        if (_dynamicFeatures.Contains(featureName))
            return true;

        if (_capabilities == null)
            return false;

        // Legacy: hardcoded switch for backward compatibility
        return featureName.ToLowerInvariant() switch
        {
            "encryption" => _capabilities.SupportsEncryption,
            "compression" => _capabilities.SupportsCompression,
            "metadata" => _capabilities.SupportsMetadata,
            "versioning" => _capabilities.SupportsVersioning,
            "deduplication" => _capabilities.SupportsDeduplication,
            "raid" => _capabilities.SupportsRaid,
            "replication" => _capabilities.SupportsReplication,
            "backup" => _capabilities.SupportsBackup,
            "tiering" => _capabilities.SupportsTiering,
            "search" => _capabilities.SupportsSearch,
            _ => false
        };
    }

    /// <summary>
    /// Registers a dynamic feature at runtime.
    /// </summary>
    /// <param name="featureName">The feature name to register.</param>
    public void RegisterFeature(string featureName)
    {
        if (_dynamicFeatures.Add(featureName))
        {
            if (_capabilities != null)
            {
                _capabilities.DynamicFeatures.Add(featureName);
                CapabilitiesChanged?.Invoke(this, _capabilities);
            }
        }
    }

    /// <summary>
    /// Unregisters a dynamic feature at runtime.
    /// </summary>
    /// <param name="featureName">The feature name to unregister.</param>
    public void UnregisterFeature(string featureName)
    {
        if (_dynamicFeatures.Remove(featureName))
        {
            _capabilities?.DynamicFeatures.Remove(featureName);
            if (_capabilities != null)
            {
                CapabilitiesChanged?.Invoke(this, _capabilities);
            }
        }
    }

    /// <summary>
    /// Gets all features available, combining dynamic features and legacy hardcoded features.
    /// </summary>
    /// <returns>Set of all available feature names.</returns>
    public HashSet<string> GetAllFeatures()
    {
        var features = new HashSet<string>(_dynamicFeatures, StringComparer.OrdinalIgnoreCase);

        if (_capabilities != null)
        {
            if (_capabilities.SupportsEncryption) features.Add("encryption");
            if (_capabilities.SupportsCompression) features.Add("compression");
            if (_capabilities.SupportsMetadata) features.Add("metadata");
            if (_capabilities.SupportsVersioning) features.Add("versioning");
            if (_capabilities.SupportsDeduplication) features.Add("deduplication");
            if (_capabilities.SupportsRaid) features.Add("raid");
            if (_capabilities.SupportsReplication) features.Add("replication");
            if (_capabilities.SupportsBackup) features.Add("backup");
            if (_capabilities.SupportsTiering) features.Add("tiering");
            if (_capabilities.SupportsSearch) features.Add("search");
        }

        return features;
    }

    /// <summary>
    /// Checks if the instance has a specific plugin loaded
    /// </summary>
    /// <param name="pluginName">Name of the plugin to check</param>
    /// <returns>True if the plugin is loaded</returns>
    public bool HasPlugin(string pluginName)
    {
        if (_capabilities == null)
            return false;

        return _capabilities.LoadedPlugins.Any(p =>
            p.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a list of all available command categories based on current capabilities
    /// </summary>
    /// <returns>List of available command categories</returns>
    public List<string> GetAvailableCommands()
    {
        if (_capabilities == null)
            return new List<string>();

        var commands = new List<string> { "core", "storage" };

        if (_capabilities.SupportsEncryption)
            commands.Add("encryption");

        if (_capabilities.SupportsCompression)
            commands.Add("compression");

        if (_capabilities.SupportsMetadata)
            commands.Add("metadata");

        if (_capabilities.SupportsVersioning)
            commands.Add("versioning");

        if (_capabilities.SupportsDeduplication)
            commands.Add("deduplication");

        if (_capabilities.SupportsRaid)
            commands.Add("raid");

        if (_capabilities.SupportsReplication)
            commands.Add("replication");

        if (_capabilities.SupportsBackup)
            commands.Add("backup");

        if (_capabilities.SupportsTiering)
            commands.Add("tiering");

        if (_capabilities.SupportsSearch)
            commands.Add("search");

        return commands;
    }

    /// <summary>
    /// Gets detailed information about current capabilities
    /// </summary>
    /// <returns>Dictionary of feature names to their availability</returns>
    public Dictionary<string, bool> GetCapabilitiesDetails()
    {
        if (_capabilities == null)
            return new Dictionary<string, bool>();

        var details = new Dictionary<string, bool>
        {
            ["encryption"] = _capabilities.SupportsEncryption,
            ["compression"] = _capabilities.SupportsCompression,
            ["metadata"] = _capabilities.SupportsMetadata,
            ["versioning"] = _capabilities.SupportsVersioning,
            ["deduplication"] = _capabilities.SupportsDeduplication,
            ["raid"] = _capabilities.SupportsRaid,
            ["replication"] = _capabilities.SupportsReplication,
            ["backup"] = _capabilities.SupportsBackup,
            ["tiering"] = _capabilities.SupportsTiering,
            ["search"] = _capabilities.SupportsSearch
        };

        // Include dynamic features
        foreach (var feature in _dynamicFeatures)
        {
            details.TryAdd(feature, true);
        }

        return details;
    }

    /// <summary>
    /// Refreshes capabilities from the connected instance.
    /// </summary>
    public async Task RefreshCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (_instanceManager == null || !_instanceManager.IsConnected)
        {
            // Set default capabilities for development mode
            _capabilities = new InstanceCapabilities
            {
                InstanceId = "dev-instance",
                Name = "Development Instance",
                Version = "1.0.0",
                SupportsEncryption = true,
                SupportsCompression = true,
                SupportsMetadata = true,
                SupportsVersioning = true,
                SupportsDeduplication = true,
                SupportsRaid = true,
                SupportsReplication = true,
                SupportsBackup = true,
                SupportsTiering = true,
                SupportsSearch = true,
                LoadedPlugins = new List<string>
                {
                    "local-storage", "s3-storage", "azure-blob",
                    "gzip-compression", "aes-encryption", "raft-consensus",
                    "rest-interface", "grpc-interface", "ai-agents",
                    "access-control", "opentelemetry", "governance"
                }
            };
            return;
        }

        var response = await _instanceManager.ExecuteAsync("system.capabilities", null, cancellationToken);

        if (response?.Data != null && response.Data.TryGetValue("capabilities", out var capData))
        {
            var capJson = JsonSerializer.Serialize(capData);
            var caps = JsonSerializer.Deserialize<InstanceCapabilities>(capJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (caps != null)
            {
                UpdateCapabilities(caps);
            }
        }
    }
}
