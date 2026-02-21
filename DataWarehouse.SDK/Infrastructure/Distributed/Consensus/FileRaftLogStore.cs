using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.Distributed
{
    /// <summary>
    /// File-based implementation of <see cref="IRaftLogStore"/> with durable persistence.
    /// Migrated from the obsolete Raft plugin into the SDK for use by all plugins via SDK contracts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Storage layout:
    /// <list type="bullet">
    /// <item><description><c>{dataDir}/log.jsonl</c> — Append-only log file (JSON Lines format)</description></item>
    /// <item><description><c>{dataDir}/state.json</c> — Persistent Raft state (currentTerm, votedFor)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Durability guarantees:
    /// <list type="bullet">
    /// <item><description>All writes use <see cref="FileOptions.WriteThrough"/> to ensure fsync before returning.</description></item>
    /// <item><description>State updates are atomic: write to a temp file, then rename over the target.</description></item>
    /// <item><description>Partial writes are detected via index-sequence validation on recovery.</description></item>
    /// <item><description>Corruption is detected and logged; corrupted entries are skipped on load.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Callers must invoke <see cref="InitializeAsync"/> before any other operations.
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 65.2: File-based durable Raft log store migrated from obsolete Raft plugin")]
    public sealed class FileRaftLogStore : IRaftLogStore, IDisposable
    {
        private readonly string _dataDir;
        private readonly string _logFilePath;
        private readonly string _stateFilePath;
        private readonly SemaphoreSlim _logLock = new(1, 1);
        private readonly SemaphoreSlim _stateLock = new(1, 1);
        private readonly ILogger<FileRaftLogStore>? _logger;

        // In-memory cache for performance
        private readonly List<RaftLogEntry> _logCache = new();
        private long _currentTerm;
        private string? _votedFor;
        private bool _initialized;

        /// <summary>
        /// Initializes a new instance of <see cref="FileRaftLogStore"/>.
        /// </summary>
        /// <param name="dataDir">Directory where log and state files will be stored.</param>
        /// <param name="logger">Optional logger for diagnostics. If null, warnings are silently dropped.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="dataDir"/> is null.</exception>
        public FileRaftLogStore(string dataDir, ILogger<FileRaftLogStore>? logger = null)
        {
            _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
            _logFilePath = Path.Combine(_dataDir, "log.jsonl");
            _stateFilePath = Path.Combine(_dataDir, "state.json");
            _logger = logger;
        }

        /// <summary>
        /// Initializes the log store by loading existing data from disk.
        /// Must be called before any other operations.
        /// </summary>
        /// <returns>A task representing the asynchronous initialization.</returns>
        public async Task InitializeAsync()
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(_dataDir);

            // Load persistent state first (currentTerm, votedFor)
            await LoadPersistentStateAsync().ConfigureAwait(false);

            // Load log entries, repairing minor corruption
            await LoadLogEntriesAsync().ConfigureAwait(false);

            _initialized = true;
        }

        /// <inheritdoc/>
        public long Count
        {
            get
            {
                EnsureInitialized();
                return _logCache.Count;
            }
        }

        /// <inheritdoc/>
        public Task<long> GetLastIndexAsync()
        {
            EnsureInitialized();
            return Task.FromResult(_logCache.Count > 0 ? _logCache[^1].Index : 0L);
        }

        /// <inheritdoc/>
        public Task<long> GetLastTermAsync()
        {
            EnsureInitialized();
            return Task.FromResult(_logCache.Count > 0 ? _logCache[^1].Term : 0L);
        }

        /// <inheritdoc/>
        public Task<RaftLogEntry?> GetAsync(long index)
        {
            EnsureInitialized();

            if (index <= 0 || index > _logCache.Count)
                return Task.FromResult<RaftLogEntry?>(null);

            return Task.FromResult<RaftLogEntry?>(_logCache[(int)index - 1]);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<RaftLogEntry>> GetRangeAsync(long fromIndex, long toIndex)
        {
            EnsureInitialized();

            if (fromIndex > _logCache.Count || fromIndex > toIndex)
                return Task.FromResult<IReadOnlyList<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

            int start = (int)Math.Max(0, fromIndex - 1);
            int end = (int)Math.Min(_logCache.Count, toIndex);
            int count = end - start;

            if (count <= 0)
                return Task.FromResult<IReadOnlyList<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

            return Task.FromResult<IReadOnlyList<RaftLogEntry>>(
                _logCache.GetRange(start, count).AsReadOnly());
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<RaftLogEntry>> GetFromAsync(long fromIndex)
        {
            EnsureInitialized();

            if (fromIndex > _logCache.Count)
                return Task.FromResult<IReadOnlyList<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

            int start = (int)Math.Max(0, fromIndex - 1);
            return Task.FromResult<IReadOnlyList<RaftLogEntry>>(
                _logCache.GetRange(start, _logCache.Count - start).AsReadOnly());
        }

        /// <inheritdoc/>
        public async Task AppendAsync(RaftLogEntry entry)
        {
            EnsureInitialized();
            ArgumentNullException.ThrowIfNull(entry);

            await _logLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Validate entry index matches expected sequence
                if (entry.Index != _logCache.Count + 1)
                {
                    throw new InvalidOperationException(
                        $"Entry index {entry.Index} does not match expected index {_logCache.Count + 1}");
                }

                // Append to file with fsync durability
                await AppendToFileAsync(entry).ConfigureAwait(false);

                // Update in-memory cache
                _logCache.Add(entry);
            }
            finally
            {
                _logLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task TruncateFromAsync(long fromIndex)
        {
            EnsureInitialized();

            await _logLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (fromIndex <= 0 || fromIndex > _logCache.Count)
                    return;

                int removeCount = _logCache.Count - (int)fromIndex + 1;
                _logCache.RemoveRange((int)fromIndex - 1, removeCount);

                // Rewrite log file atomically
                await RewriteLogFileAsync().ConfigureAwait(false);
            }
            finally
            {
                _logLock.Release();
            }
        }

        /// <inheritdoc/>
        public Task<(long term, string? votedFor)> GetPersistentStateAsync()
        {
            EnsureInitialized();
            return Task.FromResult((_currentTerm, _votedFor));
        }

        /// <inheritdoc/>
        public async Task SavePersistentStateAsync(long term, string? votedFor)
        {
            EnsureInitialized();

            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _currentTerm = term;
                _votedFor = votedFor;

                // Write to temp file, then atomic rename for crash safety
                var tempPath = _stateFilePath + ".tmp";
                var state = new PersistentState
                {
                    Term = term,
                    VotedFor = votedFor ?? string.Empty
                };

                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

                // Write with fsync via FileOptions.WriteThrough
                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                await using (var writer = new StreamWriter(fs, Encoding.UTF8))
                {
                    await writer.WriteAsync(json).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    await fs.FlushAsync().ConfigureAwait(false);
                }

                // Atomic rename over the target
                File.Move(tempPath, _stateFilePath, overwrite: true);
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task CompactAsync(long upToIndex)
        {
            EnsureInitialized();

            await _logLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (upToIndex <= 0 || upToIndex > _logCache.Count)
                    return;

                // Remove compacted entries from cache
                _logCache.RemoveRange(0, (int)upToIndex);

                // Re-index remaining entries starting from 1
                for (int i = 0; i < _logCache.Count; i++)
                {
                    _logCache[i].Index = i + 1;
                }

                // Rewrite log file to reflect compacted state
                await RewriteLogFileAsync().ConfigureAwait(false);
            }
            finally
            {
                _logLock.Release();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _logLock.Dispose();
            _stateLock.Dispose();
        }

        #region Private Methods

        private void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException(
                    "FileRaftLogStore must be initialized before use. Call InitializeAsync() first.");
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
                var json = await File.ReadAllTextAsync(_stateFilePath).ConfigureAwait(false);
                var state = JsonSerializer.Deserialize<PersistentState>(json);

                if (state != null)
                {
                    _currentTerm = state.Term;
                    _votedFor = string.IsNullOrEmpty(state.VotedFor) ? null : state.VotedFor;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Raft] Failed to load persistent state from {Path}; defaulting to term=0, votedFor=null", _stateFilePath);
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
                var lines = await File.ReadAllLinesAsync(_logFilePath).ConfigureAwait(false);
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
                        // Skip corrupted entries but log the event for diagnostics
                        _logger?.LogWarning(ex, "[Raft] Skipping corrupted log entry in {Path}", _logFilePath);
                    }
                }

                // Validate log index consistency; truncate on first gap detected
                for (int i = 0; i < _logCache.Count; i++)
                {
                    if (_logCache[i].Index != i + 1)
                    {
                        _logger?.LogWarning("[Raft] Log inconsistency at position {Position} (expected index {Expected}, got {Actual}). Truncating.", i + 1, i + 1, _logCache[i].Index);
                        _logCache.RemoveRange(i, _logCache.Count - i);
                        await RewriteLogFileAsync().ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Raft] Failed to load log entries from {Path}; starting with empty log", _logFilePath);
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

            await writer.WriteLineAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            await fs.FlushAsync().ConfigureAwait(false);
        }

        private async Task RewriteLogFileAsync()
        {
            var tempPath = _logFilePath + ".tmp";

            // Write all current entries to a temp file
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(fs, Encoding.UTF8))
            {
                foreach (var entry in _logCache)
                {
                    var json = JsonSerializer.Serialize(entry);
                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                }

                await writer.FlushAsync().ConfigureAwait(false);
                await fs.FlushAsync().ConfigureAwait(false);
            }

            // Atomic rename over the live log file
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
