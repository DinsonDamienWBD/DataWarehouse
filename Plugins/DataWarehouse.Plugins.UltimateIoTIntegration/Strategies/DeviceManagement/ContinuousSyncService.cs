using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.DeviceManagement;

/// <summary>
/// Configuration options for continuous device synchronization.
/// </summary>
/// <param name="MaxHistoryPerProperty">Maximum number of historical values to retain per property (default 1000).</param>
/// <param name="SyncTimeoutMs">Maximum sync operation time in milliseconds (default 100).</param>
/// <param name="MaxDevices">Maximum number of devices to track (default 10,000).</param>
public record ContinuousSyncOptions(
    int MaxHistoryPerProperty = 1000,
    int SyncTimeoutMs = 100,
    int MaxDevices = 10_000
);

/// <summary>
/// Timestamped sensor value with metadata.
/// </summary>
/// <param name="Timestamp">Time of measurement.</param>
/// <param name="Value">Measured value.</param>
public record TimestampedValue(
    DateTimeOffset Timestamp,
    object Value
);

/// <summary>
/// Result of a device synchronization operation.
/// </summary>
/// <param name="DeviceId">Device identifier.</param>
/// <param name="LatencyMs">Synchronization latency in milliseconds.</param>
/// <param name="UpdatedPropertyCount">Number of properties updated.</param>
/// <param name="SyncTimestamp">Time of synchronization.</param>
public record SyncResult(
    string DeviceId,
    double LatencyMs,
    int UpdatedPropertyCount,
    DateTimeOffset SyncTimestamp
);

/// <summary>
/// Projected value with trend analysis.
/// </summary>
/// <param name="CurrentValue">Current measured value.</param>
/// <param name="FutureValue">Predicted future value.</param>
/// <param name="Trend">Direction of change (Increasing, Decreasing, Stable).</param>
/// <param name="ConfidenceInterval">Statistical confidence range (0-1).</param>
public record ProjectedValue(
    object? CurrentValue,
    object? FutureValue,
    string Trend,
    double ConfidenceInterval
);

/// <summary>
/// Projected device state at a future point in time.
/// </summary>
/// <param name="DeviceId">Device identifier.</param>
/// <param name="ProjectionTimestamp">Time of projection.</param>
/// <param name="ProjectedProperties">Predicted property values.</param>
/// <param name="ConfidenceScore">Overall prediction confidence (0-1).</param>
public record ProjectedState(
    string DeviceId,
    DateTimeOffset ProjectionTimestamp,
    Dictionary<string, ProjectedValue> ProjectedProperties,
    double ConfidenceScore
);

/// <summary>
/// Result of a what-if simulation.
/// </summary>
/// <param name="DeviceId">Device identifier.</param>
/// <param name="OriginalState">Device state before simulation.</param>
/// <param name="ModifiedState">Device state with parameter changes applied.</param>
/// <param name="PredictedOutcomes">Predicted outcomes after parameter changes.</param>
/// <param name="ImpactScore">Overall impact magnitude (0-1).</param>
public record SimulationResult(
    string DeviceId,
    Dictionary<string, object> OriginalState,
    Dictionary<string, object> ModifiedState,
    Dictionary<string, ProjectedValue> PredictedOutcomes,
    double ImpactScore
);

/// <summary>
/// Per-device synchronization state tracking.
/// </summary>
internal class DeviceSyncState
{
    /// <summary>
    /// Reference to the device twin.
    /// </summary>
    public required DeviceTwin Twin { get; set; }

    /// <summary>
    /// Property value history (property name -> list of timestamped values).
    /// </summary>
    public BoundedDictionary<string, BoundedList<TimestampedValue>> PropertyHistory { get; } = new BoundedDictionary<string, BoundedList<TimestampedValue>>(1000);

    /// <summary>
    /// Last synchronization timestamp.
    /// </summary>
    public DateTimeOffset LastSyncAt { get; set; }

    /// <summary>
    /// Total number of syncs performed.
    /// </summary>
    public long SyncCount { get; set; }
}

/// <summary>
/// Thread-safe bounded list with automatic eviction.
/// </summary>
internal class BoundedList<T>
{
    private readonly Queue<T> _items = new();
    private readonly int _maxSize;
    private readonly object _lock = new();

    public BoundedList(int maxSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSize);
        _maxSize = maxSize;
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            if (_items.Count >= _maxSize)
                _items.Dequeue();
            _items.Enqueue(item);
        }
    }

    public List<T> GetAll()
    {
        lock (_lock)
        {
            return _items.ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }
}

/// <summary>
/// Continuous device synchronization service with real-time property tracking.
/// </summary>
public class ContinuousSyncService
{
    private readonly ContinuousSyncOptions _options;
    private readonly BoundedDictionary<string, DeviceSyncState> _syncStates = new BoundedDictionary<string, DeviceSyncState>(1000);
    private readonly object _registerLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinuousSyncService"/> class.
    /// </summary>
    /// <param name="options">Configuration options (uses defaults if null).</param>
    public ContinuousSyncService(ContinuousSyncOptions? options = null)
    {
        _options = options ?? new ContinuousSyncOptions();
    }

    /// <summary>
    /// Registers a device twin for continuous synchronization.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="twin">Device twin to sync.</param>
    public void RegisterTwin(string deviceId, DeviceTwin twin)
    {
        // Lock covers both the count check and the write to prevent concurrent
        // RegisterTwin calls from exceeding MaxDevices (TOCTOU race).
        lock (_registerLock)
        {
            if (_syncStates.Count >= _options.MaxDevices)
                throw new InvalidOperationException($"Maximum device limit ({_options.MaxDevices}) reached");

            _syncStates[deviceId] = new DeviceSyncState
            {
                Twin = twin,
                LastSyncAt = DateTimeOffset.UtcNow,
                SyncCount = 0
            };
        }
    }

    /// <summary>
    /// Unregisters a device from continuous synchronization.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    public void UnregisterTwin(string deviceId)
    {
        _syncStates.TryRemove(deviceId, out _);
    }

    /// <summary>
    /// Synchronizes sensor data with the device twin.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="sensorReadings">Sensor readings to sync.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Synchronization result with latency metrics.</returns>
    /// <exception cref="KeyNotFoundException">Device not registered.</exception>
    public async Task<SyncResult> SyncSensorDataAsync(
        string deviceId,
        Dictionary<string, object> sensorReadings,
        CancellationToken ct = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!_syncStates.TryGetValue(deviceId, out var syncState))
            throw new KeyNotFoundException($"Device '{deviceId}' not registered for sync");

        // Use timeout to enforce latency constraint
        using var timeoutCts = new CancellationTokenSource(_options.SyncTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await Task.Run(() =>
            {
                var timestamp = DateTimeOffset.UtcNow;
                var updatedCount = 0;

                foreach (var reading in sensorReadings)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    // Update twin's reported properties
                    syncState.Twin.ReportedProperties[reading.Key] = reading.Value;

                    // Record in history
                    var history = syncState.PropertyHistory.GetOrAdd(
                        reading.Key,
                        _ => new BoundedList<TimestampedValue>(_options.MaxHistoryPerProperty));

                    history.Add(new TimestampedValue(timestamp, reading.Value));
                    updatedCount++;
                }

                syncState.LastSyncAt = timestamp;
                syncState.SyncCount++;
                syncState.Twin.LastUpdated = timestamp;

                return updatedCount;
            }, linkedCts.Token);

            var latencyMs = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;

            return new SyncResult(
                deviceId,
                latencyMs,
                sensorReadings.Count,
                DateTimeOffset.UtcNow
            );
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Sync exceeded timeout of {_options.SyncTimeoutMs}ms");
        }
    }

    /// <summary>
    /// Gets synchronization state for a device.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <returns>Synchronization state or null if not registered.</returns>
    internal DeviceSyncState? GetSyncState(string deviceId)
    {
        _syncStates.TryGetValue(deviceId, out var state);
        return state;
    }

    /// <summary>
    /// Gets all registered device IDs.
    /// </summary>
    /// <returns>Collection of device IDs.</returns>
    public IEnumerable<string> GetRegisteredDevices()
    {
        return _syncStates.Keys;
    }
}

/// <summary>
/// State projection engine using historical data for predictive analytics.
/// </summary>
public class StateProjectionEngine
{
    private readonly ContinuousSyncService _syncService;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateProjectionEngine"/> class.
    /// </summary>
    /// <param name="syncService">Continuous sync service providing historical data.</param>
    public StateProjectionEngine(ContinuousSyncService syncService)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
    }

    /// <summary>
    /// Projects device state into the future using linear regression.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="horizon">How far ahead to project.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Projected state with confidence metrics.</returns>
    /// <exception cref="KeyNotFoundException">Device not registered.</exception>
    public async Task<ProjectedState> ProjectStateAsync(
        string deviceId,
        TimeSpan horizon,
        CancellationToken ct = default)
    {
        var syncState = _syncService.GetSyncState(deviceId);
        if (syncState == null)
            throw new KeyNotFoundException($"Device '{deviceId}' not registered");

        return await Task.Run(() =>
        {
            var projectedProps = new Dictionary<string, ProjectedValue>();
            var confidenceScores = new List<double>();

            foreach (var prop in syncState.PropertyHistory)
            {
                ct.ThrowIfCancellationRequested();

                var history = prop.Value.GetAll();
                if (history.Count < 2)
                {
                    // Not enough data for projection
                    projectedProps[prop.Key] = new ProjectedValue(
                        history.LastOrDefault()?.Value,
                        history.LastOrDefault()?.Value,
                        "Stable",
                        0.0
                    );
                    continue;
                }

                var projection = ProjectProperty(history, horizon);
                projectedProps[prop.Key] = projection;
                confidenceScores.Add(projection.ConfidenceInterval);
            }

            var overallConfidence = confidenceScores.Any() ? confidenceScores.Average() : 0.0;

            return new ProjectedState(
                deviceId,
                DateTimeOffset.UtcNow,
                projectedProps,
                overallConfidence
            );
        }, ct);
    }

    private ProjectedValue ProjectProperty(List<TimestampedValue> history, TimeSpan horizon)
    {
        var current = history.Last();

        // Try to convert values to double for numeric projection
        var numericPoints = new List<(double x, double y)>();
        var baseTime = history.First().Timestamp;

        foreach (var point in history)
        {
            if (TryConvertToDouble(point.Value, out var numValue))
            {
                var x = (point.Timestamp - baseTime).TotalSeconds;
                numericPoints.Add((x, numValue));
            }
        }

        if (numericPoints.Count < 2)
        {
            // Not enough numeric data
            return new ProjectedValue(current.Value, current.Value, "Stable", 0.0);
        }

        // Linear regression: y = mx + b
        var n = numericPoints.Count;
        var xMean = numericPoints.Average(p => p.x);
        var yMean = numericPoints.Average(p => p.y);

        var numerator = numericPoints.Sum(p => (p.x - xMean) * (p.y - yMean));
        var denominator = numericPoints.Sum(p => Math.Pow(p.x - xMean, 2));

        if (Math.Abs(denominator) < 1e-10)
        {
            // No variance in x (all points at same time)
            return new ProjectedValue(current.Value, current.Value, "Stable", 0.5);
        }

        var slope = numerator / denominator;
        var intercept = yMean - (slope * xMean);

        // Project into future
        var futureX = (DateTimeOffset.UtcNow.Add(horizon) - baseTime).TotalSeconds;
        var projectedY = (slope * futureX) + intercept;

        // Determine trend
        var trend = Math.Abs(slope) < 1e-6 ? "Stable" :
                    slope > 0 ? "Increasing" : "Decreasing";

        // Calculate confidence based on R²
        var totalSumSquares = numericPoints.Sum(p => Math.Pow(p.y - yMean, 2));
        var residualSumSquares = numericPoints.Sum(p =>
        {
            var predicted = (slope * p.x) + intercept;
            return Math.Pow(p.y - predicted, 2);
        });

        var rSquared = totalSumSquares > 0 ? 1.0 - (residualSumSquares / totalSumSquares) : 0.0;
        var confidence = Math.Max(0.0, Math.Min(1.0, rSquared));

        return new ProjectedValue(
            current.Value,
            projectedY,
            trend,
            confidence
        );
    }

    // LOW-3405: delegate to shared static helper to avoid duplication with WhatIfSimulator
    private static bool TryConvertToDouble(object value, out double result)
        => DeviceManagementHelper.TryConvertToDouble(value, out result);
}

/// <summary>
/// What-if simulator for device parameter impact analysis.
/// </summary>
public class WhatIfSimulator
{
    private readonly ContinuousSyncService _syncService;
    private readonly StateProjectionEngine _projectionEngine;

    /// <summary>
    /// Initializes a new instance of the <see cref="WhatIfSimulator"/> class.
    /// </summary>
    /// <param name="syncService">Continuous sync service.</param>
    /// <param name="projectionEngine">State projection engine.</param>
    public WhatIfSimulator(ContinuousSyncService syncService, StateProjectionEngine projectionEngine)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _projectionEngine = projectionEngine ?? throw new ArgumentNullException(nameof(projectionEngine));
    }

    /// <summary>
    /// Simulates the impact of parameter changes on device behavior.
    /// </summary>
    /// <param name="deviceId">Device identifier.</param>
    /// <param name="parameterChanges">Parameters to modify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Simulation result with predicted outcomes.</returns>
    /// <exception cref="KeyNotFoundException">Device not registered.</exception>
    public async Task<SimulationResult> SimulateAsync(
        string deviceId,
        Dictionary<string, object> parameterChanges,
        CancellationToken ct = default)
    {
        var syncState = _syncService.GetSyncState(deviceId);
        if (syncState == null)
            throw new KeyNotFoundException($"Device '{deviceId}' not registered");

        return await Task.Run(async () =>
        {
            // Capture original state
            var originalState = new Dictionary<string, object>(syncState.Twin.ReportedProperties);

            // Apply modifications
            var modifiedState = new Dictionary<string, object>(originalState);
            foreach (var change in parameterChanges)
            {
                modifiedState[change.Key] = change.Value;
            }

            // Project outcomes with a short horizon (simulate immediate impact)
            var projection = await _projectionEngine.ProjectStateAsync(deviceId, TimeSpan.FromMinutes(5), ct);

            // Calculate impact score based on magnitude of changes
            var impactScore = CalculateImpactScore(originalState, modifiedState, projection);

            return new SimulationResult(
                deviceId,
                originalState,
                modifiedState,
                projection.ProjectedProperties,
                impactScore
            );
        }, ct);
    }

    private double CalculateImpactScore(
        Dictionary<string, object> original,
        Dictionary<string, object> modified,
        ProjectedState projection)
    {
        var changes = 0;
        var totalChangeMagnitude = 0.0;

        foreach (var change in modified)
        {
            if (!original.TryGetValue(change.Key, out var origValue))
            {
                changes++;
                continue;
            }

            if (!Equals(origValue, change.Value))
            {
                changes++;

                // Try to quantify magnitude for numeric changes
                if (TryConvertToDouble(origValue, out var origNum) &&
                    TryConvertToDouble(change.Value, out var newNum))
                {
                    var relativeDelta = Math.Abs((newNum - origNum) / (origNum + 1e-10));
                    totalChangeMagnitude += relativeDelta;
                }
                else
                {
                    totalChangeMagnitude += 1.0; // Non-numeric change counts as full magnitude
                }
            }
        }

        if (changes == 0)
            return 0.0;

        // Normalize to 0-1 range
        var avgMagnitude = totalChangeMagnitude / changes;
        return Math.Min(1.0, avgMagnitude);
    }

    // LOW-3405: delegate to shared static helper
    private static bool TryConvertToDouble(object value, out double result)
        => DeviceManagementHelper.TryConvertToDouble(value, out result);
}

/// <summary>
/// LOW-3405: Shared helper methods extracted to avoid duplication between StateProjectionEngine and WhatIfSimulator.
/// </summary>
internal static class DeviceManagementHelper
{
    internal static bool TryConvertToDouble(object value, out double result)
    {
        result = 0;
        if (value == null)
            return false;

        switch (value)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case decimal dec: result = (double)dec; return true;
            case string s: return double.TryParse(s, out result);
            default: return false;
        }
    }
}
