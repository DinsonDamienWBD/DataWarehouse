using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Storage;

#region Storage Config Base

/// <summary>
/// Base configuration class for all hybrid storage plugins.
/// Provides common settings for connection management, caching, and indexing.
/// </summary>
public class StorageConfigBase
{
    /// <summary>
    /// Optional instance identifier for multi-instance configurations.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Display name for this storage instance.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Maximum number of concurrent connections.
    /// </summary>
    public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Operation timeout in seconds.
    /// </summary>
    public int OperationTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Whether to enable caching for this storage instance.
    /// </summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>
    /// Default cache TTL for cached entries.
    /// </summary>
    public TimeSpan DefaultCacheTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum cache size in bytes (0 = unlimited).
    /// </summary>
    public long MaxCacheSizeBytes { get; set; } = 0;

    /// <summary>
    /// Whether to enable automatic indexing for this storage instance.
    /// </summary>
    public bool EnableIndexing { get; set; } = true;

    /// <summary>
    /// Whether to enable automatic health monitoring.
    /// </summary>
    public bool EnableHealthMonitoring { get; set; } = true;

    /// <summary>
    /// Health check interval in seconds.
    /// </summary>
    public int HealthCheckIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum retry attempts for failed operations.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Retry delay in milliseconds.
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Optional tags for categorization.
    /// </summary>
    public HashSet<string> Tags { get; set; } = new();

    /// <summary>
    /// Additional metadata for this configuration.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

#endregion

#region Hybrid Storage Plugin Base

/// <summary>
/// Abstract base class for hybrid storage plugins with multi-instance support.
/// Combines storage with caching, indexing, and connection registry capabilities.
/// Plugins extending this class gain:
/// - Multi-instance connection management via StorageConnectionRegistry
/// - Automatic caching with TTL support
/// - Document indexing and search
/// - Health monitoring
/// - Role-based instance selection (Primary, Cache, Index, Archive, etc.)
/// </summary>
/// <typeparam name="TConfig">Configuration type extending StorageConfigBase.</typeparam>
public abstract class HybridStoragePluginBase<TConfig> : IndexableStoragePluginBase
    where TConfig : StorageConfigBase, new()
{
    /// <summary>
    /// Primary configuration for this plugin.
    /// </summary>
    protected readonly TConfig _config;

    /// <summary>
    /// Connection registry for multi-instance management.
    /// </summary>
    protected readonly StorageConnectionRegistry<TConfig> _connectionRegistry;

    /// <summary>
    /// Instance health cache for quick lookups.
    /// </summary>
    protected readonly BoundedDictionary<string, InstanceHealthStatus> _healthCache = new BoundedDictionary<string, InstanceHealthStatus>(1000);

    /// <summary>
    /// Health monitoring timer.
    /// </summary>
    private Timer? _healthMonitorTimer;

    /// <summary>
    /// Whether the plugin has been disposed.
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// Storage category for this plugin (e.g., "Local", "Cloud", "Network").
    /// </summary>
    public abstract string StorageCategory { get; }

    /// <summary>
    /// Creates a new hybrid storage plugin with the specified configuration.
    /// </summary>
    protected HybridStoragePluginBase(TConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _connectionRegistry = new StorageConnectionRegistry<TConfig>(CreateConnectionAsync);

        // Start health monitoring if enabled
        if (_config.EnableHealthMonitoring && _config.HealthCheckIntervalSeconds > 0)
        {
            _healthMonitorTimer = new Timer(
                async _ => { try { await CheckAllHealthAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
                null,
                TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds),
                TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds));
        }
    }

    /// <summary>
    /// Creates a connection for the given configuration.
    /// Override to provide custom connection creation logic.
    /// </summary>
    protected abstract Task<object> CreateConnectionAsync(TConfig config);

    #region Multi-Instance Management

    /// <summary>
    /// Registers a new storage instance.
    /// </summary>
    /// <param name="instanceId">Unique identifier for this instance.</param>
    /// <param name="config">Instance configuration.</param>
    /// <param name="roles">Roles this instance serves (Primary, Cache, Index, etc.).</param>
    /// <param name="priority">Priority for role selection (higher = preferred).</param>
    public async Task<StorageConnectionInstance<TConfig>> RegisterInstanceAsync(
        string instanceId,
        TConfig config,
        StorageRole roles = StorageRole.Primary,
        int priority = 0)
    {
        return await _connectionRegistry.RegisterAsync(instanceId, config, roles, priority);
    }

    /// <summary>
    /// Registers a storage instance synchronously.
    /// </summary>
    public StorageConnectionInstance<TConfig> RegisterInstance(
        string instanceId,
        TConfig config,
        StorageRole roles = StorageRole.Primary,
        int priority = 0)
    {
        return _connectionRegistry.Register(instanceId, config, roles, priority);
    }

    /// <summary>
    /// Unregisters a storage instance.
    /// </summary>
    public async Task UnregisterInstanceAsync(string instanceId)
    {
        await _connectionRegistry.UnregisterAsync(instanceId);
        _healthCache.TryRemove(instanceId, out _);
    }

    /// <summary>
    /// Gets all registered instance IDs.
    /// </summary>
    public IEnumerable<string> GetInstanceIds() => _connectionRegistry.InstanceIds;

    /// <summary>
    /// Gets an instance by ID.
    /// </summary>
    public StorageConnectionInstance<TConfig>? GetInstance(string instanceId)
        => _connectionRegistry.Get(instanceId);

    /// <summary>
    /// Gets all instances for a specific role.
    /// </summary>
    public IEnumerable<StorageConnectionInstance<TConfig>> GetInstancesByRole(StorageRole role)
        => _connectionRegistry.GetByRole(role);

    /// <summary>
    /// Gets the primary instance for a role.
    /// </summary>
    public StorageConnectionInstance<TConfig>? GetPrimaryForRole(StorageRole role)
        => _connectionRegistry.GetPrimaryForRole(role);

    /// <summary>
    /// Gets a healthy instance for a role.
    /// </summary>
    public StorageConnectionInstance<TConfig>? GetHealthyForRole(StorageRole role)
        => _connectionRegistry.GetHealthyForRole(role);

    /// <summary>
    /// Updates the roles for an instance at runtime.
    /// </summary>
    public bool UpdateInstanceRoles(string instanceId, StorageRole roles)
    {
        var instance = _connectionRegistry.Get(instanceId);
        if (instance == null) return false;
        instance.Roles = roles;
        return true;
    }

    /// <summary>
    /// Updates the priority for an instance at runtime.
    /// </summary>
    public bool UpdateInstancePriority(string instanceId, int priority)
    {
        var instance = _connectionRegistry.Get(instanceId);
        if (instance == null) return false;
        instance.Priority = priority;
        return true;
    }

    #endregion

    #region Health Monitoring

    /// <summary>
    /// Checks health of all registered instances.
    /// </summary>
    public async Task<Dictionary<string, InstanceHealthStatus>> CheckAllHealthAsync(CancellationToken ct = default)
    {
        var results = await _connectionRegistry.CheckHealthAsync(ct);
        foreach (var kv in results)
        {
            _healthCache[kv.Key] = kv.Value;
        }
        return results;
    }

    /// <summary>
    /// Gets cached health status for an instance.
    /// </summary>
    public InstanceHealthStatus GetInstanceHealth(string instanceId)
        => _healthCache.TryGetValue(instanceId, out var status) ? status : InstanceHealthStatus.Unknown;

    /// <summary>
    /// Gets aggregate health status.
    /// </summary>
    public InstanceHealthStatus GetAggregateHealth()
    {
        // Cat 13 (finding 636): single pass over Values to avoid O(3n) multiple enumerations.
        int total = 0, healthy = 0;
        foreach (var h in _healthCache.Values)
        {
            total++;
            if (h == InstanceHealthStatus.Healthy) healthy++;
        }
        if (total == 0) return InstanceHealthStatus.Unknown;
        if (healthy == total) return InstanceHealthStatus.Healthy;
        if (healthy > 0) return InstanceHealthStatus.Degraded;
        return InstanceHealthStatus.Unhealthy;
    }

    #endregion

    #region Message Handling

    /// <summary>
    /// Handles plugin messages including multi-instance management.
    /// </summary>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        // Handle multi-instance management messages
        switch (message.Type)
        {
            case "storage.instance.register":
                await HandleRegisterInstanceAsync(message);
                return;
            case "storage.instance.unregister":
                await HandleUnregisterInstanceAsync(message);
                return;
            case "storage.instance.update-roles":
                HandleUpdateRoles(message);
                return;
            case "storage.instance.update-priority":
                HandleUpdatePriority(message);
                return;
            case "storage.instance.list":
                HandleListInstances(message);
                return;
            case "storage.instance.health":
                await HandleHealthCheckAsync(message);
                return;
            case "storage.health":
                await HandleAggregateHealthAsync(message);
                return;
        }

        // Delegate to derived class
        await base.OnMessageAsync(message);
    }

    private async Task HandleRegisterInstanceAsync(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload)
            return;

        var instanceId = payload.TryGetValue("instanceId", out var id) ? id?.ToString() : null;
        var configDict = payload.TryGetValue("config", out var cfg) ? cfg as Dictionary<string, object> : null;

        if (string.IsNullOrEmpty(instanceId) || configDict == null)
            return;

        // Parse roles
        var roles = StorageRole.Primary;
        if (payload.TryGetValue("roles", out var rolesObj))
        {
            if (rolesObj is string rolesStr && Enum.TryParse<StorageRole>(rolesStr, true, out var parsed))
                roles = parsed;
            else if (rolesObj is int rolesInt)
                roles = (StorageRole)rolesInt;
        }

        // Parse priority
        var priority = 0;
        if (payload.TryGetValue("priority", out var prioObj) && prioObj is int prio)
            priority = prio;

        // Create config from dictionary
        var config = CreateConfigFromDictionary(configDict);
        await RegisterInstanceAsync(instanceId, config, roles, priority);
    }

    private async Task HandleUnregisterInstanceAsync(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload ||
            !payload.TryGetValue("instanceId", out var id))
            return;

        var instanceId = id?.ToString();
        if (!string.IsNullOrEmpty(instanceId))
            await UnregisterInstanceAsync(instanceId);
    }

    private void HandleUpdateRoles(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload ||
            !payload.TryGetValue("instanceId", out var id) ||
            !payload.TryGetValue("roles", out var rolesObj))
            return;

        var instanceId = id?.ToString();
        if (string.IsNullOrEmpty(instanceId)) return;

        var roles = StorageRole.Primary;
        if (rolesObj is string rolesStr && Enum.TryParse<StorageRole>(rolesStr, true, out var parsed))
            roles = parsed;
        else if (rolesObj is int rolesInt)
            roles = (StorageRole)rolesInt;

        UpdateInstanceRoles(instanceId, roles);
    }

    private void HandleUpdatePriority(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload ||
            !payload.TryGetValue("instanceId", out var id) ||
            !payload.TryGetValue("priority", out var prioObj))
            return;

        var instanceId = id?.ToString();
        if (string.IsNullOrEmpty(instanceId)) return;

        if (prioObj is int prio)
            UpdateInstancePriority(instanceId, prio);
    }

    private void HandleListInstances(PluginMessage message)
    {
        var instances = _connectionRegistry.GetAll().Select(i => new Dictionary<string, object>
        {
            ["instanceId"] = i.InstanceId,
            ["roles"] = (int)i.Roles,
            ["priority"] = i.Priority,
            ["health"] = (int)i.Health,
            ["isConnected"] = i.IsConnected,
            ["lastActivity"] = i.LastActivity?.ToString("O") ?? string.Empty,
        }).ToList<object>();

        // Populate the message payload with the result so callers awaiting this key can retrieve it
        message.Payload["instances"] = instances;
        message.Payload["count"] = instances.Count;
    }

    private async Task HandleHealthCheckAsync(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload ||
            !payload.TryGetValue("instanceId", out var id))
        {
            await CheckAllHealthAsync();
            return;
        }

        var instanceId = id?.ToString();
        if (!string.IsNullOrEmpty(instanceId))
        {
            var instance = _connectionRegistry.Get(instanceId);
            if (instance != null)
                await instance.CheckHealthAsync();
        }
    }

    private async Task HandleAggregateHealthAsync(PluginMessage message)
    {
        var instanceHealthMap = await CheckAllHealthAsync();
        var health = GetAggregateHealth();

        // Populate the message payload with the result so callers awaiting this key can retrieve it
        message.Payload["aggregateHealth"] = (int)health;
        message.Payload["aggregateHealthName"] = health.ToString();
        message.Payload["instanceCount"] = instanceHealthMap.Count;
    }

    /// <summary>
    /// Creates a configuration object from a dictionary.
    /// Override for custom config parsing.
    /// </summary>
    protected virtual TConfig CreateConfigFromDictionary(Dictionary<string, object> dict)
    {
        var config = new TConfig();

        if (dict.TryGetValue("instanceId", out var instanceId))
            config.InstanceId = instanceId?.ToString();
        if (dict.TryGetValue("displayName", out var displayName))
            config.DisplayName = displayName?.ToString();
        if (dict.TryGetValue("maxConnections", out var maxConn) && maxConn is int mc)
            config.MaxConnections = mc;
        if (dict.TryGetValue("connectionTimeoutSeconds", out var connTimeout) && connTimeout is int ct)
            config.ConnectionTimeoutSeconds = ct;
        if (dict.TryGetValue("operationTimeoutSeconds", out var opTimeout) && opTimeout is int ot)
            config.OperationTimeoutSeconds = ot;
        if (dict.TryGetValue("enableCaching", out var enableCache) && enableCache is bool ec)
            config.EnableCaching = ec;
        if (dict.TryGetValue("enableIndexing", out var enableIndex) && enableIndex is bool ei)
            config.EnableIndexing = ei;
        if (dict.TryGetValue("enableHealthMonitoring", out var enableHealth) && enableHealth is bool eh)
            config.EnableHealthMonitoring = eh;
        if (dict.TryGetValue("maxRetries", out var maxRetries) && maxRetries is int mr)
            config.MaxRetries = mr;

        return config;
    }

    #endregion

    #region Metadata

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["StorageCategory"] = StorageCategory;
        metadata["SupportsMultiInstance"] = true;
        metadata["RegisteredInstances"] = _connectionRegistry.Count;
        metadata["AggregateHealth"] = GetAggregateHealth().ToString();
        metadata["EnableCaching"] = _config.EnableCaching;
        metadata["EnableIndexing"] = _config.EnableIndexing;
        metadata["EnableHealthMonitoring"] = _config.EnableHealthMonitoring;
        metadata["RuntimeConfigurable"] = true;
        return metadata;
    }

    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        var capabilities = base.GetCapabilities();

        // Add multi-instance management capabilities
        capabilities.AddRange(new[]
        {
            new PluginCapabilityDescriptor
            {
                Name = "storage.instance.register",
                DisplayName = "Register Instance",
                Description = "Register a new storage instance with roles and priority"
            },
            new PluginCapabilityDescriptor
            {
                Name = "storage.instance.unregister",
                DisplayName = "Unregister Instance",
                Description = "Unregister and dispose a storage instance"
            },
            new PluginCapabilityDescriptor
            {
                Name = "storage.instance.update-roles",
                DisplayName = "Update Roles",
                Description = "Update roles for an existing instance"
            },
            new PluginCapabilityDescriptor
            {
                Name = "storage.instance.update-priority",
                DisplayName = "Update Priority",
                Description = "Update priority for an existing instance"
            },
            new PluginCapabilityDescriptor
            {
                Name = "storage.instance.list",
                DisplayName = "List Instances",
                Description = "List all registered storage instances"
            },
            new PluginCapabilityDescriptor
            {
                Name = "storage.instance.health",
                DisplayName = "Instance Health",
                Description = "Check health of storage instances"
            },
            new PluginCapabilityDescriptor
            {
                Name = "storage.health",
                DisplayName = "Aggregate Health",
                Description = "Get aggregate health status of all instances"
            }
        });

        return capabilities;
    }

    #endregion

    #region Disposal

    /// <summary>
    /// Performs async cleanup of storage connections and health monitor.
    /// </summary>
    protected override async ValueTask DisposeAsyncCore()
    {
        if (_disposed) return;
        _disposed = true;

        _healthMonitorTimer?.Dispose();
        await _connectionRegistry.DisposeAsync();

        await base.DisposeAsyncCore();
    }

    #endregion
}

#endregion

#region Storage Plugin Configuration Builder

/// <summary>
/// Fluent builder for storage plugin configurations.
/// </summary>
public class StorageConfigBuilder<TConfig> where TConfig : StorageConfigBase, new()
{
    private readonly TConfig _config = new();

    public StorageConfigBuilder<TConfig> WithInstanceId(string id)
    {
        _config.InstanceId = id;
        return this;
    }

    public StorageConfigBuilder<TConfig> WithDisplayName(string name)
    {
        _config.DisplayName = name;
        return this;
    }

    public StorageConfigBuilder<TConfig> WithMaxConnections(int max)
    {
        _config.MaxConnections = max;
        return this;
    }

    public StorageConfigBuilder<TConfig> WithConnectionTimeout(int seconds)
    {
        _config.ConnectionTimeoutSeconds = seconds;
        return this;
    }

    public StorageConfigBuilder<TConfig> WithOperationTimeout(int seconds)
    {
        _config.OperationTimeoutSeconds = seconds;
        return this;
    }

    public StorageConfigBuilder<TConfig> EnableCaching(bool enable = true, TimeSpan? defaultTtl = null)
    {
        _config.EnableCaching = enable;
        if (defaultTtl.HasValue)
            _config.DefaultCacheTtl = defaultTtl.Value;
        return this;
    }

    public StorageConfigBuilder<TConfig> EnableIndexing(bool enable = true)
    {
        _config.EnableIndexing = enable;
        return this;
    }

    public StorageConfigBuilder<TConfig> EnableHealthMonitoring(bool enable = true, int intervalSeconds = 60)
    {
        _config.EnableHealthMonitoring = enable;
        _config.HealthCheckIntervalSeconds = intervalSeconds;
        return this;
    }

    public StorageConfigBuilder<TConfig> WithRetryPolicy(int maxRetries, int delayMs)
    {
        _config.MaxRetries = maxRetries;
        _config.RetryDelayMs = delayMs;
        return this;
    }

    public StorageConfigBuilder<TConfig> WithTags(params string[] tags)
    {
        foreach (var tag in tags)
            _config.Tags.Add(tag);
        return this;
    }

    public StorageConfigBuilder<TConfig> WithMetadata(string key, object value)
    {
        _config.Metadata[key] = value;
        return this;
    }

    /// <summary>
    /// Allows further configuration via an action.
    /// </summary>
    public StorageConfigBuilder<TConfig> Configure(Action<TConfig> configure)
    {
        configure(_config);
        return this;
    }

    public TConfig Build() => _config;
}

/// <summary>
/// Extension methods for storage configurations.
/// </summary>
public static class StorageConfigExtensions
{
    /// <summary>
    /// Creates a builder for storage configurations.
    /// </summary>
    public static StorageConfigBuilder<TConfig> CreateBuilder<TConfig>() where TConfig : StorageConfigBase, new()
        => new();
}

#endregion
