// <copyright file="ComplementaryFilter.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorFusion;

/// <summary>
/// Implements a complementary filter for IMU fusion (accelerometer + gyroscope).
/// Combines high-pass filtered gyroscope (good for short-term) with low-pass filtered accelerometer (good for long-term).
/// </summary>
public sealed class ComplementaryFilter
{
    private readonly double _alpha;
    private double _roll;
    private double _pitch;
    private double _yaw;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComplementaryFilter"/> class.
    /// </summary>
    /// <param name="alpha">High-pass weight for gyroscope (typically 0.98). Range: 0.0-1.0.</param>
    public ComplementaryFilter(double alpha = 0.98)
    {
        if (alpha < 0.0 || alpha > 1.0)
            throw new ArgumentException("Alpha must be between 0.0 and 1.0", nameof(alpha));

        _alpha = alpha;
        _roll = 0.0;
        _pitch = 0.0;
        _yaw = 0.0;
    }

    /// <summary>
    /// Updates the orientation estimate with new sensor data.
    /// </summary>
    /// <param name="accelerometer">Accelerometer reading [ax, ay, az] in m/s².</param>
    /// <param name="gyroscope">Gyroscope reading [gx, gy, gz] in rad/s.</param>
    /// <param name="dt">Time step in seconds since last update.</param>
    /// <returns>Estimated orientation [roll, pitch, yaw] in radians.</returns>
    public double[] Update(double[] accelerometer, double[] gyroscope, double dt)
    {
        if (accelerometer.Length != 3)
            throw new ArgumentException("Accelerometer must have 3 elements [ax, ay, az]");

        if (gyroscope.Length != 3)
            throw new ArgumentException("Gyroscope must have 3 elements [gx, gy, gz]");

        // Extract sensor readings
        double ax = accelerometer[0];
        double ay = accelerometer[1];
        double az = accelerometer[2];
        double gx = gyroscope[0];
        double gy = gyroscope[1];
        double gz = gyroscope[2];

        // Compute roll and pitch from accelerometer (tilt sensing)
        // Roll: rotation around X-axis
        // Pitch: rotation around Y-axis
        double accelRoll = Math.Atan2(ay, az);
        double accelPitch = Math.Atan2(-ax, Math.Sqrt(ay * ay + az * az));

        // Integrate gyroscope for high-frequency changes
        double gyroRoll = _roll + gx * dt;
        double gyroPitch = _pitch + gy * dt;
        double gyroYaw = _yaw + gz * dt;

        // Complementary filter: combine gyro (high-pass) with accel (low-pass)
        // angle = alpha * (angle + gyro * dt) + (1-alpha) * accel_angle
        _roll = _alpha * gyroRoll + (1.0 - _alpha) * accelRoll;
        _pitch = _alpha * gyroPitch + (1.0 - _alpha) * accelPitch;
        _yaw = gyroYaw; // Yaw from gyro only (accelerometer can't measure yaw)

        return new double[] { _roll, _pitch, _yaw };
    }

    /// <summary>
    /// Resets the filter state to zero.
    /// </summary>
    public void Reset()
    {
        _roll = 0.0;
        _pitch = 0.0;
        _yaw = 0.0;
    }

    /// <summary>
    /// Gets the current orientation estimate.
    /// </summary>
    /// <returns>Current orientation [roll, pitch, yaw] in radians.</returns>
    public double[] GetOrientation()
    {
        return new double[] { _roll, _pitch, _yaw };
    }
}
