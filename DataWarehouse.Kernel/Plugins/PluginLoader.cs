using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace DataWarehouse.Kernel.Plugins
{
    /// <summary>
    /// Configuration for plugin loading security validation.
    /// </summary>
    public class PluginSecurityConfig
    {
        /// <summary>
        /// Whether to require plugins to be signed with a strong name.
        /// Default: false (development mode). Set to true for production.
        /// </summary>
        public bool RequireSignedAssemblies { get; set; } = false;

        /// <summary>
        /// List of trusted publisher public key tokens (hex strings).
        /// If empty and RequireSignedAssemblies is true, all signed assemblies are trusted.
        /// </summary>
        public HashSet<string> TrustedPublicKeyTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Allowed assembly prefixes. If non-empty, only assemblies whose names start with these prefixes are allowed.
        /// </summary>
        public HashSet<string> AllowedAssemblyPrefixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Blocked assembly names. Assemblies matching these names will not be loaded.
        /// </summary>
        public HashSet<string> BlockedAssemblies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether to validate assembly hash against a known manifest.
        /// </summary>
        public bool ValidateAssemblyHash { get; set; } = false;

        /// <summary>
        /// Known assembly hashes (file name -> SHA256 hash).
        /// Only used when ValidateAssemblyHash is true.
        /// </summary>
        public Dictionary<string, string> KnownAssemblyHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Maximum assembly file size in bytes. 0 means unlimited.
        /// </summary>
        public long MaxAssemblySize { get; set; } = 50 * 1024 * 1024; // 50MB default

        /// <summary>
        /// Whether to log detailed security validation information.
        /// </summary>
        public bool EnableSecurityAuditLog { get; set; } = true;
    }

    /// <summary>
    /// Result of plugin security validation.
    /// </summary>
    public class PluginSecurityValidationResult
    {
        public bool IsValid { get; init; }
        public string AssemblyName { get; init; } = string.Empty;
        public string AssemblyPath { get; init; } = string.Empty;
        public string? PublicKeyToken { get; init; }
        public string? Hash { get; init; }
        public List<string> Errors { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Manages hot plugin loading and unloading using AssemblyLoadContext.
    /// Enables updating plugins without kernel restart.
    ///
    /// Security features:
    /// - Optional strong name signature validation
    /// - Trusted publisher whitelist
    /// - Assembly hash verification
    /// - Size limits to prevent DoS
    /// - Security audit logging
    /// </summary>
    public sealed class PluginLoader : IPluginReloader, IDisposable
    {
        private readonly ConcurrentDictionary<string, PluginContext> _loadedPlugins = new();
        private readonly PluginRegistry _registry;
        private readonly IKernelContext _kernelContext;
        private readonly string _pluginDirectory;
        private readonly object _reloadLock = new();
        private readonly PluginSecurityConfig _securityConfig;

        public event Action<PluginReloadEvent>? OnPluginReloading;
        public event Action<PluginReloadEvent>? OnPluginReloaded;

        public PluginLoader(
            PluginRegistry registry,
            IKernelContext kernelContext,
            string? pluginDirectory = null,
            PluginSecurityConfig? securityConfig = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _kernelContext = kernelContext ?? throw new ArgumentNullException(nameof(kernelContext));
            _pluginDirectory = pluginDirectory ?? Path.Combine(kernelContext.RootPath, "plugins");
            _securityConfig = securityConfig ?? new PluginSecurityConfig();

            // Ensure plugin directory exists
            if (!Directory.Exists(_pluginDirectory))
            {
                Directory.CreateDirectory(_pluginDirectory);
            }
        }

        /// <summary>
        /// Validates a plugin assembly for security compliance before loading.
        /// </summary>
        public PluginSecurityValidationResult ValidateAssemblySecurity(string assemblyPath)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var fileName = Path.GetFileName(assemblyPath);

            if (!File.Exists(assemblyPath))
            {
                return new PluginSecurityValidationResult
                {
                    IsValid = false,
                    AssemblyPath = assemblyPath,
                    Errors = new List<string> { $"Assembly file not found: {assemblyPath}" }
                };
            }

            var fileInfo = new FileInfo(assemblyPath);

            // Check file size
            if (_securityConfig.MaxAssemblySize > 0 && fileInfo.Length > _securityConfig.MaxAssemblySize)
            {
                errors.Add($"Assembly exceeds maximum size: {fileInfo.Length} bytes (max: {_securityConfig.MaxAssemblySize})");
            }

            // Check blocked assemblies
            if (_securityConfig.BlockedAssemblies.Contains(fileName))
            {
                errors.Add($"Assembly is in blocked list: {fileName}");
            }

            // Check allowed prefixes
            if (_securityConfig.AllowedAssemblyPrefixes.Count > 0)
            {
                var allowed = _securityConfig.AllowedAssemblyPrefixes.Any(prefix =>
                    fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                {
                    errors.Add($"Assembly name does not match any allowed prefix: {fileName}");
                }
            }

            // Compute hash
            string? hash = null;
            try
            {
                using var stream = File.OpenRead(assemblyPath);
                using var sha256 = SHA256.Create();
                var hashBytes = sha256.ComputeHash(stream);
                hash = Convert.ToHexString(hashBytes);

                // Validate hash if required
                if (_securityConfig.ValidateAssemblyHash)
                {
                    if (_securityConfig.KnownAssemblyHashes.TryGetValue(fileName, out var expectedHash))
                    {
                        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"Assembly hash mismatch. Expected: {expectedHash}, Got: {hash}");
                        }
                    }
                    else
                    {
                        errors.Add($"Assembly not in known hash manifest: {fileName}");
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not compute assembly hash: {ex.Message}");
            }

            // Check strong name / signature
            string? publicKeyToken = null;
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                var tokenBytes = assemblyName.GetPublicKeyToken();

                if (tokenBytes != null && tokenBytes.Length > 0)
                {
                    publicKeyToken = Convert.ToHexString(tokenBytes);

                    // Validate against trusted publishers
                    if (_securityConfig.TrustedPublicKeyTokens.Count > 0)
                    {
                        if (!_securityConfig.TrustedPublicKeyTokens.Contains(publicKeyToken))
                        {
                            errors.Add($"Assembly signed with untrusted key: {publicKeyToken}");
                        }
                    }
                }
                else if (_securityConfig.RequireSignedAssemblies)
                {
                    errors.Add($"Assembly is not signed with a strong name: {fileName}");
                }
                else
                {
                    warnings.Add($"Assembly is not signed: {fileName}");
                }
            }
            catch (Exception ex)
            {
                if (_securityConfig.RequireSignedAssemblies)
                {
                    errors.Add($"Could not validate assembly signature: {ex.Message}");
                }
                else
                {
                    warnings.Add($"Could not validate assembly signature: {ex.Message}");
                }
            }

            var isValid = errors.Count == 0;

            // Audit log
            if (_securityConfig.EnableSecurityAuditLog)
            {
                if (isValid)
                {
                    _kernelContext.LogInfo($"[PluginSecurity] Validated assembly: {fileName} (Hash: {hash?[..16]}..., KeyToken: {publicKeyToken ?? "unsigned"})");
                }
                else
                {
                    _kernelContext.LogWarning($"[PluginSecurity] Rejected assembly: {fileName} - {string.Join("; ", errors)}");
                }
            }

            return new PluginSecurityValidationResult
            {
                IsValid = isValid,
                AssemblyName = fileName,
                AssemblyPath = assemblyPath,
                PublicKeyToken = publicKeyToken,
                Hash = hash,
                Errors = errors,
                Warnings = warnings
            };
        }

        /// <summary>
        /// Loads a plugin from an assembly file.
        /// Performs security validation before loading.
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

            // Security validation before loading
            var securityResult = ValidateAssemblySecurity(assemblyPath);
            if (!securityResult.IsValid)
            {
                return new PluginLoadResult
                {
                    Success = false,
                    Error = $"Security validation failed: {string.Join("; ", securityResult.Errors)}"
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
                            KernelId = _kernelContext.RootPath,
                            ProtocolVersion = "1.0",
                            Mode = _kernelContext.Mode,
                            RootPath = _kernelContext.RootPath,
                            Timestamp = DateTime.UtcNow,
                            AlreadyLoadedPlugins = []
                        };

                        var response = await plugin.OnHandshakeAsync(handshakeRequest);
                        if (!response.Success)
                        {
                            _kernelContext.LogWarning($"Plugin {plugin.Id} rejected handshake: {response.ErrorMessage}");
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
