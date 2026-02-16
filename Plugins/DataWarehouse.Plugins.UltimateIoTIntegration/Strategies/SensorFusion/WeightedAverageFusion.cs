// <copyright file="WeightedAverageFusion.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorFusion;

/// <summary>
/// Fuses redundant sensor readings using weighted averaging with outlier detection.
/// </summary>
public sealed class WeightedAverageFusion
{
    private readonly ConcurrentDictionary<string, double> _sensorWeights = new();
    private readonly ConcurrentDictionary<string, List<double>> _recentVariances = new();

    /// <summary>
    /// Configures weights for each sensor.
    /// </summary>
    /// <param name="sensorWeights">Dictionary mapping sensor IDs to weights (will be normalized).</param>
    public void Configure(Dictionary<string, double> sensorWeights)
    {
        _sensorWeights.Clear();
        foreach (var kvp in sensorWeights)
        {
            _sensorWeights[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Fuses multiple sensor readings into a single estimate.
    /// </summary>
    /// <param name="readings">Array of sensor readings to fuse.</param>
    /// <returns>Fused reading with combined value and confidence.</returns>
    public FusedReading Fuse(SensorReading[] readings)
    {
        if (readings.Length == 0)
            throw new ArgumentException("At least one reading is required", nameof(readings));

        // Determine value dimension from first reading
        int valueDim = readings[0].Value.Length;

        // Filter outliers (readings > 3 standard deviations from mean)
        var filteredReadings = FilterOutliers(readings, valueDim);

        if (filteredReadings.Length == 0)
        {
            // All readings were outliers - return average of original readings with low confidence
            return CreateFusedReading(readings, readings, 0.1);
        }

        // Compute weighted average
        var weights = ComputeNormalizedWeights(filteredReadings);
        var fusedValue = new double[valueDim];

        for (int dim = 0; dim < valueDim; dim++)
        {
            double sum = 0.0;
            for (int i = 0; i < filteredReadings.Length; i++)
            {
                sum += weights[i] * filteredReadings[i].Value[dim];
            }
            fusedValue[dim] = sum;
        }

        // Compute confidence based on agreement between sensors
        double confidence = ComputeConfidence(filteredReadings, fusedValue, weights);

        return new FusedReading(
            readings,
            fusedValue,
            confidence,
            "WeightedAverage",
            DateTimeOffset.UtcNow);
    }

    private SensorReading[] FilterOutliers(SensorReading[] readings, int valueDim)
    {
        var filtered = new List<SensorReading>();

        for (int dim = 0; dim < valueDim; dim++)
        {
            // Compute mean and standard deviation for this dimension
            double mean = readings.Average(r => r.Value[dim]);
            double variance = readings.Average(r => Math.Pow(r.Value[dim] - mean, 2));
            double stdDev = Math.Sqrt(variance);

            // Track variance for adaptive weighting
            foreach (var reading in readings)
            {
                if (!_recentVariances.ContainsKey(reading.SensorId))
                {
                    _recentVariances[reading.SensorId] = new List<double>();
                }

                _recentVariances[reading.SensorId].Add(variance);
                if (_recentVariances[reading.SensorId].Count > 10)
                {
                    _recentVariances[reading.SensorId].RemoveAt(0);
                }
            }
        }

        // Filter readings: keep those within 3 standard deviations for all dimensions
        foreach (var reading in readings)
        {
            bool isOutlier = false;

            for (int dim = 0; dim < valueDim; dim++)
            {
                double mean = readings.Average(r => r.Value[dim]);
                double variance = readings.Average(r => Math.Pow(r.Value[dim] - mean, 2));
                double stdDev = Math.Sqrt(variance);

                double deviation = Math.Abs(reading.Value[dim] - mean);
                if (deviation > 3.0 * stdDev && stdDev > 1e-10)
                {
                    isOutlier = true;
                    break;
                }
            }

            if (!isOutlier)
            {
                filtered.Add(reading);
            }
        }

        return filtered.ToArray();
    }

    private double[] ComputeNormalizedWeights(SensorReading[] readings)
    {
        var weights = new double[readings.Length];

        for (int i = 0; i < readings.Length; i++)
        {
            string sensorId = readings[i].SensorId;

            // Start with configured weight (or 1.0 if not configured)
            double weight = _sensorWeights.TryGetValue(sensorId, out double configWeight)
                ? configWeight
                : 1.0;

            // Adjust weight based on sensor accuracy (if available)
            double? accuracy = readings[i].Accuracy;
            if (accuracy.HasValue)
            {
                weight *= accuracy.Value;
            }

            // Reduce weight for sensors with high variance
            if (_recentVariances.TryGetValue(sensorId, out var variances) && variances.Count > 0)
            {
                double avgVariance = variances.Average();
                if (avgVariance > 1e-10)
                {
                    weight /= (1.0 + avgVariance);
                }
            }

            weights[i] = weight;
        }

        // Normalize weights to sum to 1.0
        double totalWeight = weights.Sum();
        if (totalWeight > 1e-10)
        {
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] /= totalWeight;
            }
        }
        else
        {
            // All weights are zero - use uniform weights
            double uniformWeight = 1.0 / readings.Length;
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = uniformWeight;
            }
        }

        return weights;
    }

    private double ComputeConfidence(SensorReading[] readings, double[] fusedValue, double[] weights)
    {
        if (readings.Length == 1)
            return readings[0].Accuracy ?? 0.5;

        // Compute weighted variance from fused value
        double weightedVariance = 0.0;
        int valueDim = fusedValue.Length;

        for (int i = 0; i < readings.Length; i++)
        {
            double sumSquaredDiff = 0.0;
            for (int dim = 0; dim < valueDim; dim++)
            {
                double diff = readings[i].Value[dim] - fusedValue[dim];
                sumSquaredDiff += diff * diff;
            }

            weightedVariance += weights[i] * sumSquaredDiff;
        }

        // Convert variance to confidence (lower variance = higher confidence)
        // Use inverse exponential mapping: confidence = exp(-variance)
        double confidence = Math.Exp(-weightedVariance);
        return Math.Max(0.0, Math.Min(1.0, confidence));
    }

    private FusedReading CreateFusedReading(SensorReading[] sources, SensorReading[] used, double confidence)
    {
        int valueDim = sources[0].Value.Length;
        var fusedValue = new double[valueDim];

        for (int dim = 0; dim < valueDim; dim++)
        {
            fusedValue[dim] = used.Average(r => r.Value[dim]);
        }

        return new FusedReading(
            sources,
            fusedValue,
            confidence,
            "WeightedAverage",
            DateTimeOffset.UtcNow);
    }
}
