using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Defines the category of AI-enhanced data management strategy.
/// </summary>
public enum AiEnhancedCategory
{
    /// <summary>
    /// Data orchestration and placement optimization.
    /// </summary>
    Orchestration,

    /// <summary>
    /// Deduplication and similarity detection.
    /// </summary>
    Deduplication,

    /// <summary>
    /// Lifecycle and importance prediction.
    /// </summary>
    Lifecycle,

    /// <summary>
    /// Autonomous data organization.
    /// </summary>
    SelfOrganizing,

    /// <summary>
    /// Intent and goal-based management.
    /// </summary>
    IntentBased,

    /// <summary>
    /// Compliance and regulatory automation.
    /// </summary>
    Compliance,

    /// <summary>
    /// Cost optimization and awareness.
    /// </summary>
    CostOptimization,

    /// <summary>
    /// Environmental sustainability.
    /// </summary>
    Sustainability
}

/// <summary>
/// Result of an AI-enhanced operation.
/// </summary>
public sealed class AiOperationResult
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Whether AI was used for this operation.
    /// </summary>
    public bool UsedAi { get; init; }

    /// <summary>
    /// Whether fallback logic was used.
    /// </summary>
    public bool UsedFallback { get; init; }

    /// <summary>
    /// Confidence score if AI was used (0.0-1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Operation duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Additional result data.
    /// </summary>
    public Dictionary<string, object>? Data { get; init; }

    /// <summary>
    /// Creates a successful result with AI.
    /// </summary>
    public static AiOperationResult OkWithAi(double confidence, TimeSpan duration, Dictionary<string, object>? data = null) =>
        new() { Success = true, UsedAi = true, UsedFallback = false, Confidence = confidence, Duration = duration, Data = data };

    /// <summary>
    /// Creates a successful result with fallback.
    /// </summary>
    public static AiOperationResult OkWithFallback(TimeSpan duration, Dictionary<string, object>? data = null) =>
        new() { Success = true, UsedAi = false, UsedFallback = true, Confidence = 0.0, Duration = duration, Data = data };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static AiOperationResult Failed(string error, TimeSpan duration) =>
        new() { Success = false, ErrorMessage = error, Duration = duration };
}

/// <summary>
/// Statistics for AI-enhanced operations.
/// </summary>
public sealed class AiEnhancedStatistics
{
    /// <summary>
    /// Total operations performed.
    /// </summary>
    public long TotalOperations { get; set; }

    /// <summary>
    /// Operations that used AI successfully.
    /// </summary>
    public long AiOperations { get; set; }

    /// <summary>
    /// Operations that used fallback logic.
    /// </summary>
    public long FallbackOperations { get; set; }

    /// <summary>
    /// Failed operations.
    /// </summary>
    public long FailedOperations { get; set; }

    /// <summary>
    /// Average confidence score for AI operations.
    /// </summary>
    public double AverageConfidence { get; set; }

    /// <summary>
    /// Average operation time in milliseconds.
    /// </summary>
    public double AverageTimeMs { get; set; }

    /// <summary>
    /// Total time spent on operations.
    /// </summary>
    public double TotalTimeMs { get; set; }
}

/// <summary>
/// Interface for AI-enhanced data management strategies.
/// </summary>
public interface IAiEnhancedStrategy : IDataManagementStrategy
{
    /// <summary>
    /// Gets the AI-enhanced category.
    /// </summary>
    AiEnhancedCategory AiCategory { get; }

    /// <summary>
    /// Gets whether AI is currently available.
    /// </summary>
    bool IsAiAvailable { get; }

    /// <summary>
    /// Gets the required Intelligence capabilities.
    /// </summary>
    IntelligenceCapabilities RequiredCapabilities { get; }

    /// <summary>
    /// Gets statistics for AI-enhanced operations.
    /// </summary>
    AiEnhancedStatistics GetAiStatistics();
}

/// <summary>
/// Abstract base class for AI-enhanced data management strategies.
/// Provides integration with Universal Intelligence (T90) via message bus
/// with automatic graceful degradation when AI is unavailable.
/// </summary>
/// <remarks>
/// Features:
/// - Automatic T90 discovery and capability checking
/// - Graceful fallback when AI unavailable
/// - Statistics tracking for AI vs fallback usage
/// - Thread-safe operation
/// - Configurable AI context
/// </remarks>
public abstract class AiEnhancedStrategyBase : DataManagementStrategyBase, IAiEnhancedStrategy
{
    private readonly object _statsLock = new();
    private readonly AiEnhancedStatistics _aiStats = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageResponse>> _pendingRequests = new();
    private readonly List<IDisposable> _subscriptions = new();

    private volatile bool _intelligenceAvailable;
    private IntelligenceCapabilities _availableCapabilities = IntelligenceCapabilities.None;
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets or sets the message bus for AI communication.
    /// </summary>
    protected IMessageBus? MessageBus { get; set; }

    /// <summary>
    /// Gets or sets the default Intelligence context.
    /// </summary>
    protected IntelligenceContext DefaultContext { get; set; } = IntelligenceContext.Default;

    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Lifecycle;

    /// <inheritdoc/>
    public abstract AiEnhancedCategory AiCategory { get; }

    /// <inheritdoc/>
    public bool IsAiAvailable => _intelligenceAvailable && HasRequiredCapabilities();

    /// <inheritdoc/>
    public abstract IntelligenceCapabilities RequiredCapabilities { get; }

    /// <summary>
    /// Checks if all required capabilities are available.
    /// </summary>
    protected bool HasRequiredCapabilities()
    {
        return RequiredCapabilities == IntelligenceCapabilities.None ||
               (_availableCapabilities & RequiredCapabilities) == RequiredCapabilities;
    }

    /// <summary>
    /// Checks if a specific capability is available.
    /// </summary>
    protected bool HasCapability(IntelligenceCapabilities capability)
    {
        return _intelligenceAvailable && (_availableCapabilities & capability) == capability;
    }

    /// <inheritdoc/>
    public AiEnhancedStatistics GetAiStatistics()
    {
        lock (_statsLock)
        {
            return new AiEnhancedStatistics
            {
                TotalOperations = _aiStats.TotalOperations,
                AiOperations = _aiStats.AiOperations,
                FallbackOperations = _aiStats.FallbackOperations,
                FailedOperations = _aiStats.FailedOperations,
                AverageConfidence = _aiStats.AverageConfidence,
                AverageTimeMs = _aiStats.AverageTimeMs,
                TotalTimeMs = _aiStats.TotalTimeMs
            };
        }
    }

    /// <summary>
    /// Records an AI-enhanced operation.
    /// </summary>
    protected void RecordAiOperation(bool usedAi, bool failed, double confidence, double timeMs)
    {
        lock (_statsLock)
        {
            _aiStats.TotalOperations++;
            _aiStats.TotalTimeMs += timeMs;

            if (failed)
            {
                _aiStats.FailedOperations++;
            }
            else if (usedAi)
            {
                _aiStats.AiOperations++;
                // Update running average
                var totalAi = _aiStats.AiOperations;
                _aiStats.AverageConfidence = ((_aiStats.AverageConfidence * (totalAi - 1)) + confidence) / totalAi;
            }
            else
            {
                _aiStats.FallbackOperations++;
            }

            _aiStats.AverageTimeMs = _aiStats.TotalTimeMs / _aiStats.TotalOperations;
        }
    }

    /// <inheritdoc/>
    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        await DiscoverIntelligenceAsync(ct);
        SubscribeToIntelligenceTopics();
        await InitializeAiCoreAsync(ct);
    }

    /// <summary>
    /// Subclass-specific initialization after AI discovery.
    /// </summary>
    protected virtual Task InitializeAiCoreAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); } catch { }
        }
        _subscriptions.Clear();

        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Discovers Universal Intelligence availability.
    /// </summary>
    protected async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _lastDiscovery < DiscoveryCacheTtl)
        {
            return _intelligenceAvailable;
        }

        if (MessageBus == null)
        {
            UpdateIntelligenceState(false, IntelligenceCapabilities.None);
            return false;
        }

        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<MessageResponse>();
            _pendingRequests[correlationId] = tcs;

            try
            {
                var request = new PluginMessage
                {
                    Type = "intelligence.discover.request",
                    CorrelationId = correlationId,
                    Source = StrategyId,
                    Payload = new Dictionary<string, object>
                    {
                        ["requestorId"] = StrategyId,
                        ["requiredCapabilities"] = (long)RequiredCapabilities,
                        ["timestamp"] = DateTimeOffset.UtcNow
                    }
                };

                await MessageBus.PublishAsync(IntelligenceTopics.Discover, request, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(DiscoveryTimeout);

                try
                {
                    var response = await tcs.Task.WaitAsync(cts.Token);

                    if (response.Success && response.Payload is Dictionary<string, object> payload)
                    {
                        var capabilities = IntelligenceCapabilities.None;
                        if (payload.TryGetValue("capabilities", out var capObj))
                        {
                            if (capObj is IntelligenceCapabilities caps)
                                capabilities = caps;
                            else if (capObj is long longVal)
                                capabilities = (IntelligenceCapabilities)longVal;
                            else if (long.TryParse(capObj?.ToString(), out var parsed))
                                capabilities = (IntelligenceCapabilities)parsed;
                        }

                        UpdateIntelligenceState(true, capabilities);
                        return true;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout
                }
            }
            finally
            {
                _pendingRequests.TryRemove(correlationId, out _);
            }

            UpdateIntelligenceState(false, IntelligenceCapabilities.None);
            return false;
        }
        catch
        {
            UpdateIntelligenceState(false, IntelligenceCapabilities.None);
            return false;
        }
    }

    private void UpdateIntelligenceState(bool available, IntelligenceCapabilities capabilities)
    {
        _intelligenceAvailable = available;
        _availableCapabilities = capabilities;
        _lastDiscovery = DateTimeOffset.UtcNow;
    }

    private void SubscribeToIntelligenceTopics()
    {
        if (MessageBus == null) return;

        try
        {
            var availableSub = MessageBus.Subscribe(IntelligenceTopics.Available, msg =>
            {
                var capabilities = IntelligenceCapabilities.None;
                if (msg.Payload.TryGetValue("capabilities", out var capObj))
                {
                    if (capObj is IntelligenceCapabilities caps)
                        capabilities = caps;
                    else if (capObj is long longVal)
                        capabilities = (IntelligenceCapabilities)longVal;
                }
                UpdateIntelligenceState(true, capabilities);
                return Task.CompletedTask;
            });
            _subscriptions.Add(availableSub);

            var unavailableSub = MessageBus.Subscribe(IntelligenceTopics.Unavailable, _ =>
            {
                UpdateIntelligenceState(false, IntelligenceCapabilities.None);
                return Task.CompletedTask;
            });
            _subscriptions.Add(unavailableSub);

            var discoverResponseSub = MessageBus.Subscribe(IntelligenceTopics.DiscoverResponse, msg =>
            {
                if (msg.CorrelationId != null && _pendingRequests.TryRemove(msg.CorrelationId, out var tcs))
                {
                    tcs.TrySetResult(new MessageResponse { Success = true, Payload = msg.Payload });
                }
                return Task.CompletedTask;
            });
            _subscriptions.Add(discoverResponseSub);
        }
        catch
        {
            // Graceful degradation
        }
    }

    /// <summary>
    /// Sends an AI request and waits for response.
    /// </summary>
    protected async Task<MessageResponse?> SendAiRequestAsync(
        string topic,
        Dictionary<string, object> payload,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (!_intelligenceAvailable || MessageBus == null)
            return null;

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<MessageResponse>();
        _pendingRequests[correlationId] = tcs;

        try
        {
            var responseTopic = $"{topic}.response";
            IDisposable? responseSub = null;

            try
            {
                responseSub = MessageBus.Subscribe(responseTopic, msg =>
                {
                    if (msg.CorrelationId == correlationId && _pendingRequests.TryRemove(correlationId, out var pendingTcs))
                    {
                        pendingTcs.TrySetResult(new MessageResponse
                        {
                            Success = msg.Payload.TryGetValue("success", out var s) && s is true,
                            Payload = msg.Payload
                        });
                    }
                    return Task.CompletedTask;
                });

                var request = new PluginMessage
                {
                    Type = $"{topic}.request",
                    CorrelationId = correlationId,
                    Source = StrategyId,
                    Payload = payload
                };

                await MessageBus.PublishAsync(topic, request, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

                return await tcs.Task.WaitAsync(cts.Token);
            }
            finally
            {
                responseSub?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Requests embeddings from Intelligence.
    /// </summary>
    protected async Task<float[][]?> RequestEmbeddingsAsync(
        string[] texts,
        IntelligenceContext? context = null,
        CancellationToken ct = default)
    {
        if (!HasCapability(IntelligenceCapabilities.Embeddings))
            return null;

        var payload = new Dictionary<string, object>
        {
            ["texts"] = texts,
            ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
        };

        if (context?.PreferredModel != null)
            payload["model"] = context.PreferredModel;

        var response = await SendAiRequestAsync(IntelligenceTopics.RequestEmbeddings, payload, context?.Timeout, ct);

        if (response?.Success == true && response.Payload is Dictionary<string, object> result)
        {
            if (result.TryGetValue("embeddings", out var embObj) && embObj is float[][] embeddings)
                return embeddings;
        }

        return null;
    }

    /// <summary>
    /// Requests classification from Intelligence.
    /// </summary>
    protected async Task<(string Category, double Confidence)[]?> RequestClassificationAsync(
        string text,
        string[] categories,
        bool multiLabel = false,
        IntelligenceContext? context = null,
        CancellationToken ct = default)
    {
        if (!HasCapability(IntelligenceCapabilities.Classification))
            return null;

        var payload = new Dictionary<string, object>
        {
            ["text"] = text,
            ["categories"] = categories,
            ["multiLabel"] = multiLabel,
            ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
        };

        var response = await SendAiRequestAsync(IntelligenceTopics.RequestClassification, payload, context?.Timeout, ct);

        if (response?.Success == true && response.Payload is Dictionary<string, object> result)
        {
            if (result.TryGetValue("classifications", out var classObj) && classObj is object[] classifications)
            {
                return classifications
                    .OfType<Dictionary<string, object>>()
                    .Select(c => (
                        Category: c.TryGetValue("category", out var cat) ? cat?.ToString() ?? "" : "",
                        Confidence: c.TryGetValue("confidence", out var conf) && conf is double d ? d : 0.0
                    ))
                    .ToArray();
            }
        }

        return null;
    }

    /// <summary>
    /// Requests prediction from Intelligence.
    /// </summary>
    protected async Task<(object? Prediction, double Confidence, Dictionary<string, object> Metadata)?> RequestPredictionAsync(
        string predictionType,
        Dictionary<string, object> inputData,
        IntelligenceContext? context = null,
        CancellationToken ct = default)
    {
        if (!HasCapability(IntelligenceCapabilities.Prediction))
            return null;

        var payload = new Dictionary<string, object>
        {
            ["predictionType"] = predictionType,
            ["inputData"] = inputData,
            ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
        };

        var response = await SendAiRequestAsync(IntelligenceTopics.RequestPrediction, payload, context?.Timeout, ct);

        if (response?.Success == true && response.Payload is Dictionary<string, object> result)
        {
            return (
                result.TryGetValue("prediction", out var p) ? p : null,
                result.TryGetValue("confidence", out var c) && c is double conf ? conf : 0.0,
                result.TryGetValue("metadata", out var m) && m is Dictionary<string, object> meta ? meta : new Dictionary<string, object>()
            );
        }

        return null;
    }

    /// <summary>
    /// Requests PII detection from Intelligence.
    /// </summary>
    protected async Task<(bool ContainsPII, (string Type, string Value, double Confidence)[] Items)?> RequestPIIDetectionAsync(
        string text,
        IntelligenceContext? context = null,
        CancellationToken ct = default)
    {
        if (!HasCapability(IntelligenceCapabilities.PIIDetection))
            return null;

        var payload = new Dictionary<string, object>
        {
            ["text"] = text,
            ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
        };

        var response = await SendAiRequestAsync(IntelligenceTopics.RequestPIIDetection, payload, context?.Timeout, ct);

        if (response?.Success == true && response.Payload is Dictionary<string, object> result)
        {
            var containsPii = result.TryGetValue("containsPII", out var c) && c is true;
            var items = Array.Empty<(string, string, double)>();

            if (result.TryGetValue("piiItems", out var itemsObj) && itemsObj is object[] piiItems)
            {
                items = piiItems
                    .OfType<Dictionary<string, object>>()
                    .Select(i => (
                        Type: i.TryGetValue("type", out var t) ? t?.ToString() ?? "" : "",
                        Value: i.TryGetValue("value", out var v) ? v?.ToString() ?? "" : "",
                        Confidence: i.TryGetValue("confidence", out var conf) && conf is double d ? d : 0.0
                    ))
                    .ToArray();
            }

            return (containsPii, items);
        }

        return null;
    }

    /// <summary>
    /// Requests entity extraction from Intelligence.
    /// </summary>
    protected async Task<(string Text, string Type, double Confidence)[]?> RequestEntityExtractionAsync(
        string text,
        string[]? entityTypes = null,
        IntelligenceContext? context = null,
        CancellationToken ct = default)
    {
        if (!HasCapability(IntelligenceCapabilities.EntityExtraction))
            return null;

        var payload = new Dictionary<string, object>
        {
            ["text"] = text,
            ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
        };

        if (entityTypes != null)
            payload["entityTypes"] = entityTypes;

        var response = await SendAiRequestAsync(IntelligenceTopics.RequestEntityExtraction, payload, context?.Timeout, ct);

        if (response?.Success == true && response.Payload is Dictionary<string, object> result)
        {
            if (result.TryGetValue("entities", out var entObj) && entObj is object[] entities)
            {
                return entities
                    .OfType<Dictionary<string, object>>()
                    .Select(e => (
                        Text: e.TryGetValue("text", out var t) ? t?.ToString() ?? "" : "",
                        Type: e.TryGetValue("type", out var tp) ? tp?.ToString() ?? "" : "",
                        Confidence: e.TryGetValue("confidence", out var conf) && conf is double d ? d : 0.0
                    ))
                    .ToArray();
            }
        }

        return null;
    }

    /// <summary>
    /// Requests text completion from Intelligence.
    /// </summary>
    protected async Task<string?> RequestCompletionAsync(
        string prompt,
        string? systemMessage = null,
        IntelligenceContext? context = null,
        CancellationToken ct = default)
    {
        if (!HasCapability(IntelligenceCapabilities.TextCompletion))
            return null;

        var payload = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["contextId"] = context?.ContextId ?? Guid.NewGuid().ToString("N")
        };

        if (systemMessage != null)
            payload["systemMessage"] = systemMessage;
        if (context?.MaxTokens != null)
            payload["maxTokens"] = context.MaxTokens.Value;
        if (context?.Temperature != null)
            payload["temperature"] = context.Temperature.Value;

        var response = await SendAiRequestAsync(IntelligenceTopics.RequestCompletion, payload, context?.Timeout, ct);

        if (response?.Success == true && response.Payload is Dictionary<string, object> result)
        {
            if (result.TryGetValue("content", out var content) && content is string text)
                return text;
        }

        return null;
    }

    /// <summary>
    /// Requests semantic similarity scoring.
    /// </summary>
    protected async Task<double?> RequestSimilarityScoreAsync(
        float[] embedding1,
        float[] embedding2,
        CancellationToken ct = default)
    {
        if (!HasCapability(IntelligenceCapabilities.SimilarityScoring))
            return null;

        var payload = new Dictionary<string, object>
        {
            ["embedding1"] = embedding1,
            ["embedding2"] = embedding2
        };

        var response = await SendAiRequestAsync("intelligence.request.similarity", payload, null, ct);

        if (response?.Success == true && response.Payload is Dictionary<string, object> result)
        {
            if (result.TryGetValue("score", out var score) && score is double s)
                return s;
        }

        return null;
    }

    /// <summary>
    /// Calculates cosine similarity locally as fallback.
    /// </summary>
    protected static double CalculateCosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0.0;

        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
            return 0.0;

        return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
