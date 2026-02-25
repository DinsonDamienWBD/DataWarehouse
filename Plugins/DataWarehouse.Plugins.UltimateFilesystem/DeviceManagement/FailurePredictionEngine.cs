using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

/// <summary>
/// Configuration for the EWMA-based failure prediction engine.
/// </summary>
/// <param name="EwmaAlpha">Smoothing factor for EWMA (0-1). Higher values weight recent samples more.</param>
/// <param name="TemperatureThresholdCelsius">Temperature threshold above which device is considered at risk.</param>
/// <param name="WearLevelThresholdPercent">Wear level percentage threshold for risk assessment.</param>
/// <param name="ErrorRateThresholdPerHour">Error rate per hour above which device is considered at risk.</param>
/// <param name="MinSamplesForPrediction">Minimum number of samples before generating meaningful predictions.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: EWMA failure prediction config (BMDV-04)")]
public sealed record FailurePredictionConfig(
    double EwmaAlpha = 0.3,
    double TemperatureThresholdCelsius = 70.0,
    double WearLevelThresholdPercent = 90.0,
    double ErrorRateThresholdPerHour = 1.0,
    int MinSamplesForPrediction = 5);

/// <summary>
/// Per-device EWMA state tracking for failure prediction.
/// </summary>
internal sealed record DeviceEwmaState
{
    public double EwmaTemperature { get; set; }
    public double EwmaErrorRate { get; set; }
    public double EwmaWearRate { get; set; }
    public DateTime LastUpdate { get; set; }
    public int SampleCount { get; set; }
    public long LastUncorrectableErrors { get; set; }
    public double LastWearLevelPercent { get; set; }
}

/// <summary>
/// Result of failure prediction for a single device.
/// </summary>
/// <param name="DeviceId">Unique device identifier.</param>
/// <param name="IsAtRisk">Whether the device is currently at risk of failure.</param>
/// <param name="EstimatedTimeToFailure">Estimated time until failure based on current trends, or null if not predictable.</param>
/// <param name="RiskLevel">Risk classification: None, Low, Medium, High, or Critical.</param>
/// <param name="RiskFactors">Descriptions of which metrics are concerning.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: EWMA failure prediction result (BMDV-04)")]
public sealed record FailurePrediction(
    string DeviceId,
    bool IsAtRisk,
    TimeSpan? EstimatedTimeToFailure,
    string RiskLevel,
    string[] RiskFactors);

/// <summary>
/// EWMA-based failure prediction engine that maintains per-device exponentially weighted
/// moving average state for temperature, error rate, and wear rate. Produces risk level
/// assessments (None/Low/Medium/High/Critical) with estimated time-to-failure.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: EWMA failure prediction (BMDV-04)")]
public sealed class FailurePredictionEngine
{
    private readonly FailurePredictionConfig _config;
    private readonly BoundedDictionary<string, DeviceEwmaState> _ewmaStates;
    private readonly BoundedDictionary<string, FailurePrediction> _lastPredictions;

    /// <summary>
    /// Initializes a new FailurePredictionEngine with optional configuration.
    /// </summary>
    /// <param name="config">Prediction configuration. Uses defaults if null.</param>
    public FailurePredictionEngine(FailurePredictionConfig? config = null)
    {
        _config = config ?? new FailurePredictionConfig();
        _ewmaStates = new BoundedDictionary<string, DeviceEwmaState>(1000);
        _lastPredictions = new BoundedDictionary<string, FailurePrediction>(1000);
    }

    /// <summary>
    /// Updates EWMA state with new health data and produces a failure prediction.
    /// </summary>
    /// <param name="deviceId">Unique device identifier.</param>
    /// <param name="health">Current SMART health snapshot from SmartMonitor.</param>
    /// <returns>Updated failure prediction for the device.</returns>
    public FailurePrediction UpdateAndPredict(string deviceId, PhysicalDeviceHealth health)
    {
        var now = DateTime.UtcNow;

        if (!_ewmaStates.TryGetValue(deviceId, out var state))
        {
            state = new DeviceEwmaState
            {
                EwmaTemperature = health.TemperatureCelsius >= 0 ? health.TemperatureCelsius : 0,
                EwmaErrorRate = 0,
                EwmaWearRate = 0,
                LastUpdate = now,
                SampleCount = 0,
                LastUncorrectableErrors = health.UncorrectableErrors,
                LastWearLevelPercent = health.WearLevelPercent
            };
        }

        var alpha = _config.EwmaAlpha;
        var elapsed = now - state.LastUpdate;
        var hoursElapsed = Math.Max(elapsed.TotalHours, 0.001); // Avoid division by zero

        // Update EWMA for temperature
        if (health.TemperatureCelsius >= 0)
        {
            state.EwmaTemperature = alpha * health.TemperatureCelsius + (1 - alpha) * state.EwmaTemperature;
        }

        // Compute error rate: errors per hour since last sample
        var errorDelta = health.UncorrectableErrors - state.LastUncorrectableErrors;
        if (errorDelta < 0) errorDelta = 0; // Counter may reset
        var errorRate = errorDelta / hoursElapsed;
        state.EwmaErrorRate = alpha * errorRate + (1 - alpha) * state.EwmaErrorRate;

        // Compute wear rate: wear% per hour since last sample
        var wearDelta = health.WearLevelPercent - state.LastWearLevelPercent;
        if (wearDelta < 0) wearDelta = 0; // Wear should not decrease
        var wearRate = wearDelta / hoursElapsed;
        state.EwmaWearRate = alpha * wearRate + (1 - alpha) * state.EwmaWearRate;

        // Update last-known values
        state.LastUpdate = now;
        state.SampleCount++;
        state.LastUncorrectableErrors = health.UncorrectableErrors;
        state.LastWearLevelPercent = health.WearLevelPercent;

        _ewmaStates[deviceId] = state;

        // Generate prediction
        var riskFactors = new List<string>();
        var riskLevel = "None";

        if (state.SampleCount >= _config.MinSamplesForPrediction)
        {
            // Evaluate temperature
            EvaluateMetric(state.EwmaTemperature, _config.TemperatureThresholdCelsius,
                "Temperature", "C", riskFactors, ref riskLevel);

            // Evaluate error rate
            EvaluateMetric(state.EwmaErrorRate, _config.ErrorRateThresholdPerHour,
                "Error rate", "/hr", riskFactors, ref riskLevel);

            // Evaluate wear level (direct, not rate-based)
            EvaluateMetric(health.WearLevelPercent, _config.WearLevelThresholdPercent,
                "Wear level", "%", riskFactors, ref riskLevel);
        }
        else
        {
            // Not enough samples yet; check for obvious issues only
            if (!health.IsHealthy)
            {
                riskLevel = "High";
                riskFactors.Add("Device self-assessment reports unhealthy");
            }

            if (health.TemperatureCelsius >= 0 && health.TemperatureCelsius > _config.TemperatureThresholdCelsius * 1.5)
            {
                riskLevel = HigherRisk(riskLevel, "Critical");
                riskFactors.Add($"Temperature critically high: {health.TemperatureCelsius:F1}C");
            }
        }

        // Device self-assessment override
        if (!health.IsHealthy && riskLevel is "None" or "Low")
        {
            riskLevel = "Medium";
            if (!riskFactors.Any(f => f.Contains("self-assessment", StringComparison.Ordinal)))
            {
                riskFactors.Add("Device self-assessment reports unhealthy");
            }
        }

        // Estimate time to failure from wear rate
        TimeSpan? estimatedTtf = null;
        if (state.EwmaWearRate > 0 && health.WearLevelPercent < 100)
        {
            var remainingWear = 100.0 - health.WearLevelPercent;
            var hoursToFailure = remainingWear / state.EwmaWearRate;
            if (hoursToFailure > 0 && hoursToFailure < 87600) // Cap at 10 years
            {
                estimatedTtf = TimeSpan.FromHours(hoursToFailure);
            }
        }
        else if (state.EwmaErrorRate > _config.ErrorRateThresholdPerHour && state.EwmaErrorRate > 0)
        {
            // Rough estimate from error rate trend: assume failure at 10x current error count
            var hoursToFailure = (health.UncorrectableErrors * 10.0) / state.EwmaErrorRate;
            if (hoursToFailure > 0 && hoursToFailure < 87600)
            {
                estimatedTtf = TimeSpan.FromHours(hoursToFailure);
            }
        }

        bool isAtRisk = riskLevel is not ("None" or "Low");

        var prediction = new FailurePrediction(
            DeviceId: deviceId,
            IsAtRisk: isAtRisk,
            EstimatedTimeToFailure: estimatedTtf,
            RiskLevel: riskLevel,
            RiskFactors: riskFactors.ToArray());

        _lastPredictions[deviceId] = prediction;

        return prediction;
    }

    /// <summary>
    /// Clears EWMA state for a specific device.
    /// </summary>
    /// <param name="deviceId">Device identifier to reset.</param>
    public void Reset(string deviceId)
    {
        _ewmaStates.TryRemove(deviceId, out _);
        _lastPredictions.TryRemove(deviceId, out _);
    }

    /// <summary>
    /// Gets the current prediction state for all tracked devices.
    /// </summary>
    /// <returns>Dictionary of device ID to most recent failure prediction.</returns>
    public IReadOnlyDictionary<string, FailurePrediction> GetAllPredictions()
    {
        var result = new Dictionary<string, FailurePrediction>();
        foreach (var kvp in _lastPredictions)
        {
            result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    private static void EvaluateMetric(double ewmaValue, double threshold,
        string metricName, string unit, List<string> riskFactors, ref string riskLevel)
    {
        if (ewmaValue > threshold * 1.5)
        {
            riskLevel = HigherRisk(riskLevel, "Critical");
            riskFactors.Add($"{metricName} critically high: {ewmaValue:F2}{unit} (threshold: {threshold:F1}{unit})");
        }
        else if (ewmaValue > threshold)
        {
            riskLevel = HigherRisk(riskLevel, "High");
            riskFactors.Add($"{metricName} exceeds threshold: {ewmaValue:F2}{unit} (threshold: {threshold:F1}{unit})");
        }
        else if (ewmaValue > threshold * 0.8)
        {
            riskLevel = HigherRisk(riskLevel, "Medium");
            riskFactors.Add($"{metricName} approaching threshold: {ewmaValue:F2}{unit} (threshold: {threshold:F1}{unit})");
        }
        else if (ewmaValue > threshold * 0.5)
        {
            // Only note trending upward if currently at None
            if (riskLevel == "None")
            {
                riskLevel = "Low";
                riskFactors.Add($"{metricName} trending upward: {ewmaValue:F2}{unit}");
            }
        }
    }

    private static string HigherRisk(string current, string candidate)
    {
        var order = new[] { "None", "Low", "Medium", "High", "Critical" };
        var currentIdx = Array.IndexOf(order, current);
        var candidateIdx = Array.IndexOf(order, candidate);
        return candidateIdx > currentIdx ? candidate : current;
    }
}
