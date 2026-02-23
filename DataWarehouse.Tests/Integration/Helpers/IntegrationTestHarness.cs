using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Tests.Helpers;
using System.Collections.Concurrent;

namespace DataWarehouse.Tests.Integration.Helpers;

/// <summary>
/// Record of a single message that flowed through the TracingMessageBus.
/// Captures timestamp, topic, payload type, and the raw message for inspection.
/// </summary>
public sealed record MessageRecord
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Topic { get; init; } = string.Empty;
    public string PayloadType { get; init; } = string.Empty;
    public PluginMessage Message { get; init; } = new();
    public int SequenceNumber { get; init; }
}

/// <summary>
/// Message bus decorator that wraps a TestMessageBus and records every Publish/Subscribe
/// call with timestamp, topic, and payload type. Provides query methods to inspect the
/// message flow trace for integration test assertions.
/// </summary>
public sealed class TracingMessageBus : IMessageBus
{
    private readonly TestMessageBus _inner;
    private readonly ConcurrentBag<MessageRecord> _trace = new();
    private readonly ConcurrentDictionary<string, int> _subscriptionCounts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageRecord>> _waiters = new();
    private int _sequenceCounter;

    public TracingMessageBus()
    {
        _inner = new TestMessageBus();
    }

    public TracingMessageBus(TestMessageBus inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Get all published messages matching an optional topic filter.
    /// If topicFilter is null/empty, returns all messages.
    /// Supports prefix matching with trailing '*' (e.g., "storage.*").
    /// </summary>
    public IReadOnlyList<MessageRecord> GetPublishedMessages(string? topicFilter = null)
    {
        var all = _trace.OrderBy(m => m.SequenceNumber).ToList();
        if (string.IsNullOrEmpty(topicFilter))
            return all.AsReadOnly();

        if (topicFilter.EndsWith("*"))
        {
            var prefix = topicFilter[..^1];
            return all.Where(m => m.Topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                       .ToList().AsReadOnly();
        }

        return all.Where(m => m.Topic.Equals(topicFilter, StringComparison.OrdinalIgnoreCase))
                   .ToList().AsReadOnly();
    }

    /// <summary>
    /// Get the number of active subscriptions for a given topic.
    /// </summary>
    public int GetSubscriptionCount(string topic)
    {
        return _subscriptionCounts.TryGetValue(topic, out var count) ? count : 0;
    }

    /// <summary>
    /// Wait for a message on the specified topic within the given timeout.
    /// Returns the MessageRecord when received, or throws TimeoutException.
    /// </summary>
    public async Task<MessageRecord> WaitForMessage(string topic, TimeSpan timeout)
    {
        var tcs = _waiters.GetOrAdd(topic, _ => new TaskCompletionSource<MessageRecord>());
        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetException(
            new TimeoutException($"No message received on topic '{topic}' within {timeout.TotalMilliseconds}ms")));
        return await tcs.Task;
    }

    /// <summary>
    /// Get the complete ordered message flow trace.
    /// </summary>
    public IReadOnlyList<MessageRecord> GetMessageFlow()
    {
        return _trace.OrderBy(m => m.SequenceNumber).ToList().AsReadOnly();
    }

    /// <summary>
    /// Reset all recorded messages, subscription counts, and waiters.
    /// </summary>
    public void Reset()
    {
        while (_trace.TryTake(out _)) { }
        _subscriptionCounts.Clear();
        _waiters.Clear();
        _inner.Reset();
        Interlocked.Exchange(ref _sequenceCounter, 0);
    }

    /// <summary>
    /// Get all unique topics that have been published to.
    /// </summary>
    public IReadOnlySet<string> GetPublishedTopics()
    {
        return _trace.Select(m => m.Topic).Distinct().ToHashSet();
    }

    private MessageRecord RecordMessage(string topic, PluginMessage message)
    {
        var record = new MessageRecord
        {
            Topic = topic,
            PayloadType = message.Payload?.GetType().Name ?? "null",
            Message = message,
            SequenceNumber = Interlocked.Increment(ref _sequenceCounter),
            Timestamp = DateTimeOffset.UtcNow
        };
        _trace.Add(record);

        // Notify waiters
        if (_waiters.TryRemove(topic, out var tcs))
        {
            tcs.TrySetResult(record);
        }

        return record;
    }

    // --- IMessageBus implementation ---

    public async Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        RecordMessage(topic, message);
        await _inner.PublishAsync(topic, message, ct);
    }

    public async Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        RecordMessage(topic, message);
        await _inner.PublishAndWaitAsync(topic, message, ct);
    }

    public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        RecordMessage(topic, message);
        return await _inner.SendAsync(topic, message, ct);
    }

    public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default)
    {
        RecordMessage(topic, message);
        return await _inner.SendAsync(topic, message, timeout, ct);
    }

    public IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler)
    {
        _subscriptionCounts.AddOrUpdate(topic, 1, (_, count) => count + 1);
        var sub = _inner.Subscribe(topic, handler);
        return new TracingSubscriptionHandle(() =>
        {
            _subscriptionCounts.AddOrUpdate(topic, 0, (_, count) => Math.Max(0, count - 1));
            sub.Dispose();
        });
    }

    public IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
    {
        _subscriptionCounts.AddOrUpdate(topic, 1, (_, count) => count + 1);
        var sub = _inner.Subscribe(topic, handler);
        return new TracingSubscriptionHandle(() =>
        {
            _subscriptionCounts.AddOrUpdate(topic, 0, (_, count) => Math.Max(0, count - 1));
            sub.Dispose();
        });
    }

    public IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler)
    {
        _subscriptionCounts.AddOrUpdate(pattern, 1, (_, count) => count + 1);
        var sub = _inner.SubscribePattern(pattern, handler);
        return new TracingSubscriptionHandle(() =>
        {
            _subscriptionCounts.AddOrUpdate(pattern, 0, (_, count) => Math.Max(0, count - 1));
            sub.Dispose();
        });
    }

    public void Unsubscribe(string topic)
    {
        _subscriptionCounts.TryRemove(topic, out _);
        _inner.Unsubscribe(topic);
    }

    public IEnumerable<string> GetActiveTopics()
    {
        return _inner.GetActiveTopics();
    }

    /// <summary>
    /// Access the underlying TestMessageBus for advanced setup (e.g., SetupResponse).
    /// </summary>
    public TestMessageBus Inner => _inner;

    private sealed class TracingSubscriptionHandle : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public TracingSubscriptionHandle(Action onDispose) => _onDispose = onDispose;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }
}

/// <summary>
/// Minimal plugin host that can load and initialize specific plugins in isolation
/// with a TracingMessageBus. Used for integration tests that need to verify
/// message flow between plugin stages.
/// </summary>
public sealed class TestPluginHost : IDisposable
{
    private readonly TracingMessageBus _bus;
    private readonly TestConfigurationProvider _config;
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    public TestPluginHost(TracingMessageBus bus, TestConfigurationProvider? config = null)
    {
        _bus = bus;
        _config = config ?? new TestConfigurationProvider();
    }

    /// <summary>
    /// The tracing message bus used by this host.
    /// </summary>
    public TracingMessageBus Bus => _bus;

    /// <summary>
    /// The configuration provider used by this host.
    /// </summary>
    public TestConfigurationProvider Config => _config;

    /// <summary>
    /// Register a handler for a topic on the bus (simulates a plugin subscribing).
    /// </summary>
    public void RegisterHandler(string topic, Func<PluginMessage, Task> handler)
    {
        _subscriptions.Add(_bus.Subscribe(topic, handler));
    }

    /// <summary>
    /// Register a handler for a topic with response capability.
    /// </summary>
    public void RegisterHandler(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
    {
        _subscriptions.Add(_bus.Subscribe(topic, handler));
    }

    /// <summary>
    /// Simulate publishing a message (as if a plugin published it).
    /// </summary>
    public Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        return _bus.PublishAndWaitAsync(topic, message, ct);
    }

    /// <summary>
    /// Shutdown and cleanup all subscriptions.
    /// </summary>
    public void Shutdown()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }
}

/// <summary>
/// In-memory storage backend for integration tests.
/// Implements basic key-value storage with read/write/delete/list operations.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class TestStorageBackend
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    public Task WriteAsync(string key, byte[] data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _store[key] = data;
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _store.TryGetValue(key, out var data);
        return Task.FromResult(data);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_store.TryRemove(key, out _));
    }

    public Task<IReadOnlyList<string>> ListAsync(string? prefix = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<string> keys = string.IsNullOrEmpty(prefix)
            ? _store.Keys.ToList().AsReadOnly()
            : _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToList().AsReadOnly();
        return Task.FromResult(keys);
    }

    public bool Exists(string key) => _store.ContainsKey(key);
    public int Count => _store.Count;
    public void Clear() => _store.Clear();
}

/// <summary>
/// Test configuration provider that returns test configuration values.
/// Supports per-test overrides via Set/Get methods.
/// </summary>
public sealed class TestConfigurationProvider
{
    private readonly ConcurrentDictionary<string, object> _values = new();

    /// <summary>
    /// Set a configuration value (supports per-test overrides).
    /// </summary>
    public void Set(string key, object value)
    {
        _values[key] = value;
    }

    /// <summary>
    /// Get a configuration value, returning the default if not set.
    /// </summary>
    public T Get<T>(string key, T defaultValue = default!)
    {
        if (_values.TryGetValue(key, out var value) && value is T typedValue)
            return typedValue;
        return defaultValue;
    }

    /// <summary>
    /// Get a string configuration value.
    /// </summary>
    public string GetString(string key, string defaultValue = "")
    {
        return Get(key, defaultValue);
    }

    /// <summary>
    /// Check if a configuration key exists.
    /// </summary>
    public bool HasKey(string key) => _values.ContainsKey(key);

    /// <summary>
    /// Remove a configuration override.
    /// </summary>
    public bool Remove(string key) => _values.TryRemove(key, out _);

    /// <summary>
    /// Clear all configuration overrides.
    /// </summary>
    public void Clear() => _values.Clear();
}
