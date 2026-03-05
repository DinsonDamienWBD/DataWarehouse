// <copyright file="DifferentialPrivacyIntegration.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateEdgeComputing.Strategies.FederatedLearning;

/// <summary>
/// Provides differential privacy mechanisms for federated learning gradients.
/// </summary>
public sealed class DifferentialPrivacyIntegration
{
    // P2-2680: Random is not thread-safe; use Random.Shared which is thread-safe (per-thread instance under the hood).
    private static readonly Random _random = Random.Shared;
    private double _remainingBudget;

    /// <summary>
    /// Initializes a new instance of the <see cref="DifferentialPrivacyIntegration"/> class.
    /// </summary>
    /// <param name="initialBudget">Initial privacy budget (epsilon).</param>
    public DifferentialPrivacyIntegration(double initialBudget = 10.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialBudget);
        _remainingBudget = initialBudget;
    }

    /// <summary>
    /// Adds differential privacy noise to gradient updates.
    /// </summary>
    /// <param name="update">Original gradient update.</param>
    /// <param name="config">Privacy configuration.</param>
    /// <returns>Gradient update with added noise and clipping.</returns>
    public GradientUpdate AddNoise(GradientUpdate update, PrivacyConfig config)
    {
        if (!config.EnablePrivacy)
        {
            return update; // No privacy, return original
        }

        var noisyGradients = new Dictionary<string, double[]>();

        foreach (var kvp in update.LayerGradients)
        {
            var layer = kvp.Key;
            var gradients = kvp.Value;

            // Step 1: Clip gradients to ClipNorm
            var clippedGradients = ClipGradients(gradients, config.ClipNorm);

            // Step 2: Add calibrated Gaussian noise
            var noisyLayer = AddGaussianNoise(clippedGradients, config);

            noisyGradients[layer] = noisyLayer;
        }

        return new GradientUpdate(
            NodeId: update.NodeId,
            LayerGradients: noisyGradients,
            SampleCount: update.SampleCount,
            Loss: update.Loss,
            Epoch: update.Epoch);
    }

    /// <summary>
    /// Clips gradient vector to maximum L2 norm.
    /// </summary>
    private double[] ClipGradients(double[] gradients, double clipNorm)
    {
        // Compute L2 norm
        var norm = Math.Sqrt(gradients.Sum(g => g * g));

        if (norm <= clipNorm)
        {
            return gradients; // No clipping needed
        }

        // Scale down to clipNorm
        var scale = clipNorm / norm;
        return gradients.Select(g => g * scale).ToArray();
    }

    /// <summary>
    /// Adds calibrated Gaussian noise to gradients.
    /// Noise scale: σ = ClipNorm * sqrt(2 * ln(1.25/delta)) / epsilon.
    /// </summary>
    private double[] AddGaussianNoise(double[] gradients, PrivacyConfig config)
    {
        // Calculate noise scale according to Gaussian mechanism
        var noiseSigma = config.ClipNorm * Math.Sqrt(2 * Math.Log(1.25 / config.Delta)) / config.Epsilon;

        var noisy = new double[gradients.Length];

        for (int i = 0; i < gradients.Length; i++)
        {
            // Sample from Gaussian distribution
            var noise = SampleGaussian(0.0, noiseSigma);
            noisy[i] = gradients[i] + noise;
        }

        return noisy;
    }

    /// <summary>
    /// Samples from Gaussian distribution using Box-Muller transform.
    /// </summary>
    private double SampleGaussian(double mean, double stdDev)
    {
        // Box-Muller transform
        var u1 = 1.0 - _random.NextDouble(); // Uniform(0,1]
        var u2 = 1.0 - _random.NextDouble();

        var randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

        return mean + stdDev * randStdNormal;
    }

    /// <summary>
    /// Gets remaining privacy budget.
    /// </summary>
    /// <returns>Remaining epsilon value.</returns>
    public double RemainingBudget()
    {
        return _remainingBudget;
    }

    /// <summary>
    /// Consumes privacy budget for one training round.
    /// </summary>
    /// <param name="epsilon">Privacy budget to consume (default from config).</param>
    public void ConsumeRound(double epsilon = 1.0)
    {
        _remainingBudget -= epsilon;

        if (_remainingBudget < 0)
        {
            _remainingBudget = 0;
        }
    }

    /// <summary>
    /// Resets privacy budget to initial value.
    /// </summary>
    /// <param name="newBudget">New privacy budget.</param>
    public void ResetBudget(double newBudget)
    {
        _remainingBudget = newBudget;
    }

    /// <summary>
    /// Checks if sufficient budget remains for another round.
    /// </summary>
    /// <param name="requiredEpsilon">Required epsilon for the round.</param>
    /// <returns>True if sufficient budget remains.</returns>
    public bool HasSufficientBudget(double requiredEpsilon)
    {
        return _remainingBudget >= requiredEpsilon;
    }
}
