// <copyright file="TemporalAligner.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorFusion;

/// <summary>
/// Aligns sensor readings from multiple sources with different sampling rates to a common timestamp.
/// Uses linear interpolation for temporal alignment.
/// </summary>
public sealed class TemporalAligner
{
    private readonly ConcurrentDictionary<string, List<SensorReading>> _buffers = new();
    private readonly int _maxBufferSize;
    private readonly double _toleranceMs;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemporalAligner"/> class.
    /// </summary>
    /// <param name="maxBufferSize">Maximum number of readings to buffer per sensor (default 100).</param>
    /// <param name="toleranceMs">Maximum time difference (ms) for alignment (default 1.0ms).</param>
    public TemporalAligner(int maxBufferSize = 100, double toleranceMs = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toleranceMs);
        _maxBufferSize = maxBufferSize;
        _toleranceMs = toleranceMs;
    }

    /// <summary>
    /// Aligns sensor readings to a target timestamp using interpolation.
    /// </summary>
    /// <param name="readings">Recent sensor readings to align.</param>
    /// <param name="targetTimestamp">Target timestamp to align all readings to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aligned sensor readings at the target timestamp.</returns>
    public Task<SensorReading[]> AlignAsync(
        SensorReading[] readings,
        DateTimeOffset targetTimestamp,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Update buffers with new readings
        foreach (var reading in readings)
        {
            if (!_buffers.ContainsKey(reading.SensorId))
            {
                _buffers[reading.SensorId] = new List<SensorReading>();
            }

            var buffer = _buffers[reading.SensorId];
            buffer.Add(reading);

            // Sort by timestamp
            buffer.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            // Trim buffer if too large
            while (buffer.Count > _maxBufferSize)
            {
                buffer.RemoveAt(0);
            }
        }

        // Align readings for each sensor
        var alignedReadings = new List<SensorReading>();

        foreach (var kvp in _buffers)
        {
            string sensorId = kvp.Key;
            var buffer = kvp.Value;

            if (buffer.Count == 0)
                continue;

            // Try to interpolate to target timestamp
            var aligned = InterpolateReading(sensorId, buffer, targetTimestamp);
            if (aligned != null)
            {
                alignedReadings.Add(aligned);
            }
        }

        return Task.FromResult(alignedReadings.ToArray());
    }

    /// <summary>
    /// Clears all buffered readings for a specific sensor.
    /// </summary>
    /// <param name="sensorId">Sensor ID to clear.</param>
    public void ClearBuffer(string sensorId)
    {
        _buffers.TryRemove(sensorId, out _);
    }

    /// <summary>
    /// Clears all buffered readings.
    /// </summary>
    public void ClearAllBuffers()
    {
        _buffers.Clear();
    }

    private SensorReading? InterpolateReading(
        string sensorId,
        List<SensorReading> buffer,
        DateTimeOffset targetTimestamp)
    {
        if (buffer.Count == 0)
            return null;

        // Find readings before and after target timestamp
        SensorReading? before = null;
        SensorReading? after = null;

        for (int i = 0; i < buffer.Count; i++)
        {
            if (buffer[i].Timestamp <= targetTimestamp)
            {
                before = buffer[i];
            }

            if (buffer[i].Timestamp >= targetTimestamp && after == null)
            {
                after = buffer[i];
                break;
            }
        }

        // Case 1: Exact match (within tolerance)
        if (before != null && Math.Abs((targetTimestamp - before.Timestamp).TotalMilliseconds) <= _toleranceMs)
        {
            return before;
        }

        if (after != null && Math.Abs((after.Timestamp - targetTimestamp).TotalMilliseconds) <= _toleranceMs)
        {
            return after;
        }

        // Case 2: Need to interpolate between two readings
        if (before != null && after != null)
        {
            return InterpolateBetween(before, after, targetTimestamp);
        }

        // Case 3: Extrapolate from most recent reading (not ideal, but better than nothing)
        if (before != null)
        {
            double timeDiffMs = (targetTimestamp - before.Timestamp).TotalMilliseconds;
            if (timeDiffMs <= _toleranceMs)
            {
                return before; // Close enough
            }
        }

        // Case 4: No suitable reading found within tolerance
        return null;
    }

    private SensorReading InterpolateBetween(
        SensorReading before,
        SensorReading after,
        DateTimeOffset targetTimestamp)
    {
        // Linear interpolation factor
        double totalDuration = (after.Timestamp - before.Timestamp).TotalMilliseconds;
        double targetOffset = (targetTimestamp - before.Timestamp).TotalMilliseconds;

        double t = totalDuration > 1e-10 ? targetOffset / totalDuration : 0.5;
        t = Math.Max(0.0, Math.Min(1.0, t)); // Clamp to [0, 1]

        // Interpolate values
        int valueDim = before.Value.Length;
        var interpolatedValue = new double[valueDim];

        for (int i = 0; i < valueDim; i++)
        {
            interpolatedValue[i] = before.Value[i] * (1.0 - t) + after.Value[i] * t;
        }

        // Interpolate accuracy if both have it
        double? interpolatedAccuracy = null;
        if (before.Accuracy.HasValue && after.Accuracy.HasValue)
        {
            interpolatedAccuracy = before.Accuracy.Value * (1.0 - t) + after.Accuracy.Value * t;
        }
        else if (before.Accuracy.HasValue)
        {
            interpolatedAccuracy = before.Accuracy.Value;
        }
        else if (after.Accuracy.HasValue)
        {
            interpolatedAccuracy = after.Accuracy.Value;
        }

        return new SensorReading(
            before.SensorId,
            before.SensorType,
            interpolatedValue,
            targetTimestamp,
            interpolatedAccuracy);
    }
}
