using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Core LSM-Tree storage engine orchestrator.
    /// Manages MemTable, WAL, SSTables, and background compaction.
    /// </summary>
    public sealed class LsmTreeEngine : IAsyncDisposable
    {
        private readonly string _dataDirectory;
        private readonly LsmTreeOptions _options;
        private readonly SemaphoreSlim _writeLock;
        private readonly SemaphoreSlim _flushLock;

        private MemTable _memTable;
        private WalWriter _wal;
        private readonly List<SSTableReader> _sstables;
        private readonly CompactionManager _compactionManager;
        private bool _disposed;
        private bool _initialized;

        /// <summary>
        /// Initializes a new LSM-Tree engine.
        /// </summary>
        /// <param name="dataDirectory">Directory for storing data files.</param>
        /// <param name="options">Engine configuration options.</param>
        public LsmTreeEngine(string dataDirectory, LsmTreeOptions? options = null)
        {
            _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
            _options = options ?? new LsmTreeOptions();

            _memTable = new MemTable(_options.MemTableMaxSize);
            _wal = new WalWriter(Path.Combine(_dataDirectory, "wal.log"));
            _sstables = new List<SSTableReader>();
            _compactionManager = new CompactionManager(_dataDirectory);

            _writeLock = new SemaphoreSlim(1, 1);
            _flushLock = new SemaphoreSlim(1, 1);
        }

        /// <summary>
        /// Initializes the engine by replaying WAL and loading existing SSTables.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            if (_initialized)
            {
                return;
            }

            // Create data directory if it doesn't exist
            if (!Directory.Exists(_dataDirectory))
            {
                Directory.CreateDirectory(_dataDirectory);
            }

            // Replay WAL to recover MemTable
            await _wal.OpenAsync(ct);
            await foreach (var entry in _wal.ReplayAsync(ct))
            {
                if (entry.Op == WalOp.Put && entry.Value != null)
                {
                    _memTable.Put(entry.Key, entry.Value);
                }
                else if (entry.Op == WalOp.Delete)
                {
                    _memTable.Delete(entry.Key);
                }
            }

            // Load existing SSTables
            await LoadExistingSSTables(ct);

            _initialized = true;
        }

        private async Task LoadExistingSSTables(CancellationToken ct)
        {
            var sstableFiles = Directory.GetFiles(_dataDirectory, "sstable-*.sst")
                .OrderByDescending(f => File.GetCreationTimeUtc(f))
                .ToList();

            foreach (var filePath in sstableFiles)
            {
                try
                {
                    var reader = await SSTableReader.OpenAsync(filePath, ct);
                    _sstables.Add(reader);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LsmTreeEngine.LoadExistingSSTables] {ex.GetType().Name}: {ex.Message}");
                    // Skip corrupted SSTables
                }
            }
        }

        /// <summary>
        /// Puts a key-value pair into the LSM-Tree.
        /// </summary>
        public async Task PutAsync(byte[] key, byte[] value, CancellationToken ct = default)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Engine not initialized. Call InitializeAsync first.");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LsmTreeEngine));
            }

            await _writeLock.WaitAsync(ct);
            try
            {
                // Write to WAL first
                await _wal.AppendAsync(new WalEntry(key, value, WalOp.Put), ct);

                // Write to MemTable
                _memTable.Put(key, value);

                // Check if MemTable is full and needs flushing
                if (_memTable.IsFull)
                {
                    // Trigger flush in background using CancellationToken.None so the caller's
                    // token cancellation does not abort an in-progress flush and leave the engine
                    // in an inconsistent state.
                    _ = Task.Run(async () =>
                    {
                        try { await FlushMemTableAsync(CancellationToken.None); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LsmTreeEngine.PutAsync] background flush: {ex.GetType().Name}: {ex.Message}"); }
                    });
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Gets the value for a given key.
        /// </summary>
        public async Task<byte[]?> GetAsync(byte[] key, CancellationToken ct = default)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Engine not initialized. Call InitializeAsync first.");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LsmTreeEngine));
            }

            // Check MemTable first
            var memValue = _memTable.Get(key);
            if (memValue != null)
            {
                return memValue;
            }

            // Take a snapshot of the current SSTable list under lock to avoid races with
            // background flush/compaction threads that mutate _sstables.
            List<SSTableReader> sstableSnapshot;
            await _writeLock.WaitAsync(ct);
            try { sstableSnapshot = new List<SSTableReader>(_sstables); }
            finally { _writeLock.Release(); }

            // Check SSTables from newest to oldest
            foreach (var sstable in sstableSnapshot)
            {
                ct.ThrowIfCancellationRequested();
                var value = await sstable.GetAsync(key, ct);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// Deletes a key by writing a tombstone.
        /// </summary>
        public async Task DeleteAsync(byte[] key, CancellationToken ct = default)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Engine not initialized. Call InitializeAsync first.");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LsmTreeEngine));
            }

            await _writeLock.WaitAsync(ct);
            try
            {
                // Write to WAL
                await _wal.AppendAsync(new WalEntry(key, null, WalOp.Delete), ct);

                // Write tombstone to MemTable
                _memTable.Delete(key);

                // Check if MemTable is full
                if (_memTable.IsFull)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await FlushMemTableAsync(CancellationToken.None); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LsmTreeEngine.DeleteAsync] background flush: {ex.GetType().Name}: {ex.Message}"); }
                    });
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Scans for all entries with keys matching the given prefix.
        /// Merges results from MemTable and all SSTables.
        /// </summary>
        public async IAsyncEnumerable<KeyValuePair<byte[], byte[]>> ScanAsync(
            byte[] prefix,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Engine not initialized. Call InitializeAsync first.");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LsmTreeEngine));
            }

            // Collect all results in a dictionary to handle overwrites and tombstones
            var results = new SortedDictionary<byte[], byte[]?>(ByteArrayComparer.Instance);

            // Scan MemTable
            foreach (var kvp in _memTable.Scan(prefix))
            {
                results[kvp.Key] = kvp.Value;
            }

            // Take a snapshot of the current SSTable list under lock (same reason as GetAsync).
            List<SSTableReader> scanSnapshot;
            await _writeLock.WaitAsync(ct);
            try { scanSnapshot = new List<SSTableReader>(_sstables); }
            finally { _writeLock.Release(); }

            // Scan SSTables from newest to oldest
            foreach (var sstable in scanSnapshot)
            {
                await foreach (var kvp in sstable.ScanAsync(prefix, ct))
                {
                    if (!results.ContainsKey(kvp.Key))
                    {
                        results[kvp.Key] = kvp.Value;
                    }
                }
            }

            // Return non-tombstone entries
            foreach (var kvp in results)
            {
                if (kvp.Value != null)
                {
                    yield return new KeyValuePair<byte[], byte[]>(kvp.Key, kvp.Value);
                }
            }
        }

        /// <summary>
        /// Flushes the current MemTable to a Level 0 SSTable.
        /// </summary>
        private async Task FlushMemTableAsync(CancellationToken ct)
        {
            await _flushLock.WaitAsync(ct);
            try
            {
                if (_memTable.Count == 0)
                {
                    return;
                }

                // Get snapshot of MemTable
                var entries = _memTable.GetSortedEntries();

                // Create new SSTable
                var timestamp = DateTime.UtcNow.Ticks;
                var sstablePath = Path.Combine(_dataDirectory, $"sstable-L0-{timestamp}.sst");

                var sstable = await SSTableWriter.WriteAsync(entries, sstablePath, ct);
                sstable = sstable with { Level = 0 };

                // Open reader for the new SSTable
                var reader = await SSTableReader.OpenAsync(sstablePath, ct);

                // Add to front of list (newest first)
                await _writeLock.WaitAsync(ct);
                try
                {
                    _sstables.Insert(0, reader);

                    // Clear MemTable and truncate WAL
                    _memTable.Clear();
                    await _wal.TruncateAsync(ct);
                    await _wal.OpenAsync(ct);
                }
                finally
                {
                    _writeLock.Release();
                }

                // Check if compaction is needed
                if (_options.EnableBackgroundCompaction)
                {
                    var level0Size = _sstables
                        .Where(s => s.Metadata?.Level == 0)
                        .Sum(s => s.Metadata?.FileSize ?? 0);

                    if (_compactionManager.NeedsCompaction(0, level0Size))
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await CompactLevel0Async(CancellationToken.None); }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LsmTreeEngine.FlushMemTableAsync] background compaction: {ex.GetType().Name}: {ex.Message}"); }
                        });
                    }
                }
            }
            finally
            {
                _flushLock.Release();
            }
        }

        /// <summary>
        /// Compacts Level 0 SSTables into Level 1.
        /// </summary>
        private async Task CompactLevel0Async(CancellationToken ct)
        {
            try
            {
                var level0Tables = _sstables
                    .Where(s => s.Metadata?.Level == 0)
                    .Take(_options.Level0CompactionThreshold)
                    .ToList();

                if (level0Tables.Count < _options.Level0CompactionThreshold)
                {
                    return;
                }

                // Perform compaction
                var compactedTable = await _compactionManager.CompactAsync(
                    level0Tables,
                    targetLevel: 1,
                    _dataDirectory,
                    ct);

                // Open reader for compacted table
                var reader = await SSTableReader.OpenAsync(compactedTable.FilePath, ct);

                await _writeLock.WaitAsync(ct);
                try
                {
                    // Remove old tables and add new one
                    foreach (var oldTable in level0Tables)
                    {
                        _sstables.Remove(oldTable);
                        await oldTable.DisposeAsync();

                        // Delete old file
                        try
                        {
                            File.Delete(oldTable.Metadata!.FilePath);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[LsmTreeEngine.CompactLevel0Async] {ex.GetType().Name}: {ex.Message}");
                            // Ignore deletion errors
                        }
                    }

                    _sstables.Add(reader);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LsmTreeEngine.CompactLevel0Async] {ex.GetType().Name}: {ex.Message}");
                // Ignore compaction errors
            }
        }

        /// <summary>
        /// Disposes resources used by the engine.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Flush remaining data
            if (_memTable.Count > 0)
            {
                try
                {
                    await FlushMemTableAsync(CancellationToken.None);
                }
                catch
                {

                    // Best effort
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }

            // Dispose resources
            _memTable?.Dispose();
            await _wal.DisposeAsync();

            foreach (var sstable in _sstables)
            {
                await sstable.DisposeAsync();
            }

            _writeLock?.Dispose();
            _flushLock?.Dispose();
        }
    }
}
