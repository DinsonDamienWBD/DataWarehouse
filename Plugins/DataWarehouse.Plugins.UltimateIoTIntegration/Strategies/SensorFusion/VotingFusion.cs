// <copyright file="VotingFusion.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorFusion;

/// <summary>
/// Fuses sensor readings using majority voting with fault detection.
/// Groups similar readings and selects the majority group as the truth.
/// </summary>
public sealed class VotingFusion
{
    private readonly BoundedDictionary<string, int> _faultCounts = new BoundedDictionary<string, int>(1000);
    private readonly ConcurrentDictionary<string, byte> _faultySensors = new();

    /// <summary>
    /// Fuses sensor readings using majority voting.
    /// </summary>
    /// <param name="readings">Array of sensor readings to vote on.</param>
    /// <param name="tolerancePercent">Tolerance for grouping similar readings (default 5%).</param>
    /// <returns>Fused reading representing the majority vote.</returns>
    public FusedReading Vote(SensorReading[] readings, double tolerancePercent = 5.0)
    {
        if (readings.Length == 0)
            throw new ArgumentException("At least one reading is required", nameof(readings));

        if (tolerancePercent <= 0.0)
            throw new ArgumentException("Tolerance must be positive", nameof(tolerancePercent));

        // Determine value dimension
        int valueDim = readings[0].Value.Length;

        // Group readings by similarity
        var groups = GroupSimilarReadings(readings, valueDim, tolerancePercent);

        // Find the largest group (majority)
        var majorityGroup = groups.OrderByDescending(g => g.Count).First();

        // Mark sensors not in majority as potentially faulty
        UpdateFaultTracking(readings, majorityGroup);

        // Compute average of majority group
        var fusedValue = new double[valueDim];
        for (int dim = 0; dim < valueDim; dim++)
        {
            fusedValue[dim] = majorityGroup.Average(r => r.Value[dim]);
        }

        // Confidence based on majority size
        double confidence = (double)majorityGroup.Count / readings.Length;

        return new FusedReading(
            readings,
            fusedValue,
            confidence,
            "Voting",
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Gets the list of sensors that have been identified as faulty.
    /// </summary>
    /// <returns>Array of faulty sensor IDs.</returns>
    public string[] GetFaultySensors()
    {
        return _faultySensors.Keys.ToArray();
    }

    /// <summary>
    /// Clears the fault tracking for all sensors.
    /// </summary>
    public void ClearFaultTracking()
    {
        _faultCounts.Clear();
        _faultySensors.Clear();
    }

    // P2-3420: track a running average per group to avoid O(N) recompute per (reading,group) pair in IsSimilar.
    private List<List<SensorReading>> GroupSimilarReadings(
        SensorReading[] readings,
        int valueDim,
        double tolerancePercent)
    {
        var groups = new List<List<SensorReading>>();
        var groupAverages = new List<double[]>(); // running average per group

        foreach (var reading in readings)
        {
            int matchingIndex = -1;

            for (int g = 0; g < groups.Count; g++)
            {
                if (IsSimilarToAverage(reading, groupAverages[g], valueDim, tolerancePercent))
                {
                    matchingIndex = g;
                    break;
                }
            }

            if (matchingIndex >= 0)
            {
                var group = groups[matchingIndex];
                group.Add(reading);
                // Update running average: newAvg = (oldAvg * (n-1) + newValue) / n
                int n = group.Count;
                var avg = groupAverages[matchingIndex];
                for (int dim = 0; dim < valueDim; dim++)
                    avg[dim] = (avg[dim] * (n - 1) + reading.Value[dim]) / n;
            }
            else
            {
                groups.Add(new List<SensorReading> { reading });
                var initAvg = new double[valueDim];
                for (int dim = 0; dim < valueDim; dim++)
                    initAvg[dim] = reading.Value[dim];
                groupAverages.Add(initAvg);
            }
        }

        return groups;
    }

    private static bool IsSimilarToAverage(
        SensorReading reading,
        double[] groupAverage,
        int valueDim,
        double tolerancePercent)
    {
        // Check if reading is within tolerance of pre-computed group average
        for (int dim = 0; dim < valueDim; dim++)
        {
            double reference = Math.Abs(groupAverage[dim]);
            if (reference < 1e-10)
                reference = 1.0; // Avoid division by zero

            double percentDiff = Math.Abs(reading.Value[dim] - groupAverage[dim]) / reference * 100.0;

            if (percentDiff > tolerancePercent)
                return false;
        }

        return true;
    }

    private void UpdateFaultTracking(SensorReading[] allReadings, List<SensorReading> majorityGroup)
    {
        var majorityIds = new HashSet<string>(majorityGroup.Select(r => r.SensorId));

        foreach (var reading in allReadings)
        {
            if (!majorityIds.Contains(reading.SensorId))
            {
                // Sensor not in majority - increment fault count
                int newCount = _faultCounts.AddOrUpdate(
                    reading.SensorId,
                    1,
                    (_, count) => count + 1);

                // Mark as faulty if it has disagreed 3+ times
                if (newCount >= 3)
                {
                    _faultySensors.TryAdd(reading.SensorId, 0);
                }
            }
            else
            {
                // Sensor in majority - decrement fault count (but not below 0)
                _faultCounts.AddOrUpdate(
                    reading.SensorId,
                    0,
                    (_, count) => Math.Max(0, count - 1));

                // Remove from faulty set if count drops to 0
                if (_faultCounts.TryGetValue(reading.SensorId, out int count) && count == 0)
                {
                    _faultySensors.TryRemove(reading.SensorId, out _);
                }
            }
        }
    }
}
