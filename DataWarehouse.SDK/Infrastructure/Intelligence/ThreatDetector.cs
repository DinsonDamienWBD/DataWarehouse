using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Threat severity levels for security signal assessment.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-04)")]
public enum ThreatLevel
{
    /// <summary>No active threat signals.</summary>
    None = 0,

    /// <summary>Minor anomalies detected but below concern threshold.</summary>
    Low = 1,

    /// <summary>Multiple signals suggest elevated risk; consider policy tightening.</summary>
    Elevated = 2,

    /// <summary>Significant threat activity; policy tightening recommended.</summary>
    High = 3,

    /// <summary>Critical threat detected; immediate policy enforcement required.</summary>
    Critical = 4
}

/// <summary>
/// A single threat signal detected from observation analysis.
/// </summary>
/// <param name="SignalType">Type of threat signal (e.g., "auth_failure_spike", "anomaly_rate_high").</param>
/// <param name="Confidence">Confidence in this signal, 0.0 to 1.0.</param>
/// <param name="Description">Human-readable description of what triggered this signal.</param>
/// <param name="DetectedAt">When this signal was first detected.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-04)")]
public sealed record ThreatSignal(
    string SignalType,
    double Confidence,
    string Description,
    DateTimeOffset DetectedAt
);

/// <summary>
/// Composite threat assessment computed from active signals.
/// </summary>
/// <param name="Level">Overall threat level.</param>
/// <param name="ThreatScore">Composite threat score from 0.0 (safe) to 1.0 (critical).</param>
/// <param name="ActiveSignals">Currently active threat signals contributing to the score.</param>
/// <param name="ShouldTightenPolicy">True if threat level meets or exceeds the configured threshold.</param>
/// <param name="AssessedAt">When this assessment was computed.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-04)")]
public sealed record ThreatAssessment(
    ThreatLevel Level,
    double ThreatScore,
    List<ThreatSignal> ActiveSignals,
    bool ShouldTightenPolicy,
    DateTimeOffset AssessedAt
);

/// <summary>
/// Detects security threats from observation events using sliding-window analysis.
/// Tracks anomaly rates, authentication failure spikes, access pattern anomalies,
/// and potential data exfiltration attempts. When threat score exceeds the configured
/// threshold, recommends policy tightening via cascade strategy escalation.
/// </summary>
/// <remarks>
/// Implements AIPI-04. Four signal types are monitored:
/// <list type="bullet">
///   <item><description>anomaly_rate_high: anomaly count exceeds 10 in 5 minutes</description></item>
///   <item><description>auth_failure_spike: auth failures exceed 5 per minute</description></item>
///   <item><description>access_pattern_anomaly: single plugin spikes > 5x rolling average</description></item>
///   <item><description>data_exfiltration_attempt: sudden 10x spike in data_read_bytes</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-04)")]
public sealed class ThreatDetector : IAiAdvisor
{
    /// <summary>
    /// Sliding window duration for threat signal analysis.
    /// </summary>
    private static readonly TimeSpan SlidingWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Short window for rate-based signals (auth failures per minute).
    /// </summary>
    private static readonly TimeSpan ShortWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Signal weights for composite threat score calculation.
    /// </summary>
    private static readonly Dictionary<string, double> SignalWeights = new()
    {
        ["anomaly_rate_high"] = 0.25,
        ["auth_failure_spike"] = 0.30,
        ["access_pattern_anomaly"] = 0.20,
        ["data_exfiltration_attempt"] = 0.40
    };

    private readonly double _configuredThreshold;
    private readonly ConcurrentQueue<ObservationEvent> _anomalyWindow = new();
    private readonly ConcurrentQueue<ObservationEvent> _authFailureWindow = new();
    private readonly ConcurrentDictionary<string, PluginObservationTracker> _pluginRates = new();
    private readonly ConcurrentQueue<ObservationEvent> _dataReadWindow = new();

    private volatile ThreatAssessment _currentAssessment;

    /// <summary>
    /// Creates a new ThreatDetector with the specified policy tightening threshold.
    /// </summary>
    /// <param name="configuredThreshold">
    /// Threat score threshold (0.0-1.0) at which ShouldTightenPolicy becomes true.
    /// Maps to ThreatLevel.Elevated (0.3) by default.
    /// </param>
    public ThreatDetector(double configuredThreshold = 0.3)
    {
        _configuredThreshold = Math.Clamp(configuredThreshold, 0.0, 1.0);
        _currentAssessment = new ThreatAssessment(
            ThreatLevel.None,
            0.0,
            new List<ThreatSignal>(),
            false,
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public string AdvisorId => "threat_detector";

    /// <summary>
    /// The current threat assessment. Updated atomically after each observation batch.
    /// </summary>
    public ThreatAssessment CurrentAssessment => _currentAssessment;

    /// <summary>
    /// Recommended cascade strategy based on current threat level.
    /// Returns MostRestrictive when High, Enforce when Critical, MostRestrictive otherwise.
    /// </summary>
    public CascadeStrategy RecommendedCascade
    {
        get
        {
            var assessment = _currentAssessment;
            return assessment.Level switch
            {
                ThreatLevel.Critical => CascadeStrategy.Enforce,
                ThreatLevel.High => CascadeStrategy.MostRestrictive,
                _ => CascadeStrategy.MostRestrictive
            };
        }
    }

    /// <inheritdoc />
    public Task ProcessObservationsAsync(IReadOnlyList<ObservationEvent> batch, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset windowStart = now - SlidingWindow;
        DateTimeOffset shortWindowStart = now - ShortWindow;

        // Classify and enqueue observations into tracking windows
        for (int i = 0; i < batch.Count; i++)
        {
            var obs = batch[i];

            // Track anomaly observations
            if (obs.AnomalyType is not null)
            {
                _anomalyWindow.Enqueue(obs);
            }

            // Track auth failure observations
            if (obs.MetricName.Contains("auth_fail", StringComparison.OrdinalIgnoreCase))
            {
                _authFailureWindow.Enqueue(obs);
            }

            // Track per-plugin observation rates
            var tracker = _pluginRates.GetOrAdd(obs.PluginId, _ => new PluginObservationTracker());
            tracker.Record(obs.Timestamp);

            // Track data read bytes for exfiltration detection
            if (obs.MetricName.Equals("data_read_bytes", StringComparison.OrdinalIgnoreCase))
            {
                _dataReadWindow.Enqueue(obs);
            }
        }

        // Evict stale entries from sliding windows
        EvictStale(_anomalyWindow, windowStart);
        EvictStale(_authFailureWindow, shortWindowStart);
        EvictStale(_dataReadWindow, windowStart);

        // Detect signals
        var signals = new List<ThreatSignal>(4);

        DetectAnomalyRate(signals, now);
        DetectAuthFailureSpike(signals, now);
        DetectAccessPatternAnomaly(signals, now);
        DetectDataExfiltration(signals, now);

        // Compute composite threat score
        double threatScore = ComputeThreatScore(signals);
        ThreatLevel level = ClassifyThreatLevel(threatScore);
        bool shouldTighten = threatScore >= _configuredThreshold;

        // Atomic swap of assessment
        _currentAssessment = new ThreatAssessment(
            level,
            threatScore,
            signals,
            shouldTighten,
            now);

        return Task.CompletedTask;
    }

    private void DetectAnomalyRate(List<ThreatSignal> signals, DateTimeOffset now)
    {
        int anomalyCount = _anomalyWindow.Count;
        if (anomalyCount > 10)
        {
            double confidence = Math.Min(1.0, anomalyCount / 20.0);
            signals.Add(new ThreatSignal(
                "anomaly_rate_high",
                confidence,
                $"Anomaly rate elevated: {anomalyCount} anomalies in sliding window (threshold: 10)",
                now));
        }
    }

    private void DetectAuthFailureSpike(List<ThreatSignal> signals, DateTimeOffset now)
    {
        int authFailCount = _authFailureWindow.Count;
        if (authFailCount > 5)
        {
            double confidence = Math.Min(1.0, authFailCount / 10.0);
            signals.Add(new ThreatSignal(
                "auth_failure_spike",
                confidence,
                $"Authentication failure spike: {authFailCount} failures in last minute (threshold: 5)",
                now));
        }
    }

    private void DetectAccessPatternAnomaly(List<ThreatSignal> signals, DateTimeOffset now)
    {
        foreach (var kvp in _pluginRates)
        {
            var tracker = kvp.Value;
            double rollingAverage = tracker.GetRollingAverage();
            double currentRate = tracker.GetCurrentRate();

            if (rollingAverage > 0 && currentRate > rollingAverage * 5.0)
            {
                double spikeRatio = currentRate / rollingAverage;
                double confidence = Math.Min(1.0, spikeRatio / 10.0);
                signals.Add(new ThreatSignal(
                    "access_pattern_anomaly",
                    confidence,
                    $"Plugin '{kvp.Key}' observation rate {currentRate:F1}/min is {spikeRatio:F1}x above rolling average {rollingAverage:F1}/min",
                    now));
                break; // One signal per batch for access patterns
            }
        }
    }

    private void DetectDataExfiltration(List<ThreatSignal> signals, DateTimeOffset now)
    {
        // Check for sudden 10x spike in data_read_bytes values
        var readings = _dataReadWindow.ToArray();
        if (readings.Length < 2) return;

        // Split into recent (last 30s) vs older
        DateTimeOffset recentCutoff = now - TimeSpan.FromSeconds(30);
        double recentSum = 0;
        int recentCount = 0;
        double olderSum = 0;
        int olderCount = 0;

        for (int i = 0; i < readings.Length; i++)
        {
            if (readings[i].Timestamp >= recentCutoff)
            {
                recentSum += readings[i].Value;
                recentCount++;
            }
            else
            {
                olderSum += readings[i].Value;
                olderCount++;
            }
        }

        if (olderCount > 0 && recentCount > 0)
        {
            double olderAvg = olderSum / olderCount;
            double recentAvg = recentSum / recentCount;

            if (olderAvg > 0 && recentAvg > olderAvg * 10.0)
            {
                double spikeRatio = recentAvg / olderAvg;
                double confidence = Math.Min(1.0, spikeRatio / 20.0);
                signals.Add(new ThreatSignal(
                    "data_exfiltration_attempt",
                    confidence,
                    $"Data read volume spike: recent avg {recentAvg:F0} bytes vs baseline {olderAvg:F0} bytes ({spikeRatio:F1}x increase)",
                    now));
            }
        }
    }

    private static double ComputeThreatScore(List<ThreatSignal> signals)
    {
        if (signals.Count == 0) return 0.0;

        double weightedSum = 0.0;
        for (int i = 0; i < signals.Count; i++)
        {
            double weight = SignalWeights.TryGetValue(signals[i].SignalType, out var w) ? w : 0.2;
            weightedSum += signals[i].Confidence * weight;
        }

        return Math.Min(1.0, weightedSum);
    }

    private static ThreatLevel ClassifyThreatLevel(double score) => score switch
    {
        <= 0.1 => ThreatLevel.None,
        <= 0.3 => ThreatLevel.Low,
        <= 0.6 => ThreatLevel.Elevated,
        <= 0.8 => ThreatLevel.High,
        _ => ThreatLevel.Critical
    };

    private static void EvictStale(ConcurrentQueue<ObservationEvent> queue, DateTimeOffset cutoff)
    {
        while (queue.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
        {
            queue.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Tracks per-plugin observation rates for access pattern anomaly detection.
    /// Maintains a rolling average over the last 5 minutes and a current-minute rate.
    /// </summary>
    private sealed class PluginObservationTracker
    {
        private readonly ConcurrentQueue<DateTimeOffset> _timestamps = new();
        private long _totalCount;

        public void Record(DateTimeOffset timestamp)
        {
            _timestamps.Enqueue(timestamp);
            Interlocked.Increment(ref _totalCount);

            // Evict entries older than 10 minutes to bound memory
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
            while (_timestamps.TryPeek(out var oldest) && oldest < cutoff)
            {
                _timestamps.TryDequeue(out _);
            }
        }

        /// <summary>
        /// Gets the rolling average observations per minute over the tracked window.
        /// </summary>
        public double GetRollingAverage()
        {
            var timestamps = _timestamps.ToArray();
            if (timestamps.Length < 2) return 0;

            DateTimeOffset oldest = timestamps[0];
            DateTimeOffset newest = timestamps[^1];
            double minutes = (newest - oldest).TotalMinutes;

            return minutes > 0 ? timestamps.Length / minutes : 0;
        }

        /// <summary>
        /// Gets the observation rate in the last 60 seconds.
        /// </summary>
        public double GetCurrentRate()
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
            var timestamps = _timestamps.ToArray();
            int recentCount = 0;

            for (int i = timestamps.Length - 1; i >= 0; i--)
            {
                if (timestamps[i] >= cutoff)
                    recentCount++;
                else
                    break;
            }

            return recentCount; // Per minute
        }
    }
}
