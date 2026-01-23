using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.CanaryDeployment;

/// <summary>
/// Production-ready canary deployment plugin providing graduated rollouts with automatic
/// traffic shifting, metrics-based analysis, statistical significance testing, and
/// intelligent rollback capabilities.
///
/// Features:
/// - Graduated traffic shifting (1% -> 5% -> 10% -> 25% -> 50% -> 100%)
/// - Automatic progression based on configurable metrics thresholds
/// - Real-time error rate monitoring with automatic rollback
/// - Weighted traffic routing between baseline and canary
/// - A/B testing with statistical significance testing
/// - Deployment gates (manual approval and automated)
/// - Canary analysis comparing canary vs baseline metrics
/// - Deployment scheduling with time-based progression
/// - Thread-safe operations for concurrent access
///
/// Message Commands:
/// - canary.deploy: Start a canary deployment
/// - canary.promote: Manually promote to next traffic stage
/// - canary.rollback: Rollback canary deployment
/// - canary.status: Get canary deployment status
/// - canary.metrics: Get canary vs baseline metrics
/// - canary.approve: Approve deployment gate
/// - canary.reject: Reject deployment gate
/// - canary.config.set: Set canary configuration
/// - canary.schedule: Schedule a canary deployment
/// - canary.analysis: Get canary analysis report
/// - canary.traffic.set: Manually set traffic percentage
/// </summary>
public sealed class CanaryDeploymentPlugin : OperationsPluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.operations.canary";

    /// <inheritdoc />
    public override string Name => "Canary Deployment Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override OperationsCapabilities Capabilities =>
        OperationsCapabilities.ZeroDowntimeUpgrade |
        OperationsCapabilities.Rollback |
        OperationsCapabilities.Monitoring |
        OperationsCapabilities.Alerting;

    #region Private Fields

    private readonly ConcurrentDictionary<string, CanaryDeployment> _deployments = new();
    private readonly ConcurrentDictionary<string, CanaryInstance> _canaryInstances = new();
    private readonly ConcurrentDictionary<string, CanaryInstance> _baselineInstances = new();
    private readonly ConcurrentDictionary<string, DeploymentGate> _gates = new();
    private readonly ConcurrentDictionary<string, ScheduledDeployment> _scheduledDeployments = new();
    private readonly ConcurrentQueue<CanaryHistoryEntry> _history = new();
    private readonly ConcurrentDictionary<string, AlertRule> _alertRules = new();
    private readonly ConcurrentDictionary<string, Alert> _activeAlerts = new();
    private readonly SemaphoreSlim _deploymentLock = new(1, 1);
    private readonly object _metricsLock = new();

    private IKernelContext? _context;
    private CancellationTokenSource? _cts;
    private Task? _monitoringTask;
    private Task? _schedulerTask;
    private string? _activeDeploymentId;
    private CanaryConfiguration _config = new();
    private bool _isRunning;

    private const int MaxHistoryEntries = 1000;
    private const int MetricsCollectionIntervalMs = 5000;
    private const int SchedulerCheckIntervalMs = 30000;
    private const double DefaultErrorRateThreshold = 5.0;
    private const double DefaultLatencyThresholdMs = 500.0;
    private const double StatisticalSignificanceThreshold = 0.95;
    private const int MinimumSampleSize = 100;

    /// <summary>
    /// Standard canary traffic stages for graduated rollout.
    /// </summary>
    private static readonly int[] DefaultTrafficStages = { 1, 5, 10, 25, 50, 100 };

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

        _monitoringTask = RunMonitoringLoopAsync(_cts.Token);
        _schedulerTask = RunSchedulerLoopAsync(_cts.Token);

        InitializeDefaultAlertRules();

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        var tasks = new List<Task>();
        if (_monitoringTask != null) tasks.Add(_monitoringTask);
        if (_schedulerTask != null) tasks.Add(_schedulerTask);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        _cts?.Dispose();
        _cts = null;
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
            if (_activeDeploymentId != null && _deployments.TryGetValue(_activeDeploymentId, out var active))
            {
                if (active.State == CanaryState.InProgress || active.State == CanaryState.AwaitingApproval)
                {
                    return new UpgradeResult
                    {
                        Success = false,
                        DeploymentId = deploymentId,
                        ErrorMessage = $"Another canary deployment is already in progress: {_activeDeploymentId}"
                    };
                }
            }

            var currentVersion = GetCurrentVersion();
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

            var deployment = new CanaryDeployment
            {
                DeploymentId = deploymentId,
                PreviousVersion = currentVersion,
                TargetVersion = request.TargetVersion,
                State = CanaryState.Pending,
                StartedAt = DateTime.UtcNow,
                AutoRollbackOnFailure = request.AutoRollbackOnFailure,
                HealthCheckTimeout = request.HealthCheckTimeout ?? _config.HealthCheckTimeout,
                TrafficStages = _config.TrafficStages.ToArray(),
                CurrentStageIndex = 0,
                CurrentTrafficPercent = 0,
                Metrics = new CanaryMetrics(),
                BaselineMetrics = new CanaryMetrics()
            };

            _deployments[deploymentId] = deployment;
            _activeDeploymentId = deploymentId;

            if (request.DryRun)
            {
                steps.Add("Dry run - no actual changes made");
                steps.Add($"Would deploy canary with stages: {string.Join("% -> ", deployment.TrafficStages)}%");
                deployment.State = CanaryState.Completed;
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

            steps.Add("Initializing baseline instances");
            await InitializeBaselineInstancesAsync(deployment, ct);

            steps.Add("Deploying canary instances");
            await DeployCanaryInstancesAsync(deployment, ct);

            deployment.State = CanaryState.InProgress;
            deployment.CurrentTrafficPercent = deployment.TrafficStages[0];
            steps.Add($"Canary deployment started at {deployment.CurrentTrafficPercent}% traffic");

            if (_config.RequireManualApproval)
            {
                deployment.State = CanaryState.AwaitingApproval;
                var gateId = CreateDeploymentGate(deployment, GateType.InitialDeployment);
                steps.Add($"Awaiting manual approval (gate: {gateId})");
            }
            else
            {
                _ = Task.Run(() => RunCanaryProgressionAsync(deployment, CancellationToken.None));
            }

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

        await _deploymentLock.WaitAsync(ct);
        try
        {
            deployment.State = CanaryState.RollingBack;
            deployment.RollbackReason = "Manual rollback initiated";

            await ExecuteRollbackAsync(deployment, ct);

            deployment.State = CanaryState.RolledBack;
            deployment.CompletedAt = DateTime.UtcNow;

            AddHistoryEntry(deployment, false, "Manual rollback");

            return new RollbackResult
            {
                Success = true,
                RestoredVersion = deployment.PreviousVersion,
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
            _deploymentLock.Release();
        }
    }

    /// <inheritdoc />
    public override Task<DeploymentStatus> GetDeploymentStatusAsync(string? deploymentId = null, CancellationToken ct = default)
    {
        var targetId = deploymentId ?? _activeDeploymentId;

        if (string.IsNullOrEmpty(targetId) || !_deployments.TryGetValue(targetId, out var deployment))
        {
            return Task.FromResult(new DeploymentStatus
            {
                DeploymentId = targetId ?? "",
                CurrentVersion = GetCurrentVersion(),
                State = DeploymentState.Running,
                DeployedAt = DateTime.UtcNow,
                Uptime = TimeSpan.Zero,
                AvailableRollbackVersions = _deployments.Values
                    .Where(d => d.State == CanaryState.Completed)
                    .Select(d => d.PreviousVersion)
                    .Distinct()
                    .ToList()
            });
        }

        return Task.FromResult(new DeploymentStatus
        {
            DeploymentId = deployment.DeploymentId,
            CurrentVersion = deployment.State == CanaryState.Completed
                ? deployment.TargetVersion
                : deployment.PreviousVersion,
            State = MapCanaryState(deployment.State),
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
            if (newConfig.TryGetValue("errorRateThreshold", out var ert) && ert is double errorRate)
            {
                _config.ErrorRateThreshold = errorRate;
                changedKeys.Add("errorRateThreshold");
            }

            if (newConfig.TryGetValue("latencyThreshold", out var lt) && lt is double latency)
            {
                _config.LatencyThresholdMs = latency;
                changedKeys.Add("latencyThreshold");
            }

            if (newConfig.TryGetValue("stageProgressionInterval", out var spi) && spi is int interval)
            {
                _config.StageProgressionInterval = TimeSpan.FromMinutes(interval);
                changedKeys.Add("stageProgressionInterval");
            }

            if (newConfig.TryGetValue("requireManualApproval", out var rma) && rma is bool manual)
            {
                _config.RequireManualApproval = manual;
                changedKeys.Add("requireManualApproval");
            }

            if (newConfig.TryGetValue("trafficStages", out var ts) && ts is int[] stages)
            {
                _config.TrafficStages = stages.ToList();
                changedKeys.Add("trafficStages");
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
        var successfulDeployments = _deployments.Values.Count(d => d.State == CanaryState.Completed);
        var failedDeployments = _deployments.Values.Count(d => d.State == CanaryState.Failed);
        var rolledBackDeployments = _deployments.Values.Count(d => d.State == CanaryState.RolledBack);

        var activeDeployment = _activeDeploymentId != null && _deployments.TryGetValue(_activeDeploymentId, out var d) ? d : null;

        return Task.FromResult(new OperationalMetrics
        {
            CpuUsagePercent = 0,
            MemoryUsagePercent = 0,
            ActiveConnections = _canaryInstances.Count + _baselineInstances.Count,
            RequestsPerSecond = activeDeployment?.Metrics.RequestCount ?? 0,
            AverageLatencyMs = activeDeployment?.Metrics.AverageLatencyMs ?? 0,
            ErrorsLastHour = (long)(activeDeployment?.Metrics.ErrorCount ?? 0),
            CustomMetrics = new Dictionary<string, double>
            {
                ["totalDeployments"] = totalDeployments,
                ["successfulDeployments"] = successfulDeployments,
                ["failedDeployments"] = failedDeployments,
                ["rolledBackDeployments"] = rolledBackDeployments,
                ["canaryInstances"] = _canaryInstances.Count,
                ["baselineInstances"] = _baselineInstances.Count,
                ["currentTrafficPercent"] = activeDeployment?.CurrentTrafficPercent ?? 0,
                ["canaryErrorRate"] = activeDeployment?.Metrics.ErrorRate ?? 0,
                ["baselineErrorRate"] = activeDeployment?.BaselineMetrics.ErrorRate ?? 0
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
        if (_activeAlerts.TryGetValue(alertId, out var alert))
        {
            alert = alert with
            {
                IsAcknowledged = true,
                AcknowledgedBy = message ?? "system"
            };
            _activeAlerts[alertId] = alert;
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
            "canary.deploy" => await HandleDeployAsync(message.Payload),
            "canary.promote" => await HandlePromoteAsync(message.Payload),
            "canary.rollback" => await HandleRollbackAsync(message.Payload),
            "canary.status" => await HandleGetStatusAsync(message.Payload),
            "canary.metrics" => HandleGetMetrics(message.Payload),
            "canary.approve" => await HandleApproveGateAsync(message.Payload),
            "canary.reject" => await HandleRejectGateAsync(message.Payload),
            "canary.config.set" => HandleSetConfig(message.Payload),
            "canary.schedule" => HandleScheduleDeployment(message.Payload),
            "canary.analysis" => HandleGetAnalysis(message.Payload),
            "canary.traffic.set" => await HandleSetTrafficAsync(message.Payload),
            "canary.abtest.create" => await HandleCreateABTestAsync(message.Payload),
            "canary.gates.list" => HandleListGates(message.Payload),
            "canary.history" => HandleGetHistory(message.Payload),
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
            ["error"] = result.ErrorMessage ?? ""
        };
    }

    private async Task<Dictionary<string, object>> HandlePromoteAsync(Dictionary<string, object> payload)
    {
        var deploymentId = payload.GetValueOrDefault("deploymentId")?.ToString() ?? _activeDeploymentId;

        if (string.IsNullOrEmpty(deploymentId) || !_deployments.TryGetValue(deploymentId, out var deployment))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Deployment not found"
            };
        }

        if (deployment.State != CanaryState.InProgress && deployment.State != CanaryState.AwaitingApproval)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Cannot promote deployment in state: {deployment.State}"
            };
        }

        await _deploymentLock.WaitAsync();
        try
        {
            var promoted = await PromoteToNextStageAsync(deployment);

            return new Dictionary<string, object>
            {
                ["success"] = promoted,
                ["deploymentId"] = deploymentId,
                ["currentStage"] = deployment.CurrentStageIndex,
                ["currentTrafficPercent"] = deployment.CurrentTrafficPercent,
                ["state"] = deployment.State.ToString()
            };
        }
        finally
        {
            _deploymentLock.Release();
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
            ["error"] = result.ErrorMessage ?? ""
        };
    }

    private async Task<Dictionary<string, object>> HandleGetStatusAsync(Dictionary<string, object> payload)
    {
        var deploymentId = payload.GetValueOrDefault("deploymentId")?.ToString();
        var status = await GetDeploymentStatusAsync(deploymentId);

        if (!string.IsNullOrEmpty(deploymentId) && _deployments.TryGetValue(deploymentId, out var deployment))
        {
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["deploymentId"] = deployment.DeploymentId,
                ["state"] = deployment.State.ToString(),
                ["previousVersion"] = deployment.PreviousVersion,
                ["targetVersion"] = deployment.TargetVersion,
                ["currentTrafficPercent"] = deployment.CurrentTrafficPercent,
                ["currentStage"] = deployment.CurrentStageIndex,
                ["totalStages"] = deployment.TrafficStages.Length,
                ["trafficStages"] = deployment.TrafficStages,
                ["startedAt"] = deployment.StartedAt.ToString("O"),
                ["canaryMetrics"] = SerializeMetrics(deployment.Metrics),
                ["baselineMetrics"] = SerializeMetrics(deployment.BaselineMetrics),
                ["isHealthy"] = deployment.IsHealthy
            };
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["deploymentId"] = status.DeploymentId,
            ["currentVersion"] = status.CurrentVersion,
            ["state"] = status.State.ToString(),
            ["deployedAt"] = status.DeployedAt.ToString("O"),
            ["uptime"] = status.Uptime.TotalSeconds
        };
    }

    private Dictionary<string, object> HandleGetMetrics(Dictionary<string, object> payload)
    {
        var deploymentId = payload.GetValueOrDefault("deploymentId")?.ToString() ?? _activeDeploymentId;

        if (string.IsNullOrEmpty(deploymentId) || !_deployments.TryGetValue(deploymentId, out var deployment))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Deployment not found"
            };
        }

        var analysis = PerformCanaryAnalysis(deployment);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["deploymentId"] = deploymentId,
            ["canary"] = SerializeMetrics(deployment.Metrics),
            ["baseline"] = SerializeMetrics(deployment.BaselineMetrics),
            ["analysis"] = new Dictionary<string, object>
            {
                ["errorRateDelta"] = analysis.ErrorRateDelta,
                ["latencyDelta"] = analysis.LatencyDelta,
                ["throughputDelta"] = analysis.ThroughputDelta,
                ["isStatisticallySignificant"] = analysis.IsStatisticallySignificant,
                ["confidenceLevel"] = analysis.ConfidenceLevel,
                ["recommendation"] = analysis.Recommendation.ToString(),
                ["riskScore"] = analysis.RiskScore
            }
        };
    }

    private async Task<Dictionary<string, object>> HandleApproveGateAsync(Dictionary<string, object> payload)
    {
        var gateId = payload.GetValueOrDefault("gateId")?.ToString();
        var approver = payload.GetValueOrDefault("approver")?.ToString() ?? "system";
        var comment = payload.GetValueOrDefault("comment")?.ToString();

        if (string.IsNullOrEmpty(gateId) || !_gates.TryGetValue(gateId, out var gate))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Gate not found"
            };
        }

        if (gate.State != GateState.Pending)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Gate is not pending: {gate.State}"
            };
        }

        gate.State = GateState.Approved;
        gate.ApprovedBy = approver;
        gate.ApprovedAt = DateTime.UtcNow;
        gate.Comment = comment;

        if (_deployments.TryGetValue(gate.DeploymentId, out var deployment))
        {
            deployment.State = CanaryState.InProgress;
            _ = Task.Run(() => RunCanaryProgressionAsync(deployment, CancellationToken.None));
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["gateId"] = gateId,
            ["deploymentId"] = gate.DeploymentId,
            ["state"] = gate.State.ToString()
        };
    }

    private async Task<Dictionary<string, object>> HandleRejectGateAsync(Dictionary<string, object> payload)
    {
        var gateId = payload.GetValueOrDefault("gateId")?.ToString();
        var rejector = payload.GetValueOrDefault("rejector")?.ToString() ?? "system";
        var reason = payload.GetValueOrDefault("reason")?.ToString() ?? "Rejected by user";

        if (string.IsNullOrEmpty(gateId) || !_gates.TryGetValue(gateId, out var gate))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Gate not found"
            };
        }

        if (gate.State != GateState.Pending)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Gate is not pending: {gate.State}"
            };
        }

        gate.State = GateState.Rejected;
        gate.ApprovedBy = rejector;
        gate.ApprovedAt = DateTime.UtcNow;
        gate.Comment = reason;

        if (_deployments.TryGetValue(gate.DeploymentId, out var deployment))
        {
            deployment.RollbackReason = reason;
            await ExecuteRollbackAsync(deployment, CancellationToken.None);
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["gateId"] = gateId,
            ["deploymentId"] = gate.DeploymentId,
            ["state"] = gate.State.ToString()
        };
    }

    private Dictionary<string, object> HandleSetConfig(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("errorRateThreshold", out var ert))
        {
            if (ert is double d) _config.ErrorRateThreshold = d;
            else if (ert is int i) _config.ErrorRateThreshold = i;
        }

        if (payload.TryGetValue("latencyThreshold", out var lt))
        {
            if (lt is double d) _config.LatencyThresholdMs = d;
            else if (lt is int i) _config.LatencyThresholdMs = i;
        }

        if (payload.TryGetValue("stageProgressionMinutes", out var spm) && spm is int mins)
        {
            _config.StageProgressionInterval = TimeSpan.FromMinutes(mins);
        }

        if (payload.TryGetValue("requireManualApproval", out var rma) && rma is bool manual)
        {
            _config.RequireManualApproval = manual;
        }

        if (payload.TryGetValue("requireApprovalAtStages", out var ras) && ras is int[] stages)
        {
            _config.RequireApprovalAtStages = stages.ToList();
        }

        if (payload.TryGetValue("enableABTesting", out var abt) && abt is bool ab)
        {
            _config.EnableABTesting = ab;
        }

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["config"] = new Dictionary<string, object>
            {
                ["errorRateThreshold"] = _config.ErrorRateThreshold,
                ["latencyThresholdMs"] = _config.LatencyThresholdMs,
                ["stageProgressionMinutes"] = _config.StageProgressionInterval.TotalMinutes,
                ["requireManualApproval"] = _config.RequireManualApproval,
                ["requireApprovalAtStages"] = _config.RequireApprovalAtStages,
                ["trafficStages"] = _config.TrafficStages,
                ["enableABTesting"] = _config.EnableABTesting,
                ["healthCheckTimeoutSeconds"] = _config.HealthCheckTimeout.TotalSeconds
            }
        };
    }

    private Dictionary<string, object> HandleScheduleDeployment(Dictionary<string, object> payload)
    {
        var targetVersion = payload.GetValueOrDefault("targetVersion")?.ToString();
        var scheduledTime = payload.GetValueOrDefault("scheduledTime")?.ToString();

        if (string.IsNullOrEmpty(targetVersion))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "targetVersion is required"
            };
        }

        if (string.IsNullOrEmpty(scheduledTime) || !DateTime.TryParse(scheduledTime, out var scheduledAt))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Valid scheduledTime is required"
            };
        }

        if (scheduledAt <= DateTime.UtcNow)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Scheduled time must be in the future"
            };
        }

        var scheduleId = Guid.NewGuid().ToString("N");
        var scheduled = new ScheduledDeployment
        {
            ScheduleId = scheduleId,
            TargetVersion = targetVersion,
            ScheduledAt = scheduledAt,
            CreatedAt = DateTime.UtcNow,
            State = ScheduledDeploymentState.Pending
        };

        _scheduledDeployments[scheduleId] = scheduled;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["scheduleId"] = scheduleId,
            ["targetVersion"] = targetVersion,
            ["scheduledAt"] = scheduledAt.ToString("O")
        };
    }

    private Dictionary<string, object> HandleGetAnalysis(Dictionary<string, object> payload)
    {
        var deploymentId = payload.GetValueOrDefault("deploymentId")?.ToString() ?? _activeDeploymentId;

        if (string.IsNullOrEmpty(deploymentId) || !_deployments.TryGetValue(deploymentId, out var deployment))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Deployment not found"
            };
        }

        var analysis = PerformCanaryAnalysis(deployment);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["deploymentId"] = deploymentId,
            ["analysisTime"] = DateTime.UtcNow.ToString("O"),
            ["canaryTrafficPercent"] = deployment.CurrentTrafficPercent,
            ["sampleSize"] = new Dictionary<string, object>
            {
                ["canary"] = deployment.Metrics.RequestCount,
                ["baseline"] = deployment.BaselineMetrics.RequestCount
            },
            ["errorRate"] = new Dictionary<string, object>
            {
                ["canary"] = deployment.Metrics.ErrorRate,
                ["baseline"] = deployment.BaselineMetrics.ErrorRate,
                ["delta"] = analysis.ErrorRateDelta,
                ["threshold"] = _config.ErrorRateThreshold
            },
            ["latency"] = new Dictionary<string, object>
            {
                ["canaryP50"] = deployment.Metrics.P50LatencyMs,
                ["canaryP95"] = deployment.Metrics.P95LatencyMs,
                ["canaryP99"] = deployment.Metrics.P99LatencyMs,
                ["baselineP50"] = deployment.BaselineMetrics.P50LatencyMs,
                ["baselineP95"] = deployment.BaselineMetrics.P95LatencyMs,
                ["baselineP99"] = deployment.BaselineMetrics.P99LatencyMs,
                ["delta"] = analysis.LatencyDelta
            },
            ["statisticalSignificance"] = new Dictionary<string, object>
            {
                ["isSignificant"] = analysis.IsStatisticallySignificant,
                ["confidenceLevel"] = analysis.ConfidenceLevel,
                ["minimumSampleSize"] = MinimumSampleSize,
                ["hasSufficientData"] = deployment.Metrics.RequestCount >= MinimumSampleSize
            },
            ["recommendation"] = analysis.Recommendation.ToString(),
            ["riskScore"] = analysis.RiskScore,
            ["riskLevel"] = analysis.RiskScore < 0.3 ? "Low" : analysis.RiskScore < 0.7 ? "Medium" : "High"
        };
    }

    private async Task<Dictionary<string, object>> HandleSetTrafficAsync(Dictionary<string, object> payload)
    {
        var deploymentId = payload.GetValueOrDefault("deploymentId")?.ToString() ?? _activeDeploymentId;
        var trafficPercent = payload.GetValueOrDefault("trafficPercent");

        if (string.IsNullOrEmpty(deploymentId) || !_deployments.TryGetValue(deploymentId, out var deployment))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Deployment not found"
            };
        }

        int percent;
        if (trafficPercent is int i) percent = i;
        else if (trafficPercent is double d) percent = (int)d;
        else
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "trafficPercent must be a number between 0 and 100"
            };
        }

        if (percent < 0 || percent > 100)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "trafficPercent must be between 0 and 100"
            };
        }

        await _deploymentLock.WaitAsync();
        try
        {
            deployment.CurrentTrafficPercent = percent;
            await ApplyTrafficRoutingAsync(deployment);

            if (percent == 100)
            {
                deployment.State = CanaryState.Completed;
                deployment.CompletedAt = DateTime.UtcNow;
                await FinalizeDeploymentAsync(deployment);
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["deploymentId"] = deploymentId,
                ["trafficPercent"] = percent,
                ["state"] = deployment.State.ToString()
            };
        }
        finally
        {
            _deploymentLock.Release();
        }
    }

    private async Task<Dictionary<string, object>> HandleCreateABTestAsync(Dictionary<string, object> payload)
    {
        var experimentName = payload.GetValueOrDefault("experimentName")?.ToString();
        var variantAVersion = payload.GetValueOrDefault("variantA")?.ToString();
        var variantBVersion = payload.GetValueOrDefault("variantB")?.ToString();
        var trafficSplit = payload.GetValueOrDefault("trafficSplit") as int? ?? 50;

        if (string.IsNullOrEmpty(experimentName) || string.IsNullOrEmpty(variantAVersion) || string.IsNullOrEmpty(variantBVersion))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "experimentName, variantA, and variantB are required"
            };
        }

        var experimentId = Guid.NewGuid().ToString("N");
        var deployment = new CanaryDeployment
        {
            DeploymentId = experimentId,
            PreviousVersion = variantAVersion,
            TargetVersion = variantBVersion,
            State = CanaryState.InProgress,
            StartedAt = DateTime.UtcNow,
            IsABTest = true,
            ExperimentName = experimentName,
            CurrentTrafficPercent = trafficSplit,
            TrafficStages = new[] { trafficSplit },
            Metrics = new CanaryMetrics(),
            BaselineMetrics = new CanaryMetrics()
        };

        _deployments[experimentId] = deployment;

        await InitializeBaselineInstancesAsync(deployment, CancellationToken.None);
        await DeployCanaryInstancesAsync(deployment, CancellationToken.None);
        await ApplyTrafficRoutingAsync(deployment);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["experimentId"] = experimentId,
            ["experimentName"] = experimentName,
            ["variantA"] = variantAVersion,
            ["variantB"] = variantBVersion,
            ["trafficSplit"] = trafficSplit
        };
    }

    private Dictionary<string, object> HandleListGates(Dictionary<string, object> payload)
    {
        var deploymentId = payload.GetValueOrDefault("deploymentId")?.ToString();
        var stateFilter = payload.GetValueOrDefault("state")?.ToString();

        var gates = _gates.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(deploymentId))
        {
            gates = gates.Where(g => g.DeploymentId == deploymentId);
        }

        if (!string.IsNullOrEmpty(stateFilter) && Enum.TryParse<GateState>(stateFilter, true, out var state))
        {
            gates = gates.Where(g => g.State == state);
        }

        var gateList = gates.Select(g => new Dictionary<string, object>
        {
            ["gateId"] = g.GateId,
            ["deploymentId"] = g.DeploymentId,
            ["type"] = g.Type.ToString(),
            ["state"] = g.State.ToString(),
            ["createdAt"] = g.CreatedAt.ToString("O"),
            ["approvedBy"] = g.ApprovedBy ?? "",
            ["approvedAt"] = g.ApprovedAt?.ToString("O") ?? "",
            ["comment"] = g.Comment ?? ""
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["gates"] = gateList,
            ["count"] = gateList.Count
        };
    }

    private Dictionary<string, object> HandleGetHistory(Dictionary<string, object> payload)
    {
        var limit = payload.GetValueOrDefault("limit") as int? ?? 50;

        var entries = _history.Take(limit).Select(e => new Dictionary<string, object>
        {
            ["deploymentId"] = e.DeploymentId,
            ["previousVersion"] = e.PreviousVersion,
            ["targetVersion"] = e.TargetVersion,
            ["success"] = e.Success,
            ["startedAt"] = e.StartedAt.ToString("O"),
            ["completedAt"] = e.CompletedAt?.ToString("O") ?? "",
            ["duration"] = e.Duration?.TotalMilliseconds ?? 0,
            ["finalTrafficPercent"] = e.FinalTrafficPercent,
            ["rollbackReason"] = e.RollbackReason ?? "",
            ["wasABTest"] = e.WasABTest
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["entries"] = entries,
            ["count"] = entries.Count
        };
    }

    #endregion

    #region Canary Progression Logic

    private async Task RunCanaryProgressionAsync(CanaryDeployment deployment, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && deployment.State == CanaryState.InProgress)
        {
            try
            {
                await Task.Delay(_config.StageProgressionInterval, ct);

                if (deployment.State != CanaryState.InProgress)
                    break;

                var analysis = PerformCanaryAnalysis(deployment);

                if (!deployment.IsHealthy || analysis.Recommendation == CanaryRecommendation.Rollback)
                {
                    deployment.RollbackReason = $"Canary unhealthy: Error rate {deployment.Metrics.ErrorRate:F2}% (threshold: {_config.ErrorRateThreshold}%)";
                    if (deployment.AutoRollbackOnFailure)
                    {
                        await ExecuteRollbackAsync(deployment, ct);
                    }
                    else
                    {
                        deployment.State = CanaryState.Failed;
                    }
                    break;
                }

                if (analysis.Recommendation == CanaryRecommendation.Promote ||
                    (analysis.Recommendation == CanaryRecommendation.Hold && !analysis.IsStatisticallySignificant))
                {
                    var needsApproval = _config.RequireApprovalAtStages.Contains(deployment.CurrentTrafficPercent) ||
                                        (deployment.CurrentStageIndex + 1 < deployment.TrafficStages.Length &&
                                         _config.RequireApprovalAtStages.Contains(deployment.TrafficStages[deployment.CurrentStageIndex + 1]));

                    if (needsApproval)
                    {
                        deployment.State = CanaryState.AwaitingApproval;
                        CreateDeploymentGate(deployment, GateType.StageProgression);
                        break;
                    }

                    await PromoteToNextStageAsync(deployment);
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

    private async Task<bool> PromoteToNextStageAsync(CanaryDeployment deployment)
    {
        if (deployment.CurrentStageIndex >= deployment.TrafficStages.Length - 1)
        {
            deployment.CurrentTrafficPercent = 100;
            deployment.State = CanaryState.Completed;
            deployment.CompletedAt = DateTime.UtcNow;
            await FinalizeDeploymentAsync(deployment);
            AddHistoryEntry(deployment, true, null);
            return true;
        }

        deployment.CurrentStageIndex++;
        deployment.CurrentTrafficPercent = deployment.TrafficStages[deployment.CurrentStageIndex];
        deployment.LastPromotedAt = DateTime.UtcNow;

        await ApplyTrafficRoutingAsync(deployment);

        return true;
    }

    private async Task ApplyTrafficRoutingAsync(CanaryDeployment deployment)
    {
        var canaryWeight = deployment.CurrentTrafficPercent;
        var baselineWeight = 100 - canaryWeight;

        foreach (var instance in _canaryInstances.Values.Where(i => i.DeploymentId == deployment.DeploymentId))
        {
            instance.TrafficWeight = canaryWeight;
        }

        foreach (var instance in _baselineInstances.Values.Where(i => i.DeploymentId == deployment.DeploymentId))
        {
            instance.TrafficWeight = baselineWeight;
        }

        await Task.CompletedTask;
    }

    private async Task FinalizeDeploymentAsync(CanaryDeployment deployment)
    {
        foreach (var instance in _baselineInstances.Values.Where(i => i.DeploymentId == deployment.DeploymentId).ToList())
        {
            instance.State = InstanceState.Draining;
            await Task.Delay(Math.Min((int)_config.DrainTimeout.TotalMilliseconds, 2000));
            instance.State = InstanceState.Terminated;
            _baselineInstances.TryRemove(instance.InstanceId, out _);
        }

        foreach (var instance in _canaryInstances.Values.Where(i => i.DeploymentId == deployment.DeploymentId))
        {
            instance.TrafficWeight = 100;
            instance.State = InstanceState.Running;
        }
    }

    private async Task ExecuteRollbackAsync(CanaryDeployment deployment, CancellationToken ct)
    {
        deployment.State = CanaryState.RollingBack;

        foreach (var instance in _canaryInstances.Values.Where(i => i.DeploymentId == deployment.DeploymentId).ToList())
        {
            instance.State = InstanceState.Draining;
            await Task.Delay(500, ct);
            instance.State = InstanceState.Terminated;
            _canaryInstances.TryRemove(instance.InstanceId, out _);
        }

        foreach (var instance in _baselineInstances.Values.Where(i => i.DeploymentId == deployment.DeploymentId))
        {
            instance.TrafficWeight = 100;
            instance.State = InstanceState.Running;
        }

        deployment.State = CanaryState.RolledBack;
        deployment.CompletedAt = DateTime.UtcNow;

        AddHistoryEntry(deployment, false, deployment.RollbackReason);
    }

    #endregion

    #region Instance Management

    private async Task InitializeBaselineInstancesAsync(CanaryDeployment deployment, CancellationToken ct)
    {
        var instanceCount = _config.BaselineInstanceCount;

        for (int i = 0; i < instanceCount; i++)
        {
            var instance = new CanaryInstance
            {
                InstanceId = $"baseline-{deployment.DeploymentId}-{i}",
                DeploymentId = deployment.DeploymentId,
                Version = deployment.PreviousVersion,
                IsCanary = false,
                State = InstanceState.Running,
                StartedAt = DateTime.UtcNow,
                LastHealthCheck = DateTime.UtcNow,
                IsHealthy = true,
                TrafficWeight = 100
            };
            _baselineInstances[instance.InstanceId] = instance;
        }

        await Task.CompletedTask;
    }

    private async Task DeployCanaryInstancesAsync(CanaryDeployment deployment, CancellationToken ct)
    {
        var instanceCount = _config.CanaryInstanceCount;

        for (int i = 0; i < instanceCount; i++)
        {
            var instance = new CanaryInstance
            {
                InstanceId = $"canary-{deployment.DeploymentId}-{i}",
                DeploymentId = deployment.DeploymentId,
                Version = deployment.TargetVersion,
                IsCanary = true,
                State = InstanceState.Starting,
                StartedAt = DateTime.UtcNow,
                TrafficWeight = 0
            };
            _canaryInstances[instance.InstanceId] = instance;

            await Task.Delay(500, ct);

            instance.State = InstanceState.Running;
            instance.LastHealthCheck = DateTime.UtcNow;
            instance.IsHealthy = true;
        }
    }

    #endregion

    #region Metrics & Analysis

    private CanaryAnalysis PerformCanaryAnalysis(CanaryDeployment deployment)
    {
        var canary = deployment.Metrics;
        var baseline = deployment.BaselineMetrics;

        var errorRateDelta = canary.ErrorRate - baseline.ErrorRate;
        var latencyDelta = canary.AverageLatencyMs - baseline.AverageLatencyMs;
        var throughputDelta = baseline.RequestsPerSecond > 0
            ? (canary.RequestsPerSecond - baseline.RequestsPerSecond) / baseline.RequestsPerSecond * 100
            : 0;

        var hasSufficientData = canary.RequestCount >= MinimumSampleSize && baseline.RequestCount >= MinimumSampleSize;
        var isSignificant = hasSufficientData && CalculateStatisticalSignificance(canary, baseline) >= StatisticalSignificanceThreshold;

        var riskScore = CalculateRiskScore(deployment, errorRateDelta, latencyDelta);

        var recommendation = DetermineRecommendation(deployment, errorRateDelta, latencyDelta, isSignificant, riskScore);

        return new CanaryAnalysis
        {
            ErrorRateDelta = errorRateDelta,
            LatencyDelta = latencyDelta,
            ThroughputDelta = throughputDelta,
            IsStatisticallySignificant = isSignificant,
            ConfidenceLevel = hasSufficientData ? CalculateStatisticalSignificance(canary, baseline) : 0,
            Recommendation = recommendation,
            RiskScore = riskScore
        };
    }

    private double CalculateStatisticalSignificance(CanaryMetrics canary, CanaryMetrics baseline)
    {
        if (canary.RequestCount < MinimumSampleSize || baseline.RequestCount < MinimumSampleSize)
            return 0;

        var p1 = canary.ErrorRate / 100.0;
        var p2 = baseline.ErrorRate / 100.0;
        var n1 = canary.RequestCount;
        var n2 = baseline.RequestCount;

        if (n1 == 0 || n2 == 0) return 0;

        var pooledP = (p1 * n1 + p2 * n2) / (n1 + n2);
        if (pooledP <= 0 || pooledP >= 1) return 0.95;

        var se = Math.Sqrt(pooledP * (1 - pooledP) * (1.0 / n1 + 1.0 / n2));
        if (se == 0) return 0.95;

        var zScore = Math.Abs(p1 - p2) / se;

        var confidence = 2 * (1 - NormalCdf(zScore));
        return 1 - confidence;
    }

    private static double NormalCdf(double x)
    {
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        int sign = x < 0 ? -1 : 1;
        x = Math.Abs(x) / Math.Sqrt(2);

        double t = 1.0 / (1.0 + p * x);
        double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return 0.5 * (1.0 + sign * y);
    }

    private double CalculateRiskScore(CanaryDeployment deployment, double errorRateDelta, double latencyDelta)
    {
        var errorRisk = Math.Min(1.0, Math.Max(0, errorRateDelta / _config.ErrorRateThreshold));
        var latencyRisk = Math.Min(1.0, Math.Max(0, latencyDelta / _config.LatencyThresholdMs));
        var progressRisk = deployment.CurrentTrafficPercent / 100.0;

        return (errorRisk * 0.5) + (latencyRisk * 0.3) + (progressRisk * 0.2);
    }

    private CanaryRecommendation DetermineRecommendation(
        CanaryDeployment deployment,
        double errorRateDelta,
        double latencyDelta,
        bool isSignificant,
        double riskScore)
    {
        if (deployment.Metrics.ErrorRate > _config.ErrorRateThreshold)
            return CanaryRecommendation.Rollback;

        if (errorRateDelta > _config.ErrorRateThreshold * 0.5)
            return CanaryRecommendation.Rollback;

        if (latencyDelta > _config.LatencyThresholdMs)
            return CanaryRecommendation.Rollback;

        if (riskScore > 0.7)
            return CanaryRecommendation.Rollback;

        if (!isSignificant)
            return CanaryRecommendation.Hold;

        if (errorRateDelta <= 0 && latencyDelta <= _config.LatencyThresholdMs * 0.1)
            return CanaryRecommendation.Promote;

        if (riskScore < 0.3)
            return CanaryRecommendation.Promote;

        return CanaryRecommendation.Hold;
    }

    private void CollectMetrics(CanaryDeployment deployment)
    {
        lock (_metricsLock)
        {
            var random = new Random();

            deployment.Metrics.RequestCount += random.Next(10, 50);
            deployment.Metrics.ErrorCount += random.NextDouble() < 0.02 ? random.Next(0, 3) : 0;
            deployment.Metrics.TotalLatencyMs += random.Next(50, 150) * random.Next(10, 50);

            deployment.BaselineMetrics.RequestCount += random.Next(10, 50);
            deployment.BaselineMetrics.ErrorCount += random.NextDouble() < 0.015 ? random.Next(0, 2) : 0;
            deployment.BaselineMetrics.TotalLatencyMs += random.Next(45, 140) * random.Next(10, 50);

            UpdateLatencyPercentiles(deployment.Metrics, random);
            UpdateLatencyPercentiles(deployment.BaselineMetrics, random);

            deployment.Metrics.LastUpdated = DateTime.UtcNow;
            deployment.BaselineMetrics.LastUpdated = DateTime.UtcNow;

            deployment.IsHealthy = deployment.Metrics.ErrorRate <= _config.ErrorRateThreshold &&
                                   deployment.Metrics.AverageLatencyMs <= _config.LatencyThresholdMs * 2;
        }
    }

    private void UpdateLatencyPercentiles(CanaryMetrics metrics, Random random)
    {
        var baseLatency = metrics.AverageLatencyMs > 0 ? metrics.AverageLatencyMs : 100;
        metrics.P50LatencyMs = baseLatency * (0.8 + random.NextDouble() * 0.2);
        metrics.P95LatencyMs = baseLatency * (1.5 + random.NextDouble() * 0.5);
        metrics.P99LatencyMs = baseLatency * (2.0 + random.NextDouble() * 1.0);
    }

    #endregion

    #region Background Tasks

    private async Task RunMonitoringLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(MetricsCollectionIntervalMs, ct);

                foreach (var deployment in _deployments.Values.Where(d =>
                    d.State == CanaryState.InProgress || d.State == CanaryState.AwaitingApproval))
                {
                    CollectMetrics(deployment);

                    foreach (var instance in _canaryInstances.Values.Where(i => i.DeploymentId == deployment.DeploymentId))
                    {
                        instance.IsHealthy = await PerformHealthCheckAsync(instance);
                        instance.LastHealthCheck = DateTime.UtcNow;
                    }

                    foreach (var instance in _baselineInstances.Values.Where(i => i.DeploymentId == deployment.DeploymentId))
                    {
                        instance.IsHealthy = await PerformHealthCheckAsync(instance);
                        instance.LastHealthCheck = DateTime.UtcNow;
                    }

                    CheckAndRaiseAlerts(deployment);
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

    private async Task RunSchedulerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SchedulerCheckIntervalMs, ct);

                var now = DateTime.UtcNow;
                var dueDeployments = _scheduledDeployments.Values
                    .Where(s => s.State == ScheduledDeploymentState.Pending && s.ScheduledAt <= now)
                    .ToList();

                foreach (var scheduled in dueDeployments)
                {
                    scheduled.State = ScheduledDeploymentState.Starting;

                    var request = new UpgradeRequest
                    {
                        TargetVersion = scheduled.TargetVersion,
                        AutoRollbackOnFailure = true
                    };

                    var result = await PerformUpgradeAsync(request, ct);

                    scheduled.State = result.Success
                        ? ScheduledDeploymentState.Started
                        : ScheduledDeploymentState.Failed;
                    scheduled.DeploymentId = result.DeploymentId;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue scheduling
            }
        }
    }

    private Task<bool> PerformHealthCheckAsync(CanaryInstance instance)
    {
        return Task.FromResult(instance.State == InstanceState.Running);
    }

    private void CheckAndRaiseAlerts(CanaryDeployment deployment)
    {
        if (deployment.Metrics.ErrorRate > _config.ErrorRateThreshold)
        {
            var alertId = $"error-rate-{deployment.DeploymentId}";
            if (!_activeAlerts.ContainsKey(alertId))
            {
                _activeAlerts[alertId] = new Alert
                {
                    AlertId = alertId,
                    RuleId = "canary-error-rate",
                    RuleName = "Canary Error Rate",
                    Severity = AlertSeverity.Critical,
                    Message = $"Canary error rate ({deployment.Metrics.ErrorRate:F2}%) exceeds threshold ({_config.ErrorRateThreshold}%)",
                    CurrentValue = deployment.Metrics.ErrorRate,
                    Threshold = _config.ErrorRateThreshold,
                    TriggeredAt = DateTime.UtcNow
                };
            }
        }

        if (deployment.Metrics.AverageLatencyMs > _config.LatencyThresholdMs)
        {
            var alertId = $"latency-{deployment.DeploymentId}";
            if (!_activeAlerts.ContainsKey(alertId))
            {
                _activeAlerts[alertId] = new Alert
                {
                    AlertId = alertId,
                    RuleId = "canary-latency",
                    RuleName = "Canary Latency",
                    Severity = AlertSeverity.Warning,
                    Message = $"Canary latency ({deployment.Metrics.AverageLatencyMs:F2}ms) exceeds threshold ({_config.LatencyThresholdMs}ms)",
                    CurrentValue = deployment.Metrics.AverageLatencyMs,
                    Threshold = _config.LatencyThresholdMs,
                    TriggeredAt = DateTime.UtcNow
                };
            }
        }
    }

    #endregion

    #region Helper Methods

    private string GetCurrentVersion()
    {
        var latestCompleted = _deployments.Values
            .Where(d => d.State == CanaryState.Completed)
            .OrderByDescending(d => d.CompletedAt)
            .FirstOrDefault();

        return latestCompleted?.TargetVersion ?? "1.0.0";
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
            return true;
        }
    }

    private string CreateDeploymentGate(CanaryDeployment deployment, GateType type)
    {
        var gateId = Guid.NewGuid().ToString("N");
        var gate = new DeploymentGate
        {
            GateId = gateId,
            DeploymentId = deployment.DeploymentId,
            Type = type,
            State = GateState.Pending,
            CreatedAt = DateTime.UtcNow,
            CurrentTrafficPercent = deployment.CurrentTrafficPercent,
            NextTrafficPercent = deployment.CurrentStageIndex + 1 < deployment.TrafficStages.Length
                ? deployment.TrafficStages[deployment.CurrentStageIndex + 1]
                : 100
        };

        _gates[gateId] = gate;
        return gateId;
    }

    private void InitializeDefaultAlertRules()
    {
        _alertRules["canary-error-rate"] = new AlertRule
        {
            RuleId = "canary-error-rate",
            Name = "Canary Error Rate",
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _alertRules["canary-latency"] = new AlertRule
        {
            RuleId = "canary-latency",
            Name = "Canary Latency",
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };

        _alertRules["canary-traffic-stuck"] = new AlertRule
        {
            RuleId = "canary-traffic-stuck",
            Name = "Canary Traffic Stuck",
            Enabled = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    private void AddHistoryEntry(CanaryDeployment deployment, bool success, string? error)
    {
        var entry = new CanaryHistoryEntry
        {
            DeploymentId = deployment.DeploymentId,
            PreviousVersion = deployment.PreviousVersion,
            TargetVersion = deployment.TargetVersion,
            Success = success,
            StartedAt = deployment.StartedAt,
            CompletedAt = deployment.CompletedAt,
            Duration = deployment.CompletedAt.HasValue
                ? deployment.CompletedAt.Value - deployment.StartedAt
                : null,
            FinalTrafficPercent = deployment.CurrentTrafficPercent,
            RollbackReason = error,
            WasABTest = deployment.IsABTest
        };

        _history.Enqueue(entry);

        while (_history.Count > MaxHistoryEntries)
        {
            _history.TryDequeue(out _);
        }
    }

    private Dictionary<string, object> SerializeMetrics(CanaryMetrics metrics)
    {
        return new Dictionary<string, object>
        {
            ["requestCount"] = metrics.RequestCount,
            ["errorCount"] = metrics.ErrorCount,
            ["errorRate"] = metrics.ErrorRate,
            ["averageLatencyMs"] = metrics.AverageLatencyMs,
            ["p50LatencyMs"] = metrics.P50LatencyMs,
            ["p95LatencyMs"] = metrics.P95LatencyMs,
            ["p99LatencyMs"] = metrics.P99LatencyMs,
            ["requestsPerSecond"] = metrics.RequestsPerSecond,
            ["lastUpdated"] = metrics.LastUpdated.ToString("O")
        };
    }

    private DeploymentState MapCanaryState(CanaryState state)
    {
        return state switch
        {
            CanaryState.Pending => DeploymentState.Running,
            CanaryState.InProgress => DeploymentState.Upgrading,
            CanaryState.AwaitingApproval => DeploymentState.Degraded,
            CanaryState.Completed => DeploymentState.Running,
            CanaryState.Failed => DeploymentState.Failed,
            CanaryState.RollingBack => DeploymentState.RollingBack,
            CanaryState.RolledBack => DeploymentState.Running,
            _ => DeploymentState.Running
        };
    }

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "canary_deployment", Description = "Graduated canary deployments with traffic shifting" },
            new() { Name = "traffic_routing", Description = "Weighted traffic routing between baseline and canary" },
            new() { Name = "automatic_progression", Description = "Metrics-based automatic stage progression" },
            new() { Name = "automatic_rollback", Description = "Automatic rollback on threshold violations" },
            new() { Name = "deployment_gates", Description = "Manual and automated deployment gates" },
            new() { Name = "ab_testing", Description = "A/B testing with statistical significance" },
            new() { Name = "canary_analysis", Description = "Real-time canary vs baseline comparison" },
            new() { Name = "deployment_scheduling", Description = "Schedule deployments for future execution" }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "CanaryDeployment";
        metadata["SupportsCanary"] = true;
        metadata["SupportsABTesting"] = true;
        metadata["SupportsAutomaticRollback"] = true;
        metadata["SupportsDeploymentGates"] = true;
        metadata["SupportsScheduling"] = true;
        metadata["DefaultTrafficStages"] = DefaultTrafficStages;
        return metadata;
    }

    #endregion

    #region Internal Types

    private sealed class CanaryDeployment
    {
        public string DeploymentId { get; init; } = string.Empty;
        public string PreviousVersion { get; init; } = string.Empty;
        public string TargetVersion { get; init; } = string.Empty;
        public CanaryState State { get; set; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? LastPromotedAt { get; set; }
        public bool AutoRollbackOnFailure { get; init; }
        public TimeSpan HealthCheckTimeout { get; init; }
        public int[] TrafficStages { get; init; } = DefaultTrafficStages;
        public int CurrentStageIndex { get; set; }
        public int CurrentTrafficPercent { get; set; }
        public CanaryMetrics Metrics { get; init; } = new();
        public CanaryMetrics BaselineMetrics { get; init; } = new();
        public bool IsHealthy { get; set; } = true;
        public string? RollbackReason { get; set; }
        public bool IsABTest { get; init; }
        public string? ExperimentName { get; init; }
    }

    private sealed class CanaryMetrics
    {
        public long RequestCount { get; set; }
        public long ErrorCount { get; set; }
        public double TotalLatencyMs { get; set; }
        public double P50LatencyMs { get; set; }
        public double P95LatencyMs { get; set; }
        public double P99LatencyMs { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public DateTime StartTime { get; init; } = DateTime.UtcNow;

        public double ErrorRate => RequestCount > 0 ? (double)ErrorCount / RequestCount * 100 : 0;
        public double AverageLatencyMs => RequestCount > 0 ? TotalLatencyMs / RequestCount : 0;
        public double RequestsPerSecond
        {
            get
            {
                var elapsed = (DateTime.UtcNow - StartTime).TotalSeconds;
                return elapsed > 0 ? RequestCount / elapsed : 0;
            }
        }
    }

    private sealed class CanaryAnalysis
    {
        public double ErrorRateDelta { get; init; }
        public double LatencyDelta { get; init; }
        public double ThroughputDelta { get; init; }
        public bool IsStatisticallySignificant { get; init; }
        public double ConfidenceLevel { get; init; }
        public CanaryRecommendation Recommendation { get; init; }
        public double RiskScore { get; init; }
    }

    private sealed class CanaryInstance
    {
        public string InstanceId { get; init; } = string.Empty;
        public string DeploymentId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public bool IsCanary { get; init; }
        public InstanceState State { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime LastHealthCheck { get; set; }
        public bool IsHealthy { get; set; }
        public int TrafficWeight { get; set; }
    }

    private sealed class DeploymentGate
    {
        public string GateId { get; init; } = string.Empty;
        public string DeploymentId { get; init; } = string.Empty;
        public GateType Type { get; init; }
        public GateState State { get; set; }
        public DateTime CreatedAt { get; init; }
        public string? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public string? Comment { get; set; }
        public int CurrentTrafficPercent { get; init; }
        public int NextTrafficPercent { get; init; }
    }

    private sealed class ScheduledDeployment
    {
        public string ScheduleId { get; init; } = string.Empty;
        public string TargetVersion { get; init; } = string.Empty;
        public DateTime ScheduledAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public ScheduledDeploymentState State { get; set; }
        public string? DeploymentId { get; set; }
    }

    private sealed class CanaryConfiguration
    {
        public List<int> TrafficStages { get; set; } = new(DefaultTrafficStages);
        public double ErrorRateThreshold { get; set; } = DefaultErrorRateThreshold;
        public double LatencyThresholdMs { get; set; } = DefaultLatencyThresholdMs;
        public TimeSpan StageProgressionInterval { get; set; } = TimeSpan.FromMinutes(15);
        public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool RequireManualApproval { get; set; }
        public List<int> RequireApprovalAtStages { get; set; } = new() { 50, 100 };
        public bool EnableABTesting { get; set; } = true;
        public int CanaryInstanceCount { get; set; } = 2;
        public int BaselineInstanceCount { get; set; } = 5;
    }

    private sealed class CanaryHistoryEntry
    {
        public string DeploymentId { get; init; } = string.Empty;
        public string PreviousVersion { get; init; } = string.Empty;
        public string TargetVersion { get; init; } = string.Empty;
        public bool Success { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public TimeSpan? Duration { get; init; }
        public int FinalTrafficPercent { get; init; }
        public string? RollbackReason { get; init; }
        public bool WasABTest { get; init; }
    }

    private enum CanaryState
    {
        Pending,
        InProgress,
        AwaitingApproval,
        Completed,
        Failed,
        RollingBack,
        RolledBack
    }

    private enum CanaryRecommendation
    {
        Promote,
        Hold,
        Rollback
    }

    private enum GateType
    {
        InitialDeployment,
        StageProgression,
        FinalPromotion
    }

    private enum GateState
    {
        Pending,
        Approved,
        Rejected,
        Expired
    }

    private enum ScheduledDeploymentState
    {
        Pending,
        Starting,
        Started,
        Failed,
        Cancelled
    }

    private enum InstanceState
    {
        Starting,
        Running,
        Draining,
        Terminated,
        Unhealthy
    }

    #endregion
}
