using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;

namespace DataWarehouse.SDK.Contracts.Scaling
{
    /// <summary>
    /// Eviction mode for <see cref="BoundedCache{TKey,TValue}"/>.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Cache eviction mode for BoundedCache")]
    public enum CacheEvictionMode
    {
        /// <summary>Least Recently Used -- O(1) get/put via linked list + dictionary.</summary>
        LRU,

        /// <summary>
        /// Adaptive Replacement Cache -- maintains T1/T2 frequency/recency lists
        /// with B1/B2 ghost lists that auto-tune the split point per the ARC paper.
        /// O(1) get/put.
        /// </summary>
        ARC,

        /// <summary>Time-To-Live -- entries expire after a configurable duration with lazy cleanup and background timer.</summary>
        TTL
    }

    /// <summary>
    /// Configuration options for <see cref="BoundedCache{TKey,TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">Cache key type.</typeparam>
    /// <typeparam name="TValue">Cache value type.</typeparam>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: BoundedCache configuration options")]
    public class BoundedCacheOptions<TKey, TValue> where TKey : notnull
    {
        /// <summary>Maximum number of entries. When <see cref="AutoSizeFromRam"/> is true, this is computed at construction.</summary>
        public int MaxEntries { get; set; } = 10_000;

        /// <summary>Eviction policy to use. Default: <see cref="CacheEvictionMode.LRU"/>.</summary>
        public CacheEvictionMode EvictionPolicy { get; set; } = CacheEvictionMode.LRU;

        /// <summary>Default TTL for entries when using <see cref="CacheEvictionMode.TTL"/> mode. Null means no expiry.</summary>
        public TimeSpan? DefaultTtl { get; set; }

        /// <summary>When true, <see cref="MaxEntries"/> is computed from available RAM at construction time.</summary>
        public bool AutoSizeFromRam { get; set; }

        /// <summary>Fraction of total available RAM to use when <see cref="AutoSizeFromRam"/> is true. Default: 10%.</summary>
        public double RamPercentage { get; set; } = 0.1;

        /// <summary>Optional persistent backing store for cache-miss lazy-load and write-through on eviction.</summary>
        public IPersistentBackingStore? BackingStore { get; set; }

        /// <summary>Base path prefix for backing store keys (e.g., <c>dw://cache/myPlugin/</c>).</summary>
        public string? BackingStorePath { get; set; }

        /// <summary>Serializer for writing values to the backing store.</summary>
        public Func<TValue, byte[]>? Serializer { get; set; }

        /// <summary>Deserializer for reading values from the backing store.</summary>
        public Func<byte[], TValue>? Deserializer { get; set; }

        /// <summary>Converts a cache key to a string path component for backing store operations.</summary>
        public Func<TKey, string>? KeyToString { get; set; }

        /// <summary>When true, evicted dirty entries are written through to the backing store.</summary>
        public bool WriteThrough { get; set; }
    }

    /// <summary>
    /// A thread-safe, bounded cache with <see cref="CacheEvictionMode.LRU"/>,
    /// <see cref="CacheEvictionMode.ARC"/>, and <see cref="CacheEvictionMode.TTL"/> eviction modes,
    /// optional <see cref="IPersistentBackingStore"/> integration for lazy-load on cache miss
    /// and write-through on eviction, and auto-sizing from available RAM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a more advanced replacement for <see cref="DataWarehouse.SDK.Utilities.BoundedDictionary{TKey,TValue}"/>.
    /// The existing BoundedDictionary remains for backward compatibility; new code should use BoundedCache.
    /// </para>
    /// <para>
    /// <b>LRU mode:</b> Standard linked-list + dictionary for O(1) get/put with least-recently-used eviction.
    /// </para>
    /// <para>
    /// <b>ARC mode:</b> Adaptive Replacement Cache with four lists (T1 recency, T2 frequency, B1/B2 ghost lists).
    /// Ghost lists track recently evicted keys to automatically tune the T1/T2 split point, adapting to
    /// workload access patterns without manual tuning.
    /// </para>
    /// <para>
    /// <b>TTL mode:</b> Time-based expiry with lazy cleanup on access and a background timer for periodic purge.
    /// </para>
    /// </remarks>
    /// <typeparam name="TKey">Cache key type. Must be non-null.</typeparam>
    /// <typeparam name="TValue">Cache value type.</typeparam>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Advanced bounded cache with LRU/ARC/TTL eviction")]
    public sealed class BoundedCache<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IDisposable, IAsyncDisposable
        where TKey : notnull
    {
        // ---- configuration ----
        private readonly CacheEvictionMode _evictionPolicy;
        private readonly TimeSpan? _defaultTtl;
        private int _maxEntries;

        // ---- backing store ----
        private readonly IPersistentBackingStore? _backingStore;
        private readonly string? _backingStorePath;
        private readonly Func<TValue, byte[]>? _serializer;
        private readonly Func<byte[], TValue>? _deserializer;
        private readonly Func<TKey, string>? _keyToString;
        private readonly bool _writeThrough;

        // ---- thread safety ----
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

        // ---- LRU state ----
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry> _lru;

        // ---- ARC state (T1=recency, T2=frequency, B1/B2=ghost lists) ----
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _arcT1Map;
        private readonly LinkedList<CacheEntry> _arcT1;
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _arcT2Map;
        private readonly LinkedList<CacheEntry> _arcT2;
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _arcB1Map;
        private readonly LinkedList<TKey> _arcB1;
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _arcB2Map;
        private readonly LinkedList<TKey> _arcB2;
        private int _arcP; // target size for T1

        // ---- TTL cleanup timer ----
        private Timer? _ttlCleanupTimer;

        // ---- statistics ----
        private long _hits;
        private long _misses;
        private long _evictions;

        // ---- disposal ----
        private bool _disposed;

        /// <summary>
        /// Raised when an entry is evicted from the cache.
        /// </summary>
        public event Action<TKey, TValue>? OnEvicted;

        /// <summary>
        /// Initializes a new <see cref="BoundedCache{TKey,TValue}"/> with the specified options.
        /// </summary>
        /// <param name="options">Configuration options controlling eviction policy, capacity, and backing store.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when max entries is less than 1.</exception>
        public BoundedCache(BoundedCacheOptions<TKey, TValue> options)
        {
            ArgumentNullException.ThrowIfNull(options);

            _evictionPolicy = options.EvictionPolicy;
            _defaultTtl = options.DefaultTtl;
            _backingStore = options.BackingStore;
            _backingStorePath = options.BackingStorePath;
            _serializer = options.Serializer;
            _deserializer = options.Deserializer;
            _keyToString = options.KeyToString;
            _writeThrough = options.WriteThrough;

            // Auto-size from RAM
            if (options.AutoSizeFromRam)
            {
                var memInfo = GC.GetGCMemoryInfo();
                var availableBytes = memInfo.TotalAvailableMemoryBytes;
                var budgetBytes = (long)(availableBytes * Math.Clamp(options.RamPercentage, 0.001, 0.5));
                // Estimate ~256 bytes per entry as a conservative heuristic
                _maxEntries = Math.Max(1, (int)Math.Min(budgetBytes / 256, int.MaxValue));
            }
            else
            {
                _maxEntries = options.MaxEntries;
            }

            if (_maxEntries < 1)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxEntries must be at least 1.");

            // Initialize data structures based on eviction policy
            _map = new Dictionary<TKey, LinkedListNode<CacheEntry>>(_maxEntries);
            _lru = new LinkedList<CacheEntry>();

            _arcT1Map = new Dictionary<TKey, LinkedListNode<CacheEntry>>();
            _arcT1 = new LinkedList<CacheEntry>();
            _arcT2Map = new Dictionary<TKey, LinkedListNode<CacheEntry>>();
            _arcT2 = new LinkedList<CacheEntry>();
            _arcB1Map = new Dictionary<TKey, LinkedListNode<TKey>>();
            _arcB1 = new LinkedList<TKey>();
            _arcB2Map = new Dictionary<TKey, LinkedListNode<TKey>>();
            _arcB2 = new LinkedList<TKey>();

            // Start TTL cleanup timer for TTL mode
            if (_evictionPolicy == CacheEvictionMode.TTL && _defaultTtl.HasValue)
            {
                var interval = TimeSpan.FromMilliseconds(Math.Max(_defaultTtl.Value.TotalMilliseconds / 4, 1000));
                _ttlCleanupTimer = new Timer(TtlCleanupCallback, null, interval, interval);
            }
        }

        // -------------------------------------------------------------------
        // Properties
        // -------------------------------------------------------------------

        /// <summary>Gets the number of entries currently in the cache.</summary>
        public int Count
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    return _evictionPolicy == CacheEvictionMode.ARC
                        ? _arcT1Map.Count + _arcT2Map.Count
                        : _map.Count;
                }
                finally { _lock.ExitReadLock(); }
            }
        }

        /// <summary>Gets an approximate memory usage in bytes (heuristic: 256 bytes per entry).</summary>
        public long EstimatedMemoryBytes => (long)Count * 256;

        // -------------------------------------------------------------------
        // Synchronous API
        // -------------------------------------------------------------------

        /// <summary>
        /// Gets the value associated with the specified key, or <c>default</c> if not found.
        /// Does not consult the backing store (use <see cref="GetAsync"/> for backing store fallback).
        /// </summary>
        /// <param name="key">The key to look up.</param>
        /// <returns>The value if found; otherwise <c>default</c>.</returns>
        public TValue? GetOrDefault(TKey key)
        {
            _lock.EnterWriteLock();
            try
            {
                return _evictionPolicy switch
                {
                    CacheEvictionMode.ARC => ArcGet(key, out var v) ? v : default,
                    _ => LruGet(key, out var v) ? v : default,
                };
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Inserts or updates a key-value pair in the cache.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        public void Put(TKey key, TValue value)
        {
            _lock.EnterWriteLock();
            try
            {
                switch (_evictionPolicy)
                {
                    case CacheEvictionMode.ARC:
                        ArcPut(key, value);
                        break;
                    default:
                        LruPut(key, value);
                        break;
                }
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Attempts to remove the entry with the specified key.
        /// </summary>
        /// <param name="key">The key to remove.</param>
        /// <param name="value">When this method returns, contains the removed value if found.</param>
        /// <returns><c>true</c> if the key was found and removed; otherwise <c>false</c>.</returns>
        public bool TryRemove(TKey key, out TValue? value)
        {
            _lock.EnterWriteLock();
            try
            {
                if (_evictionPolicy == CacheEvictionMode.ARC)
                    return ArcTryRemove(key, out value);
                return LruTryRemove(key, out value);
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// Determines whether the cache contains the specified key.
        /// </summary>
        /// <param name="key">The key to check.</param>
        /// <returns><c>true</c> if the key exists in cache; otherwise <c>false</c>.</returns>
        public bool ContainsKey(TKey key)
        {
            _lock.EnterReadLock();
            try
            {
                return _evictionPolicy == CacheEvictionMode.ARC
                    ? _arcT1Map.ContainsKey(key) || _arcT2Map.ContainsKey(key)
                    : _map.ContainsKey(key);
            }
            finally { _lock.ExitReadLock(); }
        }

        // -------------------------------------------------------------------
        // Async API with backing store integration
        // -------------------------------------------------------------------

        /// <summary>
        /// Gets the value associated with the specified key.
        /// On cache miss, falls back to the <see cref="IPersistentBackingStore"/> if configured.
        /// </summary>
        /// <param name="key">The key to look up.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The value if found in cache or backing store; otherwise <c>default</c>.</returns>
        public async Task<TValue?> GetAsync(TKey key, CancellationToken ct = default)
        {
            // Try cache first
            _lock.EnterWriteLock();
            try
            {
                bool found = _evictionPolicy == CacheEvictionMode.ARC
                    ? ArcGet(key, out var av)
                    : LruGet(key, out av);

                if (found)
                {
                    Interlocked.Increment(ref _hits);
                    return av;
                }
            }
            finally { _lock.ExitWriteLock(); }

            // Cache miss -- try backing store
            Interlocked.Increment(ref _misses);

            if (_backingStore == null || _deserializer == null || _keyToString == null)
                return default;

            var path = BuildBackingStorePath(key);
            var data = await _backingStore.ReadAsync(path, ct).ConfigureAwait(false);
            if (data == null)
                return default;

            var value = _deserializer(data);

            // Insert into cache
            _lock.EnterWriteLock();
            try
            {
                switch (_evictionPolicy)
                {
                    case CacheEvictionMode.ARC:
                        ArcPut(key, value);
                        break;
                    default:
                        LruPut(key, value);
                        break;
                }
            }
            finally { _lock.ExitWriteLock(); }

            return value;
        }

        /// <summary>
        /// Inserts or updates a key-value pair in the cache.
        /// If write-through is enabled, also persists to the backing store.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task PutAsync(TKey key, TValue value, CancellationToken ct = default)
        {
            Put(key, value);

            if (_writeThrough && _backingStore != null && _serializer != null && _keyToString != null)
            {
                var path = BuildBackingStorePath(key);
                var data = _serializer(value);
                await _backingStore.WriteAsync(path, data, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Returns cache statistics including hit/miss counts, eviction count, and capacity.
        /// Reuses the existing <see cref="CacheStatistics"/> type from ICacheableStorage.
        /// </summary>
        /// <returns>Current cache statistics snapshot.</returns>
        public CacheStatistics GetStatistics()
        {
            var count = Count;
            var hits = Interlocked.Read(ref _hits);
            var misses = Interlocked.Read(ref _misses);
            var evictions = Interlocked.Read(ref _evictions);
            return CacheStatistics.Create(count, EstimatedMemoryBytes, hits, misses, evictions);
        }

        // -------------------------------------------------------------------
        // LRU implementation (also used for TTL mode)
        // -------------------------------------------------------------------

        /// <summary>LRU get. Must be called under write lock. Handles TTL lazy eviction.</summary>
        private bool LruGet(TKey key, out TValue? value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // TTL lazy check
                if (_evictionPolicy == CacheEvictionMode.TTL && node.Value.ExpiresAt.HasValue
                    && node.Value.ExpiresAt.Value <= DateTime.UtcNow)
                {
                    EvictLruNode(node);
                    value = default;
                    return false;
                }

                _lru.Remove(node);
                _lru.AddFirst(node);
                Interlocked.Increment(ref _hits);
                value = node.Value.Value;
                return true;
            }

            Interlocked.Increment(ref _misses);
            value = default;
            return false;
        }

        /// <summary>LRU put. Must be called under write lock.</summary>
        private void LruPut(TKey key, TValue value)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                existing.Value = new CacheEntry(key, value, ComputeExpiry());
                _lru.AddFirst(existing);
                _map[key] = existing;
                return;
            }

            // Evict if at capacity
            while (_map.Count >= _maxEntries && _lru.Last != null)
            {
                EvictLruNode(_lru.Last);
            }

            var entry = new CacheEntry(key, value, ComputeExpiry());
            var node = _lru.AddFirst(entry);
            _map[key] = node;
        }

        private bool LruTryRemove(TKey key, out TValue? value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                value = node.Value.Value;
                _lru.Remove(node);
                _map.Remove(key);
                return true;
            }
            value = default;
            return false;
        }

        private void EvictLruNode(LinkedListNode<CacheEntry> node)
        {
            _lru.Remove(node);
            _map.Remove(node.Value.Key);
            Interlocked.Increment(ref _evictions);
            OnEvicted?.Invoke(node.Value.Key, node.Value.Value);

            // Write-through on eviction (fire-and-forget)
            if (_writeThrough && _backingStore != null && _serializer != null && _keyToString != null)
            {
                var path = BuildBackingStorePath(node.Value.Key);
                var data = _serializer(node.Value.Value);
                _ = _backingStore.WriteAsync(path, data).ContinueWith(
                    static t => { /* best-effort */ }, TaskScheduler.Default);
            }
        }

        // -------------------------------------------------------------------
        // ARC implementation
        // -------------------------------------------------------------------

        /// <summary>ARC get. Must be called under write lock.</summary>
        private bool ArcGet(TKey key, out TValue? value)
        {
            // Case 1: Hit in T1 -- promote to T2 (recency -> frequency)
            if (_arcT1Map.TryGetValue(key, out var t1Node))
            {
                value = t1Node.Value.Value;
                _arcT1.Remove(t1Node);
                _arcT1Map.Remove(key);
                var t2Entry = new CacheEntry(key, value!, null);
                var t2Node = _arcT2.AddFirst(t2Entry);
                _arcT2Map[key] = t2Node;
                Interlocked.Increment(ref _hits);
                return true;
            }

            // Case 2: Hit in T2 -- move to MRU of T2
            if (_arcT2Map.TryGetValue(key, out var t2Existing))
            {
                value = t2Existing.Value.Value;
                _arcT2.Remove(t2Existing);
                _arcT2.AddFirst(t2Existing);
                Interlocked.Increment(ref _hits);
                return true;
            }

            Interlocked.Increment(ref _misses);
            value = default;
            return false;
        }

        /// <summary>ARC put. Must be called under write lock.</summary>
        private void ArcPut(TKey key, TValue value)
        {
            int totalCache = _arcT1Map.Count + _arcT2Map.Count;

            // Case: key already in T1 or T2 -- update in place
            if (_arcT1Map.TryGetValue(key, out var existT1))
            {
                _arcT1.Remove(existT1);
                _arcT1Map.Remove(key);
                var t2Entry = new CacheEntry(key, value, null);
                var t2Node = _arcT2.AddFirst(t2Entry);
                _arcT2Map[key] = t2Node;
                return;
            }
            if (_arcT2Map.TryGetValue(key, out var existT2))
            {
                existT2.Value = new CacheEntry(key, value, null);
                _arcT2.Remove(existT2);
                _arcT2.AddFirst(existT2);
                return;
            }

            // Case: key in B1 ghost list -- adapt p upward (favor T1)
            if (_arcB1Map.TryGetValue(key, out var b1Node))
            {
                int delta = _arcB2Map.Count >= _arcB1Map.Count
                    ? 1
                    : _arcB2Map.Count > 0 ? _arcB1Map.Count / _arcB2Map.Count : 1;
                _arcP = Math.Min(_arcP + Math.Max(delta, 1), _maxEntries);

                _arcB1.Remove(b1Node);
                _arcB1Map.Remove(key);

                ArcReplace(key);
                var t2Entry = new CacheEntry(key, value, null);
                var t2Node = _arcT2.AddFirst(t2Entry);
                _arcT2Map[key] = t2Node;
                return;
            }

            // Case: key in B2 ghost list -- adapt p downward (favor T2)
            if (_arcB2Map.TryGetValue(key, out var b2Node))
            {
                int delta = _arcB1Map.Count >= _arcB2Map.Count
                    ? 1
                    : _arcB1Map.Count > 0 ? _arcB2Map.Count / _arcB1Map.Count : 1;
                _arcP = Math.Max(_arcP - Math.Max(delta, 1), 0);

                _arcB2.Remove(b2Node);
                _arcB2Map.Remove(key);

                ArcReplace(key);
                var t2Entry = new CacheEntry(key, value, null);
                var t2Node = _arcT2.AddFirst(t2Entry);
                _arcT2Map[key] = t2Node;
                return;
            }

            // Case: completely new key
            int totalAll = totalCache + _arcB1Map.Count + _arcB2Map.Count;

            if (totalCache >= _maxEntries)
            {
                ArcReplace(key);

                // Also trim ghost lists if full
                if (_arcB1Map.Count + _arcB2Map.Count >= _maxEntries)
                {
                    if (_arcB1Map.Count > 0 && _arcB1.Last != null)
                    {
                        var last = _arcB1.Last;
                        _arcB1.RemoveLast();
                        _arcB1Map.Remove(last.Value);
                    }
                }
            }
            else if (totalAll >= _maxEntries)
            {
                if (totalAll >= _maxEntries * 2)
                {
                    if (_arcB1Map.Count > 0 && _arcB1.Last != null)
                    {
                        var last = _arcB1.Last;
                        _arcB1.RemoveLast();
                        _arcB1Map.Remove(last.Value);
                    }
                    else if (_arcB2Map.Count > 0 && _arcB2.Last != null)
                    {
                        var last = _arcB2.Last;
                        _arcB2.RemoveLast();
                        _arcB2Map.Remove(last.Value);
                    }
                }
                ArcReplace(key);
            }

            // Insert into T1 (recency)
            var newEntry = new CacheEntry(key, value, null);
            var newNode = _arcT1.AddFirst(newEntry);
            _arcT1Map[key] = newNode;
        }

        /// <summary>
        /// ARC Replace: evict one entry from T1 or T2 based on the adaptive parameter p.
        /// Evicted keys are moved to the corresponding ghost list (B1 or B2).
        /// Must be called under write lock.
        /// </summary>
        private void ArcReplace(TKey incomingKey)
        {
            if (_arcT1Map.Count > 0 &&
                (_arcT1Map.Count > _arcP ||
                 (_arcT1Map.Count == _arcP && _arcB2Map.ContainsKey(incomingKey))))
            {
                // Evict from T1 LRU, move to B1
                if (_arcT1.Last != null)
                {
                    var victim = _arcT1.Last;
                    _arcT1.RemoveLast();
                    _arcT1Map.Remove(victim.Value.Key);
                    Interlocked.Increment(ref _evictions);
                    OnEvicted?.Invoke(victim.Value.Key, victim.Value.Value);

                    // Add to B1 ghost
                    var ghostNode = _arcB1.AddFirst(victim.Value.Key);
                    _arcB1Map[victim.Value.Key] = ghostNode;

                    // Trim B1 ghost if too large
                    while (_arcB1Map.Count > _maxEntries && _arcB1.Last != null)
                    {
                        var last = _arcB1.Last;
                        _arcB1.RemoveLast();
                        _arcB1Map.Remove(last.Value);
                    }
                }
            }
            else
            {
                // Evict from T2 LRU, move to B2
                if (_arcT2.Last != null)
                {
                    var victim = _arcT2.Last;
                    _arcT2.RemoveLast();
                    _arcT2Map.Remove(victim.Value.Key);
                    Interlocked.Increment(ref _evictions);
                    OnEvicted?.Invoke(victim.Value.Key, victim.Value.Value);

                    // Add to B2 ghost
                    var ghostNode = _arcB2.AddFirst(victim.Value.Key);
                    _arcB2Map[victim.Value.Key] = ghostNode;

                    // Trim B2 ghost if too large
                    while (_arcB2Map.Count > _maxEntries && _arcB2.Last != null)
                    {
                        var last = _arcB2.Last;
                        _arcB2.RemoveLast();
                        _arcB2Map.Remove(last.Value);
                    }
                }
                else if (_arcT1.Last != null)
                {
                    // Fallback: evict from T1 if T2 is empty
                    var victim = _arcT1.Last;
                    _arcT1.RemoveLast();
                    _arcT1Map.Remove(victim.Value.Key);
                    Interlocked.Increment(ref _evictions);
                    OnEvicted?.Invoke(victim.Value.Key, victim.Value.Value);

                    var ghostNode = _arcB1.AddFirst(victim.Value.Key);
                    _arcB1Map[victim.Value.Key] = ghostNode;
                }
            }
        }

        private bool ArcTryRemove(TKey key, out TValue? value)
        {
            if (_arcT1Map.TryGetValue(key, out var t1Node))
            {
                value = t1Node.Value.Value;
                _arcT1.Remove(t1Node);
                _arcT1Map.Remove(key);
                return true;
            }
            if (_arcT2Map.TryGetValue(key, out var t2Node))
            {
                value = t2Node.Value.Value;
                _arcT2.Remove(t2Node);
                _arcT2Map.Remove(key);
                return true;
            }
            value = default;
            return false;
        }

        // -------------------------------------------------------------------
        // TTL cleanup
        // -------------------------------------------------------------------

        private void TtlCleanupCallback(object? state)
        {
            if (_disposed) return;

            _lock.EnterWriteLock();
            try
            {
                var now = DateTime.UtcNow;
                var node = _lru.Last;
                while (node != null)
                {
                    var prev = node.Previous;
                    if (node.Value.ExpiresAt.HasValue && node.Value.ExpiresAt.Value <= now)
                    {
                        EvictLruNode(node);
                    }
                    node = prev;
                }
            }
            finally { _lock.ExitWriteLock(); }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private DateTime? ComputeExpiry()
        {
            return _evictionPolicy == CacheEvictionMode.TTL && _defaultTtl.HasValue
                ? DateTime.UtcNow + _defaultTtl.Value
                : null;
        }

        private string BuildBackingStorePath(TKey key)
        {
            var keyStr = _keyToString != null ? _keyToString(key) : key.ToString() ?? "null";
            return string.IsNullOrEmpty(_backingStorePath)
                ? keyStr
                : $"{_backingStorePath.TrimEnd('/')}/{keyStr}";
        }

        // -------------------------------------------------------------------
        // IEnumerable
        // -------------------------------------------------------------------

        /// <inheritdoc/>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            _lock.EnterReadLock();
            List<KeyValuePair<TKey, TValue>> snapshot;
            try
            {
                if (_evictionPolicy == CacheEvictionMode.ARC)
                {
                    snapshot = new List<KeyValuePair<TKey, TValue>>(_arcT1Map.Count + _arcT2Map.Count);
                    foreach (var kvp in _arcT1Map)
                        snapshot.Add(new KeyValuePair<TKey, TValue>(kvp.Key, kvp.Value.Value.Value));
                    foreach (var kvp in _arcT2Map)
                        snapshot.Add(new KeyValuePair<TKey, TValue>(kvp.Key, kvp.Value.Value.Value));
                }
                else
                {
                    snapshot = new List<KeyValuePair<TKey, TValue>>(_map.Count);
                    foreach (var kvp in _map)
                        snapshot.Add(new KeyValuePair<TKey, TValue>(kvp.Key, kvp.Value.Value.Value));
                }
            }
            finally { _lock.ExitReadLock(); }
            return snapshot.GetEnumerator();
        }

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // -------------------------------------------------------------------
        // IDisposable / IAsyncDisposable
        // -------------------------------------------------------------------

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ttlCleanupTimer?.Dispose();
            _ttlCleanupTimer = null;
            _lock.Dispose();
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        // -------------------------------------------------------------------
        // Internal cache entry
        // -------------------------------------------------------------------

        private record struct CacheEntry(TKey Key, TValue Value, DateTime? ExpiresAt);
    }
}
