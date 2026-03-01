// <copyright file="KalmanFilter.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorFusion;

/// <summary>
/// Implements a Kalman filter for sensor fusion.
/// State vector: [x, y, z, vx, vy, vz] (position + velocity).
/// </summary>
public sealed class KalmanFilter
{
    private KalmanState? _state;
    private readonly object _stateLock = new();

    /// <summary>
    /// Gets whether the filter has been initialized with an initial position.
    /// </summary>
    public bool IsInitialized
    {
        get { lock (_stateLock) { return _state != null; } }
    }

    /// <summary>
    /// Initializes the Kalman filter with an initial position.
    /// </summary>
    /// <param name="initialPosition">Initial position [x, y, z].</param>
    public void Initialize(double[] initialPosition)
    {
        if (initialPosition.Length != 3)
            throw new ArgumentException("Initial position must have 3 elements [x, y, z]");

        // State vector: [x, y, z, vx, vy, vz]
        var x = new Matrix(6, 1);
        x[0, 0] = initialPosition[0];
        x[1, 0] = initialPosition[1];
        x[2, 0] = initialPosition[2];
        // Velocities start at zero
        x[3, 0] = 0.0;
        x[4, 0] = 0.0;
        x[5, 0] = 0.0;

        // Initial covariance (high uncertainty)
        var p = Matrix.Identity(6);
        for (int i = 0; i < 6; i++)
        {
            p[i, i] = 1000.0;
        }

        // State transition matrix (will be updated with dt in Predict)
        var f = Matrix.Identity(6);

        // Observation matrix (we observe position only)
        var h = new Matrix(3, 6);
        h[0, 0] = 1.0; // x
        h[1, 1] = 1.0; // y
        h[2, 2] = 1.0; // z

        // Process noise covariance
        var q = Matrix.Identity(6);
        for (int i = 0; i < 6; i++)
        {
            q[i, i] = 0.01; // Small process noise
        }

        // Measurement noise covariance
        var r = Matrix.Identity(3);
        for (int i = 0; i < 3; i++)
        {
            r[i, i] = 1.0; // Measurement noise
        }

        lock (_stateLock)
        {
            _state = new KalmanState(x, p, f, h, q, r);
        }
    }

    /// <summary>
    /// Prediction step: propagate state forward in time.
    /// </summary>
    /// <param name="dt">Time step in seconds.</param>
    public void Predict(double dt)
    {
        lock (_stateLock)
        {
            if (_state == null)
                throw new InvalidOperationException("Filter not initialized");

            // Update state transition matrix with time step
            // F = [I3  dt*I3]
            //     [0   I3   ]
            var f = Matrix.Identity(6);
            f[0, 3] = dt; // x += vx * dt
            f[1, 4] = dt; // y += vy * dt
            f[2, 5] = dt; // z += vz * dt

            // Predict state: X = F * X
            var xPred = Matrix.Multiply(f, _state.X);

            // Predict covariance: P = F * P * F^T + Q
            var fPfT = Matrix.Multiply(Matrix.Multiply(f, _state.P), Matrix.Transpose(f));
            var pPred = Matrix.Add(fPfT, _state.Q);

            _state = _state with { X = xPred, P = pPred, F = f };
        }
    }

    /// <summary>
    /// Update step: incorporate new measurement.
    /// </summary>
    /// <param name="measurement">Measured position [x, y, z].</param>
    /// <param name="measurementNoise">Measurement noise variances [σx², σy², σz²].</param>
    public void Update(double[] measurement, double[] measurementNoise)
    {
        if (measurement.Length != 3)
            throw new ArgumentException("Measurement must have 3 elements [x, y, z]");

        if (measurementNoise.Length != 3)
            throw new ArgumentException("Measurement noise must have 3 elements");

        lock (_stateLock)
        {
            if (_state == null)
                throw new InvalidOperationException("Filter not initialized");

            // Update measurement noise covariance
            var r = Matrix.Identity(3);
            r[0, 0] = measurementNoise[0];
            r[1, 1] = measurementNoise[1];
            r[2, 2] = measurementNoise[2];

            // Innovation: y = z - H * X
            var z = Matrix.FromArray(measurement);
            var hx = Matrix.Multiply(_state.H, _state.X);
            var y = Matrix.Subtract(z, hx);

            // Innovation covariance: S = H * P * H^T + R
            var hpht = Matrix.Multiply(Matrix.Multiply(_state.H, _state.P), Matrix.Transpose(_state.H));
            var s = Matrix.Add(hpht, r);

            // Kalman gain: K = P * H^T * S^-1
            var sInv = Matrix.Inverse(s);
            var k = Matrix.Multiply(Matrix.Multiply(_state.P, Matrix.Transpose(_state.H)), sInv);

            // Update state: X = X + K * y
            var xUpd = Matrix.Add(_state.X, Matrix.Multiply(k, y));

            // Update covariance: P = (I - K * H) * P
            var i = Matrix.Identity(6);
            var kh = Matrix.Multiply(k, _state.H);
            var iMinusKh = Matrix.Subtract(i, kh);
            var pUpd = Matrix.Multiply(iMinusKh, _state.P);

            _state = _state with { X = xUpd, P = pUpd, R = r };
        }
    }

    /// <summary>
    /// Gets the current state estimate (position only).
    /// </summary>
    /// <returns>Estimated position [x, y, z].</returns>
    public double[] GetEstimate()
    {
        lock (_stateLock)
        {
            if (_state == null)
                throw new InvalidOperationException("Filter not initialized");

            return new double[]
            {
                _state.X[0, 0],
                _state.X[1, 0],
                _state.X[2, 0]
            };
        }
    }

    /// <summary>
    /// Gets the current velocity estimate.
    /// </summary>
    /// <returns>Estimated velocity [vx, vy, vz].</returns>
    public double[] GetVelocity()
    {
        lock (_stateLock)
        {
            if (_state == null)
                throw new InvalidOperationException("Filter not initialized");

            return new double[]
            {
                _state.X[3, 0],
                _state.X[4, 0],
                _state.X[5, 0]
            };
        }
    }

    /// <summary>
    /// Gets the current state covariance (uncertainty).
    /// </summary>
    /// <returns>Position variance [σx², σy², σz²].</returns>
    public double[] GetCovariance()
    {
        lock (_stateLock)
        {
            if (_state == null)
                throw new InvalidOperationException("Filter not initialized");

            return new double[]
            {
                _state.P[0, 0],
                _state.P[1, 1],
                _state.P[2, 2]
            };
        }
    }

    /// <summary>
    /// Internal Kalman filter state.
    /// </summary>
    /// <param name="X">State vector (6x1): [x, y, z, vx, vy, vz].</param>
    /// <param name="P">State covariance matrix (6x6).</param>
    /// <param name="F">State transition matrix (6x6).</param>
    /// <param name="H">Observation matrix (3x6).</param>
    /// <param name="Q">Process noise covariance (6x6).</param>
    /// <param name="R">Measurement noise covariance (3x3).</param>
    private sealed record KalmanState(
        Matrix X,
        Matrix P,
        Matrix F,
        Matrix H,
        Matrix Q,
        Matrix R);
}
