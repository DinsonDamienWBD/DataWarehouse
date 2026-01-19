using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// Service for discovering and managing plugins dynamically.
/// </summary>
public interface IPluginDiscoveryService
{
    /// <summary>
    /// Gets all discovered plugins.
    /// </summary>
    IEnumerable<PluginInfo> GetAllPlugins();

    /// <summary>
    /// Gets plugins by category.
    /// </summary>
    IEnumerable<PluginInfo> GetPluginsByCategory(PluginCategory category);

    /// <summary>
    /// Gets a specific plugin by ID.
    /// </summary>
    PluginInfo? GetPlugin(string pluginId);

    /// <summary>
    /// Gets active (loaded) plugins.
    /// </summary>
    IEnumerable<PluginInfo> GetActivePlugins();

    /// <summary>
    /// Gets inactive (available but not loaded) plugins.
    /// </summary>
    IEnumerable<PluginInfo> GetInactivePlugins();

    /// <summary>
    /// Enables a plugin.
    /// </summary>
    Task<bool> EnablePluginAsync(string pluginId);

    /// <summary>
    /// Disables a plugin.
    /// </summary>
    Task<bool> DisablePluginAsync(string pluginId);

    /// <summary>
    /// Gets plugin configuration schema.
    /// </summary>
    PluginConfigSchema? GetPluginConfigSchema(string pluginId);

    /// <summary>
    /// Updates plugin configuration.
    /// </summary>
    Task<bool> UpdatePluginConfigAsync(string pluginId, Dictionary<string, object> config);

    /// <summary>
    /// Refreshes plugin discovery.
    /// </summary>
    Task RefreshAsync();

    /// <summary>
    /// Event raised when plugins change.
    /// </summary>
    event EventHandler<PluginChangedEventArgs>? PluginsChanged;
}

/// <summary>
/// Information about a discovered plugin.
/// </summary>
public class PluginInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public PluginCategory Category { get; set; }
    public bool IsActive { get; set; }
    public bool IsHealthy { get; set; }
    public DateTime? LastActivity { get; set; }
    public List<PluginCapabilityDescriptor> Capabilities { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
    public Dictionary<string, object> CurrentConfig { get; set; } = new();
    public string? AssemblyPath { get; set; }
    public PluginReadyState ReadyState { get; set; }
}

/// <summary>
/// Schema for plugin configuration.
/// </summary>
public class PluginConfigSchema
{
    public string PluginId { get; set; } = string.Empty;
    public List<ConfigProperty> Properties { get; set; } = new();
}

/// <summary>
/// Configuration property definition.
/// </summary>
public class ConfigProperty
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = "string"; // string, number, boolean, array, object
    public object? DefaultValue { get; set; }
    public bool Required { get; set; }
    public string? ValidationPattern { get; set; }
    public object? MinValue { get; set; }
    public object? MaxValue { get; set; }
    public List<string>? AllowedValues { get; set; }
}

/// <summary>
/// Event args for plugin changes.
/// </summary>
public class PluginChangedEventArgs : EventArgs
{
    public string PluginId { get; set; } = string.Empty;
    public PluginChangeType ChangeType { get; set; }
    public PluginInfo? Plugin { get; set; }
}

public enum PluginChangeType
{
    Discovered,
    Enabled,
    Disabled,
    ConfigUpdated,
    HealthChanged,
    Removed
}

/// <summary>
/// Implementation of plugin discovery service.
/// </summary>
public class PluginDiscoveryService : IPluginDiscoveryService
{
    private readonly ConcurrentDictionary<string, PluginInfo> _plugins = new();
    private readonly ConcurrentDictionary<string, IPluginBase> _activeInstances = new();
    private readonly ILogger<PluginDiscoveryService> _logger;
    private readonly string _pluginsDirectory;

    public event EventHandler<PluginChangedEventArgs>? PluginsChanged;

    public PluginDiscoveryService(ILogger<PluginDiscoveryService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _pluginsDirectory = configuration.GetValue<string>("PluginsDirectory")
            ?? Path.Combine(AppContext.BaseDirectory, "Plugins");

        // Initial discovery
        _ = RefreshAsync();
    }

    public IEnumerable<PluginInfo> GetAllPlugins() => _plugins.Values.OrderBy(p => p.Category).ThenBy(p => p.Name);

    public IEnumerable<PluginInfo> GetPluginsByCategory(PluginCategory category) =>
        _plugins.Values.Where(p => p.Category == category).OrderBy(p => p.Name);

    public PluginInfo? GetPlugin(string pluginId) =>
        _plugins.TryGetValue(pluginId, out var plugin) ? plugin : null;

    public IEnumerable<PluginInfo> GetActivePlugins() =>
        _plugins.Values.Where(p => p.IsActive).OrderBy(p => p.Name);

    public IEnumerable<PluginInfo> GetInactivePlugins() =>
        _plugins.Values.Where(p => !p.IsActive).OrderBy(p => p.Name);

    public async Task<bool> EnablePluginAsync(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var plugin))
            return false;

        try
        {
            // Would normally load and initialize the plugin here
            plugin.IsActive = true;
            plugin.ReadyState = PluginReadyState.Ready;

            PluginsChanged?.Invoke(this, new PluginChangedEventArgs
            {
                PluginId = pluginId,
                ChangeType = PluginChangeType.Enabled,
                Plugin = plugin
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable plugin {PluginId}", pluginId);
            return false;
        }
    }

    public async Task<bool> DisablePluginAsync(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var plugin))
            return false;

        try
        {
            plugin.IsActive = false;
            plugin.ReadyState = PluginReadyState.NotReady;

            if (_activeInstances.TryRemove(pluginId, out var instance))
            {
                if (instance is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (instance is IDisposable disposable)
                    disposable.Dispose();
            }

            PluginsChanged?.Invoke(this, new PluginChangedEventArgs
            {
                PluginId = pluginId,
                ChangeType = PluginChangeType.Disabled,
                Plugin = plugin
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable plugin {PluginId}", pluginId);
            return false;
        }
    }

    public PluginConfigSchema? GetPluginConfigSchema(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var plugin))
            return null;

        // Generate schema from plugin metadata
        var schema = new PluginConfigSchema { PluginId = pluginId };

        // Extract configuration properties from metadata
        if (plugin.Metadata.TryGetValue("ConfigProperties", out var configProps) && configProps is List<ConfigProperty> props)
        {
            schema.Properties = props;
        }
        else
        {
            // Generate basic schema from current config
            foreach (var (key, value) in plugin.CurrentConfig)
            {
                schema.Properties.Add(new ConfigProperty
                {
                    Name = key,
                    DisplayName = FormatDisplayName(key),
                    Type = GetJsonType(value),
                    DefaultValue = value
                });
            }
        }

        return schema;
    }

    public async Task<bool> UpdatePluginConfigAsync(string pluginId, Dictionary<string, object> config)
    {
        if (!_plugins.TryGetValue(pluginId, out var plugin))
            return false;

        try
        {
            foreach (var (key, value) in config)
            {
                plugin.CurrentConfig[key] = value;
            }

            PluginsChanged?.Invoke(this, new PluginChangedEventArgs
            {
                PluginId = pluginId,
                ChangeType = PluginChangeType.ConfigUpdated,
                Plugin = plugin
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update plugin config {PluginId}", pluginId);
            return false;
        }
    }

    public async Task RefreshAsync()
    {
        _logger.LogInformation("Discovering plugins...");

        // Discover from known plugin types in loaded assemblies
        var pluginTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.FullName?.Contains("DataWarehouse") == true)
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .Where(t => !t.IsAbstract && typeof(IPluginBase).IsAssignableFrom(t));

        foreach (var pluginType in pluginTypes)
        {
            try
            {
                await DiscoverPluginFromType(pluginType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to discover plugin type {Type}", pluginType.FullName);
            }
        }

        _logger.LogInformation("Discovered {Count} plugins", _plugins.Count);
    }

    private async Task DiscoverPluginFromType(Type pluginType)
    {
        try
        {
            // Try to create instance for metadata extraction
            var instance = Activator.CreateInstance(pluginType) as IPluginBase;
            if (instance == null) return;

            var handshake = await instance.OnHandshakeAsync(new HandshakeRequest
            {
                Config = new Dictionary<string, object>()
            });

            var info = new PluginInfo
            {
                Id = handshake.PluginId,
                Name = handshake.Name,
                Version = $"{handshake.Version.Major}.{handshake.Version.Minor}.{handshake.Version.Patch}",
                Category = handshake.Category,
                IsActive = false,
                IsHealthy = true,
                ReadyState = PluginReadyState.NotReady,
                Capabilities = handshake.Capabilities?.ToList() ?? new(),
                Metadata = handshake.Metadata ?? new(),
                AssemblyPath = pluginType.Assembly.Location
            };

            // Extract description from metadata
            if (info.Metadata.TryGetValue("Description", out var desc))
                info.Description = desc?.ToString() ?? string.Empty;

            _plugins.AddOrUpdate(info.Id, info, (_, _) => info);

            PluginsChanged?.Invoke(this, new PluginChangedEventArgs
            {
                PluginId = info.Id,
                ChangeType = PluginChangeType.Discovered,
                Plugin = info
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract metadata from {Type}", pluginType.Name);
        }
    }

    private static string FormatDisplayName(string name)
    {
        // Convert camelCase/PascalCase to display name
        var result = System.Text.RegularExpressions.Regex.Replace(name, "(\\B[A-Z])", " $1");
        return char.ToUpper(result[0]) + result[1..];
    }

    private static string GetJsonType(object? value) => value switch
    {
        null => "string",
        bool => "boolean",
        int or long or float or double or decimal => "number",
        string => "string",
        Array or System.Collections.IList => "array",
        _ => "object"
    };
}
