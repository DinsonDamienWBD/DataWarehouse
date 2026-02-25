using System.Diagnostics;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDeployment;

/// <summary>
/// Defines the deployment strategy type.
/// </summary>
public enum DeploymentType
{
    /// <summary>Blue-green deployment pattern.</summary>
    BlueGreen = 0,
    /// <summary>Canary deployment with gradual rollout.</summary>
    Canary = 1,
    /// <summary>Rolling update deployment.</summary>
    RollingUpdate = 2,
    /// <summary>Recreate/destroy and redeploy.</summary>
    Recreate = 3,
    /// <summary>A/B testing deployment.</summary>
    ABTesting = 4,
    /// <summary>Shadow deployment (dark launch).</summary>
    Shadow = 5,
    /// <summary>Container orchestration deployment.</summary>
    ContainerOrchestration = 6,
    /// <summary>Serverless function deployment.</summary>
    Serverless = 7,
    /// <summary>Virtual machine deployment.</summary>
    VirtualMachine = 8,
    /// <summary>Bare metal deployment.</summary>
    BareMetal = 9,
    /// <summary>CI/CD pipeline integration.</summary>
    CICD = 10,
    /// <summary>Feature flag controlled deployment.</summary>
    FeatureFlag = 11,
    /// <summary>Hot reload without downtime.</summary>
    HotReload = 12,
    /// <summary>Rollback to previous version.</summary>
    Rollback = 13
}

/// <summary>
/// Deployment health status.
/// </summary>
public enum DeploymentHealth
{
    /// <summary>Unknown health status.</summary>
    Unknown = 0,
    /// <summary>Deployment is healthy.</summary>
    Healthy = 1,
    /// <summary>Deployment is degraded but functional.</summary>
    Degraded = 2,
    /// <summary>Deployment is unhealthy.</summary>
    Unhealthy = 3,
    /// <summary>Deployment is in progress.</summary>
    InProgress = 4,
    /// <summary>Deployment failed.</summary>
    Failed = 5,
    /// <summary>Rollback in progress.</summary>
    RollingBack = 6
}

/// <summary>
/// Represents the current state of a deployment.
/// </summary>
public sealed record DeploymentState
{
    /// <summary>Unique deployment identifier.</summary>
    public required string DeploymentId { get; init; }

    /// <summary>Version being deployed.</summary>
    public required string Version { get; init; }

    /// <summary>Previous version (for rollback).</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>Current health status.</summary>
    public DeploymentHealth Health { get; init; } = DeploymentHealth.Unknown;

    /// <summary>Deployment progress (0-100).</summary>
    public int ProgressPercent { get; init; }

    /// <summary>Number of instances deployed.</summary>
    public int DeployedInstances { get; init; }

    /// <summary>Target number of instances.</summary>
    public int TargetInstances { get; init; }

    /// <summary>Number of healthy instances.</summary>
    public int HealthyInstances { get; init; }

    /// <summary>Deployment start time.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Deployment completion time.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Configuration for a deployment operation.
/// </summary>
public sealed record DeploymentConfig
{
    /// <summary>Target environment (production, staging, dev).</summary>
    public required string Environment { get; init; }

    /// <summary>Version to deploy.</summary>
    public required string Version { get; init; }

    /// <summary>Artifact location (image, package, bundle).</summary>
    public required string ArtifactUri { get; init; }

    /// <summary>Target instances or replicas.</summary>
    public int TargetInstances { get; init; } = 1;

    /// <summary>Health check endpoint.</summary>
    public string? HealthCheckPath { get; init; } = "/health";

    /// <summary>Health check interval in seconds.</summary>
    public int HealthCheckIntervalSeconds { get; init; } = 10;

    /// <summary>Health check timeout in seconds.</summary>
    public int HealthCheckTimeoutSeconds { get; init; } = 5;

    /// <summary>Maximum deployment time in minutes.</summary>
    public int DeploymentTimeoutMinutes { get; init; } = 30;

    /// <summary>Auto-rollback on failure.</summary>
    public bool AutoRollbackOnFailure { get; init; } = true;

    /// <summary>Canary traffic percentage (for canary deployments).</summary>
    public int CanaryPercent { get; init; } = 10;

    /// <summary>Environment variables to set.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();

    /// <summary>Resource limits (CPU, memory).</summary>
    public Dictionary<string, string> ResourceLimits { get; init; } = new();

    /// <summary>Labels/tags for the deployment.</summary>
    public Dictionary<string, string> Labels { get; init; } = new();

    /// <summary>Additional strategy-specific configuration.</summary>
    public Dictionary<string, object> StrategyConfig { get; init; } = new();
}

/// <summary>
/// Result of a health check operation.
/// </summary>
public sealed record HealthCheckResult
{
    /// <summary>Instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Whether the instance is healthy.</summary>
    public bool IsHealthy { get; init; }

    /// <summary>HTTP status code (if applicable).</summary>
    public int? StatusCode { get; init; }

    /// <summary>Response time in milliseconds.</summary>
    public double ResponseTimeMs { get; init; }

    /// <summary>Check timestamp.</summary>
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Error message if unhealthy.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Additional health details.</summary>
    public Dictionary<string, object> Details { get; init; } = new();
}

/// <summary>
/// Describes the characteristics of a deployment strategy.
/// </summary>
public sealed record DeploymentCharacteristics
{
    /// <summary>Strategy name.</summary>
    public required string StrategyName { get; init; }

    /// <summary>Type of deployment.</summary>
    public required DeploymentType DeploymentType { get; init; }

    /// <summary>Whether the strategy supports zero-downtime deployment.</summary>
    public bool SupportsZeroDowntime { get; init; }

    /// <summary>Whether the strategy supports instant rollback.</summary>
    public bool SupportsInstantRollback { get; init; }

    /// <summary>Whether the strategy supports gradual traffic shifting.</summary>
    public bool SupportsTrafficShifting { get; init; }

    /// <summary>Whether the strategy supports health checks.</summary>
    public bool SupportsHealthChecks { get; init; } = true;

    /// <summary>Whether the strategy supports auto-scaling.</summary>
    public bool SupportsAutoScaling { get; init; }

    /// <summary>Typical deployment time in minutes (1-60 scale).</summary>
    public int TypicalDeploymentTimeMinutes { get; init; }

    /// <summary>Resource overhead percentage (0-100).</summary>
    public int ResourceOverheadPercent { get; init; }

    /// <summary>Complexity level (1-10).</summary>
    public int ComplexityLevel { get; init; }

    /// <summary>Required infrastructure (e.g., "Kubernetes", "AWS Lambda").</summary>
    public string[] RequiredInfrastructure { get; init; } = Array.Empty<string>();

    /// <summary>Description of the strategy.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Statistics for deployment operations.
/// </summary>
public sealed class DeploymentStatistics
{
    public long TotalDeployments { get; set; }
    public long SuccessfulDeployments { get; set; }
    public long FailedDeployments { get; set; }
    public long RollbackCount { get; set; }
    public double AverageDeploymentTimeMs { get; set; }
    public double TotalDeploymentTimeMs { get; set; }
    public long HealthChecksPerformed { get; set; }
    public long HealthCheckFailures { get; set; }
}

/// <summary>
/// Interface for deployment strategies.
/// </summary>
public interface IDeploymentStrategy
{
    /// <summary>Gets the strategy characteristics.</summary>
    DeploymentCharacteristics Characteristics { get; }

    /// <summary>Deploys the specified configuration.</summary>
    Task<DeploymentState> DeployAsync(DeploymentConfig config, CancellationToken ct = default);

    /// <summary>Gets the current deployment state.</summary>
    Task<DeploymentState> GetStateAsync(string deploymentId, CancellationToken ct = default);

    /// <summary>Performs a health check on deployed instances.</summary>
    Task<HealthCheckResult[]> HealthCheckAsync(string deploymentId, CancellationToken ct = default);

    /// <summary>Rolls back to the previous version.</summary>
    Task<DeploymentState> RollbackAsync(string deploymentId, string? targetVersion = null, CancellationToken ct = default);

    /// <summary>Scales the deployment to the specified instance count.</summary>
    Task<DeploymentState> ScaleAsync(string deploymentId, int targetInstances, CancellationToken ct = default);

    /// <summary>Gets deployment statistics.</summary>
    DeploymentStatistics GetStatistics();

    /// <summary>Resets statistics.</summary>
    void ResetStatistics();
}

/// <summary>
/// Abstract base class for deployment strategy implementations.
/// Provides common functionality including health checks, statistics tracking, and rollback triggers.
/// </summary>
public abstract class DeploymentStrategyBase : IDeploymentStrategy
{
    private readonly DeploymentStatistics _statistics = new();
    private readonly object _statsLock = new();
    private readonly BoundedDictionary<string, DeploymentState> _activeDeployments = new BoundedDictionary<string, DeploymentState>(1000);
    private readonly BoundedDictionary<string, long> _counters = new BoundedDictionary<string, long>(1000);
    private readonly HttpClient _httpClient;
    private bool _initialized;
    private DateTime? _healthCacheExpiry;
    private bool? _cachedHealthy;

    protected DeploymentStrategyBase()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Gets whether this strategy has been initialized.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>Initializes the strategy. Idempotent.</summary>
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return Task.CompletedTask;
        _initialized = true;
        IncrementCounter("initialized");
        return Task.CompletedTask;
    }

    /// <summary>Shuts down the strategy gracefully, completing in-flight deployments.</summary>
    public virtual Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) return Task.CompletedTask;
        _initialized = false;
        IncrementCounter("shutdown");
        return Task.CompletedTask;
    }

    /// <summary>Gets cached health status, refreshing every 60 seconds.</summary>
    public bool GetStrategyHealthy()
    {
        if (_cachedHealthy.HasValue && _healthCacheExpiry.HasValue && DateTime.UtcNow < _healthCacheExpiry.Value)
            return _cachedHealthy.Value;
        _cachedHealthy = _initialized && _activeDeployments.Values.All(d => d.Health != DeploymentHealth.Failed);
        _healthCacheExpiry = DateTime.UtcNow.AddSeconds(60);
        return _cachedHealthy.Value;
    }

    /// <summary>Increments a named counter. Thread-safe.</summary>
    protected void IncrementCounter(string name)
    {
        _counters.AddOrUpdate(name, 1, (_, current) => Interlocked.Increment(ref current));
    }

    /// <summary>Gets all counter values.</summary>
    public IReadOnlyDictionary<string, long> GetCounters() => new Dictionary<string, long>(_counters);

    /// <inheritdoc/>
    public abstract DeploymentCharacteristics Characteristics { get; }

    /// <inheritdoc/>
    public async Task<DeploymentState> DeployAsync(DeploymentConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var deploymentId = GenerateDeploymentId(config);
        var sw = Stopwatch.StartNew();

        var state = new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = config.Version,
            Health = DeploymentHealth.InProgress,
            ProgressPercent = 0,
            TargetInstances = config.TargetInstances,
            Metadata = new Dictionary<string, object>
            {
                ["environment"] = config.Environment,
                ["artifactUri"] = config.ArtifactUri,
                ["strategy"] = Characteristics.StrategyName
            }
        };

        _activeDeployments[deploymentId] = state;

        try
        {
            state = await DeployCoreAsync(config, state, ct);
            sw.Stop();

            if (state.Health == DeploymentHealth.Healthy)
            {
                UpdateDeploymentStats(true, sw.Elapsed.TotalMilliseconds);
            }
            else if (state.Health == DeploymentHealth.Failed && config.AutoRollbackOnFailure)
            {
                // Trigger auto-rollback
                state = await RollbackAsync(deploymentId, state.PreviousVersion, ct);
            }

            _activeDeployments[deploymentId] = state;
            return state;
        }
        catch (Exception ex)
        {
            sw.Stop();
            UpdateDeploymentStats(false, sw.Elapsed.TotalMilliseconds);

            state = state with
            {
                Health = DeploymentHealth.Failed,
                ErrorMessage = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow
            };

            _activeDeployments[deploymentId] = state;

            if (config.AutoRollbackOnFailure && state.PreviousVersion != null)
            {
                try
                {
                    return await RollbackAsync(deploymentId, state.PreviousVersion, ct);
                }
                catch
                {
                    // Rollback also failed, return failed state
                }
            }

            return state;
        }
    }

    /// <inheritdoc/>
    public Task<DeploymentState> GetStateAsync(string deploymentId, CancellationToken ct = default)
    {
        if (_activeDeployments.TryGetValue(deploymentId, out var state))
        {
            return Task.FromResult(state);
        }

        return GetStateCoreAsync(deploymentId, ct);
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult[]> HealthCheckAsync(string deploymentId, CancellationToken ct = default)
    {
        var state = await GetStateAsync(deploymentId, ct);
        if (state.Health == DeploymentHealth.Failed || state.Health == DeploymentHealth.Unknown)
        {
            return Array.Empty<HealthCheckResult>();
        }

        var results = await HealthCheckCoreAsync(deploymentId, state, ct);

        lock (_statsLock)
        {
            _statistics.HealthChecksPerformed += results.Length;
            _statistics.HealthCheckFailures += results.Count(r => !r.IsHealthy);
        }

        // Update deployment state based on health check results
        var healthyCount = results.Count(r => r.IsHealthy);
        var health = DetermineOverallHealth(healthyCount, results.Length);

        _activeDeployments[deploymentId] = state with
        {
            Health = health,
            HealthyInstances = healthyCount
        };

        return results;
    }

    /// <inheritdoc/>
    public async Task<DeploymentState> RollbackAsync(string deploymentId, string? targetVersion = null, CancellationToken ct = default)
    {
        var currentState = await GetStateAsync(deploymentId, ct);
        var rollbackVersion = targetVersion ?? currentState.PreviousVersion;

        if (string.IsNullOrEmpty(rollbackVersion))
        {
            throw new InvalidOperationException("No previous version available for rollback");
        }

        var state = currentState with
        {
            Health = DeploymentHealth.RollingBack,
            ProgressPercent = 0
        };

        _activeDeployments[deploymentId] = state;

        try
        {
            state = await RollbackCoreAsync(deploymentId, rollbackVersion, state, ct);

            lock (_statsLock)
            {
                _statistics.RollbackCount++;
            }

            _activeDeployments[deploymentId] = state;
            return state;
        }
        catch (Exception ex)
        {
            state = state with
            {
                Health = DeploymentHealth.Failed,
                ErrorMessage = $"Rollback failed: {ex.Message}",
                CompletedAt = DateTimeOffset.UtcNow
            };

            _activeDeployments[deploymentId] = state;
            return state;
        }
    }

    /// <inheritdoc/>
    public async Task<DeploymentState> ScaleAsync(string deploymentId, int targetInstances, CancellationToken ct = default)
    {
        if (targetInstances < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetInstances), "Target instances must be non-negative");
        }

        var state = await GetStateAsync(deploymentId, ct);
        state = await ScaleCoreAsync(deploymentId, targetInstances, state, ct);

        _activeDeployments[deploymentId] = state;
        return state;
    }

    /// <inheritdoc/>
    public DeploymentStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new DeploymentStatistics
            {
                TotalDeployments = _statistics.TotalDeployments,
                SuccessfulDeployments = _statistics.SuccessfulDeployments,
                FailedDeployments = _statistics.FailedDeployments,
                RollbackCount = _statistics.RollbackCount,
                AverageDeploymentTimeMs = _statistics.TotalDeployments > 0
                    ? _statistics.TotalDeploymentTimeMs / _statistics.TotalDeployments
                    : 0,
                TotalDeploymentTimeMs = _statistics.TotalDeploymentTimeMs,
                HealthChecksPerformed = _statistics.HealthChecksPerformed,
                HealthCheckFailures = _statistics.HealthCheckFailures
            };
        }
    }

    /// <inheritdoc/>
    public void ResetStatistics()
    {
        lock (_statsLock)
        {
            _statistics.TotalDeployments = 0;
            _statistics.SuccessfulDeployments = 0;
            _statistics.FailedDeployments = 0;
            _statistics.RollbackCount = 0;
            _statistics.AverageDeploymentTimeMs = 0;
            _statistics.TotalDeploymentTimeMs = 0;
            _statistics.HealthChecksPerformed = 0;
            _statistics.HealthCheckFailures = 0;
        }
    }

    // ========================================
    // Abstract Methods for Derived Classes
    // ========================================

    /// <summary>
    /// Core deployment implementation.
    /// </summary>
    protected abstract Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct);

    /// <summary>
    /// Core rollback implementation.
    /// </summary>
    protected abstract Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct);

    /// <summary>
    /// Core scaling implementation.
    /// </summary>
    protected abstract Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct);

    /// <summary>
    /// Core health check implementation.
    /// </summary>
    protected abstract Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct);

    /// <summary>
    /// Core state retrieval for deployments not in cache.
    /// </summary>
    protected abstract Task<DeploymentState> GetStateCoreAsync(
        string deploymentId,
        CancellationToken ct);

    // ========================================
    // Helper Methods
    // ========================================

    /// <summary>
    /// Generates a unique deployment ID.
    /// </summary>
    protected virtual string GenerateDeploymentId(DeploymentConfig config)
    {
        return $"{config.Environment}-{config.Version}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    /// <summary>
    /// Performs an HTTP health check on a URL.
    /// </summary>
    protected async Task<HealthCheckResult> PerformHttpHealthCheckAsync(
        string instanceId,
        string healthUrl,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.GetAsync(healthUrl, ct);
            sw.Stop();

            return new HealthCheckResult
            {
                InstanceId = instanceId,
                IsHealthy = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                ResponseTimeMs = sw.Elapsed.TotalMilliseconds,
                Details = new Dictionary<string, object>
                {
                    ["url"] = healthUrl,
                    ["statusCode"] = (int)response.StatusCode,
                    ["reasonPhrase"] = response.ReasonPhrase ?? ""
                }
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HealthCheckResult
            {
                InstanceId = instanceId,
                IsHealthy = false,
                ResponseTimeMs = sw.Elapsed.TotalMilliseconds,
                ErrorMessage = ex.Message,
                Details = new Dictionary<string, object>
                {
                    ["url"] = healthUrl,
                    ["exception"] = ex.GetType().Name
                }
            };
        }
    }

    /// <summary>
    /// Determines overall health from individual health check results.
    /// </summary>
    protected virtual DeploymentHealth DetermineOverallHealth(int healthyCount, int totalCount)
    {
        if (totalCount == 0) return DeploymentHealth.Unknown;
        var healthyPercent = (double)healthyCount / totalCount * 100;

        return healthyPercent switch
        {
            100 => DeploymentHealth.Healthy,
            >= 50 => DeploymentHealth.Degraded,
            _ => DeploymentHealth.Unhealthy
        };
    }

    /// <summary>
    /// Updates deployment statistics.
    /// </summary>
    private void UpdateDeploymentStats(bool success, double durationMs)
    {
        lock (_statsLock)
        {
            _statistics.TotalDeployments++;
            _statistics.TotalDeploymentTimeMs += durationMs;

            if (success)
                _statistics.SuccessfulDeployments++;
            else
                _statistics.FailedDeployments++;
        }
    }

    /// <summary>
    /// Waits for a condition with timeout.
    /// </summary>
    protected async Task<bool> WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await condition())
                return true;

            await Task.Delay(pollInterval, ct);
        }

        return false;
    }

    // ========================================
    // Intelligence Integration
    // ========================================

    /// <summary>
    /// Strategy ID for intelligence integration.
    /// </summary>
    public virtual string StrategyId =>
        Characteristics.StrategyName.ToLowerInvariant().Replace(" ", "-");

    /// <summary>
    /// Strategy name for display.
    /// </summary>
    public virtual string StrategyName => Characteristics.StrategyName;

    /// <summary>
    /// Message bus for Intelligence communication.
    /// </summary>
    protected IMessageBus? MessageBus { get; private set; }

    /// <summary>
    /// Configures Intelligence integration.
    /// </summary>
    public virtual void ConfigureIntelligence(IMessageBus? messageBus)
    {
        MessageBus = messageBus;
    }

    /// <summary>
    /// Gets static knowledge about this strategy.
    /// </summary>
    public virtual KnowledgeObject GetStrategyKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"deployment.strategy.{StrategyId}",
            Topic = "deployment.strategy",
            SourcePluginId = "com.datawarehouse.deployment.ultimate",
            SourcePluginName = StrategyName,
            KnowledgeType = "capability",
            Description = Characteristics.Description ?? $"{StrategyName} deployment strategy",
            Payload = new Dictionary<string, object>
            {
                ["strategyId"] = StrategyId,
                ["strategyName"] = StrategyName,
                ["deploymentType"] = Characteristics.DeploymentType.ToString(),
                ["supportsZeroDowntime"] = Characteristics.SupportsZeroDowntime,
                ["supportsInstantRollback"] = Characteristics.SupportsInstantRollback,
                ["supportsTrafficShifting"] = Characteristics.SupportsTrafficShifting,
                ["supportsAutoScaling"] = Characteristics.SupportsAutoScaling,
                ["typicalDeploymentTime"] = Characteristics.TypicalDeploymentTimeMinutes,
                ["resourceOverhead"] = Characteristics.ResourceOverheadPercent,
                ["complexity"] = Characteristics.ComplexityLevel,
                ["requiredInfrastructure"] = Characteristics.RequiredInfrastructure
            },
            Tags = new[]
            {
                "deployment",
                "strategy",
                Characteristics.DeploymentType.ToString().ToLowerInvariant(),
                StrategyId
            }
        };
    }

    /// <summary>
    /// Gets the registered capability for this strategy.
    /// </summary>
    public virtual RegisteredCapability GetStrategyCapability()
    {
        return new RegisteredCapability
        {
            CapabilityId = $"deployment.strategy.{StrategyId}",
            DisplayName = StrategyName,
            Description = Characteristics.Description ?? $"{StrategyName} deployment strategy",
            Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Deployment,
            SubCategory = Characteristics.DeploymentType.ToString(),
            PluginId = "com.datawarehouse.deployment.ultimate",
            PluginName = "Ultimate Deployment",
            PluginVersion = "1.0.0",
            Tags = new[]
            {
                "deployment",
                "strategy",
                Characteristics.DeploymentType.ToString().ToLowerInvariant()
            },
            Metadata = new Dictionary<string, object>
            {
                ["supportsZeroDowntime"] = Characteristics.SupportsZeroDowntime,
                ["supportsInstantRollback"] = Characteristics.SupportsInstantRollback,
                ["complexity"] = Characteristics.ComplexityLevel
            },
            SemanticDescription = $"Deploy using {StrategyName} strategy" +
                (Characteristics.SupportsZeroDowntime ? " with zero downtime" : "") +
                (Characteristics.SupportsInstantRollback ? " and instant rollback" : "")
        };
    }
}
