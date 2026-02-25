// <copyright file="ConvergenceDetector.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateEdgeComputing.Strategies.FederatedLearning;

/// <summary>
/// Detects convergence in federated learning based on loss history.
/// </summary>
public sealed class ConvergenceDetector
{
    private readonly double _lossThreshold;
    private readonly int _patience;
    private readonly List<double> _lossHistory = new();
    private int _stableRounds = 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConvergenceDetector"/> class.
    /// </summary>
    /// <param name="lossThreshold">Minimum loss delta required to reset patience counter.</param>
    /// <param name="patience">Number of consecutive rounds with small loss change to consider converged.</param>
    public ConvergenceDetector(double lossThreshold = 0.001, int patience = 5)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lossThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(patience);
        _lossThreshold = lossThreshold;
        _patience = patience;
    }

    /// <summary>
    /// Records loss from a training round.
    /// </summary>
    /// <param name="loss">Loss value to record.</param>
    public void RecordLoss(double loss)
    {
        _lossHistory.Add(loss);

        // Check if loss change is below threshold
        if (_lossHistory.Count > 1)
        {
            var previousLoss = _lossHistory[^2];
            var lossDelta = Math.Abs(previousLoss - loss);

            if (lossDelta < _lossThreshold)
            {
                _stableRounds++;
            }
            else
            {
                _stableRounds = 0; // Reset counter if loss changed significantly
            }
        }
    }

    /// <summary>
    /// Checks whether training has converged.
    /// </summary>
    /// <returns>True if converged (loss stable for patience rounds), false otherwise.</returns>
    public bool HasConverged()
    {
        return _stableRounds >= _patience;
    }

    /// <summary>
    /// Gets the complete loss history.
    /// </summary>
    /// <returns>Array of loss values across all rounds.</returns>
    public double[] GetLossHistory()
    {
        return _lossHistory.ToArray();
    }

    /// <summary>
    /// Gets the current number of stable rounds.
    /// </summary>
    /// <returns>Number of consecutive rounds with loss change below threshold.</returns>
    public int GetStableRounds()
    {
        return _stableRounds;
    }

    /// <summary>
    /// Resets the convergence detector state.
    /// </summary>
    public void Reset()
    {
        _lossHistory.Clear();
        _stableRounds = 0;
    }

    /// <summary>
    /// Gets the most recent loss value.
    /// </summary>
    /// <returns>Most recent loss, or double.NaN if no history.</returns>
    public double GetCurrentLoss()
    {
        return _lossHistory.Count > 0 ? _lossHistory[^1] : double.NaN;
    }

    /// <summary>
    /// Gets the improvement rate from the first to most recent loss.
    /// </summary>
    /// <returns>Percentage improvement, or 0 if insufficient history.</returns>
    public double GetImprovementRate()
    {
        if (_lossHistory.Count < 2)
        {
            return 0.0;
        }

        var initialLoss = _lossHistory[0];
        var currentLoss = _lossHistory[^1];

        if (initialLoss == 0.0)
        {
            return 0.0;
        }

        return (initialLoss - currentLoss) / initialLoss * 100.0;
    }
}
