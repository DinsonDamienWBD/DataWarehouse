using DataWarehouse.SDK.Contracts.Persistence;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Utilities
{
    /// <summary>
    /// Shared constants for bounded collection types. Kept in a non-generic class
    /// to avoid Sonar S2743 (static field in generic type).
    /// </summary>
    internal static class BoundedCollectionConstants
    {
        /// <summary>How long to wait after the last modification before persisting state.</summary>
        internal static readonly TimeSpan PersistDebounceInterval = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// A thread-safe, bounded dictionary with LRU eviction and optional auto-persistence.
    /// Provides a drop-in replacement for <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
    /// with configurable maximum capacity. When the dictionary reaches capacity, the least-recently-used
    /// entry is evicted automatically.
    /// </summary>
    /// <typeparam name="TKey">The type of keys. Must be non-null.</typeparam>
    /// <typeparam name="TValue">The type of values.</typeparam>
    public sealed class BoundedDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IReadOnlyDictionary<TKey, TValue>, IDisposable, IAsyncDisposable
        where TKey : notnull
    {
        // --- backing store ---
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
        private readonly LinkedList<(TKey Key, TValue Value)> _lru;
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

        // --- capacity ---
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
        /// Fired when an entry is evicted due to LRU capacity enforcement.
        /// </summary>
        public event Action<TKey, TValue>? OnEvicted;

        // -------------------------------------------------------------------------
        // Constructors
        // -------------------------------------------------------------------------

        /// <summary>
        /// Initializes a new <see cref="BoundedDictionary{TKey,TValue}"/> with the specified capacity.
        /// </summary>
        /// <param name="maxCapacity">Maximum number of entries. Must be greater than zero.</param>
        /// <param name="stateStore">Optional state store for auto-persistence.</param>
        /// <param name="pluginId">Plugin identifier used as namespace in the state store.</param>
        /// <param name="stateKey">State key used to scope persisted data within the plugin namespace.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxCapacity"/> is less than 1.</exception>
        public BoundedDictionary(
            int maxCapacity,
            IPluginStateStore? stateStore = null,
            string? pluginId = null,
            string? stateKey = null)
        {
            if (maxCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCapacity), "Capacity must be at least 1.");

            _maxCapacity = maxCapacity;
            _map = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(maxCapacity);
            _lru = new LinkedList<(TKey Key, TValue Value)>();

            _stateStore = stateStore;
            _pluginId = pluginId;
            _stateKey = stateKey;

            if (PersistenceEnabled)
            {
                _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Initializes a new <see cref="BoundedDictionary{TKey,TValue}"/> with the specified capacity and key comparer.
        /// </summary>
        /// <param name="maxCapacity">Maximum number of entries. Must be greater than zero.</param>
        /// <param name="comparer">The equality comparer to use for keys.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxCapacity"/> is less than 1.</exception>
        public BoundedDictionary(int maxCapacity, IEqualityComparer<TKey> comparer)
        {
            if (maxCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCapacity), "Capacity must be at least 1.");

            _maxCapacity = maxCapacity;
            _map = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(maxCapacity, comparer);
            _lru = new LinkedList<(TKey Key, TValue Value)>();
        }

        // -------------------------------------------------------------------------
        // Properties
        // -------------------------------------------------------------------------

        /// <summary>Gets the configured maximum capacity of this dictionary.</summary>
        public int MaxCapacity => _maxCapacity;

        /// <summary>Gets the number of key-value pairs currently in the dictionary.</summary>
        public int Count
        {
            get
            {
                _lock.EnterReadLock();
                try { return _map.Count; }
                finally { _lock.ExitReadLock(); }
            }
        }

        /// <summary>Gets a snapshot of all keys currently in the dictionary.</summary>
        public IEnumerable<TKey> Keys
        {
            get
            {
                _lock.EnterReadLock();
                try { return new List<TKey>(_map.Keys); }
                finally { _lock.ExitReadLock(); }
            }
        }

        /// <summary>Gets a snapshot of all values currently in the dictionary.</summary>
        public IEnumerable<TValue> Values
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    var result = new List<TValue>(_map.Count);
                    foreach (var node in _map.Values)
                        result.Add(node.Value.Value);
                    return result;
                }
                finally { _lock.ExitReadLock(); }
            }
        }

        /// <summary>
        /// Gets or sets the value associated with the specified key.
        /// Getting a value promotes the entry to most-recently-used.
        /// Setting a value either updates an existing entry (promoting it) or adds a new one
        /// (evicting LRU if at capacity).
        /// </summary>
        /// <param name="key">The key to look up or set.</param>
        /// <exception cref="KeyNotFoundException">Thrown on get when the key does not exist.</exception>
        public TValue this[TKey key]
        {
            get
            {
                _lock.EnterWriteLock();
                try
                {
                    if (!_map.TryGetValue(key, out var node))
                        throw new KeyNotFoundException($"Key not found: {key}");
                    MoveToFront(node);
                    return node.Value.Value;
                }
                finally { _lock.ExitWriteLock(); }
            }
            set
            {
                _lock.EnterWriteLock();
                try
                {
                    if (_map.TryGetValue(key, out var existing))
                    {
                        _lru.Remove(existing);
                        var updated = _lru.AddFirst((key, value));
                        _map[key] = updated;
                    }
                    else
                    {
                        EnsureCapacity();
                        var node = _lru.AddFirst((key, value));
                        _map[key] = node;
                    }
                }
                finally { _lock.ExitWriteLock(); }
                SchedulePersist();
            }
        }

        // -------------------------------------------------------------------------
        // ConcurrentDictionary-compatible API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Attempts to add a key-value pair to the dictionary.
        /// </summary>
        /// <param name="key">The key to add.</param>
        /// <param name="value">The value to associate with the key.</param>
        /// <returns><c>true</c> if the pair was added; <c>false</c> if the key already exists.</returns>
        public bool TryAdd(TKey key, TValue value)
        {
            bool added;
            _lock.EnterWriteLock();
            try
            {
                if (_map.ContainsKey(key))
                {
                    added = false;
                }
                else
                {
                    EnsureCapacity();
                    var node = _lru.AddFirst((key, value));
                    _map[key] = node;
                    added = true;
                }
            }
            finally { _lock.ExitWriteLock(); }
            if (added) SchedulePersist();
            return added;
        }

        /// <summary>
        /// Attempts to get the value associated with the specified key.
        /// Promotes the entry to most-recently-used on success (write lock required for LRU update).
        /// Use <see cref="TryPeek"/> for read-only access without LRU promotion.
        /// </summary>
        /// <param name="key">The key to look up.</param>
        /// <param name="value">When this method returns, contains the associated value if found.</param>
        /// <returns><c>true</c> if the key was found; otherwise <c>false</c>.</returns>
        public bool TryGetValue(TKey key, out TValue value)
        {
            _lock.EnterWriteLock();
            try
            {
                if (_map.TryGetValue(key, out var node))
                {
                    MoveToFront(node);
                    value = node.Value.Value;
                    return true;
                }
                value = default!;
                return false;
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Reads the value associated with the specified key WITHOUT updating LRU order.
        /// Uses a read lock so concurrent peeks can proceed in parallel.
        /// Use this when LRU promotion is not desired (e.g., read-only scanning or metrics).
        /// </summary>
        /// <param name="key">The key to look up.</param>
        /// <param name="value">When this method returns, contains the associated value if found.</param>
        /// <returns><c>true</c> if the key was found; otherwise <c>false</c>.</returns>
        public bool TryPeek(TKey key, out TValue value)
        {
            _lock.EnterReadLock();
            try
            {
                if (_map.TryGetValue(key, out var node))
                {
                    value = node.Value.Value;
                    return true;
                }
                value = default!;
                return false;
            }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>
        /// Attempts to remove the value associated with the specified key.
        /// </summary>
        /// <param name="key">The key to remove.</param>
        /// <param name="value">When this method returns, contains the removed value if found.</param>
        /// <returns><c>true</c> if the key was found and removed; otherwise <c>false</c>.</returns>
        public bool TryRemove(TKey key, out TValue value)
        {
            bool removed;
            _lock.EnterWriteLock();
            try
            {
                if (_map.TryGetValue(key, out var node))
                {
                    value = node.Value.Value;
                    _lru.Remove(node);
                    _map.Remove(key);
                    removed = true;
                }
                else
                {
                    value = default!;
                    removed = false;
                }
            }
            finally { _lock.ExitWriteLock(); }
            if (removed) SchedulePersist();
            return removed;
        }

        /// <summary>
        /// Returns the existing value for the key, or adds and returns <paramref name="value"/>
        /// if the key does not exist. This overload matches the
        /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> simple-value overload.
        /// </summary>
        /// <param name="key">The key to look up or add.</param>
        /// <param name="value">The value to add if the key does not exist.</param>
        /// <returns>The existing or newly added value.</returns>
        public TValue GetOrAdd(TKey key, TValue value)
        {
            bool added;
            TValue result;
            _lock.EnterWriteLock();
            try
            {
                if (_map.TryGetValue(key, out var node))
                {
                    MoveToFront(node);
                    return node.Value.Value;
                }
                result = value;
                EnsureCapacity();
                var newNode = _lru.AddFirst((key, result));
                _map[key] = newNode;
                added = true;
            }
            finally { _lock.ExitWriteLock(); }
            if (added) SchedulePersist();
            return result;
        }

        /// <summary>
        /// Returns the existing value for the key, or adds and returns a new value produced by
        /// <paramref name="valueFactory"/> if the key does not exist.
        /// </summary>
        /// <param name="key">The key to look up or add.</param>
        /// <param name="valueFactory">Factory function invoked when the key does not exist.</param>
        /// <returns>The existing or newly added value.</returns>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (valueFactory is null) throw new ArgumentNullException(nameof(valueFactory));
            bool added;
            TValue result;
            _lock.EnterWriteLock();
            try
            {
                if (_map.TryGetValue(key, out var node))
                {
                    MoveToFront(node);
                    return node.Value.Value;
                }
                result = valueFactory(key);
                EnsureCapacity();
                var newNode = _lru.AddFirst((key, result));
                _map[key] = newNode;
                added = true;
            }
            finally { _lock.ExitWriteLock(); }
            if (added) SchedulePersist();
            return result;
        }

        /// <summary>
        /// Adds a key/value pair, or updates the value of an existing key using
        /// <paramref name="updateValueFactory"/>.
        /// </summary>
        /// <param name="key">The key to add or update.</param>
        /// <param name="addValue">Value to add if the key does not exist.</param>
        /// <param name="updateValueFactory">Factory invoked with the key and existing value when the key exists.</param>
        /// <returns>The new value for the key (either added or updated).</returns>
        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (updateValueFactory is null) throw new ArgumentNullException(nameof(updateValueFactory));
            TValue result;
            _lock.EnterWriteLock();
            try
            {
                if (_map.TryGetValue(key, out var node))
                {
                    result = updateValueFactory(key, node.Value.Value);
                    _lru.Remove(node);
                    var updated = _lru.AddFirst((key, result));
                    _map[key] = updated;
                }
                else
                {
                    result = addValue;
                    EnsureCapacity();
                    var newNode = _lru.AddFirst((key, result));
                    _map[key] = newNode;
                }
            }
            finally { _lock.ExitWriteLock(); }
            SchedulePersist();
            return result;
        }

        /// <summary>
        /// Adds a key/value pair, or updates the value of an existing key. The add value is produced
        /// by <paramref name="addValueFactory"/> when the key does not exist.
        /// This overload matches <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
        /// factory-based AddOrUpdate.
        /// </summary>
        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (addValueFactory is null) throw new ArgumentNullException(nameof(addValueFactory));
            if (updateValueFactory is null) throw new ArgumentNullException(nameof(updateValueFactory));
            TValue result;
            _lock.EnterWriteLock();
            try
            {
                if (_map.TryGetValue(key, out var node))
                {
                    result = updateValueFactory(key, node.Value.Value);
                    _lru.Remove(node);
                    var updated = _lru.AddFirst((key, result));
                    _map[key] = updated;
                }
                else
                {
                    result = addValueFactory(key);
                    EnsureCapacity();
                    var newNode = _lru.AddFirst((key, result));
                    _map[key] = newNode;
                }
            }
            finally { _lock.ExitWriteLock(); }
            SchedulePersist();
            return result;
        }

        /// <summary>
        /// Determines whether the dictionary contains the specified key.
        /// </summary>
        /// <param name="key">The key to check.</param>
        /// <returns><c>true</c> if the key exists; otherwise <c>false</c>.</returns>
        public bool ContainsKey(TKey key)
        {
            _lock.EnterReadLock();
            try { return _map.ContainsKey(key); }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>
        /// Attempts to update the value associated with <paramref name="key"/>
        /// only if the current value equals <paramref name="comparisonValue"/>.
        /// This is the atomic compare-and-swap equivalent of
        /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryUpdate"/>.
        /// </summary>
        /// <param name="key">The key to update.</param>
        /// <param name="newValue">The new value to set.</param>
        /// <param name="comparisonValue">The value to compare against the current value.</param>
        /// <returns><c>true</c> if the value was updated; <c>false</c> if the key was not found or comparison failed.</returns>
        public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_map.TryGetValue(key, out var node))
                    return false;

                // Compare using default equality
                if (!EqualityComparer<TValue>.Default.Equals(node.Value.Value, comparisonValue))
                    return false;

                _lru.Remove(node);
                var updated = _lru.AddFirst((key, newValue));
                _map[key] = updated;
                return true;
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Removes all entries from the dictionary.
        /// </summary>
        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                _map.Clear();
                _lru.Clear();
            }
            finally { _lock.ExitWriteLock(); }
            SchedulePersist();
        }

        /// <summary>
        /// Returns a snapshot of all key-value pairs as an array.
        /// </summary>
        public IEnumerable<KeyValuePair<TKey, TValue>> ToArray()
        {
            _lock.EnterReadLock();
            try
            {
                var result = new KeyValuePair<TKey, TValue>[_map.Count];
                int i = 0;
                foreach (var kvp in _map)
                    result[i++] = new KeyValuePair<TKey, TValue>(kvp.Key, kvp.Value.Value.Value);
                return result;
            }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>Returns true if the dictionary contains no entries.</summary>
        public bool IsEmpty => Count == 0;

        // -------------------------------------------------------------------------
        // IReadOnlyDictionary<TKey, TValue> explicit implementation
        // -------------------------------------------------------------------------

        /// <inheritdoc cref="IReadOnlyDictionary{TKey,TValue}.Keys"/>
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

        /// <inheritdoc cref="IReadOnlyDictionary{TKey,TValue}.Values"/>
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        /// <inheritdoc cref="IReadOnlyDictionary{TKey,TValue}.this"/>
        TValue IReadOnlyDictionary<TKey, TValue>.this[TKey key] => this[key];

        /// <summary>
        /// Attempts to get a value by key without throwing.
        /// Implements <see cref="IReadOnlyDictionary{TKey,TValue}.TryGetValue"/>.
        /// </summary>
        bool IReadOnlyDictionary<TKey, TValue>.TryGetValue(TKey key, out TValue value) =>
            TryGetValue(key, out value);

        /// <inheritdoc cref="IReadOnlyCollection{T}.Count"/>
        int IReadOnlyCollection<KeyValuePair<TKey, TValue>>.Count => Count;

        // -------------------------------------------------------------------------
        // Persistence
        // -------------------------------------------------------------------------

        private bool PersistenceEnabled =>
            _stateStore != null && _pluginId != null && _stateKey != null;

        /// <summary>
        /// Immediately persists the current dictionary state to the configured state store.
        /// No-op if no state store is configured.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task PersistAsync(CancellationToken ct = default)
        {
            if (!PersistenceEnabled) return;

            Dictionary<string, string> snapshot;
            _lock.EnterReadLock();
            try
            {
                snapshot = new Dictionary<string, string>(_map.Count);
                foreach (var kvp in _map)
                {
                    var keyJson = JsonSerializer.Serialize(kvp.Key);
                    var valJson = JsonSerializer.Serialize(kvp.Value.Value.Value);
                    snapshot[keyJson] = valJson;
                }
            }
            finally { _lock.ExitReadLock(); }

            var json = JsonSerializer.Serialize(snapshot);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _stateStore!.SaveAsync(_pluginId!, _stateKey!, bytes, ct).ConfigureAwait(false);
            _pendingPersist = false;
        }

        /// <summary>
        /// Loads previously persisted state from the configured state store into this dictionary.
        /// Existing entries are merged; persisted entries overwrite in-memory ones on key conflict.
        /// No-op if no state store is configured or no state exists.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task LoadPersistedAsync(CancellationToken ct = default)
        {
            if (!PersistenceEnabled) return;

            var bytes = await _stateStore!.LoadAsync(_pluginId!, _stateKey!, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return;

            var json = Encoding.UTF8.GetString(bytes);
            var snapshot = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (snapshot is null) return;

            _lock.EnterWriteLock();
            try
            {
                foreach (var kvp in snapshot)
                {
                    var key = JsonSerializer.Deserialize<TKey>(kvp.Key)!;
                    var value = JsonSerializer.Deserialize<TValue>(kvp.Value)!;

                    if (_map.TryGetValue(key, out var existing))
                    {
                        _lru.Remove(existing);
                    }
                    else
                    {
                        EnsureCapacity();
                    }
                    var node = _lru.AddFirst((key, value));
                    _map[key] = node;
                }
            }
            finally { _lock.ExitWriteLock(); }
        }

        private void SchedulePersist()
        {
            if (!PersistenceEnabled) return;
            _pendingPersist = true;
            // Restart the debounce timer — persist fires 5s after last modification
            _debounceTimer?.Change(BoundedCollectionConstants.PersistDebounceInterval, Timeout.InfiniteTimeSpan);
        }

        private void OnDebounceElapsed(object? _)
        {
            if (!_pendingPersist || _disposed) return;
            // Fire-and-forget with structured exception handling
            _ = PersistAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // Swallow — persistence is best-effort; do not crash the plugin thread
                }
            }, TaskScheduler.Default);
        }

        // -------------------------------------------------------------------------
        // LRU helpers
        // -------------------------------------------------------------------------

        /// <summary>Moves the given node to the front of the LRU list. Must be called under write lock.</summary>
        private void MoveToFront(LinkedListNode<(TKey Key, TValue Value)> node)
        {
            if (_lru.First == node) return;
            _lru.Remove(node);
            _lru.AddFirst(node);
        }

        /// <summary>
        /// Evicts the LRU tail entry when the map is at capacity. Must be called under write lock.
        /// </summary>
        private void EnsureCapacity()
        {
            while (_map.Count >= _maxCapacity && _lru.Last != null)
            {
                var tail = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(tail.Value.Key);
                OnEvicted?.Invoke(tail.Value.Key, tail.Value.Value);
            }
        }

        // -------------------------------------------------------------------------
        // IEnumerable
        // -------------------------------------------------------------------------

        /// <inheritdoc/>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            // Snapshot to avoid holding lock during enumeration
            _lock.EnterReadLock();
            List<KeyValuePair<TKey, TValue>> snapshot;
            try
            {
                snapshot = new List<KeyValuePair<TKey, TValue>>(_map.Count);
                foreach (var kvp in _map)
                    snapshot.Add(new KeyValuePair<TKey, TValue>(kvp.Key, kvp.Value.Value.Value));
            }
            finally { _lock.ExitReadLock(); }
            return snapshot.GetEnumerator();
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

            // Best-effort flush — do NOT block (sync-over-async deadlocks threadpool-starved callers).
            // Prefer DisposeAsync() for guaranteed flush. Dispose() is a fire-and-forget fallback.
            if (_pendingPersist && PersistenceEnabled)
            {
                _ = Task.Run(() => PersistAsync());
            }

            _lock.Dispose();
        }
    }
}
