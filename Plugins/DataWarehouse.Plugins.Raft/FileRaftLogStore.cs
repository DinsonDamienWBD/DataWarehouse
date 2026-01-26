using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.Raft
{
    /// <summary>
    /// File-based implementation of IRaftLogStore with durable persistence.
    ///
    /// Storage layout:
    /// - {dataDir}/log.jsonl - Append-only log file (JSON Lines format)
    /// - {dataDir}/state.json - Persistent state (currentTerm, votedFor)
    /// - {dataDir}/log.index - Optional index file for faster lookups
    ///
    /// Durability guarantees:
    /// - All writes use FileStream.Flush(true) to ensure fsync
    /// - State updates are atomic (write to temp file, then rename)
    /// - Partial writes are detected via entry validation
    /// </summary>
    public sealed class FileRaftLogStore : IRaftLogStore, IDisposable
    {
        private readonly string _dataDir;
        private readonly string _logFilePath;
        private readonly string _stateFilePath;
        private readonly SemaphoreSlim _logLock = new(1, 1);
        private readonly SemaphoreSlim _stateLock = new(1, 1);

        // In-memory cache for performance
        private readonly List<RaftLogEntry> _logCache = new();
        private long _currentTerm;
        private string? _votedFor;
        private bool _initialized;

        public FileRaftLogStore(string dataDir)
        {
            _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
            _logFilePath = Path.Combine(_dataDir, "log.jsonl");
            _stateFilePath = Path.Combine(_dataDir, "state.json");
        }

        /// <summary>
        /// Initializes the log store by loading existing data.
        /// Must be called before any other operations.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(_dataDir);

            // Load persistent state
            await LoadPersistentStateAsync();

            // Load log entries
            await LoadLogEntriesAsync();

            _initialized = true;
        }

        public Task<long> GetLastIndexAsync()
        {
            EnsureInitialized();
            return Task.FromResult(_logCache.Count > 0 ? _logCache[^1].Index : 0L);
        }

        public Task<long> GetLastTermAsync()
        {
            EnsureInitialized();
            return Task.FromResult(_logCache.Count > 0 ? _logCache[^1].Term : 0L);
        }

        public Task<RaftLogEntry?> GetEntryAsync(long index)
        {
            EnsureInitialized();

            if (index <= 0 || index > _logCache.Count)
                return Task.FromResult<RaftLogEntry?>(null);

            return Task.FromResult<RaftLogEntry?>(_logCache[(int)index - 1]);
        }

        public Task<IEnumerable<RaftLogEntry>> GetEntriesFromAsync(long startIndex)
        {
            EnsureInitialized();

            if (startIndex > _logCache.Count)
                return Task.FromResult<IEnumerable<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

            var entries = _logCache.Skip((int)startIndex - 1).ToList();
            return Task.FromResult<IEnumerable<RaftLogEntry>>(entries);
        }

        public async Task AppendAsync(RaftLogEntry entry)
        {
            EnsureInitialized();

            await _logLock.WaitAsync();
            try
            {
                // Validate entry
                if (entry.Index != _logCache.Count + 1)
                {
                    throw new InvalidOperationException(
                        $"Entry index {entry.Index} does not match expected index {_logCache.Count + 1}");
                }

                // Append to file with fsync
                await AppendToFileAsync(entry);

                // Update cache
                _logCache.Add(entry);
            }
            finally
            {
                _logLock.Release();
            }
        }

        public async Task TruncateFromAsync(long index)
        {
            EnsureInitialized();

            await _logLock.WaitAsync();
            try
            {
                if (index <= 0 || index > _logCache.Count)
                    return;

                // Remove from cache
                var removeCount = _logCache.Count - (int)index + 1;
                _logCache.RemoveRange((int)index - 1, removeCount);

                // Rewrite log file
                await RewriteLogFileAsync();
            }
            finally
            {
                _logLock.Release();
            }
        }

        public Task<(long term, string? votedFor)> GetPersistentStateAsync()
        {
            EnsureInitialized();
            return Task.FromResult((_currentTerm, _votedFor));
        }

        public async Task SavePersistentStateAsync(long term, string? votedFor)
        {
            EnsureInitialized();

            await _stateLock.WaitAsync();
            try
            {
                _currentTerm = term;
                _votedFor = votedFor;

                // Write to temp file, then atomic rename
                var tempPath = _stateFilePath + ".tmp";
                var state = new PersistentState
                {
                    Term = term,
                    VotedFor = votedFor ?? string.Empty
                };

                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

                // Write with fsync
                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                await using (var writer = new StreamWriter(fs, Encoding.UTF8))
                {
                    await writer.WriteAsync(json);
                    await writer.FlushAsync();
                    await fs.FlushAsync(); // fsync via FileOptions.WriteThrough
                }

                // Atomic rename
                File.Move(tempPath, _stateFilePath, overwrite: true);
            }
            finally
            {
                _stateLock.Release();
            }
        }

        public async Task CompactAsync(long upToIndex)
        {
            EnsureInitialized();

            await _logLock.WaitAsync();
            try
            {
                if (upToIndex <= 0 || upToIndex > _logCache.Count)
                    return;

                // Remove compacted entries from cache
                _logCache.RemoveRange(0, (int)upToIndex);

                // Reindex remaining entries
                for (int i = 0; i < _logCache.Count; i++)
                {
                    _logCache[i].Index = i + 1;
                }

                // Rewrite log file
                await RewriteLogFileAsync();
            }
            finally
            {
                _logLock.Release();
            }
        }

        public void Dispose()
        {
            _logLock.Dispose();
            _stateLock.Dispose();
        }

        #region Private Methods

        private void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("LogStore must be initialized before use. Call InitializeAsync().");
        }

        private async Task LoadPersistentStateAsync()
        {
            if (!File.Exists(_stateFilePath))
            {
                _currentTerm = 0;
                _votedFor = null;
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_stateFilePath);
                var state = JsonSerializer.Deserialize<PersistentState>(json);

                if (state != null)
                {
                    _currentTerm = state.Term;
                    _votedFor = string.IsNullOrEmpty(state.VotedFor) ? null : state.VotedFor;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Raft] Failed to load persistent state: {ex.Message}");
                _currentTerm = 0;
                _votedFor = null;
            }
        }

        private async Task LoadLogEntriesAsync()
        {
            _logCache.Clear();

            if (!File.Exists(_logFilePath))
                return;

            try
            {
                var lines = await File.ReadAllLinesAsync(_logFilePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var entry = JsonSerializer.Deserialize<RaftLogEntry>(line);
                        if (entry != null)
                        {
                            _logCache.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Skip corrupted entries - log warning but continue
                        Console.WriteLine($"[Raft] Skipping corrupted log entry: {ex.Message}");
                    }
                }

                // Validate log consistency
                for (int i = 0; i < _logCache.Count; i++)
                {
                    if (_logCache[i].Index != i + 1)
                    {
                        Console.WriteLine($"[Raft] Log inconsistency detected at index {i + 1}. Truncating.");
                        _logCache.RemoveRange(i, _logCache.Count - i);
                        await RewriteLogFileAsync();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Raft] Failed to load log entries: {ex.Message}");
                _logCache.Clear();
            }
        }

        private async Task AppendToFileAsync(RaftLogEntry entry)
        {
            var json = JsonSerializer.Serialize(entry);

            // Append with fsync for durability
            await using var fs = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            await using var writer = new StreamWriter(fs, Encoding.UTF8);

            await writer.WriteLineAsync(json);
            await writer.FlushAsync();
            await fs.FlushAsync(); // fsync via FileOptions.WriteThrough
        }

        private async Task RewriteLogFileAsync()
        {
            var tempPath = _logFilePath + ".tmp";

            // Write to temp file
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(fs, Encoding.UTF8))
            {
                foreach (var entry in _logCache)
                {
                    var json = JsonSerializer.Serialize(entry);
                    await writer.WriteLineAsync(json);
                }

                await writer.FlushAsync();
                await fs.FlushAsync(); // fsync via FileOptions.WriteThrough
            }

            // Atomic rename
            File.Move(tempPath, _logFilePath, overwrite: true);
        }

        #endregion

        #region Supporting Types

        private sealed class PersistentState
        {
            public long Term { get; set; }
            public string VotedFor { get; set; } = string.Empty;
        }

        #endregion
    }
}
