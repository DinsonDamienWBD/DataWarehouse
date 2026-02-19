using DataWarehouse.Kernel.Configuration;
using DataWarehouse.Kernel.Messaging;
using DataWarehouse.Kernel.Pipeline;
using DataWarehouse.Kernel.Plugins;
using DataWarehouse.Kernel.Registry;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Primitives.Configuration;
using DataWarehouse.SDK.Services;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
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
        private readonly IMessageBus _enforcedMessageBus;
        private readonly AccessVerificationMatrix _accessMatrix;
        private readonly DefaultPipelineOrchestrator _pipelineOrchestrator;
        private readonly PluginCapabilityRegistry _capabilityRegistry;
        private readonly ILogger<DataWarehouseKernel>? _logger;
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly ConcurrentDictionary<string, Task> _backgroundJobs = new();
        private readonly SemaphoreSlim _initLock = new(1, 1);

        private readonly PluginLoader _pluginLoader;
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
        /// Capability registry for dynamic capability discovery.
        /// Enables dynamic endpoint generation and capability-based routing.
        /// </summary>
        public IPluginCapabilityRegistry CapabilityRegistry => _capabilityRegistry;

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

            // Build the access verification matrix with default production policies
            _accessMatrix = BuildDefaultAccessMatrix();

            // Wrap the raw bus with access enforcement for plugin-facing communication.
            // The raw _messageBus is retained for kernel-internal use (system lifecycle topics).
            _enforcedMessageBus = _messageBus.WithAccessEnforcement(
                _accessMatrix,
                onDenied: verdict => logger?.LogWarning(
                    "Access DENIED: Principal={Principal}, Topic={Topic}, Action={Action}, Rule={Rule}, Reason={Reason}",
                    verdict.Identity.EffectivePrincipalId, verdict.Resource, verdict.Action,
                    verdict.RuleId, verdict.Reason),
                onAllowed: null);

            _pipelineOrchestrator = new DefaultPipelineOrchestrator(_registry, _messageBus, logger);

            // Create capability registry with message bus integration
            _capabilityRegistry = new PluginCapabilityRegistry(_messageBus);

            // Create secure plugin loader with production-safe defaults (ISO-02, INFRA-01, ISO-05)
            // RequireSignedAssemblies defaults to true for production security.
            // Set to false only in development via KernelConfiguration.
            var pluginSecurityConfig = new PluginSecurityConfig
            {
                RequireSignedAssemblies = config.RequireSignedPluginAssemblies,
                EnableSecurityAuditLog = true,
                MaxAssemblySize = 50 * 1024 * 1024 // 50MB
            };
            var kernelContext = new LoggerKernelContext(
                logger,
                config.RootPath ?? Environment.CurrentDirectory,
                config.OperatingMode);
            _pluginLoader = new PluginLoader(
                _registry,
                kernelContext,
                pluginDirectory: null,
                securityConfig: pluginSecurityConfig);
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

                // INFRA-02: Validate and audit environment variable configuration overrides
                await ValidateEnvironmentOverridesAsync();

                // INFRA-03: Audit kernel startup event
                await _auditLog.LogChangeAsync("System", "kernel.lifecycle.startup", null, KernelId,
                    $"Kernel initialized in {OperatingMode} mode");

                // Subscribe to config change and plugin lifecycle events for audit logging
                SubscribeToAuditableEvents();

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
                    pluginBase.InjectKernelServices(_enforcedMessageBus, _capabilityRegistry, null);

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

                // INFRA-03: Audit plugin registration
                if (_auditLog != null)
                {
                    _ = _auditLog.LogChangeAsync("System", "kernel.plugin.load",
                        null, plugin.Id, $"Plugin {plugin.Name} ({plugin.Category}) registered");
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
                catch (OperationCanceledException)
                {
                    /* Cancellation is expected during shutdown */
                }
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

        /// <summary>
        /// Loads plugins from configured paths using the secure PluginLoader.
        /// All assembly loading goes through PluginLoader with hash/signature validation (ISO-02, INFRA-01).
        /// Assembly.LoadFrom is never called directly -- all loading uses isolated AssemblyLoadContext.
        /// </summary>
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
                    // Skip common runtime/SDK DLLs (same filter as PluginLoader.LoadAllPluginsAsync)
                    var fileName = Path.GetFileName(assemblyPath);
                    if (fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("DataWarehouse.SDK", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

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

        /// <summary>
        /// Loads plugins from a single assembly using the secure PluginLoader pipeline.
        /// Replaces direct Assembly.LoadFrom with validated, isolated loading (ISO-02, INFRA-01).
        /// Security validation includes: size limits, blocklist, hash verification, signature checks.
        /// </summary>
        private async Task LoadPluginsFromAssemblyAsync(string assemblyPath, CancellationToken ct)
        {
            // Route all assembly loading through PluginLoader for security validation
            // PluginLoader performs: hash verification, signature check, size limits, blocklist,
            // and loads into a collectible AssemblyLoadContext for isolation
            var result = await _pluginLoader.LoadPluginAsync(assemblyPath, ct);

            if (!result.Success)
            {
                _logger?.LogWarning("PluginLoader rejected assembly {Assembly}: {Error}",
                    assemblyPath, result.Error);
                return;
            }

            // PluginLoader already registered plugins with the registry.
            // Now inject kernel services (message bus, capability registry, configuration)
            // for each loaded plugin, since PluginLoader doesn't have access to these.
            if (result.PluginIds != null)
            {
                foreach (var pluginId in result.PluginIds)
                {
                    var plugin = _registry.GetAllPlugins().FirstOrDefault(p => p.Id == pluginId);
                    if (plugin is PluginBase pluginBase)
                    {
                        pluginBase.InjectKernelServices(_enforcedMessageBus, _capabilityRegistry, null);

                        if (_currentConfiguration != null)
                        {
                            pluginBase.InjectConfiguration(_currentConfiguration);
                        }
                    }

                    // Start feature plugins
                    if (plugin is IFeaturePlugin feature && _config.AutoStartFeatures)
                    {
                        _ = StartFeatureInBackgroundAsync(feature, ct);
                    }

                    // Register with pipeline if transformation
                    if (plugin is IDataTransformation transformation)
                    {
                        _pipelineOrchestrator.RegisterStage(transformation);
                    }
                }
            }

            _logger?.LogInformation("Loaded {Count} plugin(s) from {Assembly} via secure PluginLoader",
                result.PluginIds?.Length ?? 0, Path.GetFileName(assemblyPath));
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

        /// <summary>
        /// Builds the default access verification matrix with production-ready policies.
        /// Fail-closed: no rules = DENY. System/kernel/security topics restricted.
        /// </summary>
        private static AccessVerificationMatrix BuildDefaultAccessMatrix()
        {
            var matrix = new AccessVerificationMatrix();

            // --- System-level rules (apply globally) ---

            // Allow system:kernel principal to access ALL resources (kernel needs full access)
            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-kernel-allow-all",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                PrincipalPattern = "system:*",
                Resource = "*",
                Action = "*",
                Decision = LevelDecision.Allow,
                Description = "Kernel and system services have full access"
            });

            // Allow any authenticated principal to publish/subscribe to general collaboration topics
            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-storage-allow-authenticated",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                Resource = "storage.*",
                Action = "*",
                Decision = LevelDecision.Allow,
                Description = "Any authenticated principal can access storage topics"
            });

            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-pipeline-allow-authenticated",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                Resource = "pipeline.*",
                Action = "*",
                Decision = LevelDecision.Allow,
                Description = "Any authenticated principal can access pipeline topics"
            });

            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-intelligence-allow-authenticated",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                Resource = "intelligence.*",
                Action = "*",
                Decision = LevelDecision.Allow,
                Description = "Any authenticated principal can access intelligence topics"
            });

            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-compliance-allow-authenticated",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                Resource = "compliance.*",
                Action = "*",
                Decision = LevelDecision.Allow,
                Description = "Any authenticated principal can access compliance topics"
            });

            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-config-subscribe-allow",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                Resource = "config.*",
                Action = "*",
                Decision = LevelDecision.Allow,
                Description = "Any authenticated principal can subscribe to config topics"
            });

            // Deny non-system principals from kernel.* topics (users and AI agents)
            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-deny-kernel-topics-users",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                PrincipalPattern = "user:*",
                Resource = "kernel.*",
                Action = "*",
                Decision = LevelDecision.Deny,
                Description = "User principals denied access to kernel topics"
            });

            // Deny non-system principals from security.* topics
            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-deny-security-topics-users",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                PrincipalPattern = "user:*",
                Resource = "security.*",
                Action = "*",
                Decision = LevelDecision.Deny,
                Description = "User principals denied access to security topics"
            });

            // Deny non-system principals from system.* topics
            // (defense-in-depth — interceptor also has bypass list for kernel-internal system topics)
            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-deny-system-topics-users",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                PrincipalPattern = "user:*",
                Resource = "system.*",
                Action = "*",
                Decision = LevelDecision.Deny,
                Description = "User principals denied access to system topics"
            });

            // --- Tenant-level rules for tenant isolation (AUTH-09) ---
            // Tenant isolation is enforced by the hierarchy: when a message carries
            // a CommandIdentity with TenantId, the matrix evaluates at the Tenant level
            // using that TenantId as the scopeId. Since we use default-deny and only
            // system-level allows exist (which match scopeId="system"), tenant-scoped
            // operations will require explicit tenant-level rules to be provisioned
            // when tenants are created. This prevents cross-tenant access by default.

            // Allow tenant-scoped principals to access resources within their own tenant
            // The wildcard scopeId="*" means this rule applies to any tenant,
            // but the matrix evaluates with the identity's TenantId as scopeId,
            // so only messages from that tenant's principals will match.
            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "tenant-allow-own-resources",
                Level = HierarchyLevel.Tenant,
                ScopeId = "*",
                Resource = "*",
                Action = "*",
                Decision = LevelDecision.Allow,
                Description = "Tenant principals can access resources within their own tenant scope (AUTH-09)"
            });

            // Allow general topic access for any authenticated identity
            // This is the catch-all that enables inter-plugin communication.
            // The AccessEnforcementInterceptor rejects null-identity messages (fail-closed).
            // Deny rules above take absolute precedence over this allow.
            matrix.AddRule(new HierarchyAccessRule
            {
                RuleId = "sys-allow-authenticated-general",
                Level = HierarchyLevel.System,
                ScopeId = "system",
                Resource = "*",
                Action = "*",
                Decision = LevelDecision.Allow,
                Description = "Authenticated principals can access general topics (identity required)"
            });

            return matrix;
        }

        /// <summary>
        /// INFRA-02: Security-sensitive configuration keys that cannot be overridden
        /// via environment variables in production mode.
        /// </summary>
        private static readonly HashSet<string> ProtectedConfigKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "DATAWAREHOUSE_REQUIRE_SIGNED_ASSEMBLIES",
            "DATAWAREHOUSE_VERIFY_SSL",
            "DATAWAREHOUSE_VERIFY_SSL_CERTIFICATES",
            "DATAWAREHOUSE_DISABLE_AUTH",
            "DATAWAREHOUSE_DISABLE_ENCRYPTION",
            "DATAWAREHOUSE_ALLOW_UNSIGNED_PLUGINS"
        };

        /// <summary>
        /// INFRA-02: Configuration keys that trigger a warning when set via environment variables.
        /// </summary>
        private static readonly HashSet<string> WarningConfigKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "DATAWAREHOUSE_CREATE_DEFAULT_ADMIN",
            "DATAWAREHOUSE_DEBUG_MODE",
            "DATAWAREHOUSE_DISABLE_RATE_LIMITING"
        };

        /// <summary>
        /// INFRA-02: Validates environment variable configuration overrides.
        /// Blocks security-sensitive overrides in production and logs all env var config usage.
        /// </summary>
        private async Task ValidateEnvironmentOverridesAsync()
        {
            if (_auditLog == null) return;

            var isProduction = _config.OperatingMode == OperatingMode.Server ||
                               _config.OperatingMode == OperatingMode.Hyperscale;

            var envVars = Environment.GetEnvironmentVariables();
            foreach (var key in envVars.Keys)
            {
                var keyStr = key?.ToString();
                if (keyStr == null || !keyStr.StartsWith("DATAWAREHOUSE_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = envVars[key!]?.ToString();

                // Block protected keys in production
                if (isProduction && ProtectedConfigKeys.Contains(keyStr))
                {
                    _logger?.LogError(
                        "SECURITY: Environment variable {Key} cannot override security configuration in production mode (INFRA-02)",
                        keyStr);
                    await _auditLog.LogChangeAsync("System", $"env.override.blocked.{keyStr}",
                        null, "[BLOCKED]",
                        "Security-sensitive env var override blocked in production mode (INFRA-02)");
                    continue;
                }

                // Warn about sensitive keys
                if (WarningConfigKeys.Contains(keyStr))
                {
                    _logger?.LogWarning(
                        "SECURITY: Environment variable {Key} is overriding configuration. Review for security implications (INFRA-02)",
                        keyStr);
                }

                // Audit all DATAWAREHOUSE_ env var overrides
                await _auditLog.LogChangeAsync("System", $"env.override.{keyStr}",
                    null, value ?? "[empty]",
                    "Configuration set via environment variable");
            }
        }

        /// <summary>
        /// INFRA-03: Subscribe to message bus topics for audit logging.
        /// Logs config changes, plugin load/unload, and security events.
        /// </summary>
        private void SubscribeToAuditableEvents()
        {
            // Audit config changes
            _messageBus.Subscribe(ConfigChanged, async msg =>
            {
                if (_auditLog == null) return;
                var path = msg.Payload.TryGetValue("SettingPath", out var p) ? p?.ToString() : "unknown";
                var oldVal = msg.Payload.TryGetValue("OldValue", out var o) ? o?.ToString() : null;
                var newVal = msg.Payload.TryGetValue("NewValue", out var n) ? n?.ToString() : null;
                var user = msg.Payload.TryGetValue("User", out var u) ? u?.ToString() : "System";
                await _auditLog.LogChangeAsync(user ?? "System", path ?? "config.unknown", oldVal, newVal,
                    "Configuration change via message bus");
            });

            // Audit plugin unloads
            _messageBus.Subscribe(PluginUnloaded, async msg =>
            {
                if (_auditLog == null) return;
                var pluginId = msg.Payload.TryGetValue("PluginId", out var id) ? id?.ToString() : "unknown";
                await _auditLog.LogChangeAsync("System", "kernel.plugin.unload",
                    pluginId, null, $"Plugin {pluginId} unloaded");
            });
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _logger?.LogInformation("Shutting down DataWarehouse Kernel {KernelId}", KernelId);

            // INFRA-03: Audit kernel shutdown event
            if (_auditLog != null)
            {
                try
                {
                    _ = _auditLog.LogChangeAsync("System", "kernel.lifecycle.shutdown",
                        KernelId, null, "Kernel shutting down");
                }
                catch
                {
                    // Best-effort audit during shutdown
                }
            }

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

            // Stop all feature plugins with timeout (ISO-04, CVSS 7.1)
            // Each plugin gets 30 seconds to shut down gracefully. If a plugin exceeds
            // the timeout, it is logged as a warning and shutdown continues for other plugins.
            // This prevents a malicious or buggy plugin from blocking kernel shutdown indefinitely.
            foreach (var feature in _registry.GetPlugins<IFeaturePlugin>())
            {
                var pluginId = (feature as IPlugin)?.Id ?? "unknown";
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await feature.StopAsync().WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogWarning(
                        "Plugin {PluginId} exceeded 30-second shutdown timeout -- forcibly continuing (ISO-04)",
                        pluginId);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error stopping feature plugin {PluginId}", pluginId);
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

            _pluginLoader.Dispose();

            _logger?.LogInformation("DataWarehouse Kernel {KernelId} shut down", KernelId);
        }
    }

    /// <summary>
    /// Adapts ILogger to SDK IKernelContext for PluginLoader compatibility.
    /// Implements the SDK's IKernelContext (DataWarehouse.SDK.Contracts.IKernelContext) which is
    /// required by PluginLoader. Provides logging, plugin access stubs, and storage stubs.
    /// </summary>
    internal sealed class LoggerKernelContext : DataWarehouse.SDK.Contracts.IKernelContext
    {
        private readonly ILogger? _logger;
        private readonly PluginRegistry? _registry;

        public string RootPath { get; }
        public OperatingMode Mode { get; }
        public IKernelStorageService Storage { get; }

        public LoggerKernelContext(
            ILogger? logger,
            string rootPath,
            OperatingMode mode,
            PluginRegistry? registry = null)
        {
            _logger = logger;
            _registry = registry;
            RootPath = rootPath;
            Mode = mode;
            Storage = new NullKernelStorageService();
        }

        public T? GetPlugin<T>() where T : class, IPlugin => _registry?.GetPlugin<T>();
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => _registry?.GetPlugins<T>() ?? [];

        public void LogInfo(string message) => _logger?.LogInformation("{Message}", message);
        public void LogError(string message, Exception? ex = null) => _logger?.LogError(ex, "{Message}", message);
        public void LogWarning(string message) => _logger?.LogWarning("{Message}", message);
        public void LogDebug(string message) => _logger?.LogDebug("{Message}", message);
    }

    /// <summary>
    /// No-op storage service for kernel context adapter.
    /// Plugin loading does not require kernel storage access.
    /// </summary>
    internal sealed class NullKernelStorageService : IKernelStorageService
    {
        public Task SaveAsync(string path, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(string path, byte[] data, IDictionary<string, string>? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Stream?> LoadAsync(string path, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
        public Task<byte[]?> LoadBytesAsync(string path, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> DeleteAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<StorageItemInfo>> ListAsync(string prefix, int limit = 100, int offset = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StorageItemInfo>>(Array.Empty<StorageItemInfo>());
        public Task<IDictionary<string, string>?> GetMetadataAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<IDictionary<string, string>?>(null);
    }
}
