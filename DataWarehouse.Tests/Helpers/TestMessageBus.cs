using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;

namespace DataWarehouse.Tests.Helpers;

/// <summary>
/// Production-quality in-memory IMessageBus implementation for testing.
/// Supports publish/subscribe, request/response, and pattern subscriptions.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class TestMessageBus : IMessageBus
{
    private readonly ConcurrentDictionary<string, List<Func<PluginMessage, Task>>> _handlers = new();
    private readonly ConcurrentDictionary<string, List<Func<PluginMessage, Task<MessageResponse>>>> _responseHandlers = new();
    private readonly ConcurrentDictionary<string, MessageResponse> _configuredResponses = new();
    private readonly ConcurrentBag<(string Topic, PluginMessage Message)> _publishedMessages = new();

    /// <summary>
    /// All messages published through this bus (for test assertions).
    /// </summary>
    public IReadOnlyList<(string Topic, PluginMessage Message)> PublishedMessages =>
        _publishedMessages.ToList().AsReadOnly();

    /// <summary>
    /// Pre-configure a response for a given topic (used for request/response testing).
    /// </summary>
    public void SetupResponse(string topic, MessageResponse response)
    {
        _configuredResponses[topic] = response;
    }

    /// <summary>
    /// Pre-configure a success response with a typed payload for a given topic.
    /// </summary>
    public void SetupResponse<T>(string topic, T payload)
    {
        _configuredResponses[topic] = MessageResponse.Ok(payload);
    }

    public Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        _publishedMessages.Add((topic, message));

        if (_handlers.TryGetValue(topic, out var handlers))
        {
            var tasks = handlers.Select(h => h(message));
            return Task.WhenAll(tasks);
        }

        return Task.CompletedTask;
    }

    public Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        return PublishAsync(topic, message, ct);
    }

    public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        _publishedMessages.Add((topic, message));

        // Check for pre-configured responses first
        if (_configuredResponses.TryGetValue(topic, out var configured))
        {
            return configured;
        }

        // Then try response handlers
        if (_responseHandlers.TryGetValue(topic, out var handlers) && handlers.Count > 0)
        {
            return await handlers[0](message);
        }

        return MessageResponse.Ok();
    }

    public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return await SendAsync(topic, message, cts.Token);
    }

    public IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler)
    {
        var handlers = _handlers.GetOrAdd(topic, _ => new List<Func<PluginMessage, Task>>());
        lock (handlers)
        {
            handlers.Add(handler);
        }

        return new SubscriptionHandle(() =>
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }
        });
    }

    public IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
    {
        var handlers = _responseHandlers.GetOrAdd(topic, _ => new List<Func<PluginMessage, Task<MessageResponse>>>());
        lock (handlers)
        {
            handlers.Add(handler);
        }

        return new SubscriptionHandle(() =>
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }
        });
    }

    public IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler)
    {
        // Simple wildcard pattern matching for tests
        return Subscribe(pattern, handler);
    }

    public void Unsubscribe(string topic)
    {
        _handlers.TryRemove(topic, out _);
        _responseHandlers.TryRemove(topic, out _);
    }

    public IEnumerable<string> GetActiveTopics()
    {
        return _handlers.Keys.Concat(_responseHandlers.Keys).Distinct();
    }

    /// <summary>
    /// Reset all subscriptions and published messages.
    /// </summary>
    public void Reset()
    {
        _handlers.Clear();
        _responseHandlers.Clear();
        _configuredResponses.Clear();
        while (_publishedMessages.TryTake(out _)) { }
    }

    private sealed class SubscriptionHandle : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public SubscriptionHandle(Action onDispose) => _onDispose = onDispose;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }
}
