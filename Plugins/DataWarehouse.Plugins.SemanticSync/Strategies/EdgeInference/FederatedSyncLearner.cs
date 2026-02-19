// <copyright file="FederatedSyncLearner.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.EdgeInference;

/// <summary>
/// Connects semantic sync model improvement to the existing federated learning infrastructure
/// in UltimateEdgeComputing -- without direct plugin references (plugin isolation rule).
/// All communication flows through message bus topics:
/// <list type="bullet">
/// <item>Publishes to <c>federated-learning.gradient-update</c> with privacy-preserving noisy gradients</item>
/// <item>Subscribes to <c>federated-learning.model-aggregated</c> to receive improved models</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Privacy is preserved through differential privacy: Laplace noise (epsilon=1.0) is added to all
/// gradients before they leave the edge node. Raw data and raw embeddings are never shared.
/// </para>
/// <para>
/// The UltimateEdgeComputing plugin's <c>FederatedLearningOrchestrator</c> subscribes to
/// <c>federated-learning.gradient-update</c> and publishes <c>federated-learning.model-aggregated</c>
/// after aggregating gradients from all participating edge nodes.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Edge inference")]
internal sealed class FederatedSyncLearner
{
    /// <summary>
    /// Topic for publishing noisy gradient updates to the federated learning orchestrator.
    /// </summary>
    private const string GradientUpdateTopic = "federated-learning.gradient-update";

    /// <summary>
    /// Topic for receiving aggregated models from the federated learning orchestrator.
    /// </summary>
    private const string ModelAggregatedTopic = "federated-learning.model-aggregated";

    /// <summary>
    /// Differential privacy parameter. Lower epsilon = more noise = more privacy.
    /// Epsilon=1.0 provides a reasonable privacy/utility tradeoff for sync classification.
    /// </summary>
    private const double DifferentialPrivacyEpsilon = 1.0;

    /// <summary>
    /// Local learning rate for online centroid updates. Small value (0.01) ensures
    /// gradual improvement without overwriting the model with a single sample.
    /// </summary>
    private const double LocalLearningRate = 0.01;

    /// <summary>
    /// Source plugin identifier for message bus messages.
    /// </summary>
    private const string SourcePluginId = "semantic-sync";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly LocalModelManager _modelManager;
    private readonly object _gradientLock = new();

    /// <summary>
    /// Accumulated gradients per importance level for periodic export.
    /// Each entry is the sum of all gradients submitted since last export.
    /// </summary>
    private readonly Dictionary<SemanticImportance, float[]> _gradientAccumulator = new();

    /// <summary>
    /// Count of gradient submissions per importance level since last export.
    /// </summary>
    private readonly Dictionary<SemanticImportance, int> _gradientCounts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FederatedSyncLearner"/> class.
    /// </summary>
    /// <param name="modelManager">The local model manager for model access and updates.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="modelManager"/> is null.</exception>
    public FederatedSyncLearner(LocalModelManager modelManager)
    {
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));

        // Initialize accumulators for all importance levels
        foreach (SemanticImportance importance in Enum.GetValues<SemanticImportance>())
        {
            _gradientAccumulator[importance] = new float[LocalModelManager.ExpectedDimensions];
            _gradientCounts[importance] = 0;
        }
    }

    /// <summary>
    /// Submits a local gradient update from a classification observation. This:
    /// <list type="number">
    /// <item>Computes gradient: difference between current centroid and new embedding</item>
    /// <item>Applies differential privacy: adds Laplace noise (epsilon=1.0) before sharing</item>
    /// <item>Packages as gradient update message for the federated learning orchestrator</item>
    /// <item>Locally updates the model via online centroid shift (learning rate 0.01)</item>
    /// </list>
    /// </summary>
    /// <param name="label">The confirmed importance level for this data observation.</param>
    /// <param name="embedding">The embedding vector of the classified data.</param>
    /// <param name="messageBus">Message bus for publishing the gradient update. May be null if unavailable.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SubmitLocalGradientAsync(
        SemanticImportance label,
        float[] embedding,
        IMessageBus? messageBus,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        ct.ThrowIfCancellationRequested();

        if (embedding.Length != LocalModelManager.ExpectedDimensions)
        {
            throw new ArgumentException(
                $"Embedding must have {LocalModelManager.ExpectedDimensions} dimensions, but had {embedding.Length}.",
                nameof(embedding));
        }

        var model = _modelManager.GetCurrentModel();
        if (model is null)
        {
            // No model loaded -- create default and proceed
            model = _modelManager.CreateDefaultModel();
            var serialized = await _modelManager.SerializeModelAsync(model, ct).ConfigureAwait(false);
            await _modelManager.LoadModelAsync(serialized, ct).ConfigureAwait(false);
            model = _modelManager.GetCurrentModel()!;
        }

        // 1. Compute gradient: difference between centroid and observation
        var centroidIndex = (int)label;
        var centroid = model.ImportanceCentroids[centroidIndex];
        var gradient = new float[LocalModelManager.ExpectedDimensions];
        for (int i = 0; i < LocalModelManager.ExpectedDimensions; i++)
        {
            gradient[i] = embedding[i] - centroid[i];
        }

        // 2. Apply differential privacy: add Laplace noise
        var noisyGradient = ApplyLaplaceNoise(gradient, DifferentialPrivacyEpsilon);

        // 3. Accumulate gradient locally
        lock (_gradientLock)
        {
            var acc = _gradientAccumulator[label];
            for (int i = 0; i < LocalModelManager.ExpectedDimensions; i++)
            {
                acc[i] += noisyGradient[i];
            }
            _gradientCounts[label]++;
        }

        // 4. Publish noisy gradient to federated learning orchestrator via message bus
        if (messageBus is not null)
        {
            var message = new PluginMessage
            {
                Type = "gradient-update",
                SourcePluginId = SourcePluginId,
                Payload = new Dictionary<string, object>
                {
                    ["importance"] = label.ToString(),
                    ["gradient"] = JsonSerializer.Serialize(noisyGradient, JsonOptions),
                    ["model_id"] = model.ModelId,
                    ["model_version"] = model.Version,
                    ["dimension_count"] = LocalModelManager.ExpectedDimensions
                },
                Description = $"Noisy gradient update for {label} importance level (epsilon={DifferentialPrivacyEpsilon})"
            };

            await messageBus.PublishAsync(GradientUpdateTopic, message, ct).ConfigureAwait(false);
        }

        // 5. Update local model with small learning rate
        var updatedModel = _modelManager.UpdateCentroids(model, label, embedding, LocalLearningRate);
        var updatedBytes = await _modelManager.SerializeModelAsync(updatedModel, ct).ConfigureAwait(false);
        await _modelManager.LoadModelAsync(updatedBytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes an aggregated model received from the federated learning orchestrator.
    /// Validates dimensionality, then loads as the new current model (with rollback available).
    /// </summary>
    /// <param name="aggregatedModel">Serialized aggregated model bytes from the orchestrator.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task OnAggregatedModelReceivedAsync(ReadOnlyMemory<byte> aggregatedModel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Deserialize to validate before loading
        var model = JsonSerializer.Deserialize<SyncInferenceModel>(aggregatedModel.Span, JsonOptions);
        if (model is null)
        {
            throw new InvalidOperationException("Failed to deserialize aggregated model: result was null.");
        }

        // Validate dimensionality matches local expectations
        if (model.ImportanceCentroids.Length != 5)
        {
            throw new InvalidOperationException(
                $"Aggregated model has {model.ImportanceCentroids.Length} importance levels, expected 5.");
        }

        for (int i = 0; i < model.ImportanceCentroids.Length; i++)
        {
            if (model.ImportanceCentroids[i].Length != LocalModelManager.ExpectedDimensions)
            {
                throw new InvalidOperationException(
                    $"Aggregated model centroid {i} has {model.ImportanceCentroids[i].Length} dimensions, " +
                    $"expected {LocalModelManager.ExpectedDimensions}.");
            }
        }

        // Load as new current model (previous model retained for rollback)
        await _modelManager.LoadModelAsync(aggregatedModel, ct).ConfigureAwait(false);

        // Clear gradient accumulator since we have a fresh model
        lock (_gradientLock)
        {
            foreach (var importance in Enum.GetValues<SemanticImportance>())
            {
                Array.Clear(_gradientAccumulator[importance]);
                _gradientCounts[importance] = 0;
            }
        }
    }

    /// <summary>
    /// Exports the accumulated local gradients as serialized bytes for federated aggregation.
    /// The existing <c>FederatedLearningOrchestrator</c> in UltimateEdgeComputing handles
    /// the actual aggregation across nodes.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized gradient accumulator as JSON bytes.</returns>
    public Task<ReadOnlyMemory<byte>> ExportGradientsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Dictionary<string, GradientExport> exportData;

        lock (_gradientLock)
        {
            exportData = new Dictionary<string, GradientExport>();
            foreach (var importance in Enum.GetValues<SemanticImportance>())
            {
                var count = _gradientCounts[importance];
                if (count == 0) continue;

                // Average the accumulated gradients
                var acc = _gradientAccumulator[importance];
                var averaged = new float[LocalModelManager.ExpectedDimensions];
                for (int i = 0; i < LocalModelManager.ExpectedDimensions; i++)
                {
                    averaged[i] = acc[i] / count;
                }

                exportData[importance.ToString()] = new GradientExport(averaged, count);
            }
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(exportData, JsonOptions);
        return Task.FromResult<ReadOnlyMemory<byte>>(bytes);
    }

    /// <summary>
    /// Gets the message bus topic this learner subscribes to for receiving aggregated models.
    /// The parent plugin should wire this subscription during initialization.
    /// </summary>
    public static string AggregatedModelTopic => ModelAggregatedTopic;

    /// <summary>
    /// Gets the message bus topic this learner publishes gradient updates to.
    /// </summary>
    public static string GradientTopic => GradientUpdateTopic;

    /// <summary>
    /// Applies Laplace noise to a gradient vector for differential privacy.
    /// The Laplace distribution has scale b = sensitivity / epsilon, where sensitivity
    /// is the L1 sensitivity of the gradient (bounded by vector norm).
    /// </summary>
    /// <param name="gradient">The raw gradient vector.</param>
    /// <param name="epsilon">Privacy budget parameter. Lower = more noise = more privacy.</param>
    /// <returns>A new gradient vector with Laplace noise added to each component.</returns>
    private static float[] ApplyLaplaceNoise(float[] gradient, double epsilon)
    {
        // Sensitivity: max L1 norm change from a single data point.
        // For unit-normalized centroids, sensitivity is bounded by 2.0.
        const double sensitivity = 2.0;
        var scale = sensitivity / epsilon;

        var noisy = new float[gradient.Length];
        Span<byte> randomBytes = stackalloc byte[8];

        for (int i = 0; i < gradient.Length; i++)
        {
            // Generate Laplace noise using inverse CDF: Laplace(0, b) = -b * sign(u-0.5) * ln(1 - 2|u-0.5|)
            RandomNumberGenerator.Fill(randomBytes);
            var u = BitConverter.ToUInt64(randomBytes) / (double)ulong.MaxValue;

            double noise;
            if (u < 0.5)
            {
                noise = scale * Math.Log(2.0 * u);
            }
            else
            {
                noise = -scale * Math.Log(2.0 * (1.0 - u));
            }

            noisy[i] = gradient[i] + (float)noise;
        }

        return noisy;
    }

    /// <summary>
    /// Internal record for gradient export serialization.
    /// </summary>
    private sealed record GradientExport(float[] AveragedGradient, int SampleCount);
}
