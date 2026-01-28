using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.AutonomousDataManagement.Engine;
using DataWarehouse.Plugins.AutonomousDataManagement.Models;
using DataWarehouse.Plugins.AutonomousDataManagement.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

using StorageTier = DataWarehouse.Plugins.AutonomousDataManagement.Models.StorageTier;

namespace DataWarehouse.Plugins.AutonomousDataManagement;

/// <summary>
/// Autonomous Data Management (AIOps) plugin for intelligent storage operations.
/// Provides self-optimizing tiering, predictive scaling, anomaly auto-remediation,
/// cost optimization, security response, compliance automation, capacity planning,
/// and performance tuning - all using provider-agnostic AI integration.
///
/// Message Topics:
/// - aiops.tiering.optimize: Optimize data tiering
/// - aiops.scaling.predict: Predict scaling needs
/// - aiops.anomaly.detect: Detect anomalies
/// - aiops.anomaly.remediate: Remediate anomalies
/// - aiops.cost.analyze: Analyze costs
/// - aiops.cost.optimize: Execute cost optimization
/// - aiops.security.respond: Respond to security threats
/// - aiops.compliance.enforce: Enforce compliance policies
/// - aiops.capacity.plan: Plan capacity
/// - aiops.performance.tune: Tune performance
/// - aiops.provider.configure: Configure AI providers
/// - aiops.provider.list: List available providers
/// - aiops.stats: Get overall statistics
/// </summary>
public sealed class AutonomousDataManagementPlugin : IntelligencePluginBase, IDisposable
{
    private readonly AIProviderSelector _providerSelector;
    private readonly TieringEngine _tieringEngine;
    private readonly ScalingEngine _scalingEngine;
    private readonly AnomalyEngine _anomalyEngine;
    private readonly CostEngine _costEngine;
    private readonly SecurityEngine _securityEngine;
    private readonly ComplianceEngine _complianceEngine;
    private readonly CapacityEngine _capacityEngine;
    private readonly PerformanceEngine _performanceEngine;
    private readonly AIOpsConfig _config;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.aiops.autonomous";

    /// <inheritdoc/>
    public override string Name => "Autonomous Data Management (AIOps) Plugin";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string ProviderType => "aiops-autonomous";

    /// <summary>
    /// Creates a new instance of the Autonomous Data Management plugin.
    /// </summary>
    /// <param name="providerRegistry">The AI provider registry for multi-provider support.</param>
    /// <param name="config">Optional configuration.</param>
    public AutonomousDataManagementPlugin(
        IAIProviderRegistry providerRegistry,
        AIOpsConfig? config = null)
    {
        if (providerRegistry == null)
            throw new ArgumentNullException(nameof(providerRegistry));

        _config = config ?? new AIOpsConfig();
        _cts = new CancellationTokenSource();

        // Initialize provider selector with registry
        _providerSelector = new AIProviderSelector(providerRegistry, _config.ProviderConfig);

        // Initialize all engines
        _tieringEngine = new TieringEngine(_providerSelector, _config.TieringConfig);
        _scalingEngine = new ScalingEngine(_providerSelector, _config.ScalingConfig);
        _anomalyEngine = new AnomalyEngine(_providerSelector, _config.AnomalyConfig);
        _costEngine = new CostEngine(_providerSelector, _config.CostConfig);
        _securityEngine = new SecurityEngine(_providerSelector, _config.SecurityConfig);
        _complianceEngine = new ComplianceEngine(_providerSelector, _config.ComplianceConfig);
        _capacityEngine = new CapacityEngine(_providerSelector, _config.CapacityConfig);
        _performanceEngine = new PerformanceEngine(_providerSelector, _config.PerformanceConfig);
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        try
        {
            switch (message.Type)
            {
                // Tiering
                case "aiops.tiering.optimize":
                    await HandleTieringOptimizeAsync(message);
                    break;
                case "aiops.tiering.recommend":
                    await HandleTieringRecommendAsync(message);
                    break;
                case "aiops.tiering.migrate":
                    await HandleTieringMigrateAsync(message);
                    break;
                case "aiops.tiering.record":
                    HandleTieringRecord(message);
                    break;
                case "aiops.tiering.stats":
                    HandleTieringStats(message);
                    break;

                // Scaling
                case "aiops.scaling.predict":
                    await HandleScalingPredictAsync(message);
                    break;
                case "aiops.scaling.recommend":
                    await HandleScalingRecommendAsync(message);
                    break;
                case "aiops.scaling.execute":
                    await HandleScalingExecuteAsync(message);
                    break;
                case "aiops.scaling.record":
                    HandleScalingRecord(message);
                    break;
                case "aiops.scaling.stats":
                    HandleScalingStats(message);
                    break;

                // Anomaly
                case "aiops.anomaly.detect":
                    await HandleAnomalyDetectAsync(message);
                    break;
                case "aiops.anomaly.analyze":
                    await HandleAnomalyAnalyzeAsync(message);
                    break;
                case "aiops.anomaly.remediate":
                    await HandleAnomalyRemediateAsync(message);
                    break;
                case "aiops.anomaly.incidents":
                    HandleAnomalyIncidents(message);
                    break;
                case "aiops.anomaly.stats":
                    HandleAnomalyStats(message);
                    break;

                // Cost
                case "aiops.cost.analyze":
                    await HandleCostAnalyzeAsync(message);
                    break;
                case "aiops.cost.optimize":
                    await HandleCostOptimizeAsync(message);
                    break;
                case "aiops.cost.record":
                    HandleCostRecord(message);
                    break;
                case "aiops.cost.stats":
                    HandleCostStats(message);
                    break;

                // Security
                case "aiops.security.analyze":
                    await HandleSecurityAnalyzeAsync(message);
                    break;
                case "aiops.security.respond":
                    await HandleSecurityRespondAsync(message);
                    break;
                case "aiops.security.incidents":
                    HandleSecurityIncidents(message);
                    break;
                case "aiops.security.block":
                    HandleSecurityBlock(message);
                    break;
                case "aiops.security.unblock":
                    HandleSecurityUnblock(message);
                    break;
                case "aiops.security.stats":
                    HandleSecurityStats(message);
                    break;

                // Compliance
                case "aiops.compliance.check":
                    await HandleComplianceCheckAsync(message);
                    break;
                case "aiops.compliance.enforce":
                    await HandleComplianceEnforceAsync(message);
                    break;
                case "aiops.compliance.report":
                    await HandleComplianceReportAsync(message);
                    break;
                case "aiops.compliance.violations":
                    HandleComplianceViolations(message);
                    break;
                case "aiops.compliance.stats":
                    HandleComplianceStats(message);
                    break;

                // Capacity
                case "aiops.capacity.forecast":
                    await HandleCapacityForecastAsync(message);
                    break;
                case "aiops.capacity.budget":
                    await HandleCapacityBudgetAsync(message);
                    break;
                case "aiops.capacity.record":
                    HandleCapacityRecord(message);
                    break;
                case "aiops.capacity.stats":
                    HandleCapacityStats(message);
                    break;

                // Performance
                case "aiops.performance.analyze":
                    await HandlePerformanceAnalyzeAsync(message);
                    break;
                case "aiops.performance.tune":
                    await HandlePerformanceTuneAsync(message);
                    break;
                case "aiops.performance.rollback":
                    await HandlePerformanceRollbackAsync(message);
                    break;
                case "aiops.performance.record":
                    HandlePerformanceRecord(message);
                    break;
                case "aiops.performance.query":
                    HandlePerformanceQuery(message);
                    break;
                case "aiops.performance.stats":
                    HandlePerformanceStats(message);
                    break;

                // Provider Management
                case "aiops.provider.configure":
                    HandleProviderConfigure(message);
                    break;
                case "aiops.provider.list":
                    HandleProviderList(message);
                    break;
                case "aiops.provider.stats":
                    HandleProviderStats(message);
                    break;

                // Overall
                case "aiops.stats":
                    HandleOverallStats(message);
                    break;

                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }
        catch (Exception ex)
        {
            message.Payload["error"] = ex.Message;
            message.Payload["success"] = false;
        }
    }

    #region Tiering Handlers

    private async Task HandleTieringOptimizeAsync(PluginMessage message)
    {
        var result = await _tieringEngine.OptimizationCycleAsync(_cts.Token);
        message.Payload["result"] = new
        {
            startTime = result.StartTime,
            endTime = result.EndTime,
            duration = result.Duration.TotalSeconds,
            resourcesEvaluated = result.ResourcesEvaluated,
            resourcesMigrated = result.ResourcesMigrated,
            errors = result.Errors
        };
        message.Payload["success"] = true;
    }

    private async Task HandleTieringRecommendAsync(PluginMessage message)
    {
        var resourceId = GetString(message.Payload, "resourceId");
        if (string.IsNullOrEmpty(resourceId))
        {
            message.Payload["error"] = "resourceId required";
            message.Payload["success"] = false;
            return;
        }

        var recommendation = await _tieringEngine.GetRecommendationAsync(resourceId, _cts.Token);
        message.Payload["result"] = new
        {
            resourceId = recommendation.ResourceId,
            recommendedTier = recommendation.RecommendedTier.ToString(),
            confidence = recommendation.Confidence,
            source = recommendation.Source.ToString(),
            reason = recommendation.Reason,
            estimatedCostSavings = recommendation.EstimatedCostSavings,
            aiInsights = recommendation.AIInsights
        };
        message.Payload["success"] = true;
    }

    private async Task HandleTieringMigrateAsync(PluginMessage message)
    {
        var resourceId = GetString(message.Payload, "resourceId");
        var tierStr = GetString(message.Payload, "targetTier");

        if (string.IsNullOrEmpty(resourceId))
        {
            message.Payload["error"] = "resourceId required";
            message.Payload["success"] = false;
            return;
        }

        var targetTier = ParseStorageTier(tierStr);
        var result = await _tieringEngine.MigrateAsync(resourceId, targetTier, _cts.Token);

        message.Payload["result"] = new
        {
            resourceId = result.ResourceId,
            success = result.Success,
            previousTier = result.PreviousTier.ToString(),
            newTier = result.NewTier.ToString(),
            reason = result.Reason
        };
        message.Payload["success"] = result.Success;
    }

    private void HandleTieringRecord(PluginMessage message)
    {
        var resourceId = GetString(message.Payload, "resourceId");
        var eventTypeStr = GetString(message.Payload, "eventType") ?? "read";
        var sizeBytes = GetLong(message.Payload, "sizeBytes") ?? 0;

        if (string.IsNullOrEmpty(resourceId))
        {
            message.Payload["error"] = "resourceId required";
            message.Payload["success"] = false;
            return;
        }

        var eventType = eventTypeStr.ToLowerInvariant() switch
        {
            "write" => AccessEventType.Write,
            "delete" => AccessEventType.Delete,
            "list" => AccessEventType.List,
            _ => AccessEventType.Read
        };

        _tieringEngine.RecordAccess(resourceId, eventType, sizeBytes);
        message.Payload["result"] = new { recorded = true, resourceId, eventType = eventType.ToString() };
        message.Payload["success"] = true;
    }

    private void HandleTieringStats(PluginMessage message)
    {
        var stats = _tieringEngine.GetStatistics();
        message.Payload["result"] = new
        {
            totalTrackedResources = stats.TotalTrackedResources,
            totalAssignments = stats.TotalAssignments,
            tierDistribution = stats.TierDistribution.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            totalMigrations = stats.TotalMigrations,
            successfulMigrations = stats.SuccessfulMigrations,
            migrationSuccessRate = stats.MigrationSuccessRate
        };
        message.Payload["success"] = true;
    }

    #endregion

    #region Scaling Handlers

    private async Task HandleScalingPredictAsync(PluginMessage message)
    {
        var resourceId = GetString(message.Payload, "resourceId");
        var hours = GetInt(message.Payload, "hours") ?? 24;

        if (string.IsNullOrEmpty(resourceId))
        {
            message.Payload["error"] = "resourceId required";
            message.Payload["success"] = false;
            return;
        }

        var forecast = await _scalingEngine.ForecastAsync(resourceId, TimeSpan.FromHours(hours), _cts.Token);

        message.Payload["result"] = new
        {
            resourceId = forecast.ResourceId,
            forecastWindow = $"{forecast.ForecastWindow.TotalHours} hours",
            peakCpuForecast = forecast.PeakCpuForecast,
            peakMemoryForecast = forecast.PeakMemoryForecast,
            peakConnectionsForecast = forecast.PeakConnectionsForecast,
            cpuTrend = forecast.CpuTrend.ToString(),
            memoryTrend = forecast.MemoryTrend.ToString(),
            confidence = forecast.Confidence,
            source = forecast.Source.ToString(),
            reason = forecast.Reason
        };
        message.Payload["success"] = true;
    }

    private async Task HandleScalingRecommendAsync(PluginMessage message)
    {
        var resourceId = GetString(message.Payload, "resourceId");
        var maxCpu = GetInt(message.Payload, "maxCpu") ?? 100;
        var maxMemoryGB = GetDouble(message.Payload, "maxMemoryGB") ?? 16;
        var maxConnections = GetInt(message.Payload, "maxConnections") ?? 1000;

        if (string.IsNullOrEmpty(resourceId))
        {
            message.Payload["error"] = "resourceId required";
            message.Payload["success"] = false;
            return;
        }

        var currentCapacity = new ResourceCapacity
        {
            MaxCpu = maxCpu,
            MaxMemoryGB = maxMemoryGB,
            MaxConnections = maxConnections
        };

        var recommendation = await _scalingEngine.GetScalingRecommendationAsync(resourceId, currentCapacity, _cts.Token);

        message.Payload["result"] = new
        {
            resourceId = recommendation.ResourceId,
            action = recommendation.Action.ToString(),
            confidence = recommendation.Confidence,
            reason = recommendation.Reason,
            estimatedCostImpact = recommendation.EstimatedCostImpact
        };
        message.Payload["success"] = true;
    }

    private async Task HandleScalingExecuteAsync(PluginMessage message)
    {
        var resourceId = GetString(message.Payload, "resourceId");
        var actionStr = GetString(message.Payload, "action");
        var maxCpu = GetInt(message.Payload, "targetCpu") ?? 100;
        var maxMemoryGB = GetDouble(message.Payload, "targetMemoryGB") ?? 16;
        var maxConnections = GetInt(message.Payload, "targetConnections") ?? 1000;

        if (string.IsNullOrEmpty(resourceId) || string.IsNullOrEmpty(actionStr))
        {
            message.Payload["error"] = "resourceId and action required";
            message.Payload["success"] = false;
            return;
        }

        var action = Enum.TryParse<ScalingAction>(actionStr, true, out var a) ? a : ScalingAction.NoAction;
        var targetCapacity = new ResourceCapacity
        {
            MaxCpu = maxCpu,
            MaxMemoryGB = maxMemoryGB,
            MaxConnections = maxConnections
        };

        var result = await _scalingEngine.ExecuteScalingAsync(resourceId, action, targetCapacity, _cts.Token);

        message.Payload["result"] = new
        {
            resourceId = result.ResourceId,
            success = result.Success,
            action = result.Action.ToString(),
            executionTime = result.ExecutionTime.TotalSeconds,
            reason = result.Reason
        };
        message.Payload["success"] = result.Success;
    }

    private void HandleScalingRecord(PluginMessage message)
    {
        var resourceId = GetString(message.Payload, "resourceId");
        var cpuPercent = GetDouble(message.Payload, "cpuPercent") ?? 0;
        var memoryPercent = GetDouble(message.Payload, "memoryPercent") ?? 0;
        var diskPercent = GetDouble(message.Payload, "diskPercent") ?? 0;
        var networkIn = GetDouble(message.Payload, "networkInMbps") ?? 0;
        var networkOut = GetDouble(message.Payload, "networkOutMbps") ?? 0;
        var connections = GetInt(message.Payload, "activeConnections") ?? 0;

        if (string.IsNullOrEmpty(resourceId))
        {
            message.Payload["error"] = "resourceId required";
            message.Payload["success"] = false;
            return;
        }

        _scalingEngine.RecordMetrics(resourceId, new ResourceMetrics
        {
            ResourceId = resourceId,
            CpuPercent = cpuPercent,
            MemoryPercent = memoryPercent,
            DiskPercent = diskPercent,
            NetworkInMbps = networkIn,
            NetworkOutMbps = networkOut,
            ActiveConnections = connections
        });

        message.Payload["result"] = new { recorded = true, resourceId };
        message.Payload["success"] = true;
    }

    private void HandleScalingStats(PluginMessage message)
    {
        var stats = _scalingEngine.GetStatistics();
        message.Payload["result"] = new
        {
            trackedResources = stats.TrackedResources,
            totalScalingEvents = stats.TotalScalingEvents,
            scaleUpEvents = stats.ScaleUpEvents,
            scaleDownEvents = stats.ScaleDownEvents
        };
        message.Payload["success"] = true;
    }

    #endregion

    #region Anomaly Handlers

    private async Task HandleAnomalyDetectAsync(PluginMessage message)
    {
        var metricName = GetString(message.Payload, "metricName");
        var value = GetDouble(message.Payload, "value") ?? 0;

        if (string.IsNullOrEmpty(metricName))
        {
            message.Payload["error"] = "metricName required";
            message.Payload["success"] = false;
            return;
        }

        var tags = message.Payload.TryGetValue("tags", out var tagsObj) && tagsObj is Dictionary<string, object> t
            ? t : null;

        var result = await _anomalyEngine.RecordAndAnalyzeAsync(metricName, value, tags, _cts.Token);

        message.Payload["result"] = new
        {
            metricName = result.MetricName,
            value = result.Value,
            isAnomaly = result.IsAnomaly,
            anomaly = result.Anomaly != null ? new
            {
                severity = result.Anomaly.Severity.ToString(),
                type = result.Anomaly.Type.ToString(),
                score = result.Anomaly.Score
            } : null,
            remediation = result.Remediation != null ? new
            {
                actionName = result.Remediation.ActionName,
                autoExecute = result.Remediation.AutoExecute,
                requiresApproval = result.Remediation.RequiresApproval
            } : null
        };
        message.Payload["success"] = true;
    }

    private async Task HandleAnomalyAnalyzeAsync(PluginMessage message)
    {
        var metrics = message.Payload.TryGetValue("metrics", out var metricsObj) && metricsObj is Dictionary<string, object> m
            ? m.ToDictionary(kv => kv.Key, kv => Convert.ToDouble(kv.Value))
            : null;

        if (metrics == null || metrics.Count == 0)
        {
            message.Payload["error"] = "metrics dictionary required";
            message.Payload["success"] = false;
            return;
        }

        var result = await _anomalyEngine.AnalyzeMultivariateAsync(metrics, _cts.Token);

        message.Payload["result"] = new
        {
            isAnomaly = result.IsAnomaly,
            rootCauseAnalysis = result.RootCauseAnalysis,
            potentialCauses = result.PotentialCauses
        };
        message.Payload["success"] = true;
    }

    private async Task HandleAnomalyRemediateAsync(PluginMessage message)
    {
        var incidentId = GetString(message.Payload, "incidentId");
        var actionId = GetString(message.Payload, "actionId");
        var actionName = GetString(message.Payload, "actionName") ?? "Manual Remediation";

        if (string.IsNullOrEmpty(incidentId))
        {
            message.Payload["error"] = "incidentId required";
            message.Payload["success"] = false;
            return;
        }

        var recommendation = new RemediationRecommendation
        {
            IncidentId = incidentId,
            ActionId = actionId ?? Guid.NewGuid().ToString(),
            ActionName = actionName,
            AutoExecute = true,
            RequiresApproval = false
        };

        var result = await _anomalyEngine.ExecuteRemediationAsync(recommendation, _cts.Token);

        message.Payload["result"] = new
        {
            success = result.Success,
            executionTime = result.ExecutionTime.TotalSeconds,
            reason = result.Reason
        };
        message.Payload["success"] = result.Success;
    }

    private void HandleAnomalyIncidents(PluginMessage message)
    {
        var incidents = _anomalyEngine.GetActiveIncidents().Take(50).ToList();

        message.Payload["result"] = incidents.Select(i => new
        {
            id = i.Id,
            metricName = i.MetricName,
            anomalyType = i.AnomalyType.ToString(),
            severity = i.Severity.ToString(),
            status = i.Status.ToString(),
            occurrenceCount = i.OccurrenceCount,
            startTime = i.StartTime,
            lastOccurrence = i.LastOccurrence
        }).ToList();
        message.Payload["success"] = true;
    }

    private void HandleAnomalyStats(PluginMessage message)
    {
        var stats = _anomalyEngine.GetStatistics();
        message.Payload["result"] = new
        {
            activeIncidents = stats.ActiveIncidents,
            resolvedIncidents = stats.ResolvedIncidents,
            totalRemediations = stats.TotalRemediations,
            successfulRemediations = stats.SuccessfulRemediations,
            remediationSuccessRate = stats.RemediationSuccessRate
        };
        message.Payload["success"] = true;
    }

    #endregion

    #region Cost Handlers

    private async Task HandleCostAnalyzeAsync(PluginMessage message)
    {
        var days = GetInt(message.Payload, "days") ?? 30;
        var report = await _costEngine.AnalyzeAsync(TimeSpan.FromDays(days), _cts.Token);

        message.Payload["result"] = new
        {
            totalCost = report.TotalCost,
            projectedMonthlyCost = report.ProjectedMonthlyCost,
            costTrend = report.CostTrend.ToString(),
            totalPotentialSavings = report.TotalPotentialSavings,
            recommendationCount = report.Recommendations.Count,
            aiInsights = report.AIInsights,
            costByCategory = report.CostByCategory,
            costAnomalies = report.CostAnomalies.Count
        };
        message.Payload["success"] = true;
    }

    private async Task HandleCostOptimizeAsync(PluginMessage message)
    {
        var recommendationId = GetString(message.Payload, "recommendationId");
        var resourceId = GetString(message.Payload, "resourceId");
        var type = GetString(message.Payload, "type") ?? "Rightsize";
        var estimatedSavings = GetDouble(message.Payload, "estimatedSavings") ?? 0;

        var recommendation = new CostOptimizationRecommendation
        {
            Id = recommendationId ?? Guid.NewGuid().ToString(),
            ResourceId = resourceId ?? "unknown",
            Type = Enum.TryParse<RecommendationType>(type, out var t) ? t : RecommendationType.Rightsize,
            EstimatedSavings = estimatedSavings,
            Risk = RiskLevel.Low
        };

        var result = await _costEngine.ExecuteOptimizationAsync(recommendation, _cts.Token);

        message.Payload["result"] = new
        {
            success = result.Success,
            actualSavings = result.ActualSavings,
            reason = result.Reason
        };
        message.Payload["success"] = result.Success;
    }

    private void HandleCostRecord(PluginMessage message)
    {
        var resourceId = GetString(message.Payload, "resourceId");
        var amount = GetDouble(message.Payload, "amount") ?? 0;
        var category = GetString(message.Payload, "category") ?? "general";

        if (string.IsNullOrEmpty(resourceId))
        {
            message.Payload["error"] = "resourceId required";
            message.Payload["success"] = false;
            return;
        }

        _costEngine.RecordCost(resourceId, amount, category);
        message.Payload["result"] = new { recorded = true, resourceId, amount, category };
        message.Payload["success"] = true;
    }

    private void HandleCostStats(PluginMessage message)
    {
        var stats = _costEngine.GetStatistics();
        message.Payload["result"] = new
        {
            totalOptimizationsExecuted = stats.TotalOptimizationsExecuted,
            successfulOptimizations = stats.SuccessfulOptimizations,
            totalSavingsRealized = stats.TotalSavingsRealized
        };
        message.Payload["success"] = true;
    }

    #endregion

    #region Security Handlers

    private async Task HandleSecurityAnalyzeAsync(PluginMessage message)
    {
        var eventType = GetString(message.Payload, "eventType") ?? "unknown";
        var sourceIP = GetString(message.Payload, "sourceIP");
        var userId = GetString(message.Payload, "userId");
        var targetResource = GetString(message.Payload, "targetResource");
        var failedAttempts = GetInt(message.Payload, "failedAttempts") ?? 0;

        var securityEvent = new SecurityEvent
        {
            EventType = eventType,
            SourceIP = sourceIP,
            UserId = userId,
            TargetResource = targetResource,
            FailedAttempts = failedAttempts
        };

        var result = await _securityEngine.AnalyzeEventAsync(securityEvent, _cts.Token);

        message.Payload["result"] = new
        {
            eventId = result.EventId,
            isThreat = result.IsThreat,
            threatScore = result.ThreatScore,
            severity = result.Severity.ToString(),
            patternMatches = result.PatternMatches.Select(p => p.PatternName).ToList(),
            recommendedResponse = result.RecommendedResponse != null ? new
            {
                action = result.RecommendedResponse.Action.ToString(),
                target = result.RecommendedResponse.Target,
                autoExecute = result.RecommendedResponse.AutoExecute
            } : null
        };
        message.Payload["success"] = true;
    }

    private async Task HandleSecurityRespondAsync(PluginMessage message)
    {
        var responseId = GetString(message.Payload, "responseId") ?? Guid.NewGuid().ToString();
        var incidentId = GetString(message.Payload, "incidentId") ?? "";
        var actionStr = GetString(message.Payload, "action") ?? "Alert";
        var target = GetString(message.Payload, "target") ?? "";
        var durationMinutes = GetInt(message.Payload, "durationMinutes") ?? 60;

        var action = Enum.TryParse<SecurityAction>(actionStr, true, out var a) ? a : SecurityAction.Alert;

        var response = new SecurityResponseRecommendation
        {
            ResponseId = responseId,
            IncidentId = incidentId,
            Action = action,
            Target = target,
            Duration = TimeSpan.FromMinutes(durationMinutes),
            AutoExecute = true
        };

        var result = await _securityEngine.ExecuteResponseAsync(response, _cts.Token);

        message.Payload["result"] = new
        {
            success = result.Success,
            executionTime = result.ExecutionTime.TotalSeconds,
            reason = result.Reason
        };
        message.Payload["success"] = result.Success;
    }

    private void HandleSecurityIncidents(PluginMessage message)
    {
        var incidents = _securityEngine.GetActiveIncidents().Take(50).ToList();

        message.Payload["result"] = incidents.Select(i => new
        {
            id = i.Id,
            type = i.Type,
            sourceIP = i.SourceIP,
            userId = i.UserId,
            severity = i.Severity.ToString(),
            status = i.Status.ToString(),
            eventCount = i.EventCount,
            startTime = i.StartTime
        }).ToList();
        message.Payload["success"] = true;
    }

    private void HandleSecurityBlock(PluginMessage message)
    {
        var entityId = GetString(message.Payload, "entityId");
        var entityTypeStr = GetString(message.Payload, "entityType") ?? "IP";

        if (string.IsNullOrEmpty(entityId))
        {
            message.Payload["error"] = "entityId required";
            message.Payload["success"] = false;
            return;
        }

        var entityType = Enum.TryParse<EntityType>(entityTypeStr, true, out var t) ? t : EntityType.IP;
        var isBlocked = _securityEngine.IsBlocked(entityId, entityType);

        message.Payload["result"] = new { entityId, entityType = entityType.ToString(), isBlocked };
        message.Payload["success"] = true;
    }

    private void HandleSecurityUnblock(PluginMessage message)
    {
        var entityId = GetString(message.Payload, "entityId");
        var entityTypeStr = GetString(message.Payload, "entityType") ?? "IP";

        if (string.IsNullOrEmpty(entityId))
        {
            message.Payload["error"] = "entityId required";
            message.Payload["success"] = false;
            return;
        }

        var entityType = Enum.TryParse<EntityType>(entityTypeStr, true, out var t) ? t : EntityType.IP;
        var unblocked = _securityEngine.Unblock(entityId, entityType);

        message.Payload["result"] = new { entityId, entityType = entityType.ToString(), unblocked };
        message.Payload["success"] = unblocked;
    }

    private void HandleSecurityStats(PluginMessage message)
    {
        var stats = _securityEngine.GetStatistics();
        message.Payload["result"] = new
        {
            activeIncidents = stats.ActiveIncidents,
            mitigatedIncidents = stats.MitigatedIncidents,
            blockedEntities = stats.BlockedEntities,
            totalResponses = stats.TotalResponses,
            successfulResponses = stats.SuccessfulResponses,
            threatPatternCount = stats.ThreatPatternCount
        };
        message.Payload["success"] = true;
    }

    #endregion

    #region Compliance Handlers

    private async Task HandleComplianceCheckAsync(PluginMessage message)
    {
        var resourceId = GetString(message.Payload, "resourceId");
        var operation = GetString(message.Payload, "operation") ?? "access";
        var userId = GetString(message.Payload, "userId");
        var classificationStr = GetString(message.Payload, "dataClassification") ?? "Internal";
        var isEncrypted = GetBool(message.Payload, "isEncrypted") ?? false;
        var isMasked = GetBool(message.Payload, "isMasked") ?? false;

        if (string.IsNullOrEmpty(resourceId))
        {
            message.Payload["error"] = "resourceId required";
            message.Payload["success"] = false;
            return;
        }

        var classification = Enum.TryParse<DataClassification>(classificationStr, true, out var c)
            ? c : DataClassification.Internal;

        var request = new ComplianceCheckRequest
        {
            ResourceId = resourceId,
            Operation = operation,
            UserId = userId,
            DataClassification = classification,
            IsEncrypted = isEncrypted,
            IsMasked = isMasked
        };

        var result = await _complianceEngine.CheckComplianceAsync(request, _cts.Token);

        message.Payload["result"] = new
        {
            isCompliant = result.IsCompliant,
            checkedPolicies = result.CheckedPolicies,
            violations = result.Violations.Select(v => new
            {
                policyId = v.PolicyId,
                policyName = v.PolicyName,
                severity = v.Severity.ToString(),
                description = v.Description
            }).ToList(),
            highestSeverity = result.HighestSeverity.ToString(),
            enforcementRecommendation = result.EnforcementRecommendation != null ? new
            {
                action = result.EnforcementRecommendation.Action.ToString(),
                autoExecute = result.EnforcementRecommendation.AutoExecute
            } : null
        };
        message.Payload["success"] = true;
    }

    private async Task HandleComplianceEnforceAsync(PluginMessage message)
    {
        var violationId = GetString(message.Payload, "violationId") ?? Guid.NewGuid().ToString();
        var actionStr = GetString(message.Payload, "action") ?? "NotifyAdmin";
        var targetResource = GetString(message.Payload, "targetResource") ?? "";

        var action = Enum.TryParse<EnforcementActionType>(actionStr, true, out var a)
            ? a : EnforcementActionType.NotifyAdmin;

        var recommendation = new EnforcementRecommendation
        {
            Id = Guid.NewGuid().ToString(),
            ViolationId = violationId,
            Action = action,
            TargetResource = targetResource,
            AutoExecute = true
        };

        var result = await _complianceEngine.ExecuteEnforcementAsync(recommendation, _cts.Token);

        message.Payload["result"] = new
        {
            success = result.Success,
            executionTime = result.ExecutionTime.TotalSeconds,
            reason = result.Reason
        };
        message.Payload["success"] = result.Success;
    }

    private async Task HandleComplianceReportAsync(PluginMessage message)
    {
        var days = GetInt(message.Payload, "days") ?? 30;
        var report = await _complianceEngine.GenerateReportAsync(TimeSpan.FromDays(days), _cts.Token);

        message.Payload["result"] = new
        {
            generatedAt = report.GeneratedAt,
            totalPolicies = report.TotalPolicies,
            enabledPolicies = report.EnabledPolicies,
            totalChecks = report.TotalChecks,
            passedChecks = report.PassedChecks,
            failedChecks = report.FailedChecks,
            complianceRate = report.ComplianceRate,
            activeViolations = report.ActiveViolations.Count,
            remediatedViolations = report.RemediatedViolations.Count,
            aiAssessment = report.AIAssessment
        };
        message.Payload["success"] = true;
    }

    private void HandleComplianceViolations(PluginMessage message)
    {
        var violations = _complianceEngine.GetActiveViolations().Take(50).ToList();

        message.Payload["result"] = violations.Select(v => new
        {
            id = v.Id,
            resourceId = v.ResourceId,
            severity = v.Severity.ToString(),
            status = v.Status.ToString(),
            occurrenceCount = v.OccurrenceCount,
            detectedTime = v.DetectedTime,
            policyViolations = v.PolicyViolations.Select(pv => pv.PolicyName).ToList()
        }).ToList();
        message.Payload["success"] = true;
    }

    private void HandleComplianceStats(PluginMessage message)
    {
        var stats = _complianceEngine.GetStatistics();
        message.Payload["result"] = new
        {
            totalPolicies = stats.TotalPolicies,
            enabledPolicies = stats.EnabledPolicies,
            activeViolations = stats.ActiveViolations,
            remediatedViolations = stats.RemediatedViolations,
            totalEnforcements = stats.TotalEnforcements,
            successfulEnforcements = stats.SuccessfulEnforcements,
            auditTrailSize = stats.AuditTrailSize
        };
        message.Payload["success"] = true;
    }

    #endregion

    #region Capacity Handlers

    private async Task HandleCapacityForecastAsync(PluginMessage message)
    {
        var resourceType = GetString(message.Payload, "resourceType") ?? "default";
        var days = GetInt(message.Payload, "days") ?? 30;

        var forecast = await _capacityEngine.ForecastAsync(resourceType, days, _cts.Token);

        message.Payload["result"] = new
        {
            resourceType = forecast.ResourceType,
            forecastDays = forecast.ForecastDays,
            currentStorageUtilization = forecast.CurrentStorageUtilization,
            currentComputeUtilization = forecast.CurrentComputeUtilization,
            storageGrowthRatePerDay = forecast.StorageGrowthRatePerDay,
            computeGrowthRatePerDay = forecast.ComputeGrowthRatePerDay,
            projectedStorageUtilization = forecast.ProjectedStorageUtilization,
            projectedComputeUtilization = forecast.ProjectedComputeUtilization,
            storageExhaustionDays = forecast.StorageExhaustionDays,
            computeExhaustionDays = forecast.ComputeExhaustionDays,
            recommendationCount = forecast.Recommendations.Count,
            confidence = forecast.Confidence,
            reason = forecast.Reason
        };
        message.Payload["success"] = true;
    }

    private async Task HandleCapacityBudgetAsync(PluginMessage message)
    {
        var resourceType = GetString(message.Payload, "resourceType") ?? "default";
        var months = GetInt(message.Payload, "months") ?? 12;
        var storageRate = GetDouble(message.Payload, "storagePerGBMonth") ?? 0.023;
        var computeRate = GetDouble(message.Payload, "computePerUnitMonth") ?? 100;
        var networkRate = GetDouble(message.Payload, "networkPerMbpsMonth") ?? 0.09;

        var costRates = new CostRates
        {
            StoragePerGBMonth = storageRate,
            ComputePerUnitMonth = computeRate,
            NetworkPerMbpsMonth = networkRate
        };

        var budget = await _capacityEngine.ProjectBudgetAsync(resourceType, months, costRates, _cts.Token);

        message.Payload["result"] = new
        {
            resourceType = budget.ResourceType,
            forecastMonths = budget.ForecastMonths,
            totalProjectedBudget = budget.TotalProjectedBudget,
            averageMonthlyCost = budget.AverageMonthlyCost,
            costGrowthRate = budget.CostGrowthRate,
            confidence = budget.Confidence,
            reason = budget.Reason
        };
        message.Payload["success"] = true;
    }

    private void HandleCapacityRecord(PluginMessage message)
    {
        var resourceType = GetString(message.Payload, "resourceType") ?? "default";
        var storageUsedGB = GetDouble(message.Payload, "storageUsedGB") ?? 0;
        var storageTotalGB = GetDouble(message.Payload, "storageTotalGB") ?? 100;
        var computeUtilization = GetDouble(message.Payload, "computeUtilization") ?? 0;
        var networkBandwidth = GetDouble(message.Payload, "networkBandwidthMbps") ?? 0;

        _capacityEngine.RecordMetrics(resourceType, new CapacityMetrics
        {
            StorageUsedGB = storageUsedGB,
            StorageTotalGB = storageTotalGB,
            ComputeUtilization = computeUtilization,
            NetworkBandwidthMbps = networkBandwidth
        });

        message.Payload["result"] = new { recorded = true, resourceType };
        message.Payload["success"] = true;
    }

    private void HandleCapacityStats(PluginMessage message)
    {
        var stats = _capacityEngine.GetStatistics();
        message.Payload["result"] = new
        {
            trackedResourceTypes = stats.TrackedResourceTypes,
            totalDataPoints = stats.TotalDataPoints
        };
        message.Payload["success"] = true;
    }

    #endregion

    #region Performance Handlers

    private async Task HandlePerformanceAnalyzeAsync(PluginMessage message)
    {
        var componentId = GetString(message.Payload, "componentId");

        if (string.IsNullOrEmpty(componentId))
        {
            message.Payload["error"] = "componentId required";
            message.Payload["success"] = false;
            return;
        }

        var analysis = await _performanceEngine.AnalyzeAsync(componentId, _cts.Token);

        message.Payload["result"] = new
        {
            componentId = analysis.ComponentId,
            hasSufficientData = analysis.HasSufficientData,
            averageLatencyMs = analysis.AverageLatencyMs,
            p95LatencyMs = analysis.P95LatencyMs,
            p99LatencyMs = analysis.P99LatencyMs,
            averageThroughput = analysis.AverageThroughput,
            averageErrorRate = analysis.AverageErrorRate,
            performanceScore = analysis.PerformanceScore,
            bottlenecks = analysis.Bottlenecks.Select(b => new
            {
                type = b.Type.ToString(),
                severity = b.Severity.ToString(),
                currentValue = b.CurrentValue,
                threshold = b.Threshold,
                description = b.Description
            }).ToList(),
            recommendationCount = analysis.Recommendations.Count,
            queryOptimizationCount = analysis.QueryOptimizations.Count,
            reason = analysis.Reason
        };
        message.Payload["success"] = true;
    }

    private async Task HandlePerformanceTuneAsync(PluginMessage message)
    {
        var componentId = GetString(message.Payload, "componentId");
        var recommendationId = GetString(message.Payload, "recommendationId") ?? Guid.NewGuid().ToString();
        var category = GetString(message.Payload, "category") ?? "General";
        var title = GetString(message.Payload, "title") ?? "Manual Tuning";

        if (string.IsNullOrEmpty(componentId))
        {
            message.Payload["error"] = "componentId required";
            message.Payload["success"] = false;
            return;
        }

        var parameters = message.Payload.TryGetValue("parameters", out var paramsObj) && paramsObj is Dictionary<string, object> p
            ? p : new Dictionary<string, object>();

        var recommendation = new TuningRecommendation
        {
            Id = recommendationId,
            ComponentId = componentId,
            Category = category,
            Title = title,
            Parameters = parameters,
            AutoApply = true
        };

        var result = await _performanceEngine.ApplyTuningAsync(recommendation, _cts.Token);

        message.Payload["result"] = new
        {
            success = result.Success,
            executionTime = result.ExecutionTime.TotalSeconds,
            appliedParameters = result.AppliedParameters,
            reason = result.Reason
        };
        message.Payload["success"] = result.Success;
    }

    private async Task HandlePerformanceRollbackAsync(PluginMessage message)
    {
        var eventId = GetString(message.Payload, "eventId");

        if (string.IsNullOrEmpty(eventId))
        {
            message.Payload["error"] = "eventId required";
            message.Payload["success"] = false;
            return;
        }

        var result = await _performanceEngine.RollbackTuningAsync(eventId, _cts.Token);

        message.Payload["result"] = new
        {
            success = result.Success,
            reason = result.Reason
        };
        message.Payload["success"] = result.Success;
    }

    private void HandlePerformanceRecord(PluginMessage message)
    {
        var componentId = GetString(message.Payload, "componentId");
        var latencyMs = GetDouble(message.Payload, "latencyMs") ?? 0;
        var throughput = GetDouble(message.Payload, "throughputOpsPerSec") ?? 0;
        var cpuPercent = GetDouble(message.Payload, "cpuPercent") ?? 0;
        var memoryPercent = GetDouble(message.Payload, "memoryPercent") ?? 0;
        var diskIOPercent = GetDouble(message.Payload, "diskIOPercent") ?? 0;
        var networkIOPercent = GetDouble(message.Payload, "networkIOPercent") ?? 0;
        var errorRate = GetDouble(message.Payload, "errorRate") ?? 0;

        if (string.IsNullOrEmpty(componentId))
        {
            message.Payload["error"] = "componentId required";
            message.Payload["success"] = false;
            return;
        }

        _performanceEngine.RecordMetrics(componentId, new PerformanceMetrics
        {
            LatencyMs = latencyMs,
            ThroughputOpsPerSec = throughput,
            CpuPercent = cpuPercent,
            MemoryPercent = memoryPercent,
            DiskIOPercent = diskIOPercent,
            NetworkIOPercent = networkIOPercent,
            ErrorRate = errorRate
        });

        message.Payload["result"] = new { recorded = true, componentId };
        message.Payload["success"] = true;
    }

    private void HandlePerformanceQuery(PluginMessage message)
    {
        var queryHash = GetString(message.Payload, "queryHash");
        var queryText = GetString(message.Payload, "queryText");
        var executionTimeMs = GetLong(message.Payload, "executionTimeMs") ?? 0;
        var rowsScanned = GetLong(message.Payload, "rowsScanned") ?? 0;
        var rowsReturned = GetLong(message.Payload, "rowsReturned") ?? 0;
        var bytesRead = GetLong(message.Payload, "bytesRead") ?? 0;

        if (string.IsNullOrEmpty(queryHash))
        {
            message.Payload["error"] = "queryHash required";
            message.Payload["success"] = false;
            return;
        }

        _performanceEngine.RecordQuery(queryHash, new QueryExecutionInfo
        {
            QueryText = queryText,
            ExecutionTimeMs = executionTimeMs,
            RowsScanned = rowsScanned,
            RowsReturned = rowsReturned,
            BytesRead = bytesRead
        });

        message.Payload["result"] = new { recorded = true, queryHash };
        message.Payload["success"] = true;
    }

    private void HandlePerformanceStats(PluginMessage message)
    {
        var stats = _performanceEngine.GetStatistics();
        message.Payload["result"] = new
        {
            trackedComponents = stats.TrackedComponents,
            totalTuningEvents = stats.TotalTuningEvents,
            successfulTunings = stats.SuccessfulTunings,
            queryPerformance = new
            {
                totalQueries = stats.QueryPerformance.TotalQueries,
                uniqueQueryCount = stats.QueryPerformance.UniqueQueryCount,
                slowQueryCount = stats.QueryPerformance.SlowQueryCount,
                averageExecutionTimeMs = stats.QueryPerformance.AverageExecutionTimeMs
            }
        };
        message.Payload["success"] = true;
    }

    #endregion

    #region Provider Handlers

    private void HandleProviderConfigure(PluginMessage message)
    {
        // Provider configuration would typically be done through the registry
        // This handler allows runtime capability mapping updates
        message.Payload["result"] = new { configured = true };
        message.Payload["success"] = true;
    }

    private void HandleProviderList(PluginMessage message)
    {
        var providers = _providerSelector.GetAvailableProviders().ToList();

        message.Payload["result"] = providers.Select(p => new
        {
            providerId = p.ProviderId,
            displayName = p.DisplayName,
            capabilities = p.Capabilities.ToString(),
            isDefault = p.IsDefault,
            preferredModel = p.PreferredModel
        }).ToList();
        message.Payload["success"] = true;
    }

    private void HandleProviderStats(PluginMessage message)
    {
        var stats = _providerSelector.GetUsageStats().ToList();

        message.Payload["result"] = stats.Select(s => new
        {
            providerId = s.ProviderId,
            totalRequests = s.TotalRequests,
            successfulRequests = s.SuccessfulRequests,
            failedRequests = s.FailedRequests,
            successRate = s.SuccessRate,
            averageLatencyMs = s.AverageLatencyMs,
            totalTokens = s.TotalTokens
        }).ToList();
        message.Payload["success"] = true;
    }

    #endregion

    #region Overall Stats

    private void HandleOverallStats(PluginMessage message)
    {
        var tieringStats = _tieringEngine.GetStatistics();
        var scalingStats = _scalingEngine.GetStatistics();
        var anomalyStats = _anomalyEngine.GetStatistics();
        var costStats = _costEngine.GetStatistics();
        var securityStats = _securityEngine.GetStatistics();
        var complianceStats = _complianceEngine.GetStatistics();
        var capacityStats = _capacityEngine.GetStatistics();
        var performanceStats = _performanceEngine.GetStatistics();
        var providerStats = _providerSelector.GetUsageStats().ToList();

        message.Payload["result"] = new
        {
            tiering = new
            {
                trackedResources = tieringStats.TotalTrackedResources,
                totalMigrations = tieringStats.TotalMigrations,
                migrationSuccessRate = tieringStats.MigrationSuccessRate
            },
            scaling = new
            {
                trackedResources = scalingStats.TrackedResources,
                totalScalingEvents = scalingStats.TotalScalingEvents
            },
            anomaly = new
            {
                activeIncidents = anomalyStats.ActiveIncidents,
                remediationSuccessRate = anomalyStats.RemediationSuccessRate
            },
            cost = new
            {
                totalOptimizations = costStats.TotalOptimizationsExecuted,
                totalSavingsRealized = costStats.TotalSavingsRealized
            },
            security = new
            {
                activeIncidents = securityStats.ActiveIncidents,
                blockedEntities = securityStats.BlockedEntities
            },
            compliance = new
            {
                activeViolations = complianceStats.ActiveViolations,
                totalPolicies = complianceStats.TotalPolicies
            },
            capacity = new
            {
                trackedResourceTypes = capacityStats.TrackedResourceTypes,
                totalDataPoints = capacityStats.TotalDataPoints
            },
            performance = new
            {
                trackedComponents = performanceStats.TrackedComponents,
                totalTuningEvents = performanceStats.TotalTuningEvents
            },
            providers = new
            {
                totalProviders = providerStats.Count,
                totalRequests = providerStats.Sum(s => s.TotalRequests),
                totalTokens = providerStats.Sum(s => s.TotalTokens)
            }
        };
        message.Payload["success"] = true;
    }

    #endregion

    #region Helpers

    private static string? GetString(Dictionary<string, object> payload, string key)
        => payload.TryGetValue(key, out var val) && val is string s ? s : null;

    private static int? GetInt(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is double d) return (int)d;
            if (val is string s && int.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    private static long? GetLong(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is long l) return l;
            if (val is int i) return i;
            if (val is double d) return (long)d;
            if (val is string s && long.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    private static double? GetDouble(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is double d) return d;
            if (val is float f) return f;
            if (val is int i) return i;
            if (val is long l) return l;
            if (val is string s && double.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    private static bool? GetBool(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is bool b) return b;
            if (val is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    private static StorageTier ParseStorageTier(string? tier)
    {
        return tier?.ToLowerInvariant() switch
        {
            "hot" => StorageTier.Hot,
            "warm" => StorageTier.Warm,
            "standard" => StorageTier.Standard,
            "cool" => StorageTier.Cool,
            "archive" => StorageTier.Archive,
            _ => StorageTier.Standard
        };
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "aiops.tiering.optimize", DisplayName = "Tiering Optimization", Description = "ML-driven data placement optimization" },
            new() { Name = "aiops.scaling.predict", DisplayName = "Predictive Scaling", Description = "AI-powered capacity forecasting" },
            new() { Name = "aiops.anomaly.detect", DisplayName = "Anomaly Detection", Description = "Detect and auto-remediate anomalies" },
            new() { Name = "aiops.cost.analyze", DisplayName = "Cost Analysis", Description = "AI-driven cost optimization" },
            new() { Name = "aiops.security.analyze", DisplayName = "Security Analysis", Description = "Threat detection and auto-response" },
            new() { Name = "aiops.compliance.check", DisplayName = "Compliance Check", Description = "Policy enforcement automation" },
            new() { Name = "aiops.capacity.forecast", DisplayName = "Capacity Planning", Description = "Future capacity forecasting" },
            new() { Name = "aiops.performance.analyze", DisplayName = "Performance Tuning", Description = "Auto-adjust system settings" },
            new() { Name = "aiops.provider.list", DisplayName = "Provider Management", Description = "Multi-provider AI configuration" }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["EngineCount"] = 8;
        metadata["AIProviderAgnostic"] = true;
        metadata["MultiProviderSupport"] = true;
        return metadata;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        _tieringEngine.Dispose();
        _scalingEngine.Dispose();
        _anomalyEngine.Dispose();
        _costEngine.Dispose();
        _securityEngine.Dispose();
        _complianceEngine.Dispose();
        _capacityEngine.Dispose();
        _performanceEngine.Dispose();
    }
}

#region Configuration

/// <summary>
/// Main configuration for the AIOps plugin.
/// </summary>
public sealed class AIOpsConfig
{
    public AIOpsProviderConfig ProviderConfig { get; set; } = new();
    public TieringEngineConfig TieringConfig { get; set; } = new();
    public ScalingEngineConfig ScalingConfig { get; set; } = new();
    public AnomalyEngineConfig AnomalyConfig { get; set; } = new();
    public CostEngineConfig CostConfig { get; set; } = new();
    public SecurityEngineConfig SecurityConfig { get; set; } = new();
    public ComplianceEngineConfig ComplianceConfig { get; set; } = new();
    public CapacityEngineConfig CapacityConfig { get; set; } = new();
    public PerformanceEngineConfig PerformanceConfig { get; set; } = new();
}

#endregion
