using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.SDK.AI.MlPipeline;

/// <summary>
/// A single feature vector for an entity within a feature set, stored in the
/// VDE Intelligence Cache region.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: ML pipeline feature vector (CE-07)")]
public sealed record FeatureVector
{
    /// <summary>Identifier of the feature set this vector belongs to.</summary>
    public required string FeatureSetId { get; init; }

    /// <summary>Identifier of the entity (row/object) this vector describes.</summary>
    public required string EntityId { get; init; }

    /// <summary>The feature values as a dense float array.</summary>
    public required float[] Values { get; init; }

    /// <summary>When this vector was computed.</summary>
    public DateTimeOffset ComputedAt { get; init; }

    /// <summary>Optional model version that produced this vector.</summary>
    public string? ModelVersion { get; init; }

    /// <summary>Optional metadata key-value pairs.</summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Status of a registered ML model version.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: ML model lifecycle status (CE-07)")]
public enum ModelStatus
{
    /// <summary>Model is currently being trained.</summary>
    Training,

    /// <summary>Model is undergoing validation.</summary>
    Validating,

    /// <summary>Model is the active version serving inference.</summary>
    Active,

    /// <summary>Model has been retired (superseded by newer version).</summary>
    Retired,

    /// <summary>Model training or validation failed.</summary>
    Failed
}

/// <summary>
/// Metadata for a specific version of an ML model, including training lineage
/// and performance metrics.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: ML model version metadata (CE-07)")]
public sealed record ModelVersion
{
    /// <summary>Unique model identifier (e.g., "fraud-detector", "tiering-classifier").</summary>
    public required string ModelId { get; init; }

    /// <summary>Monotonically increasing version number per model.</summary>
    public required int Version { get; init; }

    /// <summary>Algorithm or architecture name (e.g., "XGBoost", "LSTM", "RandomForest").</summary>
    public required string Algorithm { get; init; }

    /// <summary>When this model version completed training.</summary>
    public DateTimeOffset TrainedAt { get; init; }

    /// <summary>SHA-256 hash of the training data for reproducibility.</summary>
    public required string DataLineageHash { get; init; }

    /// <summary>Performance metrics (e.g., accuracy, f1, loss, precision, recall).</summary>
    public Dictionary<string, double> Metrics { get; init; } = new();

    /// <summary>Current lifecycle status of this model version.</summary>
    public ModelStatus Status { get; init; }
}

/// <summary>
/// Defines the schema of a feature set (column names and types).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: ML feature set schema (CE-07)")]
public sealed record FeatureSetDefinition
{
    /// <summary>Unique feature set identifier.</summary>
    public required string FeatureSetId { get; init; }

    /// <summary>Ordered names of features in the vector.</summary>
    public required string[] FeatureNames { get; init; }

    /// <summary>Corresponding types for each feature.</summary>
    public required FeatureType[] FeatureTypes { get; init; }

    /// <summary>Human-readable description of this feature set.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Type classification for individual features within a feature set.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: ML feature type enum (CE-07)")]
public enum FeatureType
{
    /// <summary>Continuous numeric value.</summary>
    Numeric,

    /// <summary>Categorical value (encoded as integer).</summary>
    Categorical,

    /// <summary>Binary flag (0 or 1).</summary>
    Binary,

    /// <summary>Dense embedding vector (float sub-array).</summary>
    Embedding,

    /// <summary>Timestamp feature (encoded as ticks).</summary>
    Timestamp,

    /// <summary>Free-text feature (encoded via hashing or embedding).</summary>
    Text
}

/// <summary>
/// Tracks the provenance of training data extracted from VDE for model training.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: ML training data lineage (CE-07)")]
public sealed record TrainingDataLineage
{
    /// <summary>Unique dataset identifier.</summary>
    public required string DatasetId { get; init; }

    /// <summary>VDE path from which data was extracted.</summary>
    public required string SourceVdePath { get; init; }

    /// <summary>Number of rows/records in the dataset.</summary>
    public long RowCount { get; init; }

    /// <summary>When the data was extracted.</summary>
    public DateTimeOffset ExtractedAt { get; init; }

    /// <summary>Filter criteria applied during extraction.</summary>
    public string FilterCriteria { get; init; } = string.Empty;

    /// <summary>SHA-256 hash of the dataset schema for compatibility checks.</summary>
    public required string SchemaHash { get; init; }
}

/// <summary>
/// Result of a model inference, cached in the Intelligence Cache for re-use.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: ML inference result (CE-07)")]
public sealed record InferenceResult
{
    /// <summary>Model that produced this inference.</summary>
    public required string ModelId { get; init; }

    /// <summary>Entity the inference was made for.</summary>
    public required string EntityId { get; init; }

    /// <summary>Prediction values (class probabilities, regression outputs, etc.).</summary>
    public required float[] Predictions { get; init; }

    /// <summary>Overall confidence score (0.0 to 1.0).</summary>
    public float Confidence { get; init; }

    /// <summary>When the inference was performed.</summary>
    public DateTimeOffset InferredAt { get; init; }

    /// <summary>Wall-clock time taken for the inference.</summary>
    public TimeSpan Latency { get; init; }
}

/// <summary>
/// Statistics snapshot for the feature store.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: ML feature store statistics (CE-07)")]
public sealed record FeatureStoreStats
{
    /// <summary>Total number of stored feature vectors.</summary>
    public long TotalVectors { get; init; }

    /// <summary>Number of distinct feature sets.</summary>
    public int FeatureSets { get; init; }

    /// <summary>Total registered model versions.</summary>
    public int Models { get; init; }

    /// <summary>Number of models in Active status.</summary>
    public int ActiveModels { get; init; }

    /// <summary>Number of cached inference results.</summary>
    public long CachedInferences { get; init; }
}

/// <summary>
/// ML feature store integrated with the VDE Intelligence Cache region.
/// Provides feature vector storage, model versioning with lineage tracking,
/// and inference result caching for ML pipeline workflows.
/// </summary>
/// <remarks>
/// All storage uses <see cref="ConcurrentDictionary{TKey, TValue}"/> internally,
/// thread-safe for concurrent ML pipeline access. Will be backed by VDE region
/// in Phase 87 when ARC cache is available.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 85: VDE-integrated ML feature store (CE-07)")]
public sealed class VdeFeatureStore
{
    private readonly ILogger _logger;

    // Feature vectors keyed by "{FeatureSetId}:{EntityId}"
    private readonly ConcurrentDictionary<string, FeatureVector> _features = new();

    // Model versions keyed by "{ModelId}:{Version}"
    private readonly ConcurrentDictionary<string, ModelVersion> _models = new();

    // Training data lineage keyed by "{ModelId}:{Version}:{DatasetId}"
    private readonly ConcurrentDictionary<string, TrainingDataLineage> _lineage = new();

    // Inference results keyed by "{ModelId}:{EntityId}"
    private readonly ConcurrentDictionary<string, InferenceResult> _inferenceCache = new();

    /// <summary>
    /// Creates a new VDE feature store.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public VdeFeatureStore(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    // ── Feature vector storage ──────────────────────────────────────────

    /// <summary>
    /// Stores a feature vector in the Intelligence Cache region.
    /// </summary>
    public ValueTask StoreFeatureVectorAsync(FeatureVector vector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ct.ThrowIfCancellationRequested();

        var key = $"{vector.FeatureSetId}:{vector.EntityId}";
        _features[key] = vector;

        _logger.LogDebug("Stored feature vector {Key} ({Dims} dimensions).", key, vector.Values.Length);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Retrieves a feature vector by feature set and entity ID.
    /// </summary>
    public ValueTask<FeatureVector?> GetFeatureVectorAsync(
        string featureSetId, string entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var key = $"{featureSetId}:{entityId}";
        _features.TryGetValue(key, out var vector);
        return ValueTask.FromResult(vector);
    }

    /// <summary>
    /// Returns all feature vectors for a given feature set, up to the specified limit.
    /// </summary>
    public ValueTask<IReadOnlyList<FeatureVector>> GetFeatureSetAsync(
        string featureSetId, int limit = 1000, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var prefix = $"{featureSetId}:";
        var results = _features
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kvp => kvp.Value)
            .Take(limit)
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<FeatureVector>>(results);
    }

    // ── Model versioning ────────────────────────────────────────────────

    /// <summary>
    /// Registers a model version. Validates that version is monotonically increasing per model.
    /// </summary>
    public ValueTask RegisterModelAsync(ModelVersion model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ct.ThrowIfCancellationRequested();

        // Check monotonically increasing version
        var existingVersions = _models
            .Where(kvp => kvp.Key.StartsWith($"{model.ModelId}:", StringComparison.Ordinal))
            .Select(kvp => kvp.Value.Version)
            .ToList();

        if (existingVersions.Count > 0 && model.Version <= existingVersions.Max())
        {
            throw new InvalidOperationException(
                $"Model version {model.Version} for '{model.ModelId}' must be greater than existing max {existingVersions.Max()}.");
        }

        var key = $"{model.ModelId}:{model.Version}";
        _models[key] = model;

        _logger.LogInformation(
            "Registered model {ModelId} v{Version} ({Algorithm}, status={Status}).",
            model.ModelId, model.Version, model.Algorithm, model.Status);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns the latest Active model version for the given model ID, or null if none.
    /// </summary>
    public ValueTask<ModelVersion?> GetActiveModelAsync(string modelId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var active = _models
            .Where(kvp => kvp.Key.StartsWith($"{modelId}:", StringComparison.Ordinal)
                          && kvp.Value.Status == ModelStatus.Active)
            .OrderByDescending(kvp => kvp.Value.Version)
            .Select(kvp => kvp.Value)
            .FirstOrDefault();

        return ValueTask.FromResult<ModelVersion?>(active);
    }

    /// <summary>
    /// Returns all model versions for audit/history, ordered by version number.
    /// </summary>
    public ValueTask<IReadOnlyList<ModelVersion>> GetModelHistoryAsync(
        string modelId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var history = _models
            .Where(kvp => kvp.Key.StartsWith($"{modelId}:", StringComparison.Ordinal))
            .Select(kvp => kvp.Value)
            .OrderBy(m => m.Version)
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<ModelVersion>>(history);
    }

    /// <summary>
    /// Promotes a specific model version to Active status, retiring any previously active version.
    /// </summary>
    public ValueTask PromoteModelAsync(string modelId, int version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var key = $"{modelId}:{version}";
        if (!_models.TryGetValue(key, out var model))
            throw new KeyNotFoundException($"Model '{modelId}' version {version} not found.");

        // Retire all currently active versions
        var activeKeys = _models
            .Where(kvp => kvp.Key.StartsWith($"{modelId}:", StringComparison.Ordinal)
                          && kvp.Value.Status == ModelStatus.Active)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var activeKey in activeKeys)
        {
            if (_models.TryGetValue(activeKey, out var activeModel))
            {
                _models[activeKey] = activeModel with { Status = ModelStatus.Retired };
            }
        }

        // Promote the target version
        _models[key] = model with { Status = ModelStatus.Active };

        _logger.LogInformation(
            "Promoted model {ModelId} v{Version} to Active. Retired {Count} previous version(s).",
            modelId, version, activeKeys.Count);

        return ValueTask.CompletedTask;
    }

    // ── Training data lineage ───────────────────────────────────────────

    /// <summary>
    /// Records the lineage of training data used for a model version.
    /// </summary>
    public ValueTask RecordLineageAsync(TrainingDataLineage lineage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ct.ThrowIfCancellationRequested();

        // Lineage is associated with a model version via DataLineageHash in ModelVersion
        // Store by DatasetId for lookup
        var key = lineage.DatasetId;
        _lineage[key] = lineage;

        _logger.LogDebug(
            "Recorded lineage for dataset {DatasetId} ({RowCount} rows from {Path}).",
            lineage.DatasetId, lineage.RowCount, lineage.SourceVdePath);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns all training data lineage records for a given model version,
    /// matched by the model's <see cref="ModelVersion.DataLineageHash"/>.
    /// </summary>
    public ValueTask<IReadOnlyList<TrainingDataLineage>> GetLineageForModelAsync(
        string modelId, int version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var modelKey = $"{modelId}:{version}";
        if (!_models.TryGetValue(modelKey, out var model))
            return ValueTask.FromResult<IReadOnlyList<TrainingDataLineage>>(Array.Empty<TrainingDataLineage>());

        // Match lineage by schema hash against model's data lineage hash
        var lineageRecords = _lineage.Values
            .Where(l => l.SchemaHash == model.DataLineageHash)
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<TrainingDataLineage>>(lineageRecords);
    }

    // ── Inference caching ───────────────────────────────────────────────

    /// <summary>
    /// Caches an inference result in the Intelligence Cache for fast re-use.
    /// </summary>
    public ValueTask CacheInferenceResultAsync(InferenceResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ct.ThrowIfCancellationRequested();

        var key = $"{result.ModelId}:{result.EntityId}";
        _inferenceCache[key] = result;

        _logger.LogDebug(
            "Cached inference for {ModelId}:{EntityId} (confidence={Confidence:F3}, latency={Latency}ms).",
            result.ModelId, result.EntityId, result.Confidence, result.Latency.TotalMilliseconds);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Retrieves a cached inference result for a model and entity.
    /// </summary>
    public ValueTask<InferenceResult?> GetCachedInferenceAsync(
        string modelId, string entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var key = $"{modelId}:{entityId}";
        _inferenceCache.TryGetValue(key, out var result);
        return ValueTask.FromResult(result);
    }

    // ── Statistics ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns a snapshot of feature store statistics.
    /// </summary>
    public FeatureStoreStats GetStats()
    {
        var featureSets = _features.Keys
            .Select(k => k.Substring(0, k.IndexOf(':', StringComparison.Ordinal)))
            .Distinct()
            .Count();

        var modelIds = _models.Values.Select(m => m.ModelId).Distinct().ToList();
        var activeModels = _models.Values.Count(m => m.Status == ModelStatus.Active);

        return new FeatureStoreStats
        {
            TotalVectors = _features.Count,
            FeatureSets = featureSets,
            Models = modelIds.Count,
            ActiveModels = activeModels,
            CachedInferences = _inferenceCache.Count
        };
    }
}
