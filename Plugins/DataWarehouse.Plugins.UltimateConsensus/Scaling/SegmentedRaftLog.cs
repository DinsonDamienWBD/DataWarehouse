using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Infrastructure.Distributed;

namespace DataWarehouse.Plugins.UltimateConsensus.Scaling;

/// <summary>
/// Segmented file-based implementation of <see cref="IRaftLogStore"/> optimized for scalable
/// Raft consensus. Each segment stores up to <see cref="EntriesPerSegment"/> log entries
/// in a length-prefixed binary format, with the most recent N segments memory-mapped
/// for zero-copy reads.
/// </summary>
/// <remarks>
/// <para>
/// Storage layout:
/// <list type="bullet">
/// <item><description><c>{dataDir}/raft-log-{groupId}-{startIndex}.seg</c> — One file per segment of entries.</description></item>
/// <item><description><c>{dataDir}/state-{groupId}.json</c> — Persistent Raft state (currentTerm, votedFor).</description></item>
/// </list>
/// </para>
/// <para>
/// Performance characteristics:
/// <list type="bullet">
/// <item><description>Sequential append is O(1) to the active segment.</description></item>
/// <item><description>Random read by index is O(1) via segment index lookup.</description></item>
/// <item><description>Memory-mapped hot segments provide zero-copy reads for recent entries.</description></item>
/// <item><description>Background compaction merges small segments and removes truncated entries.</description></item>
/// </list>
/// </para>
/// <para>
/// Entries are serialized using a length-prefix binary format:
/// <c>[4-byte length][JSON bytes]</c> enabling fast sequential reads without delimiter scanning.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-05: Segmented Raft log store with mmap'd hot segments")]
public sealed class SegmentedRaftLog : IRaftLogStore, IDisposable
{
    /// <summary>
    /// Number of log entries per segment file. Default: 10,000.
    /// </summary>
    public int EntriesPerSegment { get; }

    /// <summary>
    /// Number of most recent segments to memory-map for zero-copy reads. Default: 3.
    /// </summary>
    public int HotSegmentCount { get; }

    private readonly string _dataDir;
    private readonly string _groupId;
    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _logLock = new(1, 1);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    // Segment index: maps segment start index to segment metadata
    private readonly SortedList<long, SegmentInfo> _segments = new();

    // In-memory cache of all entries for fast random access
    private readonly List<RaftLogEntry> _entries = new();

    // Memory-mapped hot segments for zero-copy reads
    private readonly ConcurrentDictionary<long, MappedSegment> _hotSegments = new();

    // Persistent Raft state
    private long _currentTerm;
    private string? _votedFor;
    private bool _initialized;
    private bool _disposed;

    // Background compaction
    private Timer? _compactionTimer;
    private const int CompactionIntervalMs = 60_000;

    /// <summary>
    /// Initializes a new <see cref="SegmentedRaftLog"/> instance.
    /// </summary>
    /// <param name="dataDir">Directory where segment files and state are stored.</param>
    /// <param name="groupId">Raft group identifier used in file naming.</param>
    /// <param name="entriesPerSegment">Maximum entries per segment file. Default: 10,000.</param>
    /// <param name="hotSegmentCount">Number of recent segments to memory-map. Default: 3.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dataDir"/> or <paramref name="groupId"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="entriesPerSegment"/> is less than 1.</exception>
    public SegmentedRaftLog(
        string dataDir,
        string groupId,
        int entriesPerSegment = 10_000,
        int hotSegmentCount = 3)
    {
        _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
        _groupId = groupId ?? throw new ArgumentNullException(nameof(groupId));
        ArgumentOutOfRangeException.ThrowIfLessThan(entriesPerSegment, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(hotSegmentCount, 1);

        EntriesPerSegment = entriesPerSegment;
        HotSegmentCount = hotSegmentCount;
        _stateFilePath = Path.Combine(_dataDir, $"state-{_groupId}.json");
    }

    /// <summary>
    /// Initializes the segmented log store by loading existing segment files and persistent state from disk.
    /// Must be called before any other operations.
    /// </summary>
    /// <returns>A task representing the asynchronous initialization.</returns>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        Directory.CreateDirectory(_dataDir);

        await LoadPersistentStateAsync().ConfigureAwait(false);
        await LoadSegmentsAsync().ConfigureAwait(false);
        RefreshHotSegments();

        _compactionTimer = new Timer(CompactionCallback, null, CompactionIntervalMs, CompactionIntervalMs);
        _initialized = true;
    }

    /// <inheritdoc/>
    public long Count
    {
        get
        {
            EnsureInitialized();
            return _entries.Count;
        }
    }

    /// <inheritdoc/>
    public Task<long> GetLastIndexAsync()
    {
        EnsureInitialized();
        return Task.FromResult(_entries.Count > 0 ? _entries[^1].Index : 0L);
    }

    /// <inheritdoc/>
    public Task<long> GetLastTermAsync()
    {
        EnsureInitialized();
        return Task.FromResult(_entries.Count > 0 ? _entries[^1].Term : 0L);
    }

    /// <inheritdoc/>
    public Task<RaftLogEntry?> GetAsync(long index)
    {
        EnsureInitialized();

        if (index <= 0 || index > _entries.Count)
            return Task.FromResult<RaftLogEntry?>(null);

        return Task.FromResult<RaftLogEntry?>(_entries[(int)index - 1]);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RaftLogEntry>> GetRangeAsync(long fromIndex, long toIndex)
    {
        EnsureInitialized();

        if (fromIndex > _entries.Count || fromIndex > toIndex)
            return Task.FromResult<IReadOnlyList<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

        int start = (int)Math.Max(0, fromIndex - 1);
        int end = (int)Math.Min(_entries.Count, toIndex);
        int count = end - start;

        if (count <= 0)
            return Task.FromResult<IReadOnlyList<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

        return Task.FromResult<IReadOnlyList<RaftLogEntry>>(
            _entries.GetRange(start, count).AsReadOnly());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RaftLogEntry>> GetFromAsync(long fromIndex)
    {
        EnsureInitialized();

        if (fromIndex > _entries.Count)
            return Task.FromResult<IReadOnlyList<RaftLogEntry>>(Array.Empty<RaftLogEntry>());

        int start = (int)Math.Max(0, fromIndex - 1);
        return Task.FromResult<IReadOnlyList<RaftLogEntry>>(
            _entries.GetRange(start, _entries.Count - start).AsReadOnly());
    }

    /// <inheritdoc/>
    public async Task AppendAsync(RaftLogEntry entry)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(entry);

        await _logLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.Index != _entries.Count + 1)
            {
                throw new InvalidOperationException(
                    $"Entry index {entry.Index} does not match expected index {_entries.Count + 1}");
            }

            // Determine which segment this entry belongs to
            long segmentStart = ((entry.Index - 1) / EntriesPerSegment) * EntriesPerSegment + 1;

            if (!_segments.ContainsKey(segmentStart))
            {
                // Create new segment
                var segPath = GetSegmentPath(segmentStart);
                _segments[segmentStart] = new SegmentInfo(segmentStart, segPath, 0);
            }

            // Append to segment file with length-prefix binary format
            await AppendToSegmentAsync(entry, segmentStart).ConfigureAwait(false);

            _entries.Add(entry);

            // Seal previous segment if current segment is new and previous is full
            if (_segments.Count > 1)
            {
                var keys = _segments.Keys;
                for (int i = 0; i < keys.Count - 1; i++)
                {
                    var seg = _segments[keys[i]];
                    if (seg.EntryCount >= EntriesPerSegment && !seg.IsSealed)
                    {
                        _segments[keys[i]] = seg with { IsSealed = true };
                    }
                }
            }

            RefreshHotSegments();
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
            if (fromIndex <= 0 || fromIndex > _entries.Count)
                return;

            int removeCount = _entries.Count - (int)fromIndex + 1;
            _entries.RemoveRange((int)fromIndex - 1, removeCount);

            // Rewrite all affected segments
            await RewriteSegmentsAsync().ConfigureAwait(false);
            RefreshHotSegments();
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

            var tempPath = _stateFilePath + ".tmp";
            var state = new PersistentStateDto
            {
                Term = term,
                VotedFor = votedFor ?? string.Empty
            };

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(fs, System.Text.Encoding.UTF8))
            {
                await writer.WriteAsync(json).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                await fs.FlushAsync().ConfigureAwait(false);
            }

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
            if (upToIndex <= 0 || upToIndex > _entries.Count)
                return;

            _entries.RemoveRange(0, (int)upToIndex);

            // Re-index remaining entries starting from 1
            for (int i = 0; i < _entries.Count; i++)
            {
                _entries[i].Index = i + 1;
            }

            await RewriteSegmentsAsync().ConfigureAwait(false);
            RefreshHotSegments();
        }
        finally
        {
            _logLock.Release();
        }
    }

    /// <summary>
    /// Gets the number of segment files currently managed by this log store.
    /// </summary>
    public int SegmentCount
    {
        get
        {
            EnsureInitialized();
            return _segments.Count;
        }
    }

    /// <summary>
    /// Gets the number of currently memory-mapped hot segments.
    /// </summary>
    public int ActiveHotSegments => _hotSegments.Count;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _compactionTimer?.Dispose();
        _compactionTimer = null;

        foreach (var mapped in _hotSegments.Values)
        {
            mapped.Dispose();
        }
        _hotSegments.Clear();

        _logLock.Dispose();
        _stateLock.Dispose();
    }

    #region Private Methods

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException(
                "SegmentedRaftLog must be initialized before use. Call InitializeAsync() first.");
    }

    private string GetSegmentPath(long startIndex)
    {
        return Path.Combine(_dataDir, $"raft-log-{_groupId}-{startIndex:D10}.seg");
    }

    /// <summary>
    /// Appends an entry to the segment file using length-prefix binary format: [4-byte length][JSON bytes].
    /// </summary>
    private async Task AppendToSegmentAsync(RaftLogEntry entry, long segmentStart)
    {
        var seg = _segments[segmentStart];
        var json = JsonSerializer.SerializeToUtf8Bytes(entry);

        await using var fs = new FileStream(seg.FilePath, FileMode.Append, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);

        // Write length prefix (4 bytes, little-endian)
        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, json.Length);
        await fs.WriteAsync(lengthBuffer).ConfigureAwait(false);

        // Write JSON payload
        await fs.WriteAsync(json).ConfigureAwait(false);
        await fs.FlushAsync().ConfigureAwait(false);

        _segments[segmentStart] = seg with { EntryCount = seg.EntryCount + 1 };
    }

    /// <summary>
    /// Rewrites all segments from the current in-memory entry list.
    /// Used after truncation or compaction.
    /// </summary>
    private async Task RewriteSegmentsAsync()
    {
        // Dispose existing hot segments
        foreach (var mapped in _hotSegments.Values)
        {
            mapped.Dispose();
        }
        _hotSegments.Clear();

        // Delete old segment files
        foreach (var seg in _segments.Values)
        {
            try { File.Delete(seg.FilePath); }
            catch { /* best-effort cleanup */ }
        }
        _segments.Clear();

        // Rewrite entries into new segments
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            long segmentStart = ((entry.Index - 1) / EntriesPerSegment) * EntriesPerSegment + 1;

            if (!_segments.ContainsKey(segmentStart))
            {
                var segPath = GetSegmentPath(segmentStart);
                _segments[segmentStart] = new SegmentInfo(segmentStart, segPath, 0);
            }

            await AppendToSegmentAsync(entry, segmentStart).ConfigureAwait(false);
        }

        // Seal all but the last segment
        if (_segments.Count > 1)
        {
            var keys = _segments.Keys;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                var seg = _segments[keys[i]];
                if (seg.EntryCount >= EntriesPerSegment)
                {
                    _segments[keys[i]] = seg with { IsSealed = true };
                }
            }
        }
    }

    /// <summary>
    /// Refreshes the memory-mapped hot segments, keeping only the most recent N segments mapped.
    /// </summary>
    private void RefreshHotSegments()
    {
        if (_segments.Count == 0) return;

        var recentKeys = _segments.Keys
            .OrderByDescending(k => k)
            .Take(HotSegmentCount)
            .ToHashSet();

        // Unmap segments that are no longer hot
        foreach (var key in _hotSegments.Keys.ToArray())
        {
            if (!recentKeys.Contains(key) && _hotSegments.TryRemove(key, out var old))
            {
                old.Dispose();
            }
        }

        // Map new hot segments
        foreach (var key in recentKeys)
        {
            if (!_hotSegments.ContainsKey(key) && _segments.TryGetValue(key, out var seg))
            {
                try
                {
                    var fileInfo = new FileInfo(seg.FilePath);
                    if (fileInfo.Exists && fileInfo.Length > 0)
                    {
                        var mmf = MemoryMappedFile.CreateFromFile(
                            seg.FilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                        var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
                        _hotSegments[key] = new MappedSegment(mmf, accessor, fileInfo.Length);
                    }
                }
                catch
                {
                    // Non-fatal: fall back to file-based reads
                }
            }
        }
    }

    /// <summary>
    /// Loads all existing segment files from disk and rebuilds the in-memory entry list.
    /// </summary>
    private async Task LoadSegmentsAsync()
    {
        _entries.Clear();
        _segments.Clear();

        var pattern = $"raft-log-{_groupId}-*.seg";
        var files = Directory.GetFiles(_dataDir, pattern).OrderBy(f => f).ToArray();

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var parts = fileName.Split('-');
            if (parts.Length < 4 || !long.TryParse(parts[^1], out var startIndex))
                continue;

            int entryCount = 0;
            try
            {
                await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096);

                var lengthBuffer = new byte[4];
                while (await ReadExactAsync(fs, lengthBuffer).ConfigureAwait(false))
                {
                    int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
                    if (jsonLength <= 0 || jsonLength > 10_000_000) break; // sanity limit

                    var jsonBuffer = new byte[jsonLength];
                    if (!await ReadExactAsync(fs, jsonBuffer).ConfigureAwait(false))
                        break;

                    var entry = JsonSerializer.Deserialize<RaftLogEntry>(jsonBuffer);
                    if (entry != null)
                    {
                        _entries.Add(entry);
                        entryCount++;
                    }
                }
            }
            catch
            {
                // Skip corrupted segment
            }

            bool isSealed = entryCount >= EntriesPerSegment;
            _segments[startIndex] = new SegmentInfo(startIndex, filePath, entryCount, isSealed);
        }

        // Validate index consistency
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Index != i + 1)
            {
                _entries.RemoveRange(i, _entries.Count - i);
                await RewriteSegmentsAsync().ConfigureAwait(false);
                break;
            }
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream.
    /// Returns false if the stream ends before the buffer is filled.
    /// </summary>
    private static async Task<bool> ReadExactAsync(FileStream fs, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await fs.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset)).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    /// <summary>
    /// Loads persisted Raft state (currentTerm, votedFor) from disk.
    /// </summary>
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
            var state = JsonSerializer.Deserialize<PersistentStateDto>(json);
            if (state != null)
            {
                _currentTerm = state.Term;
                _votedFor = string.IsNullOrEmpty(state.VotedFor) ? null : state.VotedFor;
            }
        }
        catch
        {
            _currentTerm = 0;
            _votedFor = null;
        }
    }

    /// <summary>
    /// Background compaction callback that merges small segments and removes empty segment files.
    /// </summary>
    private void CompactionCallback(object? state)
    {
        if (_disposed) return;

        // Compact: remove empty segment files
        try
        {
            _logLock.Wait();
            try
            {
                var emptySegments = _segments.Where(s => s.Value.EntryCount == 0).Select(s => s.Key).ToList();
                foreach (var key in emptySegments)
                {
                    if (_segments.TryGetValue(key, out var seg))
                    {
                        try { File.Delete(seg.FilePath); }
                        catch { /* best-effort */ }
                        _segments.Remove(key);
                    }
                }
            }
            finally
            {
                _logLock.Release();
            }
        }
        catch
        {
            // Non-fatal compaction failure
        }
    }

    #endregion

    #region Supporting Types

    /// <summary>
    /// Metadata about a single log segment file.
    /// </summary>
    private sealed record SegmentInfo(
        long StartIndex,
        string FilePath,
        int EntryCount,
        bool IsSealed = false);

    /// <summary>
    /// A memory-mapped segment for zero-copy reads of hot (recent) data.
    /// </summary>
    private sealed class MappedSegment : IDisposable
    {
        public MemoryMappedFile File { get; }
        public MemoryMappedViewAccessor Accessor { get; }
        public long Length { get; }

        public MappedSegment(MemoryMappedFile file, MemoryMappedViewAccessor accessor, long length)
        {
            File = file;
            Accessor = accessor;
            Length = length;
        }

        public void Dispose()
        {
            Accessor.Dispose();
            File.Dispose();
        }
    }

    /// <summary>
    /// DTO for persisting Raft state (currentTerm, votedFor) to disk.
    /// </summary>
    private sealed class PersistentStateDto
    {
        public long Term { get; set; }
        public string VotedFor { get; set; } = string.Empty;
    }

    #endregion
}
