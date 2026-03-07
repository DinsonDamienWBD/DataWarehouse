using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;

namespace DataWarehouse.Hardening.Tests.Chaos.MessageBus;

/// <summary>
/// Chaos engineering proxy that decorates a real <see cref="IMessageBus"/> implementation
/// with configurable disruption modes: message loss, duplication, and reordering.
/// Thread-safe for concurrent publishers and subscribers.
///
/// Used by chaos tests to prove the system handles message bus faults without data corruption.
/// </summary>
public sealed class ChaosMessageBusProxy : IMessageBus
{
    private readonly IMessageBus _inner;
    private readonly Random _random;
    private readonly Lock _reorderLock = new();
    private readonly List<(string Topic, PluginMessage Message, CancellationToken Ct)> _reorderBuffer = new();

    /// <summary>Chaos disruption modes.</summary>
    public enum ChaosMode
    {
        /// <summary>Pass all messages through unchanged.</summary>
        Normal,
        /// <summary>Drop messages with configured probability.</summary>
        DropMessages,
        /// <summary>Duplicate messages with configured probability.</summary>
        DuplicateMessages,
        /// <summary>Buffer messages and deliver in random order.</summary>
        ReorderMessages,
        /// <summary>Apply all three disruptions simultaneously.</summary>
        Combined
    }

    /// <summary>Current disruption mode.</summary>
    public ChaosMode Mode { get; set; } = ChaosMode.Normal;

    /// <summary>Probability [0.0, 1.0] of dropping a message in DropMessages mode.</summary>
    public double DropRate { get; set; } = 0.5;

    /// <summary>Probability [0.0, 1.0] of duplicating a message in DuplicateMessages mode.</summary>
    public double DuplicateRate { get; set; } = 0.5;

    /// <summary>Number of messages to buffer before shuffling and delivering in ReorderMessages mode.</summary>
    public int ReorderBufferSize { get; set; } = 5;

    // --- Statistics ---
    private long _messagesPublished;
    private long _messagesDropped;
    private long _messagesDuplicated;
    private long _messagesReordered;
    private long _messagesDelivered;

    /// <summary>Total messages received for publishing.</summary>
    public long MessagesPublished => Interlocked.Read(ref _messagesPublished);

    /// <summary>Messages intentionally dropped.</summary>
    public long MessagesDropped => Interlocked.Read(ref _messagesDropped);

    /// <summary>Messages intentionally duplicated (extra copies sent).</summary>
    public long MessagesDuplicated => Interlocked.Read(ref _messagesDuplicated);

    /// <summary>Messages that went through the reorder buffer.</summary>
    public long MessagesReordered => Interlocked.Read(ref _messagesReordered);

    /// <summary>Messages successfully delivered to the inner bus.</summary>
    public long MessagesDelivered => Interlocked.Read(ref _messagesDelivered);

    /// <summary>
    /// Creates a new chaos proxy wrapping the specified message bus.
    /// </summary>
    /// <param name="inner">The real message bus to decorate.</param>
    /// <param name="seed">Optional random seed for reproducible chaos.</param>
    public ChaosMessageBusProxy(IMessageBus inner, int? seed = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    /// <summary>Reset all statistics counters to zero.</summary>
    public void ResetStats()
    {
        Interlocked.Exchange(ref _messagesPublished, 0);
        Interlocked.Exchange(ref _messagesDropped, 0);
        Interlocked.Exchange(ref _messagesDuplicated, 0);
        Interlocked.Exchange(ref _messagesReordered, 0);
        Interlocked.Exchange(ref _messagesDelivered, 0);
    }

    /// <summary>Flush any buffered reorder messages immediately.</summary>
    public async Task FlushReorderBufferAsync()
    {
        List<(string Topic, PluginMessage Message, CancellationToken Ct)> toFlush;
        lock (_reorderLock)
        {
            toFlush = new List<(string, PluginMessage, CancellationToken)>(_reorderBuffer);
            _reorderBuffer.Clear();
        }

        // Shuffle before delivery
        Shuffle(toFlush);

        foreach (var (topic, message, ct) in toFlush)
        {
            Interlocked.Increment(ref _messagesReordered);
            Interlocked.Increment(ref _messagesDelivered);
            await _inner.PublishAsync(topic, message, ct);
        }
    }

    public async Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _messagesPublished);

        switch (Mode)
        {
            case ChaosMode.Normal:
                Interlocked.Increment(ref _messagesDelivered);
                await _inner.PublishAsync(topic, message, ct);
                break;

            case ChaosMode.DropMessages:
                await HandleDropAsync(topic, message, ct);
                break;

            case ChaosMode.DuplicateMessages:
                await HandleDuplicateAsync(topic, message, ct);
                break;

            case ChaosMode.ReorderMessages:
                await HandleReorderAsync(topic, message, ct);
                break;

            case ChaosMode.Combined:
                await HandleCombinedAsync(topic, message, ct);
                break;
        }
    }

    public async Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _messagesPublished);

        // PublishAndWait always delivers (used for critical operations) but may duplicate
        if (Mode == ChaosMode.DuplicateMessages || Mode == ChaosMode.Combined)
        {
            bool shouldDuplicate;
            lock (_reorderLock) { shouldDuplicate = _random.NextDouble() < DuplicateRate; }

            Interlocked.Increment(ref _messagesDelivered);
            await _inner.PublishAndWaitAsync(topic, message, ct);

            if (shouldDuplicate)
            {
                Interlocked.Increment(ref _messagesDuplicated);
                Interlocked.Increment(ref _messagesDelivered);
                await _inner.PublishAndWaitAsync(topic, message, ct);
            }
        }
        else
        {
            Interlocked.Increment(ref _messagesDelivered);
            await _inner.PublishAndWaitAsync(topic, message, ct);
        }
    }

    public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _messagesPublished);

        // SendAsync (request/response) - drop returns error, duplicate sends twice but returns first
        if ((Mode == ChaosMode.DropMessages || Mode == ChaosMode.Combined))
        {
            bool shouldDrop;
            lock (_reorderLock) { shouldDrop = _random.NextDouble() < DropRate; }

            if (shouldDrop)
            {
                Interlocked.Increment(ref _messagesDropped);
                return MessageResponse.Error("Message dropped by chaos proxy", "CHAOS_DROP");
            }
        }

        Interlocked.Increment(ref _messagesDelivered);
        return await _inner.SendAsync(topic, message, ct);
    }

    public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _messagesPublished);

        if ((Mode == ChaosMode.DropMessages || Mode == ChaosMode.Combined))
        {
            bool shouldDrop;
            lock (_reorderLock) { shouldDrop = _random.NextDouble() < DropRate; }

            if (shouldDrop)
            {
                Interlocked.Increment(ref _messagesDropped);
                return MessageResponse.Error("Message dropped by chaos proxy", "CHAOS_DROP");
            }
        }

        Interlocked.Increment(ref _messagesDelivered);
        return await _inner.SendAsync(topic, message, timeout, ct);
    }

    public IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler) =>
        _inner.Subscribe(topic, handler);

    public IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler) =>
        _inner.Subscribe(topic, handler);

    public IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler) =>
        _inner.SubscribePattern(pattern, handler);

    public void Unsubscribe(string topic) => _inner.Unsubscribe(topic);

    public IEnumerable<string> GetActiveTopics() => _inner.GetActiveTopics();

    // --- Private chaos handlers ---

    private async Task HandleDropAsync(string topic, PluginMessage message, CancellationToken ct)
    {
        bool shouldDrop;
        lock (_reorderLock) { shouldDrop = _random.NextDouble() < DropRate; }

        if (shouldDrop)
        {
            Interlocked.Increment(ref _messagesDropped);
            return; // Message silently dropped
        }

        Interlocked.Increment(ref _messagesDelivered);
        await _inner.PublishAsync(topic, message, ct);
    }

    private async Task HandleDuplicateAsync(string topic, PluginMessage message, CancellationToken ct)
    {
        Interlocked.Increment(ref _messagesDelivered);
        await _inner.PublishAsync(topic, message, ct);

        bool shouldDuplicate;
        lock (_reorderLock) { shouldDuplicate = _random.NextDouble() < DuplicateRate; }

        if (shouldDuplicate)
        {
            Interlocked.Increment(ref _messagesDuplicated);
            Interlocked.Increment(ref _messagesDelivered);
            await _inner.PublishAsync(topic, message, ct);
        }
    }

    private async Task HandleReorderAsync(string topic, PluginMessage message, CancellationToken ct)
    {
        bool shouldFlush;
        lock (_reorderLock)
        {
            _reorderBuffer.Add((topic, message, ct));
            shouldFlush = _reorderBuffer.Count >= ReorderBufferSize;
        }

        if (shouldFlush)
        {
            await FlushReorderBufferAsync();
        }
    }

    private async Task HandleCombinedAsync(string topic, PluginMessage message, CancellationToken ct)
    {
        // Step 1: Check drop
        bool shouldDrop;
        lock (_reorderLock) { shouldDrop = _random.NextDouble() < DropRate; }

        if (shouldDrop)
        {
            Interlocked.Increment(ref _messagesDropped);
            return;
        }

        // Step 2: Check duplicate
        bool shouldDuplicate;
        lock (_reorderLock) { shouldDuplicate = _random.NextDouble() < DuplicateRate; }

        // Step 3: Check reorder
        bool shouldFlush;
        lock (_reorderLock)
        {
            _reorderBuffer.Add((topic, message, ct));
            if (shouldDuplicate)
            {
                _reorderBuffer.Add((topic, message, ct));
                Interlocked.Increment(ref _messagesDuplicated);
            }
            shouldFlush = _reorderBuffer.Count >= ReorderBufferSize;
        }

        if (shouldFlush)
        {
            await FlushReorderBufferAsync();
        }
    }

    private void Shuffle<T>(List<T> list)
    {
        lock (_reorderLock)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
