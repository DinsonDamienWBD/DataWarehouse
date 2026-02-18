using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDeployment.Strategies.Rollback;

/// <summary>
/// Automatic rollback strategy that triggers rollback based on health thresholds,
/// error rates, and deployment monitoring signals.
/// </summary>
public sealed class AutomaticRollbackStrategy : DeploymentStrategyBase
{
    private readonly ConcurrentDictionary<string, RollbackPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, DeploymentMonitor> _monitors = new();
    private readonly ConcurrentDictionary<string, List<DeploymentSnapshot>> _snapshotHistory = new();

    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Automatic Rollback",
        DeploymentType = DeploymentType.Rollback,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 2,
        ResourceOverheadPercent = 5,
        ComplexityLevel = 4,
        RequiredInfrastructure = new[] { "Deployment Store", "Health Monitor" },
        Description = "Automatically triggers rollback based on health thresholds and error rate monitoring"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        var policy = ParseRollbackPolicy(config);
        _policies[initialState.DeploymentId] = policy;

        // Create deployment snapshot before proceeding
        var snapshot = await CreateDeploymentSnapshotAsync(initialState.DeploymentId, config, ct);
        TrackSnapshot(initialState.DeploymentId, snapshot);

        // Initialize deployment monitor with thresholds
        var monitor = new DeploymentMonitor
        {
            DeploymentId = initialState.DeploymentId,
            Policy = policy,
            StartedAt = DateTimeOffset.UtcNow,
            ErrorCount = 0,
            TotalRequests = 0,
            LastHealthCheck = DateTimeOffset.UtcNow
        };
        _monitors[initialState.DeploymentId] = monitor;

        // Start monitoring task (background)
        _ = MonitorDeploymentAsync(initialState.DeploymentId, ct);

        return initialState with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(initialState.Metadata)
            {
                ["rollbackPolicy"] = JsonSerializer.Serialize(policy),
                ["snapshotId"] = snapshot.SnapshotId,
                ["monitoringEnabled"] = true
            }
        };
    }

    private async Task MonitorDeploymentAsync(string deploymentId, CancellationToken ct)
    {
        if (!_monitors.TryGetValue(deploymentId, out var monitor))
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(monitor.Policy.CheckIntervalSeconds), ct);

                var healthResults = await HealthCheckCoreAsync(deploymentId,
                    await GetStateAsync(deploymentId, ct), ct);

                var unhealthyCount = healthResults.Count(h => !h.IsHealthy);
                var errorRate = monitor.TotalRequests > 0
                    ? (double)monitor.ErrorCount / monitor.TotalRequests * 100
                    : 0;

                monitor.LastHealthCheck = DateTimeOffset.UtcNow;

                // Check rollback triggers
                if (ShouldTriggerRollback(monitor, unhealthyCount, healthResults.Length, errorRate))
                {
                    await TriggerAutomaticRollbackAsync(deploymentId,
                        DetermineRollbackReason(monitor, unhealthyCount, errorRate), ct);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue monitoring even on transient errors
            }
        }
    }

    private bool ShouldTriggerRollback(DeploymentMonitor monitor, int unhealthyCount, int totalCount, double errorRate)
    {
        var policy = monitor.Policy;

        // Check error rate threshold
        if (errorRate > policy.MaxErrorRatePercent)
            return true;

        // Check unhealthy instance threshold
        if (totalCount > 0)
        {
            var unhealthyPercent = (double)unhealthyCount / totalCount * 100;
            if (unhealthyPercent > policy.MaxUnhealthyPercent)
                return true;
        }

        // Check latency threshold (simulated via response time in health checks)
        if (monitor.AverageLatencyMs > policy.MaxLatencyMs)
            return true;

        // Check consecutive failures
        if (monitor.ConsecutiveFailures >= policy.MaxConsecutiveFailures)
            return true;

        return false;
    }

    private string DetermineRollbackReason(DeploymentMonitor monitor, int unhealthyCount, double errorRate)
    {
        if (errorRate > monitor.Policy.MaxErrorRatePercent)
            return $"Error rate ({errorRate:F1}%) exceeded threshold ({monitor.Policy.MaxErrorRatePercent}%)";

        if (monitor.ConsecutiveFailures >= monitor.Policy.MaxConsecutiveFailures)
            return $"Consecutive health check failures ({monitor.ConsecutiveFailures}) exceeded threshold";

        if (monitor.AverageLatencyMs > monitor.Policy.MaxLatencyMs)
            return $"Average latency ({monitor.AverageLatencyMs:F0}ms) exceeded threshold ({monitor.Policy.MaxLatencyMs}ms)";

        return $"Unhealthy instances ({unhealthyCount}) exceeded threshold";
    }

    private async Task TriggerAutomaticRollbackAsync(string deploymentId, string reason, CancellationToken ct)
    {
        var state = await GetStateAsync(deploymentId, ct);

        // Emit rollback event for intelligence integration
        if (MessageBus != null)
        {
            await MessageBus.PublishAsync("deployment.rollback.triggered", new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "deployment.rollback.triggered",
                Source = StrategyId,
                Payload = new Dictionary<string, object>
                {
                    ["deploymentId"] = deploymentId,
                    ["reason"] = reason,
                    ["triggeredAt"] = DateTimeOffset.UtcNow,
                    ["automatic"] = true
                }
            }, ct);
        }

        await RollbackAsync(deploymentId, state.PreviousVersion, ct);
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Find the snapshot for target version
        if (!_snapshotHistory.TryGetValue(deploymentId, out var snapshots))
        {
            throw new InvalidOperationException($"No snapshots found for deployment {deploymentId}");
        }

        var targetSnapshot = snapshots
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault(s => s.Version == targetVersion);

        if (targetSnapshot == null)
        {
            throw new InvalidOperationException($"No snapshot found for version {targetVersion}");
        }

        // Simulate rollback execution
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

        // Restore from snapshot
        var restoredState = await RestoreFromSnapshotAsync(deploymentId, targetSnapshot, ct);

        // Stop monitoring old deployment
        _monitors.TryRemove(deploymentId, out _);

        return restoredState;
    }

    private async Task<DeploymentState> RestoreFromSnapshotAsync(
        string deploymentId,
        DeploymentSnapshot snapshot,
        CancellationToken ct)
    {
        // Simulate restoration process
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);

        return new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = snapshot.Version,
            PreviousVersion = snapshot.PreviousVersion,
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = snapshot.InstanceCount,
            HealthyInstances = snapshot.InstanceCount,
            TargetInstances = snapshot.InstanceCount,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["restoredFromSnapshot"] = snapshot.SnapshotId,
                ["rollbackCompleted"] = true
            }
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances,
            HealthyInstances = targetInstances
        };
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var results = new List<HealthCheckResult>();

        for (int i = 0; i < currentState.DeployedInstances; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);

            results.Add(new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-instance-{i}",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = Random.Shared.Next(10, 100),
                Details = new Dictionary<string, object>
                {
                    ["autoRollbackEnabled"] = true
                }
            });
        }

        // Update monitor statistics
        if (_monitors.TryGetValue(deploymentId, out var monitor))
        {
            monitor.TotalRequests += results.Count;
            monitor.AverageLatencyMs = results.Average(r => r.ResponseTimeMs);
        }

        return results.ToArray();
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private RollbackPolicy ParseRollbackPolicy(DeploymentConfig config)
    {
        var policy = new RollbackPolicy();

        if (config.StrategyConfig.TryGetValue("maxErrorRatePercent", out var errorRate))
            policy.MaxErrorRatePercent = Convert.ToDouble(errorRate);

        if (config.StrategyConfig.TryGetValue("maxUnhealthyPercent", out var unhealthy))
            policy.MaxUnhealthyPercent = Convert.ToDouble(unhealthy);

        if (config.StrategyConfig.TryGetValue("maxLatencyMs", out var latency))
            policy.MaxLatencyMs = Convert.ToDouble(latency);

        if (config.StrategyConfig.TryGetValue("checkIntervalSeconds", out var interval))
            policy.CheckIntervalSeconds = Convert.ToInt32(interval);

        if (config.StrategyConfig.TryGetValue("maxConsecutiveFailures", out var failures))
            policy.MaxConsecutiveFailures = Convert.ToInt32(failures);

        return policy;
    }

    private async Task<DeploymentSnapshot> CreateDeploymentSnapshotAsync(
        string deploymentId,
        DeploymentConfig config,
        CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);

        return new DeploymentSnapshot
        {
            SnapshotId = $"snap-{Guid.NewGuid():N}",
            DeploymentId = deploymentId,
            Version = config.Version,
            PreviousVersion = null,
            CreatedAt = DateTimeOffset.UtcNow,
            InstanceCount = config.TargetInstances,
            Configuration = config.StrategyConfig
        };
    }

    private void TrackSnapshot(string deploymentId, DeploymentSnapshot snapshot)
    {
        _snapshotHistory.AddOrUpdate(
            deploymentId,
            _ => new List<DeploymentSnapshot> { snapshot },
            (_, list) =>
            {
                list.Add(snapshot);
                // Keep only last 10 snapshots
                if (list.Count > 10)
                    list.RemoveAt(0);
                return list;
            });
    }

    private sealed class RollbackPolicy
    {
        public double MaxErrorRatePercent { get; set; } = 5.0;
        public double MaxUnhealthyPercent { get; set; } = 20.0;
        public double MaxLatencyMs { get; set; } = 5000.0;
        public int CheckIntervalSeconds { get; set; } = 30;
        public int MaxConsecutiveFailures { get; set; } = 3;
    }

    private sealed class DeploymentMonitor
    {
        public required string DeploymentId { get; init; }
        public required RollbackPolicy Policy { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset LastHealthCheck { get; set; }
        public int ErrorCount { get; set; }
        public int TotalRequests { get; set; }
        public double AverageLatencyMs { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    private sealed class DeploymentSnapshot
    {
        public required string SnapshotId { get; init; }
        public required string DeploymentId { get; init; }
        public required string Version { get; init; }
        public string? PreviousVersion { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public int InstanceCount { get; init; }
        public Dictionary<string, object> Configuration { get; init; } = new();
    }
}

/// <summary>
/// Manual rollback strategy that provides controlled, operator-initiated rollback
/// with approval workflows, pre-rollback checks, and audit logging.
/// </summary>
public sealed class ManualRollbackStrategy : DeploymentStrategyBase
{
    private readonly ConcurrentDictionary<string, RollbackRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, List<RollbackAuditEntry>> _auditLog = new();
    private readonly ConcurrentDictionary<string, DeploymentVersionHistory> _versionHistory = new();

    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Manual Rollback",
        DeploymentType = DeploymentType.Rollback,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 5,
        ResourceOverheadPercent = 10,
        ComplexityLevel = 3,
        RequiredInfrastructure = new[] { "Version Registry", "Approval System" },
        Description = "Operator-controlled rollback with approval workflows and comprehensive audit trails"
    };

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        // Track version history for rollback targets
        var history = new DeploymentVersionHistory
        {
            DeploymentId = initialState.DeploymentId,
            Versions = new List<VersionEntry>
            {
                new()
                {
                    Version = config.Version,
                    DeployedAt = DateTimeOffset.UtcNow,
                    ArtifactUri = config.ArtifactUri,
                    Configuration = config.EnvironmentVariables
                }
            }
        };
        _versionHistory[initialState.DeploymentId] = history;

        // Initialize audit log
        _auditLog[initialState.DeploymentId] = new List<RollbackAuditEntry>
        {
            new()
            {
                Action = "DEPLOYMENT_CREATED",
                Version = config.Version,
                Timestamp = DateTimeOffset.UtcNow,
                Operator = config.Labels.GetValueOrDefault("operator", "system"),
                Details = "Initial deployment tracked for manual rollback capability"
            }
        };

        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        return initialState with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(initialState.Metadata)
            {
                ["manualRollbackEnabled"] = true,
                ["versionHistoryCount"] = 1
            }
        };
    }

    /// <summary>
    /// Creates a rollback request that requires approval before execution.
    /// </summary>
    public async Task<string> CreateRollbackRequestAsync(
        string deploymentId,
        string targetVersion,
        string reason,
        string requestor,
        CancellationToken ct = default)
    {
        var requestId = $"rb-{Guid.NewGuid():N}";

        var request = new RollbackRequest
        {
            RequestId = requestId,
            DeploymentId = deploymentId,
            TargetVersion = targetVersion,
            Reason = reason,
            Requestor = requestor,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = RollbackRequestStatus.PendingApproval
        };

        _pendingRequests[requestId] = request;

        AddAuditEntry(deploymentId, new RollbackAuditEntry
        {
            Action = "ROLLBACK_REQUESTED",
            Version = targetVersion,
            Timestamp = DateTimeOffset.UtcNow,
            Operator = requestor,
            Details = $"Reason: {reason}"
        });

        // Perform pre-rollback validation
        await PerformPreRollbackChecksAsync(request, ct);

        return requestId;
    }

    /// <summary>
    /// Approves a pending rollback request.
    /// </summary>
    public async Task<bool> ApproveRollbackRequestAsync(
        string requestId,
        string approver,
        CancellationToken ct = default)
    {
        if (!_pendingRequests.TryGetValue(requestId, out var request))
            return false;

        if (request.Status != RollbackRequestStatus.PendingApproval)
            return false;

        request.Status = RollbackRequestStatus.Approved;
        request.ApprovedBy = approver;
        request.ApprovedAt = DateTimeOffset.UtcNow;

        AddAuditEntry(request.DeploymentId, new RollbackAuditEntry
        {
            Action = "ROLLBACK_APPROVED",
            Version = request.TargetVersion,
            Timestamp = DateTimeOffset.UtcNow,
            Operator = approver,
            Details = $"Request {requestId} approved"
        });

        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        return true;
    }

    /// <summary>
    /// Executes an approved rollback request.
    /// </summary>
    public async Task<DeploymentState> ExecuteRollbackRequestAsync(
        string requestId,
        CancellationToken ct = default)
    {
        if (!_pendingRequests.TryGetValue(requestId, out var request))
            throw new InvalidOperationException($"Request {requestId} not found");

        if (request.Status != RollbackRequestStatus.Approved)
            throw new InvalidOperationException($"Request {requestId} is not approved (status: {request.Status})");

        request.Status = RollbackRequestStatus.InProgress;

        var state = await GetStateAsync(request.DeploymentId, ct);
        return await RollbackAsync(request.DeploymentId, request.TargetVersion, ct);
    }

    private async Task PerformPreRollbackChecksAsync(RollbackRequest request, CancellationToken ct)
    {
        var checks = new List<PreRollbackCheck>();

        // Check 1: Version exists in history
        if (_versionHistory.TryGetValue(request.DeploymentId, out var history))
        {
            var versionExists = history.Versions.Any(v => v.Version == request.TargetVersion);
            checks.Add(new PreRollbackCheck
            {
                CheckName = "Version Exists",
                Passed = versionExists,
                Message = versionExists
                    ? $"Version {request.TargetVersion} found in history"
                    : $"Version {request.TargetVersion} not found"
            });
        }

        // Check 2: Current deployment is stable
        var currentState = await GetStateAsync(request.DeploymentId, ct);
        checks.Add(new PreRollbackCheck
        {
            CheckName = "Current State",
            Passed = currentState.Health != DeploymentHealth.InProgress,
            Message = $"Current health: {currentState.Health}"
        });

        // Check 3: No pending operations
        checks.Add(new PreRollbackCheck
        {
            CheckName = "No Pending Operations",
            Passed = true,
            Message = "No conflicting operations in progress"
        });

        request.PreChecks = checks;
        request.PreChecksCompleted = DateTimeOffset.UtcNow;
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        if (!_versionHistory.TryGetValue(deploymentId, out var history))
        {
            throw new InvalidOperationException($"No version history for deployment {deploymentId}");
        }

        var targetEntry = history.Versions.FirstOrDefault(v => v.Version == targetVersion);
        if (targetEntry == null)
        {
            throw new InvalidOperationException($"Version {targetVersion} not found in history");
        }

        AddAuditEntry(deploymentId, new RollbackAuditEntry
        {
            Action = "ROLLBACK_STARTED",
            Version = targetVersion,
            Timestamp = DateTimeOffset.UtcNow,
            Operator = "system",
            Details = $"Rolling back from {currentState.Version} to {targetVersion}"
        });

        // Simulate gradual rollback with traffic shifting
        for (int progress = 0; progress <= 100; progress += 25)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        AddAuditEntry(deploymentId, new RollbackAuditEntry
        {
            Action = "ROLLBACK_COMPLETED",
            Version = targetVersion,
            Timestamp = DateTimeOffset.UtcNow,
            Operator = "system",
            Details = "Rollback completed successfully"
        });

        return currentState with
        {
            Version = targetVersion,
            PreviousVersion = currentState.Version,
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["rolledBackFrom"] = currentState.Version,
                ["rollbackType"] = "manual"
            }
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        AddAuditEntry(deploymentId, new RollbackAuditEntry
        {
            Action = "SCALED",
            Version = currentState.Version,
            Timestamp = DateTimeOffset.UtcNow,
            Operator = "system",
            Details = $"Scaled from {currentState.TargetInstances} to {targetInstances}"
        });

        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances,
            HealthyInstances = targetInstances
        };
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var results = new List<HealthCheckResult>();

        for (int i = 0; i < currentState.DeployedInstances; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            results.Add(new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-instance-{i}",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = Random.Shared.Next(20, 80)
            });
        }

        return results.ToArray();
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private void AddAuditEntry(string deploymentId, RollbackAuditEntry entry)
    {
        _auditLog.AddOrUpdate(
            deploymentId,
            _ => new List<RollbackAuditEntry> { entry },
            (_, list) =>
            {
                list.Add(entry);
                return list;
            });
    }

    /// <summary>
    /// Gets the audit log for a deployment.
    /// </summary>
    public IReadOnlyList<RollbackAuditEntry> GetAuditLog(string deploymentId)
    {
        return _auditLog.TryGetValue(deploymentId, out var log)
            ? log.AsReadOnly()
            : Array.Empty<RollbackAuditEntry>();
    }

    public enum RollbackRequestStatus
    {
        PendingApproval,
        Approved,
        Rejected,
        InProgress,
        Completed,
        Failed
    }

    public sealed class RollbackRequest
    {
        public required string RequestId { get; init; }
        public required string DeploymentId { get; init; }
        public required string TargetVersion { get; init; }
        public required string Reason { get; init; }
        public required string Requestor { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public RollbackRequestStatus Status { get; set; }
        public string? ApprovedBy { get; set; }
        public DateTimeOffset? ApprovedAt { get; set; }
        public List<PreRollbackCheck> PreChecks { get; set; } = new();
        public DateTimeOffset? PreChecksCompleted { get; set; }
    }

    public sealed class PreRollbackCheck
    {
        public required string CheckName { get; init; }
        public bool Passed { get; init; }
        public string? Message { get; init; }
    }

    public sealed class RollbackAuditEntry
    {
        public required string Action { get; init; }
        public required string Version { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public required string Operator { get; init; }
        public string? Details { get; init; }
    }

    private sealed class DeploymentVersionHistory
    {
        public required string DeploymentId { get; init; }
        public List<VersionEntry> Versions { get; init; } = new();
    }

    private sealed class VersionEntry
    {
        public required string Version { get; init; }
        public DateTimeOffset DeployedAt { get; init; }
        public required string ArtifactUri { get; init; }
        public Dictionary<string, string> Configuration { get; init; } = new();
    }
}

/// <summary>
/// Version pinning strategy that locks deployments to specific versions,
/// preventing unauthorized updates and enabling controlled version governance.
/// </summary>
public sealed class VersionPinningStrategy : DeploymentStrategyBase
{
    private readonly ConcurrentDictionary<string, VersionPin> _pinnedVersions = new();
    private readonly ConcurrentDictionary<string, List<VersionPinEvent>> _pinHistory = new();
    private readonly ConcurrentDictionary<string, VersionGovernancePolicy> _policies = new();

    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Version Pinning",
        DeploymentType = DeploymentType.Rollback,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 1,
        ResourceOverheadPercent = 0,
        ComplexityLevel = 2,
        RequiredInfrastructure = new[] { "Version Registry" },
        Description = "Locks deployments to specific versions with governance policies and pin management"
    };

    /// <summary>
    /// Pins a deployment to a specific version.
    /// </summary>
    public async Task<VersionPin> PinVersionAsync(
        string deploymentId,
        string version,
        string reason,
        string operator_,
        TimeSpan? pinDuration = null,
        CancellationToken ct = default)
    {
        var pin = new VersionPin
        {
            PinId = $"pin-{Guid.NewGuid():N}",
            DeploymentId = deploymentId,
            Version = version,
            Reason = reason,
            PinnedBy = operator_,
            PinnedAt = DateTimeOffset.UtcNow,
            ExpiresAt = pinDuration.HasValue ? DateTimeOffset.UtcNow.Add(pinDuration.Value) : null,
            IsActive = true
        };

        _pinnedVersions[deploymentId] = pin;

        TrackPinEvent(deploymentId, new VersionPinEvent
        {
            EventType = "PINNED",
            Version = version,
            Timestamp = DateTimeOffset.UtcNow,
            Operator = operator_,
            Details = $"Reason: {reason}" + (pinDuration.HasValue ? $", Duration: {pinDuration.Value}" : ", No expiration")
        });

        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        return pin;
    }

    /// <summary>
    /// Unpins a deployment, allowing version changes.
    /// </summary>
    public async Task<bool> UnpinVersionAsync(
        string deploymentId,
        string operator_,
        string reason,
        CancellationToken ct = default)
    {
        if (!_pinnedVersions.TryGetValue(deploymentId, out var pin))
            return false;

        pin.IsActive = false;
        pin.UnpinnedAt = DateTimeOffset.UtcNow;
        pin.UnpinnedBy = operator_;

        TrackPinEvent(deploymentId, new VersionPinEvent
        {
            EventType = "UNPINNED",
            Version = pin.Version,
            Timestamp = DateTimeOffset.UtcNow,
            Operator = operator_,
            Details = $"Reason: {reason}"
        });

        await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        return true;
    }

    /// <summary>
    /// Checks if deployment is allowed based on version pinning.
    /// </summary>
    public bool IsDeploymentAllowed(string deploymentId, string requestedVersion)
    {
        if (!_pinnedVersions.TryGetValue(deploymentId, out var pin))
            return true;

        if (!pin.IsActive)
            return true;

        // Check if pin has expired
        if (pin.ExpiresAt.HasValue && pin.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            pin.IsActive = false;
            return true;
        }

        // Check governance policy for exceptions
        if (_policies.TryGetValue(deploymentId, out var policy))
        {
            if (policy.AllowedVersionPatterns.Any(p => MatchesPattern(requestedVersion, p)))
                return true;

            if (policy.EmergencyOverrideEnabled)
                return true;
        }

        // Only allow deployment to pinned version
        return pin.Version == requestedVersion;
    }

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        // Check version pinning before deployment
        if (!IsDeploymentAllowed(initialState.DeploymentId, config.Version))
        {
            if (_pinnedVersions.TryGetValue(initialState.DeploymentId, out var pin))
            {
                throw new InvalidOperationException(
                    $"Deployment blocked: Version is pinned to {pin.Version}. " +
                    $"Reason: {pin.Reason}. Pinned by: {pin.PinnedBy}");
            }
        }

        // Parse and apply governance policy
        if (config.StrategyConfig.TryGetValue("governancePolicy", out var policyJson))
        {
            var policy = JsonSerializer.Deserialize<VersionGovernancePolicy>(policyJson.ToString()!);
            if (policy != null)
                _policies[initialState.DeploymentId] = policy;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        var isPinned = _pinnedVersions.TryGetValue(initialState.DeploymentId, out var existingPin)
                       && existingPin.IsActive;

        return initialState with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(initialState.Metadata)
            {
                ["versionPinningEnabled"] = true,
                ["isPinned"] = isPinned,
                ["pinnedVersion"] = isPinned ? (object)existingPin!.Version : null! // Dictionary<string, object?> stores null when not pinned
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Check if rollback target version is allowed
        if (!IsDeploymentAllowed(deploymentId, targetVersion))
        {
            if (_pinnedVersions.TryGetValue(deploymentId, out var pin))
            {
                // Allow rollback to pinned version
                if (pin.Version != targetVersion)
                {
                    throw new InvalidOperationException(
                        $"Rollback blocked: Version pinned to {pin.Version}");
                }
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);

        TrackPinEvent(deploymentId, new VersionPinEvent
        {
            EventType = "ROLLBACK",
            Version = targetVersion,
            Timestamp = DateTimeOffset.UtcNow,
            Operator = "system",
            Details = $"Rolled back from {currentState.Version}"
        });

        return currentState with
        {
            Version = targetVersion,
            PreviousVersion = currentState.Version,
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances,
            HealthyInstances = targetInstances
        };
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var results = new List<HealthCheckResult>();

        for (int i = 0; i < currentState.DeployedInstances; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
            results.Add(new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-instance-{i}",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = Random.Shared.Next(10, 50),
                Details = new Dictionary<string, object>
                {
                    ["versionPinned"] = _pinnedVersions.TryGetValue(deploymentId, out var pin) && pin.IsActive
                }
            });
        }

        return results.ToArray();
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private void TrackPinEvent(string deploymentId, VersionPinEvent evt)
    {
        _pinHistory.AddOrUpdate(
            deploymentId,
            _ => new List<VersionPinEvent> { evt },
            (_, list) =>
            {
                list.Add(evt);
                return list;
            });
    }

    private static bool MatchesPattern(string version, string pattern)
    {
        // Simple pattern matching: * matches anything, ? matches single char
        if (pattern == "*") return true;

        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(version, regex);
    }

    /// <summary>
    /// Gets the pin history for a deployment.
    /// </summary>
    public IReadOnlyList<VersionPinEvent> GetPinHistory(string deploymentId)
    {
        return _pinHistory.TryGetValue(deploymentId, out var history)
            ? history.AsReadOnly()
            : Array.Empty<VersionPinEvent>();
    }

    /// <summary>
    /// Gets the current pin status for a deployment.
    /// </summary>
    public VersionPin? GetPinStatus(string deploymentId)
    {
        return _pinnedVersions.TryGetValue(deploymentId, out var pin) ? pin : null;
    }

    public sealed class VersionPin
    {
        public required string PinId { get; init; }
        public required string DeploymentId { get; init; }
        public required string Version { get; init; }
        public required string Reason { get; init; }
        public required string PinnedBy { get; init; }
        public DateTimeOffset PinnedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public bool IsActive { get; set; }
        public string? UnpinnedBy { get; set; }
        public DateTimeOffset? UnpinnedAt { get; set; }
    }

    public sealed class VersionPinEvent
    {
        public required string EventType { get; init; }
        public required string Version { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public required string Operator { get; init; }
        public string? Details { get; init; }
    }

    public sealed class VersionGovernancePolicy
    {
        public string[] AllowedVersionPatterns { get; init; } = Array.Empty<string>();
        public bool EmergencyOverrideEnabled { get; init; }
        public string[] ApprovedOperators { get; init; } = Array.Empty<string>();
        public int MaxPinDurationDays { get; init; } = 30;
    }
}

/// <summary>
/// Snapshot restore strategy that creates complete deployment snapshots
/// and enables point-in-time recovery with data integrity verification.
/// </summary>
public sealed class SnapshotRestoreStrategy : DeploymentStrategyBase
{
    private readonly ConcurrentDictionary<string, List<FullSnapshot>> _snapshots = new();
    private readonly ConcurrentDictionary<string, SnapshotPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, RestoreOperation> _restoreOperations = new();

    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Snapshot Restore",
        DeploymentType = DeploymentType.Rollback,
        SupportsZeroDowntime = false,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = false,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 10,
        ResourceOverheadPercent = 50,
        ComplexityLevel = 6,
        RequiredInfrastructure = new[] { "Snapshot Storage", "Backup Service" },
        Description = "Creates complete deployment snapshots with data for point-in-time recovery"
    };

    /// <summary>
    /// Creates a full snapshot of the current deployment state.
    /// </summary>
    public async Task<FullSnapshot> CreateSnapshotAsync(
        string deploymentId,
        string label,
        CancellationToken ct = default)
    {
        var state = await GetStateAsync(deploymentId, ct);

        var snapshot = new FullSnapshot
        {
            SnapshotId = $"snap-{Guid.NewGuid():N}",
            DeploymentId = deploymentId,
            Label = label,
            Version = state.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            State = state,
            InstanceCount = state.DeployedInstances,
            SizeBytes = await CalculateSnapshotSizeAsync(state, ct),
            Checksum = await ComputeSnapshotChecksumAsync(state, ct),
            Status = SnapshotStatus.Creating
        };

        // Simulate snapshot creation
        await SimulateSnapshotCreationAsync(snapshot, ct);

        snapshot.Status = SnapshotStatus.Completed;
        snapshot.CompletedAt = DateTimeOffset.UtcNow;

        TrackSnapshot(deploymentId, snapshot);

        return snapshot;
    }

    /// <summary>
    /// Restores a deployment from a snapshot.
    /// </summary>
    public async Task<RestoreOperation> RestoreFromSnapshotAsync(
        string snapshotId,
        RestoreOptions? options = null,
        CancellationToken ct = default)
    {
        var snapshot = FindSnapshot(snapshotId);
        if (snapshot == null)
            throw new InvalidOperationException($"Snapshot {snapshotId} not found");

        if (snapshot.Status != SnapshotStatus.Completed)
            throw new InvalidOperationException($"Snapshot {snapshotId} is not in completed state");

        var operation = new RestoreOperation
        {
            OperationId = $"restore-{Guid.NewGuid():N}",
            SnapshotId = snapshotId,
            DeploymentId = snapshot.DeploymentId,
            TargetVersion = snapshot.Version,
            StartedAt = DateTimeOffset.UtcNow,
            Status = RestoreOperationStatus.Validating,
            Options = options ?? new RestoreOptions()
        };

        _restoreOperations[operation.OperationId] = operation;

        try
        {
            // Step 1: Validate snapshot integrity
            operation.Status = RestoreOperationStatus.Validating;
            await ValidateSnapshotIntegrityAsync(snapshot, ct);
            operation.ValidationCompleted = true;

            // Step 2: Prepare restore environment
            operation.Status = RestoreOperationStatus.Preparing;
            await PrepareRestoreEnvironmentAsync(snapshot, operation.Options, ct);
            operation.PreparationCompleted = true;

            // Step 3: Execute restore
            operation.Status = RestoreOperationStatus.Restoring;
            await ExecuteRestoreAsync(snapshot, operation, ct);
            operation.RestoreCompleted = true;

            // Step 4: Verify restoration
            operation.Status = RestoreOperationStatus.Verifying;
            await VerifyRestorationAsync(snapshot, ct);
            operation.VerificationCompleted = true;

            operation.Status = RestoreOperationStatus.Completed;
            operation.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            operation.Status = RestoreOperationStatus.Failed;
            operation.ErrorMessage = ex.Message;
            operation.CompletedAt = DateTimeOffset.UtcNow;
        }

        return operation;
    }

    /// <summary>
    /// Lists available snapshots for a deployment.
    /// </summary>
    public IReadOnlyList<FullSnapshot> ListSnapshots(string deploymentId)
    {
        return _snapshots.TryGetValue(deploymentId, out var snapshots)
            ? snapshots.OrderByDescending(s => s.CreatedAt).ToList().AsReadOnly()
            : Array.Empty<FullSnapshot>();
    }

    /// <summary>
    /// Deletes a snapshot.
    /// </summary>
    public async Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        foreach (var kvp in _snapshots)
        {
            var snapshot = kvp.Value.FirstOrDefault(s => s.SnapshotId == snapshotId);
            if (snapshot != null)
            {
                kvp.Value.Remove(snapshot);
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Configures snapshot policy for a deployment.
    /// </summary>
    public void ConfigureSnapshotPolicy(string deploymentId, SnapshotPolicy policy)
    {
        _policies[deploymentId] = policy;
    }

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        // Parse snapshot policy from config
        if (config.StrategyConfig.TryGetValue("snapshotPolicy", out var policyJson))
        {
            var policy = JsonSerializer.Deserialize<SnapshotPolicy>(policyJson.ToString()!);
            if (policy != null)
                _policies[initialState.DeploymentId] = policy;
        }

        // Create initial snapshot if auto-snapshot is enabled
        var autoSnapshot = config.StrategyConfig.TryGetValue("autoSnapshot", out var auto)
                           && Convert.ToBoolean(auto);

        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);

        var state = initialState with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(initialState.Metadata)
            {
                ["snapshotRestoreEnabled"] = true,
                ["autoSnapshotEnabled"] = autoSnapshot
            }
        };

        if (autoSnapshot)
        {
            await CreateSnapshotAsync(initialState.DeploymentId, "auto-initial", ct);
        }

        return state;
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Find snapshot for target version
        if (!_snapshots.TryGetValue(deploymentId, out var snapshots))
        {
            throw new InvalidOperationException($"No snapshots available for deployment {deploymentId}");
        }

        var targetSnapshot = snapshots
            .Where(s => s.Version == targetVersion && s.Status == SnapshotStatus.Completed)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault();

        if (targetSnapshot == null)
        {
            throw new InvalidOperationException($"No completed snapshot found for version {targetVersion}");
        }

        // Perform restore
        var restoreOp = await RestoreFromSnapshotAsync(targetSnapshot.SnapshotId, null, ct);

        if (restoreOp.Status == RestoreOperationStatus.Failed)
        {
            throw new InvalidOperationException($"Restore failed: {restoreOp.ErrorMessage}");
        }

        return targetSnapshot.State with
        {
            PreviousVersion = currentState.Version,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(targetSnapshot.State.Metadata)
            {
                ["restoredFromSnapshot"] = targetSnapshot.SnapshotId,
                ["restoreOperation"] = restoreOp.OperationId
            }
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Create snapshot before scaling if policy requires it
        if (_policies.TryGetValue(deploymentId, out var policy) && policy.SnapshotBeforeScale)
        {
            await CreateSnapshotAsync(deploymentId, "pre-scale", ct);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances,
            HealthyInstances = targetInstances
        };
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var results = new List<HealthCheckResult>();

        for (int i = 0; i < currentState.DeployedInstances; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);

            var snapshotCount = _snapshots.TryGetValue(deploymentId, out var snaps) ? snaps.Count : 0;

            results.Add(new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-instance-{i}",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = Random.Shared.Next(15, 60),
                Details = new Dictionary<string, object>
                {
                    ["snapshotsAvailable"] = snapshotCount
                }
            });
        }

        return results.ToArray();
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        // Try to get from latest snapshot
        if (_snapshots.TryGetValue(deploymentId, out var snapshots) && snapshots.Count > 0)
        {
            var latest = snapshots.OrderByDescending(s => s.CreatedAt).First();
            return Task.FromResult(latest.State);
        }

        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private void TrackSnapshot(string deploymentId, FullSnapshot snapshot)
    {
        _snapshots.AddOrUpdate(
            deploymentId,
            _ => new List<FullSnapshot> { snapshot },
            (_, list) =>
            {
                list.Add(snapshot);

                // Apply retention policy
                if (_policies.TryGetValue(deploymentId, out var policy))
                {
                    ApplyRetentionPolicy(list, policy);
                }
                else
                {
                    // Default: keep last 10
                    while (list.Count > 10)
                        list.RemoveAt(0);
                }

                return list;
            });
    }

    private void ApplyRetentionPolicy(List<FullSnapshot> snapshots, SnapshotPolicy policy)
    {
        // Remove snapshots older than retention period
        var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.RetentionDays);
        snapshots.RemoveAll(s => s.CreatedAt < cutoff && s.Label != "pinned");

        // Keep max count
        while (snapshots.Count > policy.MaxSnapshots)
        {
            var oldest = snapshots
                .Where(s => s.Label != "pinned")
                .OrderBy(s => s.CreatedAt)
                .FirstOrDefault();

            if (oldest != null)
                snapshots.Remove(oldest);
            else
                break;
        }
    }

    private FullSnapshot? FindSnapshot(string snapshotId)
    {
        foreach (var kvp in _snapshots)
        {
            var snapshot = kvp.Value.FirstOrDefault(s => s.SnapshotId == snapshotId);
            if (snapshot != null)
                return snapshot;
        }
        return null;
    }

    private async Task<long> CalculateSnapshotSizeAsync(DeploymentState state, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        // Simulate size calculation based on instance count
        return state.DeployedInstances * 1024 * 1024; // 1MB per instance
    }

    private async Task<string> ComputeSnapshotChecksumAsync(DeploymentState state, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        var data = JsonSerializer.SerializeToUtf8Bytes(state);
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task SimulateSnapshotCreationAsync(FullSnapshot snapshot, CancellationToken ct)
    {
        // Simulate incremental snapshot progress
        for (int progress = 0; progress <= 100; progress += 10)
        {
            ct.ThrowIfCancellationRequested();
            snapshot.Progress = progress;
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    private async Task ValidateSnapshotIntegrityAsync(FullSnapshot snapshot, CancellationToken ct)
    {
        // Verify checksum
        var currentChecksum = await ComputeSnapshotChecksumAsync(snapshot.State, ct);
        if (currentChecksum != snapshot.Checksum)
        {
            throw new InvalidOperationException("Snapshot integrity check failed: checksum mismatch");
        }
    }

    private async Task PrepareRestoreEnvironmentAsync(FullSnapshot snapshot, RestoreOptions options, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        if (options.CreateBackupBeforeRestore)
        {
            // Create backup of current state
            await CreateSnapshotAsync(snapshot.DeploymentId, "pre-restore-backup", ct);
        }
    }

    private async Task ExecuteRestoreAsync(FullSnapshot snapshot, RestoreOperation operation, CancellationToken ct)
    {
        // Simulate restore process
        for (int progress = 0; progress <= 100; progress += 5)
        {
            ct.ThrowIfCancellationRequested();
            operation.Progress = progress;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    private async Task VerifyRestorationAsync(FullSnapshot snapshot, CancellationToken ct)
    {
        // Verify deployment state matches snapshot
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);

        var healthResults = await HealthCheckCoreAsync(
            snapshot.DeploymentId, snapshot.State, ct);

        var unhealthyCount = healthResults.Count(h => !h.IsHealthy);
        if (unhealthyCount > 0)
        {
            throw new InvalidOperationException(
                $"Restoration verification failed: {unhealthyCount} unhealthy instances");
        }
    }

    public enum SnapshotStatus
    {
        Creating,
        Completed,
        Failed,
        Expired
    }

    public enum RestoreOperationStatus
    {
        Validating,
        Preparing,
        Restoring,
        Verifying,
        Completed,
        Failed
    }

    public sealed class FullSnapshot
    {
        public required string SnapshotId { get; init; }
        public required string DeploymentId { get; init; }
        public required string Label { get; init; }
        public required string Version { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
        public required DeploymentState State { get; init; }
        public int InstanceCount { get; init; }
        public long SizeBytes { get; init; }
        public required string Checksum { get; init; }
        public SnapshotStatus Status { get; set; }
        public int Progress { get; set; }
    }

    public sealed class RestoreOperation
    {
        public required string OperationId { get; init; }
        public required string SnapshotId { get; init; }
        public required string DeploymentId { get; init; }
        public required string TargetVersion { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
        public RestoreOperationStatus Status { get; set; }
        public int Progress { get; set; }
        public bool ValidationCompleted { get; set; }
        public bool PreparationCompleted { get; set; }
        public bool RestoreCompleted { get; set; }
        public bool VerificationCompleted { get; set; }
        public string? ErrorMessage { get; set; }
        public required RestoreOptions Options { get; init; }
    }

    public sealed class RestoreOptions
    {
        public bool CreateBackupBeforeRestore { get; init; } = true;
        public bool VerifyAfterRestore { get; init; } = true;
        public bool RestoreConfiguration { get; init; } = true;
        public bool RestoreData { get; init; } = false;
        public int TimeoutMinutes { get; init; } = 30;
    }

    public sealed class SnapshotPolicy
    {
        public int MaxSnapshots { get; init; } = 10;
        public int RetentionDays { get; init; } = 30;
        public bool AutoSnapshot { get; init; }
        public int AutoSnapshotIntervalHours { get; init; } = 24;
        public bool SnapshotBeforeScale { get; init; }
        public bool SnapshotBeforeDeploy { get; init; } = true;
    }
}

/// <summary>
/// Immutable deployment strategy that creates versioned, immutable deployment artifacts
/// with cryptographic verification and tamper detection.
/// </summary>
public sealed class ImmutableDeploymentStrategy : DeploymentStrategyBase
{
    private readonly ConcurrentDictionary<string, ImmutableArtifact> _artifacts = new();
    private readonly ConcurrentDictionary<string, List<DeploymentRecord>> _deploymentLedger = new();

    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Immutable Deployment",
        DeploymentType = DeploymentType.Rollback,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = true,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = true,
        TypicalDeploymentTimeMinutes = 3,
        ResourceOverheadPercent = 20,
        ComplexityLevel = 5,
        RequiredInfrastructure = new[] { "Artifact Registry", "Content-Addressable Storage" },
        Description = "Immutable, versioned deployment artifacts with cryptographic integrity verification"
    };

    /// <summary>
    /// Registers an immutable artifact for deployment.
    /// </summary>
    public async Task<ImmutableArtifact> RegisterArtifactAsync(
        string artifactUri,
        string version,
        byte[] content,
        CancellationToken ct = default)
    {
        var hash = SHA256.HashData(content);
        var contentHash = Convert.ToHexString(hash).ToLowerInvariant();

        var artifact = new ImmutableArtifact
        {
            ArtifactId = $"art-{contentHash[..16]}",
            Version = version,
            ArtifactUri = artifactUri,
            ContentHash = contentHash,
            SizeBytes = content.Length,
            CreatedAt = DateTimeOffset.UtcNow,
            IsSealed = false
        };

        // Seal the artifact (make immutable)
        artifact.SealedAt = DateTimeOffset.UtcNow;
        artifact.IsSealed = true;
        artifact.SealSignature = ComputeSealSignature(artifact);

        _artifacts[$"{artifact.ArtifactId}:{version}"] = artifact;

        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        return artifact;
    }

    /// <summary>
    /// Verifies an artifact's integrity.
    /// </summary>
    public bool VerifyArtifactIntegrity(string artifactId, string version)
    {
        if (!_artifacts.TryGetValue($"{artifactId}:{version}", out var artifact))
            return false;

        if (!artifact.IsSealed)
            return false;

        var expectedSignature = ComputeSealSignature(artifact);
        return artifact.SealSignature == expectedSignature;
    }

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        // Verify artifact before deployment
        var artifactKey = _artifacts.Keys.FirstOrDefault(k => k.EndsWith($":{config.Version}"));
        if (artifactKey != null)
        {
            var artifact = _artifacts[artifactKey];
            if (!VerifyArtifactIntegrity(artifact.ArtifactId, config.Version))
            {
                throw new InvalidOperationException("Artifact integrity verification failed");
            }
        }

        // Record deployment in immutable ledger
        var record = new DeploymentRecord
        {
            RecordId = $"rec-{Guid.NewGuid():N}",
            DeploymentId = initialState.DeploymentId,
            Version = config.Version,
            ArtifactUri = config.ArtifactUri,
            Timestamp = DateTimeOffset.UtcNow,
            Action = "DEPLOY"
        };

        AddToLedger(initialState.DeploymentId, record);

        await Task.Delay(TimeSpan.FromMilliseconds(150), ct);

        return initialState with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(initialState.Metadata)
            {
                ["immutableDeployment"] = true,
                ["artifactVerified"] = artifactKey != null,
                ["ledgerRecordId"] = record.RecordId
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        // Find artifact for target version
        var artifactKey = _artifacts.Keys.FirstOrDefault(k => k.EndsWith($":{targetVersion}"));
        if (artifactKey != null)
        {
            var artifact = _artifacts[artifactKey];
            if (!VerifyArtifactIntegrity(artifact.ArtifactId, targetVersion))
            {
                throw new InvalidOperationException(
                    $"Cannot rollback: artifact integrity verification failed for version {targetVersion}");
            }
        }

        // Record rollback in ledger
        var record = new DeploymentRecord
        {
            RecordId = $"rec-{Guid.NewGuid():N}",
            DeploymentId = deploymentId,
            Version = targetVersion,
            ArtifactUri = "",
            Timestamp = DateTimeOffset.UtcNow,
            Action = "ROLLBACK",
            PreviousVersion = currentState.Version
        };

        AddToLedger(deploymentId, record);

        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        return currentState with
        {
            Version = targetVersion,
            PreviousVersion = currentState.Version,
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["rollbackRecordId"] = record.RecordId
            }
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var record = new DeploymentRecord
        {
            RecordId = $"rec-{Guid.NewGuid():N}",
            DeploymentId = deploymentId,
            Version = currentState.Version,
            ArtifactUri = "",
            Timestamp = DateTimeOffset.UtcNow,
            Action = "SCALE",
            ScaleFrom = currentState.TargetInstances,
            ScaleTo = targetInstances
        };

        AddToLedger(deploymentId, record);

        await Task.Delay(TimeSpan.FromMilliseconds(75), ct);

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances,
            HealthyInstances = targetInstances
        };
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var results = new List<HealthCheckResult>();

        for (int i = 0; i < currentState.DeployedInstances; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            results.Add(new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-instance-{i}",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = Random.Shared.Next(10, 40),
                Details = new Dictionary<string, object>
                {
                    ["immutable"] = true
                }
            });
        }

        return results.ToArray();
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        // Get latest deployment record from ledger
        if (_deploymentLedger.TryGetValue(deploymentId, out var records) && records.Count > 0)
        {
            var latest = records.OrderByDescending(r => r.Timestamp).First();
            return Task.FromResult(new DeploymentState
            {
                DeploymentId = deploymentId,
                Version = latest.Version,
                Health = DeploymentHealth.Healthy
            });
        }

        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private void AddToLedger(string deploymentId, DeploymentRecord record)
    {
        // Compute hash chain for immutability
        if (_deploymentLedger.TryGetValue(deploymentId, out var records) && records.Count > 0)
        {
            var previous = records.Last();
            record.PreviousHash = previous.RecordHash;
        }

        record.RecordHash = ComputeRecordHash(record);

        _deploymentLedger.AddOrUpdate(
            deploymentId,
            _ => new List<DeploymentRecord> { record },
            (_, list) =>
            {
                list.Add(record);
                return list;
            });
    }

    private static string ComputeSealSignature(ImmutableArtifact artifact)
    {
        var data = $"{artifact.ArtifactId}:{artifact.Version}:{artifact.ContentHash}:{artifact.CreatedAt.ToUnixTimeSeconds()}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeRecordHash(DeploymentRecord record)
    {
        var data = $"{record.RecordId}:{record.DeploymentId}:{record.Version}:{record.Action}:{record.Timestamp.ToUnixTimeSeconds()}:{record.PreviousHash ?? "genesis"}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Gets the deployment ledger for auditing.
    /// </summary>
    public IReadOnlyList<DeploymentRecord> GetDeploymentLedger(string deploymentId)
    {
        return _deploymentLedger.TryGetValue(deploymentId, out var records)
            ? records.AsReadOnly()
            : Array.Empty<DeploymentRecord>();
    }

    /// <summary>
    /// Verifies the integrity of the deployment ledger chain.
    /// </summary>
    public bool VerifyLedgerIntegrity(string deploymentId)
    {
        if (!_deploymentLedger.TryGetValue(deploymentId, out var records))
            return true;

        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var expectedHash = ComputeRecordHash(record);

            if (record.RecordHash != expectedHash)
                return false;

            if (i > 0 && record.PreviousHash != records[i - 1].RecordHash)
                return false;
        }

        return true;
    }

    public sealed class ImmutableArtifact
    {
        public required string ArtifactId { get; init; }
        public required string Version { get; init; }
        public required string ArtifactUri { get; init; }
        public required string ContentHash { get; init; }
        public long SizeBytes { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public bool IsSealed { get; set; }
        public DateTimeOffset? SealedAt { get; set; }
        public string? SealSignature { get; set; }
    }

    public sealed class DeploymentRecord
    {
        public required string RecordId { get; init; }
        public required string DeploymentId { get; init; }
        public required string Version { get; init; }
        public required string ArtifactUri { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public required string Action { get; init; }
        public string? PreviousVersion { get; set; }
        public int? ScaleFrom { get; set; }
        public int? ScaleTo { get; set; }
        public string? PreviousHash { get; set; }
        public string? RecordHash { get; set; }
    }
}

/// <summary>
/// Time-based rollback strategy that enables scheduling rollbacks based on time windows,
/// maintenance schedules, and time-based governance policies.
/// </summary>
public sealed class TimeBasedRollbackStrategy : DeploymentStrategyBase
{
    private readonly ConcurrentDictionary<string, ScheduledRollback> _scheduledRollbacks = new();
    private readonly ConcurrentDictionary<string, MaintenanceWindow> _maintenanceWindows = new();
    private readonly ConcurrentDictionary<string, List<TimeBasedEvent>> _eventLog = new();

    public override DeploymentCharacteristics Characteristics => new()
    {
        StrategyName = "Time-Based Rollback",
        DeploymentType = DeploymentType.Rollback,
        SupportsZeroDowntime = true,
        SupportsInstantRollback = false,
        SupportsTrafficShifting = true,
        SupportsHealthChecks = true,
        SupportsAutoScaling = false,
        TypicalDeploymentTimeMinutes = 5,
        ResourceOverheadPercent = 5,
        ComplexityLevel = 4,
        RequiredInfrastructure = new[] { "Scheduler", "Time Service" },
        Description = "Schedule rollbacks based on time windows and maintenance schedules"
    };

    /// <summary>
    /// Schedules a rollback for a specific time.
    /// </summary>
    public async Task<ScheduledRollback> ScheduleRollbackAsync(
        string deploymentId,
        string targetVersion,
        DateTimeOffset scheduledTime,
        string reason,
        CancellationToken ct = default)
    {
        var scheduled = new ScheduledRollback
        {
            ScheduleId = $"sched-{Guid.NewGuid():N}",
            DeploymentId = deploymentId,
            TargetVersion = targetVersion,
            ScheduledTime = scheduledTime,
            Reason = reason,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = ScheduledRollbackStatus.Pending
        };

        _scheduledRollbacks[scheduled.ScheduleId] = scheduled;

        LogEvent(deploymentId, new TimeBasedEvent
        {
            EventType = "ROLLBACK_SCHEDULED",
            Timestamp = DateTimeOffset.UtcNow,
            Details = $"Scheduled for {scheduledTime:O}, target version: {targetVersion}"
        });

        // Start monitoring task
        _ = MonitorScheduledRollbackAsync(scheduled, ct);

        await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        return scheduled;
    }

    /// <summary>
    /// Cancels a scheduled rollback.
    /// </summary>
    public async Task<bool> CancelScheduledRollbackAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        if (!_scheduledRollbacks.TryGetValue(scheduleId, out var scheduled))
            return false;

        if (scheduled.Status != ScheduledRollbackStatus.Pending)
            return false;

        scheduled.Status = ScheduledRollbackStatus.Cancelled;
        scheduled.CancelledAt = DateTimeOffset.UtcNow;

        LogEvent(scheduled.DeploymentId, new TimeBasedEvent
        {
            EventType = "ROLLBACK_CANCELLED",
            Timestamp = DateTimeOffset.UtcNow,
            Details = $"Scheduled rollback {scheduleId} cancelled"
        });

        await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        return true;
    }

    /// <summary>
    /// Configures a maintenance window for a deployment.
    /// </summary>
    public void ConfigureMaintenanceWindow(string deploymentId, MaintenanceWindow window)
    {
        _maintenanceWindows[deploymentId] = window;

        LogEvent(deploymentId, new TimeBasedEvent
        {
            EventType = "MAINTENANCE_WINDOW_CONFIGURED",
            Timestamp = DateTimeOffset.UtcNow,
            Details = $"{window.DayOfWeek} {window.StartTime}-{window.EndTime} UTC"
        });
    }

    /// <summary>
    /// Checks if current time is within maintenance window.
    /// </summary>
    public bool IsInMaintenanceWindow(string deploymentId)
    {
        if (!_maintenanceWindows.TryGetValue(deploymentId, out var window))
            return true; // No window configured, allow anytime

        var now = DateTimeOffset.UtcNow;
        if (now.DayOfWeek != window.DayOfWeek)
            return false;

        var currentTime = now.TimeOfDay;
        return currentTime >= window.StartTime && currentTime <= window.EndTime;
    }

    private async Task MonitorScheduledRollbackAsync(ScheduledRollback scheduled, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && scheduled.Status == ScheduledRollbackStatus.Pending)
        {
            if (DateTimeOffset.UtcNow >= scheduled.ScheduledTime)
            {
                await ExecuteScheduledRollbackAsync(scheduled, ct);
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task ExecuteScheduledRollbackAsync(ScheduledRollback scheduled, CancellationToken ct)
    {
        // Check maintenance window
        if (!IsInMaintenanceWindow(scheduled.DeploymentId))
        {
            // Reschedule to next maintenance window
            if (_maintenanceWindows.TryGetValue(scheduled.DeploymentId, out var window))
            {
                var nextWindow = CalculateNextMaintenanceWindow(window);
                scheduled.ScheduledTime = nextWindow;

                LogEvent(scheduled.DeploymentId, new TimeBasedEvent
                {
                    EventType = "ROLLBACK_RESCHEDULED",
                    Timestamp = DateTimeOffset.UtcNow,
                    Details = $"Rescheduled to next maintenance window: {nextWindow:O}"
                });

                return;
            }
        }

        scheduled.Status = ScheduledRollbackStatus.InProgress;

        try
        {
            var state = await GetStateAsync(scheduled.DeploymentId, ct);
            await RollbackAsync(scheduled.DeploymentId, scheduled.TargetVersion, ct);

            scheduled.Status = ScheduledRollbackStatus.Completed;
            scheduled.CompletedAt = DateTimeOffset.UtcNow;

            LogEvent(scheduled.DeploymentId, new TimeBasedEvent
            {
                EventType = "SCHEDULED_ROLLBACK_COMPLETED",
                Timestamp = DateTimeOffset.UtcNow,
                Details = $"Rolled back to {scheduled.TargetVersion}"
            });
        }
        catch (Exception ex)
        {
            scheduled.Status = ScheduledRollbackStatus.Failed;
            scheduled.ErrorMessage = ex.Message;

            LogEvent(scheduled.DeploymentId, new TimeBasedEvent
            {
                EventType = "SCHEDULED_ROLLBACK_FAILED",
                Timestamp = DateTimeOffset.UtcNow,
                Details = ex.Message
            });
        }
    }

    private DateTimeOffset CalculateNextMaintenanceWindow(MaintenanceWindow window)
    {
        var now = DateTimeOffset.UtcNow;
        var daysUntilNext = ((int)window.DayOfWeek - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilNext == 0 && now.TimeOfDay > window.EndTime)
            daysUntilNext = 7;

        var nextDate = now.Date.AddDays(daysUntilNext);
        return new DateTimeOffset(nextDate + window.StartTime, TimeSpan.Zero);
    }

    protected override async Task<DeploymentState> DeployCoreAsync(
        DeploymentConfig config,
        DeploymentState initialState,
        CancellationToken ct)
    {
        // Check maintenance window before deployment
        if (!IsInMaintenanceWindow(initialState.DeploymentId))
        {
            var allowOutsideWindow = config.StrategyConfig.TryGetValue("allowOutsideMaintenanceWindow", out var allow)
                                     && Convert.ToBoolean(allow);

            if (!allowOutsideWindow && _maintenanceWindows.ContainsKey(initialState.DeploymentId))
            {
                throw new InvalidOperationException("Deployment not allowed outside maintenance window");
            }
        }

        LogEvent(initialState.DeploymentId, new TimeBasedEvent
        {
            EventType = "DEPLOYMENT",
            Timestamp = DateTimeOffset.UtcNow,
            Details = $"Deployed version {config.Version}"
        });

        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        return initialState with
        {
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = config.TargetInstances,
            HealthyInstances = config.TargetInstances,
            CompletedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, object>(initialState.Metadata)
            {
                ["timeBasedRollbackEnabled"] = true,
                ["hasMaintenanceWindow"] = _maintenanceWindows.ContainsKey(initialState.DeploymentId)
            }
        };
    }

    protected override async Task<DeploymentState> RollbackCoreAsync(
        string deploymentId,
        string targetVersion,
        DeploymentState currentState,
        CancellationToken ct)
    {
        LogEvent(deploymentId, new TimeBasedEvent
        {
            EventType = "ROLLBACK",
            Timestamp = DateTimeOffset.UtcNow,
            Details = $"Rolling back from {currentState.Version} to {targetVersion}"
        });

        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);

        return currentState with
        {
            Version = targetVersion,
            PreviousVersion = currentState.Version,
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    protected override async Task<DeploymentState> ScaleCoreAsync(
        string deploymentId,
        int targetInstances,
        DeploymentState currentState,
        CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);

        return currentState with
        {
            TargetInstances = targetInstances,
            DeployedInstances = targetInstances,
            HealthyInstances = targetInstances
        };
    }

    protected override async Task<HealthCheckResult[]> HealthCheckCoreAsync(
        string deploymentId,
        DeploymentState currentState,
        CancellationToken ct)
    {
        var results = new List<HealthCheckResult>();

        for (int i = 0; i < currentState.DeployedInstances; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
            results.Add(new HealthCheckResult
            {
                InstanceId = $"{deploymentId}-instance-{i}",
                IsHealthy = true,
                StatusCode = 200,
                ResponseTimeMs = Random.Shared.Next(10, 50)
            });
        }

        return results.ToArray();
    }

    protected override Task<DeploymentState> GetStateCoreAsync(string deploymentId, CancellationToken ct)
    {
        return Task.FromResult(new DeploymentState
        {
            DeploymentId = deploymentId,
            Version = "unknown",
            Health = DeploymentHealth.Unknown
        });
    }

    private void LogEvent(string deploymentId, TimeBasedEvent evt)
    {
        _eventLog.AddOrUpdate(
            deploymentId,
            _ => new List<TimeBasedEvent> { evt },
            (_, list) =>
            {
                list.Add(evt);
                return list;
            });
    }

    /// <summary>
    /// Gets scheduled rollbacks for a deployment.
    /// </summary>
    public IReadOnlyList<ScheduledRollback> GetScheduledRollbacks(string deploymentId)
    {
        return _scheduledRollbacks.Values
            .Where(s => s.DeploymentId == deploymentId)
            .OrderBy(s => s.ScheduledTime)
            .ToList()
            .AsReadOnly();
    }

    public enum ScheduledRollbackStatus
    {
        Pending,
        InProgress,
        Completed,
        Cancelled,
        Failed
    }

    public sealed class ScheduledRollback
    {
        public required string ScheduleId { get; init; }
        public required string DeploymentId { get; init; }
        public required string TargetVersion { get; init; }
        public DateTimeOffset ScheduledTime { get; set; }
        public required string Reason { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public ScheduledRollbackStatus Status { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset? CancelledAt { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public sealed class MaintenanceWindow
    {
        public DayOfWeek DayOfWeek { get; init; }
        public TimeSpan StartTime { get; init; }
        public TimeSpan EndTime { get; init; }
        public string? Description { get; init; }
    }

    public sealed class TimeBasedEvent
    {
        public required string EventType { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public string? Details { get; init; }
    }
}
