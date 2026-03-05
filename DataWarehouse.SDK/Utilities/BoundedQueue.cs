using DataWarehouse.SDK.Contracts.Persistence;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Utilities
{
    /// <summary>
    /// A thread-safe, bounded queue that enforces a configurable maximum capacity.
    /// When the queue is full and a new item is enqueued, the oldest item is dropped
    /// (drop-oldest policy). Optionally auto-persists state via <see cref="IPluginStateStore"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    public sealed class BoundedQueue<T> : IEnumerable<T>, IDisposable, IAsyncDisposable
    {
        private readonly Queue<T> _queue;
        private readonly object _syncRoot = new();

        private readonly int _maxCapacity;

        // --- persistence ---
        private readonly IPluginStateStore? _stateStore;
        private readonly string? _pluginId;
        private readonly string? _stateKey;
        private Timer? _debounceTimer;
        private volatile bool _pendingPersist;

        private bool _disposed;

        // -------------------------------------------------------------------------
        // Events
        // -------------------------------------------------------------------------

        /// <summary>
        /// Fired when an item is dropped from the front of the queue due to capacity enforcement.
        /// </summary>
        public event Action<T>? OnDropped;

        // -------------------------------------------------------------------------
        // Constructor
        // -------------------------------------------------------------------------

        /// <summary>
        /// Initializes a new <see cref="BoundedQueue{T}"/> with the specified capacity.
        /// </summary>
        /// <param name="maxCapacity">Maximum number of elements. Must be greater than zero.</param>
        /// <param name="stateStore">Optional state store for auto-persistence.</param>
        /// <param name="pluginId">Plugin identifier used as namespace in the state store.</param>
        /// <param name="stateKey">State key scoping persisted data within the plugin namespace.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxCapacity"/> is less than 1.</exception>
        public BoundedQueue(
            int maxCapacity,
            IPluginStateStore? stateStore = null,
            string? pluginId = null,
            string? stateKey = null)
        {
            if (maxCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCapacity), "Capacity must be at least 1.");

            _maxCapacity = maxCapacity;
            _queue = new Queue<T>(maxCapacity);
            _stateStore = stateStore;
            _pluginId = pluginId;
            _stateKey = stateKey;

            if (PersistenceEnabled)
                _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        // -------------------------------------------------------------------------
        // Properties
        // -------------------------------------------------------------------------

        /// <summary>Gets the configured maximum capacity of this queue.</summary>
        public int MaxCapacity => _maxCapacity;

        /// <summary>Gets the number of elements currently in the queue.</summary>
        public int Count
        {
            get
            {
                lock (_syncRoot) { return _queue.Count; }
            }
        }

        // -------------------------------------------------------------------------
        // Mutating operations
        // -------------------------------------------------------------------------

        /// <summary>
        /// Adds an item to the back of the queue. If the queue is at capacity, the
        /// oldest item (front) is dequeued and the <see cref="OnDropped"/> event is fired.
        /// </summary>
        /// <param name="item">The item to enqueue.</param>
        public void Enqueue(T item)
        {
            T? dropped = default;
            bool hadDrop = false;

            lock (_syncRoot)
            {
                if (_queue.Count >= _maxCapacity)
                {
                    dropped = _queue.Dequeue();
                    hadDrop = true;
                }
                _queue.Enqueue(item);
            }

            if (hadDrop) OnDropped?.Invoke(dropped!);
            SchedulePersist();
        }

        /// <summary>
        /// Attempts to remove and return the item at the front of the queue.
        /// </summary>
        /// <param name="item">When this method returns, contains the dequeued item if successful.</param>
        /// <returns><c>true</c> if an item was dequeued; <c>false</c> if the queue was empty.</returns>
        public bool TryDequeue(out T item)
        {
            bool success;
            lock (_syncRoot)
            {
                success = _queue.TryDequeue(out item!);
            }
            if (success) SchedulePersist();
            return success;
        }

        /// <summary>
        /// Attempts to return the item at the front of the queue without removing it.
        /// </summary>
        /// <param name="item">When this method returns, contains the front item if the queue is non-empty.</param>
        /// <returns><c>true</c> if an item was peeked; <c>false</c> if the queue was empty.</returns>
        public bool TryPeek(out T item)
        {
            lock (_syncRoot)
            {
                return _queue.TryPeek(out item!);
            }
        }

        /// <summary>
        /// Removes all items from the queue.
        /// </summary>
        public void Clear()
        {
            lock (_syncRoot) { _queue.Clear(); }
            SchedulePersist();
        }

        // -------------------------------------------------------------------------
        // Query operations
        // -------------------------------------------------------------------------

        /// <summary>
        /// Returns a snapshot of all elements in the queue as an array (front to back).
        /// </summary>
        public T[] ToArray()
        {
            lock (_syncRoot) { return _queue.ToArray(); }
        }

        // -------------------------------------------------------------------------
        // Persistence
        // -------------------------------------------------------------------------

        private bool PersistenceEnabled =>
            _stateStore != null && _pluginId != null && _stateKey != null;

        /// <summary>
        /// Immediately persists the current queue state to the configured state store.
        /// No-op if no state store is configured.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task PersistAsync(CancellationToken ct = default)
        {
            if (!PersistenceEnabled) return;

            // Clear flag before save to avoid lost-persist race
            _pendingPersist = false;

            T[] snapshot;
            lock (_syncRoot) { snapshot = _queue.ToArray(); }

            var json = JsonSerializer.Serialize(snapshot);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _stateStore!.SaveAsync(_pluginId!, _stateKey!, bytes, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads previously persisted state from the configured state store.
        /// The queue is replaced with the persisted snapshot (capacity limits still apply).
        /// No-op if no state store is configured or no state exists.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task LoadPersistedAsync(CancellationToken ct = default)
        {
            if (!PersistenceEnabled) return;

            var bytes = await _stateStore!.LoadAsync(_pluginId!, _stateKey!, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return;

            var json = Encoding.UTF8.GetString(bytes);
            var items = JsonSerializer.Deserialize<T[]>(json);
            if (items is null) return;

            lock (_syncRoot)
            {
                _queue.Clear();
                foreach (var item in items)
                {
                    // Respect capacity — drop oldest if needed
                    if (_queue.Count >= _maxCapacity)
                        _queue.Dequeue();
                    _queue.Enqueue(item);
                }
            }
        }

        private void SchedulePersist()
        {
            if (!PersistenceEnabled) return;
            _pendingPersist = true;
            _debounceTimer?.Change(BoundedCollectionConstants.PersistDebounceInterval, Timeout.InfiniteTimeSpan);
        }

        private void OnDebounceElapsed(object? _)
        {
            if (!_pendingPersist || _disposed) return;
            _ = PersistAsync().ContinueWith(t =>
            {
                // Best-effort persistence — swallow exceptions to avoid crashing plugin threads
            }, TaskScheduler.Default);
        }

        // -------------------------------------------------------------------------
        // IEnumerable
        // -------------------------------------------------------------------------

        /// <inheritdoc/>
        public IEnumerator<T> GetEnumerator()
        {
            T[] snapshot;
            lock (_syncRoot) { snapshot = _queue.ToArray(); }
            return ((IEnumerable<T>)snapshot).GetEnumerator();
        }

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // -------------------------------------------------------------------------
        // IDisposable / IAsyncDisposable
        // -------------------------------------------------------------------------

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            if (_pendingPersist && PersistenceEnabled)
            {
                try { await PersistAsync().ConfigureAwait(false); }
                catch { /* Best-effort */ }
            }

            // Queue uses lock, no unmanaged resource to dispose
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            // Best-effort flush — do NOT block (sync-over-async deadlocks threadpool-starved callers).
            // Prefer DisposeAsync() for guaranteed flush. Dispose() is a fire-and-forget fallback.
            if (_pendingPersist && PersistenceEnabled)
            {
                _ = Task.Run(() => PersistAsync());
            }

            // Queue uses lock, no unmanaged resource to dispose
        }
    }
}
