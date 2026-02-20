using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.Integration;

/// <summary>
/// Bridge between the chaos vaccination system and the existing UltimateResilience infrastructure.
/// Communicates exclusively via the message bus -- no direct assembly reference to UltimateResilience.
///
/// Supports request/response patterns with correlation IDs and configurable timeouts.
/// All operations are thread-safe and tolerate missing responders (timeout with meaningful error).
///
/// Topics used:
/// - "resilience.circuit-breaker.trip" / "resilience.circuit-breaker.reset"
/// - "resilience.bulkhead.create" / "resilience.bulkhead.release"
/// - "resilience.circuit-status.request" / "resilience.circuit-status.response"
/// - "resilience.health-check.request" / "resilience.health-check.response"
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination orchestrator")]
public sealed class ExistingResilienceIntegration : IDisposable
{
    private readonly IMessageBus _messageBus;
    private readonly BoundedDictionary<string, TaskCompletionSource<PluginMessage>> _pendingRequests = new BoundedDictionary<string, TaskCompletionSource<PluginMessage>>(1000);
    private readonly List<IDisposable> _subscriptions = new();
    private readonly TimeSpan _defaultTimeout;
    private bool _disposed;

    /// <summary>
    /// Creates a new ExistingResilienceIntegration bridge.
    /// </summary>
    /// <param name="messageBus">The message bus for resilience infrastructure communication.</param>
    /// <param name="defaultTimeoutMs">Default timeout in milliseconds for request/response operations. Default: 5000.</param>
    public ExistingResilienceIntegration(IMessageBus messageBus, int defaultTimeoutMs = 5000)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _defaultTimeout = TimeSpan.FromMilliseconds(defaultTimeoutMs);

        // Subscribe to response topics
        _subscriptions.Add(_messageBus.Subscribe("resilience.circuit-status.response", OnResponseReceived));
        _subscriptions.Add(_messageBus.Subscribe("resilience.health-check.response", OnResponseReceived));
    }

    /// <summary>
    /// Requests a circuit breaker trip for the specified plugin.
    /// Publishes to "resilience.circuit-breaker.trip" with the plugin ID and reason.
    /// </summary>
    /// <param name="pluginId">The plugin whose circuit breaker should be tripped.</param>
    /// <param name="reason">The reason for tripping the circuit breaker.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RequestCircuitBreakerTripAsync(string pluginId, string reason = "Chaos vaccination request", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _messageBus.PublishAsync("resilience.circuit-breaker.trip", new PluginMessage
        {
            Type = "resilience.circuit-breaker.trip",
            SourcePluginId = "com.datawarehouse.chaos.vaccination",
            Payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["pluginId"] = pluginId,
                ["reason"] = reason,
                ["source"] = "chaos-vaccination"
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests a circuit breaker reset for the specified plugin.
    /// Publishes to "resilience.circuit-breaker.reset" with the plugin ID.
    /// </summary>
    /// <param name="pluginId">The plugin whose circuit breaker should be reset.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RequestCircuitBreakerResetAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _messageBus.PublishAsync("resilience.circuit-breaker.reset", new PluginMessage
        {
            Type = "resilience.circuit-breaker.reset",
            SourcePluginId = "com.datawarehouse.chaos.vaccination",
            Payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["pluginId"] = pluginId,
                ["source"] = "chaos-vaccination"
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests bulkhead isolation for the specified plugin with a given concurrency limit.
    /// Publishes to "resilience.bulkhead.create" with configuration.
    /// </summary>
    /// <param name="pluginId">The plugin to isolate.</param>
    /// <param name="maxConcurrency">Maximum concurrent operations allowed for the bulkhead.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RequestBulkheadIsolationAsync(string pluginId, int maxConcurrency, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (maxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be positive.");

        await _messageBus.PublishAsync("resilience.bulkhead.create", new PluginMessage
        {
            Type = "resilience.bulkhead.create",
            SourcePluginId = "com.datawarehouse.chaos.vaccination",
            Payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["pluginId"] = pluginId,
                ["maxConcurrency"] = maxConcurrency,
                ["source"] = "chaos-vaccination"
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests release of bulkhead isolation for the specified plugin.
    /// Publishes to "resilience.bulkhead.release".
    /// </summary>
    /// <param name="pluginId">The plugin whose bulkhead should be released.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RequestBulkheadReleaseAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _messageBus.PublishAsync("resilience.bulkhead.release", new PluginMessage
        {
            Type = "resilience.bulkhead.release",
            SourcePluginId = "com.datawarehouse.chaos.vaccination",
            Payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["pluginId"] = pluginId,
                ["source"] = "chaos-vaccination"
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the circuit breaker status for the specified plugin via request/response pattern.
    /// Publishes to "resilience.circuit-status.request" and awaits response on "resilience.circuit-status.response".
    /// </summary>
    /// <param name="pluginId">The plugin to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response message containing circuit breaker status, or null if timeout.</returns>
    public async Task<PluginMessage?> GetCircuitBreakerStatusAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<PluginMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingRequests[correlationId] = tcs;
        try
        {
            await _messageBus.PublishAsync("resilience.circuit-status.request", new PluginMessage
            {
                Type = "resilience.circuit-status.request",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                CorrelationId = correlationId,
                Payload = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["pluginId"] = pluginId
                }
            }, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_defaultTimeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout -- responder did not reply in time
                return null;
            }
        }
        finally
        {
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Gets health check results from the resilience infrastructure via request/response pattern.
    /// Publishes to "resilience.health-check.request" and awaits response on "resilience.health-check.response".
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The response message containing health check results, or null if timeout.</returns>
    public async Task<PluginMessage?> GetHealthCheckResultsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<PluginMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingRequests[correlationId] = tcs;
        try
        {
            await _messageBus.PublishAsync("resilience.health-check.request", new PluginMessage
            {
                Type = "resilience.health-check.request",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                CorrelationId = correlationId,
                Payload = new System.Collections.Generic.Dictionary<string, object>()
            }, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_defaultTimeout);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }
        finally
        {
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Handles response messages from the resilience infrastructure, completing pending request/response flows.
    /// </summary>
    private Task OnResponseReceived(PluginMessage message)
    {
        if (!string.IsNullOrEmpty(message.CorrelationId) &&
            _pendingRequests.TryRemove(message.CorrelationId, out var tcs))
        {
            tcs.TrySetResult(message);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes subscriptions and cancels pending requests.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); }
            catch { /* best effort */ }
        }
        _subscriptions.Clear();

        // Cancel all pending requests
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();
    }
}
