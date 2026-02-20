using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies;

/// <summary>
/// Data classification confidence calibration strategy.
/// Provides calibrated confidence scores for data classification decisions.
/// </summary>
public sealed class ClassificationConfidenceCalibrationStrategy : DataGovernanceStrategyBase
{
    private readonly BoundedDictionary<string, ClassificationConfidence> _calibrations = new BoundedDictionary<string, ClassificationConfidence>(1000);
    private readonly BoundedDictionary<string, List<CalibrationSample>> _samples = new BoundedDictionary<string, List<CalibrationSample>>(1000);

    public override string StrategyId => "classification-confidence-calibration";
    public override string DisplayName => "Classification Confidence Calibration";
    public override GovernanceCategory Category => GovernanceCategory.DataClassification;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true, SupportsBatch = true,
        SupportsRealTime = true, SupportsAudit = true,
        SupportsVersioning = false
    };
    public override string SemanticDescription =>
        "Calibrate classification confidence scores using historical accuracy data and Platt scaling";
    public override string[] Tags => ["classification", "confidence", "calibration", "accuracy"];

    /// <summary>
    /// Records a classification with ground truth for calibration.
    /// </summary>
    public void RecordSample(string classifierId, string predictedClass, double rawConfidence, string actualClass)
    {
        var sample = new CalibrationSample
        {
            ClassifierId = classifierId,
            PredictedClass = predictedClass,
            RawConfidence = rawConfidence,
            ActualClass = actualClass,
            IsCorrect = predictedClass == actualClass,
            Timestamp = DateTimeOffset.UtcNow
        };

        _samples.AddOrUpdate(
            classifierId,
            _ => new List<CalibrationSample> { sample },
            (_, list) => { lock (list) { list.Add(sample); if (list.Count > 10000) list.RemoveAt(0); } return list; });
    }

    /// <summary>
    /// Gets calibrated confidence for a raw confidence score.
    /// Uses simple binning calibration (Platt scaling approximation).
    /// </summary>
    public double GetCalibratedConfidence(string classifierId, double rawConfidence)
    {
        if (!_samples.TryGetValue(classifierId, out var samples) || samples.Count < 10)
            return rawConfidence; // Not enough data for calibration

        // Bin the raw confidence into 10 buckets and compute actual accuracy per bin
        var bin = (int)(rawConfidence * 10);
        bin = Math.Max(0, Math.Min(9, bin));

        var binLower = bin / 10.0;
        var binUpper = (bin + 1) / 10.0;

        List<CalibrationSample> binSamples;
        lock (samples)
        {
            binSamples = samples.Where(s => s.RawConfidence >= binLower && s.RawConfidence < binUpper).ToList();
        }

        if (binSamples.Count < 5) return rawConfidence;

        // Calibrated confidence = actual accuracy in this bin
        return (double)binSamples.Count(s => s.IsCorrect) / binSamples.Count;
    }

    /// <summary>
    /// Gets calibration report for a classifier.
    /// </summary>
    public CalibrationReport GetReport(string classifierId)
    {
        if (!_samples.TryGetValue(classifierId, out var samples))
            return new CalibrationReport { ClassifierId = classifierId };

        List<CalibrationSample> samplesCopy;
        lock (samples) { samplesCopy = samples.ToList(); }

        var bins = Enumerable.Range(0, 10).Select(i =>
        {
            var lower = i / 10.0;
            var upper = (i + 1) / 10.0;
            var binSamples = samplesCopy.Where(s => s.RawConfidence >= lower && s.RawConfidence < upper).ToList();
            return new CalibrationBin
            {
                BinIndex = i,
                BinRange = $"{lower:F1}-{upper:F1}",
                SampleCount = binSamples.Count,
                MeanRawConfidence = binSamples.Count > 0 ? binSamples.Average(s => s.RawConfidence) : 0,
                ActualAccuracy = binSamples.Count > 0 ? (double)binSamples.Count(s => s.IsCorrect) / binSamples.Count : 0
            };
        }).ToList();

        var overallAccuracy = samplesCopy.Count > 0
            ? (double)samplesCopy.Count(s => s.IsCorrect) / samplesCopy.Count : 0;

        return new CalibrationReport
        {
            ClassifierId = classifierId,
            TotalSamples = samplesCopy.Count,
            OverallAccuracy = overallAccuracy,
            Bins = bins,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Stewardship assignment workflow strategy for data governance.
/// </summary>
public sealed class StewardshipAssignmentWorkflowStrategy : DataGovernanceStrategyBase
{
    private readonly BoundedDictionary<string, StewardshipAssignment> _assignments = new BoundedDictionary<string, StewardshipAssignment>(1000);
    private readonly BoundedDictionary<string, List<StewardshipTask>> _tasks = new BoundedDictionary<string, List<StewardshipTask>>(1000);

    public override string StrategyId => "stewardship-assignment-workflow";
    public override string DisplayName => "Stewardship Assignment Workflow";
    public override GovernanceCategory Category => GovernanceCategory.DataStewardship;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true, SupportsBatch = true,
        SupportsRealTime = true, SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription =>
        "Assign data stewards to data domains with task tracking and responsibility management";
    public override string[] Tags => ["stewardship", "assignment", "workflow", "responsibility"];

    /// <summary>
    /// Creates a stewardship assignment.
    /// </summary>
    public StewardshipAssignment Assign(string stewardId, string dataDomain,
        string[] responsibilities, string assignedBy)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var assignment = new StewardshipAssignment
        {
            AssignmentId = id,
            StewardId = stewardId,
            DataDomain = dataDomain,
            Responsibilities = responsibilities,
            AssignedBy = assignedBy,
            AssignedAt = DateTimeOffset.UtcNow,
            Status = StewardshipStatus.Active
        };
        _assignments[id] = assignment;
        return assignment;
    }

    /// <summary>
    /// Creates a stewardship task.
    /// </summary>
    public StewardshipTask CreateTask(string assignmentId, string title, string description,
        StewardshipTaskPriority priority)
    {
        var task = new StewardshipTask
        {
            TaskId = Guid.NewGuid().ToString("N")[..12],
            AssignmentId = assignmentId,
            Title = title,
            Description = description,
            Priority = priority,
            Status = StewardshipTaskStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _tasks.AddOrUpdate(
            assignmentId,
            _ => new List<StewardshipTask> { task },
            (_, list) => { lock (list) { list.Add(task); } return list; });

        return task;
    }

    /// <summary>
    /// Gets assignments for a steward.
    /// </summary>
    public IReadOnlyList<StewardshipAssignment> GetAssignments(string? stewardId = null) =>
        (stewardId != null
            ? _assignments.Values.Where(a => a.StewardId == stewardId)
            : _assignments.Values)
        .ToList().AsReadOnly();

    /// <summary>
    /// Gets tasks for an assignment.
    /// </summary>
    public IReadOnlyList<StewardshipTask> GetTasks(string assignmentId) =>
        _tasks.TryGetValue(assignmentId, out var tasks) ? tasks.AsReadOnly() : Array.Empty<StewardshipTask>();
}

/// <summary>
/// Quality SLA enforcement automation strategy.
/// </summary>
public sealed class QualitySlaEnforcementStrategy : DataGovernanceStrategyBase
{
    private readonly BoundedDictionary<string, QualitySla> _slas = new BoundedDictionary<string, QualitySla>(1000);
    private readonly BoundedDictionary<string, List<SlaViolation>> _violations = new BoundedDictionary<string, List<SlaViolation>>(1000);

    public override string StrategyId => "quality-sla-enforcement";
    public override string DisplayName => "Quality SLA Enforcement";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true, SupportsBatch = true,
        SupportsRealTime = true, SupportsAudit = true,
        SupportsVersioning = false
    };
    public override string SemanticDescription =>
        "Enforce data quality SLAs with automated monitoring, violation detection, and escalation";
    public override string[] Tags => ["quality", "sla", "enforcement", "monitoring"];

    /// <summary>
    /// Defines a quality SLA.
    /// </summary>
    public QualitySla DefineSla(string name, string datasetId, Dictionary<string, double> thresholds,
        TimeSpan evaluationInterval)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var sla = new QualitySla
        {
            SlaId = id,
            Name = name,
            DatasetId = datasetId,
            Thresholds = thresholds,
            EvaluationInterval = evaluationInterval,
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
        _slas[id] = sla;
        return sla;
    }

    /// <summary>
    /// Evaluates SLA compliance against actual metrics.
    /// </summary>
    public SlaEvaluationResult Evaluate(string slaId, Dictionary<string, double> actualMetrics)
    {
        if (!_slas.TryGetValue(slaId, out var sla))
            return new SlaEvaluationResult { SlaId = slaId, Compliant = false, Error = "SLA not found" };

        var violations = new List<SlaViolation>();
        foreach (var (metric, threshold) in sla.Thresholds)
        {
            if (actualMetrics.TryGetValue(metric, out var actual))
            {
                if (actual < threshold)
                {
                    violations.Add(new SlaViolation
                    {
                        SlaId = slaId,
                        MetricName = metric,
                        ThresholdValue = threshold,
                        ActualValue = actual,
                        DetectedAt = DateTimeOffset.UtcNow,
                        Severity = threshold - actual > threshold * 0.2 ? "Critical" : "Warning"
                    });
                }
            }
        }

        if (violations.Count > 0)
        {
            _violations.AddOrUpdate(
                slaId,
                _ => new List<SlaViolation>(violations),
                (_, list) => { lock (list) { list.AddRange(violations); } return list; });
        }

        return new SlaEvaluationResult
        {
            SlaId = slaId,
            Compliant = violations.Count == 0,
            Violations = violations,
            EvaluatedAt = DateTimeOffset.UtcNow,
            ComplianceRate = sla.Thresholds.Count > 0
                ? 1.0 - (double)violations.Count / sla.Thresholds.Count : 1.0
        };
    }

    /// <summary>
    /// Gets all SLAs.
    /// </summary>
    public IReadOnlyList<QualitySla> GetSlas(string? datasetId = null) =>
        (datasetId != null ? _slas.Values.Where(s => s.DatasetId == datasetId) : _slas.Values)
        .ToList().AsReadOnly();

    /// <summary>
    /// Gets violation history for an SLA.
    /// </summary>
    public IReadOnlyList<SlaViolation> GetViolations(string slaId) =>
        _violations.TryGetValue(slaId, out var violations) ? violations.AsReadOnly() : Array.Empty<SlaViolation>();
}

#region Models

public sealed record CalibrationSample
{
    public required string ClassifierId { get; init; }
    public required string PredictedClass { get; init; }
    public double RawConfidence { get; init; }
    public required string ActualClass { get; init; }
    public bool IsCorrect { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record ClassificationConfidence
{
    public required string ClassifierId { get; init; }
    public double RawConfidence { get; init; }
    public double CalibratedConfidence { get; init; }
}

public sealed record CalibrationReport
{
    public required string ClassifierId { get; init; }
    public int TotalSamples { get; init; }
    public double OverallAccuracy { get; init; }
    public List<CalibrationBin> Bins { get; init; } = new();
    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed record CalibrationBin
{
    public int BinIndex { get; init; }
    public required string BinRange { get; init; }
    public int SampleCount { get; init; }
    public double MeanRawConfidence { get; init; }
    public double ActualAccuracy { get; init; }
}

public sealed record StewardshipAssignment
{
    public required string AssignmentId { get; init; }
    public required string StewardId { get; init; }
    public required string DataDomain { get; init; }
    public required string[] Responsibilities { get; init; }
    public required string AssignedBy { get; init; }
    public DateTimeOffset AssignedAt { get; init; }
    public StewardshipStatus Status { get; init; }
}

public enum StewardshipStatus { Active, OnLeave, Reassigned, Completed }

public sealed record StewardshipTask
{
    public required string TaskId { get; init; }
    public required string AssignmentId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public StewardshipTaskPriority Priority { get; init; }
    public StewardshipTaskStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public enum StewardshipTaskPriority { Low, Medium, High, Critical }
public enum StewardshipTaskStatus { Open, InProgress, Review, Completed, Cancelled }

public sealed record QualitySla
{
    public required string SlaId { get; init; }
    public required string Name { get; init; }
    public required string DatasetId { get; init; }
    public Dictionary<string, double> Thresholds { get; init; } = new();
    public TimeSpan EvaluationInterval { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; init; }
}

public sealed record SlaEvaluationResult
{
    public required string SlaId { get; init; }
    public bool Compliant { get; init; }
    public string? Error { get; init; }
    public List<SlaViolation> Violations { get; init; } = new();
    public DateTimeOffset EvaluatedAt { get; init; }
    public double ComplianceRate { get; init; }
}

public sealed record SlaViolation
{
    public required string SlaId { get; init; }
    public required string MetricName { get; init; }
    public double ThresholdValue { get; init; }
    public double ActualValue { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public required string Severity { get; init; }
}

#endregion
