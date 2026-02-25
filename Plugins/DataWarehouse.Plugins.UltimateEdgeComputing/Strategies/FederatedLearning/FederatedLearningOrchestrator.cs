// <copyright file="FederatedLearningOrchestrator.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateEdgeComputing.Strategies.FederatedLearning;

/// <summary>
/// Main coordinator for federated learning across edge nodes.
/// </summary>
public sealed class FederatedLearningOrchestrator
{
    private readonly TrainingConfig _config;
    private readonly PrivacyConfig _privacyConfig;
    private readonly BoundedDictionary<string, bool> _registeredNodes = new BoundedDictionary<string, bool>(1000);
    private readonly ModelDistributor _distributor = new();
    private readonly LocalTrainingCoordinator _trainer = new();
    private readonly GradientAggregator _aggregator = new();
    private readonly ConvergenceDetector _convergenceDetector;
    private readonly DifferentialPrivacyIntegration? _privacyIntegration;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederatedLearningOrchestrator"/> class.
    /// </summary>
    /// <param name="config">Training configuration.</param>
    /// <param name="privacyConfig">Privacy configuration.</param>
    public FederatedLearningOrchestrator(TrainingConfig? config = null, PrivacyConfig? privacyConfig = null)
    {
        _config = config ?? new TrainingConfig();
        _privacyConfig = privacyConfig ?? new PrivacyConfig();
        _convergenceDetector = new ConvergenceDetector();

        if (_privacyConfig.EnablePrivacy)
        {
            _privacyIntegration = new DifferentialPrivacyIntegration(_privacyConfig.Epsilon * _config.MaxRounds);
        }
    }

    /// <summary>
    /// Registers a node for federated learning.
    /// </summary>
    /// <param name="nodeId">Node identifier to register.</param>
    public void RegisterNode(string nodeId)
    {
        _registeredNodes[nodeId] = true;
    }

    /// <summary>
    /// Unregisters a node from federated learning.
    /// </summary>
    /// <param name="nodeId">Node identifier to unregister.</param>
    public void UnregisterNode(string nodeId)
    {
        _registeredNodes.TryRemove(nodeId, out _);
    }

    /// <summary>
    /// Gets the list of registered node IDs.
    /// </summary>
    /// <returns>Array of registered node identifiers.</returns>
    public string[] GetRegisteredNodes()
    {
        return _registeredNodes.Keys.ToArray();
    }

    /// <summary>
    /// Runs federated training across all registered nodes.
    /// </summary>
    /// <param name="initialWeights">Initial global model weights.</param>
    /// <param name="dataProvider">Function to provide training data for each node.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Final training result with metrics.</returns>
    public async Task<TrainingResult> RunTrainingAsync(
        ModelWeights initialWeights,
        Func<string, Task<(double[][] data, double[][] labels)>> dataProvider,
        CancellationToken ct = default)
    {
        var currentWeights = initialWeights;
        var roundResults = new List<RoundResult>();
        var converged = false;

        _convergenceDetector.Reset();

        for (int round = 1; round <= _config.MaxRounds; round++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var roundStart = DateTimeOffset.UtcNow;

            // Step 1: Distribute current weights to all nodes
            var nodeIds = GetRegisteredNodes();
            await _distributor.DistributeAsync(currentWeights, nodeIds, ct);

            // Step 2: Wait for gradient updates with straggler timeout
            var updates = await CollectGradientUpdatesAsync(
                nodeIds,
                currentWeights,
                dataProvider,
                ct);

            // Check minimum participation
            var participationRate = (double)updates.Length / nodeIds.Length;
            if (participationRate < _config.MinParticipation)
            {
                // Insufficient participation, skip this round
                continue;
            }

            // Step 3: Apply differential privacy if enabled
            if (_privacyConfig.EnablePrivacy && _privacyIntegration != null)
            {
                if (!_privacyIntegration.HasSufficientBudget(_privacyConfig.Epsilon))
                {
                    // Privacy budget exhausted
                    break;
                }

                updates = updates.Select(u => _privacyIntegration.AddNoise(u, _privacyConfig)).ToArray();
                _privacyIntegration.ConsumeRound(_privacyConfig.Epsilon);
            }

            // Step 4: Aggregate gradients
            var aggregatedGradients = await _aggregator.AggregateAsync(
                updates,
                AggregationStrategy.FedAvg,
                ct);

            currentWeights = aggregatedGradients;

            // Step 5: Calculate global loss
            var globalLoss = updates.Average(u => u.Loss);
            _convergenceDetector.RecordLoss(globalLoss);

            // Record round result
            var roundDuration = DateTimeOffset.UtcNow - roundStart;
            roundResults.Add(new RoundResult(
                RoundNumber: round,
                ParticipatingNodes: updates.Length,
                GlobalLoss: globalLoss,
                AggregatedWeights: currentWeights,
                Duration: roundDuration));

            // Step 6: Check convergence
            if (_convergenceDetector.HasConverged())
            {
                converged = true;
                break;
            }
        }

        return new TrainingResult(
            FinalWeights: currentWeights,
            RoundResults: roundResults.ToArray(),
            TotalRounds: roundResults.Count,
            Converged: converged,
            FinalLoss: _convergenceDetector.GetCurrentLoss());
    }

    /// <summary>
    /// Collects gradient updates from nodes with straggler timeout.
    /// </summary>
    private async Task<GradientUpdate[]> CollectGradientUpdatesAsync(
        string[] nodeIds,
        ModelWeights currentWeights,
        Func<string, Task<(double[][] data, double[][] labels)>> dataProvider,
        CancellationToken ct)
    {
        var updates = new ConcurrentBag<GradientUpdate>();
        var timeout = TimeSpan.FromMilliseconds(_config.StragglerTimeoutMs);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // Launch training tasks for all nodes
        var tasks = nodeIds.Select(async nodeId =>
        {
            try
            {
                var (data, labels) = await dataProvider(nodeId);

                if (data.Length == 0 || labels.Length == 0)
                {
                    return; // Skip nodes with no data
                }

                var update = await _trainer.TrainLocalAsync(
                    nodeId,
                    currentWeights,
                    data,
                    labels,
                    _config,
                    cts.Token);

                updates.Add(update);
            }
            catch (OperationCanceledException ex)
            {

                // Straggler timeout or cancellation - skip this node
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {

                // Training error - skip this node
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }).ToArray();

        // Wait for all tasks or timeout
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {

            // Some tasks failed or timed out
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return updates.ToArray();
    }

    /// <summary>
    /// Gets current training statistics.
    /// </summary>
    /// <returns>Training statistics including loss history and convergence state.</returns>
    public (double[] lossHistory, bool hasConverged, int stableRounds) GetTrainingStats()
    {
        return (
            _convergenceDetector.GetLossHistory(),
            _convergenceDetector.HasConverged(),
            _convergenceDetector.GetStableRounds());
    }

    /// <summary>
    /// Gets remaining privacy budget if privacy is enabled.
    /// </summary>
    /// <returns>Remaining privacy budget, or null if privacy not enabled.</returns>
    public double? GetRemainingPrivacyBudget()
    {
        return _privacyIntegration?.RemainingBudget();
    }
}
