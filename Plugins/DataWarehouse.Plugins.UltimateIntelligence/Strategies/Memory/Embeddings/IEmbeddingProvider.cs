using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// Provides embedding generation capabilities for text-to-vector conversion.
/// Implementations support various AI providers including OpenAI, Azure, Cohere,
/// HuggingFace, Ollama, ONNX, VoyageAI, and Jina.
/// </summary>
public interface IEmbeddingProvider : IDisposable
{
    /// <summary>
    /// Gets the unique identifier for this provider (e.g., "openai", "cohere", "ollama").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets the human-readable display name for this provider.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the dimensionality of the embedding vectors produced by this provider.
    /// </summary>
    int VectorDimensions { get; }

    /// <summary>
    /// Gets the maximum number of tokens that can be processed in a single text input.
    /// </summary>
    int MaxTokens { get; }

    /// <summary>
    /// Gets whether this provider supports batch embedding of multiple texts in a single request.
    /// </summary>
    bool SupportsMultipleTexts { get; }

    /// <summary>
    /// Generates an embedding vector for a single text input.
    /// </summary>
    /// <param name="text">The text to generate an embedding for.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A float array representing the embedding vector.</returns>
    /// <exception cref="EmbeddingException">Thrown when embedding generation fails.</exception>
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Generates embedding vectors for multiple texts in a batch operation.
    /// </summary>
    /// <param name="texts">Array of texts to generate embeddings for.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A 2D float array where each row is an embedding vector.</returns>
    /// <exception cref="EmbeddingException">Thrown when batch embedding generation fails.</exception>
    Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default);

    /// <summary>
    /// Validates that the provider can connect and authenticate successfully.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>True if the connection is valid, false otherwise.</returns>
    Task<bool> ValidateConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Extended embedding provider interface with additional capabilities.
/// </summary>
public interface IEmbeddingProviderExtended : IEmbeddingProvider
{
    /// <summary>
    /// Gets the available models for this provider.
    /// </summary>
    IReadOnlyList<EmbeddingModelInfo> AvailableModels { get; }

    /// <summary>
    /// Gets or sets the currently selected model.
    /// </summary>
    string CurrentModel { get; set; }

    /// <summary>
    /// Gets the current metrics for this provider.
    /// </summary>
    EmbeddingProviderMetrics GetMetrics();

    /// <summary>
    /// Resets the provider metrics.
    /// </summary>
    void ResetMetrics();

    /// <summary>
    /// Gets the estimated cost for embedding the given texts.
    /// </summary>
    /// <param name="texts">Texts to estimate cost for.</param>
    /// <returns>Estimated cost information.</returns>
    EmbeddingCostEstimate EstimateCost(string[] texts);
}

/// <summary>
/// Information about an embedding model.
/// </summary>
public sealed record EmbeddingModelInfo
{
    /// <summary>Model identifier.</summary>
    public required string ModelId { get; init; }

    /// <summary>Human-readable model name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Number of dimensions in the output vectors.</summary>
    public required int Dimensions { get; init; }

    /// <summary>Maximum tokens supported.</summary>
    public required int MaxTokens { get; init; }

    /// <summary>Cost per 1000 tokens (USD).</summary>
    public decimal CostPer1KTokens { get; init; }

    /// <summary>Whether this is the default model.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Additional capabilities or features.</summary>
    public string[] Features { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Metrics for embedding provider usage.
/// </summary>
public sealed class EmbeddingProviderMetrics
{
    /// <summary>Total number of embedding requests made.</summary>
    public long TotalRequests { get; set; }

    /// <summary>Number of successful requests.</summary>
    public long SuccessfulRequests { get; set; }

    /// <summary>Number of failed requests.</summary>
    public long FailedRequests { get; set; }

    /// <summary>Total tokens processed.</summary>
    public long TotalTokensProcessed { get; set; }

    /// <summary>Total embeddings generated.</summary>
    public long TotalEmbeddingsGenerated { get; set; }

    /// <summary>Total latency in milliseconds (cumulative).</summary>
    public double TotalLatencyMs { get; set; }

    /// <summary>Average latency per request in milliseconds.</summary>
    public double AverageLatencyMs => TotalRequests > 0 ? TotalLatencyMs / TotalRequests : 0;

    /// <summary>Estimated total cost in USD.</summary>
    public decimal EstimatedCostUsd { get; set; }

    /// <summary>Number of rate limit hits.</summary>
    public long RateLimitHits { get; set; }

    /// <summary>Number of retries performed.</summary>
    public long TotalRetries { get; set; }

    /// <summary>Cache hit count (if caching is enabled).</summary>
    public long CacheHits { get; set; }

    /// <summary>Cache miss count.</summary>
    public long CacheMisses { get; set; }

    /// <summary>When metrics tracking started.</summary>
    public DateTime StartTime { get; init; } = DateTime.UtcNow;

    /// <summary>Time of last request.</summary>
    public DateTime LastRequestTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cost estimate for embedding operations.
/// </summary>
public sealed record EmbeddingCostEstimate
{
    /// <summary>Estimated number of tokens.</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Estimated cost in USD.</summary>
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>Cost per 1000 tokens.</summary>
    public decimal CostPer1KTokens { get; init; }

    /// <summary>Model used for estimation.</summary>
    public string Model { get; init; } = "";
}

/// <summary>
/// Configuration for embedding providers.
/// </summary>
public sealed record EmbeddingProviderConfig
{
    /// <summary>API key or authentication token.</summary>
    public string? ApiKey { get; init; }

    /// <summary>API endpoint URL.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Model to use for embeddings.</summary>
    public string? Model { get; init; }

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Maximum retry attempts.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Initial retry delay in milliseconds.</summary>
    public int RetryDelayMs { get; init; } = 1000;

    /// <summary>Maximum retry delay in milliseconds.</summary>
    public int MaxRetryDelayMs { get; init; } = 30000;

    /// <summary>Whether to enable request/response logging.</summary>
    public bool EnableLogging { get; init; }

    /// <summary>Additional provider-specific configuration.</summary>
    public Dictionary<string, object> AdditionalConfig { get; init; } = new();
}

/// <summary>
/// Exception thrown when embedding generation fails.
/// </summary>
public class EmbeddingException : Exception
{
    /// <summary>The provider that threw the exception.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Whether this is a transient error that may succeed on retry.</summary>
    public bool IsTransient { get; init; }

    /// <summary>HTTP status code if applicable.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>Rate limit information if applicable.</summary>
    public RateLimitInfo? RateLimitInfo { get; init; }

    public EmbeddingException(string message) : base(message) { }
    public EmbeddingException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Rate limit information from the provider.
/// </summary>
public sealed record RateLimitInfo
{
    /// <summary>When the rate limit resets.</summary>
    public DateTime ResetAt { get; init; }

    /// <summary>Remaining requests in the current window.</summary>
    public int RemainingRequests { get; init; }

    /// <summary>Total requests allowed in the window.</summary>
    public int TotalRequests { get; init; }

    /// <summary>Seconds until reset.</summary>
    public int RetryAfterSeconds { get; init; }
}

/// <summary>
/// Abstract base class for embedding providers with common functionality.
/// </summary>
public abstract class EmbeddingProviderBase : IEmbeddingProviderExtended
{
    private readonly EmbeddingProviderMetrics _metrics = new();
    private readonly SemaphoreSlim _rateLimitSemaphore;
    private readonly object _metricsLock = new();
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>
    /// Configuration for this provider.
    /// </summary>
    protected EmbeddingProviderConfig Config { get; }

    /// <summary>
    /// HTTP client for making requests.
    /// </summary>
    protected HttpClient HttpClient { get; }

    /// <inheritdoc/>
    public abstract string ProviderId { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract int VectorDimensions { get; }

    /// <inheritdoc/>
    public abstract int MaxTokens { get; }

    /// <inheritdoc/>
    public virtual bool SupportsMultipleTexts => true;

    /// <inheritdoc/>
    public abstract IReadOnlyList<EmbeddingModelInfo> AvailableModels { get; }

    /// <inheritdoc/>
    public abstract string CurrentModel { get; set; }

    /// <summary>
    /// Creates a new embedding provider instance.
    /// </summary>
    /// <param name="config">Provider configuration.</param>
    /// <param name="httpClient">Optional HTTP client (will create one if null).</param>
    protected EmbeddingProviderBase(EmbeddingProviderConfig config, HttpClient? httpClient = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _ownsHttpClient = httpClient == null;
        HttpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
        _rateLimitSemaphore = new SemaphoreSlim(10, 10); // Default 10 concurrent requests
    }

    /// <inheritdoc/>
    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be null or whitespace", nameof(text));

        var sw = Stopwatch.StartNew();
        try
        {
            await _rateLimitSemaphore.WaitAsync(ct);
            try
            {
                var result = await ExecuteWithRetryAsync(
                    () => GetEmbeddingCoreAsync(text, ct),
                    ct);

                RecordSuccess(sw.Elapsed.TotalMilliseconds, 1, EstimateTokens(text));
                return result;
            }
            finally
            {
                _rateLimitSemaphore.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure(ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
    {
        if (texts == null || texts.Length == 0)
            throw new ArgumentException("Texts array cannot be null or empty", nameof(texts));

        var sw = Stopwatch.StartNew();
        try
        {
            await _rateLimitSemaphore.WaitAsync(ct);
            try
            {
                var result = await ExecuteWithRetryAsync(
                    () => GetEmbeddingsBatchCoreAsync(texts, ct),
                    ct);

                var totalTokens = texts.Sum(t => EstimateTokens(t));
                RecordSuccess(sw.Elapsed.TotalMilliseconds, texts.Length, totalTokens);
                return result;
            }
            finally
            {
                _rateLimitSemaphore.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure(ex);
            throw;
        }
    }

    /// <summary>
    /// Core implementation of single embedding generation. Override in derived classes.
    /// </summary>
    protected abstract Task<float[]> GetEmbeddingCoreAsync(string text, CancellationToken ct);

    /// <summary>
    /// Core implementation of batch embedding generation. Override in derived classes.
    /// Default implementation calls GetEmbeddingCoreAsync for each text.
    /// </summary>
    protected virtual async Task<float[][]> GetEmbeddingsBatchCoreAsync(string[] texts, CancellationToken ct)
    {
        var results = new float[texts.Length][];
        for (int i = 0; i < texts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            results[i] = await GetEmbeddingCoreAsync(texts[i], ct);
        }
        return results;
    }

    /// <inheritdoc/>
    public abstract Task<bool> ValidateConnectionAsync(CancellationToken ct = default);

    /// <inheritdoc/>
    public EmbeddingProviderMetrics GetMetrics()
    {
        lock (_metricsLock)
        {
            return new EmbeddingProviderMetrics
            {
                TotalRequests = _metrics.TotalRequests,
                SuccessfulRequests = _metrics.SuccessfulRequests,
                FailedRequests = _metrics.FailedRequests,
                TotalTokensProcessed = _metrics.TotalTokensProcessed,
                TotalEmbeddingsGenerated = _metrics.TotalEmbeddingsGenerated,
                TotalLatencyMs = _metrics.TotalLatencyMs,
                EstimatedCostUsd = _metrics.EstimatedCostUsd,
                RateLimitHits = _metrics.RateLimitHits,
                TotalRetries = _metrics.TotalRetries,
                CacheHits = _metrics.CacheHits,
                CacheMisses = _metrics.CacheMisses,
                StartTime = _metrics.StartTime,
                LastRequestTime = _metrics.LastRequestTime
            };
        }
    }

    /// <inheritdoc/>
    public void ResetMetrics()
    {
        lock (_metricsLock)
        {
            _metrics.TotalRequests = 0;
            _metrics.SuccessfulRequests = 0;
            _metrics.FailedRequests = 0;
            _metrics.TotalTokensProcessed = 0;
            _metrics.TotalEmbeddingsGenerated = 0;
            _metrics.TotalLatencyMs = 0;
            _metrics.EstimatedCostUsd = 0;
            _metrics.RateLimitHits = 0;
            _metrics.TotalRetries = 0;
            _metrics.CacheHits = 0;
            _metrics.CacheMisses = 0;
        }
    }

    /// <inheritdoc/>
    public virtual EmbeddingCostEstimate EstimateCost(string[] texts)
    {
        var totalTokens = texts.Sum(t => EstimateTokens(t));
        var model = AvailableModels.FirstOrDefault(m => m.ModelId == CurrentModel)
            ?? AvailableModels.FirstOrDefault();
        var costPer1K = model?.CostPer1KTokens ?? 0;
        var estimatedCost = (totalTokens / 1000m) * costPer1K;

        return new EmbeddingCostEstimate
        {
            EstimatedTokens = totalTokens,
            EstimatedCostUsd = estimatedCost,
            CostPer1KTokens = costPer1K,
            Model = CurrentModel
        };
    }

    /// <summary>
    /// Estimates the number of tokens in a text. Override for more accurate estimation.
    /// </summary>
    protected virtual int EstimateTokens(string text)
    {
        // Simple estimation: ~4 characters per token for English text
        return (int)Math.Ceiling(text.Length / 4.0);
    }

    /// <summary>
    /// Executes an operation with retry logic and exponential backoff.
    /// </summary>
    protected async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var retryCount = 0;
        var delay = Config.RetryDelayMs;

        while (true)
        {
            try
            {
                return await operation();
            }
            catch (EmbeddingException ex) when (ex.IsTransient && retryCount < Config.MaxRetries)
            {
                retryCount++;
                RecordRetry();

                if (ex.RateLimitInfo != null)
                {
                    RecordRateLimitHit();
                    delay = Math.Max(delay, ex.RateLimitInfo.RetryAfterSeconds * 1000);
                }

                await Task.Delay(Math.Min(delay, Config.MaxRetryDelayMs), ct);
                delay *= 2; // Exponential backoff
            }
            catch (HttpRequestException) when (retryCount < Config.MaxRetries)
            {
                retryCount++;
                RecordRetry();
                await Task.Delay(Math.Min(delay, Config.MaxRetryDelayMs), ct);
                delay *= 2;
            }
        }
    }

    /// <summary>
    /// Records a successful operation.
    /// </summary>
    protected void RecordSuccess(double latencyMs, int embeddingCount, int tokenCount)
    {
        lock (_metricsLock)
        {
            _metrics.TotalRequests++;
            _metrics.SuccessfulRequests++;
            _metrics.TotalLatencyMs += latencyMs;
            _metrics.TotalEmbeddingsGenerated += embeddingCount;
            _metrics.TotalTokensProcessed += tokenCount;
            _metrics.LastRequestTime = DateTime.UtcNow;

            // Calculate cost
            var model = AvailableModels.FirstOrDefault(m => m.ModelId == CurrentModel);
            if (model != null)
            {
                _metrics.EstimatedCostUsd += (tokenCount / 1000m) * model.CostPer1KTokens;
            }
        }
    }

    /// <summary>
    /// Records a failed operation.
    /// </summary>
    protected void RecordFailure(Exception ex)
    {
        lock (_metricsLock)
        {
            _metrics.TotalRequests++;
            _metrics.FailedRequests++;
            _metrics.LastRequestTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Records a retry attempt.
    /// </summary>
    protected void RecordRetry()
    {
        lock (_metricsLock)
        {
            _metrics.TotalRetries++;
        }
    }

    /// <summary>
    /// Records a rate limit hit.
    /// </summary>
    protected void RecordRateLimitHit()
    {
        lock (_metricsLock)
        {
            _metrics.RateLimitHits++;
        }
    }

    /// <summary>
    /// Records a cache hit.
    /// </summary>
    protected void RecordCacheHit()
    {
        lock (_metricsLock)
        {
            _metrics.CacheHits++;
        }
    }

    /// <summary>
    /// Records a cache miss.
    /// </summary>
    protected void RecordCacheMiss()
    {
        lock (_metricsLock)
        {
            _metrics.CacheMisses++;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes resources used by this provider.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _rateLimitSemaphore.Dispose();
                if (_ownsHttpClient)
                {
                    HttpClient.Dispose();
                }
            }
            _disposed = true;
        }
    }
}
