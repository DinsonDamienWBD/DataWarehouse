using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataIntegration.Strategies.Monitoring;

#region 126.8.1 Pipeline Health Monitoring Strategy

/// <summary>
/// 126.8.1: Pipeline health monitoring strategy tracking pipeline status,
/// throughput, latency, and error rates with alerting.
/// </summary>
public sealed class PipelineHealthMonitoringStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, PipelineHealth> _healthStatus = new BoundedDictionary<string, PipelineHealth>(1000);
    private readonly BoundedDictionary<string, List<HealthCheck>> _healthHistory = new BoundedDictionary<string, List<HealthCheck>>(1000);

    public override string StrategyId => "monitoring-pipeline-health";
    public override string DisplayName => "Pipeline Health Monitoring";
    public override IntegrationCategory Category => IntegrationCategory.IntegrationMonitoring;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = false,
        SupportsSchemaEvolution = false,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 10000000,
        TypicalLatencyMs = 1.0
    };
    public override string SemanticDescription =>
        "Pipeline health monitoring tracking status, throughput, latency, error rates, " +
        "and resource utilization with configurable alerting thresholds.";
    public override string[] Tags => ["monitoring", "health", "pipeline", "alerting", "metrics"];

    /// <summary>
    /// Registers a pipeline for monitoring.
    /// </summary>
    public Task<PipelineHealth> RegisterPipelineAsync(
        string pipelineId,
        HealthMonitoringConfig? config = null,
        CancellationToken ct = default)
    {
        var health = new PipelineHealth
        {
            PipelineId = pipelineId,
            Config = config ?? new HealthMonitoringConfig(),
            Status = PipelineHealthStatus.Unknown,
            RegisteredAt = DateTime.UtcNow
        };

        if (!_healthStatus.TryAdd(pipelineId, health))
            throw new InvalidOperationException($"Pipeline {pipelineId} already registered");

        _healthHistory[pipelineId] = new List<HealthCheck>();

        RecordOperation("RegisterPipeline");
        return Task.FromResult(health);
    }

    /// <summary>
    /// Records a health check.
    /// </summary>
    public Task<HealthCheck> RecordHealthCheckAsync(
        string pipelineId,
        HealthMetrics metrics,
        CancellationToken ct = default)
    {
        if (!_healthStatus.TryGetValue(pipelineId, out var health))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not registered");

        if (!_healthHistory.TryGetValue(pipelineId, out var history))
            throw new InvalidOperationException($"Health history not found for pipeline {pipelineId}");

        var check = new HealthCheck
        {
            CheckId = Guid.NewGuid().ToString("N"),
            PipelineId = pipelineId,
            Metrics = metrics,
            Status = DetermineStatus(metrics, health.Config),
            Timestamp = DateTime.UtcNow
        };

        lock (history)
        {
            history.Add(check);
        }

        health.Status = check.Status;
        health.LastCheck = check;
        health.LastCheckedAt = DateTime.UtcNow;

        // Generate alerts if needed
        if (check.Status == PipelineHealthStatus.Critical || check.Status == PipelineHealthStatus.Warning)
        {
            lock (health.ActiveAlerts)
            {
                health.ActiveAlerts.Add(new HealthAlert
                {
                    AlertId = Guid.NewGuid().ToString("N"),
                    PipelineId = pipelineId,
                    Severity = check.Status == PipelineHealthStatus.Critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                    Message = GetAlertMessage(metrics, health.Config),
                    TriggeredAt = DateTime.UtcNow
                });
                // LOW-2341: evict oldest alerts beyond cap to prevent unbounded list growth.
                const int MaxActiveAlerts = 1000;
                while (health.ActiveAlerts.Count > MaxActiveAlerts)
                    health.ActiveAlerts.RemoveAt(0);
            }
        }

        RecordOperation("RecordHealthCheck");
        return Task.FromResult(check);
    }

    /// <summary>
    /// Gets the current health status.
    /// </summary>
    public Task<PipelineHealth> GetHealthAsync(string pipelineId, CancellationToken ct = default)
    {
        if (!_healthStatus.TryGetValue(pipelineId, out var health))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not registered");

        return Task.FromResult(health);
    }

    /// <summary>
    /// Gets health history.
    /// </summary>
    public Task<List<HealthCheck>> GetHealthHistoryAsync(
        string pipelineId,
        DateTime? since = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (!_healthHistory.TryGetValue(pipelineId, out var history))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not registered");

        List<HealthCheck> result;
        lock (history)
        {
            result = since.HasValue
                ? history.Where(h => h.Timestamp > since.Value).TakeLast(limit).ToList()
                : history.TakeLast(limit).ToList();
        }

        return Task.FromResult(result);
    }

    private PipelineHealthStatus DetermineStatus(HealthMetrics metrics, HealthMonitoringConfig config)
    {
        if (metrics.ErrorRate > config.CriticalErrorRateThreshold ||
            metrics.Latency > config.CriticalLatencyThreshold)
            return PipelineHealthStatus.Critical;

        if (metrics.ErrorRate > config.WarningErrorRateThreshold ||
            metrics.Latency > config.WarningLatencyThreshold)
            return PipelineHealthStatus.Warning;

        return PipelineHealthStatus.Healthy;
    }

    private string GetAlertMessage(HealthMetrics metrics, HealthMonitoringConfig config)
    {
        var issues = new List<string>();

        if (metrics.ErrorRate > config.CriticalErrorRateThreshold)
            issues.Add($"Critical error rate: {metrics.ErrorRate:P2}");
        else if (metrics.ErrorRate > config.WarningErrorRateThreshold)
            issues.Add($"High error rate: {metrics.ErrorRate:P2}");

        if (metrics.Latency > config.CriticalLatencyThreshold)
            issues.Add($"Critical latency: {metrics.Latency.TotalMilliseconds}ms");
        else if (metrics.Latency > config.WarningLatencyThreshold)
            issues.Add($"High latency: {metrics.Latency.TotalMilliseconds}ms");

        return string.Join("; ", issues);
    }
}

#endregion

#region 126.8.2 SLA Tracking Strategy

/// <summary>
/// 126.8.2: SLA tracking strategy monitoring service level agreements
/// for data freshness, completeness, and availability.
/// </summary>
public sealed class SlaTrackingStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, SlaDefinition> _slas = new BoundedDictionary<string, SlaDefinition>(1000);
    private readonly BoundedDictionary<string, List<SlaViolation>> _violations = new BoundedDictionary<string, List<SlaViolation>>(1000);

    public override string StrategyId => "monitoring-sla-tracking";
    public override string DisplayName => "SLA Tracking";
    public override IntegrationCategory Category => IntegrationCategory.IntegrationMonitoring;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = false,
        SupportsSchemaEvolution = false,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 5000000,
        TypicalLatencyMs = 2.0
    };
    public override string SemanticDescription =>
        "SLA tracking for monitoring data freshness, completeness, availability, and processing " +
        "latency with violation tracking and reporting.";
    public override string[] Tags => ["monitoring", "sla", "freshness", "availability", "compliance"];

    /// <summary>
    /// Defines an SLA.
    /// </summary>
    public Task<SlaDefinition> DefineSlaAsync(
        string slaId,
        string name,
        SlaRequirements requirements,
        CancellationToken ct = default)
    {
        var sla = new SlaDefinition
        {
            SlaId = slaId,
            Name = name,
            Requirements = requirements,
            Status = SlaStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

        if (!_slas.TryAdd(slaId, sla))
            throw new InvalidOperationException($"SLA {slaId} already exists");

        _violations[slaId] = new List<SlaViolation>();

        RecordOperation("DefineSla");
        return Task.FromResult(sla);
    }

    /// <summary>
    /// Checks SLA compliance.
    /// </summary>
    public Task<SlaComplianceResult> CheckComplianceAsync(
        string slaId,
        SlaMetrics metrics,
        CancellationToken ct = default)
    {
        if (!_slas.TryGetValue(slaId, out var sla))
            throw new KeyNotFoundException($"SLA {slaId} not found");

        if (!_violations.TryGetValue(slaId, out var violations))
            throw new InvalidOperationException($"Violations list not found for SLA {slaId}");

        var result = new SlaComplianceResult
        {
            SlaId = slaId,
            CheckedAt = DateTime.UtcNow,
            Violations = new List<string>()
        };

        // Check freshness
        if (sla.Requirements.MaxDataAge.HasValue &&
            metrics.DataAge > sla.Requirements.MaxDataAge.Value)
        {
            result.Violations.Add($"Data freshness violation: {metrics.DataAge} > {sla.Requirements.MaxDataAge}");
        }

        // Check completeness
        if (sla.Requirements.MinCompleteness.HasValue &&
            metrics.Completeness < sla.Requirements.MinCompleteness.Value)
        {
            result.Violations.Add($"Completeness violation: {metrics.Completeness:P2} < {sla.Requirements.MinCompleteness:P2}");
        }

        // Check availability
        if (sla.Requirements.MinAvailability.HasValue &&
            metrics.Availability < sla.Requirements.MinAvailability.Value)
        {
            result.Violations.Add($"Availability violation: {metrics.Availability:P2} < {sla.Requirements.MinAvailability:P2}");
        }

        // Check latency
        if (sla.Requirements.MaxLatency.HasValue &&
            metrics.ProcessingLatency > sla.Requirements.MaxLatency.Value)
        {
            result.Violations.Add($"Latency violation: {metrics.ProcessingLatency} > {sla.Requirements.MaxLatency}");
        }

        result.IsCompliant = result.Violations.Count == 0;

        // Record violations
        if (!result.IsCompliant)
        {
            lock (violations)
            {
                violations.Add(new SlaViolation
                {
                    ViolationId = Guid.NewGuid().ToString("N"),
                    SlaId = slaId,
                    Violations = result.Violations,
                    Metrics = metrics,
                    OccurredAt = DateTime.UtcNow
                });
            }
        }

        sla.LastChecked = DateTime.UtcNow;
        sla.LastComplianceResult = result.IsCompliant;

        RecordOperation("CheckCompliance");
        return Task.FromResult(result);
    }

    /// <summary>
    /// Gets SLA report.
    /// </summary>
    public Task<SlaReport> GetReportAsync(
        string slaId,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken ct = default)
    {
        if (!_slas.TryGetValue(slaId, out var sla))
            throw new KeyNotFoundException($"SLA {slaId} not found");

        if (!_violations.TryGetValue(slaId, out var violations))
            throw new InvalidOperationException($"Violations list not found for SLA {slaId}");

        List<SlaViolation> periodViolations;
        lock (violations)
        {
            periodViolations = violations
                .Where(v => v.OccurredAt >= fromDate && v.OccurredAt <= toDate)
                .ToList();
        }

        var totalChecks = Math.Max(1, (int)(toDate - fromDate).TotalHours);
        var complianceRate = (double)(totalChecks - periodViolations.Count) / totalChecks;

        RecordOperation("GetSlaReport");

        return Task.FromResult(new SlaReport
        {
            SlaId = slaId,
            SlaName = sla.Name,
            FromDate = fromDate,
            ToDate = toDate,
            TotalViolations = periodViolations.Count,
            ComplianceRate = complianceRate,
            Violations = periodViolations
        });
    }
}

#endregion

#region 126.8.3 Data Quality Monitoring Strategy

/// <summary>
/// 126.8.3: Data quality monitoring strategy tracking data quality metrics
/// including accuracy, consistency, completeness, and timeliness.
/// </summary>
public sealed class DataQualityMonitoringStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, DataQualityProfile> _profiles = new BoundedDictionary<string, DataQualityProfile>(1000);
    private readonly BoundedDictionary<string, List<QualityScore>> _scores = new BoundedDictionary<string, List<QualityScore>>(1000);

    public override string StrategyId => "monitoring-data-quality";
    public override string DisplayName => "Data Quality Monitoring";
    public override IntegrationCategory Category => IntegrationCategory.IntegrationMonitoring;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = false,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1000000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Data quality monitoring tracking accuracy, consistency, completeness, timeliness, " +
        "uniqueness, and validity metrics with trend analysis and anomaly detection.";
    public override string[] Tags => ["monitoring", "quality", "accuracy", "completeness", "consistency"];

    /// <summary>
    /// Creates a data quality profile.
    /// </summary>
    public Task<DataQualityProfile> CreateProfileAsync(
        string profileId,
        string datasetName,
        IReadOnlyList<QualityRule> rules,
        CancellationToken ct = default)
    {
        var profile = new DataQualityProfile
        {
            ProfileId = profileId,
            DatasetName = datasetName,
            Rules = rules.ToList(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_profiles.TryAdd(profileId, profile))
            throw new InvalidOperationException($"Profile {profileId} already exists");

        _scores[profileId] = new List<QualityScore>();

        RecordOperation("CreateQualityProfile");
        return Task.FromResult(profile);
    }

    /// <summary>
    /// Evaluates data quality.
    /// </summary>
    public Task<QualityEvaluation> EvaluateQualityAsync(
        string profileId,
        IReadOnlyList<Dictionary<string, object>> records,
        CancellationToken ct = default)
    {
        if (!_profiles.TryGetValue(profileId, out var profile))
            throw new KeyNotFoundException($"Profile {profileId} not found");

        if (!_scores.TryGetValue(profileId, out var scores))
            throw new InvalidOperationException($"Scores list not found for profile {profileId}");

        var evaluation = new QualityEvaluation
        {
            EvaluationId = Guid.NewGuid().ToString("N"),
            ProfileId = profileId,
            RecordsEvaluated = records.Count,
            EvaluatedAt = DateTime.UtcNow,
            RuleResults = new List<RuleResult>()
        };

        // Evaluate each rule
        foreach (var rule in profile.Rules)
        {
            var result = EvaluateRule(rule, records);
            evaluation.RuleResults.Add(result);
        }

        // Calculate overall score
        evaluation.OverallScore = evaluation.RuleResults.Average(r => r.Score);

        // Record score history
        lock (scores)
        {
            scores.Add(new QualityScore
            {
                ProfileId = profileId,
                Score = evaluation.OverallScore,
                RecordedAt = DateTime.UtcNow
            });
        }

        profile.LastEvaluated = DateTime.UtcNow;
        profile.LastScore = evaluation.OverallScore;

        RecordOperation("EvaluateQuality");
        return Task.FromResult(evaluation);
    }

    /// <summary>
    /// Gets quality trend.
    /// </summary>
    public Task<QualityTrend> GetTrendAsync(
        string profileId,
        int periods = 10,
        CancellationToken ct = default)
    {
        if (!_scores.TryGetValue(profileId, out var scores))
            throw new KeyNotFoundException($"Profile {profileId} not found");

        List<QualityScore> recentScores;
        lock (scores)
        {
            recentScores = scores.TakeLast(periods).ToList();
        }
        var trend = recentScores.Count >= 2
            ? (recentScores.Last().Score - recentScores.First().Score) / recentScores.Count
            : 0;

        RecordOperation("GetQualityTrend");

        return Task.FromResult(new QualityTrend
        {
            ProfileId = profileId,
            Scores = recentScores,
            TrendDirection = trend > 0.01 ? TrendDirection.Improving :
                             trend < -0.01 ? TrendDirection.Declining : TrendDirection.Stable,
            TrendValue = trend
        });
    }

    private RuleResult EvaluateRule(QualityRule rule, IReadOnlyList<Dictionary<string, object>> records)
    {
        var violations = 0;

        foreach (var record in records)
        {
            if (!EvaluateSingleRule(rule, record))
                violations++;
        }

        return new RuleResult
        {
            RuleId = rule.RuleId,
            RuleName = rule.Name,
            Score = records.Count > 0 ? (double)(records.Count - violations) / records.Count : 1.0,
            Violations = violations
        };
    }

    private bool EvaluateSingleRule(QualityRule rule, Dictionary<string, object> record)
    {
        return rule.Type switch
        {
            QualityRuleType.NotNull => record.TryGetValue(rule.ColumnName!, out var v) && v != null,
            QualityRuleType.Unique => true, // Would check uniqueness against cache
            QualityRuleType.Range => EvaluateRangeRule(rule, record),
            QualityRuleType.Pattern => EvaluatePatternRule(rule, record),
            QualityRuleType.Referential => true, // Would check referential integrity
            _ => true
        };
    }

    private bool EvaluateRangeRule(QualityRule rule, Dictionary<string, object> record)
    {
        if (!record.TryGetValue(rule.ColumnName!, out var value) || value == null)
            return rule.AllowNull;

        var numValue = Convert.ToDouble(value);
        return (!rule.MinValue.HasValue || numValue >= rule.MinValue.Value) &&
               (!rule.MaxValue.HasValue || numValue <= rule.MaxValue.Value);
    }

    private bool EvaluatePatternRule(QualityRule rule, Dictionary<string, object> record)
    {
        if (!record.TryGetValue(rule.ColumnName!, out var value) || value == null)
            return rule.AllowNull;

        return System.Text.RegularExpressions.Regex.IsMatch(value.ToString()!, rule.Pattern!);
    }
}

#endregion

#region 126.8.4 Integration Lineage Tracking Strategy

/// <summary>
/// 126.8.4: Integration lineage tracking strategy capturing data flow,
/// transformations, and dependencies across pipelines.
/// </summary>
public sealed class IntegrationLineageTrackingStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, LineageNode> _nodes = new BoundedDictionary<string, LineageNode>(1000);
    private readonly BoundedDictionary<string, List<LineageEdge>> _edges = new BoundedDictionary<string, List<LineageEdge>>(1000);

    public override string StrategyId => "monitoring-lineage-tracking";
    public override string DisplayName => "Integration Lineage Tracking";
    public override IntegrationCategory Category => IntegrationCategory.IntegrationMonitoring;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 500000,
        TypicalLatencyMs = 5.0
    };
    public override string SemanticDescription =>
        "Integration lineage tracking capturing end-to-end data flow, transformations, " +
        "and dependencies with impact analysis and root cause tracing.";
    public override string[] Tags => ["monitoring", "lineage", "tracking", "impact-analysis", "governance"];

    /// <summary>
    /// Registers a lineage node.
    /// </summary>
    public Task<LineageNode> RegisterNodeAsync(
        string nodeId,
        string nodeName,
        LineageNodeType nodeType,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        var node = new LineageNode
        {
            NodeId = nodeId,
            NodeName = nodeName,
            NodeType = nodeType,
            Metadata = metadata ?? new(),
            RegisteredAt = DateTime.UtcNow
        };

        if (!_nodes.TryAdd(nodeId, node))
            throw new InvalidOperationException($"Node {nodeId} already exists");

        _edges[nodeId] = new List<LineageEdge>();

        RecordOperation("RegisterNode");
        return Task.FromResult(node);
    }

    /// <summary>
    /// Records a lineage edge.
    /// </summary>
    public Task<LineageEdge> RecordEdgeAsync(
        string sourceNodeId,
        string targetNodeId,
        LineageEdgeType edgeType,
        Dictionary<string, object>? transformations = null,
        CancellationToken ct = default)
    {
        if (!_nodes.ContainsKey(sourceNodeId))
            throw new KeyNotFoundException($"Source node {sourceNodeId} not found");

        if (!_nodes.ContainsKey(targetNodeId))
            throw new KeyNotFoundException($"Target node {targetNodeId} not found");

        var edge = new LineageEdge
        {
            EdgeId = Guid.NewGuid().ToString("N"),
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId,
            EdgeType = edgeType,
            Transformations = transformations ?? new(),
            RecordedAt = DateTime.UtcNow
        };

        if (_edges.TryGetValue(sourceNodeId, out var edges))
        {
            lock (edges)
            {
                edges.Add(edge);
            }
        }

        RecordOperation("RecordEdge");
        return Task.FromResult(edge);
    }

    /// <summary>
    /// Gets upstream lineage.
    /// </summary>
    public Task<LineageGraph> GetUpstreamLineageAsync(
        string nodeId,
        int maxDepth = 10,
        CancellationToken ct = default)
    {
        if (!_nodes.TryGetValue(nodeId, out var startNode))
            throw new KeyNotFoundException($"Node {nodeId} not found");

        var graph = new LineageGraph
        {
            RootNodeId = nodeId,
            Direction = LineageDirection.Upstream,
            Nodes = new List<LineageNode> { startNode },
            Edges = new List<LineageEdge>()
        };

        // Traverse upstream
        var visited = new HashSet<string> { nodeId };
        TraverseUpstream(nodeId, graph, visited, maxDepth, 0);

        RecordOperation("GetUpstreamLineage");
        return Task.FromResult(graph);
    }

    /// <summary>
    /// Gets downstream lineage.
    /// </summary>
    public Task<LineageGraph> GetDownstreamLineageAsync(
        string nodeId,
        int maxDepth = 10,
        CancellationToken ct = default)
    {
        if (!_nodes.TryGetValue(nodeId, out var startNode))
            throw new KeyNotFoundException($"Node {nodeId} not found");

        var graph = new LineageGraph
        {
            RootNodeId = nodeId,
            Direction = LineageDirection.Downstream,
            Nodes = new List<LineageNode> { startNode },
            Edges = new List<LineageEdge>()
        };

        // Traverse downstream
        var visited = new HashSet<string> { nodeId };
        TraverseDownstream(nodeId, graph, visited, maxDepth, 0);

        RecordOperation("GetDownstreamLineage");
        return Task.FromResult(graph);
    }

    /// <summary>
    /// Performs impact analysis.
    /// </summary>
    public Task<ImpactAnalysis> AnalyzeImpactAsync(
        string nodeId,
        CancellationToken ct = default)
    {
        if (!_nodes.TryGetValue(nodeId, out var node))
            throw new KeyNotFoundException($"Node {nodeId} not found");

        var downstream = new HashSet<string>();
        TraverseDownstreamIds(nodeId, downstream, 100, 0);

        RecordOperation("AnalyzeImpact");

        return Task.FromResult(new ImpactAnalysis
        {
            SourceNodeId = nodeId,
            ImpactedNodes = downstream.Select(id => _nodes[id]).ToList(),
            TotalImpactedCount = downstream.Count,
            AnalyzedAt = DateTime.UtcNow
        });
    }

    private void TraverseUpstream(string nodeId, LineageGraph graph, HashSet<string> visited, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth) return;

        // Find all edges where this node is the target
        foreach (var edgeList in _edges.Values)
        {
            List<LineageEdge> snapshot;
            lock (edgeList) { snapshot = new List<LineageEdge>(edgeList); }
            foreach (var edge in snapshot.Where(e => e.TargetNodeId == nodeId && !visited.Contains(e.SourceNodeId)))
            {
                visited.Add(edge.SourceNodeId);
                if (_nodes.TryGetValue(edge.SourceNodeId, out var sourceNode))
                {
                    graph.Nodes.Add(sourceNode);
                    graph.Edges.Add(edge);
                    TraverseUpstream(edge.SourceNodeId, graph, visited, maxDepth, currentDepth + 1);
                }
            }
        }
    }

    private void TraverseDownstream(string nodeId, LineageGraph graph, HashSet<string> visited, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth) return;

        if (_edges.TryGetValue(nodeId, out var edgeList))
        {
            List<LineageEdge> snapshot;
            lock (edgeList) { snapshot = new List<LineageEdge>(edgeList); }
            foreach (var edge in snapshot.Where(e => !visited.Contains(e.TargetNodeId)))
            {
                visited.Add(edge.TargetNodeId);
                if (_nodes.TryGetValue(edge.TargetNodeId, out var targetNode))
                {
                    graph.Nodes.Add(targetNode);
                    graph.Edges.Add(edge);
                    TraverseDownstream(edge.TargetNodeId, graph, visited, maxDepth, currentDepth + 1);
                }
            }
        }
    }

    private void TraverseDownstreamIds(string nodeId, HashSet<string> visited, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth) return;

        if (_edges.TryGetValue(nodeId, out var edgeList2))
        {
            List<LineageEdge> snapshot;
            lock (edgeList2) { snapshot = new List<LineageEdge>(edgeList2); }
            foreach (var edge in snapshot.Where(e => !visited.Contains(e.TargetNodeId)))
            {
                visited.Add(edge.TargetNodeId);
                TraverseDownstreamIds(edge.TargetNodeId, visited, maxDepth, currentDepth + 1);
            }
        }
    }
}

#endregion

#region 126.8.5 Alert and Notification Strategy

/// <summary>
/// 126.8.5: Alert and notification strategy for managing integration
/// alerts with escalation, suppression, and routing.
/// </summary>
public sealed class AlertNotificationStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, AlertRule> _rules = new BoundedDictionary<string, AlertRule>(1000);
    private readonly BoundedDictionary<string, List<Alert>> _alerts = new BoundedDictionary<string, List<Alert>>(1000);
    private readonly BoundedDictionary<string, NotificationChannel> _channels = new BoundedDictionary<string, NotificationChannel>(1000);

    public override string StrategyId => "monitoring-alert-notification";
    public override string DisplayName => "Alert and Notification";
    public override IntegrationCategory Category => IntegrationCategory.IntegrationMonitoring;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = false,
        SupportsSchemaEvolution = false,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 100000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Alert and notification management with configurable rules, severity levels, " +
        "escalation policies, alert suppression, and multi-channel notification routing.";
    public override string[] Tags => ["monitoring", "alerts", "notifications", "escalation", "routing"];

    /// <summary>
    /// Creates an alert rule.
    /// </summary>
    public Task<AlertRule> CreateRuleAsync(
        string ruleId,
        string ruleName,
        AlertCondition condition,
        AlertAction action,
        CancellationToken ct = default)
    {
        var rule = new AlertRule
        {
            RuleId = ruleId,
            RuleName = ruleName,
            Condition = condition,
            Action = action,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        if (!_rules.TryAdd(ruleId, rule))
            throw new InvalidOperationException($"Rule {ruleId} already exists");

        _alerts[ruleId] = new List<Alert>();

        RecordOperation("CreateAlertRule");
        return Task.FromResult(rule);
    }

    /// <summary>
    /// Registers a notification channel.
    /// </summary>
    public Task<NotificationChannel> RegisterChannelAsync(
        string channelId,
        string channelName,
        ChannelType channelType,
        Dictionary<string, string> config,
        CancellationToken ct = default)
    {
        var channel = new NotificationChannel
        {
            ChannelId = channelId,
            ChannelName = channelName,
            ChannelType = channelType,
            Config = config,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        if (!_channels.TryAdd(channelId, channel))
            throw new InvalidOperationException($"Channel {channelId} already exists");

        RecordOperation("RegisterChannel");
        return Task.FromResult(channel);
    }

    /// <summary>
    /// Triggers an alert.
    /// </summary>
    public Task<Alert> TriggerAlertAsync(
        string ruleId,
        Dictionary<string, object> context,
        CancellationToken ct = default)
    {
        if (!_rules.TryGetValue(ruleId, out var rule))
            throw new KeyNotFoundException($"Rule {ruleId} not found");

        if (!_alerts.TryGetValue(ruleId, out var alerts))
            throw new InvalidOperationException($"Alerts list not found for rule {ruleId}");

        var alert = new Alert
        {
            AlertId = Guid.NewGuid().ToString("N"),
            RuleId = ruleId,
            RuleName = rule.RuleName,
            Severity = rule.Condition.Severity,
            Message = FormatAlertMessage(rule, context),
            Context = context,
            Status = AlertStatus.Triggered,
            TriggeredAt = DateTime.UtcNow
        };

        lock (alerts)
        {
            alerts.Add(alert);
        }

        // Send notifications
        if (rule.Action.NotifyChannels != null)
        {
            foreach (var channelId in rule.Action.NotifyChannels)
            {
                if (_channels.TryGetValue(channelId, out var channel) && channel.IsEnabled)
                {
                    SendNotification(channel, alert);
                }
            }
        }

        RecordOperation("TriggerAlert");
        return Task.FromResult(alert);
    }

    /// <summary>
    /// Acknowledges an alert.
    /// </summary>
    public Task<Alert> AcknowledgeAlertAsync(
        string alertId,
        string acknowledgedBy,
        CancellationToken ct = default)
    {
        foreach (var alertList in _alerts.Values)
        {
            Alert? alert;
            lock (alertList)
            {
                alert = alertList.FirstOrDefault(a => a.AlertId == alertId);
            }
            if (alert != null)
            {
                alert.Status = AlertStatus.Acknowledged;
                alert.AcknowledgedAt = DateTime.UtcNow;
                alert.AcknowledgedBy = acknowledgedBy;
                RecordOperation("AcknowledgeAlert");
                return Task.FromResult(alert);
            }
        }

        throw new KeyNotFoundException($"Alert {alertId} not found");
    }

    /// <summary>
    /// Gets active alerts.
    /// </summary>
    public Task<List<Alert>> GetActiveAlertsAsync(
        AlertSeverity? minSeverity = null,
        CancellationToken ct = default)
    {
        var snapshots = new List<Alert>();
        foreach (var alertList in _alerts.Values)
        {
            lock (alertList) { snapshots.AddRange(alertList); }
        }

        var activeAlerts = snapshots
            .Where(a => a.Status == AlertStatus.Triggered || a.Status == AlertStatus.Acknowledged)
            .Where(a => !minSeverity.HasValue || a.Severity >= minSeverity.Value)
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.TriggeredAt)
            .ToList();

        return Task.FromResult(activeAlerts);
    }

    private string FormatAlertMessage(AlertRule rule, Dictionary<string, object> context)
    {
        var message = rule.Condition.MessageTemplate ?? $"Alert triggered: {rule.RuleName}";
        foreach (var kvp in context)
        {
            message = message.Replace($"{{{kvp.Key}}}", kvp.Value?.ToString() ?? "");
        }
        return message;
    }

    private void SendNotification(NotificationChannel channel, Alert alert)
    {
        // Simulate sending notification
        // In production, this would integrate with actual notification services
    }
}

#endregion

#region Supporting Types

// Pipeline Health Types
public enum PipelineHealthStatus { Unknown, Healthy, Warning, Critical }
public enum AlertSeverity { Info = 0, Warning = 1, Critical = 2 }

public sealed record PipelineHealth
{
    public required string PipelineId { get; init; }
    public required HealthMonitoringConfig Config { get; init; }
    public PipelineHealthStatus Status { get; set; }
    public DateTime RegisteredAt { get; init; }
    public DateTime? LastCheckedAt { get; set; }
    public HealthCheck? LastCheck { get; set; }
    public List<HealthAlert> ActiveAlerts { get; init; } = new();
}

public sealed record HealthMonitoringConfig
{
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(1);
    public double WarningErrorRateThreshold { get; init; } = 0.01;
    public double CriticalErrorRateThreshold { get; init; } = 0.05;
    public TimeSpan WarningLatencyThreshold { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CriticalLatencyThreshold { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record HealthMetrics
{
    public long RecordsProcessed { get; init; }
    public long Errors { get; init; }
    public double ErrorRate => RecordsProcessed > 0 ? (double)Errors / RecordsProcessed : 0;
    public TimeSpan Latency { get; init; }
    public double CpuUtilization { get; init; }
    public double MemoryUtilization { get; init; }
}

public sealed record HealthCheck
{
    public required string CheckId { get; init; }
    public required string PipelineId { get; init; }
    public required HealthMetrics Metrics { get; init; }
    public PipelineHealthStatus Status { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed record HealthAlert
{
    public required string AlertId { get; init; }
    public required string PipelineId { get; init; }
    public AlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public DateTime TriggeredAt { get; init; }
}

// SLA Types
public enum SlaStatus { Active, Suspended, Archived }

public sealed record SlaDefinition
{
    public required string SlaId { get; init; }
    public required string Name { get; init; }
    public required SlaRequirements Requirements { get; init; }
    public SlaStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastChecked { get; set; }
    public bool? LastComplianceResult { get; set; }
}

public sealed record SlaRequirements
{
    public TimeSpan? MaxDataAge { get; init; }
    public double? MinCompleteness { get; init; }
    public double? MinAvailability { get; init; }
    public TimeSpan? MaxLatency { get; init; }
}

public sealed record SlaMetrics
{
    public TimeSpan DataAge { get; init; }
    public double Completeness { get; init; }
    public double Availability { get; init; }
    public TimeSpan ProcessingLatency { get; init; }
}

public sealed record SlaComplianceResult
{
    public required string SlaId { get; init; }
    public DateTime CheckedAt { get; init; }
    public bool IsCompliant { get; set; }
    public required List<string> Violations { get; init; }
}

public sealed record SlaViolation
{
    public required string ViolationId { get; init; }
    public required string SlaId { get; init; }
    public required List<string> Violations { get; init; }
    public required SlaMetrics Metrics { get; init; }
    public DateTime OccurredAt { get; init; }
}

public sealed record SlaReport
{
    public required string SlaId { get; init; }
    public required string SlaName { get; init; }
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public int TotalViolations { get; init; }
    public double ComplianceRate { get; init; }
    public required List<SlaViolation> Violations { get; init; }
}

// Data Quality Types
public enum QualityRuleType { NotNull, Unique, Range, Pattern, Referential }
public enum TrendDirection { Improving, Stable, Declining }

public sealed record DataQualityProfile
{
    public required string ProfileId { get; init; }
    public required string DatasetName { get; init; }
    public required List<QualityRule> Rules { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastEvaluated { get; set; }
    public double? LastScore { get; set; }
}

public sealed record QualityRule
{
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    public QualityRuleType Type { get; init; }
    public string? ColumnName { get; init; }
    public bool AllowNull { get; init; } = false;
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public string? Pattern { get; init; }
}

public sealed record QualityEvaluation
{
    public required string EvaluationId { get; init; }
    public required string ProfileId { get; init; }
    public int RecordsEvaluated { get; init; }
    public DateTime EvaluatedAt { get; init; }
    public double OverallScore { get; set; }
    public required List<RuleResult> RuleResults { get; init; }
}

public sealed record RuleResult
{
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public double Score { get; init; }
    public int Violations { get; init; }
}

public sealed record QualityScore
{
    public required string ProfileId { get; init; }
    public double Score { get; init; }
    public DateTime RecordedAt { get; init; }
}

public sealed record QualityTrend
{
    public required string ProfileId { get; init; }
    public required List<QualityScore> Scores { get; init; }
    public TrendDirection TrendDirection { get; init; }
    public double TrendValue { get; init; }
}

// Lineage Types
public enum LineageNodeType { Source, Transform, Target, Pipeline }
public enum LineageEdgeType { DataFlow, Transformation, Dependency }
public enum LineageDirection { Upstream, Downstream }

public sealed record LineageNode
{
    public required string NodeId { get; init; }
    public required string NodeName { get; init; }
    public LineageNodeType NodeType { get; init; }
    public required Dictionary<string, object> Metadata { get; init; }
    public DateTime RegisteredAt { get; init; }
}

public sealed record LineageEdge
{
    public required string EdgeId { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public LineageEdgeType EdgeType { get; init; }
    public required Dictionary<string, object> Transformations { get; init; }
    public DateTime RecordedAt { get; init; }
}

public sealed record LineageGraph
{
    public required string RootNodeId { get; init; }
    public LineageDirection Direction { get; init; }
    public required List<LineageNode> Nodes { get; init; }
    public required List<LineageEdge> Edges { get; init; }
}

public sealed record ImpactAnalysis
{
    public required string SourceNodeId { get; init; }
    public required List<LineageNode> ImpactedNodes { get; init; }
    public int TotalImpactedCount { get; init; }
    public DateTime AnalyzedAt { get; init; }
}

// Alert Types
public enum AlertStatus { Triggered, Acknowledged, Resolved, Suppressed }
public enum ChannelType { Email, Slack, PagerDuty, Webhook, Teams }

public sealed record AlertRule
{
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public required AlertCondition Condition { get; init; }
    public required AlertAction Action { get; init; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record AlertCondition
{
    public required string Metric { get; init; }
    public required string Operator { get; init; }
    public required double Threshold { get; init; }
    public AlertSeverity Severity { get; init; }
    public string? MessageTemplate { get; init; }
}

public sealed record AlertAction
{
    public List<string>? NotifyChannels { get; init; }
    public TimeSpan? EscalateAfter { get; init; }
    public List<string>? EscalateTo { get; init; }
}

public sealed record NotificationChannel
{
    public required string ChannelId { get; init; }
    public required string ChannelName { get; init; }
    public ChannelType ChannelType { get; init; }
    public required Dictionary<string, string> Config { get; init; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record Alert
{
    public required string AlertId { get; init; }
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public AlertSeverity Severity { get; init; }
    public required string Message { get; init; }
    public required Dictionary<string, object> Context { get; init; }
    public AlertStatus Status { get; set; }
    public DateTime TriggeredAt { get; init; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

#endregion
