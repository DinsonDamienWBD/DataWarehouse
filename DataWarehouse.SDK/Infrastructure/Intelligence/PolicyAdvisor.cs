using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy.Performance;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// A single step in a rationale chain, contributed by one advisor.
/// </summary>
/// <param name="AdvisorId">Which advisor contributed this reasoning step.</param>
/// <param name="Finding">What was observed (e.g., "Memory pressure at 92%").</param>
/// <param name="Implication">What the finding means for policy (e.g., "Compression should be increased").</param>
/// <param name="Confidence">Confidence in this step, 0.0 to 1.0.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-07)")]
public sealed record RationaleStep(
    string AdvisorId,
    string Finding,
    string Implication,
    double Confidence
);

/// <summary>
/// Full rationale chain for a policy recommendation, aggregating reasoning from multiple advisors.
/// </summary>
/// <param name="Steps">Ordered chain of reasoning steps from contributing advisors.</param>
/// <param name="Summary">One-line human-readable summary of the rationale.</param>
/// <param name="OverallConfidence">Product of all step confidences (0.0-1.0).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-07)")]
public sealed record RationaleChain(
    List<RationaleStep> Steps,
    string Summary,
    double OverallConfidence
);

/// <summary>
/// Synthesizes outputs from all 5 AI advisors (hardware, workload, threat, cost, sensitivity)
/// into <see cref="PolicyRecommendation"/> records with full <see cref="RationaleChain"/> reasoning.
/// Respects per-feature autonomy levels from <see cref="AiAutonomyConfiguration"/> and only emits
/// recommendations when advisor state changes warrant a new recommendation.
/// </summary>
/// <remarks>
/// Implements AIPI-07. On each observation processing cycle, the PolicyAdvisor:
/// <list type="number">
///   <item><description>Reads current snapshots from all 5 advisors.</description></item>
///   <item><description>Iterates all 94 features from <see cref="CheckClassificationTable"/>.</description></item>
///   <item><description>Builds a per-feature rationale chain from relevant advisor signals.</description></item>
///   <item><description>Checks autonomy configuration before generating recommendations.</description></item>
///   <item><description>Only emits when confidence exceeds threshold and state has changed.</description></item>
/// </list>
/// Security features always consult ThreatDetector + SensitivityAnalyzer.
/// Performance features always consult HardwareProbe + WorkloadAnalyzer + CostAnalyzer.
/// Other features consult all advisors but only emit if confidence exceeds 0.5.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-07)")]
public sealed class PolicyAdvisor : IAiAdvisor
{
    private readonly HardwareProbe _hardwareProbe;
    private readonly WorkloadAnalyzer _workloadAnalyzer;
    private readonly ThreatDetector _threatDetector;
    private readonly CostAnalyzer _costAnalyzer;
    private readonly DataSensitivityAnalyzer _sensitivityAnalyzer;
    private readonly AiAutonomyConfiguration _autonomyConfig;

    private readonly ConcurrentDictionary<string, PolicyRecommendation> _lastEmitted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RationaleChain> _rationaleChains = new(StringComparer.Ordinal);
    private readonly List<PolicyRecommendation> _pending = new();
    private readonly object _pendingLock = new();

    /// <summary>
    /// Security-sensitive feature IDs that always consult ThreatDetector and SensitivityAnalyzer.
    /// </summary>
    private static readonly HashSet<string> SecurityFeatures = new(StringComparer.Ordinal)
    {
        "encryption", "access_control", "auth_model", "key_management", "fips_mode"
    };

    /// <summary>
    /// Performance-sensitive feature IDs that always consult HardwareProbe, WorkloadAnalyzer, and CostAnalyzer.
    /// </summary>
    private static readonly HashSet<string> PerformanceFeatures = new(StringComparer.Ordinal)
    {
        "compression", "cache_strategy", "indexing"
    };

    /// <summary>
    /// Creates a new PolicyAdvisor that aggregates all 5 advisor outputs.
    /// </summary>
    /// <param name="hardwareProbe">Hardware capability and resource state advisor.</param>
    /// <param name="workloadAnalyzer">Workload pattern and throughput advisor.</param>
    /// <param name="threatDetector">Security threat detection advisor.</param>
    /// <param name="costAnalyzer">Algorithm cost and billing projection advisor.</param>
    /// <param name="sensitivityAnalyzer">Data sensitivity and PII detection advisor.</param>
    /// <param name="autonomyConfig">Per-feature per-level autonomy configuration.</param>
    public PolicyAdvisor(
        HardwareProbe hardwareProbe,
        WorkloadAnalyzer workloadAnalyzer,
        ThreatDetector threatDetector,
        CostAnalyzer costAnalyzer,
        DataSensitivityAnalyzer sensitivityAnalyzer,
        AiAutonomyConfiguration autonomyConfig)
    {
        _hardwareProbe = hardwareProbe ?? throw new ArgumentNullException(nameof(hardwareProbe));
        _workloadAnalyzer = workloadAnalyzer ?? throw new ArgumentNullException(nameof(workloadAnalyzer));
        _threatDetector = threatDetector ?? throw new ArgumentNullException(nameof(threatDetector));
        _costAnalyzer = costAnalyzer ?? throw new ArgumentNullException(nameof(costAnalyzer));
        _sensitivityAnalyzer = sensitivityAnalyzer ?? throw new ArgumentNullException(nameof(sensitivityAnalyzer));
        _autonomyConfig = autonomyConfig ?? throw new ArgumentNullException(nameof(autonomyConfig));
    }

    /// <inheritdoc />
    public string AdvisorId => "policy_advisor";

    /// <summary>
    /// Recommendations generated but not yet dispatched to plugins.
    /// Thread-safe snapshot; the list is replaced atomically on each cycle.
    /// </summary>
    public IReadOnlyList<PolicyRecommendation> PendingRecommendations
    {
        get
        {
            lock (_pendingLock)
            {
                return _pending.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets the latest recommendation for a specific feature, or null if none has been generated.
    /// </summary>
    /// <param name="featureId">The feature identifier to look up.</param>
    /// <returns>The most recent recommendation, or null.</returns>
    public PolicyRecommendation? GetRecommendation(string featureId)
    {
        return _lastEmitted.TryGetValue(featureId, out var rec) ? rec : null;
    }

    /// <summary>
    /// Gets the full rationale chain for a feature's latest recommendation, or null if none exists.
    /// </summary>
    /// <param name="featureId">The feature identifier to look up.</param>
    /// <returns>The rationale chain, or null.</returns>
    public RationaleChain? GetRationale(string featureId)
    {
        return _rationaleChains.TryGetValue(featureId, out var chain) ? chain : null;
    }

    /// <inheritdoc />
    public Task ProcessObservationsAsync(IReadOnlyList<ObservationEvent> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return Task.CompletedTask;

        // Read current state from all 5 advisors
        HardwareSnapshot hardware = _hardwareProbe.CurrentSnapshot;
        WorkloadProfile workload = _workloadAnalyzer.CurrentProfile;
        ThreatAssessment threat = _threatDetector.CurrentAssessment;
        CostProfile cost = _costAnalyzer.CurrentProfile;
        SensitivityProfile sensitivity = _sensitivityAnalyzer.CurrentProfile;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var newPending = new List<PolicyRecommendation>();

        // Iterate all 94 features across all timing categories
        foreach (CheckTiming timing in Enum.GetValues<CheckTiming>())
        {
            var features = CheckClassificationTable.GetFeaturesByTiming(timing);
            for (int i = 0; i < features.Count; i++)
            {
                string featureId = features[i];

                // Build rationale chain for this feature
                var chain = BuildRationaleChain(featureId, hardware, workload, threat, cost, sensitivity);

                // Skip if no advisors had relevant signals
                if (chain.Steps.Count == 0)
                    continue;

                // For non-security non-performance features, only emit if confidence > 0.5
                if (!SecurityFeatures.Contains(featureId) &&
                    !PerformanceFeatures.Contains(featureId) &&
                    chain.OverallConfidence <= 0.5)
                {
                    continue;
                }

                // Check autonomy level — if ManualOnly, skip auto-generation
                AiAutonomyLevel configuredAutonomy = _autonomyConfig.GetAutonomy(featureId, PolicyLevel.VDE);
                if (configuredAutonomy == AiAutonomyLevel.ManualOnly)
                    continue;

                // Determine suggested policy
                FeaturePolicy suggestedPolicy = BuildSuggestedPolicy(featureId, chain, hardware, workload, threat, sensitivity);

                // Determine minimum required autonomy for auto-application
                AiAutonomyLevel requiredAutonomy = DetermineRequiredAutonomy(featureId, chain, threat, sensitivity);

                // Create recommendation
                var recommendation = new PolicyRecommendation(
                    featureId,
                    chain.Summary,
                    suggestedPolicy,
                    requiredAutonomy,
                    chain.OverallConfidence,
                    now);

                // Only emit if changed from last recommendation
                if (HasRecommendationChanged(featureId, recommendation))
                {
                    _lastEmitted[featureId] = recommendation;
                    _rationaleChains[featureId] = chain;
                    newPending.Add(recommendation);
                }
            }
        }

        // Atomic swap of pending recommendations
        lock (_pendingLock)
        {
            _pending.Clear();
            _pending.AddRange(newPending);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a rationale chain for a feature by querying each relevant advisor.
    /// </summary>
    private RationaleChain BuildRationaleChain(
        string featureId,
        HardwareSnapshot hardware,
        WorkloadProfile workload,
        ThreatAssessment threat,
        CostProfile cost,
        SensitivityProfile sensitivity)
    {
        var steps = new List<RationaleStep>(5);
        bool isSecurity = SecurityFeatures.Contains(featureId);
        bool isPerformance = PerformanceFeatures.Contains(featureId);

        // Hardware advisor signals
        if (isPerformance || !isSecurity)
        {
            var hwStep = BuildHardwareStep(featureId, hardware);
            if (hwStep is not null) steps.Add(hwStep);
        }

        // Workload advisor signals
        if (isPerformance || !isSecurity)
        {
            var wlStep = BuildWorkloadStep(featureId, workload);
            if (wlStep is not null) steps.Add(wlStep);
        }

        // Threat detector signals (always for security, always check for others too)
        if (isSecurity || threat.Level >= ThreatLevel.Low)
        {
            var thStep = BuildThreatStep(featureId, threat);
            if (thStep is not null) steps.Add(thStep);
        }

        // Cost analyzer signals
        if (isPerformance || !isSecurity)
        {
            var costStep = BuildCostStep(featureId, cost);
            if (costStep is not null) steps.Add(costStep);
        }

        // Sensitivity analyzer signals (always for security)
        if (isSecurity || sensitivity.HighestClassification >= DataClassification.Internal)
        {
            var sensStep = BuildSensitivityStep(featureId, sensitivity);
            if (sensStep is not null) steps.Add(sensStep);
        }

        // Compute overall confidence as product of step confidences
        double overallConfidence = 1.0;
        for (int i = 0; i < steps.Count; i++)
        {
            overallConfidence *= steps[i].Confidence;
        }

        string summary = BuildSummary(featureId, steps);
        return new RationaleChain(steps, summary, steps.Count > 0 ? overallConfidence : 0.0);
    }

    private static RationaleStep? BuildHardwareStep(string featureId, HardwareSnapshot hardware)
    {
        // Memory pressure signal
        if (hardware.MemoryPressurePercent > 85.0)
        {
            return featureId switch
            {
                "compression" => new RationaleStep(
                    "hardware_probe",
                    $"Memory pressure at {hardware.MemoryPressurePercent:F0}%",
                    "Increase compression to reduce memory footprint",
                    0.85),
                "cache_strategy" => new RationaleStep(
                    "hardware_probe",
                    $"Memory pressure at {hardware.MemoryPressurePercent:F0}%",
                    "Reduce cache size to free memory",
                    0.80),
                _ => null
            };
        }

        // SIMD capability signals
        if (hardware.HasAes && featureId == "encryption")
        {
            return new RationaleStep(
                "hardware_probe",
                "CPU has AES-NI hardware acceleration",
                "Recommend AES-NI encryption for near-zero CPU overhead",
                0.90);
        }

        // Thermal throttling
        if (hardware.Thermal >= ThermalState.Warm)
        {
            return featureId switch
            {
                "compression" => new RationaleStep(
                    "hardware_probe",
                    $"Thermal state: {hardware.Thermal}",
                    "Reduce compression effort to lower CPU heat generation",
                    0.75),
                "indexing" => new RationaleStep(
                    "hardware_probe",
                    $"Thermal state: {hardware.Thermal}",
                    "Defer background indexing until thermal state normalizes",
                    0.70),
                _ => null
            };
        }

        // NVMe storage optimization
        if (hardware.StorageSpeed == StorageSpeedClass.NvMe && featureId == "cache_strategy")
        {
            return new RationaleStep(
                "hardware_probe",
                "NVMe storage detected",
                "Reduce read cache as NVMe latency is already low",
                0.65);
        }

        return null;
    }

    private static RationaleStep? BuildWorkloadStep(string featureId, WorkloadProfile workload)
    {
        // Peak hours optimization
        if (workload.TimeOfDay == TimeOfDayPattern.Peak)
        {
            return featureId switch
            {
                "compression" => new RationaleStep(
                    "workload_analyzer",
                    "Peak hours detected",
                    "Optimize compression for throughput over ratio",
                    0.80),
                "cache_strategy" => new RationaleStep(
                    "workload_analyzer",
                    "Peak hours detected",
                    "Increase cache size for peak-hour read performance",
                    0.85),
                "indexing" => new RationaleStep(
                    "workload_analyzer",
                    "Peak hours detected",
                    "Defer index rebuilds to off-peak hours",
                    0.80),
                _ => null
            };
        }

        // Off-peak maintenance window
        if (workload.TimeOfDay == TimeOfDayPattern.OffPeak)
        {
            return featureId switch
            {
                "indexing" => new RationaleStep(
                    "workload_analyzer",
                    "Off-peak hours detected",
                    "Allow background maintenance and index rebuilds",
                    0.80),
                "compression" => new RationaleStep(
                    "workload_analyzer",
                    "Off-peak hours detected",
                    "Allow higher compression ratios during low load",
                    0.70),
                _ => null
            };
        }

        // Burst detection
        if (workload.IsBurstDetected)
        {
            return featureId switch
            {
                "cache_strategy" => new RationaleStep(
                    "workload_analyzer",
                    $"Burst detected (burst #{workload.BurstCount})",
                    "Temporarily increase cache to absorb burst",
                    0.75),
                "compression" => new RationaleStep(
                    "workload_analyzer",
                    $"Burst detected (burst #{workload.BurstCount})",
                    "Reduce compression effort to minimize latency during burst",
                    0.70),
                _ => null
            };
        }

        return null;
    }

    private static RationaleStep? BuildThreatStep(string featureId, ThreatAssessment threat)
    {
        if (threat.Level < ThreatLevel.Low) return null;

        return featureId switch
        {
            "access_control" => new RationaleStep(
                "threat_detector",
                $"Threat level: {threat.Level} (score: {threat.ThreatScore:F2})",
                threat.Level >= ThreatLevel.High
                    ? "Tighten access control to MostRestrictive cascade"
                    : "Elevate access control monitoring",
                Math.Min(1.0, 0.6 + threat.ThreatScore * 0.4)),
            "encryption" => threat.Level >= ThreatLevel.Elevated
                ? new RationaleStep(
                    "threat_detector",
                    $"Threat level: {threat.Level} (score: {threat.ThreatScore:F2})",
                    "Enforce encryption on all data paths",
                    Math.Min(1.0, 0.7 + threat.ThreatScore * 0.3))
                : null,
            "auth_model" => new RationaleStep(
                "threat_detector",
                $"Threat level: {threat.Level} (score: {threat.ThreatScore:F2})",
                "Enforce stricter authentication requirements",
                Math.Min(1.0, 0.5 + threat.ThreatScore * 0.5)),
            "key_management" => threat.Level >= ThreatLevel.High
                ? new RationaleStep(
                    "threat_detector",
                    $"Threat level: {threat.Level}",
                    "Accelerate key rotation schedule",
                    0.85)
                : null,
            "fips_mode" => threat.Level >= ThreatLevel.Critical
                ? new RationaleStep(
                    "threat_detector",
                    $"Critical threat detected",
                    "Enable FIPS mode for compliance-grade cryptography",
                    0.90)
                : null,
            _ => threat.Level >= ThreatLevel.Elevated
                ? new RationaleStep(
                    "threat_detector",
                    $"Threat level: {threat.Level} (score: {threat.ThreatScore:F2})",
                    $"Security posture elevated; review {featureId} policy",
                    0.60)
                : null
        };
    }

    private static RationaleStep? BuildCostStep(string featureId, CostProfile cost)
    {
        if (cost.AlgorithmCosts.Count == 0) return null;

        // Only provide cost advice for features that use algorithms
        if (featureId is "compression" or "encryption" or "indexing")
        {
            if (!string.IsNullOrEmpty(cost.MostExpensiveAlgorithm) &&
                !string.IsNullOrEmpty(cost.MostEfficientAlgorithm) &&
                cost.MostExpensiveAlgorithm != cost.MostEfficientAlgorithm)
            {
                if (cost.AlgorithmCosts.TryGetValue(cost.MostExpensiveAlgorithm, out var expensive) &&
                    cost.AlgorithmCosts.TryGetValue(cost.MostEfficientAlgorithm, out var efficient) &&
                    efficient.EstimatedCloudCostPerOp > 0)
                {
                    double costRatio = expensive.EstimatedCloudCostPerOp / efficient.EstimatedCloudCostPerOp;
                    if (costRatio > 2.0)
                    {
                        return new RationaleStep(
                            "cost_analyzer",
                            $"Algorithm '{cost.MostExpensiveAlgorithm}' costs {costRatio:F1}x more than '{cost.MostEfficientAlgorithm}'",
                            $"Consider switching to '{cost.MostEfficientAlgorithm}' for cost reduction",
                            Math.Min(1.0, 0.5 + (costRatio - 2.0) * 0.1));
                    }
                }
            }

            // Monthly cost warning
            if (cost.EstimatedMonthlyCostUsd > 100.0)
            {
                return new RationaleStep(
                    "cost_analyzer",
                    $"Estimated monthly cost: ${cost.EstimatedMonthlyCostUsd:F2}",
                    "Review algorithm selection for cost optimization opportunities",
                    0.65);
            }
        }

        return null;
    }

    private static RationaleStep? BuildSensitivityStep(string featureId, SensitivityProfile sensitivity)
    {
        if (sensitivity.HighestClassification <= DataClassification.Public) return null;

        return featureId switch
        {
            "encryption" => sensitivity.PiiDetected
                ? new RationaleStep(
                    "data_sensitivity_analyzer",
                    "PII detected in data stream",
                    "Mandate encryption and audit trail for PII protection",
                    0.95)
                : sensitivity.HighestClassification >= DataClassification.Confidential
                    ? new RationaleStep(
                        "data_sensitivity_analyzer",
                        $"Data classification: {sensitivity.HighestClassification}",
                        "Encryption required for classified data",
                        0.85)
                    : null,
            "access_control" => sensitivity.HighestClassification >= DataClassification.Restricted
                ? new RationaleStep(
                    "data_sensitivity_analyzer",
                    $"Data classification: {sensitivity.HighestClassification}",
                    "Restrict access control to need-to-know basis",
                    0.90)
                : null,
            "auth_model" => sensitivity.PiiDetected
                ? new RationaleStep(
                    "data_sensitivity_analyzer",
                    "PII data requires strong authentication",
                    "Enforce multi-factor authentication for PII access",
                    0.85)
                : null,
            "key_management" => sensitivity.HighestClassification >= DataClassification.Restricted
                ? new RationaleStep(
                    "data_sensitivity_analyzer",
                    $"Data classification: {sensitivity.HighestClassification}",
                    "Use HSM-backed key management for restricted data",
                    0.80)
                : null,
            "fips_mode" => sensitivity.HighestClassification >= DataClassification.TopSecret
                ? new RationaleStep(
                    "data_sensitivity_analyzer",
                    "Top Secret classification requires FIPS compliance",
                    "Enable FIPS mode for cryptographic algorithm compliance",
                    0.95)
                : null,
            _ => sensitivity.PiiDetected
                ? new RationaleStep(
                    "data_sensitivity_analyzer",
                    "PII detected",
                    $"Review {featureId} configuration for PII compliance requirements",
                    0.60)
                : null
        };
    }

    /// <summary>
    /// Builds a suggested FeaturePolicy based on the rationale chain analysis.
    /// </summary>
    private static FeaturePolicy BuildSuggestedPolicy(
        string featureId,
        RationaleChain chain,
        HardwareSnapshot hardware,
        WorkloadProfile workload,
        ThreatAssessment threat,
        SensitivityProfile sensitivity)
    {
        // Determine intensity level based on advisor signals
        int intensity = 50; // Default moderate intensity
        CascadeStrategy cascade = CascadeStrategy.Inherit;
        AiAutonomyLevel aiAutonomy = AiAutonomyLevel.Suggest;

        // Security features: tighten on threat/sensitivity
        if (SecurityFeatures.Contains(featureId))
        {
            if (threat.Level >= ThreatLevel.High || sensitivity.HighestClassification >= DataClassification.Restricted)
            {
                intensity = 90;
                cascade = CascadeStrategy.Enforce;
                aiAutonomy = AiAutonomyLevel.SuggestExplain;
            }
            else if (threat.Level >= ThreatLevel.Elevated || sensitivity.HighestClassification >= DataClassification.Confidential)
            {
                intensity = 75;
                cascade = CascadeStrategy.MostRestrictive;
                aiAutonomy = AiAutonomyLevel.SuggestExplain;
            }
        }

        // Performance features: optimize based on workload + hardware
        if (PerformanceFeatures.Contains(featureId))
        {
            if (hardware.MemoryPressurePercent > 85.0 || hardware.Thermal >= ThermalState.Warm)
            {
                intensity = 30; // Reduce effort under hardware pressure
                cascade = CascadeStrategy.Inherit;
                aiAutonomy = AiAutonomyLevel.AutoNotify;
            }
            else if (workload.TimeOfDay == TimeOfDayPattern.Peak || workload.IsBurstDetected)
            {
                intensity = 40; // Optimize for throughput
                cascade = CascadeStrategy.Inherit;
                aiAutonomy = AiAutonomyLevel.AutoNotify;
            }
            else if (workload.TimeOfDay == TimeOfDayPattern.OffPeak)
            {
                intensity = 80; // Deep compression/indexing during off-peak
                cascade = CascadeStrategy.Inherit;
                aiAutonomy = AiAutonomyLevel.AutoSilent;
            }
        }

        return new FeaturePolicy
        {
            FeatureId = featureId,
            Level = PolicyLevel.VDE,
            IntensityLevel = intensity,
            Cascade = cascade,
            AiAutonomy = aiAutonomy
        };
    }

    /// <summary>
    /// Determines the minimum autonomy level required to auto-apply a recommendation.
    /// Higher-impact recommendations require higher autonomy.
    /// </summary>
    private static AiAutonomyLevel DetermineRequiredAutonomy(
        string featureId,
        RationaleChain chain,
        ThreatAssessment threat,
        SensitivityProfile sensitivity)
    {
        // Security changes under active threat require at minimum SuggestExplain
        if (SecurityFeatures.Contains(featureId) &&
            (threat.Level >= ThreatLevel.Elevated || sensitivity.PiiDetected))
        {
            return AiAutonomyLevel.SuggestExplain;
        }

        // High-confidence performance recommendations can auto-apply with notification
        if (PerformanceFeatures.Contains(featureId) && chain.OverallConfidence > 0.7)
        {
            return AiAutonomyLevel.AutoNotify;
        }

        // Default: require explicit suggestion approval
        return AiAutonomyLevel.Suggest;
    }

    /// <summary>
    /// Checks if a recommendation differs meaningfully from the last emitted recommendation
    /// for the same feature.
    /// </summary>
    private bool HasRecommendationChanged(string featureId, PolicyRecommendation recommendation)
    {
        if (!_lastEmitted.TryGetValue(featureId, out var last))
            return true; // First recommendation for this feature

        // Changed if suggested policy differs or confidence moved significantly
        if (last.SuggestedPolicy.IntensityLevel != recommendation.SuggestedPolicy.IntensityLevel)
            return true;
        if (last.SuggestedPolicy.Cascade != recommendation.SuggestedPolicy.Cascade)
            return true;
        if (last.RequiredAutonomy != recommendation.RequiredAutonomy)
            return true;
        if (Math.Abs(last.ConfidenceScore - recommendation.ConfidenceScore) > 0.1)
            return true;

        return false;
    }

    /// <summary>
    /// Builds a human-readable summary from the rationale steps.
    /// </summary>
    private static string BuildSummary(string featureId, List<RationaleStep> steps)
    {
        if (steps.Count == 0)
            return $"No advisor signals for {featureId}";

        if (steps.Count == 1)
            return $"{featureId}: {steps[0].Implication}";

        // Multi-advisor summary: combine implications
        return $"{featureId}: {steps[0].Implication}; {steps[1].Implication}" +
               (steps.Count > 2 ? $" (+{steps.Count - 2} more)" : string.Empty);
    }
}
