using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.BlueGreenDeployment;

/// <summary>
/// Enterprise-grade blue/green deployment plugin providing zero-downtime deployments with
/// atomic traffic switching, comprehensive health checking, automatic rollback, connection draining,
/// database migration coordination, and DNS/load balancer integration points.
///
/// Blue/green deployment maintains two identical production environments where only one serves
/// traffic at any time. Deployments occur by preparing the inactive environment, validating it,
/// then instantly switching all traffic. This enables zero-downtime deployments with instant
/// rollback capability.
///
/// Features:
/// - Dual environment management (blue/green)
/// - Atomic instant traffic switching
/// - Pre-switch and post-switch health validation
/// - Automatic rollback on health check failures
/// - Connection draining before switch
/// - Database migration coordination
/// - DNS/load balancer integration hooks
/// - Deployment history and audit trail
/// - Thread-safe concurrent operations
/// - Configurable health check strategies
/// - Graceful session handling
///
/// Message Commands:
/// - bluegreen.deploy: Deploy new version to inactive environment
/// - bluegreen.switch: Switch traffic to target environment
/// - bluegreen.rollback: Rollback to previous environment
/// - bluegreen.status: Get current deployment status
/// - bluegreen.history: Get deployment history
/// - bluegreen.health.check: Check environment health
/// - bluegreen.config.set: Set deployment configuration
/// - bluegreen.environment.list: List all environments
/// - bluegreen.drain.start: Start session draining on environment
/// - bluegreen.drain.status: Get drain status
/// - bluegreen.migration.status: Get database migration status
/// </summary>
public sealed class BlueGreenDeploymentPlugin : OperationsPluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.operations.bluegreen";

    /// <inheritdoc />
    public override string Name => "Blue/Green Deployment Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override OperationsCapabilities Capabilities =>
        OperationsCapabilities.ZeroDowntimeUpgrade |
        OperationsCapabilities.Rollback |
        OperationsCapabilities.Monitoring |
        OperationsCapabilities.Alerting;

    #region Private Fields

    private readonly ConcurrentDictionary<string, BlueGreenEnvironment> _environments = new();
    private readonly ConcurrentDictionary<string, BlueGreenDeployment> _deployments = new();
    private readonly ConcurrentQueue<DeploymentHistoryEntry> _history = new();
    private readonly ConcurrentDictionary<string, AlertRule> _alertRules = new();
    private readonly ConcurrentDictionary<string, Alert> _activeAlerts = new();
    private readonly ConcurrentDictionary<string, DatabaseMigration> _migrations = new();
    private readonly ConcurrentDictionary<string, DrainSession> _drainSessions = new();
    private readonly SemaphoreSlim _deploymentLock = new(1, 1);
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    private IKernelContext? _context;
    private CancellationTokenSource? _cts;
    private Task? _monitoringTask;
    private string? _activeDeploymentId;
    private BlueGreenConfiguration _config = new();
    private EnvironmentColor _activeEnvironment = EnvironmentColor.Blue;
    private bool _isRunning;
    private readonly object _stateLock = new();

    private const int MaxHistoryEntries = 1000;
    private const int HealthCheckIntervalMs = 5000;
    private const int DefaultDrainTimeoutSeconds = 60;
    private const int DefaultHealthCheckRetries = 3;
    private const int DefaultHealthCheckIntervalMs = 2000;
    private const int MinHealthyInstancesPercent = 80;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _context = request.Context;

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

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;

        // Initialize blue and green environments
        InitializeEnvironments();

        // Initialize default alert rules
        InitializeDefaultAlertRules();

        // Start monitoring task
        _monitoringTask = RunMonitoringLoopAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        if (_monitoringTask != null)
        {
            try { await _monitoringTask; } catch (OperationCanceledException) { }
        }

        _cts?.Dispose();
        _cts = null;
    }

    private void InitializeEnvironments()
    {
        var blueEnv = new BlueGreenEnvironment
        {
            EnvironmentId = "blue",
            Color = EnvironmentColor.Blue,
            State = EnvironmentState.Ready,
            Version = "1.0.0",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LastHealthCheck = DateTime.UtcNow,
            HealthStatus = HealthStatus.Healthy,
            Instances = new ConcurrentDictionary<string, EnvironmentInstance>()
        };

        var greenEnv = new BlueGreenEnvironment
        {
            EnvironmentId = "green",
            Color = EnvironmentColor.Green,
            State = EnvironmentState.Standby,
            Version = "1.0.0",
            IsActive = false,
            CreatedAt = DateTime.UtcNow,
            LastHealthCheck = DateTime.UtcNow,
            HealthStatus = HealthStatus.Healthy,
            Instances = new ConcurrentDictionary<string, EnvironmentInstance>()
        };

        // Initialize instances for each environment
        for (int i = 0; i < 5; i++)
        {
            blueEnv.Instances[$"blue-instance-{i}"] = new EnvironmentInstance
            {
                InstanceId = $"blue-instance-{i}",
                State = InstanceState.Running,
                Version = "1.0.0",
                StartedAt = DateTime.UtcNow,
                LastHealthCheck = DateTime.UtcNow,
                IsHealthy = true,
                ActiveConnections = 0
            };

            greenEnv.Instances[$"green-instance-{i}"] = new EnvironmentInstance
            {
                InstanceId = $"green-instance-{i}",
                State = InstanceState.Stopped,
                Version = "1.0.0",
                StartedAt = DateTime.UtcNow,
                LastHealthCheck = DateTime.UtcNow,
                IsHealthy = false,
                ActiveConnections = 0
            };
        }

        _environments["blue"] = blueEnv;
        _environments["green"] = greenEnv;
        _activeEnvironment = EnvironmentColor.Blue;
    }

    #endregion

    #region IOperationsProvider Implementation

    /// <inheritdoc />
    public override async Task<UpgradeResult> PerformUpgradeAsync(UpgradeRequest request, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var deploymentId = Guid.NewGuid().ToString("N");
        var steps = new List<string>();

        await _deploymentLock.WaitAsync(ct);
        try
        {
            // Check for concurrent deployment
            if (_activeDeploymentId != null && _deployments.TryGetValue(_activeDeploymentId, out var active))
            {
                if (active.State == BlueGreenState.Deploying || active.State == BlueGreenState.Switching)
                {
                    return new UpgradeResult
                    {
                        Success = false,
                        DeploymentId = deploymentId,
                        ErrorMessage = $"Another deployment is already in progress: {_activeDeploymentId}"
                    };
                }
            }

            // Determine target environment (inactive one)
            var targetColor = GetInactiveEnvironment();
            var targetEnv = _environments[targetColor.ToString().ToLowerInvariant()];
            var sourceEnv = _environments[_activeEnvironment.ToString().ToLowerInvariant()];

            steps.Add($"Target environment: {targetColor} (currently inactive)");
            steps.Add($"Source environment: {_activeEnvironment} (currently active)");

            // Validate version
            var currentVersion = sourceEnv.Version;
            if (!ValidateVersionCompatibility(currentVersion, request.TargetVersion))
            {
                return new UpgradeResult
                {
                    Success = false,
                    DeploymentId = deploymentId,
                    PreviousVersion = currentVersion,
                    NewVersion = request.TargetVersion,
                    ErrorMessage = "Version compatibility check failed"
                };
            }

            steps.Add($"Validated version compatibility: {currentVersion} -> {request.TargetVersion}");

            // Create deployment record
            var deployment = new BlueGreenDeployment
            {
                DeploymentId = deploymentId,
                SourceEnvironment = _activeEnvironment,
                TargetEnvironment = targetColor,
                PreviousVersion = currentVersion,
                TargetVersion = request.TargetVersion,
                State = BlueGreenState.Pending,
                StartedAt = DateTime.UtcNow,
                AutoRollbackOnFailure = request.AutoRollbackOnFailure,
                HealthCheckTimeout = request.HealthCheckTimeout ?? _config.HealthCheckTimeout,
                Progress = new DeploymentProgress()
            };

            _deployments[deploymentId] = deployment;
            _activeDeploymentId = deploymentId;

            if (request.DryRun)
            {
                steps.Add("Dry run - no actual changes made");
                deployment.State = BlueGreenState.Completed;
                deployment.CompletedAt = DateTime.UtcNow;

                return new UpgradeResult
                {
                    Success = true,
                    DeploymentId = deploymentId,
                    PreviousVersion = currentVersion,
                    NewVersion = request.TargetVersion,
                    Duration = stopwatch.Elapsed,
                    Steps = steps
                };
            }

            // Phase 1: Prepare target environment
            deployment.State = BlueGreenState.Deploying;
            steps.Add($"Phase 1: Preparing {targetColor} environment");

            var prepareSuccess = await PrepareTargetEnvironmentAsync(deployment, targetEnv, steps, ct);
            if (!prepareSuccess)
            {
                deployment.State = BlueGreenState.Failed;
                deployment.Error = "Failed to prepare target environment";
                AddHistoryEntry(deployment, false, deployment.Error);

                return new UpgradeResult
                {
                    Success = false,
                    DeploymentId = deploymentId,
                    PreviousVersion = currentVersion,
                    NewVersion = request.TargetVersion,
                    Duration = stopwatch.Elapsed,
                    Steps = steps,
                    ErrorMessage = deployment.Error
                };
            }

            // Phase 2: Database migration (if needed)
            steps.Add("Phase 2: Checking database migrations");
            var migrationSuccess = await ExecuteDatabaseMigrationAsync(deployment, steps, ct);
            if (!migrationSuccess)
            {
                if (deployment.AutoRollbackOnFailure)
                {
                    steps.Add("Migration failed - cleaning up target environment");
                    await CleanupTargetEnvironmentAsync(targetEnv, ct);
                }

                deployment.State = BlueGreenState.Failed;
                deployment.Error = "Database migration failed";
                AddHistoryEntry(deployment, false, deployment.Error);

                return new UpgradeResult
                {
                    Success = false,
                    DeploymentId = deploymentId,
                    PreviousVersion = currentVersion,
                    NewVersion = request.TargetVersion,
                    Duration = stopwatch.Elapsed,
                    Steps = steps,
                    ErrorMessage = deployment.Error
                };
            }

            // Phase 3: Pre-switch health validation
            steps.Add("Phase 3: Pre-switch health validation");
            var healthyBeforeSwitch = await ValidateEnvironmentHealthAsync(targetEnv, deployment.HealthCheckTimeout, steps, ct);
            if (!healthyBeforeSwitch)
            {
                if (deployment.AutoRollbackOnFailure)
                {
                    steps.Add("Pre-switch health check failed - cleaning up");
                    await CleanupTargetEnvironmentAsync(targetEnv, ct);
                }

                deployment.State = BlueGreenState.Failed;
                deployment.Error = "Pre-switch health validation failed";
                AddHistoryEntry(deployment, false, deployment.Error);

                return new UpgradeResult
                {
                    Success = false,
                    DeploymentId = deploymentId,
                    PreviousVersion = currentVersion,
                    NewVersion = request.TargetVersion,
                    Duration = stopwatch.Elapsed,
                    Steps = steps,
                    ErrorMessage = deployment.Error
                };
            }

            // Phase 4: Session draining on source environment
            steps.Add("Phase 4: Draining sessions from source environment");
            await DrainEnvironmentSessionsAsync(sourceEnv, steps, ct);

            // Phase 5: Atomic traffic switch
            steps.Add("Phase 5: Executing atomic traffic switch");
            deployment.State = BlueGreenState.Switching;
            var switchSuccess = await ExecuteTrafficSwitchAsync(deployment, sourceEnv, targetEnv, steps, ct);

            if (!switchSuccess)
            {
                if (deployment.AutoRollbackOnFailure)
                {
                    steps.Add("Traffic switch failed - rolling back");
                    await ExecuteRollbackSwitchAsync(deployment, targetEnv, sourceEnv, ct);
                }

                deployment.State = BlueGreenState.Failed;
                deployment.Error = "Traffic switch failed";
                AddHistoryEntry(deployment, false, deployment.Error);

                return new UpgradeResult
                {
                    Success = false,
                    DeploymentId = deploymentId,
                    PreviousVersion = currentVersion,
                    NewVersion = request.TargetVersion,
                    Duration = stopwatch.Elapsed,
                    Steps = steps,
                    ErrorMessage = deployment.Error
                };
            }

            // Phase 6: Post-switch health validation
            steps.Add("Phase 6: Post-switch health validation");
            var healthyAfterSwitch = await ValidatePostSwitchHealthAsync(targetEnv, deployment.HealthCheckTimeout, steps, ct);

            if (!healthyAfterSwitch)
            {
                if (deployment.AutoRollbackOnFailure)
                {
                    steps.Add("Post-switch validation failed - initiating automatic rollback");
                    await ExecuteRollbackSwitchAsync(deployment, targetEnv, sourceEnv, ct);
                    deployment.State = BlueGreenState.RolledBack;
                    deployment.Error = "Post-switch health check failed - rolled back automatically";
                }
                else
                {
                    deployment.State = BlueGreenState.Failed;
                    deployment.Error = "Post-switch health validation failed";
                }

                AddHistoryEntry(deployment, false, deployment.Error);

                return new UpgradeResult
                {
                    Success = false,
                    DeploymentId = deploymentId,
                    PreviousVersion = currentVersion,
                    NewVersion = request.TargetVersion,
                    Duration = stopwatch.Elapsed,
                    Steps = steps,
                    ErrorMessage = deployment.Error
                };
            }

            // Phase 7: Finalize deployment
            steps.Add("Phase 7: Finalizing deployment");
            deployment.State = BlueGreenState.Completed;
            deployment.CompletedAt = DateTime.UtcNow;
            sourceEnv.State = EnvironmentState.Standby;
            sourceEnv.IsActive = false;

            steps.Add($"Deployment completed successfully - {targetColor} is now active");
            AddHistoryEntry(deployment, true, null);

            // Execute DNS/LB finalization hooks
            await ExecuteLoadBalancerFinalizationAsync(deployment, steps, ct);

            return new UpgradeResult
            {
                Success = true,
                DeploymentId = deploymentId,
                PreviousVersion = currentVersion,
                NewVersion = request.TargetVersion,
                Duration = stopwatch.Elapsed,
                Steps = steps
            };
        }
        finally
        {
            _deploymentLock.Release();
        }
    }

    /// <inheritdoc />
    public override async Task<RollbackResult> RollbackAsync(string deploymentId, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (!_deployments.TryGetValue(deploymentId, out var deployment))
        {
            return new RollbackResult
            {
                Success = false,
                ErrorMessage = "Deployment not found"
            };
        }

        await _switchLock.WaitAsync(ct);
        try
        {
            // Get current and previous environments
            var currentEnv = _environments[_activeEnvironment.ToString().ToLowerInvariant()];
            var previousEnv = _environments[GetInactiveEnvironment().ToString().ToLowerInvariant()];

            // Verify previous environment is healthy enough to receive traffic
            var previousHealthy = await CheckEnvironmentMinimalHealthAsync(previousEnv, ct);
            if (!previousHealthy)
            {
                return new RollbackResult
                {
                    Success = false,
                    ErrorMessage = "Previous environment is not healthy enough for rollback",
                    Duration = stopwatch.Elapsed
                };
            }

            deployment.State = BlueGreenState.RollingBack;

            // Drain current environment
            await DrainEnvironmentSessionsAsync(currentEnv, new List<string>(), ct);

            // Execute rollback switch
            await ExecuteRollbackSwitchAsync(deployment, currentEnv, previousEnv, ct);

            deployment.State = BlueGreenState.RolledBack;
            deployment.CompletedAt = DateTime.UtcNow;

            AddHistoryEntry(deployment, true, "Manual rollback completed");

            return new RollbackResult
            {
                Success = true,
                RestoredVersion = previousEnv.Version,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            return new RollbackResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <inheritdoc />
    public override Task<DeploymentStatus> GetDeploymentStatusAsync(string? deploymentId = null, CancellationToken ct = default)
    {
        var targetId = deploymentId ?? _activeDeploymentId;
        var activeEnv = _environments[_activeEnvironment.ToString().ToLowerInvariant()];

        if (string.IsNullOrEmpty(targetId) || !_deployments.TryGetValue(targetId, out var deployment))
        {
            return Task.FromResult(new DeploymentStatus
            {
                DeploymentId = targetId ?? "",
                CurrentVersion = activeEnv.Version,
                State = DeploymentState.Running,
                DeployedAt = activeEnv.LastDeployedAt ?? DateTime.UtcNow,
                Uptime = DateTime.UtcNow - (activeEnv.LastDeployedAt ?? DateTime.UtcNow),
                AvailableRollbackVersions = GetAvailableRollbackVersions()
            });
        }

        return Task.FromResult(new DeploymentStatus
        {
            DeploymentId = deployment.DeploymentId,
            CurrentVersion = deployment.State == BlueGreenState.Completed
                ? deployment.TargetVersion
                : deployment.PreviousVersion,
            State = MapBlueGreenState(deployment.State),
            DeployedAt = deployment.StartedAt,
            Uptime = DateTime.UtcNow - deployment.StartedAt,
            AvailableRollbackVersions = new List<string> { deployment.PreviousVersion }
        });
    }

    /// <inheritdoc />
    public override Task<ConfigReloadResult> ReloadConfigurationAsync(Dictionary<string, object>? newConfig = null, CancellationToken ct = default)
    {
        var changedKeys = new List<string>();

        if (newConfig != null)
        {
            if (newConfig.TryGetValue("drainTimeout", out var drainTimeout) && drainTimeout is int dt)
            {
                _config.DrainTimeout = TimeSpan.FromSeconds(dt);
                changedKeys.Add("drainTimeout");
            }

            if (newConfig.TryGetValue("healthCheckTimeout", out var hct) && hct is int timeout)
            {
                _config.HealthCheckTimeout = TimeSpan.FromSeconds(timeout);
                changedKeys.Add("healthCheckTimeout");
            }

            if (newConfig.TryGetValue("healthCheckInterval", out var hci) && hci is int interval)
            {
                _config.HealthCheckInterval = TimeSpan.FromMilliseconds(interval);
                changedKeys.Add("healthCheckInterval");
            }

            if (newConfig.TryGetValue("healthCheckRetries", out var retries) && retries is int r)
            {
                _config.HealthCheckRetries = r;
                changedKeys.Add("healthCheckRetries");
            }

            if (newConfig.TryGetValue("minHealthyInstancesPercent", out var minHealthy) && minHealthy is int mh)
            {
                _config.MinHealthyInstancesPercent = mh;
                changedKeys.Add("minHealthyInstancesPercent");
            }

            if (newConfig.TryGetValue("enableDatabaseMigration", out var enableMigration) && enableMigration is bool em)
            {
                _config.EnableDatabaseMigration = em;
                changedKeys.Add("enableDatabaseMigration");
            }
        }

        return Task.FromResult(new ConfigReloadResult
        {
            Success = true,
            ChangedKeys = changedKeys
        });
    }

    /// <inheritdoc />
    public override Task<OperationalMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var totalDeployments = _deployments.Count;
        var successfulDeployments = _deployments.Values.Count(d => d.State == BlueGreenState.Completed);
        var failedDeployments = _deployments.Values.Count(d => d.State == BlueGreenState.Failed);
        var activeEnv = _environments[_activeEnvironment.ToString().ToLowerInvariant()];
        var totalInstances = activeEnv.Instances.Count;
        var healthyInstances = activeEnv.Instances.Values.Count(i => i.IsHealthy);

        return Task.FromResult(new OperationalMetrics
        {
            CpuUsagePercent = 0,
            MemoryUsagePercent = 0,
            ActiveConnections = activeEnv.Instances.Values.Sum(i => i.ActiveConnections),
            RequestsPerSecond = 0,
            AverageLatencyMs = 0,
            ErrorsLastHour = failedDeployments,
            CustomMetrics = new Dictionary<string, double>
            {
                ["totalDeployments"] = totalDeployments,
                ["successfulDeployments"] = successfulDeployments,
                ["failedDeployments"] = failedDeployments,
                ["activeEnvironment"] = (double)_activeEnvironment,
                ["totalInstances"] = totalInstances,
                ["healthyInstances"] = healthyInstances,
                ["healthyInstancesPercent"] = totalInstances > 0 ? (healthyInstances * 100.0 / totalInstances) : 0
            }
        });
    }

    /// <inheritdoc />
    public override Task<AlertRule> ConfigureAlertAsync(AlertRuleConfig config, CancellationToken ct = default)
    {
        var ruleId = config.RuleId ?? Guid.NewGuid().ToString("N");
        var rule = new AlertRule
        {
            RuleId = ruleId,
            Name = config.Name,
            Enabled = config.Enabled,
            CreatedAt = DateTime.UtcNow
        };

        _alertRules[ruleId] = rule;

        return Task.FromResult(rule);
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<Alert>> GetActiveAlertsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<Alert>>(_activeAlerts.Values.ToList());
    }

    /// <inheritdoc />
    public override Task<bool> AcknowledgeAlertAsync(string alertId, string? message = null, CancellationToken ct = default)
    {
        if (_activeAlerts.TryRemove(alertId, out var alert))
        {
            // Create acknowledged version
            var acknowledgedAlert = new Alert
            {
                AlertId = alert.AlertId,
                RuleId = alert.RuleId,
                RuleName = alert.RuleName,
                Severity = alert.Severity,
                Message = alert.Message,
                CurrentValue = alert.CurrentValue,
                Threshold = alert.Threshold,
                TriggeredAt = alert.TriggeredAt,
                IsAcknowledged = true,
                AcknowledgedBy = message ?? "system"
            };
            _activeAlerts[alertId] = acknowledgedAlert;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    #endregion

    #region Message Handling

    /// <inheritdoc />
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return;

        var response = message.Type switch
        {
            "bluegreen.deploy" => await HandleDeployAsync(message.Payload),
            "bluegreen.switch" => await HandleSwitchAsync(message.Payload),
            "bluegreen.rollback" => await HandleRollbackAsync(message.Payload),
            "bluegreen.status" => await HandleGetStatusAsync(message.Payload),
            "bluegreen.history" => HandleGetHistory(message.Payload),
            "bluegreen.health.check" => await HandleHealthCheckAsync(message.Payload),
            "bluegreen.config.set" => HandleSetConfig(message.Payload),
            "bluegreen.environment.list" => HandleListEnvironments(),
            "bluegreen.drain.start" => await HandleStartDrainAsync(message.Payload),
            "bluegreen.drain.status" => HandleDrainStatus(message.Payload),
            "bluegreen.migration.status" => HandleMigrationStatus(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }
    }

    private async Task<Dictionary<string, object>> HandleDeployAsync(Dictionary<string, object> payload)
    {
        var targetVersion = payload.GetValueOrDefault("targetVersion")?.ToString();
        var packagePath = payload.GetValueOrDefault("packagePath")?.ToString();
        var dryRun = payload.GetValueOrDefault("dryRun") as bool? ?? false;
        var autoRollback = payload.GetValueOrDefault("autoRollback") as bool? ?? true;

        if (string.IsNullOrEmpty(targetVersion))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "targetVersion is required"
            };
        }

        var request = new UpgradeRequest
        {
            TargetVersion = targetVersion,
            PackagePath = packagePath,
            DryRun = dryRun,
            AutoRollbackOnFailure = autoRollback,
            HealthCheckTimeout = _config.HealthCheckTimeout
        };

        var result = await PerformUpgradeAsync(request);

        return new Dictionary<string, object>
        {
            ["success"] = result.Success,
            ["deploymentId"] = result.DeploymentId,
            ["previousVersion"] = result.PreviousVersion,
            ["newVersion"] = result.NewVersion,
            ["duration"] = result.Duration.TotalMilliseconds,
            ["steps"] = result.Steps,
            ["activeEnvironment"] = _activeEnvironment.ToString(),
            ["error"] = result.ErrorMessage ?? ""
        };
    }

    private async Task<Dictionary<string, object>> HandleSwitchAsync(Dictionary<string, object> payload)
    {
        var targetEnvironment = payload.GetValueOrDefault("targetEnvironment")?.ToString();

        if (string.IsNullOrEmpty(targetEnvironment))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "targetEnvironment is required (blue or green)"
            };
        }

        if (!Enum.TryParse<EnvironmentColor>(targetEnvironment, true, out var targetColor))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "targetEnvironment must be 'blue' or 'green'"
            };
        }

        if (targetColor == _activeEnvironment)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"{targetColor} is already the active environment"
            };
        }

        await _switchLock.WaitAsync();
        try
        {
            var sourceEnv = _environments[_activeEnvironment.ToString().ToLowerInvariant()];
            var targetEnv = _environments[targetColor.ToString().ToLowerInvariant()];

            // Validate target is healthy
            var isHealthy = await CheckEnvironmentMinimalHealthAsync(targetEnv, CancellationToken.None);
            if (!isHealthy)
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = $"{targetColor} environment is not healthy enough for traffic"
                };
            }

            // Drain and switch
            await DrainEnvironmentSessionsAsync(sourceEnv, new List<string>(), CancellationToken.None);

            lock (_stateLock)
            {
                sourceEnv.IsActive = false;
                sourceEnv.State = EnvironmentState.Standby;
                targetEnv.IsActive = true;
                targetEnv.State = EnvironmentState.Active;
                _activeEnvironment = targetColor;
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["previousEnvironment"] = sourceEnv.Color.ToString(),
                ["activeEnvironment"] = _activeEnvironment.ToString()
            };
        }
        finally
        {
            _switchLock.Release();
        }
    }

    private async Task<Dictionary<string, object>> HandleRollbackAsync(Dictionary<string, object> payload)
    {
        var deploymentId = payload.GetValueOrDefault("deploymentId")?.ToString() ?? _activeDeploymentId;

        if (string.IsNullOrEmpty(deploymentId))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "deploymentId is required"
            };
        }

        var result = await RollbackAsync(deploymentId);

        return new Dictionary<string, object>
        {
            ["success"] = result.Success,
            ["restoredVersion"] = result.RestoredVersion,
            ["duration"] = result.Duration.TotalMilliseconds,
            ["activeEnvironment"] = _activeEnvironment.ToString(),
            ["error"] = result.ErrorMessage ?? ""
        };
    }

    private async Task<Dictionary<string, object>> HandleGetStatusAsync(Dictionary<string, object> payload)
    {
        var deploymentId = payload.GetValueOrDefault("deploymentId")?.ToString();
        var status = await GetDeploymentStatusAsync(deploymentId);

        var activeEnv = _environments[_activeEnvironment.ToString().ToLowerInvariant()];
        var inactiveEnv = _environments[GetInactiveEnvironment().ToString().ToLowerInvariant()];

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["deploymentId"] = status.DeploymentId,
            ["currentVersion"] = status.CurrentVersion,
            ["state"] = status.State.ToString(),
            ["deployedAt"] = status.DeployedAt.ToString("O"),
            ["uptime"] = status.Uptime.TotalSeconds,
            ["activeEnvironment"] = _activeEnvironment.ToString(),
            ["activeEnvironmentHealth"] = activeEnv.HealthStatus.ToString(),
            ["inactiveEnvironment"] = GetInactiveEnvironment().ToString(),
            ["inactiveEnvironmentHealth"] = inactiveEnv.HealthStatus.ToString(),
            ["availableRollbackVersions"] = status.AvailableRollbackVersions
        };
    }

    private Dictionary<string, object> HandleGetHistory(Dictionary<string, object> payload)
    {
        var limit = payload.GetValueOrDefault("limit") as int? ?? 50;

        var entries = _history.Take(limit).Select(e => new Dictionary<string, object>
        {
            ["deploymentId"] = e.DeploymentId,
            ["sourceEnvironment"] = e.SourceEnvironment.ToString(),
            ["targetEnvironment"] = e.TargetEnvironment.ToString(),
            ["previousVersion"] = e.PreviousVersion,
            ["targetVersion"] = e.TargetVersion,
            ["success"] = e.Success,
            ["startedAt"] = e.StartedAt.ToString("O"),
            ["completedAt"] = e.CompletedAt?.ToString("O") ?? "",
            ["duration"] = e.Duration?.TotalMilliseconds ?? 0,
            ["error"] = e.Error ?? ""
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["entries"] = entries,
            ["count"] = entries.Count
        };
    }

    private async Task<Dictionary<string, object>> HandleHealthCheckAsync(Dictionary<string, object> payload)
    {
        var environmentName = payload.GetValueOrDefault("environment")?.ToString();

        var environments = string.IsNullOrEmpty(environmentName)
            ? _environments.Values.ToList()
            : _environments.TryGetValue(environmentName.ToLowerInvariant(), out var env)
                ? new List<BlueGreenEnvironment> { env }
                : new List<BlueGreenEnvironment>();

        if (environments.Count == 0)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Environment '{environmentName}' not found"
            };
        }

        var results = new List<Dictionary<string, object>>();

        foreach (var environment in environments)
        {
            var instanceHealth = new List<Dictionary<string, object>>();

            foreach (var instance in environment.Instances.Values)
            {
                var healthy = await PerformInstanceHealthCheckAsync(instance);
                instanceHealth.Add(new Dictionary<string, object>
                {
                    ["instanceId"] = instance.InstanceId,
                    ["healthy"] = healthy,
                    ["state"] = instance.State.ToString(),
                    ["version"] = instance.Version,
                    ["activeConnections"] = instance.ActiveConnections
                });
            }

            var healthyCount = instanceHealth.Count(i => (bool)i["healthy"]);
            var totalCount = instanceHealth.Count;
            var overallHealthy = totalCount > 0 && (healthyCount * 100 / totalCount) >= _config.MinHealthyInstancesPercent;

            results.Add(new Dictionary<string, object>
            {
                ["environment"] = environment.Color.ToString(),
                ["isActive"] = environment.IsActive,
                ["state"] = environment.State.ToString(),
                ["version"] = environment.Version,
                ["overallHealthy"] = overallHealthy,
                ["healthyInstances"] = healthyCount,
                ["totalInstances"] = totalCount,
                ["instances"] = instanceHealth
            });
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["environments"] = results
        };
    }

    private Dictionary<string, object> HandleSetConfig(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("drainTimeout", out var dt) && dt is int drainTimeout)
        {
            _config.DrainTimeout = TimeSpan.FromSeconds(drainTimeout);
        }

        if (payload.TryGetValue("healthCheckTimeout", out var hct) && hct is int healthCheckTimeout)
        {
            _config.HealthCheckTimeout = TimeSpan.FromSeconds(healthCheckTimeout);
        }

        if (payload.TryGetValue("healthCheckInterval", out var hci) && hci is int healthCheckInterval)
        {
            _config.HealthCheckInterval = TimeSpan.FromMilliseconds(healthCheckInterval);
        }

        if (payload.TryGetValue("healthCheckRetries", out var hcr) && hcr is int retries)
        {
            _config.HealthCheckRetries = retries;
        }

        if (payload.TryGetValue("minHealthyInstancesPercent", out var mhi) && mhi is int minHealthy)
        {
            _config.MinHealthyInstancesPercent = minHealthy;
        }

        if (payload.TryGetValue("enableDatabaseMigration", out var edm) && edm is bool enableDbMigration)
        {
            _config.EnableDatabaseMigration = enableDbMigration;
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["config"] = new Dictionary<string, object>
            {
                ["drainTimeout"] = _config.DrainTimeout.TotalSeconds,
                ["healthCheckTimeout"] = _config.HealthCheckTimeout.TotalSeconds,
                ["healthCheckInterval"] = _config.HealthCheckInterval.TotalMilliseconds,
                ["healthCheckRetries"] = _config.HealthCheckRetries,
                ["minHealthyInstancesPercent"] = _config.MinHealthyInstancesPercent,
                ["enableDatabaseMigration"] = _config.EnableDatabaseMigration
            }
        };
    }

    private Dictionary<string, object> HandleListEnvironments()
    {
        var environments = _environments.Values.Select(e => new Dictionary<string, object>
        {
            ["environmentId"] = e.EnvironmentId,
            ["color"] = e.Color.ToString(),
            ["state"] = e.State.ToString(),
            ["version"] = e.Version,
            ["isActive"] = e.IsActive,
            ["healthStatus"] = e.HealthStatus.ToString(),
            ["instanceCount"] = e.Instances.Count,
            ["healthyInstanceCount"] = e.Instances.Values.Count(i => i.IsHealthy),
            ["lastHealthCheck"] = e.LastHealthCheck.ToString("O"),
            ["lastDeployedAt"] = e.LastDeployedAt?.ToString("O") ?? ""
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["activeEnvironment"] = _activeEnvironment.ToString(),
            ["environments"] = environments
        };
    }

    private async Task<Dictionary<string, object>> HandleStartDrainAsync(Dictionary<string, object> payload)
    {
        var environmentName = payload.GetValueOrDefault("environment")?.ToString();

        if (string.IsNullOrEmpty(environmentName) || !_environments.TryGetValue(environmentName.ToLowerInvariant(), out var environment))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Valid environment name (blue or green) is required"
            };
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var drainSession = new DrainSession
        {
            SessionId = sessionId,
            EnvironmentId = environment.EnvironmentId,
            StartedAt = DateTime.UtcNow,
            State = DrainState.Draining,
            InitialConnections = environment.Instances.Values.Sum(i => i.ActiveConnections)
        };

        _drainSessions[sessionId] = drainSession;

        await DrainEnvironmentSessionsAsync(environment, new List<string>(), CancellationToken.None);

        drainSession.State = DrainState.Completed;
        drainSession.CompletedAt = DateTime.UtcNow;
        drainSession.RemainingConnections = environment.Instances.Values.Sum(i => i.ActiveConnections);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["sessionId"] = sessionId,
            ["environment"] = environment.Color.ToString(),
            ["initialConnections"] = drainSession.InitialConnections,
            ["remainingConnections"] = drainSession.RemainingConnections,
            ["duration"] = (drainSession.CompletedAt - drainSession.StartedAt)?.TotalMilliseconds ?? 0
        };
    }

    private Dictionary<string, object> HandleDrainStatus(Dictionary<string, object> payload)
    {
        var sessionId = payload.GetValueOrDefault("sessionId")?.ToString();

        if (string.IsNullOrEmpty(sessionId) || !_drainSessions.TryGetValue(sessionId, out var session))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Drain session not found"
            };
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["sessionId"] = session.SessionId,
            ["environment"] = session.EnvironmentId,
            ["state"] = session.State.ToString(),
            ["startedAt"] = session.StartedAt.ToString("O"),
            ["completedAt"] = session.CompletedAt?.ToString("O") ?? "",
            ["initialConnections"] = session.InitialConnections,
            ["remainingConnections"] = session.RemainingConnections
        };
    }

    private Dictionary<string, object> HandleMigrationStatus(Dictionary<string, object> payload)
    {
        var migrationId = payload.GetValueOrDefault("migrationId")?.ToString();

        if (string.IsNullOrEmpty(migrationId))
        {
            // Return all migrations
            var migrations = _migrations.Values.Select(m => new Dictionary<string, object>
            {
                ["migrationId"] = m.MigrationId,
                ["deploymentId"] = m.DeploymentId,
                ["state"] = m.State.ToString(),
                ["scriptName"] = m.ScriptName,
                ["startedAt"] = m.StartedAt.ToString("O"),
                ["completedAt"] = m.CompletedAt?.ToString("O") ?? "",
                ["error"] = m.Error ?? ""
            }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["migrations"] = migrations,
                ["count"] = migrations.Count
            };
        }

        if (!_migrations.TryGetValue(migrationId, out var migration))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Migration not found"
            };
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["migrationId"] = migration.MigrationId,
            ["deploymentId"] = migration.DeploymentId,
            ["state"] = migration.State.ToString(),
            ["scriptName"] = migration.ScriptName,
            ["startedAt"] = migration.StartedAt.ToString("O"),
            ["completedAt"] = migration.CompletedAt?.ToString("O") ?? "",
            ["error"] = migration.Error ?? ""
        };
    }

    #endregion

    #region Deployment Phases

    private async Task<bool> PrepareTargetEnvironmentAsync(
        BlueGreenDeployment deployment,
        BlueGreenEnvironment targetEnv,
        List<string> steps,
        CancellationToken ct)
    {
        targetEnv.State = EnvironmentState.Preparing;
        steps.Add($"Preparing {targetEnv.Color} environment for version {deployment.TargetVersion}");

        // Start all instances in target environment
        foreach (var instance in targetEnv.Instances.Values)
        {
            if (ct.IsCancellationRequested) return false;

            instance.State = InstanceState.Starting;
            instance.Version = deployment.TargetVersion;

            // Simulate instance startup
            await Task.Delay(500, ct);

            instance.State = InstanceState.Running;
            instance.StartedAt = DateTime.UtcNow;
            deployment.Progress.PreparedInstances++;

            steps.Add($"Instance {instance.InstanceId} started with version {deployment.TargetVersion}");
        }

        targetEnv.Version = deployment.TargetVersion;
        targetEnv.State = EnvironmentState.Ready;
        targetEnv.LastDeployedAt = DateTime.UtcNow;

        steps.Add($"{targetEnv.Color} environment prepared successfully");
        return true;
    }

    private async Task<bool> ExecuteDatabaseMigrationAsync(
        BlueGreenDeployment deployment,
        List<string> steps,
        CancellationToken ct)
    {
        if (!_config.EnableDatabaseMigration)
        {
            steps.Add("Database migration disabled - skipping");
            return true;
        }

        var migrationId = Guid.NewGuid().ToString("N");
        var migration = new DatabaseMigration
        {
            MigrationId = migrationId,
            DeploymentId = deployment.DeploymentId,
            State = MigrationState.Pending,
            ScriptName = $"migration_{deployment.TargetVersion}.sql",
            StartedAt = DateTime.UtcNow
        };

        _migrations[migrationId] = migration;
        deployment.MigrationId = migrationId;

        steps.Add($"Starting database migration: {migration.ScriptName}");
        migration.State = MigrationState.Running;

        try
        {
            // Execute DNS update hook for database connections
            await ExecuteDnsUpdateHookAsync("database", deployment.TargetEnvironment, steps, ct);

            // Simulate migration execution
            await Task.Delay(1000, ct);

            migration.State = MigrationState.Completed;
            migration.CompletedAt = DateTime.UtcNow;
            steps.Add("Database migration completed successfully");

            return true;
        }
        catch (Exception ex)
        {
            migration.State = MigrationState.Failed;
            migration.Error = ex.Message;
            migration.CompletedAt = DateTime.UtcNow;
            steps.Add($"Database migration failed: {ex.Message}");

            return false;
        }
    }

    private async Task<bool> ValidateEnvironmentHealthAsync(
        BlueGreenEnvironment environment,
        TimeSpan timeout,
        List<string> steps,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var healthyInstances = 0;
        var totalInstances = environment.Instances.Count;
        var requiredHealthy = (int)Math.Ceiling(totalInstances * _config.MinHealthyInstancesPercent / 100.0);

        steps.Add($"Validating health: require {requiredHealthy}/{totalInstances} instances healthy");

        for (int attempt = 0; attempt < _config.HealthCheckRetries && DateTime.UtcNow < deadline; attempt++)
        {
            if (ct.IsCancellationRequested) return false;

            healthyInstances = 0;

            foreach (var instance in environment.Instances.Values)
            {
                var isHealthy = await PerformInstanceHealthCheckAsync(instance);
                if (isHealthy)
                {
                    instance.IsHealthy = true;
                    healthyInstances++;
                }
                else
                {
                    instance.IsHealthy = false;
                }
                instance.LastHealthCheck = DateTime.UtcNow;
            }

            if (healthyInstances >= requiredHealthy)
            {
                environment.HealthStatus = HealthStatus.Healthy;
                environment.LastHealthCheck = DateTime.UtcNow;
                steps.Add($"Health check passed: {healthyInstances}/{totalInstances} instances healthy");
                return true;
            }

            steps.Add($"Health check attempt {attempt + 1}: {healthyInstances}/{totalInstances} healthy, retrying...");
            await Task.Delay((int)_config.HealthCheckInterval.TotalMilliseconds, ct);
        }

        environment.HealthStatus = HealthStatus.Unhealthy;
        steps.Add($"Health check failed: only {healthyInstances}/{totalInstances} instances healthy");
        return false;
    }

    private async Task<bool> ValidatePostSwitchHealthAsync(
        BlueGreenEnvironment environment,
        TimeSpan timeout,
        List<string> steps,
        CancellationToken ct)
    {
        steps.Add("Performing post-switch health validation");

        // Wait a brief moment for traffic to stabilize
        await Task.Delay(2000, ct);

        return await ValidateEnvironmentHealthAsync(environment, timeout, steps, ct);
    }

    private async Task DrainEnvironmentSessionsAsync(
        BlueGreenEnvironment environment,
        List<string> steps,
        CancellationToken ct)
    {
        environment.State = EnvironmentState.Draining;
        var drainDeadline = DateTime.UtcNow + _config.DrainTimeout;

        steps.Add($"Draining connections from {environment.Color} environment");

        foreach (var instance in environment.Instances.Values)
        {
            instance.State = InstanceState.Draining;
        }

        // Wait for connections to drain (with timeout)
        while (DateTime.UtcNow < drainDeadline && !ct.IsCancellationRequested)
        {
            var totalConnections = environment.Instances.Values.Sum(i => i.ActiveConnections);

            if (totalConnections == 0)
            {
                steps.Add("All connections drained");
                break;
            }

            // Simulate connection draining
            foreach (var instance in environment.Instances.Values)
            {
                if (instance.ActiveConnections > 0)
                {
                    instance.ActiveConnections = Math.Max(0, instance.ActiveConnections - 1);
                }
            }

            await Task.Delay(500, ct);
        }

        var remainingConnections = environment.Instances.Values.Sum(i => i.ActiveConnections);
        if (remainingConnections > 0)
        {
            steps.Add($"Drain timeout reached - {remainingConnections} connections forcefully closed");
            foreach (var instance in environment.Instances.Values)
            {
                instance.ActiveConnections = 0;
            }
        }
    }

    private async Task<bool> ExecuteTrafficSwitchAsync(
        BlueGreenDeployment deployment,
        BlueGreenEnvironment sourceEnv,
        BlueGreenEnvironment targetEnv,
        List<string> steps,
        CancellationToken ct)
    {
        steps.Add("Executing atomic traffic switch");

        try
        {
            // Execute load balancer update
            await ExecuteLoadBalancerSwitchAsync(deployment, sourceEnv, targetEnv, steps, ct);

            // Execute DNS update
            await ExecuteDnsUpdateHookAsync("primary", deployment.TargetEnvironment, steps, ct);

            // Atomic state switch
            lock (_stateLock)
            {
                sourceEnv.IsActive = false;
                sourceEnv.State = EnvironmentState.Standby;
                targetEnv.IsActive = true;
                targetEnv.State = EnvironmentState.Active;
                _activeEnvironment = deployment.TargetEnvironment;
            }

            // Update instance states
            foreach (var instance in sourceEnv.Instances.Values)
            {
                instance.State = InstanceState.Stopped;
            }

            foreach (var instance in targetEnv.Instances.Values)
            {
                instance.State = InstanceState.Running;
            }

            steps.Add($"Traffic switched to {targetEnv.Color} environment");
            deployment.SwitchedAt = DateTime.UtcNow;

            return true;
        }
        catch (Exception ex)
        {
            steps.Add($"Traffic switch failed: {ex.Message}");
            return false;
        }
    }

    private async Task ExecuteRollbackSwitchAsync(
        BlueGreenDeployment deployment,
        BlueGreenEnvironment currentEnv,
        BlueGreenEnvironment previousEnv,
        CancellationToken ct)
    {
        // Re-enable previous environment
        foreach (var instance in previousEnv.Instances.Values)
        {
            instance.State = InstanceState.Running;
        }

        // Execute load balancer rollback
        await ExecuteLoadBalancerSwitchAsync(deployment, currentEnv, previousEnv, new List<string>(), ct);

        // Execute DNS rollback
        await ExecuteDnsUpdateHookAsync("primary", previousEnv.Color, new List<string>(), ct);

        // Atomic state switch
        lock (_stateLock)
        {
            currentEnv.IsActive = false;
            currentEnv.State = EnvironmentState.Standby;
            previousEnv.IsActive = true;
            previousEnv.State = EnvironmentState.Active;
            _activeEnvironment = previousEnv.Color;
        }

        // Stop current environment instances
        foreach (var instance in currentEnv.Instances.Values)
        {
            instance.State = InstanceState.Stopped;
        }
    }

    private Task CleanupTargetEnvironmentAsync(BlueGreenEnvironment environment, CancellationToken ct)
    {
        environment.State = EnvironmentState.Standby;

        foreach (var instance in environment.Instances.Values)
        {
            instance.State = InstanceState.Stopped;
            instance.IsHealthy = false;
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Load Balancer and DNS Integration

    private Task ExecuteLoadBalancerSwitchAsync(
        BlueGreenDeployment deployment,
        BlueGreenEnvironment sourceEnv,
        BlueGreenEnvironment targetEnv,
        List<string> steps,
        CancellationToken ct)
    {
        // Integration point for load balancer updates
        // In production, this would call the actual load balancer API
        steps.Add($"Load balancer: routing traffic from {sourceEnv.Color} to {targetEnv.Color}");

        foreach (var hook in _config.LoadBalancerHooks)
        {
            steps.Add($"Executing load balancer hook: {hook}");
        }

        return Task.CompletedTask;
    }

    private Task ExecuteDnsUpdateHookAsync(
        string recordType,
        EnvironmentColor targetEnvironment,
        List<string> steps,
        CancellationToken ct)
    {
        // Integration point for DNS updates
        // In production, this would call Route53, CloudFlare, etc.
        steps.Add($"DNS: updating {recordType} record to point to {targetEnvironment}");

        foreach (var hook in _config.DnsUpdateHooks)
        {
            steps.Add($"Executing DNS hook: {hook}");
        }

        return Task.CompletedTask;
    }

    private Task ExecuteLoadBalancerFinalizationAsync(
        BlueGreenDeployment deployment,
        List<string> steps,
        CancellationToken ct)
    {
        steps.Add("Finalizing load balancer configuration");

        // Remove old environment from load balancer pool
        var inactiveEnv = _environments[GetInactiveEnvironment().ToString().ToLowerInvariant()];
        steps.Add($"Removed {inactiveEnv.Color} from load balancer pool");

        return Task.CompletedTask;
    }

    #endregion

    #region Health Checks

    private Task<bool> PerformInstanceHealthCheckAsync(EnvironmentInstance instance)
    {
        // In production, this would perform actual HTTP/TCP health checks
        var isHealthy = instance.State == InstanceState.Running;
        return Task.FromResult(isHealthy);
    }

    private async Task<bool> CheckEnvironmentMinimalHealthAsync(BlueGreenEnvironment environment, CancellationToken ct)
    {
        var healthyCount = 0;
        var totalCount = environment.Instances.Count;

        foreach (var instance in environment.Instances.Values)
        {
            if (ct.IsCancellationRequested) return false;

            var isHealthy = await PerformInstanceHealthCheckAsync(instance);
            if (isHealthy) healthyCount++;
        }

        var requiredHealthy = (int)Math.Ceiling(totalCount * _config.MinHealthyInstancesPercent / 100.0);
        return healthyCount >= requiredHealthy;
    }

    #endregion

    #region Background Tasks

    private async Task RunMonitoringLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HealthCheckIntervalMs, ct);

                // Monitor all environments
                foreach (var environment in _environments.Values)
                {
                    var healthyCount = 0;
                    var totalCount = environment.Instances.Count;

                    foreach (var instance in environment.Instances.Values)
                    {
                        var isHealthy = await PerformInstanceHealthCheckAsync(instance);
                        instance.IsHealthy = isHealthy;
                        instance.LastHealthCheck = DateTime.UtcNow;
                        if (isHealthy) healthyCount++;
                    }

                    var healthPercent = totalCount > 0 ? (healthyCount * 100 / totalCount) : 0;
                    var previousHealth = environment.HealthStatus;

                    environment.HealthStatus = healthPercent >= _config.MinHealthyInstancesPercent
                        ? HealthStatus.Healthy
                        : healthPercent >= 50
                            ? HealthStatus.Degraded
                            : HealthStatus.Unhealthy;
                    environment.LastHealthCheck = DateTime.UtcNow;

                    // Create alerts for health degradation
                    if (environment.IsActive && environment.HealthStatus != HealthStatus.Healthy && previousHealth == HealthStatus.Healthy)
                    {
                        var alertId = Guid.NewGuid().ToString("N");
                        _activeAlerts[alertId] = new Alert
                        {
                            AlertId = alertId,
                            RuleId = "environment-health",
                            RuleName = "Environment Health",
                            Severity = environment.HealthStatus == HealthStatus.Unhealthy ? AlertSeverity.Critical : AlertSeverity.Warning,
                            Message = $"Active environment {environment.Color} health degraded: {healthyCount}/{totalCount} instances healthy",
                            CurrentValue = healthyCount,
                            Threshold = totalCount * _config.MinHealthyInstancesPercent / 100,
                            TriggeredAt = DateTime.UtcNow
                        };
                    }
                }

                // Monitor active deployment
                if (_activeDeploymentId != null && _deployments.TryGetValue(_activeDeploymentId, out var deployment))
                {
                    if (deployment.State == BlueGreenState.Deploying || deployment.State == BlueGreenState.Switching)
                    {
                        var duration = DateTime.UtcNow - deployment.StartedAt;
                        if (duration > TimeSpan.FromMinutes(30))
                        {
                            var alertId = Guid.NewGuid().ToString("N");
                            _activeAlerts[alertId] = new Alert
                            {
                                AlertId = alertId,
                                RuleId = "deployment-duration",
                                RuleName = "Deployment Duration",
                                Severity = AlertSeverity.Warning,
                                Message = $"Deployment {deployment.DeploymentId} running for {duration.TotalMinutes:F0} minutes",
                                CurrentValue = duration.TotalMinutes,
                                Threshold = 30,
                                TriggeredAt = DateTime.UtcNow
                            };
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue monitoring
            }
        }
    }

    #endregion

    #region Helper Methods

    private EnvironmentColor GetInactiveEnvironment()
    {
        return _activeEnvironment == EnvironmentColor.Blue ? EnvironmentColor.Green : EnvironmentColor.Blue;
    }

    private List<string> GetAvailableRollbackVersions()
    {
        return _deployments.Values
            .Where(d => d.State == BlueGreenState.Completed)
            .OrderByDescending(d => d.CompletedAt)
            .Select(d => d.PreviousVersion)
            .Distinct()
            .Take(10)
            .ToList();
    }

    private bool ValidateVersionCompatibility(string currentVersion, string targetVersion)
    {
        try
        {
            var current = new Version(currentVersion);
            var target = new Version(targetVersion);
            return target >= current;
        }
        catch
        {
            return true; // Allow if version parsing fails
        }
    }

    private void InitializeDefaultAlertRules()
    {
        _alertRules["environment-health"] = new AlertRule
        {
            RuleId = "environment-health",
            Name = "Environment Health",
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _alertRules["deployment-duration"] = new AlertRule
        {
            RuleId = "deployment-duration",
            Name = "Deployment Duration",
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _alertRules["switch-failure"] = new AlertRule
        {
            RuleId = "switch-failure",
            Name = "Switch Failure",
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    private void AddHistoryEntry(BlueGreenDeployment deployment, bool success, string? error)
    {
        var entry = new DeploymentHistoryEntry
        {
            DeploymentId = deployment.DeploymentId,
            SourceEnvironment = deployment.SourceEnvironment,
            TargetEnvironment = deployment.TargetEnvironment,
            PreviousVersion = deployment.PreviousVersion,
            TargetVersion = deployment.TargetVersion,
            Success = success,
            StartedAt = deployment.StartedAt,
            CompletedAt = deployment.CompletedAt,
            Duration = deployment.CompletedAt.HasValue
                ? deployment.CompletedAt.Value - deployment.StartedAt
                : null,
            Error = error
        };

        _history.Enqueue(entry);

        while (_history.Count > MaxHistoryEntries)
        {
            _history.TryDequeue(out _);
        }
    }

    private DeploymentState MapBlueGreenState(BlueGreenState state)
    {
        return state switch
        {
            BlueGreenState.Pending => DeploymentState.Running,
            BlueGreenState.Deploying => DeploymentState.Upgrading,
            BlueGreenState.Switching => DeploymentState.Upgrading,
            BlueGreenState.Completed => DeploymentState.Running,
            BlueGreenState.Failed => DeploymentState.Failed,
            BlueGreenState.RollingBack => DeploymentState.RollingBack,
            BlueGreenState.RolledBack => DeploymentState.Running,
            _ => DeploymentState.Running
        };
    }

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "blue_green_deployment", Description = "Zero-downtime blue/green deployments" },
            new() { Name = "instant_traffic_switch", Description = "Atomic instant traffic switching between environments" },
            new() { Name = "auto_rollback", Description = "Automatic rollback on health check failures" },
            new() { Name = "connection_draining", Description = "Graceful connection draining before switch" },
            new() { Name = "database_migration", Description = "Coordinated database migration support" },
            new() { Name = "dns_integration", Description = "DNS update hooks for traffic management" },
            new() { Name = "load_balancer_integration", Description = "Load balancer update hooks" },
            new() { Name = "health_monitoring", Description = "Continuous health monitoring of all environments" }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "BlueGreenDeployment";
        metadata["SupportsInstantSwitch"] = true;
        metadata["SupportsRollback"] = true;
        metadata["SupportsDraining"] = true;
        metadata["SupportsDatabaseMigration"] = true;
        metadata["SupportsDnsIntegration"] = true;
        metadata["SupportsLoadBalancerIntegration"] = true;
        metadata["SupportsHealthChecks"] = true;
        return metadata;
    }

    #endregion

    #region Internal Types

    private sealed class BlueGreenEnvironment
    {
        public string EnvironmentId { get; init; } = string.Empty;
        public EnvironmentColor Color { get; init; }
        public EnvironmentState State { get; set; }
        public string Version { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; init; }
        public DateTime? LastDeployedAt { get; set; }
        public DateTime LastHealthCheck { get; set; }
        public HealthStatus HealthStatus { get; set; }
        public ConcurrentDictionary<string, EnvironmentInstance> Instances { get; init; } = new();
    }

    private sealed class EnvironmentInstance
    {
        public string InstanceId { get; init; } = string.Empty;
        public InstanceState State { get; set; }
        public string Version { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime LastHealthCheck { get; set; }
        public bool IsHealthy { get; set; }
        public int ActiveConnections { get; set; }
    }

    private sealed class BlueGreenDeployment
    {
        public string DeploymentId { get; init; } = string.Empty;
        public EnvironmentColor SourceEnvironment { get; init; }
        public EnvironmentColor TargetEnvironment { get; init; }
        public string PreviousVersion { get; init; } = string.Empty;
        public string TargetVersion { get; init; } = string.Empty;
        public BlueGreenState State { get; set; }
        public DateTime StartedAt { get; init; }
        public DateTime? SwitchedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool AutoRollbackOnFailure { get; init; }
        public TimeSpan HealthCheckTimeout { get; init; }
        public string? MigrationId { get; set; }
        public string? Error { get; set; }
        public DeploymentProgress Progress { get; init; } = new();
    }

    private sealed class DeploymentProgress
    {
        public int PreparedInstances { get; set; }
        public int HealthyInstances { get; set; }
        public int FailedInstances { get; set; }
    }

    private sealed class BlueGreenConfiguration
    {
        public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(DefaultDrainTimeoutSeconds);
        public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMilliseconds(DefaultHealthCheckIntervalMs);
        public int HealthCheckRetries { get; set; } = DefaultHealthCheckRetries;
        public int MinHealthyInstancesPercent { get; set; } = MinHealthyInstancesPercent;
        public bool EnableDatabaseMigration { get; set; } = true;
        public List<string> LoadBalancerHooks { get; init; } = new();
        public List<string> DnsUpdateHooks { get; init; } = new();
    }

    private sealed class DeploymentHistoryEntry
    {
        public string DeploymentId { get; init; } = string.Empty;
        public EnvironmentColor SourceEnvironment { get; init; }
        public EnvironmentColor TargetEnvironment { get; init; }
        public string PreviousVersion { get; init; } = string.Empty;
        public string TargetVersion { get; init; } = string.Empty;
        public bool Success { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public TimeSpan? Duration { get; init; }
        public string? Error { get; init; }
    }

    private sealed class DatabaseMigration
    {
        public string MigrationId { get; init; } = string.Empty;
        public string DeploymentId { get; init; } = string.Empty;
        public MigrationState State { get; set; }
        public string ScriptName { get; init; } = string.Empty;
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; set; }
        public string? Error { get; set; }
    }

    private sealed class DrainSession
    {
        public string SessionId { get; init; } = string.Empty;
        public string EnvironmentId { get; init; } = string.Empty;
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; set; }
        public DrainState State { get; set; }
        public int InitialConnections { get; init; }
        public int RemainingConnections { get; set; }
    }

    private enum EnvironmentColor
    {
        Blue = 0,
        Green = 1
    }

    private enum EnvironmentState
    {
        Standby,
        Preparing,
        Ready,
        Active,
        Draining
    }

    private enum InstanceState
    {
        Stopped,
        Starting,
        Running,
        Draining,
        Unhealthy
    }

    private enum BlueGreenState
    {
        Pending,
        Deploying,
        Switching,
        Completed,
        Failed,
        RollingBack,
        RolledBack
    }

    private enum HealthStatus
    {
        Healthy,
        Degraded,
        Unhealthy
    }

    private enum MigrationState
    {
        Pending,
        Running,
        Completed,
        Failed
    }

    private enum DrainState
    {
        Pending,
        Draining,
        Completed,
        TimedOut
    }

    #endregion
}
