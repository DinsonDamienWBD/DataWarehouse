using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.AI
{
    /// <summary>
    /// Configuration for AI rate limiting.
    /// </summary>
    public class AIRateLimitConfig
    {
        /// <summary>Maximum requests per minute for completions.</summary>
        public int CompletionRequestsPerMinute { get; set; } = 60;

        /// <summary>Maximum requests per minute for embeddings.</summary>
        public int EmbeddingRequestsPerMinute { get; set; } = 120;

        /// <summary>Maximum tokens per minute (if supported by provider).</summary>
        public int TokensPerMinute { get; set; } = 100000;

        /// <summary>Maximum concurrent requests.</summary>
        public int MaxConcurrentRequests { get; set; } = 10;

        /// <summary>Whether to queue excess requests or reject immediately.</summary>
        public bool QueueExcessRequests { get; set; } = true;

        /// <summary>Maximum time to wait in queue before timing out.</summary>
        public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Enable per-provider rate limiting.</summary>
        public bool EnablePerProviderLimits { get; set; } = true;
    }

    /// <summary>
    /// Rate-limited wrapper for AI providers.
    /// Implements token bucket rate limiting with queue support.
    /// </summary>
    public sealed class RateLimitedAIProvider : IAIProvider, IDisposable
    {
        private readonly IAIProvider _inner;
        private readonly AIRateLimitConfig _config;
        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly SemaphoreSlim _completionRateLimiter;
        private readonly SemaphoreSlim _embeddingRateLimiter;
        private readonly Timer _refillTimer;
        private readonly object _statsLock = new();

        private long _totalRequests;
        private long _throttledRequests;
        private long _rejectedRequests;

        public string ProviderId => _inner.ProviderId;
        public string DisplayName => $"{_inner.DisplayName} (Rate Limited)";
        public bool IsAvailable => _inner.IsAvailable;
        public AICapabilities Capabilities => _inner.Capabilities;

        public RateLimitedAIProvider(IAIProvider inner, AIRateLimitConfig? config = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _config = config ?? new AIRateLimitConfig();

            _concurrencySemaphore = new SemaphoreSlim(_config.MaxConcurrentRequests, _config.MaxConcurrentRequests);
            _completionRateLimiter = new SemaphoreSlim(_config.CompletionRequestsPerMinute, _config.CompletionRequestsPerMinute);
            _embeddingRateLimiter = new SemaphoreSlim(_config.EmbeddingRequestsPerMinute, _config.EmbeddingRequestsPerMinute);

            // Refill rate limiters every minute
            _refillTimer = new Timer(RefillRateLimiters, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        private void RefillRateLimiters(object? state)
        {
            // Refill completion limiter
            var completionToRelease = _config.CompletionRequestsPerMinute - _completionRateLimiter.CurrentCount;
            if (completionToRelease > 0)
            {
                _completionRateLimiter.Release(completionToRelease);
            }

            // Refill embedding limiter
            var embeddingToRelease = _config.EmbeddingRequestsPerMinute - _embeddingRateLimiter.CurrentCount;
            if (embeddingToRelease > 0)
            {
                _embeddingRateLimiter.Release(embeddingToRelease);
            }
        }

        private async Task AcquireRateLimitAsync(SemaphoreSlim limiter, CancellationToken ct)
        {
            Interlocked.Increment(ref _totalRequests);

            // Try to acquire concurrency slot
            if (!await _concurrencySemaphore.WaitAsync(_config.QueueTimeout, ct))
            {
                Interlocked.Increment(ref _rejectedRequests);
                throw new AIRateLimitExceededException("Maximum concurrent requests exceeded", TimeSpan.FromSeconds(5));
            }

            try
            {
                // Try to acquire rate limit slot
                if (_config.QueueExcessRequests)
                {
                    if (!await limiter.WaitAsync(_config.QueueTimeout, ct))
                    {
                        Interlocked.Increment(ref _throttledRequests);
                        throw new AIRateLimitExceededException("Rate limit queue timeout", TimeSpan.FromMinutes(1));
                    }
                }
                else
                {
                    if (!await limiter.WaitAsync(TimeSpan.Zero, ct))
                    {
                        Interlocked.Increment(ref _throttledRequests);
                        throw new AIRateLimitExceededException("Rate limit exceeded", TimeSpan.FromMinutes(1));
                    }
                }
            }
            catch
            {
                _concurrencySemaphore.Release();
                throw;
            }
        }

        private void ReleaseRateLimit()
        {
            _concurrencySemaphore.Release();
        }

        public async Task<AIResponse> CompleteAsync(AIRequest request, CancellationToken ct = default)
        {
            await AcquireRateLimitAsync(_completionRateLimiter, ct);
            try
            {
                return await _inner.CompleteAsync(request, ct);
            }
            finally
            {
                ReleaseRateLimit();
            }
        }

        public async IAsyncEnumerable<AIStreamChunk> CompleteStreamingAsync(AIRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await AcquireRateLimitAsync(_completionRateLimiter, ct);
            try
            {
                await foreach (var chunk in _inner.CompleteStreamingAsync(request, ct))
                {
                    yield return chunk;
                }
            }
            finally
            {
                ReleaseRateLimit();
            }
        }

        public async Task<float[]> GetEmbeddingsAsync(string text, CancellationToken ct = default)
        {
            await AcquireRateLimitAsync(_embeddingRateLimiter, ct);
            try
            {
                return await _inner.GetEmbeddingsAsync(text, ct);
            }
            finally
            {
                ReleaseRateLimit();
            }
        }

        public async Task<float[][]> GetEmbeddingsBatchAsync(string[] texts, CancellationToken ct = default)
        {
            await AcquireRateLimitAsync(_embeddingRateLimiter, ct);
            try
            {
                return await _inner.GetEmbeddingsBatchAsync(texts, ct);
            }
            finally
            {
                ReleaseRateLimit();
            }
        }

        /// <summary>
        /// Gets rate limiting statistics.
        /// </summary>
        public AIRateLimitStats GetStats()
        {
            return new AIRateLimitStats
            {
                TotalRequests = Interlocked.Read(ref _totalRequests),
                ThrottledRequests = Interlocked.Read(ref _throttledRequests),
                RejectedRequests = Interlocked.Read(ref _rejectedRequests),
                CurrentConcurrentRequests = _config.MaxConcurrentRequests - _concurrencySemaphore.CurrentCount,
                AvailableCompletionSlots = _completionRateLimiter.CurrentCount,
                AvailableEmbeddingSlots = _embeddingRateLimiter.CurrentCount
            };
        }

        public void Dispose()
        {
            _refillTimer.Dispose();
            _concurrencySemaphore.Dispose();
            _completionRateLimiter.Dispose();
            _embeddingRateLimiter.Dispose();
        }
    }

    /// <summary>
    /// Exception thrown when AI rate limit is exceeded.
    /// </summary>
    public class AIRateLimitExceededException : Exception
    {
        public TimeSpan RetryAfter { get; }

        public AIRateLimitExceededException(string message, TimeSpan retryAfter)
            : base(message)
        {
            RetryAfter = retryAfter;
        }
    }

    /// <summary>
    /// Rate limiting statistics.
    /// </summary>
    public class AIRateLimitStats
    {
        public long TotalRequests { get; init; }
        public long ThrottledRequests { get; init; }
        public long RejectedRequests { get; init; }
        public int CurrentConcurrentRequests { get; init; }
        public int AvailableCompletionSlots { get; init; }
        public int AvailableEmbeddingSlots { get; init; }
    }

    /// <summary>
    /// Default implementation of the AI Provider Registry.
    /// Manages registration, discovery, and selection of AI providers.
    /// Supports automatic rate limiting for all registered providers.
    /// </summary>
    public sealed class AIProviderRegistry : IAIProviderRegistry
    {
        private readonly ConcurrentDictionary<string, IAIProvider> _providers = new();
        private readonly ConcurrentDictionary<string, RateLimitedAIProvider> _rateLimitedProviders = new();
        private readonly object _defaultLock = new();
        private string? _defaultProviderId;
        private readonly AIRateLimitConfig? _globalRateLimitConfig;

        /// <summary>
        /// Event raised when a provider is registered.
        /// </summary>
        public event Action<IAIProvider>? OnProviderRegistered;

        /// <summary>
        /// Event raised when a provider is unregistered.
        /// </summary>
        public event Action<string>? OnProviderUnregistered;

        public AIProviderRegistry(AIRateLimitConfig? globalRateLimitConfig = null)
        {
            _globalRateLimitConfig = globalRateLimitConfig;
        }

        /// <summary>
        /// Registers a provider with optional rate limiting.
        /// </summary>
        /// <param name="provider">The provider to register.</param>
        /// <param name="enableRateLimiting">Whether to wrap with rate limiting. Uses global config if true.</param>
        /// <param name="rateLimitConfig">Custom rate limit config for this provider. Overrides global config.</param>
        public void Register(IAIProvider provider, bool enableRateLimiting = false, AIRateLimitConfig? rateLimitConfig = null)
        {
            ArgumentNullException.ThrowIfNull(provider);

            if (string.IsNullOrEmpty(provider.ProviderId))
            {
                throw new ArgumentException("Provider must have a valid ProviderId", nameof(provider));
            }

            // Wrap with rate limiting if requested
            IAIProvider providerToRegister = provider;
            if (enableRateLimiting || _globalRateLimitConfig != null)
            {
                var config = rateLimitConfig ?? _globalRateLimitConfig ?? new AIRateLimitConfig();
                var rateLimited = new RateLimitedAIProvider(provider, config);
                _rateLimitedProviders[provider.ProviderId] = rateLimited;
                providerToRegister = rateLimited;
            }

            _providers[provider.ProviderId] = providerToRegister;

            // Set as default if it's the first provider
            lock (_defaultLock)
            {
                if (_defaultProviderId == null)
                {
                    _defaultProviderId = provider.ProviderId;
                }
            }

            OnProviderRegistered?.Invoke(providerToRegister);
        }

        /// <summary>
        /// Interface implementation for backward compatibility.
        /// </summary>
        public void Register(IAIProvider provider)
        {
            Register(provider, enableRateLimiting: _globalRateLimitConfig != null);
        }

        /// <summary>
        /// Gets rate limit statistics for a provider.
        /// </summary>
        public AIRateLimitStats? GetRateLimitStats(string providerId)
        {
            return _rateLimitedProviders.TryGetValue(providerId, out var provider)
                ? provider.GetStats()
                : null;
        }

        public void Unregister(string providerId)
        {
            ArgumentNullException.ThrowIfNull(providerId);

            if (_providers.TryRemove(providerId, out _))
            {
                // Clean up rate limited wrapper if exists
                if (_rateLimitedProviders.TryRemove(providerId, out var rateLimited))
                {
                    rateLimited.Dispose();
                }

                // If this was the default, pick a new one
                lock (_defaultLock)
                {
                    if (_defaultProviderId == providerId)
                    {
                        _defaultProviderId = _providers.Keys.FirstOrDefault();
                    }
                }

                OnProviderUnregistered?.Invoke(providerId);
            }
        }

        public IAIProvider? GetProvider(string providerId)
        {
            ArgumentNullException.ThrowIfNull(providerId);

            return _providers.TryGetValue(providerId, out var provider) ? provider : null;
        }

        public IEnumerable<IAIProvider> GetAllProviders()
        {
            return _providers.Values.ToArray();
        }

        public IAIProvider? GetDefaultProvider()
        {
            lock (_defaultLock)
            {
                if (_defaultProviderId == null)
                {
                    return null;
                }

                return _providers.TryGetValue(_defaultProviderId, out var provider) ? provider : null;
            }
        }

        public void SetDefaultProvider(string providerId)
        {
            ArgumentNullException.ThrowIfNull(providerId);

            if (!_providers.ContainsKey(providerId))
            {
                throw new ArgumentException($"Provider '{providerId}' is not registered", nameof(providerId));
            }

            lock (_defaultLock)
            {
                _defaultProviderId = providerId;
            }
        }

        public IEnumerable<IAIProvider> GetProvidersWithCapabilities(AICapabilities required)
        {
            return _providers.Values
                .Where(p => p.IsAvailable && (p.Capabilities & required) == required)
                .ToArray();
        }

        /// <summary>
        /// Gets the best available provider for the specified capabilities.
        /// Returns available providers sorted by capability match.
        /// </summary>
        public IAIProvider? GetBestProvider(AICapabilities required)
        {
            var candidates = GetProvidersWithCapabilities(required).ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            // Prefer the default provider if it meets requirements
            var defaultProvider = GetDefaultProvider();
            if (defaultProvider != null &&
                defaultProvider.IsAvailable &&
                (defaultProvider.Capabilities & required) == required)
            {
                return defaultProvider;
            }

            // Otherwise return the first available
            return candidates.FirstOrDefault();
        }

        /// <summary>
        /// Gets a provider for embeddings, or null if none available.
        /// </summary>
        public IAIProvider? GetEmbeddingProvider()
        {
            return GetBestProvider(AICapabilities.Embeddings);
        }

        /// <summary>
        /// Gets a provider for text completion, or null if none available.
        /// </summary>
        public IAIProvider? GetCompletionProvider()
        {
            return GetBestProvider(AICapabilities.TextCompletion);
        }

        /// <summary>
        /// Gets a provider for chat completion, or null if none available.
        /// </summary>
        public IAIProvider? GetChatProvider()
        {
            return GetBestProvider(AICapabilities.ChatCompletion);
        }

        /// <summary>
        /// Gets a provider for streaming completion, or null if none available.
        /// </summary>
        public IAIProvider? GetStreamingProvider()
        {
            return GetBestProvider(AICapabilities.ChatCompletion | AICapabilities.Streaming);
        }

        /// <summary>
        /// Gets a provider chain for fallback scenarios.
        /// Returns providers in priority order that support the required capabilities.
        /// </summary>
        public IEnumerable<IAIProvider> GetProviderChain(AICapabilities required)
        {
            var providers = GetProvidersWithCapabilities(required).ToList();

            // Default provider first
            var defaultProvider = GetDefaultProvider();
            if (defaultProvider != null &&
                defaultProvider.IsAvailable &&
                (defaultProvider.Capabilities & required) == required)
            {
                yield return defaultProvider;
                providers.Remove(defaultProvider);
            }

            foreach (var provider in providers)
            {
                yield return provider;
            }
        }

        /// <summary>
        /// Gets provider availability summary.
        /// </summary>
        public AIProviderSummary GetSummary()
        {
            var providers = _providers.Values.ToList();

            return new AIProviderSummary
            {
                TotalProviders = providers.Count,
                AvailableProviders = providers.Count(p => p.IsAvailable),
                DefaultProviderId = _defaultProviderId,
                ProviderDetails = providers.Select(p => new AIProviderDetail
                {
                    ProviderId = p.ProviderId,
                    DisplayName = p.DisplayName,
                    IsAvailable = p.IsAvailable,
                    Capabilities = p.Capabilities,
                    IsDefault = p.ProviderId == _defaultProviderId
                }).ToList()
            };
        }
    }

    /// <summary>
    /// Summary of registered AI providers.
    /// </summary>
    public class AIProviderSummary
    {
        public int TotalProviders { get; init; }
        public int AvailableProviders { get; init; }
        public string? DefaultProviderId { get; init; }
        public List<AIProviderDetail> ProviderDetails { get; init; } = new();
    }

    /// <summary>
    /// Details of a single AI provider.
    /// </summary>
    public class AIProviderDetail
    {
        public string ProviderId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public bool IsAvailable { get; init; }
        public AICapabilities Capabilities { get; init; }
        public bool IsDefault { get; init; }
    }
}
