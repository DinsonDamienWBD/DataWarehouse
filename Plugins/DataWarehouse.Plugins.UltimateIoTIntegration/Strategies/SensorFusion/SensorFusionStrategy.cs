// <copyright file="SensorFusionStrategy.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorFusion;

/// <summary>
/// IoT strategy for sensor fusion and multi-sensor data processing.
/// Provides high-level interface for fusing sensor data from multiple sources.
/// </summary>
public sealed class SensorFusionStrategy : IoTStrategyBase
{
    private SensorFusionEngine? _engine;

    /// <inheritdoc/>
    public override string StrategyId => "sensor-fusion";

    /// <inheritdoc/>
    public override string StrategyName => "Sensor Fusion Strategy";

    /// <inheritdoc/>
    public override IoTStrategyCategory Category => IoTStrategyCategory.Analytics;

    /// <inheritdoc/>
    public override string Description => "Fuses data from multiple sensors using Kalman filtering, complementary filtering, voting, and weighted averaging.";

    /// <inheritdoc/>
    public override string[] Tags => new[] { "sensor-fusion", "kalman", "imu", "gps", "multi-sensor" };

    /// <summary>
    /// Initializes the sensor fusion engine with custom configuration.
    /// </summary>
    /// <param name="config">Fusion pipeline configuration.</param>
    public void Initialize(FusionPipelineConfig? config = null)
    {
        _engine = new SensorFusionEngine(config);
    }

    /// <summary>
    /// Processes sensor data through the fusion pipeline.
    /// </summary>
    /// <param name="readings">Array of sensor readings to fuse.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Fused sensor reading.</returns>
    public async Task<FusedReading> ProcessSensorDataAsync(SensorReading[] readings, CancellationToken ct = default)
    {
        if (_engine == null)
        {
            // Initialize with default config if not already initialized
            Initialize();
        }

        return await _engine!.ProcessAsync(readings, ct);
    }

    /// <summary>
    /// Gets direct access to the fusion engine for advanced scenarios.
    /// </summary>
    /// <returns>The underlying sensor fusion engine.</returns>
    public SensorFusionEngine GetEngine()
    {
        if (_engine == null)
        {
            Initialize();
        }

        return _engine!;
    }
}
