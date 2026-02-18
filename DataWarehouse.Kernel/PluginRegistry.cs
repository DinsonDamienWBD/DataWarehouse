using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel
{
    /// <summary>
    /// Registry for managing loaded plugins in the DataWarehouse Kernel.
    ///
    /// Provides:
    /// - Plugin registration and lookup
    /// - Category-based querying
    /// - Plugin selection based on operating mode (Intelligent Defaults)
    /// - Thread-safe operations
    /// - Implements IPluginRegistry for inter-plugin discovery
    /// </summary>
    public sealed class PluginRegistry : IPluginRegistry
    {
        private readonly ConcurrentDictionary<string, IPlugin> _plugins = new();
        private readonly ConcurrentDictionary<PluginCategory, List<IPlugin>> _byCategory = new();
        private readonly object _categoryLock = new();
        private OperatingMode _operatingMode = OperatingMode.Workstation;

        /// <summary>
        /// Number of registered plugins.
        /// </summary>
        public int Count => _plugins.Count;

        /// <summary>
        /// Set the operating mode for intelligent plugin selection.
        /// </summary>
        public void SetOperatingMode(OperatingMode mode)
        {
            _operatingMode = mode;
        }

        /// <summary>
        /// Get the current operating mode.
        /// </summary>
        public OperatingMode OperatingMode => _operatingMode;

        /// <summary>
        /// Register a plugin.
        /// </summary>
        public void Register(IPlugin plugin)
        {
            ArgumentNullException.ThrowIfNull(plugin);

            _plugins[plugin.Id] = plugin;

            lock (_categoryLock)
            {
                if (!_byCategory.TryGetValue(plugin.Category, out var list))
                {
                    list = new List<IPlugin>();
                    _byCategory[plugin.Category] = list;
                }

                // Remove any existing plugin with same ID
                list.RemoveAll(p => p.Id == plugin.Id);
                list.Add(plugin);
            }
        }

        /// <summary>
        /// Unregister a plugin by ID.
        /// </summary>
        public bool Unregister(string pluginId)
        {
            if (_plugins.TryRemove(pluginId, out var plugin))
            {
                lock (_categoryLock)
                {
                    if (_byCategory.TryGetValue(plugin.Category, out var list))
                    {
                        list.RemoveAll(p => p.Id == pluginId);
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Get a plugin by ID.
        /// </summary>
        public IPlugin? GetPluginById(string pluginId)
        {
            return _plugins.TryGetValue(pluginId, out var plugin) ? plugin : null;
        }

        /// <summary>
        /// Get a plugin by ID (IPluginRegistry interface implementation).
        /// </summary>
        IPlugin? IPluginRegistry.GetById(string pluginId) => GetPluginById(pluginId);

        /// <summary>
        /// Get a plugin by type.
        /// Returns the best available plugin based on operating mode.
        /// </summary>
        public T? GetPlugin<T>() where T : class, IPlugin
        {
            var candidates = _plugins.Values.OfType<T>().ToList();
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            // Select based on operating mode and quality level
            return SelectBestPlugin(candidates);
        }

        /// <summary>
        /// Get a plugin by type (IPluginRegistry interface implementation).
        /// </summary>
        T? IPluginRegistry.Get<T>() where T : class => GetPlugin<T>();

        /// <summary>
        /// Get a plugin by type and ID.
        /// </summary>
        public T? GetPlugin<T>(string pluginId) where T : class, IPlugin
        {
            return GetPluginById(pluginId) as T;
        }

        /// <summary>
        /// Get all plugins of a specific type.
        /// </summary>
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin
        {
            return _plugins.Values.OfType<T>();
        }

        /// <summary>
        /// Get all plugins of a specific type (IPluginRegistry interface implementation).
        /// </summary>
        IEnumerable<T> IPluginRegistry.GetAll<T>() => GetPlugins<T>();

        /// <summary>
        /// Check if a plugin of the specified type is registered (IPluginRegistry interface).
        /// </summary>
        public bool Has<T>() where T : class, IPlugin
        {
            return _plugins.Values.OfType<T>().Any();
        }

        /// <summary>
        /// Get all plugins in a category.
        /// </summary>
        public IEnumerable<IPlugin> GetPluginsByCategory(PluginCategory category)
        {
            lock (_categoryLock)
            {
                if (_byCategory.TryGetValue(category, out var list))
                {
                    return list.ToList();
                }
            }
            return Enumerable.Empty<IPlugin>();
        }

        /// <summary>
        /// Get all registered plugins.
        /// </summary>
        public IEnumerable<IPlugin> GetAll()
        {
            return _plugins.Values.ToList();
        }

        /// <summary>
        /// Get all registered plugins (alias for GetAll).
        /// </summary>
        public IEnumerable<IPlugin> GetAllPlugins()
        {
            return GetAll();
        }

        /// <summary>
        /// Check if a plugin is registered.
        /// </summary>
        public bool Contains(string pluginId)
        {
            return _plugins.ContainsKey(pluginId);
        }

        /// <summary>
        /// Get all registered plugin IDs.
        /// </summary>
        public IEnumerable<string> GetPluginIds()
        {
            return _plugins.Keys.ToList();
        }

        /// <summary>
        /// Get a summary of registered plugins by category.
        /// </summary>
        public Dictionary<PluginCategory, int> GetCategorySummary()
        {
            var result = new Dictionary<PluginCategory, int>();
            lock (_categoryLock)
            {
                foreach (var kvp in _byCategory)
                {
                    result[kvp.Key] = kvp.Value.Count;
                }
            }
            return result;
        }

        /// <summary>
        /// Select the best plugin based on operating mode and quality.
        /// </summary>
        private T SelectBestPlugin<T>(List<T> candidates) where T : class, IPlugin
        {
            // If any plugin explicitly declares operating mode preference, prefer it
            var modePreferred = candidates.FirstOrDefault(p =>
            {
                var metadata = GetPluginMetadata(p);
                if (metadata.TryGetValue("PreferredMode", out var mode))
                {
                    return mode.ToString()?.Equals(_operatingMode.ToString(), StringComparison.OrdinalIgnoreCase) == true;
                }
                return false;
            });

            if (modePreferred != null) return modePreferred;

            // Otherwise select by quality level
            var ordered = candidates.OrderByDescending(p =>
            {
                var metadata = GetPluginMetadata(p);
                if (metadata.TryGetValue("QualityLevel", out var ql) && ql is int quality)
                {
                    return quality;
                }
                return 50; // Default quality
            });

            return ordered.First();
        }

        private async Task<Dictionary<string, object>> GetPluginMetadataAsync(IPlugin plugin)
        {
            try
            {
                var response = await plugin.OnHandshakeAsync(new HandshakeRequest
                {
                    KernelId = "registry",
                    ProtocolVersion = "1.0",
                    Timestamp = DateTime.UtcNow
                });

                return response.Metadata ?? new Dictionary<string, object>();
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        [Obsolete("Use GetPluginMetadataAsync instead")]
        private Dictionary<string, object> GetPluginMetadata(IPlugin plugin)
        {
            // Cannot be async: Called from sync context during plugin registration.
            // Using Task.Run to prevent sync context deadlock, then block safely.
            return Task.Run(() => GetPluginMetadataAsync(plugin)).Result;
        }
    }
}
