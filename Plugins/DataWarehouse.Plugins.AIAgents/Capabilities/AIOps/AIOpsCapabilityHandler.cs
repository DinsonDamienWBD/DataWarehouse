// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.AIOps;

/// <summary>
/// Main AIOps capability handler implementing comprehensive AI-driven operations.
/// Provides predictive tiering, scaling, anomaly detection, cost optimization,
/// capacity planning, performance tuning, security response, compliance automation,
/// query optimization, and failure prediction capabilities.
/// </summary>
public sealed class AIOpsCapabilityHandler : ICapabilityHandler, IDisposable
{
    /// <summary>
    /// Capability constants for AIOps operations.
    /// </summary>
    public static class Capabilities
    {
        /// <summary>ML-driven data placement optimization.</summary>
        public const string PredictiveTiering = "AIOps.PredictiveTiering";
        /// <summary>AI-powered capacity forecasting and scaling.</summary>
        public const string PredictiveScaling = "AIOps.PredictiveScaling";
        /// <summary>Statistical and ML-based anomaly detection.</summary>
        public const string AnomalyDetection = "AIOps.AnomalyDetection";
        /// <summary>Automatic remediation of detected anomalies.</summary>
        public const string AnomalyRemediation = "AIOps.AnomalyRemediation";
        /// <summary>AI-driven cost analysis and optimization.</summary>
        public const string CostOptimization = "AIOps.CostOptimization";
        /// <summary>Future capacity needs forecasting.</summary>
        public const string CapacityPlanning = "AIOps.CapacityPlanning";
        /// <summary>Automatic system performance tuning.</summary>
        public const string PerformanceTuning = "AIOps.PerformanceTuning";
        /// <summary>Automated security threat response.</summary>
        public const string SecurityResponse = "AIOps.SecurityResponse";
        /// <summary>Automated compliance policy enforcement.</summary>
        public const string ComplianceAutomation = "AIOps.ComplianceAutomation";
        /// <summary>AI-driven query performance optimization.</summary>
        public const string QueryOptimization = "AIOps.QueryOptimization";
        /// <summary>Predictive failure analysis.</summary>
        public const string FailurePrediction = "AIOps.FailurePrediction";
    }

    private readonly IAIProviderRegistry _providerRegistry;
    private readonly AIOpsConfiguration _config;
    private readonly ConcurrentDictionary<string, TieringEngine> _tieringEngines;
    private readonly ConcurrentDictionary<string, ScalingEngine> _scalingEngines;
    private readonly ConcurrentDictionary<string, AnomalyEngine> _anomalyEngines;
    private readonly ConcurrentDictionary<string, CostEngine> _costEngines;
    private readonly ConcurrentDictionary<string, PerformanceEngine> _performanceEngines;
    private readonly ConcurrentDictionary<string, PredictionModels> _predictionModels;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    /// <inheritdoc />
    public string CapabilityDomain => "AIOps";

    /// <inheritdoc />
    public string DisplayName => "AI-Driven Operations (AIOps)";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedCapabilities { get; } = new[]
    {
        Capabilities.PredictiveTiering,
        Capabilities.PredictiveScaling,
        Capabilities.AnomalyDetection,
        Capabilities.AnomalyRemediation,
        Capabilities.CostOptimization,
        Capabilities.CapacityPlanning,
        Capabilities.PerformanceTuning,
        Capabilities.SecurityResponse,
        Capabilities.ComplianceAutomation,
        Capabilities.QueryOptimization,
        Capabilities.FailurePrediction
    };

    /// <summary>
    /// Creates a new AIOps capability handler.
    /// </summary>
    /// <param name="providerRegistry">The AI provider registry for multi-provider support.</param>
    /// <param name="config">Optional AIOps configuration.</param>
    public AIOpsCapabilityHandler(IAIProviderRegistry providerRegistry, AIOpsConfiguration? config = null)
    {
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _config = config ?? new AIOpsConfiguration();
        _tieringEngines = new ConcurrentDictionary<string, TieringEngine>();
        _scalingEngines = new ConcurrentDictionary<string, ScalingEngine>();
        _anomalyEngines = new ConcurrentDictionary<string, AnomalyEngine>();
        _costEngines = new ConcurrentDictionary<string, CostEngine>();
        _performanceEngines = new ConcurrentDictionary<string, PerformanceEngine>();
        _predictionModels = new ConcurrentDictionary<string, PredictionModels>();
        _cts = new CancellationTokenSource();
    }

    /// <inheritdoc />
    public bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability)
    {
        if (instanceConfig == null)
            return false;

        // Check if explicitly disabled
        if (instanceConfig.DisabledCapabilities.Contains(capability))
            return false;

        // Check if AIOps domain is configured
        if (!instanceConfig.CapabilityProviderMappings.ContainsKey(CapabilityDomain) &&
            !instanceConfig.CapabilityProviderMappings.ContainsKey(capability))
            return false;

        return true;
    }

    /// <inheritdoc />
    public string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability)
    {
        if (instanceConfig == null)
            return null;

        // Check specific capability mapping first
        if (instanceConfig.CapabilityProviderMappings.TryGetValue(capability, out var specificProvider))
            return specificProvider;

        // Fall back to domain-level mapping
        if (instanceConfig.CapabilityProviderMappings.TryGetValue(CapabilityDomain, out var domainProvider))
            return domainProvider;

        return null;
    }

    #region Engine Access

    /// <summary>
    /// Gets or creates a tiering engine for the specified instance.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <returns>The tiering engine for the instance.</returns>
    public TieringEngine GetTieringEngine(InstanceCapabilityConfig instanceConfig)
    {
        return _tieringEngines.GetOrAdd(instanceConfig.InstanceId, _ =>
        {
            var provider = GetAIProvider(instanceConfig, Capabilities.PredictiveTiering);
            return new TieringEngine(provider as IExtendedAIProvider, _config.Tiering);
        });
    }

    /// <summary>
    /// Gets or creates a scaling engine for the specified instance.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <returns>The scaling engine for the instance.</returns>
    public ScalingEngine GetScalingEngine(InstanceCapabilityConfig instanceConfig)
    {
        return _scalingEngines.GetOrAdd(instanceConfig.InstanceId, _ =>
        {
            var provider = GetAIProvider(instanceConfig, Capabilities.PredictiveScaling);
            return new ScalingEngine(provider as IExtendedAIProvider, _config.Scaling);
        });
    }

    /// <summary>
    /// Gets or creates an anomaly engine for the specified instance.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <returns>The anomaly engine for the instance.</returns>
    public AnomalyEngine GetAnomalyEngine(InstanceCapabilityConfig instanceConfig)
    {
        return _anomalyEngines.GetOrAdd(instanceConfig.InstanceId, _ =>
        {
            var provider = GetAIProvider(instanceConfig, Capabilities.AnomalyDetection);
            return new AnomalyEngine(provider as IExtendedAIProvider, _config.Anomaly);
        });
    }

    /// <summary>
    /// Gets or creates a cost engine for the specified instance.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <returns>The cost engine for the instance.</returns>
    public CostEngine GetCostEngine(InstanceCapabilityConfig instanceConfig)
    {
        return _costEngines.GetOrAdd(instanceConfig.InstanceId, _ =>
        {
            var provider = GetAIProvider(instanceConfig, Capabilities.CostOptimization);
            return new CostEngine(provider as IExtendedAIProvider, _config.Cost);
        });
    }

    /// <summary>
    /// Gets or creates a performance engine for the specified instance.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <returns>The performance engine for the instance.</returns>
    public PerformanceEngine GetPerformanceEngine(InstanceCapabilityConfig instanceConfig)
    {
        return _performanceEngines.GetOrAdd(instanceConfig.InstanceId, _ =>
        {
            var provider = GetAIProvider(instanceConfig, Capabilities.PerformanceTuning);
            return new PerformanceEngine(provider as IExtendedAIProvider, _config.Performance);
        });
    }

    /// <summary>
    /// Gets or creates prediction models for the specified instance.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <returns>The prediction models for the instance.</returns>
    public PredictionModels GetPredictionModels(InstanceCapabilityConfig instanceConfig)
    {
        return _predictionModels.GetOrAdd(instanceConfig.InstanceId, _ =>
        {
            var provider = GetAIProvider(instanceConfig, Capabilities.FailurePrediction);
            return new PredictionModels(provider as IExtendedAIProvider, _config.Prediction);
        });
    }

    #endregion

    #region High-Level Operations

    /// <summary>
    /// Runs a comprehensive AIOps optimization cycle for an instance.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The optimization cycle result.</returns>
    public async Task<AIOpsOptimizationResult> RunOptimizationCycleAsync(
        InstanceCapabilityConfig instanceConfig,
        CancellationToken ct = default)
    {
        var result = new AIOpsOptimizationResult
        {
            InstanceId = instanceConfig.InstanceId,
            StartTime = DateTime.UtcNow
        };

        var tasks = new List<Task>();

        // Run tiering optimization if enabled
        if (IsCapabilityEnabled(instanceConfig, Capabilities.PredictiveTiering))
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var engine = GetTieringEngine(instanceConfig);
                    result.TieringResult = await engine.OptimizationCycleAsync(ct);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Tiering: {ex.Message}");
                }
            }, ct));
        }

        // Run scaling forecast if enabled
        if (IsCapabilityEnabled(instanceConfig, Capabilities.PredictiveScaling))
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var engine = GetScalingEngine(instanceConfig);
                    result.ScalingResult = await engine.ForecastCycleAsync(ct);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Scaling: {ex.Message}");
                }
            }, ct));
        }

        // Run anomaly detection if enabled
        if (IsCapabilityEnabled(instanceConfig, Capabilities.AnomalyDetection))
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var engine = GetAnomalyEngine(instanceConfig);
                    result.AnomalyResult = await engine.DetectionCycleAsync(ct);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Anomaly: {ex.Message}");
                }
            }, ct));
        }

        // Run cost analysis if enabled
        if (IsCapabilityEnabled(instanceConfig, Capabilities.CostOptimization))
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var engine = GetCostEngine(instanceConfig);
                    result.CostResult = await engine.AnalysisCycleAsync(ct);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Cost: {ex.Message}");
                }
            }, ct));
        }

        // Run performance tuning if enabled
        if (IsCapabilityEnabled(instanceConfig, Capabilities.PerformanceTuning))
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var engine = GetPerformanceEngine(instanceConfig);
                    result.PerformanceResult = await engine.TuningCycleAsync(ct);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Performance: {ex.Message}");
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        result.EndTime = DateTime.UtcNow;
        result.Success = result.Errors.Count == 0;

        return result;
    }

    /// <summary>
    /// Gets comprehensive AIOps statistics for an instance.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <returns>The AIOps statistics.</returns>
    public AIOpsStatistics GetStatistics(InstanceCapabilityConfig instanceConfig)
    {
        var stats = new AIOpsStatistics
        {
            InstanceId = instanceConfig.InstanceId,
            GeneratedAt = DateTime.UtcNow
        };

        if (_tieringEngines.TryGetValue(instanceConfig.InstanceId, out var tieringEngine))
        {
            stats.Tiering = tieringEngine.GetStatistics();
        }

        if (_scalingEngines.TryGetValue(instanceConfig.InstanceId, out var scalingEngine))
        {
            stats.Scaling = scalingEngine.GetStatistics();
        }

        if (_anomalyEngines.TryGetValue(instanceConfig.InstanceId, out var anomalyEngine))
        {
            stats.Anomaly = anomalyEngine.GetStatistics();
        }

        if (_costEngines.TryGetValue(instanceConfig.InstanceId, out var costEngine))
        {
            stats.Cost = costEngine.GetStatistics();
        }

        if (_performanceEngines.TryGetValue(instanceConfig.InstanceId, out var performanceEngine))
        {
            stats.Performance = performanceEngine.GetStatistics();
        }

        if (_predictionModels.TryGetValue(instanceConfig.InstanceId, out var predictionModels))
        {
            stats.Prediction = predictionModels.GetStatistics();
        }

        return stats;
    }

    #endregion

    #region Private Methods

    private DataWarehouse.SDK.AI.IAIProvider? GetAIProvider(InstanceCapabilityConfig instanceConfig, string capability)
    {
        var providerId = GetProviderForCapability(instanceConfig, capability);
        if (string.IsNullOrEmpty(providerId))
        {
            return _providerRegistry.GetDefaultProvider();
        }

        return _providerRegistry.GetProvider(providerId) ?? _providerRegistry.GetDefaultProvider();
    }

    #endregion

    /// <summary>
    /// Releases all resources used by the handler.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        foreach (var engine in _tieringEngines.Values)
            engine.Dispose();

        foreach (var engine in _scalingEngines.Values)
            engine.Dispose();

        foreach (var engine in _anomalyEngines.Values)
            engine.Dispose();

        foreach (var engine in _costEngines.Values)
            engine.Dispose();

        foreach (var engine in _performanceEngines.Values)
            engine.Dispose();

        _tieringEngines.Clear();
        _scalingEngines.Clear();
        _anomalyEngines.Clear();
        _costEngines.Clear();
        _performanceEngines.Clear();
        _predictionModels.Clear();
    }
}

#region Configuration

/// <summary>
/// Main configuration for the AIOps capability handler.
/// </summary>
public sealed class AIOpsConfiguration
{
    /// <summary>Gets or sets tiering engine configuration.</summary>
    public TieringEngineConfig Tiering { get; set; } = new();

    /// <summary>Gets or sets scaling engine configuration.</summary>
    public ScalingEngineConfig Scaling { get; set; } = new();

    /// <summary>Gets or sets anomaly engine configuration.</summary>
    public AnomalyEngineConfig Anomaly { get; set; } = new();

    /// <summary>Gets or sets cost engine configuration.</summary>
    public CostEngineConfig Cost { get; set; } = new();

    /// <summary>Gets or sets performance engine configuration.</summary>
    public PerformanceEngineConfig Performance { get; set; } = new();

    /// <summary>Gets or sets prediction models configuration.</summary>
    public PredictionModelsConfig Prediction { get; set; } = new();

    /// <summary>Gets or sets whether to enable autonomous background operations.</summary>
    public bool EnableAutonomousOperations { get; set; } = true;

    /// <summary>Gets or sets the interval for autonomous optimization cycles in minutes.</summary>
    public int OptimizationIntervalMinutes { get; set; } = 15;
}

#endregion

#region Results

/// <summary>
/// Result of a comprehensive AIOps optimization cycle.
/// </summary>
public sealed class AIOpsOptimizationResult
{
    /// <summary>Gets or sets the instance identifier.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>Gets or sets whether the optimization succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the start time of the optimization.</summary>
    public DateTime StartTime { get; set; }

    /// <summary>Gets or sets the end time of the optimization.</summary>
    public DateTime EndTime { get; set; }

    /// <summary>Gets the duration of the optimization.</summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>Gets or sets the tiering optimization result.</summary>
    public TieringOptimizationResult? TieringResult { get; set; }

    /// <summary>Gets or sets the scaling forecast result.</summary>
    public ScalingForecastCycleResult? ScalingResult { get; set; }

    /// <summary>Gets or sets the anomaly detection result.</summary>
    public AnomalyDetectionCycleResult? AnomalyResult { get; set; }

    /// <summary>Gets or sets the cost analysis result.</summary>
    public CostAnalysisResult? CostResult { get; set; }

    /// <summary>Gets or sets the performance tuning result.</summary>
    public TuningCycleResult? PerformanceResult { get; set; }

    /// <summary>Gets or sets any errors that occurred.</summary>
    public List<string> Errors { get; init; } = new();
}

/// <summary>
/// Comprehensive AIOps statistics for an instance.
/// </summary>
public sealed class AIOpsStatistics
{
    /// <summary>Gets or sets the instance identifier.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>Gets or sets when the statistics were generated.</summary>
    public DateTime GeneratedAt { get; init; }

    /// <summary>Gets or sets tiering statistics.</summary>
    public TieringStatistics? Tiering { get; set; }

    /// <summary>Gets or sets scaling statistics.</summary>
    public ScalingStatistics? Scaling { get; set; }

    /// <summary>Gets or sets anomaly statistics.</summary>
    public AnomalyStatistics? Anomaly { get; set; }

    /// <summary>Gets or sets cost statistics.</summary>
    public CostEngineStatistics? Cost { get; set; }

    /// <summary>Gets or sets performance statistics.</summary>
    public PerformanceStatistics? Performance { get; set; }

    /// <summary>Gets or sets prediction statistics.</summary>
    public PredictionStatistics? Prediction { get; set; }
}

#endregion
