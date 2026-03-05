// <copyright file="ModelDistributor.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateEdgeComputing.Strategies.FederatedLearning;

/// <summary>
/// Distributes model weights to edge nodes for federated learning.
/// </summary>
public sealed class ModelDistributor
{
    private readonly BoundedDictionary<string, ModelWeights> _nodeWeights = new BoundedDictionary<string, ModelWeights>(1000);

    /// <summary>
    /// Distributes model weights to specified edge nodes.
    /// </summary>
    /// <param name="weights">Model weights to distribute.</param>
    /// <param name="nodeIds">Array of node identifiers to distribute to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary mapping node IDs to success status.</returns>
    public async Task<Dictionary<string, bool>> DistributeAsync(
        ModelWeights weights,
        string[] nodeIds,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, bool>();

        foreach (var nodeId in nodeIds)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                // Store weights in the local cache for node to pull via GetLatestWeightsAsync.
                // Real distribution to remote nodes occurs through the federated learning
                // orchestrator's transport layer configured by the operator.
                _nodeWeights[nodeId] = weights;
                results[nodeId] = true;
                await Task.Yield(); // Yield to allow cancellation checks between iterations
            }
            catch
            {
                results[nodeId] = false;
            }
        }

        return results;
    }

    /// <summary>
    /// Serializes model weights to byte array using JSON.
    /// </summary>
    /// <param name="weights">Model weights to serialize.</param>
    /// <returns>Serialized byte array.</returns>
    public byte[] SerializeWeights(ModelWeights weights)
    {
        var json = JsonSerializer.Serialize(weights, new JsonSerializerOptions
        {
            WriteIndented = false
        });
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Deserializes model weights from byte array.
    /// </summary>
    /// <param name="data">Serialized byte array.</param>
    /// <returns>Deserialized model weights.</returns>
    public ModelWeights DeserializeWeights(byte[] data)
    {
        var json = System.Text.Encoding.UTF8.GetString(data);
        return JsonSerializer.Deserialize<ModelWeights>(json)
            ?? throw new InvalidOperationException("Failed to deserialize weights");
    }

    /// <summary>
    /// Retrieves the latest weights for a specific node.
    /// </summary>
    /// <param name="nodeId">Node identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Latest weights for the node, or null if not available.</returns>
    public Task<ModelWeights?> GetLatestWeightsAsync(string nodeId, CancellationToken ct = default)
    {
        _nodeWeights.TryGetValue(nodeId, out var weights);
        return Task.FromResult<ModelWeights?>(weights);
    }

    /// <summary>
    /// Clears stored weights for a specific node.
    /// </summary>
    /// <param name="nodeId">Node identifier.</param>
    public void ClearNodeWeights(string nodeId)
    {
        _nodeWeights.TryRemove(nodeId, out _);
    }

    /// <summary>
    /// Clears all stored weights.
    /// </summary>
    public void ClearAllWeights()
    {
        _nodeWeights.Clear();
    }
}
