// <copyright file="FederatedLearningModels.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.UltimateEdgeComputing.Strategies.FederatedLearning;

/// <summary>
/// Represents model weights for a neural network layer.
/// </summary>
/// <param name="LayerWeights">Dictionary mapping layer names to their weight arrays.</param>
/// <param name="Version">Version number of the model weights.</param>
/// <param name="CreatedAt">Timestamp when the weights were created.</param>
public record ModelWeights(
    Dictionary<string, double[]> LayerWeights,
    int Version,
    DateTimeOffset CreatedAt);

/// <summary>
/// Represents gradient updates computed by a node during local training.
/// </summary>
/// <param name="NodeId">Identifier of the node that computed the gradients.</param>
/// <param name="LayerGradients">Dictionary mapping layer names to gradient arrays.</param>
/// <param name="SampleCount">Number of training samples used to compute gradients.</param>
/// <param name="Loss">Training loss achieved during local training.</param>
/// <param name="Epoch">Epoch number during which gradients were computed.</param>
public record GradientUpdate(
    string NodeId,
    Dictionary<string, double[]> LayerGradients,
    int SampleCount,
    double Loss,
    int Epoch);

/// <summary>
/// Configuration for federated training process.
/// </summary>
/// <param name="Epochs">Number of training epochs per round.</param>
/// <param name="BatchSize">Mini-batch size for training.</param>
/// <param name="LearningRate">Learning rate for gradient descent.</param>
/// <param name="MinParticipation">Minimum fraction of nodes required to participate (0.0 to 1.0).</param>
/// <param name="StragglerTimeoutMs">Timeout in milliseconds to wait for stragglers.</param>
/// <param name="MaxRounds">Maximum number of training rounds.</param>
public record TrainingConfig(
    int Epochs = 5,
    int BatchSize = 32,
    double LearningRate = 0.01,
    double MinParticipation = 0.5,
    int StragglerTimeoutMs = 30000,
    int MaxRounds = 100);

/// <summary>
/// Strategy for aggregating gradients from multiple nodes.
/// </summary>
public enum AggregationStrategy
{
    /// <summary>Federated Averaging - weight by sample count.</summary>
    FedAvg,

    /// <summary>Federated Stochastic Gradient Descent - simple average.</summary>
    FedSGD
}

/// <summary>
/// Result of a single training round.
/// </summary>
/// <param name="RoundNumber">Round number (1-based).</param>
/// <param name="ParticipatingNodes">Number of nodes that participated in this round.</param>
/// <param name="GlobalLoss">Global loss after aggregation.</param>
/// <param name="AggregatedWeights">Aggregated model weights.</param>
/// <param name="Duration">Duration of the round.</param>
public record RoundResult(
    int RoundNumber,
    int ParticipatingNodes,
    double GlobalLoss,
    ModelWeights AggregatedWeights,
    TimeSpan Duration);

/// <summary>
/// Configuration for differential privacy in federated learning.
/// </summary>
/// <param name="Epsilon">Privacy budget parameter (lower = more privacy).</param>
/// <param name="Delta">Probability of privacy breach (typically very small).</param>
/// <param name="ClipNorm">L2 norm threshold for gradient clipping.</param>
/// <param name="EnablePrivacy">Whether to enable differential privacy.</param>
public record PrivacyConfig(
    double Epsilon = 1.0,
    double Delta = 1e-5,
    double ClipNorm = 1.0,
    bool EnablePrivacy = false);

/// <summary>
/// Final result of federated training.
/// </summary>
/// <param name="FinalWeights">Final trained model weights.</param>
/// <param name="RoundResults">Results from each training round.</param>
/// <param name="TotalRounds">Total number of rounds executed.</param>
/// <param name="Converged">Whether training converged.</param>
/// <param name="FinalLoss">Final global loss.</param>
public record TrainingResult(
    ModelWeights FinalWeights,
    RoundResult[] RoundResults,
    int TotalRounds,
    bool Converged,
    double FinalLoss);
