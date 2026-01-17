using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace DataWarehouse.Plugins.HotReload
{
    /// <summary>
    /// Hot Plugin Reload infrastructure plugin.
    /// Provides AssemblyLoadContext isolation, state preservation, and graceful connection draining.
    ///
    /// Features:
    /// - Load/unload plugins without restarting the kernel
    /// - Preserve plugin state across reloads
    /// - Drain in-flight operations before unload
    /// - Watch plugin directories for changes
    /// - Version compatibility checking
    ///
    /// Message Commands:
    /// - hotreload.load: Load a plugin from assembly path
    /// - hotreload.unload: Unload a plugin by ID
    /// - hotreload.reload: Reload a plugin (unload + load)
    /// - hotreload.list: List loaded plugins
    /// - hotreload.watch.start: Start watching a directory
    /// - hotreload.watch.stop: Stop watching a directory
    /// - hotreload.state.save: Save plugin state
    /// - hotreload.state.restore: Restore plugin state
    /// </summary>
    public sealed class HotReloadPlugin : FeaturePluginBase
    {
        public override string Id => "datawarehouse.hotreload";
        public override string Name => "Hot Plugin Reload";
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;
        public override string Version => "1.0.0";

        private readonly ConcurrentDictionary<string, PluginLoadContext> _loadContexts = new();
        private readonly ConcurrentDictionary<string, LoadedPluginInfo> _loadedPlugins = new();
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
        private readonly ConcurrentDictionary<string, byte[]> _savedStates = new();
        private readonly ConcurrentDictionary<string, DrainContext> _drainContexts = new();

        private IMessageBus? _messageBus;
        private CancellationTokenSource? _cts;
        private string? _kernelId;
        private string? _statePath;
        private bool _isRunning;

        // Configuration
        private TimeSpan _drainTimeout = TimeSpan.FromSeconds(30);
        private TimeSpan _watchDebounce = TimeSpan.FromMilliseconds(500);
        private bool _autoReloadOnChange = true;
        private bool _preserveStateOnReload = true;

        public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            _kernelId = request.KernelId;
            _statePath = Path.Combine(request.RootPath ?? ".", ".hotreload-state");

            return Task.FromResult(new HandshakeResponse
            {
                PluginId = Id,
                Name = Name,
                Version = ParseSemanticVersion(Version),
                Category = Category,
                Success = true,
                ReadyState = PluginReadyState.Ready,
                Capabilities = GetCapabilities(),
                Metadata = GetMetadata()
            });
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new()
                {
                    Name = "load",
                    Description = "Load a plugin from assembly path",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["assemblyPath"] = new { type = "string", description = "Path to plugin assembly" },
                            ["restoreState"] = new { type = "boolean", description = "Restore saved state if available" }
                        },
                        ["required"] = new[] { "assemblyPath" }
                    }
                },
                new()
                {
                    Name = "unload",
                    Description = "Unload a plugin by ID with graceful draining",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["pluginId"] = new { type = "string", description = "Plugin ID to unload" },
                            ["saveState"] = new { type = "boolean", description = "Save plugin state before unload" },
                            ["force"] = new { type = "boolean", description = "Force unload without draining" }
                        },
                        ["required"] = new[] { "pluginId" }
                    }
                },
                new()
                {
                    Name = "reload",
                    Description = "Reload a plugin (unload + load with state preservation)",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["pluginId"] = new { type = "string", description = "Plugin ID to reload" }
                        },
                        ["required"] = new[] { "pluginId" }
                    }
                },
                new()
                {
                    Name = "watch",
                    Description = "Watch a directory for plugin changes",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["directory"] = new { type = "string", description = "Directory to watch" },
                            ["enabled"] = new { type = "boolean", description = "Enable or disable watching" }
                        },
                        ["required"] = new[] { "directory" }
                    }
                }
            };
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            return new Dictionary<string, object>
            {
                ["Description"] = "Hot plugin reload infrastructure with AssemblyLoadContext isolation",
                ["FeatureType"] = "HotReload",
                ["SupportsStatePreservation"] = true,
                ["SupportsGracefulDrain"] = true,
                ["SupportsFileWatching"] = true,
                ["DrainTimeoutSeconds"] = _drainTimeout.TotalSeconds,
                ["LoadedPluginCount"] = _loadedPlugins.Count
            };
        }

        public override async Task StartAsync(CancellationToken ct)
        {
            if (_isRunning) return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _isRunning = true;

            // Ensure state directory exists
            if (_statePath != null && !Directory.Exists(_statePath))
            {
                Directory.CreateDirectory(_statePath);
            }

            // Load any persisted state
            await LoadPersistedStatesAsync();
        }

        public override async Task StopAsync()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();

            // Stop all file watchers
            foreach (var watcher in _watchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();

            // Unload all plugins gracefully
            var pluginIds = _loadedPlugins.Keys.ToArray();
            foreach (var pluginId in pluginIds)
            {
                await UnloadPluginAsync(pluginId, saveState: true, force: true);
            }

            _cts?.Dispose();
            _cts = null;
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            if (message.Payload == null) return;

            var response = message.Type switch
            {
                "hotreload.load" => await HandleLoadAsync(message.Payload),
                "hotreload.unload" => await HandleUnloadAsync(message.Payload),
                "hotreload.reload" => await HandleReloadAsync(message.Payload),
                "hotreload.list" => HandleList(),
                "hotreload.watch.start" => HandleWatchStart(message.Payload),
                "hotreload.watch.stop" => HandleWatchStop(message.Payload),
                "hotreload.state.save" => await HandleStateSaveAsync(message.Payload),
                "hotreload.state.restore" => await HandleStateRestoreAsync(message.Payload),
                "hotreload.configure" => HandleConfigure(message.Payload),
                "hotreload.drain.start" => HandleDrainStart(message.Payload),
                "hotreload.drain.complete" => HandleDrainComplete(message.Payload),
                _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
            };

            // Publish response if message bus available
            if (_messageBus != null && message.CorrelationId != null)
            {
                await _messageBus.PublishAsync($"hotreload.response.{message.CorrelationId}", new PluginMessage
                {
                    Type = "hotreload.response",
                    CorrelationId = message.CorrelationId,
                    Payload = response
                });
            }
        }

        /// <summary>
        /// Load a plugin from an assembly path.
        /// </summary>
        public async Task<LoadedPluginInfo?> LoadPluginAsync(string assemblyPath, bool restoreState = true)
        {
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Plugin assembly not found: {assemblyPath}");
            }

            var absolutePath = Path.GetFullPath(assemblyPath);
            var assemblyName = Path.GetFileNameWithoutExtension(absolutePath);

            // Create isolated load context
            var loadContext = new PluginLoadContext(absolutePath, assemblyName);

            try
            {
                // Load assembly
                var assembly = loadContext.LoadFromAssemblyPath(absolutePath);

                // Find plugin types
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                    .ToList();

                if (pluginTypes.Count == 0)
                {
                    loadContext.Unload();
                    throw new InvalidOperationException($"No plugin types found in assembly: {assemblyPath}");
                }

                IPlugin? loadedPlugin = null;
                LoadedPluginInfo? info = null;

                foreach (var pluginType in pluginTypes)
                {
                    if (Activator.CreateInstance(pluginType) is IPlugin plugin)
                    {
                        // Check if already loaded
                        if (_loadedPlugins.ContainsKey(plugin.Id))
                        {
                            continue; // Skip already loaded plugins
                        }

                        // Store context and info
                        _loadContexts[plugin.Id] = loadContext;

                        info = new LoadedPluginInfo
                        {
                            PluginId = plugin.Id,
                            Name = plugin.Name,
                            Version = plugin.Version,
                            Category = plugin.Category,
                            AssemblyPath = absolutePath,
                            LoadedAt = DateTime.UtcNow,
                            Plugin = plugin
                        };

                        _loadedPlugins[plugin.Id] = info;
                        loadedPlugin = plugin;

                        // Restore state if requested
                        if (restoreState && _savedStates.TryGetValue(plugin.Id, out var state))
                        {
                            await RestorePluginStateAsync(plugin, state);
                        }
                    }
                }

                return info;
            }
            catch
            {
                loadContext.Unload();
                throw;
            }
        }

        /// <summary>
        /// Unload a plugin with graceful draining.
        /// </summary>
        public async Task<bool> UnloadPluginAsync(string pluginId, bool saveState = true, bool force = false)
        {
            if (!_loadedPlugins.TryGetValue(pluginId, out var info))
            {
                return false;
            }

            // Create drain context
            var drainContext = new DrainContext(pluginId, _drainTimeout);
            _drainContexts[pluginId] = drainContext;

            try
            {
                if (!force)
                {
                    // Wait for drain to complete
                    await drainContext.WaitForDrainAsync();
                }

                // Save state if requested
                if (saveState && info.Plugin != null)
                {
                    var state = await ExtractPluginStateAsync(info.Plugin);
                    if (state != null)
                    {
                        _savedStates[pluginId] = state;
                        await PersistStateAsync(pluginId, state);
                    }
                }

                // Stop feature plugins
                if (info.Plugin is IFeaturePlugin feature)
                {
                    try
                    {
                        await feature.StopAsync();
                    }
                    catch { /* Ignore stop errors during unload */ }
                }

                // Remove from tracking
                _loadedPlugins.TryRemove(pluginId, out _);

                // Unload assembly context
                if (_loadContexts.TryRemove(pluginId, out var context))
                {
                    context.Unload();

                    // Trigger GC to help unload
                    for (int i = 0; i < 3; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }

                return true;
            }
            finally
            {
                _drainContexts.TryRemove(pluginId, out _);
            }
        }

        /// <summary>
        /// Reload a plugin with state preservation.
        /// </summary>
        public async Task<LoadedPluginInfo?> ReloadPluginAsync(string pluginId)
        {
            if (!_loadedPlugins.TryGetValue(pluginId, out var existingInfo))
            {
                throw new InvalidOperationException($"Plugin not loaded: {pluginId}");
            }

            var assemblyPath = existingInfo.AssemblyPath;

            // Unload with state preservation
            await UnloadPluginAsync(pluginId, saveState: _preserveStateOnReload, force: false);

            // Wait a bit for assembly to be released
            await Task.Delay(100);

            // Reload
            return await LoadPluginAsync(assemblyPath, restoreState: _preserveStateOnReload);
        }

        /// <summary>
        /// Start watching a directory for plugin changes.
        /// </summary>
        public void StartWatching(string directory)
        {
            if (_watchers.ContainsKey(directory)) return;
            if (!Directory.Exists(directory)) return;

            var watcher = new FileSystemWatcher(directory)
            {
                Filter = "*.dll",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            var debounceTimer = new Dictionary<string, DateTime>();
            var lockObj = new object();

            watcher.Changed += async (s, e) =>
            {
                if (!_autoReloadOnChange) return;

                lock (lockObj)
                {
                    var now = DateTime.UtcNow;
                    if (debounceTimer.TryGetValue(e.FullPath, out var lastChange) &&
                        now - lastChange < _watchDebounce)
                    {
                        return;
                    }
                    debounceTimer[e.FullPath] = now;
                }

                // Find plugins loaded from this assembly
                var plugins = _loadedPlugins.Values
                    .Where(p => p.AssemblyPath.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var plugin in plugins)
                {
                    try
                    {
                        await ReloadPluginAsync(plugin.PluginId);
                    }
                    catch { /* Log error */ }
                }
            };

            _watchers[directory] = watcher;
        }

        /// <summary>
        /// Stop watching a directory.
        /// </summary>
        public void StopWatching(string directory)
        {
            if (_watchers.TryRemove(directory, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }

        /// <summary>
        /// Get a loaded plugin by ID.
        /// </summary>
        public IPlugin? GetPlugin(string pluginId)
        {
            return _loadedPlugins.TryGetValue(pluginId, out var info) ? info.Plugin : null;
        }

        /// <summary>
        /// Get all loaded plugins.
        /// </summary>
        public IEnumerable<LoadedPluginInfo> GetLoadedPlugins()
        {
            return _loadedPlugins.Values;
        }

        /// <summary>
        /// Start draining operations for a plugin (call from operations before unload).
        /// </summary>
        public IDisposable? BeginOperation(string pluginId)
        {
            if (_drainContexts.TryGetValue(pluginId, out var context))
            {
                return context.BeginOperation();
            }
            return null;
        }

        #region Private Methods

        private async Task<Dictionary<string, object>> HandleLoadAsync(Dictionary<string, object> payload)
        {
            try
            {
                var assemblyPath = payload.GetValueOrDefault("assemblyPath")?.ToString();
                var restoreState = payload.GetValueOrDefault("restoreState") as bool? ?? true;

                if (string.IsNullOrEmpty(assemblyPath))
                {
                    return new Dictionary<string, object> { ["error"] = "assemblyPath is required" };
                }

                var info = await LoadPluginAsync(assemblyPath, restoreState);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["pluginId"] = info?.PluginId ?? "",
                    ["name"] = info?.Name ?? "",
                    ["version"] = info?.Version ?? ""
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        private async Task<Dictionary<string, object>> HandleUnloadAsync(Dictionary<string, object> payload)
        {
            try
            {
                var pluginId = payload.GetValueOrDefault("pluginId")?.ToString();
                var saveState = payload.GetValueOrDefault("saveState") as bool? ?? true;
                var force = payload.GetValueOrDefault("force") as bool? ?? false;

                if (string.IsNullOrEmpty(pluginId))
                {
                    return new Dictionary<string, object> { ["error"] = "pluginId is required" };
                }

                var success = await UnloadPluginAsync(pluginId, saveState, force);

                return new Dictionary<string, object>
                {
                    ["success"] = success,
                    ["pluginId"] = pluginId
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        private async Task<Dictionary<string, object>> HandleReloadAsync(Dictionary<string, object> payload)
        {
            try
            {
                var pluginId = payload.GetValueOrDefault("pluginId")?.ToString();

                if (string.IsNullOrEmpty(pluginId))
                {
                    return new Dictionary<string, object> { ["error"] = "pluginId is required" };
                }

                var info = await ReloadPluginAsync(pluginId);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["pluginId"] = info?.PluginId ?? "",
                    ["version"] = info?.Version ?? ""
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        private Dictionary<string, object> HandleList()
        {
            var plugins = _loadedPlugins.Values.Select(p => new Dictionary<string, object>
            {
                ["pluginId"] = p.PluginId,
                ["name"] = p.Name,
                ["version"] = p.Version,
                ["category"] = p.Category.ToString(),
                ["assemblyPath"] = p.AssemblyPath,
                ["loadedAt"] = p.LoadedAt.ToString("O")
            }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["plugins"] = plugins,
                ["count"] = plugins.Count
            };
        }

        private Dictionary<string, object> HandleWatchStart(Dictionary<string, object> payload)
        {
            var directory = payload.GetValueOrDefault("directory")?.ToString();
            if (string.IsNullOrEmpty(directory))
            {
                return new Dictionary<string, object> { ["error"] = "directory is required" };
            }

            StartWatching(directory);
            return new Dictionary<string, object> { ["success"] = true, ["directory"] = directory };
        }

        private Dictionary<string, object> HandleWatchStop(Dictionary<string, object> payload)
        {
            var directory = payload.GetValueOrDefault("directory")?.ToString();
            if (string.IsNullOrEmpty(directory))
            {
                return new Dictionary<string, object> { ["error"] = "directory is required" };
            }

            StopWatching(directory);
            return new Dictionary<string, object> { ["success"] = true, ["directory"] = directory };
        }

        private async Task<Dictionary<string, object>> HandleStateSaveAsync(Dictionary<string, object> payload)
        {
            var pluginId = payload.GetValueOrDefault("pluginId")?.ToString();
            if (string.IsNullOrEmpty(pluginId))
            {
                return new Dictionary<string, object> { ["error"] = "pluginId is required" };
            }

            if (!_loadedPlugins.TryGetValue(pluginId, out var info) || info.Plugin == null)
            {
                return new Dictionary<string, object> { ["error"] = "Plugin not found" };
            }

            var state = await ExtractPluginStateAsync(info.Plugin);
            if (state != null)
            {
                _savedStates[pluginId] = state;
                await PersistStateAsync(pluginId, state);
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["pluginId"] = pluginId,
                ["stateSize"] = state?.Length ?? 0
            };
        }

        private async Task<Dictionary<string, object>> HandleStateRestoreAsync(Dictionary<string, object> payload)
        {
            var pluginId = payload.GetValueOrDefault("pluginId")?.ToString();
            if (string.IsNullOrEmpty(pluginId))
            {
                return new Dictionary<string, object> { ["error"] = "pluginId is required" };
            }

            if (!_loadedPlugins.TryGetValue(pluginId, out var info) || info.Plugin == null)
            {
                return new Dictionary<string, object> { ["error"] = "Plugin not found" };
            }

            if (!_savedStates.TryGetValue(pluginId, out var state))
            {
                return new Dictionary<string, object> { ["error"] = "No saved state found" };
            }

            await RestorePluginStateAsync(info.Plugin, state);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["pluginId"] = pluginId,
                ["stateSize"] = state.Length
            };
        }

        private Dictionary<string, object> HandleConfigure(Dictionary<string, object> payload)
        {
            if (payload.TryGetValue("drainTimeoutSeconds", out var timeout) && timeout is double t)
            {
                _drainTimeout = TimeSpan.FromSeconds(t);
            }

            if (payload.TryGetValue("watchDebounceMs", out var debounce) && debounce is double d)
            {
                _watchDebounce = TimeSpan.FromMilliseconds(d);
            }

            if (payload.TryGetValue("autoReloadOnChange", out var autoReload) && autoReload is bool ar)
            {
                _autoReloadOnChange = ar;
            }

            if (payload.TryGetValue("preserveStateOnReload", out var preserveState) && preserveState is bool ps)
            {
                _preserveStateOnReload = ps;
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["drainTimeoutSeconds"] = _drainTimeout.TotalSeconds,
                ["watchDebounceMs"] = _watchDebounce.TotalMilliseconds,
                ["autoReloadOnChange"] = _autoReloadOnChange,
                ["preserveStateOnReload"] = _preserveStateOnReload
            };
        }

        private Dictionary<string, object> HandleDrainStart(Dictionary<string, object> payload)
        {
            var pluginId = payload.GetValueOrDefault("pluginId")?.ToString();
            if (string.IsNullOrEmpty(pluginId))
            {
                return new Dictionary<string, object> { ["error"] = "pluginId is required" };
            }

            if (!_drainContexts.TryGetValue(pluginId, out var context))
            {
                context = new DrainContext(pluginId, _drainTimeout);
                _drainContexts[pluginId] = context;
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["pluginId"] = pluginId,
                ["activeOperations"] = context.ActiveOperations
            };
        }

        private Dictionary<string, object> HandleDrainComplete(Dictionary<string, object> payload)
        {
            var pluginId = payload.GetValueOrDefault("pluginId")?.ToString();
            if (string.IsNullOrEmpty(pluginId))
            {
                return new Dictionary<string, object> { ["error"] = "pluginId is required" };
            }

            if (_drainContexts.TryGetValue(pluginId, out var context))
            {
                context.SignalDrainComplete();
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["pluginId"] = pluginId
            };
        }

        private async Task<byte[]?> ExtractPluginStateAsync(IPlugin plugin)
        {
            try
            {
                // Check if plugin implements IStateful
                if (plugin is IStatefulPlugin stateful)
                {
                    var state = await stateful.GetStateAsync();
                    return JsonSerializer.SerializeToUtf8Bytes(state);
                }

                // Try reflection for plugins with [PreserveState] attributes
                var stateProperties = plugin.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetCustomAttribute<PreserveStateAttribute>() != null && p.CanRead)
                    .ToList();

                if (stateProperties.Count > 0)
                {
                    var stateDict = new Dictionary<string, object?>();
                    foreach (var prop in stateProperties)
                    {
                        stateDict[prop.Name] = prop.GetValue(plugin);
                    }
                    return JsonSerializer.SerializeToUtf8Bytes(stateDict);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task RestorePluginStateAsync(IPlugin plugin, byte[] state)
        {
            try
            {
                if (plugin is IStatefulPlugin stateful)
                {
                    var stateObj = JsonSerializer.Deserialize<Dictionary<string, object>>(state);
                    if (stateObj != null)
                    {
                        await stateful.SetStateAsync(stateObj);
                    }
                    return;
                }

                // Try reflection for [PreserveState] properties
                var stateDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(state);
                if (stateDict == null) return;

                var stateProperties = plugin.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetCustomAttribute<PreserveStateAttribute>() != null && p.CanWrite)
                    .ToDictionary(p => p.Name);

                foreach (var kvp in stateDict)
                {
                    if (stateProperties.TryGetValue(kvp.Key, out var prop))
                    {
                        var value = JsonSerializer.Deserialize(kvp.Value.GetRawText(), prop.PropertyType);
                        prop.SetValue(plugin, value);
                    }
                }
            }
            catch
            {
                // State restoration failed, continue with fresh state
            }
        }

        private async Task PersistStateAsync(string pluginId, byte[] state)
        {
            if (_statePath == null) return;

            try
            {
                var filePath = Path.Combine(_statePath, $"{SanitizeFileName(pluginId)}.state");
                await File.WriteAllBytesAsync(filePath, state);
            }
            catch
            {
                // Persist failed, state is still in memory
            }
        }

        private async Task LoadPersistedStatesAsync()
        {
            if (_statePath == null || !Directory.Exists(_statePath)) return;

            try
            {
                var stateFiles = Directory.GetFiles(_statePath, "*.state");
                foreach (var file in stateFiles)
                {
                    var pluginId = Path.GetFileNameWithoutExtension(file);
                    var state = await File.ReadAllBytesAsync(file);
                    _savedStates[pluginId] = state;
                }
            }
            catch
            {
                // Failed to load persisted states
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        #endregion
    }

    /// <summary>
    /// Custom AssemblyLoadContext for plugin isolation.
    /// Supports unloading and independent dependency resolution.
    /// </summary>
    public class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _pluginPath;

        public PluginLoadContext(string pluginPath, string name) : base(name, isCollectible: true)
        {
            _pluginPath = pluginPath;
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // First try to resolve from plugin directory
            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            // Fall back to default context for shared assemblies (SDK, etc.)
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

    /// <summary>
    /// Information about a loaded plugin.
    /// </summary>
    public class LoadedPluginInfo
    {
        public required string PluginId { get; init; }
        public required string Name { get; init; }
        public required string Version { get; init; }
        public required PluginCategory Category { get; init; }
        public required string AssemblyPath { get; init; }
        public required DateTime LoadedAt { get; init; }
        public IPlugin? Plugin { get; init; }
    }

    /// <summary>
    /// Context for managing graceful drain of in-flight operations.
    /// </summary>
    public class DrainContext
    {
        private readonly string _pluginId;
        private readonly TimeSpan _timeout;
        private readonly TaskCompletionSource _drainComplete = new();
        private readonly SemaphoreSlim _operationLock = new(1);
        private int _activeOperations;
        private bool _draining;

        public string PluginId => _pluginId;
        public int ActiveOperations => _activeOperations;
        public bool IsDraining => _draining;

        public DrainContext(string pluginId, TimeSpan timeout)
        {
            _pluginId = pluginId;
            _timeout = timeout;
        }

        /// <summary>
        /// Begin an operation - returns a disposable that completes the operation when disposed.
        /// Returns null if draining has started.
        /// </summary>
        public IDisposable? BeginOperation()
        {
            if (_draining) return null;

            Interlocked.Increment(ref _activeOperations);
            return new OperationScope(this);
        }

        /// <summary>
        /// Signal that an operation has completed.
        /// </summary>
        public void CompleteOperation()
        {
            var remaining = Interlocked.Decrement(ref _activeOperations);
            if (_draining && remaining <= 0)
            {
                _drainComplete.TrySetResult();
            }
        }

        /// <summary>
        /// Wait for all operations to complete or timeout.
        /// </summary>
        public async Task WaitForDrainAsync()
        {
            _draining = true;

            if (_activeOperations <= 0)
            {
                return;
            }

            // Wait for drain or timeout
            var timeoutTask = Task.Delay(_timeout);
            var completed = await Task.WhenAny(_drainComplete.Task, timeoutTask);

            if (completed == timeoutTask)
            {
                // Force complete on timeout
                _drainComplete.TrySetResult();
            }
        }

        /// <summary>
        /// Signal that drain is complete externally.
        /// </summary>
        public void SignalDrainComplete()
        {
            _drainComplete.TrySetResult();
        }

        private class OperationScope : IDisposable
        {
            private readonly DrainContext _context;
            private bool _disposed;

            public OperationScope(DrainContext context)
            {
                _context = context;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _context.CompleteOperation();
                }
            }
        }
    }

    /// <summary>
    /// Interface for plugins that support state preservation.
    /// </summary>
    public interface IStatefulPlugin
    {
        /// <summary>
        /// Get the current state for persistence.
        /// </summary>
        Task<Dictionary<string, object>> GetStateAsync();

        /// <summary>
        /// Restore state from a previous session.
        /// </summary>
        Task SetStateAsync(Dictionary<string, object> state);
    }

    /// <summary>
    /// Attribute to mark properties for automatic state preservation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class PreserveStateAttribute : Attribute
    {
    }
}
