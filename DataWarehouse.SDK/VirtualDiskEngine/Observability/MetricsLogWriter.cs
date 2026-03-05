using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Observability;

/// <summary>
/// Spec-compliant MLOG region writer for the DWVD v2.0 format (VOPT-68-70).
/// Appends 18-byte fixed-width metric entries and auto-compacts old entries by
/// downsampling within DownsampleFactor windows per MetricId.
/// </summary>
/// <remarks>
/// MLOG region header layout (38 bytes, all LE):
/// <code>
///   [RegionMagic:8]       0x4D45545249435300 ("METRICS\0")
///   [EntryCount:8]        uint64 total metric entries
///   [OldestTimestamp:8]   uint64 UTC nanoseconds
///   [NewestTimestamp:8]   uint64 UTC nanoseconds
///   [MetricIdCount:2]     uint16 distinct metric IDs
///   [RetentionSeconds:4]  uint32 retention period
///   [DownsampleFactor:2]  uint16 downsampling factor
/// </code>
///
/// Metric entry layout (18 bytes, all LE):
/// <code>
///   [Timestamp:8]   uint64 UTC nanoseconds
///   [MetricId:2]    uint16
///   [Value:8]       int64 or float64 depending on MetricId
/// </code>
///
/// Each MLOG block is closed with a 16-byte <see cref="UniversalBlockTrailer"/> using
/// <see cref="BlockTypeTags.Mlog"/> as the block type tag.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: MLOG region writer (VOPT-68-70)")]
public sealed class MetricsLogWriter
{
    // ── Constants ─────────────────────────────────────────────────────────

    /// <summary>MLOG region header size in bytes.</summary>
    public const int HeaderSize = 38;

    /// <summary>Metric entry size in bytes (Timestamp:8 + MetricId:2 + Value:8).</summary>
    public const int EntrySize = 18;

    /// <summary>MLOG region magic: "METRICS\0" = 0x4D45545249435300.</summary>
    public const ulong RegionMagic = 0x4D45545249435300UL;

    // ── State ─────────────────────────────────────────────────────────────

    private readonly List<(ulong Timestamp, ushort MetricId, long RawValue)> _entries = new();
    private readonly HashSet<ushort> _distinctIds = new();

    private ulong _oldestTimestamp;
    private ulong _newestTimestamp;

    // ── Configuration ─────────────────────────────────────────────────────

    /// <summary>Retention period in seconds. Entries older than this are eligible for compaction.</summary>
    public uint RetentionSeconds { get; }

    /// <summary>
    /// Number of entries per compaction window per MetricId.
    /// Old entries are grouped into windows of this size and replaced with averaged entries.
    /// </summary>
    public ushort DownsampleFactor { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="MetricsLogWriter"/>.
    /// </summary>
    /// <param name="retentionSeconds">
    /// Retention period in seconds (default 86400 = 24 hours).
    /// Entries older than this are eligible for compaction.
    /// </param>
    /// <param name="downsampleFactor">
    /// Window size for compaction averaging (default 60 entries per window).
    /// </param>
    public MetricsLogWriter(uint retentionSeconds = 86400, ushort downsampleFactor = 60)
    {
        if (downsampleFactor == 0)
            throw new ArgumentOutOfRangeException(nameof(downsampleFactor), "DownsampleFactor must be at least 1.");

        RetentionSeconds = retentionSeconds;
        DownsampleFactor = downsampleFactor;
    }

    // ── Properties ────────────────────────────────────────────────────────

    /// <summary>Current number of metric entries.</summary>
    public int EntryCount => _entries.Count;

    // ── Append ────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends an int64 metric entry.
    /// </summary>
    /// <param name="timestampNanos">UTC timestamp in nanoseconds.</param>
    /// <param name="metricId">Metric identifier (see <see cref="WellKnownMetricId"/>).</param>
    /// <param name="intValue">Raw int64 value.</param>
    public void AppendEntry(ulong timestampNanos, ushort metricId, long intValue)
    {
        _entries.Add((timestampNanos, metricId, intValue));
        _distinctIds.Add(metricId);
        UpdateTimestampBounds(timestampNanos);
    }

    /// <summary>
    /// Appends a float64 metric entry. The double value is stored as its bit-equivalent int64
    /// via <see cref="BitConverter.DoubleToInt64Bits"/> for binary-compatible serialization.
    /// </summary>
    /// <param name="timestampNanos">UTC timestamp in nanoseconds.</param>
    /// <param name="metricId">Metric identifier (see <see cref="WellKnownMetricId"/>).</param>
    /// <param name="floatValue">Float64 value.</param>
    public void AppendEntryFloat(ulong timestampNanos, ushort metricId, double floatValue)
    {
        long rawBits = BitConverter.DoubleToInt64Bits(floatValue);
        _entries.Add((timestampNanos, metricId, rawBits));
        _distinctIds.Add(metricId);
        UpdateTimestampBounds(timestampNanos);
    }

    // ── Compaction ────────────────────────────────────────────────────────

    /// <summary>
    /// Compacts entries older than <see cref="RetentionSeconds"/> by grouping them
    /// per MetricId into windows of <see cref="DownsampleFactor"/> entries and replacing
    /// each window with a single averaged entry. Recent entries (within the retention window)
    /// are preserved unchanged.
    /// </summary>
    public void RunCompaction()
    {
        if (_entries.Count == 0 || RetentionSeconds == 0)
            return;

        // Compute retention boundary in nanoseconds
        ulong nowNanos = _newestTimestamp;
        ulong retentionNanos = (ulong)RetentionSeconds * 1_000_000_000UL;
        ulong cutoffNanos = nowNanos > retentionNanos ? nowNanos - retentionNanos : 0UL;

        // Partition entries into old (eligible for compaction) and recent (preserve as-is)
        var oldEntries = new List<(ulong Timestamp, ushort MetricId, long RawValue)>();
        var recentEntries = new List<(ulong Timestamp, ushort MetricId, long RawValue)>();

        foreach (var entry in _entries)
        {
            if (entry.Timestamp < cutoffNanos)
                oldEntries.Add(entry);
            else
                recentEntries.Add(entry);
        }

        if (oldEntries.Count == 0)
            return;

        // Group old entries by MetricId, downsample each group
        var byMetric = new Dictionary<ushort, List<(ulong Timestamp, long RawValue)>>();
        foreach (var entry in oldEntries)
        {
            if (!byMetric.TryGetValue(entry.MetricId, out var list))
            {
                list = new List<(ulong, long)>();
                byMetric[entry.MetricId] = list;
            }
            list.Add((entry.Timestamp, entry.RawValue));
        }

        var compactedOld = new List<(ulong Timestamp, ushort MetricId, long RawValue)>();
        foreach (var (metricId, samples) in byMetric)
        {
            bool isFloat = WellKnownMetricId.IsFloat64Metric(metricId);
            int windowSize = DownsampleFactor;

            for (int i = 0; i < samples.Count; i += windowSize)
            {
                int end = Math.Min(i + windowSize, samples.Count);
                ulong windowTimestamp = samples[i].Timestamp;

                if (isFloat)
                {
                    // Average float64 values
                    double sum = 0.0;
                    for (int j = i; j < end; j++)
                        sum += BitConverter.Int64BitsToDouble(samples[j].RawValue);
                    double avg = sum / (end - i);
                    long avgBits = BitConverter.DoubleToInt64Bits(avg);
                    compactedOld.Add((windowTimestamp, metricId, avgBits));
                }
                else
                {
                    // Average int64 values (truncate fractional)
                    long sum = 0L;
                    for (int j = i; j < end; j++)
                        sum += samples[j].RawValue;
                    long avg = sum / (end - i);
                    compactedOld.Add((windowTimestamp, metricId, avg));
                }
            }
        }

        // Rebuild entry list: compacted old + recent (insertion-ordered is fine here)
        _entries.Clear();
        _entries.AddRange(compactedOld);
        _entries.AddRange(recentEntries);

        // Rebuild distinct ID set and timestamp bounds
        _distinctIds.Clear();
        _oldestTimestamp = 0;
        _newestTimestamp = 0;
        foreach (var e in _entries)
        {
            _distinctIds.Add(e.MetricId);
            UpdateTimestampBounds(e.Timestamp);
        }
    }

    // ── Query ─────────────────────────────────────────────────────────────

    /// <summary>Returns all entries as a read-only list of (Timestamp, MetricId, RawValue) tuples.</summary>
    public IReadOnlyList<(ulong Timestamp, ushort MetricId, long RawValue)> GetEntries() =>
        _entries.AsReadOnly();

    // ── Sizing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the number of blocks required to serialize all entries for the given block size.
    /// Always at least 1 (header block).
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        if (blockSize < UniversalBlockTrailer.Size + HeaderSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {UniversalBlockTrailer.Size + HeaderSize} bytes.");

        if (_entries.Count == 0)
            return 1;

        int payloadPerBlock = blockSize - UniversalBlockTrailer.Size;
        int availableInBlock0 = payloadPerBlock - HeaderSize;

        int totalEntryBytes = _entries.Count * EntrySize;

        if (totalEntryBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalEntryBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadPerBlock - 1) / payloadPerBlock;
        return 1 + overflowBlocks;
    }

    // ── Serialization ─────────────────────────────────────────────────────

    /// <summary>
    /// Serializes the MLOG region (header + all entries) into one or more MLOG blocks.
    /// Each block ends with a <see cref="UniversalBlockTrailer"/> using <see cref="BlockTypeTags.Mlog"/>.
    /// </summary>
    /// <param name="buffer">
    /// Target buffer. Must be at least <see cref="RequiredBlocks"/> * <paramref name="blockSize"/> bytes.
    /// </param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>
    /// A <see cref="ReadOnlySpan{T}"/> over the written bytes
    /// (<see cref="RequiredBlocks"/> * <paramref name="blockSize"/>).
    /// </returns>
    public ReadOnlySpan<byte> SerializeRegion(Span<byte> buffer, int blockSize)
    {
        int required = RequiredBlocks(blockSize);
        int totalSize = required * blockSize;

        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));

        buffer.Slice(0, totalSize).Clear();

        int payloadPerBlock = blockSize - UniversalBlockTrailer.Size;

        // ── Write region header into block 0 payload ──────────────────────
        var block0 = buffer.Slice(0, blockSize);

        BinaryPrimitives.WriteUInt64LittleEndian(block0, RegionMagic);
        BinaryPrimitives.WriteUInt64LittleEndian(block0.Slice(8), (ulong)_entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(block0.Slice(16), _oldestTimestamp);
        BinaryPrimitives.WriteUInt64LittleEndian(block0.Slice(24), _newestTimestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(block0.Slice(32), (ushort)_distinctIds.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(block0.Slice(34), RetentionSeconds);
        BinaryPrimitives.WriteUInt16LittleEndian(block0.Slice(36), DownsampleFactor);

        // ── Write entries ────────────────────────────────────────────────
        int currentBlock = 0;
        int offset = HeaderSize; // first entry starts after header in block 0

        for (int i = 0; i < _entries.Count; i++)
        {
            // Advance to next block if no room
            if (offset + EntrySize > payloadPerBlock)
            {
                UniversalBlockTrailer.Write(
                    buffer.Slice(currentBlock * blockSize, blockSize),
                    blockSize, BlockTypeTags.Mlog, generation: 0);
                currentBlock++;
                offset = 0;
            }

            var entrySpan = buffer.Slice(currentBlock * blockSize + offset, EntrySize);
            var (ts, metricId, rawValue) = _entries[i];
            BinaryPrimitives.WriteUInt64LittleEndian(entrySpan, ts);
            BinaryPrimitives.WriteUInt16LittleEndian(entrySpan.Slice(8), metricId);
            BinaryPrimitives.WriteInt64LittleEndian(entrySpan.Slice(10), rawValue);
            offset += EntrySize;
        }

        // Trailer for the last data block (and any remaining empty blocks)
        for (int blk = currentBlock; blk < required; blk++)
        {
            UniversalBlockTrailer.Write(
                buffer.Slice(blk * blockSize, blockSize),
                blockSize, BlockTypeTags.Mlog, generation: 0);
        }

        return buffer.Slice(0, totalSize);
    }

    // ── Deserialization ───────────────────────────────────────────────────

    /// <summary>
    /// Deserializes an MLOG region from one or more blocks.
    /// Validates the region magic and all block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks allocated to this region.</param>
    /// <returns>A populated <see cref="MetricsLogWriter"/> instance.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the region magic is invalid or a block trailer fails verification.
    /// </exception>
    public static MetricsLogWriter DeserializeRegion(
        ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        int totalSize = blockSize * blockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "At least one block is required.");
        if (blockSize < UniversalBlockTrailer.Size + HeaderSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {UniversalBlockTrailer.Size + HeaderSize} bytes.");

        // Verify all block trailers
        for (int blk = 0; blk < blockCount; blk++)
        {
            if (!UniversalBlockTrailer.Verify(buffer.Slice(blk * blockSize, blockSize), blockSize))
                throw new InvalidDataException($"MLOG block {blk} trailer verification failed.");
        }

        // Read and validate region header from block 0
        var block0 = buffer.Slice(0, blockSize);
        ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(block0);
        if (magic != RegionMagic)
            throw new InvalidDataException(
                $"MLOG region magic mismatch: expected 0x{RegionMagic:X16}, got 0x{magic:X16}.");

        ulong entryCount      = BinaryPrimitives.ReadUInt64LittleEndian(block0.Slice(8));
        ulong oldestTimestamp = BinaryPrimitives.ReadUInt64LittleEndian(block0.Slice(16));
        ulong newestTimestamp = BinaryPrimitives.ReadUInt64LittleEndian(block0.Slice(24));
        // MetricIdCount (2 bytes at offset 32) — informational, not used during load
        uint retentionSeconds  = BinaryPrimitives.ReadUInt32LittleEndian(block0.Slice(34));
        ushort downsampleFactor = BinaryPrimitives.ReadUInt16LittleEndian(block0.Slice(36));

        if (downsampleFactor == 0) downsampleFactor = 1; // guard against malformed on-disk data

        var writer = new MetricsLogWriter(retentionSeconds, downsampleFactor);

        // Read entries
        int payloadPerBlock = blockSize - UniversalBlockTrailer.Size;
        int currentBlock = 0;
        int offset = HeaderSize;

        for (ulong i = 0; i < entryCount; i++)
        {
            if (offset + EntrySize > payloadPerBlock)
            {
                currentBlock++;
                offset = 0;
            }

            if (currentBlock >= blockCount)
                throw new InvalidDataException("MLOG entry count exceeds available blocks.");

            var entrySpan = buffer.Slice(currentBlock * blockSize + offset, EntrySize);
            ulong ts        = BinaryPrimitives.ReadUInt64LittleEndian(entrySpan);
            ushort metricId = BinaryPrimitives.ReadUInt16LittleEndian(entrySpan.Slice(8));
            long rawValue   = BinaryPrimitives.ReadInt64LittleEndian(entrySpan.Slice(10));

            writer._entries.Add((ts, metricId, rawValue));
            writer._distinctIds.Add(metricId);
            offset += EntrySize;
        }

        // Restore timestamp bounds from stored header (avoids re-scan)
        writer._oldestTimestamp = oldestTimestamp;
        writer._newestTimestamp = newestTimestamp;

        return writer;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private void UpdateTimestampBounds(ulong timestampNanos)
    {
        if (_entries.Count == 1)
        {
            // First entry: initialize both bounds
            _oldestTimestamp = timestampNanos;
            _newestTimestamp = timestampNanos;
            return;
        }

        if (timestampNanos < _oldestTimestamp)
            _oldestTimestamp = timestampNanos;
        if (timestampNanos > _newestTimestamp)
            _newestTimestamp = timestampNanos;
    }
}
