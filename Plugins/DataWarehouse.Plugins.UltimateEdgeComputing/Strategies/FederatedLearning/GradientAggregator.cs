// <copyright file="GradientAggregator.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateEdgeComputing.Strategies.FederatedLearning;

/// <summary>
/// Aggregates gradients from multiple nodes using FedAvg or FedSGD strategies.
/// </summary>
public sealed class GradientAggregator
{
    /// <summary>
    /// Aggregates gradient updates from multiple nodes into new global weights.
    /// </summary>
    /// <param name="updates">Array of gradient updates from participating nodes.</param>
    /// <param name="strategy">Aggregation strategy to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New global model weights after aggregation.</returns>
    public async Task<ModelWeights> AggregateAsync(
        GradientUpdate[] updates,
        AggregationStrategy strategy,
        CancellationToken ct = default)
    {
        if (updates.Length == 0)
        {
            throw new ArgumentException("No gradient updates to aggregate", nameof(updates));
        }

        await Task.CompletedTask; // Keep async signature

        return strategy switch
        {
            AggregationStrategy.FedAvg => AggregateWithFedAvg(updates),
            AggregationStrategy.FedSGD => AggregateWithFedSGD(updates),
            _ => throw new ArgumentException($"Unknown aggregation strategy: {strategy}")
        };
    }

    /// <summary>
    /// Federated Averaging: weighted average by sample count.
    /// w_new = Σ(n_k/n * w_k) where n_k = samples from node k, n = total samples.
    /// </summary>
    private ModelWeights AggregateWithFedAvg(GradientUpdate[] updates)
    {
        var totalSamples = updates.Sum(u => u.SampleCount);
        var aggregatedWeights = new Dictionary<string, double[]>();

        // Get all layer names from first update
        var layerNames = updates[0].LayerGradients.Keys.ToList();

        foreach (var layer in layerNames)
        {
            // Determine layer size
            var layerSize = updates[0].LayerGradients[layer].Length;
            aggregatedWeights[layer] = new double[layerSize];

            // Weighted average across all nodes
            foreach (var update in updates)
            {
                if (!update.LayerGradients.ContainsKey(layer))
                {
                    continue; // Skip if layer missing
                }

                var weight = (double)update.SampleCount / totalSamples;

                for (int i = 0; i < Math.Min(layerSize, update.LayerGradients[layer].Length); i++)
                {
                    aggregatedWeights[layer][i] += weight * update.LayerGradients[layer][i];
                }
            }
        }

        var averageLoss = updates.Average(u => u.Loss);

        return new ModelWeights(
            LayerWeights: aggregatedWeights,
            Version: ComputeContentVersion(aggregatedWeights),
            CreatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Federated SGD: simple average of per-sample gradients.
    /// g = (1/K) * Σ g_k where K = number of nodes.
    /// </summary>
    private ModelWeights AggregateWithFedSGD(GradientUpdate[] updates)
    {
        var numNodes = updates.Length;
        var aggregatedWeights = new Dictionary<string, double[]>();

        // Get all layer names from first update
        var layerNames = updates[0].LayerGradients.Keys.ToList();

        foreach (var layer in layerNames)
        {
            // Determine layer size
            var layerSize = updates[0].LayerGradients[layer].Length;
            aggregatedWeights[layer] = new double[layerSize];

            // Simple average across all nodes
            foreach (var update in updates)
            {
                if (!update.LayerGradients.ContainsKey(layer))
                {
                    continue; // Skip if layer missing
                }

                for (int i = 0; i < Math.Min(layerSize, update.LayerGradients[layer].Length); i++)
                {
                    aggregatedWeights[layer][i] += update.LayerGradients[layer][i] / numNodes;
                }
            }
        }

        return new ModelWeights(
            LayerWeights: aggregatedWeights,
            Version: ComputeContentVersion(aggregatedWeights),
            CreatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Applies aggregated gradients to current weights to produce new weights.
    /// </summary>
    /// <param name="currentWeights">Current global weights.</param>
    /// <param name="gradients">Aggregated gradients to apply.</param>
    /// <param name="learningRate">Learning rate for weight update.</param>
    /// <returns>New weights after applying gradients.</returns>
    public ModelWeights ApplyGradients(
        ModelWeights currentWeights,
        Dictionary<string, double[]> gradients,
        double learningRate = 1.0)
    {
        var newWeights = new Dictionary<string, double[]>();

        foreach (var layer in currentWeights.LayerWeights.Keys)
        {
            newWeights[layer] = new double[currentWeights.LayerWeights[layer].Length];

            if (gradients.ContainsKey(layer))
            {
                for (int i = 0; i < currentWeights.LayerWeights[layer].Length; i++)
                {
                    newWeights[layer][i] = currentWeights.LayerWeights[layer][i] -
                        learningRate * gradients[layer][i];
                }
            }
            else
            {
                // No gradient for this layer, keep weights unchanged
                Array.Copy(currentWeights.LayerWeights[layer], newWeights[layer],
                    currentWeights.LayerWeights[layer].Length);
            }
        }

        return new ModelWeights(
            LayerWeights: newWeights,
            Version: currentWeights.Version + 1,
            CreatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Computes a content-based version hash from aggregated weights using SHA-256.
    /// Produces a stable, deterministic version that reflects actual weight content,
    /// unlike object.GetHashCode() which is identity-based and non-deterministic.
    /// </summary>
    private static int ComputeContentVersion(Dictionary<string, double[]> weights)
    {
        // Serialize weight magnitudes deterministically and hash them
        using var sha = System.Security.Cryptography.SHA256.Create();
        var buffer = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(buffer);

        foreach (var layer in weights.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            writer.Write(layer);
            foreach (var w in weights[layer])
                writer.Write(w);
        }

        writer.Flush();
        var hash = sha.ComputeHash(buffer.ToArray());
        // Use first 4 bytes as int version (deterministic, content-based)
        return System.BitConverter.ToInt32(hash, 0);
    }
}
