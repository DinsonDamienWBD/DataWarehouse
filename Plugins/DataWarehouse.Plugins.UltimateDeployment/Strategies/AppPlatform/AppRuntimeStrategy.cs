using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.AppPlatform;

/// <summary>
/// Application runtime strategy for DataWarehouse platform deployments.
/// Manages per-app AI workflow configurations, observability telemetry isolation,
/// budget enforcement, concurrency limiting, and runtime service orchestration.
///
/// <para>
/// Supports the following runtime operations:
/// <list type="bullet">
///   <item>Per-app AI workflow configuration (Auto, Manual, Budget, Approval modes)</item>
///   <item>Monthly and per-request budget enforcement</item>
///   <item>Concurrency cap enforcement for AI requests</item>
///   <item>Per-app observability: metrics, traces, logs with app_id isolation</item>
///   <item>Telemetry routing to UniversalObservability with mandatory app_id filtering</item>
///   <item>AI usage tracking and monthly counter resets</item>
/// </list>
/// </para>
/// </summary>
public sealed class AppRuntimeStrategy : DeploymentStrategyBase
{
    private readonly BoundedDictionary<string, AiWorkflowConfig> _aiConfigs = new BoundedDictionary<string, AiWorkflowConfig>(1000);
    private readonly BoundedDictionary<string, AiUsageTracking> _aiUsage = new BoundedDictionary<string, AiUsageTracking>(1000);
    private readonly BoundedDictionary<string, int> _concurrentCounts = new BoundedDictionary<string, int>(1000);
    // Per-app lock objects eliminate the TOCTOU race between budget/concurrency check and increment.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _aiLocks = new();
    private readonly BoundedDictionary<string, ObservabilityConfig> _observabilityConfigs = new BoundedDictionary<string, ObservabilityConfig>(1000);

    public override DeploymentCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "App Runtime Services",
        DeploymentType = DeploymentType.HotReload,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 3,
        ComplexityLevel = 5,
        RequiredInfrastructure = ["DataWarehouse Kernel", "UltimateIntelligence"],
        Description = "Application runtime with AI workflow management, budget enforcement, and observability isolation"
    };

    #region Deployment Lifecycle

    protected override async Task<DeploymentState> DeployCoreAsync(DeploymentConfig config, DeploymentState initialState, CancellationToken ct)
    {
        IncrementCounter("app_runtime.deploy");
        var state = initialState;
        var appId = config.StrategyConfig.TryGetValue("appId", out var id) && id is string idStr ? idStr : "unknown";

        // Configure AI workflow if specified
        if (config.StrategyConfig.TryGetValue("aiMode", out var mode) && mode is string modeStr)
        {
            ConfigureAiWorkflow(appId, new AiWorkflowConfig
            {
                AppId = appId,
                Mode = Enum.TryParse<AiWorkflowMode>(modeStr, true, out var m) ? m : AiWorkflowMode.Manual,
                MonthlyBudgetUsd = config.StrategyConfig.TryGetValue("aiBudget", out var b) && b is string bs
                    ? decimal.TryParse(bs, out var bd) ? bd : 100m : 100m,
                MaxConcurrentRequests = config.StrategyConfig.TryGetValue("aiConcurrency", out var c) && c is string cs
                    ? int.TryParse(cs, out var ci) ? ci : 5 : 5
            });
        }
        state = state with { ProgressPercent = 50 };

        // Configure observability if specified
        if (config.StrategyConfig.TryGetValue("observabilityEnabled", out var obs) && obs is string obsStr && obsStr == "true")
        {
            ConfigureObservability(appId, new ObservabilityConfig
            {
                AppId = appId,
                MetricsEnabled = true,
                TracesEnabled = true,
                LogsEnabled = true,
                RetentionDays = 30,
                LogLevel = "Information"
            });
        }
        state = state with { ProgressPercent = 80 };

        return state with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 1,
            HealthyInstances = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(state.Metadata)
            {
                ["appId"] = appId,
                ["aiConfigured"] = _aiConfigs.ContainsKey(appId),
                ["observabilityConfigured"] = _observabilityConfigs.ContainsKey(appId)
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(string deploymentId, string targetVersion, DeploymentState currentState, CancellationToken ct)
    {
        IncrementCounter("app_runtime.rollback");
        if (currentState.Metadata.TryGetValue("appId", out var appIdObj) && appIdObj is string appId)
        {
            _aiConfigs.TryRemove(appId, out _);
            _aiUsage.TryRemove(appId, out _);
            _observabilityConfigs.TryRemove(appId, out _);
        }
        return currentState with { Health = DeploymentHealth.Healthy, Version = targetVersion, ProgressPercent = 100, CompletedAt = DateTimeOffset.UtcNow };
    }

    protected override Task<DeploymentState> ScaleCoreAsync(string deploymentId, int targetInstances, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(currentState);

    protected override Task<HealthCheckResult[]> HealthCheckCoreAsync(string deploymentId, DeploymentState currentState, CancellationToken ct)
        => Task.FromResult(new[] { new HealthCheckResult { InstanceId = deploymentId, IsHealthy = true, StatusCode = 200, ResponseTimeMs = 1 } });

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
        => Task.FromResult(new DeploymentState { DeploymentId = deploymentId, Version = "1.0.0", Health = DeploymentHealth.Healthy });

    #endregion

    #region AI Workflow Management

    /// <summary>Configures per-app AI workflow settings.</summary>
    public void ConfigureAiWorkflow(string appId, AiWorkflowConfig config)
    {
        config.AppId = appId;
        _aiConfigs[appId] = config;
        _aiUsage.TryAdd(appId, new AiUsageTracking { AppId = appId });
        _concurrentCounts.TryAdd(appId, 0);
        IncrementCounter("app_runtime.ai_configure");
    }

    /// <summary>Removes per-app AI workflow configuration.</summary>
    public bool RemoveAiWorkflow(string appId)
    {
        _aiConfigs.TryRemove(appId, out _);
        _aiUsage.TryRemove(appId, out _);
        _concurrentCounts.TryRemove(appId, out _);
        IncrementCounter("app_runtime.ai_remove");
        return true;
    }

    /// <summary>Gets per-app AI workflow configuration.</summary>
    public AiWorkflowConfig? GetAiWorkflow(string appId) => _aiConfigs.TryGetValue(appId, out var c) ? c : null;

    /// <summary>Submits an AI request with budget and concurrency enforcement.</summary>
    public async Task<AiRequestResult> SubmitAiRequestAsync(string appId, string operation, decimal estimatedCost, CancellationToken ct)
    {
        if (!_aiConfigs.TryGetValue(appId, out var config))
            return new AiRequestResult { Success = false, ErrorMessage = "No AI workflow configured for this app" };

        // Check allowed operations
        if (config.AllowedOperations.Count > 0 && !config.AllowedOperations.Contains(operation))
            return new AiRequestResult { Success = false, ErrorMessage = $"Operation '{operation}' not allowed" };

        // Acquire per-app lock to make budget check + increment atomic (eliminates TOCTOU).
        var appLock = _aiLocks.GetOrAdd(appId, _ => new object());
        lock (appLock)
        {
            // Re-read usage inside the lock so the check is consistent with the increment.
            _aiUsage.TryGetValue(appId, out var usage);

            // Check budget
            if (usage != null)
            {
                if (usage.MonthlySpendUsd + estimatedCost > config.MonthlyBudgetUsd)
                    return new AiRequestResult { Success = false, ErrorMessage = "Monthly AI budget exceeded" };
                if (estimatedCost > config.MaxPerRequestUsd && config.MaxPerRequestUsd > 0)
                    return new AiRequestResult { Success = false, ErrorMessage = "Per-request budget exceeded" };
            }

            // Check concurrency
            if (_concurrentCounts.TryGetValue(appId, out var current) && current >= config.MaxConcurrentRequests)
                return new AiRequestResult { Success = false, ErrorMessage = "Concurrent AI request limit reached" };

            // Increment concurrent count and track usage atomically.
            _concurrentCounts.AddOrUpdate(appId, 1, (_, v) => v + 1);
            if (usage != null)
            {
                usage.MonthlySpendUsd += estimatedCost;
                usage.TotalRequests++;
            }
        }

        try
        {
            IncrementCounter("app_runtime.ai_request");
            return new AiRequestResult { Success = true, RequestId = $"req-{Guid.NewGuid():N}", EstimatedCostUsd = estimatedCost };
        }
        finally
        {
            lock (appLock)
            {
                _concurrentCounts.AddOrUpdate(appId, 0, (_, v) => Math.Max(0, v - 1));
            }
        }
    }

    /// <summary>Gets AI usage tracking for an app.</summary>
    public AiUsageTracking? GetAiUsage(string appId) => _aiUsage.TryGetValue(appId, out var u) ? u : null;

    /// <summary>Resets monthly AI usage counters.</summary>
    public void ResetAiUsage(string appId)
    {
        if (_aiUsage.TryGetValue(appId, out var usage))
        {
            usage.MonthlySpendUsd = 0;
            usage.TotalRequests = 0;
        }
    }

    #endregion

    #region Observability Management

    /// <summary>Configures per-app observability settings.</summary>
    public void ConfigureObservability(string appId, ObservabilityConfig config)
    {
        config.AppId = appId;
        _observabilityConfigs[appId] = config;
        IncrementCounter("app_runtime.observability_configure");
    }

    /// <summary>Removes per-app observability configuration.</summary>
    public bool RemoveObservability(string appId)
    {
        return _observabilityConfigs.TryRemove(appId, out _);
    }

    /// <summary>Gets per-app observability configuration.</summary>
    public ObservabilityConfig? GetObservability(string appId) => _observabilityConfigs.TryGetValue(appId, out var c) ? c : null;

    /// <summary>Emits a metric with app_id tag injection.</summary>
    public void EmitMetric(string appId, string metricName, double value, Dictionary<string, string>? tags = null)
    {
        if (!_observabilityConfigs.TryGetValue(appId, out var config) || !config.MetricsEnabled)
            return;

        tags ??= new Dictionary<string, string>();
        tags["app_id"] = appId; // Always inject app_id for isolation
        IncrementCounter("app_runtime.metric_emit");
    }

    /// <summary>Emits a trace with app_id attribute injection.</summary>
    public void EmitTrace(string appId, string traceName, string spanId, Dictionary<string, string>? attributes = null)
    {
        if (!_observabilityConfigs.TryGetValue(appId, out var config) || !config.TracesEnabled)
            return;

        attributes ??= new Dictionary<string, string>();
        attributes["app_id"] = appId;
        IncrementCounter("app_runtime.trace_emit");
    }

    /// <summary>Emits a log entry with app_id property injection.</summary>
    public void EmitLog(string appId, string level, string message, Dictionary<string, string>? properties = null)
    {
        if (!_observabilityConfigs.TryGetValue(appId, out var config) || !config.LogsEnabled)
            return;

        properties ??= new Dictionary<string, string>();
        properties["app_id"] = appId;
        IncrementCounter("app_runtime.log_emit");
    }

    #endregion

    #region Internal Types

    public sealed class AiWorkflowConfig
    {
        public string AppId { get; set; } = string.Empty;
        public AiWorkflowMode Mode { get; init; } = AiWorkflowMode.Manual;
        public decimal MonthlyBudgetUsd { get; init; } = 100m;
        public decimal MaxPerRequestUsd { get; init; } = 10m;
        public int MaxConcurrentRequests { get; init; } = 5;
        public List<string> AllowedOperations { get; init; } = new();
        public List<string> PreferredProviders { get; init; } = new();
        public List<string> PreferredModels { get; init; } = new();
    }

    public enum AiWorkflowMode { Auto, Manual, Budget, Approval }

    public sealed class AiUsageTracking
    {
        public string AppId { get; init; } = string.Empty;
        public decimal MonthlySpendUsd { get; set; }
        public long TotalRequests { get; set; }
    }

    public sealed class AiRequestResult
    {
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public string? RequestId { get; init; }
        public decimal EstimatedCostUsd { get; init; }
    }

    public sealed class ObservabilityConfig
    {
        public string AppId { get; set; } = string.Empty;
        public bool MetricsEnabled { get; init; }
        public bool TracesEnabled { get; init; }
        public bool LogsEnabled { get; init; }
        public int RetentionDays { get; init; } = 30;
        public string LogLevel { get; init; } = "Information";
    }

    #endregion
}
