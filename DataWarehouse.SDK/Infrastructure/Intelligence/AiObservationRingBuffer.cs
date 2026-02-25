using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// An observation event emitted by a plugin for the AI observation pipeline.
/// Captures metric values, anomalies, or both from plugin runtime behavior.
/// </summary>
/// <param name="PluginId">The plugin that emitted this observation.</param>
/// <param name="MetricName">Name of the metric being observed.</param>
/// <param name="Value">The metric value.</param>
/// <param name="AnomalyType">Type of anomaly, or null for normal metrics.</param>
/// <param name="Description">Human-readable description, or null for normal metrics.</param>
/// <param name="Timestamp">When this observation was captured.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-01)")]
public sealed record ObservationEvent(
    string PluginId,
    string MetricName,
    double Value,
    string? AnomalyType,
    string? Description,
    DateTimeOffset Timestamp
);

/// <summary>
/// Lock-free ring buffer for AI observation events. Uses a power-of-two sized array
/// with CAS-based (Interlocked.CompareExchange) writes for zero contention on the hot path.
/// Single-producer-multiple-consumer is supported via TryRead/DrainTo on the consumer side.
/// </summary>
/// <remarks>
/// Design: AIPI-01 requires zero hot-path-impact observation collection.
/// The buffer drops observations when full rather than blocking the caller.
/// No lock, Monitor, Mutex, or SemaphoreSlim is used on the write path.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-01)")]
public sealed class AiObservationRingBuffer
{
    private readonly ObservationEvent?[] _buffer;
    private readonly int _mask;
    private long _head; // next write position (producers advance this)
    private long _tail; // next read position (consumer advances this)

    /// <summary>
    /// Creates a ring buffer with the specified capacity (rounded up to next power of two).
    /// </summary>
    /// <param name="capacity">Desired capacity. Will be rounded up to next power of two. Default 8192.</param>
    public AiObservationRingBuffer(int capacity = 8192)
    {
        if (capacity < 2) capacity = 2;
        int size = RoundUpToPowerOfTwo(capacity);
        _buffer = new ObservationEvent?[size];
        _mask = size - 1;
        _head = 0;
        _tail = 0;
    }

    /// <summary>
    /// The capacity of the ring buffer (always a power of two).
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Approximate number of items currently in the buffer.
    /// This is a snapshot and may be slightly stale due to concurrent operations.
    /// </summary>
    public int Count
    {
        get
        {
            long head = Volatile.Read(ref _head);
            long tail = Volatile.Read(ref _tail);
            return (int)(head - tail);
        }
    }

    /// <summary>
    /// Attempts to write an observation event to the ring buffer using lock-free CAS.
    /// Returns false if the buffer is full (observation is dropped, not blocked).
    /// </summary>
    /// <param name="evt">The observation event to write.</param>
    /// <returns>True if written successfully, false if buffer was full.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(ObservationEvent evt)
    {
        // CAS loop for lock-free multi-producer support
        while (true)
        {
            long currentHead = Volatile.Read(ref _head);
            long currentTail = Volatile.Read(ref _tail);

            // Check if buffer is full
            if (currentHead - currentTail >= _buffer.Length)
            {
                return false; // Drop — do not block
            }

            // Attempt to claim the slot via CAS
            long newHead = currentHead + 1;
            if (Interlocked.CompareExchange(ref _head, newHead, currentHead) == currentHead)
            {
                // We claimed slot at currentHead — write the event
                int index = (int)(currentHead & _mask);
                Volatile.Write(ref _buffer[index], evt);
                return true;
            }

            // CAS failed — another producer won. Retry.
        }
    }

    /// <summary>
    /// Attempts to read one observation event from the buffer (single consumer).
    /// </summary>
    /// <param name="evt">The observation event read, or null if buffer was empty.</param>
    /// <returns>True if an event was read, false if buffer was empty.</returns>
    public bool TryRead(out ObservationEvent? evt)
    {
        evt = null;

        long currentTail = Volatile.Read(ref _tail);
        long currentHead = Volatile.Read(ref _head);

        if (currentTail >= currentHead)
        {
            return false; // Empty
        }

        int index = (int)(currentTail & _mask);
        evt = Volatile.Read(ref _buffer[index]);

        if (evt is null)
        {
            // Producer claimed slot but hasn't written yet — treat as empty for now
            return false;
        }

        // Clear slot and advance tail
        Volatile.Write(ref _buffer[index], null);
        Volatile.Write(ref _tail, currentTail + 1);
        return true;
    }

    /// <summary>
    /// Drains up to <paramref name="maxCount"/> events from the buffer into the batch list.
    /// Designed for the consumer pipeline to batch-process observations.
    /// </summary>
    /// <param name="batch">The list to drain events into. Not cleared by this method.</param>
    /// <param name="maxCount">Maximum number of events to drain.</param>
    /// <returns>The number of events actually drained.</returns>
    public int DrainTo(List<ObservationEvent> batch, int maxCount)
    {
        int drained = 0;

        while (drained < maxCount)
        {
            if (TryRead(out var evt) && evt is not null)
            {
                batch.Add(evt);
                drained++;
            }
            else
            {
                break;
            }
        }

        return drained;
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value++;
        return value;
    }
}
