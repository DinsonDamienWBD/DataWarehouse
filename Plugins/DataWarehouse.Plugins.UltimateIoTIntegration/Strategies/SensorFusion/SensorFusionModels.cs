// <copyright file="SensorFusionModels.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorFusion;

/// <summary>
/// Type of sensor producing readings.
/// </summary>
public enum SensorType
{
    /// <summary>GPS location sensor.</summary>
    GPS,

    /// <summary>Inertial Measurement Unit.</summary>
    IMU,

    /// <summary>Accelerometer sensor.</summary>
    Accelerometer,

    /// <summary>Gyroscope sensor.</summary>
    Gyroscope,

    /// <summary>Magnetometer sensor.</summary>
    Magnetometer,

    /// <summary>Temperature sensor.</summary>
    Temperature,

    /// <summary>Pressure sensor.</summary>
    Pressure,

    /// <summary>Humidity sensor.</summary>
    Humidity,

    /// <summary>Generic sensor type.</summary>
    Generic
}

/// <summary>
/// Represents a single sensor reading with metadata.
/// </summary>
/// <param name="SensorId">Unique identifier for the sensor.</param>
/// <param name="SensorType">Type of sensor that produced this reading.</param>
/// <param name="Value">Array of measurement values (e.g., [x,y,z] for accelerometer).</param>
/// <param name="Timestamp">When the reading was captured.</param>
/// <param name="Accuracy">Optional accuracy/confidence measure for this reading (0.0-1.0).</param>
public sealed record SensorReading(
    string SensorId,
    SensorType SensorType,
    double[] Value,
    DateTimeOffset Timestamp,
    double? Accuracy);

/// <summary>
/// Result of fusing multiple sensor readings into a single estimate.
/// </summary>
/// <param name="Sources">Original sensor readings that contributed to this fusion.</param>
/// <param name="FusedValue">Combined/filtered value from all sources.</param>
/// <param name="Confidence">Confidence level of the fused result (0.0-1.0).</param>
/// <param name="Algorithm">Name of the fusion algorithm that produced this result.</param>
/// <param name="Timestamp">When the fusion was computed.</param>
public sealed record FusedReading(
    SensorReading[] Sources,
    double[] FusedValue,
    double Confidence,
    string Algorithm,
    DateTimeOffset Timestamp);

/// <summary>
/// Configuration for the sensor fusion pipeline.
/// </summary>
/// <param name="EnableKalman">Enable Kalman filter for GPS+IMU fusion.</param>
/// <param name="EnableComplementary">Enable complementary filter for accelerometer+gyroscope fusion.</param>
/// <param name="EnableVoting">Enable voting fusion for redundant sensor arrays.</param>
/// <param name="EnableWeightedAverage">Enable weighted average fusion for generic multi-sensor fusion.</param>
/// <param name="TemporalAlignmentToleranceMs">Maximum time difference (ms) for temporal alignment (default 1.0ms).</param>
public sealed record FusionPipelineConfig(
    bool EnableKalman = true,
    bool EnableComplementary = true,
    bool EnableVoting = true,
    bool EnableWeightedAverage = true,
    double TemporalAlignmentToleranceMs = 1.0);
