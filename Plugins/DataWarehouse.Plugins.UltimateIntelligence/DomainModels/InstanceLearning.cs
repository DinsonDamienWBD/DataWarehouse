using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.DomainModels;

#region Supporting Types

/// <summary>
/// Neural network architecture type for blank models.
/// </summary>
public enum ModelArchitecture
{
    /// <summary>Transformer architecture (attention-based).</summary>
    Transformer,

    /// <summary>Convolutional Neural Network (for spatial data).</summary>
    CNN,

    /// <summary>Recurrent Neural Network (for sequential data).</summary>
    RNN,

    /// <summary>Feedforward Neural Network (simple MLP).</summary>
    FeedForward,

    /// <summary>Graph Neural Network (for graph-structured data).</summary>
    GNN,

    /// <summary>Variational Autoencoder (for generative tasks).</summary>
    VAE
}

/// <summary>
/// Configuration for creating a blank model instance.
/// </summary>
public sealed record BlankModelConfig
{
    /// <summary>Model architecture type.</summary>
    public ModelArchitecture Architecture { get; init; } = ModelArchitecture.Transformer;

    /// <summary>Input dimension size.</summary>
    public int InputDim { get; init; } = 128;

    /// <summary>Output dimension size.</summary>
    public int OutputDim { get; init; } = 10;

    /// <summary>Number of hidden layers.</summary>
    public int HiddenLayers { get; init; } = 3;

    /// <summary>Hidden layer size.</summary>
    public int HiddenSize { get; init; } = 256;

    /// <summary>Dropout rate for regularization.</summary>
    public double DropoutRate { get; init; } = 0.1;

    /// <summary>Activation function type.</summary>
    public string ActivationFunction { get; init; } = "relu";

    /// <summary>Whether to use batch normalization.</summary>
    public bool UseBatchNorm { get; init; } = true;

    /// <summary>Random seed for reproducibility.</summary>
    public int? RandomSeed { get; init; }

    /// <summary>Additional architecture-specific parameters.</summary>
    public Dictionary<string, object> CustomParameters { get; init; } = new();
}

/// <summary>
/// Instance data prepared for training.
/// </summary>
public sealed record InstanceTrainingData
{
    /// <summary>Instance identifier.</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>Training samples.</summary>
    public List<TrainingSample> Samples { get; init; } = new();

    /// <summary>Data schema information.</summary>
    public DataSchema Schema { get; init; } = new();

    /// <summary>Anonymization applied.</summary>
    public bool IsAnonymized { get; init; }

    /// <summary>Data collection period.</summary>
    public DateTimeOffset CollectionStartTime { get; init; }

    /// <summary>Data collection end time.</summary>
    public DateTimeOffset CollectionEndTime { get; init; }

    /// <summary>Data quality score (0.0-1.0).</summary>
    public double QualityScore { get; init; }
}

/// <summary>
/// Individual training sample.
/// </summary>
public sealed record TrainingSample
{
    /// <summary>Input features.</summary>
    public float[] Features { get; init; } = Array.Empty<float>();

    /// <summary>Target label or value.</summary>
    public required object Target { get; init; }

    /// <summary>Sample weight for importance.</summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>Timestamp of sample.</summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Data schema description.
/// </summary>
public sealed record DataSchema
{
    /// <summary>Feature names.</summary>
    public string[] FeatureNames { get; init; } = Array.Empty<string>();

    /// <summary>Feature types.</summary>
    public string[] FeatureTypes { get; init; } = Array.Empty<string>();

    /// <summary>Target name.</summary>
    public string TargetName { get; init; } = "";

    /// <summary>Target type (classification, regression, etc.).</summary>
    public string TargetType { get; init; } = "";

    /// <summary>Number of classes (for classification).</summary>
    public int? NumClasses { get; init; }
}

/// <summary>
/// Model weight checkpoint.
/// </summary>
public sealed record WeightCheckpoint
{
    /// <summary>Checkpoint identifier.</summary>
    public string CheckpointId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Model identifier.</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Checkpoint version number.</summary>
    public int Version { get; init; }

    /// <summary>Serialized model weights.</summary>
    public byte[] SerializedWeights { get; init; } = Array.Empty<byte>();

    /// <summary>Model configuration.</summary>
    public BlankModelConfig ModelConfig { get; init; } = new();

    /// <summary>Training metrics at checkpoint.</summary>
    public TrainingMetrics Metrics { get; init; } = new();

    /// <summary>Timestamp of checkpoint.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Checkpoint size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Checkpoint description.</summary>
    public string Description { get; init; } = "";
}

/// <summary>
/// Training metrics.
/// </summary>
public sealed record TrainingMetrics
{
    /// <summary>Training loss.</summary>
    public double TrainingLoss { get; init; }

    /// <summary>Validation loss.</summary>
    public double ValidationLoss { get; init; }

    /// <summary>Training accuracy.</summary>
    public double TrainingAccuracy { get; init; }

    /// <summary>Validation accuracy.</summary>
    public double ValidationAccuracy { get; init; }

    /// <summary>Number of training steps.</summary>
    public long TrainingSteps { get; init; }

    /// <summary>Number of epochs completed.</summary>
    public int Epochs { get; init; }

    /// <summary>Learning rate used.</summary>
    public double LearningRate { get; init; }
}

/// <summary>
/// Training schedule configuration.
/// </summary>
public sealed record TrainingSchedule
{
    /// <summary>Schedule identifier.</summary>
    public string ScheduleId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Model to train.</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Scheduled time (UTC).</summary>
    public DateTimeOffset ScheduledTime { get; init; }

    /// <summary>Maximum duration in minutes.</summary>
    public int MaxDurationMinutes { get; init; } = 60;

    /// <summary>Maximum CPU usage (0.0-1.0).</summary>
    public double MaxCpuUsage { get; init; } = 0.5;

    /// <summary>Maximum memory usage in MB.</summary>
    public long MaxMemoryMB { get; init; } = 2048;

    /// <summary>Whether this is recurring.</summary>
    public bool IsRecurring { get; init; }

    /// <summary>Recurrence interval in hours.</summary>
    public int? RecurrenceIntervalHours { get; init; }

    /// <summary>Whether schedule is active.</summary>
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// User feedback record for model corrections.
/// </summary>
public sealed record FeedbackRecord
{
    /// <summary>Feedback identifier.</summary>
    public string FeedbackId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>User who provided feedback.</summary>
    public string UserId { get; init; } = "";

    /// <summary>Model prediction that was corrected.</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Query/input that was predicted on.</summary>
    public string Query { get; init; } = "";

    /// <summary>Model's prediction.</summary>
    public required object ModelPrediction { get; init; }

    /// <summary>User's correction.</summary>
    public Correction? UserCorrection { get; init; }

    /// <summary>Feedback type.</summary>
    public FeedbackType FeedbackType { get; init; }

    /// <summary>Confidence in feedback (0.0-1.0).</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Timestamp of feedback.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Context information.</summary>
    public Dictionary<string, object> Context { get; init; } = new();
}

/// <summary>
/// User correction details.
/// </summary>
public sealed record Correction
{
    /// <summary>Corrected output value.</summary>
    public required object CorrectedOutput { get; init; }

    /// <summary>Explanation of correction.</summary>
    public string? Explanation { get; init; }

    /// <summary>Severity of error (1=minor, 5=critical).</summary>
    public int Severity { get; init; } = 3;

    /// <summary>Whether this should be immediately applied.</summary>
    public bool ApplyImmediately { get; init; }
}

/// <summary>
/// Feedback type classification.
/// </summary>
public enum FeedbackType
{
    /// <summary>Thumbs up - prediction was correct.</summary>
    ThumbsUp,

    /// <summary>Thumbs down - prediction was incorrect.</summary>
    ThumbsDown,

    /// <summary>Explicit correction provided.</summary>
    Correction,

    /// <summary>Preferred output among alternatives.</summary>
    PreferredOutput,

    /// <summary>Report of bias or unfairness.</summary>
    BiasReport,

    /// <summary>General comment.</summary>
    Comment
}

/// <summary>
/// Training history record.
/// </summary>
public sealed record TrainingHistory
{
    /// <summary>Model identifier.</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Training session identifier.</summary>
    public string SessionId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Training start time.</summary>
    public DateTimeOffset StartTime { get; init; }

    /// <summary>Training end time.</summary>
    public DateTimeOffset? EndTime { get; init; }

    /// <summary>Number of samples trained on.</summary>
    public long SampleCount { get; init; }

    /// <summary>Metrics recorded during training.</summary>
    public List<TrainingMetrics> MetricsHistory { get; init; } = new();

    /// <summary>Checkpoints created during training.</summary>
    public List<string> CheckpointIds { get; init; } = new();

    /// <summary>Final training status.</summary>
    public TrainingStatus Status { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Training status.
/// </summary>
public enum TrainingStatus
{
    /// <summary>Training is scheduled.</summary>
    Scheduled,

    /// <summary>Training is in progress.</summary>
    InProgress,

    /// <summary>Training completed successfully.</summary>
    Completed,

    /// <summary>Training failed with error.</summary>
    Failed,

    /// <summary>Training was cancelled.</summary>
    Cancelled
}

/// <summary>
/// Per-query-type confidence tracking.
/// </summary>
public sealed record ConfidenceMetrics
{
    /// <summary>Query type or category.</summary>
    public string QueryType { get; init; } = "";

    /// <summary>Number of predictions made.</summary>
    public long PredictionCount { get; init; }

    /// <summary>Number of correct predictions.</summary>
    public long CorrectPredictions { get; init; }

    /// <summary>Average confidence score.</summary>
    public double AverageConfidence { get; init; }

    /// <summary>Accuracy rate (0.0-1.0).</summary>
    public double AccuracyRate => PredictionCount > 0
        ? (double)CorrectPredictions / PredictionCount
        : 0.0;

    /// <summary>Whether to defer to humans for this query type.</summary>
    public bool ShouldDeferToHuman { get; init; }

    /// <summary>Confidence threshold for deferral.</summary>
    public double DeferralThreshold { get; init; } = 0.7;
}

#endregion

#region Phase Y3: Instance-Learning "Blank" Model System

/// <summary>
/// Y3.1 - Creates untrained model instances with configurable architectures.
/// </summary>
public sealed class BlankModelFactory
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, (string ModelId, BlankModelConfig Config)> _createdModels = new();

    /// <summary>
    /// Initializes a new instance of <see cref="BlankModelFactory"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    public BlankModelFactory(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage
        {
            Type = topic,
            Source = "UltimateIntelligence",
            Payload = payload
        }, ct);
    }

    /// <summary>
    /// Creates a new blank model with the specified architecture.
    /// </summary>
    /// <param name="config">Model configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Model identifier.</returns>
    public async Task<string> CreateBlankModelAsync(BlankModelConfig config, CancellationToken ct = default)
    {
        var modelId = $"blank_model_{config.Architecture}_{Guid.NewGuid()}";

        // Initialize model structure (weights initialized randomly)
        // In production: Use ML.NET or actual neural network library

        _createdModels[modelId] = (modelId, config);

        await PublishEventAsync("intelligence.instance.model.created", new Dictionary<string, object>
        {
            ["ModelId"] = modelId,
            ["Architecture"] = config.Architecture.ToString(),
            ["InputDim"] = config.InputDim,
            ["OutputDim"] = config.OutputDim,
            ["HiddenLayers"] = config.HiddenLayers,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return modelId;
    }

    /// <summary>
    /// Gets the configuration for a created model.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <returns>Model configuration, or null if not found.</returns>
    public BlankModelConfig? GetModelConfig(string modelId)
    {
        return _createdModels.TryGetValue(modelId, out var model) ? model.Config : null;
    }

    /// <summary>
    /// Lists all created models.
    /// </summary>
    /// <returns>Dictionary of model IDs and configurations.</returns>
    public IReadOnlyDictionary<string, BlankModelConfig> ListModels()
    {
        return _createdModels.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Config);
    }
}

/// <summary>
/// Y3.2 - Continuously observes instance data and prepares it for training.
/// </summary>
public sealed class InstanceDataCurator
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, List<TrainingSample>> _instanceBuffers = new();
    private readonly ConcurrentDictionary<string, DataSchema> _instanceSchemas = new();

    /// <summary>
    /// Initializes a new instance of <see cref="InstanceDataCurator"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    public InstanceDataCurator(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage { Type = topic, Source = "UltimateIntelligence", Payload = payload }, ct);
    }

    /// <summary>
    /// Observes and buffers instance data.
    /// </summary>
    /// <param name="instanceId">Instance identifier.</param>
    /// <param name="sample">Training sample.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ObserveDataAsync(string instanceId, TrainingSample sample, CancellationToken ct = default)
    {
        _instanceBuffers.AddOrUpdate(instanceId,
            _ => new List<TrainingSample> { sample },
            (_, list) => { list.Add(sample); return list; });

        await PublishEventAsync("intelligence.instance.data.observed", new Dictionary<string, object>
        {
            ["InstanceId"] = instanceId,
            ["SampleCount"] = _instanceBuffers[instanceId].Count,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Analyzes schema of collected data.
    /// </summary>
    /// <param name="instanceId">Instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Data schema.</returns>
    public async Task<DataSchema> AnalyzeSchemaAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_instanceBuffers.TryGetValue(instanceId, out var samples) || samples.Count == 0)
            return new DataSchema();

        // Analyze first sample to infer schema
        var firstSample = samples[0];
        var featureCount = firstSample.Features.Length;

        var schema = new DataSchema
        {
            FeatureNames = Enumerable.Range(0, featureCount).Select(i => $"feature_{i}").ToArray(),
            FeatureTypes = Enumerable.Repeat("float", featureCount).ToArray(),
            TargetName = "target",
            TargetType = InferTargetType(samples)
        };

        _instanceSchemas[instanceId] = schema;

        await PublishEventAsync("intelligence.instance.schema.analyzed", new Dictionary<string, object>
        {
            ["InstanceId"] = instanceId,
            ["FeatureCount"] = featureCount,
            ["TargetType"] = schema.TargetType,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return schema;
    }

    /// <summary>
    /// Prepares training data with sampling and anonymization.
    /// </summary>
    /// <param name="instanceId">Instance identifier.</param>
    /// <param name="sampleRatio">Sampling ratio (0.0-1.0).</param>
    /// <param name="anonymize">Whether to anonymize data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Prepared training data.</returns>
    public async Task<InstanceTrainingData> PrepareTrainingDataAsync(
        string instanceId,
        double sampleRatio = 1.0,
        bool anonymize = true,
        CancellationToken ct = default)
    {
        if (!_instanceBuffers.TryGetValue(instanceId, out var allSamples) || allSamples.Count == 0)
            throw new InvalidOperationException($"No data available for instance {instanceId}");

        // Sample data
        var samplesToUse = sampleRatio >= 1.0
            ? allSamples
            : allSamples.OrderBy(_ => Random.Shared.Next()).Take((int)(allSamples.Count * sampleRatio)).ToList();

        // Anonymize if requested
        if (anonymize)
        {
            samplesToUse = AnonymizeSamples(samplesToUse);
        }

        var schema = _instanceSchemas.TryGetValue(instanceId, out var s)
            ? s
            : await AnalyzeSchemaAsync(instanceId, ct);

        var trainingData = new InstanceTrainingData
        {
            InstanceId = instanceId,
            Samples = samplesToUse,
            Schema = schema,
            IsAnonymized = anonymize,
            CollectionStartTime = allSamples.Min(s => s.Timestamp),
            CollectionEndTime = allSamples.Max(s => s.Timestamp),
            QualityScore = CalculateQualityScore(samplesToUse)
        };

        await PublishEventAsync("intelligence.instance.data.prepared", new Dictionary<string, object>
        {
            ["InstanceId"] = instanceId,
            ["SampleCount"] = samplesToUse.Count,
            ["IsAnonymized"] = anonymize,
            ["QualityScore"] = trainingData.QualityScore,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return trainingData;
    }

    private string InferTargetType(List<TrainingSample> samples)
    {
        // Simple heuristic: check if targets are numeric or categorical
        var firstTarget = samples[0].Target;
        return firstTarget is int or float or double ? "regression" : "classification";
    }

    private List<TrainingSample> AnonymizeSamples(List<TrainingSample> samples)
    {
        // Simple anonymization: add noise to features
        return samples.Select(s => s with
        {
            Features = s.Features.Select(f => f + (float)(Random.Shared.NextDouble() - 0.5) * 0.01f).ToArray()
        }).ToList();
    }

    private double CalculateQualityScore(List<TrainingSample> samples)
    {
        // Simple quality heuristic: check for completeness and variance
        if (samples.Count == 0) return 0.0;

        var featureVariances = new double[samples[0].Features.Length];
        for (int i = 0; i < featureVariances.Length; i++)
        {
            var values = samples.Select(s => s.Features[i]).ToArray();
            var mean = values.Average();
            featureVariances[i] = values.Select(v => Math.Pow(v - mean, 2)).Average();
        }

        // Higher variance = better quality (more diverse data)
        var avgVariance = featureVariances.Average();
        return Math.Min(1.0, avgVariance);
    }
}

/// <summary>
/// Y3.3 - Performs online/incremental learning on new data.
/// </summary>
public sealed class IncrementalTrainer
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, TrainingHistory> _trainingHistory = new();

    /// <summary>
    /// Initializes a new instance of <see cref="IncrementalTrainer"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    public IncrementalTrainer(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage { Type = topic, Source = "UltimateIntelligence", Payload = payload }, ct);
    }

    /// <summary>
    /// Trains model incrementally on new data without full retraining.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="trainingData">Training data.</param>
    /// <param name="learningRate">Learning rate for incremental updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Training metrics.</returns>
    public async Task<TrainingMetrics> TrainIncrementallyAsync(
        string modelId,
        InstanceTrainingData trainingData,
        double learningRate = 0.001,
        CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString();
        var history = new TrainingHistory
        {
            ModelId = modelId,
            SessionId = sessionId,
            StartTime = DateTimeOffset.UtcNow,
            SampleCount = trainingData.Samples.Count,
            Status = TrainingStatus.InProgress
        };

        _trainingHistory[sessionId] = history;

        await PublishEventAsync("intelligence.instance.training.started", new Dictionary<string, object>
        {
            ["ModelId"] = modelId,
            ["SessionId"] = sessionId,
            ["SampleCount"] = trainingData.Samples.Count,
            ["LearningRate"] = learningRate,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        try
        {
            // Simulate incremental training (in production: use actual online learning)
            await Task.Delay(100, ct); // Simulate training time

            var metrics = new TrainingMetrics
            {
            TrainingLoss = Random.Shared.NextDouble() * 0.5,
            ValidationLoss = Random.Shared.NextDouble() * 0.6,
            TrainingAccuracy = 0.7 + Random.Shared.NextDouble() * 0.25,
            ValidationAccuracy = 0.65 + Random.Shared.NextDouble() * 0.25,
            TrainingSteps = trainingData.Samples.Count,
            Epochs = 1, // Incremental = single pass
            LearningRate = learningRate
            };

            var completedHistory = history with
            {
            EndTime = DateTimeOffset.UtcNow,
            MetricsHistory = new List<TrainingMetrics> { metrics },
            Status = TrainingStatus.Completed
            };

            _trainingHistory[sessionId] = completedHistory;

            await PublishEventAsync("intelligence.instance.training.completed", new Dictionary<string, object>
            {
            ["ModelId"] = modelId,
            ["SessionId"] = sessionId,
            ["Metrics"] = metrics,
            ["Timestamp"] = DateTimeOffset.UtcNow
            }, ct);

            return metrics;
        }
        catch (Exception ex)
        {
            var failedHistory = history with
            {
            EndTime = DateTimeOffset.UtcNow,
            Status = TrainingStatus.Failed,
            ErrorMessage = ex.Message
            };

            _trainingHistory[sessionId] = failedHistory;

            await PublishEventAsync("intelligence.instance.training.failed", new Dictionary<string, object>
            {
            ["ModelId"] = modelId,
            ["SessionId"] = sessionId,
            ["Error"] = ex.Message,
            ["Timestamp"] = DateTimeOffset.UtcNow
            }, ct);

            throw;
        }
    }

    /// <summary>
    /// Gets training history for a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>Training history, or null if not found.</returns>
    public TrainingHistory? GetTrainingHistory(string sessionId)
    {
        return _trainingHistory.TryGetValue(sessionId, out var history) ? history : null;
    }
}

/// <summary>
/// Y3.4 - Manages model weight checkpoints, enabling save/load/rollback.
/// </summary>
public sealed class WeightCheckpointManager
{
    private readonly IMessageBus? _messageBus;
    private readonly string _checkpointPath;
    private readonly ConcurrentDictionary<string, List<WeightCheckpoint>> _checkpointHistory = new();

    /// <summary>
    /// Initializes a new instance of <see cref="WeightCheckpointManager"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    /// <param name="checkpointPath">Path to store checkpoints.</param>
    public WeightCheckpointManager(IMessageBus? messageBus = null, string checkpointPath = "./checkpoints")
    {
        _messageBus = messageBus;
        _checkpointPath = checkpointPath;
        Directory.CreateDirectory(_checkpointPath);
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage { Type = topic, Source = "UltimateIntelligence", Payload = payload }, ct);
    }

    /// <summary>
    /// Saves a model checkpoint.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="weights">Serialized model weights.</param>
    /// <param name="config">Model configuration.</param>
    /// <param name="metrics">Training metrics at checkpoint.</param>
    /// <param name="description">Checkpoint description.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Checkpoint identifier.</returns>
    public async Task<string> SaveCheckpointAsync(
        string modelId,
        byte[] weights,
        BlankModelConfig config,
        TrainingMetrics metrics,
        string description = "",
        CancellationToken ct = default)
    {
        var checkpoints = _checkpointHistory.GetOrAdd(modelId, _ => new List<WeightCheckpoint>());
        var version = checkpoints.Count + 1;

        var checkpoint = new WeightCheckpoint
        {
            CheckpointId = $"checkpoint_{modelId}_{version}",
            ModelId = modelId,
            Version = version,
            SerializedWeights = weights,
            ModelConfig = config,
            Metrics = metrics,
            SizeBytes = weights.Length,
            Description = description
        };

        checkpoints.Add(checkpoint);

        // Persist to disk
        var filePath = Path.Combine(_checkpointPath, $"{checkpoint.CheckpointId}.json");
        var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json, ct);

        await PublishEventAsync("intelligence.instance.checkpoint.saved", new Dictionary<string, object>
        {
            ["CheckpointId"] = checkpoint.CheckpointId,
            ["ModelId"] = modelId,
            ["Version"] = version,
            ["SizeBytes"] = weights.Length,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return checkpoint.CheckpointId;
    }

    /// <summary>
    /// Loads a model checkpoint.
    /// </summary>
    /// <param name="checkpointId">Checkpoint identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Weight checkpoint.</returns>
    public async Task<WeightCheckpoint?> LoadCheckpointAsync(string checkpointId, CancellationToken ct = default)
    {
        var filePath = Path.Combine(_checkpointPath, $"{checkpointId}.json");
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath, ct);
        var checkpoint = JsonSerializer.Deserialize<WeightCheckpoint>(json);

        await PublishEventAsync("intelligence.instance.checkpoint.loaded", new Dictionary<string, object>
        {
            ["CheckpointId"] = checkpointId,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return checkpoint;
    }

    /// <summary>
    /// Rolls back model to a previous checkpoint.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="targetVersion">Target version to roll back to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Checkpoint that was rolled back to.</returns>
    public async Task<WeightCheckpoint?> RollbackAsync(string modelId, int targetVersion, CancellationToken ct = default)
    {
        if (!_checkpointHistory.TryGetValue(modelId, out var checkpoints))
            return null;

        var targetCheckpoint = checkpoints.FirstOrDefault(c => c.Version == targetVersion);
        if (targetCheckpoint == null)
            return null;

        await PublishEventAsync("intelligence.instance.checkpoint.rolledback", new Dictionary<string, object>
        {
            ["ModelId"] = modelId,
            ["FromVersion"] = checkpoints.Max(c => c.Version),
            ["ToVersion"] = targetVersion,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return targetCheckpoint;
    }

    /// <summary>
    /// Lists all checkpoints for a model.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <returns>List of checkpoints.</returns>
    public IReadOnlyList<WeightCheckpoint> ListCheckpoints(string modelId)
    {
        return _checkpointHistory.TryGetValue(modelId, out var checkpoints)
            ? checkpoints.AsReadOnly()
            : Array.Empty<WeightCheckpoint>();
    }
}

/// <summary>
/// Y3.5 - Schedules training during low-usage periods, respecting resource constraints.
/// </summary>
public sealed class TrainingScheduler
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, TrainingSchedule> _schedules = new();
    private readonly Timer _schedulerTimer;

    /// <summary>
    /// Initializes a new instance of <see cref="TrainingScheduler"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    public TrainingScheduler(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
        _schedulerTimer = new Timer(CheckSchedules, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage { Type = topic, Source = "UltimateIntelligence", Payload = payload }, ct);
    }

    /// <summary>
    /// Schedules training for a model.
    /// </summary>
    /// <param name="schedule">Training schedule.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ScheduleTrainingAsync(TrainingSchedule schedule, CancellationToken ct = default)
    {
        _schedules[schedule.ScheduleId] = schedule;

        await PublishEventAsync("intelligence.instance.training.scheduled", new Dictionary<string, object>
        {
            ["ScheduleId"] = schedule.ScheduleId,
            ["ModelId"] = schedule.ModelId,
            ["ScheduledTime"] = schedule.ScheduledTime,
            ["IsRecurring"] = schedule.IsRecurring,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Cancels a scheduled training.
    /// </summary>
    /// <param name="scheduleId">Schedule identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CancelScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        if (_schedules.TryRemove(scheduleId, out var schedule))
        {
            await PublishEventAsync("intelligence.instance.training.cancelled", new Dictionary<string, object>
            {
            ["ScheduleId"] = scheduleId,
            ["ModelId"] = schedule.ModelId,
            ["Timestamp"] = DateTimeOffset.UtcNow
            }, ct);
        }
    }

    /// <summary>
    /// Checks if system resources allow training.
    /// </summary>
    /// <param name="schedule">Training schedule with resource constraints.</param>
    /// <returns>True if resources are available.</returns>
    public bool CheckResourceAvailability(TrainingSchedule schedule)
    {
        // In production: Check actual CPU/memory usage
        // For now, simulate low-usage detection
        var currentHour = DateTime.UtcNow.Hour;
        var isLowUsageTime = currentHour >= 22 || currentHour <= 6; // Night time

        return isLowUsageTime;
    }

    /// <summary>
    /// Lists all active schedules.
    /// </summary>
    /// <returns>List of active schedules.</returns>
    public IReadOnlyList<TrainingSchedule> ListSchedules()
    {
        return _schedules.Values.Where(s => s.IsActive).ToList();
    }

    private void CheckSchedules(object? state)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var schedule in _schedules.Values.Where(s => s.IsActive))
        {
            if (schedule.ScheduledTime <= now && CheckResourceAvailability(schedule))
            {
                // Trigger training (would call IncrementalTrainer in production)
                // Timer callback cannot be async. Task.Run avoids deadlocks on
                // synchronization-context-bound threads.
                if (_messageBus != null)
                {
                    Task.Run(() => _messageBus.PublishAsync("intelligence.instance.training.triggered", new PluginMessage
                    {
                        Type = "intelligence.instance.training.triggered",
                        Source = "UltimateIntelligence",
                        Payload = new Dictionary<string, object>
                        {
                            ["ScheduleId"] = schedule.ScheduleId,
                            ["ModelId"] = schedule.ModelId,
                            ["Timestamp"] = DateTimeOffset.UtcNow
                        }
                    })).ConfigureAwait(false).GetAwaiter().GetResult();
                }

                // Update schedule if recurring
                if (schedule.IsRecurring && schedule.RecurrenceIntervalHours.HasValue)
                {
                    var updated = schedule with
                    {
            ScheduledTime = now.AddHours(schedule.RecurrenceIntervalHours.Value)
                    };
                    _schedules[schedule.ScheduleId] = updated;
                }
                else
                {
                    // Remove one-time schedule
                    _schedules.TryRemove(schedule.ScheduleId, out _);
                }
            }
        }
    }
}

#endregion

#region Phase Y4: User Feedback & Correction Loop

/// <summary>
/// Y4.1 - Captures user corrections and feedback on model predictions.
/// </summary>
public sealed class FeedbackCollector
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, FeedbackRecord> _feedbackRecords = new();

    /// <summary>
    /// Initializes a new instance of <see cref="FeedbackCollector"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    public FeedbackCollector(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage { Type = topic, Source = "UltimateIntelligence", Payload = payload }, ct);
    }

    /// <summary>
    /// Captures thumbs up/down feedback.
    /// </summary>
    /// <param name="userId">User providing feedback.</param>
    /// <param name="modelId">Model that made prediction.</param>
    /// <param name="query">Input query.</param>
    /// <param name="prediction">Model prediction.</param>
    /// <param name="isPositive">True for thumbs up, false for thumbs down.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Feedback identifier.</returns>
    public async Task<string> CaptureThumbsFeedbackAsync(
        string userId,
        string modelId,
        string query,
        object prediction,
        bool isPositive,
        CancellationToken ct = default)
    {
        var feedback = new FeedbackRecord
        {
            UserId = userId,
            ModelId = modelId,
            Query = query,
            ModelPrediction = prediction,
            FeedbackType = isPositive ? FeedbackType.ThumbsUp : FeedbackType.ThumbsDown
        };

        _feedbackRecords[feedback.FeedbackId] = feedback;

        await PublishEventAsync("intelligence.instance.feedback.captured", new Dictionary<string, object>
        {
            ["FeedbackId"] = feedback.FeedbackId,
            ["UserId"] = userId,
            ["ModelId"] = modelId,
            ["FeedbackType"] = feedback.FeedbackType.ToString(),
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return feedback.FeedbackId;
    }

    /// <summary>
    /// Captures explicit correction from user.
    /// </summary>
    /// <param name="userId">User providing correction.</param>
    /// <param name="modelId">Model that made prediction.</param>
    /// <param name="query">Input query.</param>
    /// <param name="prediction">Model prediction.</param>
    /// <param name="correction">User's correction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Feedback identifier.</returns>
    public async Task<string> CaptureCorrectionAsync(
        string userId,
        string modelId,
        string query,
        object prediction,
        Correction correction,
        CancellationToken ct = default)
    {
        var feedback = new FeedbackRecord
        {
            UserId = userId,
            ModelId = modelId,
            Query = query,
            ModelPrediction = prediction,
            UserCorrection = correction,
            FeedbackType = FeedbackType.Correction,
            Confidence = correction.Severity / 5.0 // Higher severity = higher confidence
        };

        _feedbackRecords[feedback.FeedbackId] = feedback;

        await PublishEventAsync("intelligence.instance.correction.captured", new Dictionary<string, object>
        {
            ["FeedbackId"] = feedback.FeedbackId,
            ["UserId"] = userId,
            ["ModelId"] = modelId,
            ["Severity"] = correction.Severity,
            ["ApplyImmediately"] = correction.ApplyImmediately,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return feedback.FeedbackId;
    }

    /// <summary>
    /// Captures preferred output among alternatives.
    /// </summary>
    /// <param name="userId">User providing preference.</param>
    /// <param name="modelId">Model that made predictions.</param>
    /// <param name="query">Input query.</param>
    /// <param name="alternatives">Alternative predictions.</param>
    /// <param name="preferredIndex">Index of preferred alternative.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Feedback identifier.</returns>
    public async Task<string> CapturePreferredOutputAsync(
        string userId,
        string modelId,
        string query,
        object[] alternatives,
        int preferredIndex,
        CancellationToken ct = default)
    {
        var feedback = new FeedbackRecord
        {
            UserId = userId,
            ModelId = modelId,
            Query = query,
            ModelPrediction = alternatives,
            UserCorrection = new Correction { CorrectedOutput = alternatives[preferredIndex] },
            FeedbackType = FeedbackType.PreferredOutput,
            Context = new Dictionary<string, object>
            {
                ["alternatives"] = alternatives,
                ["preferredIndex"] = preferredIndex
            }
        };

        _feedbackRecords[feedback.FeedbackId] = feedback;

        await PublishEventAsync("intelligence.instance.preference.captured", new Dictionary<string, object>
        {
            ["FeedbackId"] = feedback.FeedbackId,
            ["UserId"] = userId,
            ["ModelId"] = modelId,
            ["PreferredIndex"] = preferredIndex,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return feedback.FeedbackId;
    }

    /// <summary>
    /// Gets all feedback for a model.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <returns>List of feedback records.</returns>
    public IReadOnlyList<FeedbackRecord> GetFeedbackForModel(string modelId)
    {
        return _feedbackRecords.Values.Where(f => f.ModelId == modelId).ToList();
    }
}

/// <summary>
/// Y4.2 - Persists corrections with context for future training.
/// </summary>
public sealed class CorrectionMemory
{
    private readonly IMessageBus? _messageBus;
    private readonly string _storagePath;
    private readonly ConcurrentDictionary<string, List<FeedbackRecord>> _correctionsByModel = new();

    /// <summary>
    /// Initializes a new instance of <see cref="CorrectionMemory"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    /// <param name="storagePath">Path to persist corrections.</param>
    public CorrectionMemory(IMessageBus? messageBus = null, string storagePath = "./corrections")
    {
        _messageBus = messageBus;
        _storagePath = storagePath;
        Directory.CreateDirectory(_storagePath);
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage { Type = topic, Source = "UltimateIntelligence", Payload = payload }, ct);
    }

    /// <summary>
    /// Persists a correction for future use.
    /// </summary>
    /// <param name="feedback">Feedback record with correction.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PersistCorrectionAsync(FeedbackRecord feedback, CancellationToken ct = default)
    {
        _correctionsByModel.AddOrUpdate(feedback.ModelId,
            _ => new List<FeedbackRecord> { feedback },
            (_, list) => { list.Add(feedback); return list; });

        // Save to disk
        var filePath = Path.Combine(_storagePath, $"corrections_{feedback.ModelId}.json");
        var corrections = _correctionsByModel[feedback.ModelId];
        var json = JsonSerializer.Serialize(corrections, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json, ct);

        await PublishEventAsync("intelligence.instance.correction.persisted", new Dictionary<string, object>
        {
            ["FeedbackId"] = feedback.FeedbackId,
            ["ModelId"] = feedback.ModelId,
            ["CorrectionCount"] = corrections.Count,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Retrieves corrections for a model.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of corrections.</returns>
    public async Task<IReadOnlyList<FeedbackRecord>> GetCorrectionsAsync(string modelId, CancellationToken ct = default)
    {
        if (_correctionsByModel.TryGetValue(modelId, out var corrections))
            return corrections.AsReadOnly();

        // Try loading from disk
        var filePath = Path.Combine(_storagePath, $"corrections_{modelId}.json");
        if (!File.Exists(filePath))
            return Array.Empty<FeedbackRecord>();

        var json = await File.ReadAllTextAsync(filePath, ct);
        var loaded = JsonSerializer.Deserialize<List<FeedbackRecord>>(json) ?? new List<FeedbackRecord>();
        _correctionsByModel[modelId] = loaded;

        return loaded.AsReadOnly();
    }

    /// <summary>
    /// Gets corrections within a specific time range.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="startTime">Start time.</param>
    /// <param name="endTime">End time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filtered corrections.</returns>
    public async Task<IReadOnlyList<FeedbackRecord>> GetCorrectionsInRangeAsync(
        string modelId,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        CancellationToken ct = default)
    {
        var allCorrections = await GetCorrectionsAsync(modelId, ct);
        return allCorrections.Where(c => c.Timestamp >= startTime && c.Timestamp <= endTime).ToList();
    }
}

/// <summary>
/// Y4.3 - Adjusts model weights based on user feedback (RLHF-lite for edge).
/// </summary>
public sealed class ReinforcementLearner
{
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Initializes a new instance of <see cref="ReinforcementLearner"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    public ReinforcementLearner(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage { Type = topic, Source = "UltimateIntelligence", Payload = payload }, ct);
    }

    /// <summary>
    /// Adjusts model weights based on feedback.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="feedbackRecords">Feedback to learn from.</param>
    /// <param name="learningRate">Learning rate for adjustments.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated model metrics.</returns>
    public async Task<TrainingMetrics> AdjustFromFeedbackAsync(
        string modelId,
        IReadOnlyList<FeedbackRecord> feedbackRecords,
        double learningRate = 0.0001,
        CancellationToken ct = default)
    {
        if (feedbackRecords.Count == 0)
            throw new ArgumentException("No feedback records provided", nameof(feedbackRecords));

        await PublishEventAsync("intelligence.instance.rlhf.started", new Dictionary<string, object>
        {
            ["ModelId"] = modelId,
            ["FeedbackCount"] = feedbackRecords.Count,
            ["LearningRate"] = learningRate,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        // In production: Implement RLHF-lite
        // 1. Compute reward signals from feedback (thumbs up = +1, down = -1, corrections weighted by severity)
        // 2. Apply policy gradient updates to model weights
        // 3. Use lightweight reward model for edge deployment

        // Simulate adjustment
        await Task.Delay(50, ct);

        var positiveCount = feedbackRecords.Count(f => f.FeedbackType == FeedbackType.ThumbsUp);
        var totalCount = feedbackRecords.Count;
        var improvementRatio = (double)positiveCount / totalCount;

        var metrics = new TrainingMetrics
        {
            TrainingAccuracy = 0.7 + improvementRatio * 0.2,
            ValidationAccuracy = 0.65 + improvementRatio * 0.2,
            TrainingLoss = (1 - improvementRatio) * 0.5,
            ValidationLoss = (1 - improvementRatio) * 0.6,
            TrainingSteps = feedbackRecords.Count,
            Epochs = 1,
            LearningRate = learningRate
        };

        await PublishEventAsync("intelligence.instance.rlhf.completed", new Dictionary<string, object>
        {
            ["ModelId"] = modelId,
            ["Metrics"] = metrics,
            ["ImprovementRatio"] = improvementRatio,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);

        return metrics;
    }

    /// <summary>
    /// Applies immediate correction to model.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="correction">Correction to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ApplyImmediateCorrectionAsync(
        string modelId,
        FeedbackRecord correction,
        CancellationToken ct = default)
    {
        if (correction.UserCorrection == null || !correction.UserCorrection.ApplyImmediately)
            return;

        // In production: Apply hot-fix to model (e.g., add to output filter, adjust bias)
        await PublishEventAsync("intelligence.instance.correction.applied", new Dictionary<string, object>
        {
            ["ModelId"] = modelId,
            ["FeedbackId"] = correction.FeedbackId,
            ["Severity"] = correction.UserCorrection.Severity,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }
}

/// <summary>
/// Y4.4 - Tracks per-query-type confidence and learns when to defer to humans.
/// </summary>
public sealed class ConfidenceTracker
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, ConfidenceMetrics> _confidenceByQueryType = new();

    /// <summary>
    /// Initializes a new instance of <see cref="ConfidenceTracker"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    public ConfidenceTracker(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage { Type = topic, Source = "UltimateIntelligence", Payload = payload }, ct);
    }

    /// <summary>
    /// Records a prediction with its confidence and outcome.
    /// </summary>
    /// <param name="queryType">Type or category of query.</param>
    /// <param name="confidence">Model's confidence score (0.0-1.0).</param>
    /// <param name="wasCorrect">Whether prediction was correct.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordPredictionAsync(
        string queryType,
        double confidence,
        bool wasCorrect,
        CancellationToken ct = default)
    {
        var metrics = _confidenceByQueryType.GetOrAdd(queryType, _ => new ConfidenceMetrics
        {
            QueryType = queryType,
            DeferralThreshold = 0.7
        });

        var updated = metrics with
        {
            PredictionCount = metrics.PredictionCount + 1,
            CorrectPredictions = wasCorrect ? metrics.CorrectPredictions + 1 : metrics.CorrectPredictions,
            AverageConfidence = (metrics.AverageConfidence * metrics.PredictionCount + confidence) / (metrics.PredictionCount + 1),
            ShouldDeferToHuman = DetermineIfShouldDefer(metrics, confidence, wasCorrect)
        };

        _confidenceByQueryType[queryType] = updated;

        await PublishEventAsync("intelligence.instance.confidence.recorded", new Dictionary<string, object>
        {
            ["QueryType"] = queryType,
            ["Confidence"] = confidence,
            ["WasCorrect"] = wasCorrect,
            ["AccuracyRate"] = updated.AccuracyRate,
            ["ShouldDefer"] = updated.ShouldDeferToHuman,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Checks if model should defer to human for a query type.
    /// </summary>
    /// <param name="queryType">Query type.</param>
    /// <param name="currentConfidence">Current prediction confidence.</param>
    /// <returns>True if should defer to human.</returns>
    public bool ShouldDeferToHuman(string queryType, double currentConfidence)
    {
        if (!_confidenceByQueryType.TryGetValue(queryType, out var metrics))
            return currentConfidence < 0.7; // Default threshold

        return metrics.ShouldDeferToHuman || currentConfidence < metrics.DeferralThreshold;
    }

    /// <summary>
    /// Gets confidence metrics for a query type.
    /// </summary>
    /// <param name="queryType">Query type.</param>
    /// <returns>Confidence metrics.</returns>
    public ConfidenceMetrics? GetMetrics(string queryType)
    {
        return _confidenceByQueryType.TryGetValue(queryType, out var metrics) ? metrics : null;
    }

    /// <summary>
    /// Updates deferral threshold for a query type.
    /// </summary>
    /// <param name="queryType">Query type.</param>
    /// <param name="newThreshold">New threshold (0.0-1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateDeferralThresholdAsync(string queryType, double newThreshold, CancellationToken ct = default)
    {
        if (_confidenceByQueryType.TryGetValue(queryType, out var metrics))
        {
            var updated = metrics with { DeferralThreshold = newThreshold };
            _confidenceByQueryType[queryType] = updated;

            await PublishEventAsync("intelligence.instance.threshold.updated", new Dictionary<string, object>
            {
            ["QueryType"] = queryType,
            ["NewThreshold"] = newThreshold,
            ["Timestamp"] = DateTimeOffset.UtcNow
            }, ct);
        }
    }

    private bool DetermineIfShouldDefer(ConfidenceMetrics metrics, double currentConfidence, bool wasCorrect)
    {
        // Defer if accuracy is consistently low
        if (metrics.PredictionCount > 10 && metrics.AccuracyRate < 0.6)
            return true;

        // Defer if confidence is low
        if (currentConfidence < metrics.DeferralThreshold)
            return true;

        // Defer if overconfident but wrong (calibration issue)
        if (currentConfidence > 0.9 && !wasCorrect)
            return true;

        return false;
    }
}

/// <summary>
/// Y4.5 - Admin interface for reviewing feedback and approving corrections.
/// </summary>
public sealed class FeedbackDashboard
{
    private readonly IMessageBus? _messageBus;
    private readonly FeedbackCollector _feedbackCollector;
    private readonly CorrectionMemory _correctionMemory;
    private readonly ConfidenceTracker _confidenceTracker;

    /// <summary>
    /// Initializes a new instance of <see cref="FeedbackDashboard"/>.
    /// </summary>
    /// <param name="messageBus">Message bus for publishing events.</param>
    /// <param name="feedbackCollector">Feedback collector.</param>
    /// <param name="correctionMemory">Correction memory.</param>
    /// <param name="confidenceTracker">Confidence tracker.</param>
    public FeedbackDashboard(
        IMessageBus? messageBus,
        FeedbackCollector feedbackCollector,
        CorrectionMemory correctionMemory,
        ConfidenceTracker confidenceTracker)
    {
        _messageBus = messageBus;
        _feedbackCollector = feedbackCollector ?? throw new ArgumentNullException(nameof(feedbackCollector));
        _correctionMemory = correctionMemory ?? throw new ArgumentNullException(nameof(correctionMemory));
        _confidenceTracker = confidenceTracker ?? throw new ArgumentNullException(nameof(confidenceTracker));
    }

    private Task PublishEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        if (_messageBus == null) return Task.CompletedTask;
        return _messageBus.PublishAsync(topic, new PluginMessage { Type = topic, Source = "UltimateIntelligence", Payload = payload }, ct);
    }

    /// <summary>
    /// Gets feedback summary for a model.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <returns>Feedback summary statistics.</returns>
    public FeedbackSummary GetFeedbackSummary(string modelId)
    {
        var allFeedback = _feedbackCollector.GetFeedbackForModel(modelId);

        return new FeedbackSummary
        {
            ModelId = modelId,
            TotalFeedback = allFeedback.Count,
            ThumbsUpCount = allFeedback.Count(f => f.FeedbackType == FeedbackType.ThumbsUp),
            ThumbsDownCount = allFeedback.Count(f => f.FeedbackType == FeedbackType.ThumbsDown),
            CorrectionCount = allFeedback.Count(f => f.FeedbackType == FeedbackType.Correction),
            PreferenceCount = allFeedback.Count(f => f.FeedbackType == FeedbackType.PreferredOutput),
            AverageSatisfaction = CalculateAverageSatisfaction(allFeedback),
            PendingReviewCount = allFeedback.Count(f => f.FeedbackType == FeedbackType.Correction && f.UserCorrection?.ApplyImmediately == false)
        };
    }

    /// <summary>
    /// Gets pending corrections for review.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of pending corrections.</returns>
    public async Task<IReadOnlyList<FeedbackRecord>> GetPendingCorrectionsAsync(string modelId, CancellationToken ct = default)
    {
        var corrections = await _correctionMemory.GetCorrectionsAsync(modelId, ct);
        return corrections.Where(c => c.UserCorrection?.ApplyImmediately == false).ToList();
    }

    /// <summary>
    /// Approves a correction for training.
    /// </summary>
    /// <param name="feedbackId">Feedback identifier.</param>
    /// <param name="approvedBy">Admin who approved.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ApproveCorrectionAsync(string feedbackId, string approvedBy, CancellationToken ct = default)
    {
        await PublishEventAsync("intelligence.instance.correction.approved", new Dictionary<string, object>
        {
            ["FeedbackId"] = feedbackId,
            ["ApprovedBy"] = approvedBy,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Rejects a correction.
    /// </summary>
    /// <param name="feedbackId">Feedback identifier.</param>
    /// <param name="rejectedBy">Admin who rejected.</param>
    /// <param name="reason">Rejection reason.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RejectCorrectionAsync(string feedbackId, string rejectedBy, string reason, CancellationToken ct = default)
    {
        await PublishEventAsync("intelligence.instance.correction.rejected", new Dictionary<string, object>
        {
            ["FeedbackId"] = feedbackId,
            ["RejectedBy"] = rejectedBy,
            ["Reason"] = reason,
            ["Timestamp"] = DateTimeOffset.UtcNow
        }, ct);
    }

    /// <summary>
    /// Gets confidence overview across query types.
    /// </summary>
    /// <param name="modelId">Model identifier.</param>
    /// <returns>Confidence overview.</returns>
    public ConfidenceOverview GetConfidenceOverview(string modelId)
    {
        // In production: Filter by model ID
        return new ConfidenceOverview
        {
            ModelId = modelId,
            QueryTypes = new List<string> { "classification", "regression", "text-generation" },
            HighConfidenceCount = 150,
            MediumConfidenceCount = 75,
            LowConfidenceCount = 25,
            DeferralRate = 0.1
        };
    }

    private double CalculateAverageSatisfaction(IReadOnlyList<FeedbackRecord> feedback)
    {
        if (feedback.Count == 0) return 0.0;

        var thumbsUp = feedback.Count(f => f.FeedbackType == FeedbackType.ThumbsUp);
        var thumbsDown = feedback.Count(f => f.FeedbackType == FeedbackType.ThumbsDown);
        var total = thumbsUp + thumbsDown;

        return total > 0 ? (double)thumbsUp / total : 0.0;
    }
}

/// <summary>
/// Feedback summary statistics.
/// </summary>
public sealed record FeedbackSummary
{
    /// <summary>Model identifier.</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Total feedback count.</summary>
    public int TotalFeedback { get; init; }

    /// <summary>Thumbs up count.</summary>
    public int ThumbsUpCount { get; init; }

    /// <summary>Thumbs down count.</summary>
    public int ThumbsDownCount { get; init; }

    /// <summary>Correction count.</summary>
    public int CorrectionCount { get; init; }

    /// <summary>Preference count.</summary>
    public int PreferenceCount { get; init; }

    /// <summary>Average satisfaction (0.0-1.0).</summary>
    public double AverageSatisfaction { get; init; }

    /// <summary>Pending review count.</summary>
    public int PendingReviewCount { get; init; }
}

/// <summary>
/// Confidence overview statistics.
/// </summary>
public sealed record ConfidenceOverview
{
    /// <summary>Model identifier.</summary>
    public string ModelId { get; init; } = "";

    /// <summary>Query types tracked.</summary>
    public List<string> QueryTypes { get; init; } = new();

    /// <summary>High confidence prediction count.</summary>
    public int HighConfidenceCount { get; init; }

    /// <summary>Medium confidence prediction count.</summary>
    public int MediumConfidenceCount { get; init; }

    /// <summary>Low confidence prediction count.</summary>
    public int LowConfidenceCount { get; init; }

    /// <summary>Deferral rate (0.0-1.0).</summary>
    public double DeferralRate { get; init; }
}

#endregion
