using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Reflection;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// Service for discovering and managing plugins dynamically.
/// Integrates with Kernel's PluginRegistry when available.
/// </summary>
public interface IPluginDiscoveryService
{
    /// <summary>
    /// Gets all discovered plugins.
    /// </summary>
    IEnumerable<PluginInfo> GetAllPlugins();

    /// <summary>
    /// Gets all discovered plugins (alias).
    /// </summary>
    IEnumerable<PluginInfo> GetDiscoveredPlugins();

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
    PluginConfigurationSchema? GetPluginConfigurationSchema(string pluginId);

    /// <summary>
    /// Updates plugin configuration.
    /// </summary>
    Task<bool> UpdatePluginConfigAsync(string pluginId, Dictionary<string, object> config);

    /// <summary>
    /// Refreshes plugin discovery.
    /// </summary>
    Task RefreshPluginsAsync();

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
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "Storage";
    public string? Author { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsActive { get; set; }
    public bool IsHealthy { get; set; } = true;
    public int InstanceCount { get; set; }
    public DateTime? LastActivity { get; set; }
    public PluginCapabilities? Capabilities { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public Dictionary<string, object> CurrentConfig { get; set; } = new();
    public string? AssemblyPath { get; set; }
}

/// <summary>
/// Plugin capabilities.
/// </summary>
public class PluginCapabilities
{
    public bool SupportsStreaming { get; set; }
    public bool SupportsMultiInstance { get; set; }
    public bool SupportsTransactions { get; set; }
    public bool SupportsCaching { get; set; }
    public bool SupportsIndexing { get; set; }
    public bool SupportsEncryption { get; set; }
    public bool SupportsCompression { get; set; }
    public bool SupportsVersioning { get; set; }
    public List<string> SupportedOperations { get; set; } = new();
}

/// <summary>
/// Schema for plugin configuration.
/// </summary>
public class PluginConfigurationSchema
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
    public string Type { get; set; } = "string";
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
/// Integrates with Kernel's PluginRegistry when available.
/// </summary>
public class PluginDiscoveryService : IPluginDiscoveryService
{
    private readonly ConcurrentDictionary<string, PluginInfo> _plugins = new();
    private readonly ILogger<PluginDiscoveryService> _logger;
    private readonly IKernelHostService? _kernelHost;
    private readonly string _pluginsDirectory;

    public event EventHandler<PluginChangedEventArgs>? PluginsChanged;

    public PluginDiscoveryService(
        ILogger<PluginDiscoveryService> logger,
        IConfiguration configuration,
        IKernelHostService? kernelHost = null)
    {
        _logger = logger;
        _kernelHost = kernelHost;
        _pluginsDirectory = configuration.GetValue<string>("PluginsDirectory")
            ?? Path.Combine(AppContext.BaseDirectory, "Plugins");

        // Subscribe to kernel state changes
        if (_kernelHost != null)
        {
            _kernelHost.StateChanged += OnKernelStateChanged;
        }

        // Initial discovery
        _ = RefreshPluginsAsync();
    }

    private void OnKernelStateChanged(object? sender, KernelStateChangedEventArgs e)
    {
        if (e.IsReady)
        {
            _ = RefreshPluginsAsync();
        }
    }

    public IEnumerable<PluginInfo> GetAllPlugins() => _plugins.Values.OrderBy(p => p.Category).ThenBy(p => p.Name);

    public IEnumerable<PluginInfo> GetDiscoveredPlugins() => GetAllPlugins();

    public IEnumerable<PluginInfo> GetPluginsByCategory(PluginCategory category)
    {
        var categoryName = category.ToString();
        return _plugins.Values.Where(p => p.Category == categoryName).OrderBy(p => p.Name);
    }

    public PluginInfo? GetPlugin(string pluginId) =>
        _plugins.TryGetValue(pluginId, out var plugin) ? plugin : null;

    public IEnumerable<PluginInfo> GetActivePlugins() =>
        _plugins.Values.Where(p => p.IsEnabled).OrderBy(p => p.Name);

    public IEnumerable<PluginInfo> GetInactivePlugins() =>
        _plugins.Values.Where(p => !p.IsEnabled).OrderBy(p => p.Name);

    public Task<bool> EnablePluginAsync(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var plugin))
            return Task.FromResult(false);

        try
        {
            plugin.IsEnabled = true;
            plugin.IsActive = true;

            PluginsChanged?.Invoke(this, new PluginChangedEventArgs
            {
                PluginId = pluginId,
                ChangeType = PluginChangeType.Enabled,
                Plugin = plugin
            });

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable plugin {PluginId}", pluginId);
            return Task.FromResult(false);
        }
    }

    public Task<bool> DisablePluginAsync(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var plugin))
            return Task.FromResult(false);

        try
        {
            plugin.IsEnabled = false;
            plugin.IsActive = false;

            PluginsChanged?.Invoke(this, new PluginChangedEventArgs
            {
                PluginId = pluginId,
                ChangeType = PluginChangeType.Disabled,
                Plugin = plugin
            });

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable plugin {PluginId}", pluginId);
            return Task.FromResult(false);
        }
    }

    public PluginConfigurationSchema? GetPluginConfigurationSchema(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var plugin))
            return null;

        var schema = new PluginConfigurationSchema { PluginId = pluginId };

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

        return schema;
    }

    public Task<bool> UpdatePluginConfigAsync(string pluginId, Dictionary<string, object> config)
    {
        if (!_plugins.TryGetValue(pluginId, out var plugin))
            return Task.FromResult(false);

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

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update plugin config {PluginId}", pluginId);
            return Task.FromResult(false);
        }
    }

    public Task RefreshPluginsAsync()
    {
        _logger.LogInformation("Discovering plugins...");

        // First, try to get plugins from the Kernel's registry
        if (_kernelHost?.IsReady == true && _kernelHost.Plugins != null)
        {
            _logger.LogInformation("Using Kernel's plugin registry");
            DiscoverFromKernel();
        }
        else
        {
            // Fallback: Discover from known plugin types in loaded assemblies
            DiscoverFromAssemblies();
        }

        // If no plugins found, add some default entries based on known plugins
        if (_plugins.IsEmpty)
        {
            AddKnownPlugins();
        }

        _logger.LogInformation("Discovered {Count} plugins", _plugins.Count);
        return Task.CompletedTask;
    }

    private void DiscoverFromKernel()
    {
        var registry = _kernelHost!.Plugins!;
        foreach (var kernelPlugin in registry.GetAll())
        {
            var info = new PluginInfo
            {
                Id = kernelPlugin.Id,
                Name = kernelPlugin.Name,
                Version = kernelPlugin.Version,
                Description = $"{kernelPlugin.Name} - {kernelPlugin.Category} plugin",
                Category = kernelPlugin.Category.ToString(),
                IsEnabled = true,
                IsActive = true,
                IsHealthy = true,
                Capabilities = new PluginCapabilities
                {
                    SupportsStreaming = kernelPlugin.Category == PluginCategory.StorageProvider,
                    SupportsMultiInstance = true,
                    SupportsTransactions = kernelPlugin.Category == PluginCategory.DatabaseProvider,
                    SupportsCaching = true,
                    SupportsIndexing = kernelPlugin.Category != PluginCategory.InterfaceProvider
                }
            };

            _plugins.AddOrUpdate(info.Id, info, (_, _) => info);
        }
    }

    private void DiscoverFromAssemblies()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.FullName?.Contains("DataWarehouse") == true);

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface &&
                               t.Name.EndsWith("Plugin") &&
                               t.Namespace?.Contains("Plugins") == true);

                foreach (var type in types)
                {
                    DiscoverPluginFromType(type);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load types from {Assembly}", assembly.FullName);
            }
        }
    }

    private void DiscoverPluginFromType(Type pluginType)
    {
        try
        {
            var category = DetermineCategory(pluginType);
            var pluginId = pluginType.Name.Replace("Plugin", "").ToLowerInvariant();

            var info = new PluginInfo
            {
                Id = pluginId,
                Name = FormatDisplayName(pluginType.Name.Replace("Plugin", "")),
                Version = pluginType.Assembly.GetName().Version?.ToString() ?? "1.0.0",
                Description = $"{pluginType.Name} for DataWarehouse",
                Category = category,
                IsEnabled = false,
                IsActive = false,
                IsHealthy = true,
                AssemblyPath = pluginType.Assembly.Location,
                Capabilities = new PluginCapabilities
                {
                    SupportsStreaming = category == "Storage",
                    SupportsMultiInstance = true,
                    SupportsTransactions = category == "Database",
                    SupportsCaching = true,
                    SupportsIndexing = category != "Interface"
                }
            };

            _plugins.TryAdd(info.Id, info);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not discover plugin from {Type}", pluginType.Name);
        }
    }

    private void AddKnownPlugins()
    {
        // Add storage plugins
        AddPlugin("filesystem", "File System Storage", "Storage", "Local file system storage provider", true);
        AddPlugin("s3", "Amazon S3", "Storage", "AWS S3-compatible object storage", false);
        AddPlugin("azure-blob", "Azure Blob Storage", "Storage", "Microsoft Azure Blob storage", false);
        AddPlugin("gcs", "Google Cloud Storage", "Storage", "Google Cloud Storage provider", false);
        AddPlugin("ipfs", "IPFS Storage", "Storage", "InterPlanetary File System storage", false);
        AddPlugin("memory", "In-Memory Storage", "Storage", "Fast in-memory storage for caching", true);

        // Add database plugins
        AddPlugin("sqlite", "SQLite Database", "Database", "Embedded SQLite database", true);
        AddPlugin("postgresql", "PostgreSQL", "Database", "PostgreSQL relational database", false);
        AddPlugin("mongodb", "MongoDB", "Database", "MongoDB document database", false);
        AddPlugin("redis", "Redis", "Database", "Redis key-value store and cache", false);
        AddPlugin("elasticsearch", "Elasticsearch", "Database", "Elasticsearch search and analytics", false);

        // Add interface plugins
        AddPlugin("rest-api", "REST API", "Interface", "HTTP/HTTPS REST API interface", true);
        AddPlugin("grpc", "gRPC API", "Interface", "High-performance gRPC interface", false);
        AddPlugin("graphql", "GraphQL API", "Interface", "GraphQL query interface", false);
    }

    private void AddPlugin(string id, string name, string category, string description, bool enabled)
    {
        _plugins.TryAdd(id, new PluginInfo
        {
            Id = id,
            Name = name,
            Category = category,
            Description = description,
            Version = "1.0.0",
            IsEnabled = enabled,
            IsActive = enabled,
            IsHealthy = true,
            Author = "DataWarehouse Team",
            Capabilities = new PluginCapabilities
            {
                SupportsStreaming = category == "Storage",
                SupportsMultiInstance = true,
                SupportsTransactions = category == "Database",
                SupportsCaching = true,
                SupportsIndexing = category != "Interface",
                SupportsEncryption = true,
                SupportsCompression = category == "Storage"
            }
        });
    }

    private static string DetermineCategory(Type pluginType)
    {
        var ns = pluginType.Namespace ?? "";
        if (ns.Contains("Storage")) return "Storage";
        if (ns.Contains("Database")) return "Database";
        if (ns.Contains("Interface")) return "Interface";
        if (ns.Contains("AI")) return "AI";
        return "Other";
    }

    private static string FormatDisplayName(string name)
    {
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
