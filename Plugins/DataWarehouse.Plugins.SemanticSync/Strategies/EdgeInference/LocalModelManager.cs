// <copyright file="LocalModelManager.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;

namespace DataWarehouse.Plugins.SemanticSync.Strategies.EdgeInference;

/// <summary>
/// Lightweight inference model representation for edge sync decisions.
/// Uses centroid vectors per importance level, classification thresholds,
/// and domain-specific rules -- NOT a neural network. Runs on any edge device.
/// </summary>
/// <param name="ModelId">Unique identifier for this model version.</param>
/// <param name="Version">Semantic version string for tracking model lineage.</param>
/// <param name="ImportanceCentroids">
/// Five float arrays (one per <see cref="SemanticImportance"/> level), each an embedding
/// centroid representing the "center" of that importance class in embedding space.
/// </param>
/// <param name="ClassificationThresholds">
/// Similarity thresholds for each importance boundary (e.g., "critical_min" = 0.85).
/// </param>
/// <param name="DomainRules">
/// Domain-hint to tag mappings for rule-based enhancement (e.g., "healthcare" -> ["phi", "hipaa"]).
/// </param>
/// <param name="TrainedAt">UTC timestamp when this model was last trained or updated.</param>
/// <param name="TrainingSampleCount">Number of training samples used to produce this model.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Edge inference")]
public sealed record SyncInferenceModel(
    string ModelId,
    string Version,
    float[][] ImportanceCentroids,
    Dictionary<string, double> ClassificationThresholds,
    Dictionary<string, string[]> DomainRules,
    DateTimeOffset TrainedAt,
    int TrainingSampleCount);

/// <summary>
/// Manages the lifecycle of lightweight inference models on edge nodes: load, run, update, rollback.
/// Thread-safe via <see cref="ReaderWriterLockSlim"/> -- concurrent reads, exclusive writes.
/// Models are centroid-based (not neural networks) so they load instantly and run on any hardware.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Edge inference")]
internal sealed class LocalModelManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Dimensionality of the centroid embedding vectors.
    /// All centroids must have exactly this many dimensions.
    /// </summary>
    private const int EmbeddingDimensions = 8;

    /// <summary>
    /// Number of importance levels (Critical, High, Normal, Low, Negligible).
    /// </summary>
    private const int ImportanceLevelCount = 5;

    private readonly ReaderWriterLockSlim _lock = new();
    private SyncInferenceModel? _currentModel;
    private SyncInferenceModel? _previousModel;
    private bool _disposed;

    /// <summary>
    /// Returns the currently loaded model, or null if no model has been loaded.
    /// Thread-safe read via reader lock.
    /// </summary>
    /// <returns>The current <see cref="SyncInferenceModel"/>, or null.</returns>
    public SyncInferenceModel? GetCurrentModel()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            return _currentModel;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Deserializes a model from JSON bytes, validates centroid dimensionality, and stores as current.
    /// The previous model is retained for rollback.
    /// </summary>
    /// <param name="serializedModel">JSON-serialized <see cref="SyncInferenceModel"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when centroid dimensionality does not match <see cref="EmbeddingDimensions"/>.</exception>
    /// <exception cref="JsonException">Thrown when deserialization fails.</exception>
    public Task LoadModelAsync(ReadOnlyMemory<byte> serializedModel, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var model = JsonSerializer.Deserialize<SyncInferenceModel>(serializedModel.Span, JsonOptions)
            ?? throw new ArgumentException("Deserialized model was null.", nameof(serializedModel));

        ValidateCentroids(model);

        _lock.EnterWriteLock();
        try
        {
            _previousModel = _currentModel;
            _currentModel = model;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Serializes a model to JSON bytes for transport across edge nodes.
    /// </summary>
    /// <param name="model">The model to serialize.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON bytes representing the model.</returns>
    public Task<ReadOnlyMemory<byte>> SerializeModelAsync(SyncInferenceModel model, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(model);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions);
        return Task.FromResult<ReadOnlyMemory<byte>>(bytes);
    }

    /// <summary>
    /// Replaces the current model with the previous version (if one exists).
    /// Use when a newly loaded model produces poor classification results.
    /// </summary>
    public void RollbackModel()
    {
        ThrowIfDisposed();

        _lock.EnterWriteLock();
        try
        {
            if (_previousModel is not null)
            {
                _currentModel = _previousModel;
                _previousModel = null;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Creates a baseline model with meaningful 8-dimensional centroids that provide reasonable
    /// classification before any training data is available. Each importance level gets a distinct
    /// normalized vector with decreasing magnitudes from Critical to Negligible.
    /// </summary>
    /// <returns>A default <see cref="SyncInferenceModel"/> ready for immediate use.</returns>
    public SyncInferenceModel CreateDefaultModel()
    {
        ThrowIfDisposed();

        // Meaningful initial centroids -- each importance level gets a distinct normalized vector.
        // The pattern creates separation in embedding space so classification works out of the box.
        var centroids = new float[ImportanceLevelCount][];

        // Critical: strong signal across all dimensions, biased toward early dimensions
        centroids[0] = Normalize(new float[] { 0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 0.4f, 0.3f, 0.2f });

        // High: moderate-strong signal, shifted pattern
        centroids[1] = Normalize(new float[] { 0.7f, 0.7f, 0.6f, 0.5f, 0.5f, 0.4f, 0.3f, 0.3f });

        // Normal: balanced moderate signal
        centroids[2] = Normalize(new float[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.4f, 0.4f, 0.3f, 0.3f });

        // Low: weak signal, biased toward later dimensions
        centroids[3] = Normalize(new float[] { 0.3f, 0.3f, 0.3f, 0.4f, 0.4f, 0.5f, 0.5f, 0.4f });

        // Negligible: very weak signal, mostly noise-like
        centroids[4] = Normalize(new float[] { 0.2f, 0.2f, 0.3f, 0.3f, 0.3f, 0.3f, 0.4f, 0.5f });

        var thresholds = new Dictionary<string, double>
        {
            ["critical_min"] = 0.85,
            ["high_min"] = 0.70,
            ["normal_min"] = 0.50,
            ["low_min"] = 0.30,
            ["negligible_max"] = 0.30
        };

        var domainRules = new Dictionary<string, string[]>
        {
            ["healthcare"] = new[] { "phi", "hipaa", "medical" },
            ["financial"] = new[] { "pci", "pii", "transaction" },
            ["iot"] = new[] { "telemetry", "sensor", "device" },
            ["security"] = new[] { "audit", "access", "threat" }
        };

        return new SyncInferenceModel(
            ModelId: "default-edge-v1",
            Version: "1.0.0",
            ImportanceCentroids: centroids,
            ClassificationThresholds: thresholds,
            DomainRules: domainRules,
            TrainedAt: DateTimeOffset.UtcNow,
            TrainingSampleCount: 0);
    }

    /// <summary>
    /// Online centroid update: shifts the importance centroid toward a new embedding by learning rate.
    /// Formula: newCentroid[i] = (1 - learningRate) * old[i] + learningRate * new[i], then normalize.
    /// Returns a new model instance with the updated centroid (models are immutable records).
    /// </summary>
    /// <param name="model">The model to update.</param>
    /// <param name="importance">Which importance level's centroid to shift.</param>
    /// <param name="newEmbedding">The new embedding to learn from.</param>
    /// <param name="learningRate">How much to shift toward the new embedding (0.0 = no change, 1.0 = full replacement).</param>
    /// <returns>A new <see cref="SyncInferenceModel"/> with the updated centroid.</returns>
    /// <exception cref="ArgumentException">Thrown when embedding dimensionality does not match.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when learningRate is not between 0.0 and 1.0.</exception>
    public SyncInferenceModel UpdateCentroids(
        SyncInferenceModel model,
        SemanticImportance importance,
        float[] newEmbedding,
        double learningRate)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(newEmbedding);

        if (newEmbedding.Length != EmbeddingDimensions)
        {
            throw new ArgumentException(
                $"Embedding must have {EmbeddingDimensions} dimensions, but had {newEmbedding.Length}.",
                nameof(newEmbedding));
        }

        if (learningRate < 0.0 || learningRate > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(learningRate), learningRate,
                "Learning rate must be between 0.0 and 1.0.");
        }

        var index = (int)importance;
        var oldCentroid = model.ImportanceCentroids[index];
        var updatedCentroid = new float[EmbeddingDimensions];

        for (int i = 0; i < EmbeddingDimensions; i++)
        {
            updatedCentroid[i] = (float)((1.0 - learningRate) * oldCentroid[i] + learningRate * newEmbedding[i]);
        }

        updatedCentroid = Normalize(updatedCentroid);

        // Clone centroids array and replace the updated one
        var newCentroids = new float[ImportanceLevelCount][];
        for (int i = 0; i < ImportanceLevelCount; i++)
        {
            newCentroids[i] = i == index ? updatedCentroid : model.ImportanceCentroids[i];
        }

        return model with
        {
            ImportanceCentroids = newCentroids,
            TrainingSampleCount = model.TrainingSampleCount + 1
        };
    }

    /// <summary>
    /// Gets the expected embedding dimensionality for centroid vectors.
    /// </summary>
    public static int ExpectedDimensions => EmbeddingDimensions;

    /// <summary>
    /// Validates that all centroids in the model have the expected dimensionality.
    /// </summary>
    private static void ValidateCentroids(SyncInferenceModel model)
    {
        if (model.ImportanceCentroids is null || model.ImportanceCentroids.Length != ImportanceLevelCount)
        {
            throw new ArgumentException(
                $"Model must have exactly {ImportanceLevelCount} importance centroids, " +
                $"but had {model.ImportanceCentroids?.Length ?? 0}.");
        }

        for (int i = 0; i < ImportanceLevelCount; i++)
        {
            if (model.ImportanceCentroids[i] is null || model.ImportanceCentroids[i].Length != EmbeddingDimensions)
            {
                throw new ArgumentException(
                    $"Centroid at index {i} must have {EmbeddingDimensions} dimensions, " +
                    $"but had {model.ImportanceCentroids[i]?.Length ?? 0}.");
            }
        }
    }

    /// <summary>
    /// Normalizes a vector to unit length (L2 norm = 1).
    /// Returns the original array if magnitude is zero (avoids division by zero).
    /// </summary>
    private static float[] Normalize(float[] vector)
    {
        double sumSquared = 0.0;
        for (int i = 0; i < vector.Length; i++)
        {
            sumSquared += (double)vector[i] * vector[i];
        }

        var magnitude = Math.Sqrt(sumSquared);
        if (magnitude < 1e-10)
        {
            return vector;
        }

        var result = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++)
        {
            result[i] = (float)(vector[i] / magnitude);
        }

        return result;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Releases resources used by the model manager, including the reader-writer lock.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
    }
}
