using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Per-algorithm compute cost tracking with cloud billing estimation.
/// </summary>
/// <param name="AlgorithmId">Identifier of the algorithm (parsed from metric name).</param>
/// <param name="OperationCount">Total number of operations observed.</param>
/// <param name="AverageDurationMs">Rolling average duration per operation in milliseconds.</param>
/// <param name="EstimatedCpuCostPerOp">Estimated CPU-seconds consumed per operation.</param>
/// <param name="EstimatedCloudCostPerOp">Estimated cloud cost in USD per operation.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-05)")]
public sealed record AlgorithmCost(
    string AlgorithmId,
    long OperationCount,
    double AverageDurationMs,
    double EstimatedCpuCostPerOp,
    double EstimatedCloudCostPerOp
);

/// <summary>
/// Aggregate cost profile across all tracked algorithms with cloud billing projections.
/// </summary>
/// <param name="AlgorithmCosts">Per-algorithm cost breakdown.</param>
/// <param name="EstimatedHourlyCostUsd">Projected hourly cloud cost based on current operation rates.</param>
/// <param name="EstimatedMonthlyCostUsd">Projected monthly cloud cost (hourly * 730).</param>
/// <param name="MostExpensiveAlgorithm">Algorithm with the highest per-operation cost.</param>
/// <param name="MostEfficientAlgorithm">Algorithm with the lowest per-operation cost.</param>
/// <param name="CalculatedAt">When this profile was last computed.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-05)")]
public sealed record CostProfile(
    Dictionary<string, AlgorithmCost> AlgorithmCosts,
    double EstimatedHourlyCostUsd,
    double EstimatedMonthlyCostUsd,
    string MostExpensiveAlgorithm,
    string MostEfficientAlgorithm,
    DateTimeOffset CalculatedAt
);

/// <summary>
/// Analyzes per-algorithm compute costs from observation events and projects cloud billing
/// estimates. Tracks algorithm operation counts, durations, and CPU-second costs to provide
/// a real-time cost profile for policy decision rationale.
/// </summary>
/// <remarks>
/// Implements AIPI-05. Monitors observations matching "algorithm_*_duration_ms",
/// "encryption_*_ms", and "compression_*_ms" metric patterns. The cloud cost rate
/// is configurable (default $0.000012/CPU-second for general compute).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-05)")]
public sealed class CostAnalyzer : IAiAdvisor
{
    /// <summary>
    /// Average number of hours in a month for billing projection.
    /// </summary>
    private const double HoursPerMonth = 730.0;

    private readonly double _cpuCostPerSecondUsd;
    private readonly ConcurrentDictionary<string, AlgorithmTracker> _trackers = new();
    private readonly ConcurrentQueue<DateTimeOffset> _operationTimestamps = new();

    private volatile CostProfile _currentProfile;

    /// <summary>
    /// Creates a new CostAnalyzer with the specified cloud billing rate.
    /// </summary>
    /// <param name="cpuCostPerSecondUsd">
    /// Cloud billing rate per CPU-second. Default $0.000012 for general compute.
    /// Adjust for specific cloud provider pricing (e.g., GPU instances, spot pricing).
    /// </param>
    public CostAnalyzer(double cpuCostPerSecondUsd = 0.000012)
    {
        _cpuCostPerSecondUsd = cpuCostPerSecondUsd;
        _currentProfile = new CostProfile(
            new Dictionary<string, AlgorithmCost>(),
            0.0,
            0.0,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public string AdvisorId => "cost_analyzer";

    /// <summary>
    /// The current cost profile. Updated atomically after each observation batch.
    /// </summary>
    public CostProfile CurrentProfile => _currentProfile;

    /// <summary>
    /// Gets the cost tracking for a specific algorithm, or null if not yet observed.
    /// </summary>
    /// <param name="algorithmId">The algorithm identifier to look up.</param>
    /// <returns>The algorithm's cost data, or null if no observations have been recorded.</returns>
    public AlgorithmCost? GetCostForAlgorithm(string algorithmId)
    {
        if (_trackers.TryGetValue(algorithmId, out var tracker))
        {
            return tracker.ToAlgorithmCost(_cpuCostPerSecondUsd);
        }
        return null;
    }

    /// <inheritdoc />
    public Task ProcessObservationsAsync(IReadOnlyList<ObservationEvent> batch, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < batch.Count; i++)
        {
            var obs = batch[i];
            string? algorithmId = TryParseAlgorithmId(obs.MetricName);
            if (algorithmId is null) continue;

            var tracker = _trackers.GetOrAdd(algorithmId, _ => new AlgorithmTracker(algorithmId));
            tracker.Record(obs.Value, obs.Timestamp);

            _operationTimestamps.Enqueue(obs.Timestamp);
        }

        // Evict operation timestamps older than 1 hour for rate calculation
        DateTimeOffset hourAgo = now - TimeSpan.FromHours(1);
        while (_operationTimestamps.TryPeek(out var oldest) && oldest < hourAgo)
        {
            _operationTimestamps.TryDequeue(out _);
        }

        // Rebuild cost profile
        RebuildProfile(now);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Attempts to parse an algorithm ID from a metric name.
    /// Matches patterns: algorithm_{id}_duration_ms, encryption_{id}_ms, compression_{id}_ms.
    /// </summary>
    private static string? TryParseAlgorithmId(string metricName)
    {
        // algorithm_{id}_duration_ms
        if (metricName.StartsWith("algorithm_", StringComparison.OrdinalIgnoreCase) &&
            metricName.EndsWith("_duration_ms", StringComparison.OrdinalIgnoreCase))
        {
            int start = "algorithm_".Length;
            int end = metricName.Length - "_duration_ms".Length;
            if (end > start)
                return metricName[start..end];
        }

        // encryption_{id}_ms
        if (metricName.StartsWith("encryption_", StringComparison.OrdinalIgnoreCase) &&
            metricName.EndsWith("_ms", StringComparison.OrdinalIgnoreCase))
        {
            int start = "encryption_".Length;
            int end = metricName.Length - "_ms".Length;
            if (end > start)
                return "enc_" + metricName[start..end];
        }

        // compression_{id}_ms
        if (metricName.StartsWith("compression_", StringComparison.OrdinalIgnoreCase) &&
            metricName.EndsWith("_ms", StringComparison.OrdinalIgnoreCase))
        {
            int start = "compression_".Length;
            int end = metricName.Length - "_ms".Length;
            if (end > start)
                return "cmp_" + metricName[start..end];
        }

        return null;
    }

    private void RebuildProfile(DateTimeOffset now)
    {
        var costs = new Dictionary<string, AlgorithmCost>();
        string mostExpensive = string.Empty;
        string mostEfficient = string.Empty;
        double maxCostPerOp = double.MinValue;
        double minCostPerOp = double.MaxValue;
        double totalCostPerHour = 0.0;

        foreach (var kvp in _trackers)
        {
            var cost = kvp.Value.ToAlgorithmCost(_cpuCostPerSecondUsd);
            costs[kvp.Key] = cost;

            if (cost.EstimatedCloudCostPerOp > maxCostPerOp)
            {
                maxCostPerOp = cost.EstimatedCloudCostPerOp;
                mostExpensive = cost.AlgorithmId;
            }

            if (cost.EstimatedCloudCostPerOp < minCostPerOp)
            {
                minCostPerOp = cost.EstimatedCloudCostPerOp;
                mostEfficient = cost.AlgorithmId;
            }

            // Estimate hourly cost from operation rate
            double opsPerHour = kvp.Value.GetOperationsPerHour(now);
            totalCostPerHour += opsPerHour * cost.EstimatedCloudCostPerOp;
        }

        _currentProfile = new CostProfile(
            costs,
            totalCostPerHour,
            totalCostPerHour * HoursPerMonth,
            mostExpensive,
            mostEfficient,
            now);
    }

    /// <summary>
    /// Tracks per-algorithm operation counts and duration rolling averages.
    /// </summary>
    private sealed class AlgorithmTracker
    {
        private readonly string _algorithmId;
        private readonly ConcurrentQueue<(DateTimeOffset Timestamp, double DurationMs)> _samples = new();
        private long _operationCount;
        private double _durationSum;

        public AlgorithmTracker(string algorithmId)
        {
            _algorithmId = algorithmId;
        }

        public void Record(double durationMs, DateTimeOffset timestamp)
        {
            _samples.Enqueue((timestamp, durationMs));
            Interlocked.Increment(ref _operationCount);
            // Non-atomic add is acceptable for advisory metrics
            _durationSum += durationMs;

            // Bound memory: keep last 10000 samples
            while (_samples.Count > 10000)
            {
                _samples.TryDequeue(out _);
            }
        }

        public AlgorithmCost ToAlgorithmCost(double cpuCostPerSecondUsd)
        {
            long ops = Volatile.Read(ref _operationCount);
            double avgMs = ops > 0 ? _durationSum / ops : 0.0;
            double cpuSecondsPerOp = avgMs / 1000.0;
            double cloudCostPerOp = cpuSecondsPerOp * cpuCostPerSecondUsd;

            return new AlgorithmCost(
                _algorithmId,
                ops,
                avgMs,
                cpuSecondsPerOp,
                cloudCostPerOp);
        }

        public double GetOperationsPerHour(DateTimeOffset now)
        {
            var samples = _samples.ToArray();
            if (samples.Length < 2) return 0;

            DateTimeOffset oldest = samples[0].Timestamp;
            double hours = (now - oldest).TotalHours;
            return hours > 0 ? samples.Length / hours : 0;
        }
    }
}
