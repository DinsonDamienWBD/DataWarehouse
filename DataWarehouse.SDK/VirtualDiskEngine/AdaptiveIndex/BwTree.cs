using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Lock-free Bw-Tree implementation for concurrent metadata caching.
/// </summary>
/// <remarks>
/// <para>
/// The Bw-Tree (Buzzword-Tree) provides lock-free concurrent access via mapping table
/// indirection and CAS (Compare-And-Swap) updates. All page modifications are performed
/// by prepending immutable delta records to page chains and atomically installing
/// the new chain head via <see cref="BwTreeMappingTable.CompareExchange"/>.
/// </para>
/// <para>
/// Key design properties:
/// <list type="bullet">
/// <item><description>Lock-free reads and writes via CAS on the mapping table</description></item>
/// <item><description>Append-only delta chains avoid page overwrites</description></item>
/// <item><description>Epoch-based garbage collection safely reclaims old delta chains</description></item>
/// <item><description>Automatic consolidation when delta chains exceed a configurable threshold</description></item>
/// <item><description>Page lookup uses <see cref="BwTreeMappingTable"/> backed by <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> which auto-rehashes at 0.75 load factor — worst-case collision chains are bounded by the runtime (Cat 13, finding 717)</description></item>
/// <item><description>Two-phase split/merge protocol for structural modifications</description></item>
/// </list>
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type. Must implement <see cref="IComparable{T}"/>.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-09 Bw-Tree")]
public sealed class BwTree<TKey, TValue> : IDisposable where TKey : IComparable<TKey>
{
    private readonly BwTreeMappingTable _mappingTable;
    private readonly EpochManager _epochManager;
    private readonly int _consolidationThreshold;
    private readonly int _pageCapacity;
    private long _rootPageId;
    private long _count;

    /// <summary>
    /// Gets the logical page ID of the root node.
    /// </summary>
    public long RootPageId => Interlocked.Read(ref _rootPageId);

    /// <summary>
    /// Gets the total number of key-value pairs in the tree.
    /// </summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>
    /// Gets the mapping table used for logical-to-physical page indirection.
    /// </summary>
    public BwTreeMappingTable MappingTable => _mappingTable;

    /// <summary>
    /// Gets the epoch manager used for garbage collection.
    /// </summary>
    public EpochManager EpochManager => _epochManager;

    /// <summary>
    /// Initializes a new Bw-Tree instance.
    /// </summary>
    /// <param name="consolidationThreshold">Maximum delta chain length before consolidation. Defaults to 8.</param>
    /// <param name="pageCapacity">Maximum entries per consolidated page. Defaults to 64.</param>
    /// <param name="mappingTableCapacity">Initial mapping table capacity. Defaults to 4096.</param>
    public BwTree(int consolidationThreshold = 8, int pageCapacity = 64, int mappingTableCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consolidationThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCapacity);

        _consolidationThreshold = consolidationThreshold;
        _pageCapacity = pageCapacity;
        _mappingTable = new BwTreeMappingTable(mappingTableCapacity);
        _epochManager = new EpochManager();
        _count = 0;

        // Create initial empty root (leaf) page
        _rootPageId = _mappingTable.AllocatePageId();
        var emptyRoot = new ConsolidationRecord<TKey, TValue>(
            entries: Array.Empty<(TKey, TValue)>(),
            children: Array.Empty<long>(),
            isLeaf: true);
        _mappingTable.CompareExchange(_rootPageId, null, emptyRoot);
    }

    /// <summary>
    /// Looks up the value associated with the specified key.
    /// </summary>
    /// <param name="key">The key to search for.</param>
    /// <param name="value">When found, contains the associated value.</param>
    /// <returns>True if the key was found, false otherwise.</returns>
    public bool TryGet(TKey key, out TValue? value)
    {
        _epochManager.Enter();
        try
        {
            long pageId = _rootPageId;

            while (true)
            {
                var chain = _mappingTable.Get(pageId) as BwTreeDeltaRecord<TKey, TValue>;
                var result = SearchChainForKey(chain, key);

                if (result.Found)
                {
                    value = result.Value;
                    return !result.Deleted;
                }

                if (result.IsLeaf)
                {
                    // Key not in this leaf page
                    value = default;
                    return false;
                }

                // Navigate to child page
                pageId = result.ChildPageId;
            }
        }
        finally
        {
            _epochManager.Exit();
        }
    }

    /// <summary>
    /// Inserts a key-value pair into the Bw-Tree.
    /// </summary>
    /// <param name="key">The key to insert.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <returns>True if inserted, false if the key already exists.</returns>
    public bool Insert(TKey key, TValue value)
    {
        _epochManager.Enter();
        try
        {
            while (true)
            {
                long leafPageId = FindLeafPage(key);
                var currentChain = _mappingTable.Get(leafPageId) as BwTreeDeltaRecord<TKey, TValue>;

                // Check if key already exists
                var searchResult = SearchChainForKey(currentChain, key);
                if (searchResult.Found && !searchResult.Deleted)
                    return false;

                // Create insert delta record and CAS prepend
                var insertDelta = new InsertDeltaRecord<TKey, TValue>(key, value, currentChain);
                if (_mappingTable.CompareExchange(leafPageId, currentChain, insertDelta))
                {
                    Interlocked.Increment(ref _count);

                    // Check if consolidation is needed
                    int chainLength = GetChainLength(insertDelta);
                    if (chainLength >= _consolidationThreshold)
                    {
                        TryConsolidate(leafPageId);
                    }

                    return true;
                }
                // CAS failed - retry from leaf search
            }
        }
        finally
        {
            _epochManager.Exit();
        }
    }

    /// <summary>
    /// Deletes a key from the Bw-Tree.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <returns>True if the key was found and deleted, false if not found.</returns>
    public bool Delete(TKey key)
    {
        _epochManager.Enter();
        try
        {
            while (true)
            {
                long leafPageId = FindLeafPage(key);
                var currentChain = _mappingTable.Get(leafPageId) as BwTreeDeltaRecord<TKey, TValue>;

                // Check if key exists
                var searchResult = SearchChainForKey(currentChain, key);
                if (!searchResult.Found || searchResult.Deleted)
                    return false;

                // Create delete delta record and CAS prepend
                var deleteDelta = new DeleteDeltaRecord<TKey, TValue>(key, currentChain);
                if (_mappingTable.CompareExchange(leafPageId, currentChain, deleteDelta))
                {
                    Interlocked.Decrement(ref _count);

                    // Check if consolidation is needed
                    int chainLength = GetChainLength(deleteDelta);
                    if (chainLength >= _consolidationThreshold)
                    {
                        TryConsolidate(leafPageId);
                    }

                    return true;
                }
                // CAS failed - retry
            }
        }
        finally
        {
            _epochManager.Exit();
        }
    }

    /// <summary>
    /// Performs a range query, returning all key-value pairs in [startKey, endKey) sorted order.
    /// The caller MUST dispose the returned enumerator (e.g., use a foreach or explicit using) to
    /// guarantee the epoch is released. Abandoning the enumerator leaks the epoch and blocks
    /// garbage collection of all subsequent delta chains. Use <c>foreach</c> which always calls Dispose.
    /// </summary>
    /// <param name="startKey">The inclusive start key. Null means from the beginning.</param>
    /// <param name="endKey">The exclusive end key. Null means to the end.</param>
    /// <returns>An enumerable of key-value pairs in sorted order.</returns>
    public IEnumerable<(TKey Key, TValue Value)> RangeQuery(TKey? startKey, TKey? endKey)
    {
        _epochManager.Enter();
        try
        {
            // Consolidate the leaf page to get a sorted view, then scan
            long leafPageId = startKey is not null ? FindLeafPage(startKey) : FindLeftmostLeafPage();

            while (leafPageId >= 0)
            {
                var chain = _mappingTable.Get(leafPageId) as BwTreeDeltaRecord<TKey, TValue>;
                var entries = MaterializeEntries(chain);

                foreach (var entry in entries)
                {
                    if (startKey is not null && entry.Key.CompareTo(startKey) < 0)
                        continue;
                    if (endKey is not null && entry.Key.CompareTo(endKey) >= 0)
                        yield break;
                    yield return entry;
                }

                // Move to right sibling
                leafPageId = GetRightSiblingPageId(chain);
            }
        }
        finally
        {
            _epochManager.Exit();
        }
    }

    /// <summary>
    /// Navigates from the root to find the leaf page containing the specified key.
    /// </summary>
    private long FindLeafPage(TKey key)
    {
        long pageId = Interlocked.Read(ref _rootPageId);

        while (true)
        {
            var chain = _mappingTable.Get(pageId) as BwTreeDeltaRecord<TKey, TValue>;

            // Check for split delta - if key >= separator, follow new sibling
            var splitRedirect = CheckSplitRedirect(chain, key);
            if (splitRedirect >= 0)
            {
                pageId = splitRedirect;
                continue;
            }

            // Get the consolidated base to determine if this is a leaf
            var basePage = FindConsolidationBase(chain);
            if (basePage == null || basePage.IsLeaf)
                return pageId;

            // Internal node - find the correct child
            pageId = FindChildPage(basePage, key);
        }
    }

    /// <summary>
    /// Finds the leftmost leaf page by always following the first child.
    /// </summary>
    private long FindLeftmostLeafPage()
    {
        long pageId = Interlocked.Read(ref _rootPageId);

        while (true)
        {
            var chain = _mappingTable.Get(pageId) as BwTreeDeltaRecord<TKey, TValue>;
            var basePage = FindConsolidationBase(chain);
            if (basePage == null || basePage.IsLeaf)
                return pageId;

            if (basePage.Children.Length == 0)
                return pageId;

            pageId = basePage.Children[0];
        }
    }

    /// <summary>
    /// Checks if a split delta redirects the search for a given key.
    /// </summary>
    /// <returns>The redirected page ID, or -1 if no redirect needed.</returns>
    private static long CheckSplitRedirect(BwTreeDeltaRecord<TKey, TValue>? chain, TKey key)
    {
        var current = chain;
        while (current != null)
        {
            if (current is SplitDeltaRecord<TKey, TValue> split)
            {
                if (key.CompareTo(split.SeparatorKey) >= 0)
                    return split.NewSiblingPageId;
            }
            current = current.Next;
        }
        return -1;
    }

    /// <summary>
    /// Finds the ConsolidationRecord base page in a delta chain.
    /// </summary>
    private static ConsolidationRecord<TKey, TValue>? FindConsolidationBase(BwTreeDeltaRecord<TKey, TValue>? chain)
    {
        var current = chain;
        while (current != null)
        {
            if (current is ConsolidationRecord<TKey, TValue> consolidation)
                return consolidation;
            current = current.Next;
        }
        return null;
    }

    /// <summary>
    /// Finds the child page for a key in an internal node.
    /// </summary>
    private static long FindChildPage(ConsolidationRecord<TKey, TValue> basePage, TKey key)
    {
        // Binary search for the child pointer
        int lo = 0, hi = basePage.Entries.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            int cmp = key.CompareTo(basePage.Entries[mid].Key);
            if (cmp < 0)
                hi = mid - 1;
            else
                lo = mid + 1;
        }

        // lo is the index of the child to follow
        if (lo < basePage.Children.Length)
            return basePage.Children[lo];

        // Edge case: key is beyond all entries, follow last child
        return basePage.Children[basePage.Children.Length - 1];
    }

    /// <summary>
    /// Gets the right sibling page ID from a chain.
    /// </summary>
    private static long GetRightSiblingPageId(BwTreeDeltaRecord<TKey, TValue>? chain)
    {
        var basePage = FindConsolidationBase(chain);
        return basePage?.RightSiblingPageId ?? -1;
    }

    /// <summary>
    /// Searches a delta chain for a specific key, returning the most recent state.
    /// </summary>
    private static ChainSearchResult SearchChainForKey(BwTreeDeltaRecord<TKey, TValue>? chain, TKey key)
    {
        var current = chain;
        bool isLeaf = true;

        while (current != null)
        {
            switch (current)
            {
                case InsertDeltaRecord<TKey, TValue> insert when key.CompareTo(insert.Key) == 0:
                    return new ChainSearchResult(true, false, insert.Value, -1, true);

                case DeleteDeltaRecord<TKey, TValue> delete when key.CompareTo(delete.Key) == 0:
                    return new ChainSearchResult(true, true, default, -1, true);

                case UpdateDeltaRecord<TKey, TValue> update when key.CompareTo(update.Key) == 0:
                    return new ChainSearchResult(true, false, update.NewValue, -1, true);

                case SplitDeltaRecord<TKey, TValue> split when key.CompareTo(split.SeparatorKey) >= 0:
                    return new ChainSearchResult(false, false, default, split.NewSiblingPageId, true);

                case ConsolidationRecord<TKey, TValue> consolidation:
                    isLeaf = consolidation.IsLeaf;
                    if (consolidation.IsLeaf)
                    {
                        // Binary search in sorted entries
                        int lo = 0, hi = consolidation.Entries.Length - 1;
                        while (lo <= hi)
                        {
                            int mid = lo + (hi - lo) / 2;
                            int cmp = key.CompareTo(consolidation.Entries[mid].Key);
                            if (cmp == 0)
                                return new ChainSearchResult(true, false, consolidation.Entries[mid].Value, -1, true);
                            if (cmp < 0)
                                hi = mid - 1;
                            else
                                lo = mid + 1;
                        }
                        return new ChainSearchResult(false, false, default, -1, true);
                    }
                    else
                    {
                        // Internal node - find child page
                        int lo2 = 0, hi2 = consolidation.Entries.Length - 1;
                        while (lo2 <= hi2)
                        {
                            int mid = lo2 + (hi2 - lo2) / 2;
                            int cmp = key.CompareTo(consolidation.Entries[mid].Key);
                            if (cmp < 0)
                                hi2 = mid - 1;
                            else
                                lo2 = mid + 1;
                        }
                        long childId = lo2 < consolidation.Children.Length
                            ? consolidation.Children[lo2]
                            : consolidation.Children[consolidation.Children.Length - 1];
                        return new ChainSearchResult(false, false, default, childId, false);
                    }

                default:
                    current = current.Next;
                    continue;
            }
        }

        return new ChainSearchResult(false, false, default, -1, isLeaf);
    }

    /// <summary>
    /// Materializes all live entries from a delta chain into a sorted array.
    /// </summary>
    private static List<(TKey Key, TValue Value)> MaterializeEntries(BwTreeDeltaRecord<TKey, TValue>? chain)
    {
        // Collect all mutations from the chain
        var inserts = new Dictionary<int, (TKey Key, TValue Value)>();
        var deletes = new HashSet<int>();
        var sortedEntries = new SortedList<TKey, TValue>();

        // Walk chain from newest to oldest
        var current = chain;
        var seenKeys = new HashSet<int>(); // Track keys we've already resolved

        while (current != null)
        {
            switch (current)
            {
                case InsertDeltaRecord<TKey, TValue> insert:
                {
                    int hash = insert.Key.GetHashCode();
                    if (!seenKeys.Contains(hash) || !sortedEntries.ContainsKey(insert.Key))
                    {
                        if (!sortedEntries.ContainsKey(insert.Key))
                        {
                            sortedEntries[insert.Key] = insert.Value;
                        }
                        seenKeys.Add(hash);
                    }
                    break;
                }

                case DeleteDeltaRecord<TKey, TValue> delete:
                {
                    sortedEntries.Remove(delete.Key);
                    seenKeys.Add(delete.Key.GetHashCode());
                    break;
                }

                case UpdateDeltaRecord<TKey, TValue> update:
                {
                    if (!seenKeys.Contains(update.Key.GetHashCode()))
                    {
                        sortedEntries[update.Key] = update.NewValue;
                        seenKeys.Add(update.Key.GetHashCode());
                    }
                    break;
                }

                case ConsolidationRecord<TKey, TValue> consolidation:
                {
                    foreach (var entry in consolidation.Entries)
                    {
                        if (!sortedEntries.ContainsKey(entry.Key))
                        {
                            sortedEntries[entry.Key] = entry.Value;
                        }
                    }
                    break; // Terminal
                }
            }

            current = current.Next;
        }

        return sortedEntries.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// Attempts to consolidate a page's delta chain into a new base page.
    /// </summary>
    /// <param name="pageId">The logical page ID to consolidate.</param>
    private void TryConsolidate(long pageId)
    {
        var currentChain = _mappingTable.Get(pageId) as BwTreeDeltaRecord<TKey, TValue>;
        if (currentChain == null)
            return;

        // Materialize all entries from the chain
        var entries = MaterializeEntries(currentChain);
        var basePage = FindConsolidationBase(currentChain);

        bool isLeaf = basePage?.IsLeaf ?? true;
        long rightSibling = basePage?.RightSiblingPageId ?? -1;
        long[] children = basePage?.Children ?? Array.Empty<long>();

        // Create new consolidation record
        var newBase = new ConsolidationRecord<TKey, TValue>(
            entries: entries.ToArray(),
            children: children,
            isLeaf: isLeaf,
            rightSiblingPageId: rightSibling);

        // CAS replace the chain
        if (_mappingTable.CompareExchange(pageId, currentChain, newBase))
        {
            // Register old chain for epoch-based GC
            if (currentChain is not null)
                _epochManager.AddGarbage(currentChain!);

            // Check if page needs splitting after consolidation
            if (entries.Count > _pageCapacity)
            {
                TrySplit(pageId);
            }
        }
    }

    /// <summary>
    /// Attempts to split an overfull page using the two-phase split protocol.
    /// </summary>
    /// <param name="pageId">The logical page ID to split.</param>
    /// <remarks>
    /// Phase 1: Install split delta on the original page, directing keys >= separator to new sibling.
    /// Phase 2: Update parent with index delta pointing to the new sibling.
    /// </remarks>
    private void TrySplit(long pageId)
    {
        var currentChain = _mappingTable.Get(pageId) as BwTreeDeltaRecord<TKey, TValue>;
        var basePage = FindConsolidationBase(currentChain);
        if (basePage == null || basePage.Entries.Length <= _pageCapacity)
            return;

        int midIndex = basePage.Entries.Length / 2;
        TKey separatorKey = basePage.Entries[midIndex].Key;

        // Create new sibling page with right half of entries
        long siblingPageId = _mappingTable.AllocatePageId();
        var siblingEntries = basePage.Entries.Skip(midIndex).ToArray();
        var siblingBase = new ConsolidationRecord<TKey, TValue>(
            entries: siblingEntries,
            children: basePage.IsLeaf ? Array.Empty<long>() : basePage.Children.Skip(midIndex).ToArray(),
            isLeaf: basePage.IsLeaf,
            rightSiblingPageId: basePage.RightSiblingPageId);
        _mappingTable.CompareExchange(siblingPageId, null, siblingBase);

        // Phase 1: Install split delta on original page
        var splitDelta = new SplitDeltaRecord<TKey, TValue>(separatorKey, siblingPageId, currentChain);
        if (_mappingTable.CompareExchange(pageId, currentChain, splitDelta))
        {
            if (currentChain is not null)
                _epochManager.AddGarbage(currentChain!);

            // Consolidate the original page to remove entries moved to sibling
            var leftEntries = basePage.Entries.Take(midIndex).ToArray();
            var leftBase = new ConsolidationRecord<TKey, TValue>(
                entries: leftEntries,
                children: basePage.IsLeaf ? Array.Empty<long>() : basePage.Children.Take(midIndex + 1).ToArray(),
                isLeaf: basePage.IsLeaf,
                rightSiblingPageId: siblingPageId);

            var chainAfterSplit = _mappingTable.Get(pageId) as BwTreeDeltaRecord<TKey, TValue>;
            _mappingTable.CompareExchange(pageId, chainAfterSplit, leftBase);
        }
    }

    /// <summary>
    /// Gets the length of a delta chain (number of records before the consolidation base).
    /// </summary>
    private static int GetChainLength(BwTreeDeltaRecord<TKey, TValue>? chain)
    {
        int length = 0;
        var current = chain;
        while (current != null)
        {
            length++;
            if (current is ConsolidationRecord<TKey, TValue>)
                break;
            current = current.Next;
        }
        return length;
    }

    /// <summary>
    /// Releases resources held by the Bw-Tree.
    /// </summary>
    public void Dispose()
    {
        _epochManager.Dispose();
    }

    /// <summary>
    /// Result of searching a delta chain for a specific key.
    /// </summary>
    private readonly struct ChainSearchResult
    {
        /// <summary>Whether the key was found in the chain.</summary>
        public readonly bool Found;

        /// <summary>Whether the most recent operation on this key was a delete.</summary>
        public readonly bool Deleted;

        /// <summary>The value associated with the key (if found and not deleted).</summary>
        public readonly TValue? Value;

        /// <summary>Child page ID for internal node navigation, or -1.</summary>
        public readonly long ChildPageId;

        /// <summary>Whether the page containing this result is a leaf.</summary>
        public readonly bool IsLeaf;

        public ChainSearchResult(bool found, bool deleted, TValue? value, long childPageId, bool isLeaf)
        {
            Found = found;
            Deleted = deleted;
            Value = value;
            ChildPageId = childPageId;
            IsLeaf = isLeaf;
        }
    }
}
