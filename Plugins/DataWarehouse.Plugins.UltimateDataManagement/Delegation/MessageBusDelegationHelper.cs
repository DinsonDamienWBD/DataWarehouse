using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Resilience;
using DataWarehouse.SDK.Contracts.Scaling;
using DataWarehouse.SDK.Infrastructure.InMemory;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Delegation;

/// <summary>
/// Wraps message bus delegation calls with circuit breaker protection and local cache fallback.
/// When the authoritative plugin (e.g. UltimateDataCatalog) is unavailable or slow,
/// this helper returns cached results or signals unavailability gracefully.
/// </summary>
/// <remarks>
/// Phase 94-04: Graceful degradation for cross-plugin message bus delegation.
/// Uses SDK's <see cref="InMemoryCircuitBreaker"/> and <see cref="BoundedCache{TKey,TValue}"/>
/// to provide resilient delegation with automatic recovery.
/// </remarks>
internal sealed class MessageBusDelegationHelper : IDisposable
{
    private static readonly TimeSpan DefaultDelegationTimeout = TimeSpan.FromSeconds(2);
    private const int DefaultCacheMaxEntries = 500;
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    private readonly Func<IMessageBus?> _messageBusAccessor;
    private readonly string _targetTopic;
    private readonly InMemoryCircuitBreaker _circuitBreaker;
    private readonly BoundedCache<string, object> _fallbackCache;
    private readonly TimeSpan _delegationTimeout;
    private bool _disposed;

    /// <summary>
    /// Initializes a new delegation helper for a specific target topic.
    /// </summary>
    /// <param name="pluginName">The name of the owning plugin (for circuit breaker naming).</param>
    /// <param name="messageBusAccessor">Accessor to get the current IMessageBus instance from the owning plugin.</param>
    /// <param name="targetTopic">The topic prefix for delegation (e.g. "catalog").</param>
    /// <param name="options">Circuit breaker options. Uses defaults if null (5 failures / 60s window / 30s break).</param>
    /// <param name="delegationTimeout">Timeout for each delegation call. Defaults to 2 seconds.</param>
    public MessageBusDelegationHelper(
        string pluginName,
        Func<IMessageBus?> messageBusAccessor,
        string targetTopic,
        CircuitBreakerOptions? options = null,
        TimeSpan? delegationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(pluginName);
        _messageBusAccessor = messageBusAccessor ?? throw new ArgumentNullException(nameof(messageBusAccessor));
        _targetTopic = targetTopic ?? throw new ArgumentNullException(nameof(targetTopic));
        _delegationTimeout = delegationTimeout ?? DefaultDelegationTimeout;

        _circuitBreaker = new InMemoryCircuitBreaker(
            $"{pluginName}.delegation.{targetTopic}",
            options ?? new CircuitBreakerOptions
            {
                FailureThreshold = 5,
                FailureWindow = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(30)
            });

        _fallbackCache = new BoundedCache<string, object>(new BoundedCacheOptions<string, object>
        {
            MaxEntries = DefaultCacheMaxEntries,
            EvictionPolicy = CacheEvictionMode.Ttl,
            DefaultTtl = DefaultCacheTtl
        });
    }

    /// <summary>
    /// Delegates a message bus call with circuit breaker protection and local cache fallback.
    /// On success, caches the response. On failure, returns cached result or signals unavailability.
    /// </summary>
    /// <param name="topic">The full topic to send to (e.g. "catalog.register").</param>
    /// <param name="message">The plugin message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The message response from the authoritative plugin, cache, or an unavailable indicator.</returns>
    public async Task<MessageResponse> DelegateAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(topic, message.Payload);

        try
        {
            var response = await _circuitBreaker.ExecuteAsync(async linkedCt =>
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCt);
                timeoutCts.CancelAfter(_delegationTimeout);

                var bus = _messageBusAccessor();
                if (bus == null)
                    throw new InvalidOperationException("MessageBus not available");

                return await bus.SendAsync(topic, message, timeoutCts.Token);
            }, ct);

            // Cache successful response
            if (response.Success)
            {
                _fallbackCache.Put(cacheKey, response);
            }

            return response;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Circuit open, timeout, or delegation failure -- try cache fallback
            if (_fallbackCache.TryGet(cacheKey, out var cached) && cached is MessageResponse cachedResponse)
            {
                return new MessageResponse
                {
                    Success = cachedResponse.Success,
                    Payload = cachedResponse.Payload,
                    ErrorMessage = cachedResponse.ErrorMessage,
                    ErrorCode = "DELEGATION_FALLBACK"
                };
            }

            // No cached result -- return unavailable response (do not throw)
            return new MessageResponse
            {
                Success = false,
                ErrorMessage = $"Delegation to {_targetTopic} unavailable and no cached result",
                ErrorCode = "DELEGATION_UNAVAILABLE"
            };
        }
    }

    /// <summary>
    /// Gets the circuit breaker statistics for observability and health monitoring.
    /// </summary>
    /// <returns>Current circuit breaker statistics.</returns>
    public CircuitBreakerStatistics GetStatistics() => _circuitBreaker.GetStatistics();

    /// <summary>
    /// Builds a deterministic cache key from the topic and payload.
    /// </summary>
    private static string BuildCacheKey(string topic, Dictionary<string, object> payload)
    {
        var action = payload.TryGetValue("action", out var actObj) && actObj is string act ? act : "";
        var id = payload.TryGetValue("id", out var idObj) && idObj is string idStr ? idStr : "";
        var nodeId = payload.TryGetValue("nodeId", out var nObj) && nObj is string nStr ? nStr : "";
        var sourceNodeId = payload.TryGetValue("sourceNodeId", out var snObj) && snObj is string snStr ? snStr : "";
        var targetNodeId = payload.TryGetValue("targetNodeId", out var tnObj) && tnObj is string tnStr ? tnStr : "";
        var query = payload.TryGetValue("query", out var qObj) && qObj is string qStr ? qStr : "";

        return $"{topic}|{action}|{id}|{nodeId}|{sourceNodeId}|{targetNodeId}|{query}";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fallbackCache.Dispose();
    }
}
