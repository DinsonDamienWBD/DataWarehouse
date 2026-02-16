using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AdaptiveTransport.BandwidthMonitor;

/// <summary>
/// Thread-safe priority queue for synchronization operations.
/// Operations are dequeued in priority order, with ties broken by creation time (FIFO).
/// </summary>
public sealed class SyncPriorityQueue
{
    private readonly object _lock = new();
    private readonly Dictionary<SyncPriority, Queue<SyncOperation>> _queues = new()
    {
        [SyncPriority.Critical] = new Queue<SyncOperation>(),
        [SyncPriority.High] = new Queue<SyncOperation>(),
        [SyncPriority.Normal] = new Queue<SyncOperation>(),
        [SyncPriority.Low] = new Queue<SyncOperation>(),
        [SyncPriority.Background] = new Queue<SyncOperation>()
    };

    /// <summary>
    /// Gets the total number of operations in the queue.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queues.Values.Sum(q => q.Count);
            }
        }
    }

    /// <summary>
    /// Adds a synchronization operation to the queue.
    /// </summary>
    /// <param name="operation">The operation to enqueue.</param>
    public void Enqueue(SyncOperation operation)
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        lock (_lock)
        {
            _queues[operation.Priority].Enqueue(operation);
        }
    }

    /// <summary>
    /// Attempts to remove and return the highest priority operation from the queue.
    /// </summary>
    /// <param name="operation">When this method returns, contains the dequeued operation, or null if the queue is empty.</param>
    /// <returns>True if an operation was dequeued; otherwise, false.</returns>
    public bool TryDequeue(out SyncOperation? operation)
    {
        lock (_lock)
        {
            // Try each priority level in order
            foreach (var priority in GetPriorityOrder())
            {
                if (_queues[priority].Count > 0)
                {
                    operation = _queues[priority].Dequeue();
                    return true;
                }
            }

            operation = null;
            return false;
        }
    }

    /// <summary>
    /// Returns the highest priority operation without removing it.
    /// </summary>
    /// <returns>The highest priority operation, or null if the queue is empty.</returns>
    public SyncOperation? Peek()
    {
        lock (_lock)
        {
            foreach (var priority in GetPriorityOrder())
            {
                if (_queues[priority].Count > 0)
                {
                    return _queues[priority].Peek();
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Gets the count of operations for each priority level.
    /// </summary>
    /// <returns>Dictionary mapping priority levels to their operation counts.</returns>
    public Dictionary<SyncPriority, int> CountByPriority()
    {
        lock (_lock)
        {
            return _queues.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Count);
        }
    }

    private static IEnumerable<SyncPriority> GetPriorityOrder()
    {
        // Return priorities from highest to lowest
        yield return SyncPriority.Critical;
        yield return SyncPriority.High;
        yield return SyncPriority.Normal;
        yield return SyncPriority.Low;
        yield return SyncPriority.Background;
    }
}
