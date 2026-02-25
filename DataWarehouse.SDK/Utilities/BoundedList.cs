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
    /// A thread-safe, bounded list that enforces a configurable maximum capacity.
    /// When the list is full and a new item is added, the oldest item (index 0) is removed
    /// to make room. Optionally auto-persists state via <see cref="IPluginStateStore"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    public sealed class BoundedList<T> : IEnumerable<T>, IDisposable, IAsyncDisposable
    {
        private readonly List<T> _items;
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

        private readonly int _maxCapacity;

        // --- persistence ---
        private readonly IPluginStateStore? _stateStore;
        private readonly string? _pluginId;
        private readonly string? _stateKey;
        private Timer? _debounceTimer;
        private volatile bool _pendingPersist;

        private bool _disposed;

        // -------------------------------------------------------------------------
        // Constructor
        // -------------------------------------------------------------------------

        /// <summary>
        /// Initializes a new <see cref="BoundedList{T}"/> with the specified capacity.
        /// </summary>
        /// <param name="maxCapacity">Maximum number of elements. Must be greater than zero.</param>
        /// <param name="stateStore">Optional state store for auto-persistence.</param>
        /// <param name="pluginId">Plugin identifier used as namespace in the state store.</param>
        /// <param name="stateKey">State key scoping persisted data within the plugin namespace.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxCapacity"/> is less than 1.</exception>
        public BoundedList(
            int maxCapacity,
            IPluginStateStore? stateStore = null,
            string? pluginId = null,
            string? stateKey = null)
        {
            if (maxCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCapacity), "Capacity must be at least 1.");

            _maxCapacity = maxCapacity;
            _items = new List<T>(maxCapacity);
            _stateStore = stateStore;
            _pluginId = pluginId;
            _stateKey = stateKey;

            if (PersistenceEnabled)
                _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        // -------------------------------------------------------------------------
        // Properties
        // -------------------------------------------------------------------------

        /// <summary>Gets the configured maximum capacity of this list.</summary>
        public int MaxCapacity => _maxCapacity;

        /// <summary>Gets the number of elements currently in the list.</summary>
        public int Count
        {
            get
            {
                _lock.EnterReadLock();
                try { return _items.Count; }
                finally { _lock.ExitReadLock(); }
            }
        }

        /// <summary>
        /// Gets the element at the specified index.
        /// </summary>
        /// <param name="index">Zero-based index.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when index is out of range.</exception>
        public T this[int index]
        {
            get
            {
                _lock.EnterReadLock();
                try { return _items[index]; }
                finally { _lock.ExitReadLock(); }
            }
        }

        // -------------------------------------------------------------------------
        // Mutating operations
        // -------------------------------------------------------------------------

        /// <summary>
        /// Adds an item to the end of the list. If the list is at capacity, the oldest
        /// item (index 0) is removed first.
        /// </summary>
        /// <param name="item">The item to add.</param>
        public void Add(T item)
        {
            _lock.EnterWriteLock();
            try
            {
                if (_items.Count >= _maxCapacity)
                    _items.RemoveAt(0);
                _items.Add(item);
            }
            finally { _lock.ExitWriteLock(); }
            SchedulePersist();
        }

        /// <summary>
        /// Removes the first occurrence of the specified item from the list.
        /// </summary>
        /// <param name="item">The item to remove.</param>
        /// <returns><c>true</c> if the item was found and removed; otherwise <c>false</c>.</returns>
        public bool Remove(T item)
        {
            bool removed;
            _lock.EnterWriteLock();
            try { removed = _items.Remove(item); }
            finally { _lock.ExitWriteLock(); }
            if (removed) SchedulePersist();
            return removed;
        }

        /// <summary>
        /// Removes all items from the list.
        /// </summary>
        public void Clear()
        {
            _lock.EnterWriteLock();
            try { _items.Clear(); }
            finally { _lock.ExitWriteLock(); }
            SchedulePersist();
        }

        // -------------------------------------------------------------------------
        // Query operations
        // -------------------------------------------------------------------------

        /// <summary>
        /// Determines whether the list contains the specified item.
        /// </summary>
        /// <param name="item">The item to locate.</param>
        /// <returns><c>true</c> if the item is found; otherwise <c>false</c>.</returns>
        public bool Contains(T item)
        {
            _lock.EnterReadLock();
            try { return _items.Contains(item); }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>
        /// Returns a read-only snapshot of all elements in the list.
        /// </summary>
        public IReadOnlyList<T> ToList()
        {
            _lock.EnterReadLock();
            try { return _items.ToArray(); }
            finally { _lock.ExitReadLock(); }
        }

        // -------------------------------------------------------------------------
        // Persistence
        // -------------------------------------------------------------------------

        private bool PersistenceEnabled =>
            _stateStore != null && _pluginId != null && _stateKey != null;

        /// <summary>
        /// Immediately persists the current list state to the configured state store.
        /// No-op if no state store is configured.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task PersistAsync(CancellationToken ct = default)
        {
            if (!PersistenceEnabled) return;

            T[] snapshot;
            _lock.EnterReadLock();
            try { snapshot = _items.ToArray(); }
            finally { _lock.ExitReadLock(); }

            var json = JsonSerializer.Serialize(snapshot);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _stateStore!.SaveAsync(_pluginId!, _stateKey!, bytes, ct).ConfigureAwait(false);
            _pendingPersist = false;
        }

        /// <summary>
        /// Loads previously persisted state from the configured state store.
        /// Loaded items are appended, respecting capacity (oldest dropped if over limit).
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

            _lock.EnterWriteLock();
            try
            {
                _items.Clear();
                foreach (var item in items)
                {
                    if (_items.Count >= _maxCapacity)
                        _items.RemoveAt(0);
                    _items.Add(item);
                }
            }
            finally { _lock.ExitWriteLock(); }
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
            _lock.EnterReadLock();
            T[] snapshot;
            try { snapshot = _items.ToArray(); }
            finally { _lock.ExitReadLock(); }
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

            _lock.Dispose();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            // Flush pending persist synchronously on dispose — Dispose() is synchronous.
            // Task.Run avoids deadlocks on synchronization-context-bound threads.
            // Prefer DisposeAsync() for callers that can await.
            if (_pendingPersist && PersistenceEnabled)
            {
                try { Task.Run(() => PersistAsync()).ConfigureAwait(false).GetAwaiter().GetResult(); }
                catch { /* Best-effort */ }
            }

            _lock.Dispose();
        }
    }
}
