// <copyright file="LocalTrainingCoordinator.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateEdgeComputing.Strategies.FederatedLearning;

/// <summary>
/// Coordinates local training on edge nodes using mini-batch SGD.
/// </summary>
public sealed class LocalTrainingCoordinator
{
    private const double GradientEpsilon = 1e-5;

    /// <summary>
    /// Performs local training on edge node data.
    /// </summary>
    /// <param name="nodeId">Identifier of the node performing training.</param>
    /// <param name="globalWeights">Current global model weights.</param>
    /// <param name="localData">Local training data (features).</param>
    /// <param name="localLabels">Local training labels (targets).</param>
    /// <param name="config">Training configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Gradient update computed from local training.</returns>
    public async Task<GradientUpdate> TrainLocalAsync(
        string nodeId,
        ModelWeights globalWeights,
        double[][] localData,
        double[][] localLabels,
        TrainingConfig config,
        CancellationToken ct = default)
    {
        var currentWeights = CopyWeights(globalWeights.LayerWeights);
        var totalSamples = localData.Length;
        var numBatches = (int)Math.Ceiling((double)totalSamples / config.BatchSize);
        var cumulativeLoss = 0.0;

        // Train for specified number of epochs
        for (int epoch = 0; epoch < config.Epochs; epoch++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Process mini-batches
            for (int batch = 0; batch < numBatches; batch++)
            {
                var batchStart = batch * config.BatchSize;
                var batchEnd = Math.Min(batchStart + config.BatchSize, totalSamples);
                var batchSize = batchEnd - batchStart;

                var batchData = localData[batchStart..batchEnd];
                var batchLabels = localLabels[batchStart..batchEnd];

                // Compute gradients for this batch
                var gradients = ComputeGradients(currentWeights, batchData, batchLabels);

                // Update weights using gradient descent
                foreach (var layer in currentWeights.Keys.ToList())
                {
                    for (int i = 0; i < currentWeights[layer].Length; i++)
                    {
                        currentWeights[layer][i] -= config.LearningRate * gradients[layer][i];
                    }
                }

                // Compute loss for this batch
                var batchLoss = ComputeLoss(currentWeights, batchData, batchLabels);
                cumulativeLoss += batchLoss;

                // Simulate training delay
                await Task.Delay(5, ct);
            }
        }

        var averageLoss = cumulativeLoss / (config.Epochs * numBatches);

        // Compute final gradients (difference from initial weights)
        var finalGradients = new Dictionary<string, double[]>();
        foreach (var layer in globalWeights.LayerWeights.Keys)
        {
            finalGradients[layer] = new double[globalWeights.LayerWeights[layer].Length];
            for (int i = 0; i < globalWeights.LayerWeights[layer].Length; i++)
            {
                finalGradients[layer][i] = globalWeights.LayerWeights[layer][i] - currentWeights[layer][i];
            }
        }

        return new GradientUpdate(
            NodeId: nodeId,
            LayerGradients: finalGradients,
            SampleCount: totalSamples,
            Loss: averageLoss,
            Epoch: config.Epochs);
    }

    /// <summary>
    /// Computes gradients using numerical approximation.
    /// </summary>
    private Dictionary<string, double[]> ComputeGradients(
        Dictionary<string, double[]> weights,
        double[][] batchData,
        double[][] batchLabels)
    {
        var gradients = new Dictionary<string, double[]>();

        foreach (var layer in weights.Keys)
        {
            gradients[layer] = new double[weights[layer].Length];

            for (int i = 0; i < weights[layer].Length; i++)
            {
                // Compute gradient via finite difference
                var originalWeight = weights[layer][i];

                // Perturb weight positively
                weights[layer][i] = originalWeight + GradientEpsilon;
                var lossPlus = ComputeLoss(weights, batchData, batchLabels);

                // Perturb weight negatively
                weights[layer][i] = originalWeight - GradientEpsilon;
                var lossMinus = ComputeLoss(weights, batchData, batchLabels);

                // Restore original weight
                weights[layer][i] = originalWeight;

                // Gradient is the slope
                gradients[layer][i] = (lossPlus - lossMinus) / (2 * GradientEpsilon);
            }
        }

        return gradients;
    }

    /// <summary>
    /// Computes mean squared error loss over a batch.
    /// </summary>
    private double ComputeLoss(
        Dictionary<string, double[]> weights,
        double[][] batchData,
        double[][] batchLabels)
    {
        var totalLoss = 0.0;

        for (int i = 0; i < batchData.Length; i++)
        {
            var prediction = ForwardPass(weights, batchData[i]);
            var label = batchLabels[i][0]; // Assume single output

            var error = prediction - label;
            totalLoss += error * error;
        }

        return totalLoss / batchData.Length;
    }

    /// <summary>
    /// Performs forward pass through simple linear model.
    /// </summary>
    private double ForwardPass(Dictionary<string, double[]> weights, double[] features)
    {
        var output = 0.0;

        // Simple linear model: output = weights * features + bias
        if (weights.ContainsKey("weights") && weights.ContainsKey("bias"))
        {
            var w = weights["weights"];
            var bias = weights["bias"][0];

            for (int i = 0; i < Math.Min(features.Length, w.Length); i++)
            {
                output += w[i] * features[i];
            }

            output += bias;
        }

        return output;
    }

    /// <summary>
    /// Creates a deep copy of weights dictionary.
    /// </summary>
    private Dictionary<string, double[]> CopyWeights(Dictionary<string, double[]> weights)
    {
        var copy = new Dictionary<string, double[]>();
        foreach (var kvp in weights)
        {
            copy[kvp.Key] = (double[])kvp.Value.Clone();
        }
        return copy;
    }
}
