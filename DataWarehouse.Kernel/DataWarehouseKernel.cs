using DataWarehouse.Kernel.Configuration;
using DataWarehouse.Kernel.Messaging;
using DataWarehouse.Kernel.Pipeline;
using DataWarehouse.Kernel.Plugins;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Primitives.Configuration;
using DataWarehouse.SDK.Services;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;

// Use SDK's MessageTopics (not Kernel's removed duplicate)
using static DataWarehouse.SDK.Contracts.MessageTopics;

namespace DataWarehouse.Kernel
{
    /// <summary>
    /// The DataWarehouse Kernel - Central orchestrator for all operations.
    ///
    /// Responsibilities:
    /// - Loads and manages plugins dynamically
    /// - Routes messages/commands between plugins
    /// - Manages pipeline execution order (with runtime overrides)
    /// - Provides storage abstraction (main storage, cache)
    /// - AI-agnostic: works with any AI provider
    /// - Non-blocking operation with background job support
    /// </summary>
    public sealed class DataWarehouseKernel : IDataWarehouse, IAsyncDisposable
    {
        private readonly KernelConfiguration _config;
        private readonly PluginRegistry _registry;
        private readonly DefaultMessageBus _messageBus;
        private readonly DefaultPipelineOrchestrator _pipelineOrchestrator;
        private readonly ILogger<DataWarehouseKernel>? _logger;
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly ConcurrentDictionary<string, Task> _backgroundJobs = new();
        private readonly SemaphoreSlim _initLock = new(1, 1);

        private IStorageProvider? _primaryStorage;
        private IStorageProvider? _cacheStorage;
        private bool _isInitialized;
        private bool _isDisposed;

        /// <summary>Default path for the unified configuration file.</summary>
        private const string DefaultConfigPath = "./config/datawarehouse-config.xml";

        /// <summary>Default path for the configuration audit log.</summary>
        private const string AuditLogPath = "./config/config-audit.log";

        /// <summary>The unified configuration loaded at startup.</summary>
        private DataWarehouseConfiguration? _currentConfiguration;

        /// <summary>Audit log for configuration changes.</summary>
        private ConfigurationAuditLog? _auditLog;

        /// <summary>Gets the current unified configuration.</summary>
        public DataWarehouseConfiguration CurrentConfiguration => _currentConfiguration ?? ConfigurationPresets.CreateStandard();

        /// <summary>
        /// Unique identifier for this kernel instance.
        /// </summary>
        public string KernelId { get; }

        /// <summary>
        /// Current operating mode.
        /// </summary>
        public OperatingMode OperatingMode => _config.OperatingMode;

        /// <summary>
        /// Whether the kernel is initialized and ready.
        /// </summary>
        public bool IsReady => _isInitialized && !_isDisposed;

        /// <summary>
        /// The message bus for inter-plugin communication.
        /// </summary>
        public IMessageBus MessageBus => _messageBus;

        /// <summary>
        /// The pipeline orchestrator for transformation chains.
        /// </summary>
        public IPipelineOrchestrator PipelineOrchestrator => _pipelineOrchestrator;

        /// <summary>
        /// Plugin registry for accessing loaded plugins.
        /// </summary>
        public PluginRegistry Plugins => _registry;

        /// <summary>
        /// Creates a new DataWarehouse Kernel instance.
        /// Use KernelBuilder for fluent configuration.
        /// </summary>
        internal DataWarehouseKernel(KernelConfiguration config, ILogger<DataWarehouseKernel>? logger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger;
            KernelId = config.KernelId ?? Guid.NewGuid().ToString("N")[..12];

            _registry = new PluginRegistry();
            _registry.SetOperatingMode(config.OperatingMode);

            _messageBus = new DefaultMessageBus(logger);
            _pipelineOrchestrator = new DefaultPipelineOrchestrator(_registry, _messageBus, logger);
        }

        /// <summary>
        /// Initialize the kernel and load plugins.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (_isInitialized) return;

                _logger?.LogInformation("Initializing DataWarehouse Kernel {KernelId}", KernelId);

                // Load unified configuration from file (or create default if missing)
                _currentConfiguration = ConfigurationSerializer.LoadFromFile(DefaultConfigPath);
                _auditLog = new ConfigurationAuditLog(AuditLogPath);
                _logger?.LogInformation("Configuration loaded from {Path}, preset: {Preset}",
                    DefaultConfigPath, _currentConfiguration.PresetName);

                // Publish startup event
                await _messageBus.PublishAsync(SystemStartup, new PluginMessage
                {
                    Type = "system.startup",
                    Payload = new Dictionary<string, object>
                    {
                        ["KernelId"] = KernelId,
                        ["OperatingMode"] = OperatingMode.ToString(),
                        ["Timestamp"] = DateTimeOffset.UtcNow
                    }
                }, ct);

                // Register built-in plugins
                await RegisterBuiltInPluginsAsync(ct);

                // Load plugins from configured paths
                if (_config.PluginPaths.Count > 0)
                {
                    await LoadPluginsFromPathsAsync(_config.PluginPaths, ct);
                }

                // Set default pipeline configuration
                _pipelineOrchestrator.SetConfiguration(
                    _config.PipelineConfiguration ?? PipelineConfiguration.CreateDefault());

                // Initialize primary storage
                await InitializeStorageAsync(ct);

                _isInitialized = true;
                _logger?.LogInformation("DataWarehouse Kernel {KernelId} initialized successfully", KernelId);
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Register a plugin with the kernel.
        /// </summary>
        public async Task<HandshakeResponse> RegisterPluginAsync(IPlugin plugin, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(plugin);

            _logger?.LogDebug("Registering plugin {PluginId} ({PluginName})", plugin.Id, plugin.Name);

            // Perform handshake
            var request = new HandshakeRequest
            {
                KernelId = KernelId,
                ProtocolVersion = "1.0",
                Timestamp = DateTime.UtcNow,
                Mode = OperatingMode,
                RootPath = _config.RootPath ?? Environment.CurrentDirectory
            };

            var response = await plugin.OnHandshakeAsync(request);

            if (response.Success)
            {
                // Inject kernel services so plugins can use IMessageBus, IStorageEngine, etc.
                if (plugin is PluginBase pluginBase)
                {
                    pluginBase.InjectKernelServices(_messageBus, null, null);

                    // Inject unified configuration
                    if (_currentConfiguration != null)
                    {
                        pluginBase.InjectConfiguration(_currentConfiguration);
                    }
                }

                _registry.Register(plugin);

                // Register with pipeline if it's a transformation plugin
                if (plugin is IDataTransformation transformation)
                {
                    _pipelineOrchestrator.RegisterStage(transformation);
                }

                // Start feature plugins
                if (plugin is IFeaturePlugin feature && _config.AutoStartFeatures)
                {
                    _ = StartFeatureInBackgroundAsync(feature, ct);
                }

                // Publish plugin loaded event
                await _messageBus.PublishAsync(PluginLoaded, new PluginMessage
                {
                    Type = "plugin.loaded",
                    Payload = new Dictionary<string, object>
                    {
                        ["PluginId"] = plugin.Id,
                        ["PluginName"] = plugin.Name,
                        ["Category"] = plugin.Category.ToString()
                    }
                }, ct);

                _logger?.LogInformation("Plugin {PluginId} registered successfully", plugin.Id);
            }
            else
            {
                _logger?.LogWarning("Plugin {PluginId} handshake failed: {Error}",
                    plugin.Id, response.ErrorMessage);
            }

            return response;
        }

        /// <summary>
        /// Set the primary storage provider.
        /// </summary>
        public void SetPrimaryStorage(IStorageProvider storage)
        {
            _primaryStorage = storage ?? throw new ArgumentNullException(nameof(storage));
            _logger?.LogInformation("Primary storage set to {Scheme}", storage.Scheme);
        }

        /// <summary>
        /// Set the cache storage provider.
        /// </summary>
        public void SetCacheStorage(IStorageProvider storage)
        {
            _cacheStorage = storage;
            _logger?.LogInformation("Cache storage set to {Scheme}", storage?.Scheme ?? "null");
        }

        /// <summary>
        /// Get the primary storage provider.
        /// </summary>
        public IStorageProvider? GetPrimaryStorage() => _primaryStorage;

        /// <summary>
        /// Get the cache storage provider.
        /// </summary>
        public IStorageProvider? GetCacheStorage() => _cacheStorage;

        /// <summary>
        /// Send a message/command and get a response.
        /// </summary>
        public Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            return _messageBus.SendAsync(topic, message, ct);
        }

        /// <summary>
        /// Publish a message to all subscribers.
        /// </summary>
        public Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
        {
            return _messageBus.PublishAsync(topic, message, ct);
        }

        /// <summary>
        /// Subscribe to messages on a topic.
        /// </summary>
        public IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler)
        {
            return _messageBus.Subscribe(topic, handler);
        }

        /// <summary>
        /// Execute the write pipeline (compress, encrypt, etc.).
        /// </summary>
        public Task<Stream> ExecuteWritePipelineAsync(Stream input, PipelineContext context, CancellationToken ct = default)
        {
            return _pipelineOrchestrator.ExecuteWritePipelineAsync(input, context, ct);
        }

        /// <summary>
        /// Execute the read pipeline (decrypt, decompress, etc.).
        /// </summary>
        public Task<Stream> ExecuteReadPipelineAsync(Stream input, PipelineContext context, CancellationToken ct = default)
        {
            return _pipelineOrchestrator.ExecuteReadPipelineAsync(input, context, ct);
        }

        /// <summary>
        /// Override pipeline configuration at runtime.
        /// </summary>
        /// <param name="config">New configuration</param>
        /// <param name="persistent">If true, persists for all future operations. If false, applies once.</param>
        public void SetPipelineConfiguration(PipelineConfiguration config, bool persistent = true)
        {
            if (persistent)
            {
                _pipelineOrchestrator.SetConfiguration(config);
                _logger?.LogInformation("Pipeline configuration updated persistently");
            }
            else
            {
                // One-time override handled by passing config directly to Execute methods
                _logger?.LogDebug("Pipeline configuration set for one-time use");
            }
        }

        /// <summary>
        /// Run a background job.
        /// </summary>
        public string RunInBackground(Func<CancellationToken, Task> job, string? jobId = null)
        {
            jobId ??= Guid.NewGuid().ToString("N")[..8];

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            var task = Task.Run(async () =>
            {
                try
                {
                    await job(linkedCts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Background job {JobId} failed", jobId);
                }
                finally
                {
                    _backgroundJobs.TryRemove(jobId, out _);
                    linkedCts.Dispose();
                }
            }, linkedCts.Token);

            _backgroundJobs[jobId] = task;
            _logger?.LogDebug("Background job {JobId} started", jobId);

            return jobId;
        }

        /// <summary>
        /// Get a plugin by type.
        /// </summary>
        public T? GetPlugin<T>() where T : class, IPlugin => _registry.GetPlugin<T>();

        /// <summary>
        /// Get a plugin by ID.
        /// </summary>
        public T? GetPlugin<T>(string id) where T : class, IPlugin => _registry.GetPlugin<T>(id);

        /// <summary>
        /// Get all plugins of a type.
        /// </summary>
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => _registry.GetPlugins<T>();

        private async Task RegisterBuiltInPluginsAsync(CancellationToken ct)
        {
            // Register built-in in-memory storage for testing
            var inMemoryStorage = new InMemoryStoragePlugin();
            await RegisterPluginAsync(inMemoryStorage, ct);

            // Set as primary if no other storage configured
            if (_config.UseInMemoryStorageByDefault)
            {
                SetPrimaryStorage(inMemoryStorage);
            }
        }

        private async Task LoadPluginsFromPathsAsync(IEnumerable<string> paths, CancellationToken ct)
        {
            foreach (var path in paths)
            {
                if (!Directory.Exists(path))
                {
                    _logger?.LogWarning("Plugin path does not exist: {Path}", path);
                    continue;
                }

                var assemblies = Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories);
                foreach (var assemblyPath in assemblies)
                {
                    try
                    {
                        await LoadPluginsFromAssemblyAsync(assemblyPath, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to load plugins from {Assembly}", assemblyPath);
                    }
                }
            }
        }

        private async Task LoadPluginsFromAssemblyAsync(string assemblyPath, CancellationToken ct)
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            var pluginTypes = assembly.GetTypes()
                .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

            foreach (var type in pluginTypes)
            {
                try
                {
                    if (Activator.CreateInstance(type) is IPlugin plugin)
                    {
                        await RegisterPluginAsync(plugin, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to instantiate plugin {Type}", type.FullName);
                }
            }
        }

        private async Task InitializeStorageAsync(CancellationToken ct)
        {
            // If primary storage not set, find one from plugins
            if (_primaryStorage == null)
            {
                _primaryStorage = _registry.GetPlugin<IStorageProvider>();
                if (_primaryStorage != null)
                {
                    _logger?.LogInformation("Auto-selected primary storage: {Scheme}", _primaryStorage.Scheme);
                }
            }
        }

        private async Task StartFeatureInBackgroundAsync(IFeaturePlugin feature, CancellationToken ct)
        {
            RunInBackground(async token =>
            {
                try
                {
                    await feature.StartAsync(token);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Feature plugin {Id} failed to start", ((IPlugin)feature).Id);
                }
            }, $"feature-{((IPlugin)feature).Id}");
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _logger?.LogInformation("Shutting down DataWarehouse Kernel {KernelId}", KernelId);

            // Signal shutdown
            _shutdownCts.Cancel();

            // Publish shutdown event
            try
            {
                await _messageBus.PublishAsync(SystemShutdown, new PluginMessage
                {
                    Type = "system.shutdown",
                    Payload = new Dictionary<string, object>
                    {
                        ["KernelId"] = KernelId,
                        ["Timestamp"] = DateTimeOffset.UtcNow
                    }
                });
            }
            catch
            {
                // Best-effort shutdown notification - ignore failures during disposal
            }

            // Stop all feature plugins
            foreach (var feature in _registry.GetPlugins<IFeaturePlugin>())
            {
                try
                {
                    await feature.StopAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error stopping feature plugin");
                }
            }

            // Wait for background jobs
            var jobs = _backgroundJobs.Values.ToArray();
            if (jobs.Length > 0)
            {
                await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(10));
            }

            _shutdownCts.Dispose();
            _initLock.Dispose();

            _logger?.LogInformation("DataWarehouse Kernel {KernelId} shut down", KernelId);
        }
    }
}
