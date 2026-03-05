using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Manages background compaction of SSTables.
    /// Implements leveled compaction strategy.
    /// </summary>
    public sealed class CompactionManager
    {
        private readonly string _dataDirectory;

        /// <summary>
        /// Initializes a new CompactionManager.
        /// </summary>
        /// <param name="dataDirectory">Base directory for SSTable files.</param>
        public CompactionManager(string dataDirectory)
        {
            _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        }

        /// <summary>
        /// Compacts multiple SSTables into a single SSTable at the target level.
        /// Uses K-way merge with priority queue for efficient merging.
        /// </summary>
        /// <param name="readers">SSTableReaders to compact.</param>
        /// <param name="targetLevel">Target level for the output SSTable.</param>
        /// <param name="outputDir">Output directory.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Newly created SSTable metadata.</returns>
        public async Task<SSTable> CompactAsync(
            List<SSTableReader> readers,
            int targetLevel,
            string outputDir,
            CancellationToken ct = default)
        {
            if (readers == null || readers.Count == 0)
            {
                throw new ArgumentException("No readers to compact", nameof(readers));
            }

            // Create output file path using timestamp + Guid to avoid filename collisions during rapid bursts.
            var timestamp = DateTime.UtcNow.Ticks;
            var unique = Guid.NewGuid().ToString("N")[..8];
            var outputPath = System.IO.Path.Combine(outputDir, $"sstable-L{targetLevel}-{timestamp}-{unique}.sst");

            // Perform K-way merge
            var mergedEntries = await KWayMergeAsync(readers, ct);

            // Write to new SSTable
            var sstable = await SSTableWriter.WriteAsync(mergedEntries, outputPath, ct);

            return sstable with { Level = targetLevel };
        }

        /// <summary>
        /// Performs K-way merge of multiple sorted streams.
        /// Removes tombstones and keeps only the most recent value for each key.
        /// Uses a min-heap (PriorityQueue) for O(n log k) merge instead of O(n log k * log k)
        /// from SortedDictionary.First() + Remove.
        /// </summary>
        private async Task<List<KeyValuePair<byte[], byte[]?>>> KWayMergeAsync(
            List<SSTableReader> readers,
            CancellationToken ct)
        {
            var result = new List<KeyValuePair<byte[], byte[]?>>();
            var enumerators = new List<IAsyncEnumerator<KeyValuePair<byte[], byte[]>>>();

            try
            {
                // Create enumerators for all readers
                foreach (var reader in readers)
                {
                    var enumerator = reader.ScanAsync(Array.Empty<byte>(), ct).GetAsyncEnumerator(ct);
                    if (await enumerator.MoveNextAsync())
                    {
                        enumerators.Add(enumerator);
                    }
                    else
                    {
                        await enumerator.DisposeAsync();
                    }
                }

                // PriorityQueue: O(log k) enqueue/dequeue vs SortedDictionary.First() O(1) but Remove O(log n).
                // PriorityQueue handles duplicate keys correctly by using (enumeratorIndex) as tiebreaker.
                // Element: (key, value, enumeratorIndex); Priority: (key, enumeratorIndex) for stable merge.
                var heap = new PriorityQueue<(byte[] key, byte[] value, int idx), (byte[] key, int idx)>(
                    Comparer<(byte[] key, int idx)>.Create((a, b) =>
                    {
                        int c = ByteArrayComparer.Instance.Compare(a.key, b.key);
                        return c != 0 ? c : a.idx.CompareTo(b.idx);
                    }));

                // Initialize heap with first element from each enumerator
                for (int i = 0; i < enumerators.Count; i++)
                {
                    var current = enumerators[i].Current;
                    heap.Enqueue((current.Key, current.Value, i), (current.Key, i));
                }

                byte[]? lastKey = null;

                while (heap.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    var (key, value, enumeratorIndex) = heap.Dequeue();

                    // Skip duplicate keys (keep only first occurrence = most recent from lowest reader index)
                    if (lastKey == null || !key.AsSpan().SequenceEqual(lastKey))
                    {
                        result.Add(new KeyValuePair<byte[], byte[]?>(key, value));
                        lastKey = key;
                    }

                    // Advance the enumerator that provided this element
                    if (await enumerators[enumeratorIndex].MoveNextAsync())
                    {
                        var next = enumerators[enumeratorIndex].Current;
                        heap.Enqueue((next.Key, next.Value, enumeratorIndex), (next.Key, enumeratorIndex));
                    }
                }

                return result;
            }
            finally
            {
                foreach (var enumerator in enumerators)
                {
                    await enumerator.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Determines if a level needs compaction based on size thresholds.
        /// </summary>
        /// <param name="level">Level to check.</param>
        /// <param name="currentSize">Current size of the level in bytes.</param>
        /// <returns>True if compaction is needed.</returns>
        public bool NeedsCompaction(int level, long currentSize)
        {
            // Level 0: trigger at 4 files
            if (level == 0)
            {
                return currentSize >= 4 * 4 * 1024 * 1024; // Approximate 4 files
            }

            // Level N: 10^N * 10MB base size
            var threshold = (long)Math.Pow(10, level) * 10 * 1024 * 1024;
            return currentSize >= threshold;
        }

        /// <summary>
        /// Gets the target size for a given level.
        /// </summary>
        /// <param name="level">Level number.</param>
        /// <returns>Target size in bytes.</returns>
        public long GetLevelTargetSize(int level)
        {
            if (level == 0)
            {
                return 4 * 4 * 1024 * 1024; // 4 SSTables
            }

            return (long)Math.Pow(10, level) * 10 * 1024 * 1024;
        }
    }
}
