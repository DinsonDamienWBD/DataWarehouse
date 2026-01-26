using DataWarehouse.Shared.Models;

namespace DataWarehouse.Shared;

/// <summary>
/// Manages instance capabilities and provides methods to query available features
/// </summary>
public class CapabilityManager
{
    private InstanceCapabilities? _capabilities;

    /// <summary>
    /// Event raised when the capabilities change (e.g., when switching instances)
    /// </summary>
    public event EventHandler<InstanceCapabilities>? CapabilitiesChanged;

    /// <summary>
    /// Gets the current instance capabilities
    /// </summary>
    public InstanceCapabilities? Capabilities => _capabilities;

    /// <summary>
    /// Updates the current capabilities
    /// </summary>
    /// <param name="capabilities">New capabilities to set</param>
    public void UpdateCapabilities(InstanceCapabilities capabilities)
    {
        _capabilities = capabilities;
        CapabilitiesChanged?.Invoke(this, capabilities);
    }

    /// <summary>
    /// Checks if the instance has a specific feature
    /// </summary>
    /// <param name="featureName">Name of the feature to check (e.g., "encryption", "compression")</param>
    /// <returns>True if the feature is available</returns>
    public bool HasFeature(string featureName)
    {
        if (_capabilities == null)
            return false;

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

        return new Dictionary<string, bool>
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
    }
}
