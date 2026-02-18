using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Configuration options for <see cref="DriverLoader"/>.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Driver loader configuration (HAL-04)")]
    public sealed record DriverLoaderOptions
    {
        /// <summary>
        /// Directory to watch for hot-plug driver loading.
        /// </summary>
        public string? DriverDirectory { get; init; }

        /// <summary>
        /// Whether to watch the driver directory for new DLLs and auto-load them.
        /// </summary>
        public bool EnableHotPlug { get; init; } = false;

        /// <summary>
        /// Whether to automatically load drivers when matching hardware is detected.
        /// </summary>
        /// <remarks>
        /// Requires <see cref="IHardwareProbe"/> to be provided to the constructor.
        /// </remarks>
        public bool AutoLoadOnHardwareChange { get; init; } = false;

        /// <summary>
        /// Maximum number of drivers that can be loaded simultaneously.
        /// </summary>
        public int MaxLoadedDrivers { get; init; } = 100;

        /// <summary>
        /// Grace period to wait for driver shutdown before forcing unload.
        /// </summary>
        public TimeSpan UnloadGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Implements dynamic storage driver loading with assembly isolation and hot-plug support.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <see cref="PluginAssemblyLoadContext"/> for isolated, collectible driver loading.
    /// Drivers can be unloaded at runtime without process restart.
    /// </para>
    /// <para>
    /// Hot-plug support:
    /// <list type="bullet">
    /// <item>Directory watching: Auto-loads drivers when DLLs are added to the configured directory</item>
    /// <item>Hardware change: Auto-loads drivers when matching hardware is detected via <see cref="IHardwareProbe"/></item>
    /// </list>
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Dynamic driver loading implementation (HAL-04)")]
    public sealed class DriverLoader : IDriverLoader
    {
        private readonly DriverLoaderOptions _options;
        private readonly IHardwareProbe? _probe;
        private readonly ConcurrentDictionary<string, DriverHandle> _loadedDrivers = new();
        private readonly ConcurrentDictionary<string, PluginAssemblyLoadContext> _contexts = new();
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private FileSystemWatcher? _directoryWatcher;
        private bool _disposed;

        /// <inheritdoc/>
        public event EventHandler<DriverEventArgs>? OnDriverLoaded;

        /// <inheritdoc/>
        public event EventHandler<DriverEventArgs>? OnDriverUnloaded;

        /// <summary>
        /// Initializes a new instance of the <see cref="DriverLoader"/> class.
        /// </summary>
        /// <param name="options">Optional configuration options.</param>
        /// <param name="probe">Optional hardware probe for auto-load on hardware change.</param>
        public DriverLoader(DriverLoaderOptions? options = null, IHardwareProbe? probe = null)
        {
            _options = options ?? new DriverLoaderOptions();
            _probe = probe;

            if (_options.EnableHotPlug && !string.IsNullOrWhiteSpace(_options.DriverDirectory))
            {
                InitializeDirectoryWatcher();
            }

            if (_options.AutoLoadOnHardwareChange && _probe != null)
            {
                _probe.OnHardwareChanged += OnHardwareChangedHandler;
            }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<DriverInfo>> ScanAsync(string assemblyPath, CancellationToken cancellationToken = default)
        {
            if (assemblyPath == null)
                throw new ArgumentNullException(nameof(assemblyPath));
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException($"Assembly not found: {assemblyPath}", assemblyPath);

            var results = new List<DriverInfo>();

            // Use a temporary collectible context for scanning to avoid locking the file
            var tempContextId = $"scan-{Guid.NewGuid()}";
            var tempContext = new PluginAssemblyLoadContext(tempContextId, assemblyPath);

            try
            {
                var assembly = tempContext.LoadFromAssemblyPath(assemblyPath);

                foreach (var type in GetTypesWithAttribute(assembly))
                {
                    var attribute = type.GetCustomAttribute<StorageDriverAttribute>();
                    if (attribute != null)
                    {
                        results.Add(new DriverInfo
                        {
                            AssemblyPath = assemblyPath,
                            TypeName = type.FullName ?? type.Name,
                            Name = attribute.Name,
                            Description = attribute.Description,
                            Version = attribute.Version,
                            SupportedDevices = attribute.SupportedDevices,
                            AutoLoad = attribute.AutoLoad
                        });
                    }
                }

                return results.AsReadOnly();
            }
            finally
            {
                // Unload the temporary context
                tempContext.Unload();
            }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<DriverInfo>> ScanDirectoryAsync(string directoryPath, string searchPattern = "*.dll", CancellationToken cancellationToken = default)
        {
            if (directoryPath == null)
                throw new ArgumentNullException(nameof(directoryPath));
            if (!Directory.Exists(directoryPath))
                throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");

            var allResults = new List<DriverInfo>();
            var files = Directory.GetFiles(directoryPath, searchPattern);

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var fileResults = await ScanAsync(file, cancellationToken);
                    allResults.AddRange(fileResults);
                }
                catch
                {
                    // Skip assemblies that fail to load
                    // In production, this should log a warning
                }
            }

            return allResults.AsReadOnly();
        }

        /// <inheritdoc/>
        public async Task<DriverHandle> LoadAsync(string assemblyPath, string typeName, CancellationToken cancellationToken = default)
        {
            if (assemblyPath == null)
                throw new ArgumentNullException(nameof(assemblyPath));
            if (typeName == null)
                throw new ArgumentNullException(nameof(typeName));
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException($"Assembly not found: {assemblyPath}", assemblyPath);

            ObjectDisposedException.ThrowIf(_disposed, this);

            await _loadLock.WaitAsync(cancellationToken);
            try
            {
                if (_loadedDrivers.Count >= _options.MaxLoadedDrivers)
                {
                    throw new InvalidOperationException(
                        $"Maximum number of loaded drivers ({_options.MaxLoadedDrivers}) reached.");
                }

                // Create isolated load context
                var handleId = Guid.NewGuid().ToString();
                var context = new PluginAssemblyLoadContext(handleId, assemblyPath);

                try
                {
                    // Load assembly
                    var assembly = context.LoadFromAssemblyPath(assemblyPath);

                    // Find type
                    var type = assembly.GetType(typeName);
                    if (type == null)
                    {
                        throw new TypeLoadException($"Type '{typeName}' not found in assembly '{assemblyPath}'.");
                    }

                    // Verify attribute
                    var attribute = type.GetCustomAttribute<StorageDriverAttribute>();
                    if (attribute == null)
                    {
                        throw new InvalidOperationException(
                            $"Type '{typeName}' does not have a [StorageDriver] attribute.");
                    }

                    // Create driver info
                    var info = new DriverInfo
                    {
                        AssemblyPath = assemblyPath,
                        TypeName = typeName,
                        Name = attribute.Name,
                        Description = attribute.Description,
                        Version = attribute.Version,
                        SupportedDevices = attribute.SupportedDevices,
                        AutoLoad = attribute.AutoLoad
                    };

                    // Create handle
                    var handle = new DriverHandle(handleId, info, type);

                    // Register
                    if (!_loadedDrivers.TryAdd(handleId, handle))
                    {
                        throw new InvalidOperationException($"Driver handle ID collision: {handleId}");
                    }

                    if (!_contexts.TryAdd(handleId, context))
                    {
                        _loadedDrivers.TryRemove(handleId, out _);
                        throw new InvalidOperationException($"Context ID collision: {handleId}");
                    }

                    // Fire event
                    OnDriverLoaded?.Invoke(this, new DriverEventArgs
                    {
                        Handle = handle,
                        EventType = DriverEventType.Loaded
                    });

                    return handle;
                }
                catch
                {
                    // Clean up context on failure
                    context.Unload();
                    throw;
                }
            }
            finally
            {
                _loadLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task UnloadAsync(DriverHandle handle, CancellationToken cancellationToken = default)
        {
            if (handle == null)
                throw new ArgumentNullException(nameof(handle));

            ObjectDisposedException.ThrowIf(_disposed, this);

            await _loadLock.WaitAsync(cancellationToken);
            try
            {
                if (!_loadedDrivers.TryRemove(handle.Id, out var removedHandle))
                {
                    throw new InvalidOperationException($"Driver '{handle.Info.Name}' is not currently loaded.");
                }

                if (_contexts.TryRemove(handle.Id, out var context))
                {
                    // Unload the context - triggers collectible cleanup
                    context.Unload();
                }

                // Mark as unloaded
                handle.IsLoaded = false;

                // Fire event
                OnDriverUnloaded?.Invoke(this, new DriverEventArgs
                {
                    Handle = handle,
                    EventType = DriverEventType.Unloaded
                });
            }
            finally
            {
                _loadLock.Release();
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<DriverHandle> GetLoadedDrivers()
        {
            return _loadedDrivers.Values.ToList().AsReadOnly();
        }

        /// <inheritdoc/>
        public Task<DriverHandle?> GetDriverAsync(string driverName, CancellationToken cancellationToken = default)
        {
            if (driverName == null)
                throw new ArgumentNullException(nameof(driverName));

            var handle = _loadedDrivers.Values.FirstOrDefault(h => h.Info.Name == driverName);
            return Task.FromResult(handle);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Unsubscribe from hardware probe
            if (_probe != null && _options.AutoLoadOnHardwareChange)
            {
                _probe.OnHardwareChanged -= OnHardwareChangedHandler;
            }

            // Stop directory watcher
            if (_directoryWatcher != null)
            {
                _directoryWatcher.EnableRaisingEvents = false;
                _directoryWatcher.Dispose();
                _directoryWatcher = null;
            }

            // Unload all drivers
            var handles = _loadedDrivers.Values.ToList();
            foreach (var handle in handles)
            {
                try
                {
                    // Use synchronous wait with grace period
                    var unloadTask = UnloadAsync(handle);
                    if (!unloadTask.Wait(_options.UnloadGracePeriod))
                    {
                        // Force unload if grace period exceeded
                        if (_contexts.TryRemove(handle.Id, out var context))
                        {
                            context.Unload();
                        }
                    }
                }
                catch
                {
                    // Best-effort cleanup
                }
            }

            _loadedDrivers.Clear();
            _contexts.Clear();
            _loadLock.Dispose();
        }

        private void InitializeDirectoryWatcher()
        {
            if (string.IsNullOrWhiteSpace(_options.DriverDirectory))
                return;

            if (!Directory.Exists(_options.DriverDirectory))
            {
                // Directory doesn't exist yet - could be created later
                return;
            }

            _directoryWatcher = new FileSystemWatcher(_options.DriverDirectory)
            {
                Filter = "*.dll",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };

            _directoryWatcher.Created += async (sender, e) => await OnDriverFileCreated(e.FullPath);
            _directoryWatcher.Deleted += async (sender, e) => await OnDriverFileDeleted(e.FullPath);

            _directoryWatcher.EnableRaisingEvents = true;
        }

        private async Task OnDriverFileCreated(string filePath)
        {
            if (_disposed)
                return;

            // Debounce: wait a bit for file to be fully written
            await Task.Delay(500);

            try
            {
                var drivers = await ScanAsync(filePath);
                foreach (var driverInfo in drivers.Where(d => d.AutoLoad))
                {
                    try
                    {
                        await LoadAsync(driverInfo.AssemblyPath, driverInfo.TypeName);
                    }
                    catch
                    {
                        // Log and continue - don't fail hot-plug on individual driver load failure
                    }
                }
            }
            catch
            {
                // Log and continue - don't fail hot-plug on scan failure
            }
        }

        private async Task OnDriverFileDeleted(string filePath)
        {
            if (_disposed)
                return;

            try
            {
                // Find all loaded drivers from this assembly
                var handles = _loadedDrivers.Values
                    .Where(h => string.Equals(h.Info.AssemblyPath, filePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var handle in handles)
                {
                    try
                    {
                        await UnloadAsync(handle);
                    }
                    catch
                    {
                        // Log and continue - best-effort unload
                    }
                }
            }
            catch
            {
                // Log and continue - don't fail hot-plug on unload failure
            }
        }

        private void OnHardwareChangedHandler(object? sender, HardwareChangeEventArgs e)
        {
            // Event handlers must be void, so we use Task.Run for async work
            _ = Task.Run(async () =>
            {
                if (_disposed || !_options.AutoLoadOnHardwareChange)
                    return;

                if (e.ChangeType != HardwareChangeType.Added)
                    return;

                try
                {
                    // Check if we already have a loaded driver supporting this device type
                    var hasMatchingDriver = _loadedDrivers.Values.Any(h =>
                        (h.Info.SupportedDevices & e.Device.Type) != 0);

                    if (hasMatchingDriver)
                        return;

                    // Scan driver directory for a matching driver
                    if (string.IsNullOrWhiteSpace(_options.DriverDirectory) || !Directory.Exists(_options.DriverDirectory))
                        return;

                    var availableDrivers = await ScanDirectoryAsync(_options.DriverDirectory);
                    var matchingDriver = availableDrivers.FirstOrDefault(d =>
                        d.AutoLoad && (d.SupportedDevices & e.Device.Type) != 0);

                    if (matchingDriver != null)
                    {
                        try
                        {
                            await LoadAsync(matchingDriver.AssemblyPath, matchingDriver.TypeName);
                        }
                        catch
                        {
                            // Log and continue - don't fail hardware change handling
                        }
                    }
                }
                catch
                {
                    // Log and continue - don't fail hardware change handling
                }
            });
        }

        private static IEnumerable<Type> GetTypesWithAttribute(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes()
                    .Where(t => !t.IsAbstract && t.IsClass && t.GetCustomAttribute<StorageDriverAttribute>() != null);
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Return types that loaded successfully
                return ex.Types.Where(t => t != null && !t.IsAbstract && t.IsClass && t.GetCustomAttribute<StorageDriverAttribute>() != null)!;
            }
        }
    }
}
