using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace DataWarehouse.Kernel.Plugins
{
    /// <summary>
    /// Manages hot plugin loading and unloading using AssemblyLoadContext.
    /// Enables updating plugins without kernel restart.
    /// </summary>
    public sealed class PluginLoader : IPluginReloader, IDisposable
    {
        private readonly ConcurrentDictionary<string, PluginContext> _loadedPlugins = new();
        private readonly PluginRegistry _registry;
        private readonly IKernelContext _kernelContext;
        private readonly string _pluginDirectory;
        private readonly object _reloadLock = new();

        public event Action<PluginReloadEvent>? OnPluginReloading;
        public event Action<PluginReloadEvent>? OnPluginReloaded;

        public PluginLoader(
            PluginRegistry registry,
            IKernelContext kernelContext,
            string? pluginDirectory = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _kernelContext = kernelContext ?? throw new ArgumentNullException(nameof(kernelContext));
            _pluginDirectory = pluginDirectory ?? Path.Combine(kernelContext.RootPath, "plugins");

            // Ensure plugin directory exists
            if (!Directory.Exists(_pluginDirectory))
            {
                Directory.CreateDirectory(_pluginDirectory);
            }
        }

        /// <summary>
        /// Loads a plugin from an assembly file.
        /// </summary>
        public async Task<PluginLoadResult> LoadPluginAsync(
            string assemblyPath,
            CancellationToken ct = default)
        {
            if (!File.Exists(assemblyPath))
            {
                return new PluginLoadResult
                {
                    Success = false,
                    Error = $"Assembly file not found: {assemblyPath}"
                };
            }

            var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

            try
            {
                // Create isolated load context
                var loadContext = new PluginLoadContext(assemblyPath);
                var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

                // Find plugin types
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IPlugin).IsAssignableFrom(t) &&
                                !t.IsAbstract &&
                                !t.IsInterface)
                    .ToList();

                if (pluginTypes.Count == 0)
                {
                    loadContext.Unload();
                    return new PluginLoadResult
                    {
                        Success = false,
                        Error = "No plugin types found in assembly"
                    };
                }

                var loadedPlugins = new List<IPlugin>();

                foreach (var pluginType in pluginTypes)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        // Create plugin instance
                        var plugin = (IPlugin?)Activator.CreateInstance(pluginType);
                        if (plugin == null)
                        {
                            continue;
                        }

                        // Perform handshake
                        var handshakeRequest = new HandshakeRequest
                        {
                            KernelVersion = "1.0.0",
                            Mode = _kernelContext.Mode,
                            Capabilities = []
                        };

                        var response = await plugin.OnHandshakeAsync(handshakeRequest);
                        if (!response.Accepted)
                        {
                            _kernelContext.LogWarning($"Plugin {plugin.Id} rejected handshake: {response.RejectReason}");
                            continue;
                        }

                        // Register with plugin registry
                        _registry.Register(plugin);
                        loadedPlugins.Add(plugin);

                        _kernelContext.LogInfo($"Loaded plugin: {plugin.Id} v{plugin.Version}");
                    }
                    catch (Exception ex)
                    {
                        _kernelContext.LogError($"Failed to instantiate plugin type {pluginType.Name}: {ex.Message}", ex);
                    }
                }

                if (loadedPlugins.Count == 0)
                {
                    loadContext.Unload();
                    return new PluginLoadResult
                    {
                        Success = false,
                        Error = "No plugins could be loaded from assembly"
                    };
                }

                // Store plugin context for later unloading
                var context = new PluginContext
                {
                    AssemblyPath = assemblyPath,
                    AssemblyName = assemblyName,
                    LoadContext = loadContext,
                    Plugins = loadedPlugins,
                    LoadedAt = DateTime.UtcNow
                };

                foreach (var plugin in loadedPlugins)
                {
                    _loadedPlugins[plugin.Id] = context;
                }

                return new PluginLoadResult
                {
                    Success = true,
                    PluginIds = loadedPlugins.Select(p => p.Id).ToArray(),
                    Version = loadedPlugins.FirstOrDefault()?.Version
                };
            }
            catch (Exception ex)
            {
                return new PluginLoadResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Unloads a plugin and releases its assembly.
        /// </summary>
        public async Task<bool> UnloadPluginAsync(string pluginId, CancellationToken ct = default)
        {
            if (!_loadedPlugins.TryGetValue(pluginId, out var context))
            {
                return false;
            }

            lock (_reloadLock)
            {
                // Unregister all plugins from this context
                foreach (var plugin in context.Plugins)
                {
                    try
                    {
                        // Notify plugin of shutdown
                        _ = plugin.OnMessageAsync(new PluginMessage
                        {
                            Type = "kernel.plugin.unloading",
                            Payload = new Dictionary<string, object>
                            {
                                ["PluginId"] = plugin.Id
                            }
                        });

                        _registry.Unregister(plugin.Id);
                        _loadedPlugins.TryRemove(plugin.Id, out _);
                    }
                    catch (Exception ex)
                    {
                        _kernelContext.LogError($"Error unloading plugin {plugin.Id}: {ex.Message}", ex);
                    }
                }

                // Unload the assembly context
                context.LoadContext.Unload();

                _kernelContext.LogInfo($"Unloaded plugin assembly: {context.AssemblyName}");
            }

            // Force garbage collection to release the assembly
            await Task.Run(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }, ct);

            return true;
        }

        /// <summary>
        /// Reloads a specific plugin.
        /// </summary>
        public async Task<PluginReloadResult> ReloadPluginAsync(string pluginId, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            OnPluginReloading?.Invoke(new PluginReloadEvent
            {
                PluginId = pluginId,
                Phase = PluginReloadPhase.Starting,
                Timestamp = DateTime.UtcNow
            });

            if (!_loadedPlugins.TryGetValue(pluginId, out var context))
            {
                return new PluginReloadResult
                {
                    PluginId = pluginId,
                    Success = false,
                    Error = "Plugin not found"
                };
            }

            var assemblyPath = context.AssemblyPath;
            var previousVersion = context.Plugins.FirstOrDefault()?.Version;

            try
            {
                // Phase: Unloading
                OnPluginReloading?.Invoke(new PluginReloadEvent
                {
                    PluginId = pluginId,
                    Version = previousVersion,
                    Phase = PluginReloadPhase.Unloading,
                    Timestamp = DateTime.UtcNow
                });

                await UnloadPluginAsync(pluginId, ct);

                // Phase: Loading
                OnPluginReloading?.Invoke(new PluginReloadEvent
                {
                    PluginId = pluginId,
                    Phase = PluginReloadPhase.Loading,
                    Timestamp = DateTime.UtcNow
                });

                var loadResult = await LoadPluginAsync(assemblyPath, ct);

                sw.Stop();

                if (!loadResult.Success)
                {
                    OnPluginReloaded?.Invoke(new PluginReloadEvent
                    {
                        PluginId = pluginId,
                        Phase = PluginReloadPhase.Failed,
                        Timestamp = DateTime.UtcNow
                    });

                    return new PluginReloadResult
                    {
                        PluginId = pluginId,
                        Success = false,
                        PreviousVersion = previousVersion,
                        Error = loadResult.Error,
                        Duration = sw.Elapsed
                    };
                }

                OnPluginReloaded?.Invoke(new PluginReloadEvent
                {
                    PluginId = pluginId,
                    Version = loadResult.Version,
                    Phase = PluginReloadPhase.Completed,
                    Timestamp = DateTime.UtcNow
                });

                return new PluginReloadResult
                {
                    PluginId = pluginId,
                    Success = true,
                    PreviousVersion = previousVersion,
                    NewVersion = loadResult.Version,
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                sw.Stop();

                OnPluginReloaded?.Invoke(new PluginReloadEvent
                {
                    PluginId = pluginId,
                    Phase = PluginReloadPhase.Failed,
                    Timestamp = DateTime.UtcNow
                });

                return new PluginReloadResult
                {
                    PluginId = pluginId,
                    Success = false,
                    PreviousVersion = previousVersion,
                    Error = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }

        /// <summary>
        /// Reloads all loaded plugins.
        /// </summary>
        public async Task<PluginReloadResult[]> ReloadAllAsync(CancellationToken ct = default)
        {
            var pluginIds = _loadedPlugins.Keys.ToArray();
            var results = new List<PluginReloadResult>();

            foreach (var pluginId in pluginIds)
            {
                ct.ThrowIfCancellationRequested();
                var result = await ReloadPluginAsync(pluginId, ct);
                results.Add(result);
            }

            return results.ToArray();
        }

        /// <summary>
        /// Loads all plugins from the plugin directory.
        /// </summary>
        public async Task<PluginLoadResult[]> LoadAllPluginsAsync(CancellationToken ct = default)
        {
            var results = new List<PluginLoadResult>();

            if (!Directory.Exists(_pluginDirectory))
            {
                return results.ToArray();
            }

            var dllFiles = Directory.GetFiles(_pluginDirectory, "*.dll", SearchOption.AllDirectories);

            foreach (var dllFile in dllFiles)
            {
                ct.ThrowIfCancellationRequested();

                // Skip common runtime DLLs
                var fileName = Path.GetFileName(dllFile);
                if (fileName.StartsWith("System.") ||
                    fileName.StartsWith("Microsoft.") ||
                    fileName.StartsWith("DataWarehouse.SDK"))
                {
                    continue;
                }

                var result = await LoadPluginAsync(dllFile, ct);
                results.Add(result);
            }

            return results.ToArray();
        }

        /// <summary>
        /// Gets information about loaded plugins.
        /// </summary>
        public IEnumerable<LoadedPluginInfo> GetLoadedPlugins()
        {
            return _loadedPlugins.Values
                .SelectMany(c => c.Plugins.Select(p => new LoadedPluginInfo
                {
                    PluginId = p.Id,
                    Name = p.Name,
                    Version = p.Version,
                    Category = p.Category,
                    AssemblyPath = c.AssemblyPath,
                    LoadedAt = c.LoadedAt
                }))
                .DistinctBy(p => p.PluginId)
                .ToArray();
        }

        public void Dispose()
        {
            foreach (var context in _loadedPlugins.Values.DistinctBy(c => c.AssemblyName))
            {
                try
                {
                    context.LoadContext.Unload();
                }
                catch
                {
                    // Ignore errors during disposal
                }
            }

            _loadedPlugins.Clear();
        }

        #region Internal Classes

        private sealed class PluginContext
        {
            public required string AssemblyPath { get; init; }
            public required string AssemblyName { get; init; }
            public required PluginLoadContext LoadContext { get; init; }
            public required List<IPlugin> Plugins { get; init; }
            public required DateTime LoadedAt { get; init; }
        }

        /// <summary>
        /// Custom AssemblyLoadContext for plugin isolation.
        /// </summary>
        private sealed class PluginLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;

            public PluginLoadContext(string pluginPath) : base(isCollectible: true)
            {
                _resolver = new AssemblyDependencyResolver(pluginPath);
            }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                // Try to resolve from plugin directory first
                var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
                if (assemblyPath != null)
                {
                    return LoadFromAssemblyPath(assemblyPath);
                }

                // Fall back to default context for shared assemblies
                return null;
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                if (libraryPath != null)
                {
                    return LoadUnmanagedDllFromPath(libraryPath);
                }

                return IntPtr.Zero;
            }
        }

        #endregion
    }

    /// <summary>
    /// Result of loading a plugin assembly.
    /// </summary>
    public class PluginLoadResult
    {
        public bool Success { get; init; }
        public string[]? PluginIds { get; init; }
        public string? Version { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// Information about a loaded plugin.
    /// </summary>
    public class LoadedPluginInfo
    {
        public string PluginId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public PluginCategory Category { get; init; }
        public string AssemblyPath { get; init; } = string.Empty;
        public DateTime LoadedAt { get; init; }
    }
}
