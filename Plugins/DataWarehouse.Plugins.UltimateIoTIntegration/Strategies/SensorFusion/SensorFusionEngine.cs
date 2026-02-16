// <copyright file="SensorFusionEngine.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateIoTIntegration.Strategies.SensorFusion;

/// <summary>
/// Main sensor fusion pipeline orchestrator.
/// Routes sensor readings through appropriate fusion algorithms based on sensor types.
/// </summary>
public sealed class SensorFusionEngine
{
    private readonly FusionPipelineConfig _config;
    private readonly TemporalAligner _temporalAligner;
    private readonly KalmanFilter? _kalmanFilter;
    private readonly ComplementaryFilter? _complementaryFilter;
    private readonly WeightedAverageFusion? _weightedAverageFusion;
    private readonly VotingFusion? _votingFusion;

    /// <summary>
    /// Initializes a new instance of the <see cref="SensorFusionEngine"/> class.
    /// </summary>
    /// <param name="config">Optional fusion pipeline configuration.</param>
    public SensorFusionEngine(FusionPipelineConfig? config = null)
    {
        _config = config ?? new FusionPipelineConfig();
        _temporalAligner = new TemporalAligner(toleranceMs: _config.TemporalAlignmentToleranceMs);

        // Initialize enabled algorithms
        if (_config.EnableKalman)
        {
            _kalmanFilter = new KalmanFilter();
        }

        if (_config.EnableComplementary)
        {
            _complementaryFilter = new ComplementaryFilter();
        }

        if (_config.EnableWeightedAverage)
        {
            _weightedAverageFusion = new WeightedAverageFusion();
        }

        if (_config.EnableVoting)
        {
            _votingFusion = new VotingFusion();
        }
    }

    /// <summary>
    /// Gets the Kalman filter instance (if enabled).
    /// </summary>
    public KalmanFilter? KalmanFilter => _kalmanFilter;

    /// <summary>
    /// Gets the complementary filter instance (if enabled).
    /// </summary>
    public ComplementaryFilter? ComplementaryFilter => _complementaryFilter;

    /// <summary>
    /// Gets the weighted average fusion instance (if enabled).
    /// </summary>
    public WeightedAverageFusion? WeightedAverageFusion => _weightedAverageFusion;

    /// <summary>
    /// Gets the voting fusion instance (if enabled).
    /// </summary>
    public VotingFusion? VotingFusion => _votingFusion;

    /// <summary>
    /// Processes sensor readings through the fusion pipeline.
    /// </summary>
    /// <param name="readings">Array of sensor readings to fuse.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Fused reading result.</returns>
    public async Task<FusedReading> ProcessAsync(SensorReading[] readings, CancellationToken ct = default)
    {
        if (readings.Length == 0)
            throw new ArgumentException("At least one reading is required", nameof(readings));

        // Step 1: Temporal alignment
        var targetTimestamp = readings.Max(r => r.Timestamp);
        var alignedReadings = await _temporalAligner.AlignAsync(readings, targetTimestamp, ct);

        if (alignedReadings.Length == 0)
        {
            // No readings within tolerance - return simple average
            return CreateSimpleAverage(readings);
        }

        // Step 2: Route to appropriate fusion algorithm based on sensor types
        var sensorTypes = alignedReadings.Select(r => r.SensorType).Distinct().ToArray();

        // GPS + IMU → Kalman filter
        if (sensorTypes.Contains(SensorType.GPS) &&
            (sensorTypes.Contains(SensorType.IMU) || sensorTypes.Contains(SensorType.Accelerometer)))
        {
            if (_kalmanFilter != null)
            {
                return await ProcessWithKalmanAsync(alignedReadings, ct);
            }
        }

        // Accelerometer + Gyroscope → Complementary filter
        if (sensorTypes.Contains(SensorType.Accelerometer) && sensorTypes.Contains(SensorType.Gyroscope))
        {
            if (_complementaryFilter != null)
            {
                return ProcessWithComplementary(alignedReadings);
            }
        }

        // Multiple sensors of same type → Voting or weighted average
        var typeCounts = alignedReadings.GroupBy(r => r.SensorType).Select(g => g.Count()).Max();
        if (typeCounts >= 3)
        {
            // 3+ redundant sensors → voting
            if (_votingFusion != null)
            {
                return _votingFusion.Vote(alignedReadings);
            }
        }

        // Default: Weighted average
        if (_weightedAverageFusion != null)
        {
            return _weightedAverageFusion.Fuse(alignedReadings);
        }

        // Fallback: Simple average
        return CreateSimpleAverage(alignedReadings);
    }

    private async Task<FusedReading> ProcessWithKalmanAsync(SensorReading[] readings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Find GPS and IMU/Accelerometer readings
        var gpsReading = readings.FirstOrDefault(r => r.SensorType == SensorType.GPS);
        var imuReading = readings.FirstOrDefault(r => r.SensorType == SensorType.IMU)
                        ?? readings.FirstOrDefault(r => r.SensorType == SensorType.Accelerometer);

        if (gpsReading == null || imuReading == null)
        {
            return CreateSimpleAverage(readings);
        }

        // Initialize filter if needed
        if (_kalmanFilter!.GetEstimate == null)
        {
            _kalmanFilter.Initialize(gpsReading.Value);
        }

        // Predict step (assume 0.1s time step)
        _kalmanFilter.Predict(0.1);

        // Update step with GPS measurement
        var measurementNoise = new double[] { 1.0, 1.0, 1.0 }; // GPS noise
        _kalmanFilter.Update(gpsReading.Value, measurementNoise);

        // Get filtered estimate
        var estimate = _kalmanFilter.GetEstimate();
        var covariance = _kalmanFilter.GetCovariance();

        // Compute confidence from covariance
        double confidence = ComputeConfidenceFromCovariance(covariance);

        return new FusedReading(
            readings,
            estimate,
            confidence,
            "Kalman",
            DateTimeOffset.UtcNow);
    }

    private FusedReading ProcessWithComplementary(SensorReading[] readings)
    {
        var accelReading = readings.FirstOrDefault(r => r.SensorType == SensorType.Accelerometer);
        var gyroReading = readings.FirstOrDefault(r => r.SensorType == SensorType.Gyroscope);

        if (accelReading == null || gyroReading == null)
        {
            return CreateSimpleAverage(readings);
        }

        // Update complementary filter (assume 0.01s time step)
        var orientation = _complementaryFilter!.Update(accelReading.Value, gyroReading.Value, 0.01);

        return new FusedReading(
            readings,
            orientation,
            0.9, // High confidence for complementary filter
            "Complementary",
            DateTimeOffset.UtcNow);
    }

    private FusedReading CreateSimpleAverage(SensorReading[] readings)
    {
        int valueDim = readings[0].Value.Length;
        var fusedValue = new double[valueDim];

        for (int dim = 0; dim < valueDim; dim++)
        {
            fusedValue[dim] = readings.Average(r => r.Value[dim]);
        }

        return new FusedReading(
            readings,
            fusedValue,
            0.5, // Medium confidence for simple average
            "SimpleAverage",
            DateTimeOffset.UtcNow);
    }

    private double ComputeConfidenceFromCovariance(double[] covariance)
    {
        // Average variance across dimensions
        double avgVariance = covariance.Average();

        // Convert to confidence: lower variance = higher confidence
        // Use exponential decay: confidence = exp(-variance)
        double confidence = Math.Exp(-avgVariance / 10.0);
        return Math.Max(0.0, Math.Min(1.0, confidence));
    }
}
