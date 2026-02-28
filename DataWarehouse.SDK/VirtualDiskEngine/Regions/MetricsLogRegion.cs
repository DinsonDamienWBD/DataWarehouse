using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single time-series metric sample recording a value for a user-defined metric
/// at a specific point in time.
/// </summary>
/// <remarks>
/// Serialized layout (19 bytes, all LE):
/// [MetricId:2][TimestampUtcTicks:8][Value:8][AggregationType:1]
/// </remarks>
[SdkCompatibility("6.0.0")]
public readonly record struct MetricsSample
{
    /// <summary>Serialized size of one sample in bytes.</summary>
    public const int SerializedSize = 19; // 2+8+8+1

    /// <summary>User-defined metric identifier.</summary>
    public ushort MetricId { get; init; }

    /// <summary>UTC ticks when the sample was recorded.</summary>
    public long TimestampUtcTicks { get; init; }

    /// <summary>The metric value.</summary>
    public double Value { get; init; }

    /// <summary>
    /// Aggregation type of this sample.
    /// 0=Raw, 1=Sum, 2=Average, 3=Min, 4=Max, 5=Count, 6=P99.
    /// </summary>
    public byte AggregationType { get; init; }

    /// <summary>Writes this sample to the buffer at the specified offset.</summary>
    internal void WriteTo(Span<byte> buffer, int offset)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), MetricId);
        offset += 2;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), TimestampUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(offset), Value);
        offset += 8;
        buffer[offset] = AggregationType;
    }

    /// <summary>Reads a sample from the buffer at the specified offset.</summary>
    internal static MetricsSample ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        ushort metricId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        long timestampUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        double value = BinaryPrimitives.ReadDoubleLittleEndian(buffer.Slice(offset));
        offset += 8;
        byte aggregationType = buffer[offset];

        return new MetricsSample
        {
            MetricId = metricId,
            TimestampUtcTicks = timestampUtcTicks,
            Value = value,
            AggregationType = aggregationType
        };
    }
}

/// <summary>
/// Metrics Log Region: records time-series <see cref="MetricsSample"/> data with
/// automatic compaction when capacity threshold is reached. Compaction groups
/// consecutive raw samples with the same MetricId into 1-minute windows, replacing
/// them with aggregated averages.
/// Serialized using <see cref="BlockTypeTags.MTRK"/> type tag.
/// </summary>
/// <remarks>
/// Serialization layout:
///   Block 0 (header): [SampleCount:8 LE][MaxCapacitySamples:8 LE]
///   [CompactionThreshold:8 LE (as double)][Reserved:8][samples...]
///   Samples overflow to block 1+ if needed. Each block ends with [UniversalBlockTrailer].
///
/// Note: BlockTypeTags.MTRK ("Metrics tracker") is shared with IntegrityTreeRegion.
/// The RegionDirectory distinguishes them by region type ID.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Metrics Log (VREG-17)")]
public sealed class MetricsLogRegion
{
    /// <summary>Size of the header fields at the start of block 0: SampleCount(8) + MaxCapacitySamples(8) + CompactionThreshold(8) + Reserved(8) = 32 bytes.</summary>
    private const int HeaderFieldsSize = 32;

    /// <summary>Ticks per minute for 1-minute compaction windows.</summary>
    private const long TicksPerMinute = TimeSpan.TicksPerMinute;

    private readonly List<MetricsSample> _samples = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of samples currently stored.</summary>
    public long SampleCount => _samples.Count;

    /// <summary>Maximum number of samples before auto-compaction is considered.</summary>
    public long MaxCapacitySamples { get; }

    /// <summary>
    /// Percentage of capacity (0.0-1.0) that triggers compaction when reached.
    /// Default is 0.8 (80%).
    /// </summary>
    public double CompactionThreshold { get; }

    /// <summary>
    /// Creates a new metrics log region with configurable capacity and compaction threshold.
    /// </summary>
    /// <param name="maxCapacitySamples">Maximum number of samples before auto-compaction triggers.</param>
    /// <param name="compactionThreshold">Percentage of capacity (0.0-1.0) that triggers compaction (default 0.8).</param>
    public MetricsLogRegion(long maxCapacitySamples, double compactionThreshold = 0.8)
    {
        if (maxCapacitySamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCapacitySamples),
                "Maximum capacity must be positive.");
        if (compactionThreshold <= 0.0 || compactionThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(compactionThreshold),
                "Compaction threshold must be between 0.0 (exclusive) and 1.0 (inclusive).");

        MaxCapacitySamples = maxCapacitySamples;
        CompactionThreshold = compactionThreshold;
    }

    /// <summary>
    /// Records a new metric sample. If SampleCount reaches MaxCapacitySamples * CompactionThreshold,
    /// auto-compaction runs before appending the new sample.
    /// </summary>
    /// <param name="sample">The sample to record.</param>
    public void RecordSample(MetricsSample sample)
    {
        if (_samples.Count >= (long)(MaxCapacitySamples * CompactionThreshold))
            AutoCompact();

        _samples.Add(sample);
    }

    /// <summary>
    /// Compacts the sample list by grouping consecutive raw samples with the same MetricId
    /// into 1-minute windows. Each window is replaced with a single aggregated sample
    /// (average value, AggregationType=Average). Non-raw samples are preserved as-is.
    /// </summary>
    public void AutoCompact()
    {
        if (_samples.Count == 0)
            return;

        var compacted = new List<MetricsSample>();

        int i = 0;
        while (i < _samples.Count)
        {
            var current = _samples[i];

            // Non-raw samples pass through unchanged
            if (current.AggregationType != 0)
            {
                compacted.Add(current);
                i++;
                continue;
            }

            // Start a 1-minute window for this MetricId
            ushort metricId = current.MetricId;
            long windowStart = current.TimestampUtcTicks;
            long windowEnd = windowStart + TicksPerMinute;

            double sum = current.Value;
            int count = 1;
            long latestTick = current.TimestampUtcTicks;
            i++;

            // Accumulate consecutive raw samples with same MetricId within the 1-minute window
            while (i < _samples.Count
                   && _samples[i].MetricId == metricId
                   && _samples[i].AggregationType == 0
                   && _samples[i].TimestampUtcTicks < windowEnd)
            {
                sum += _samples[i].Value;
                if (_samples[i].TimestampUtcTicks > latestTick)
                    latestTick = _samples[i].TimestampUtcTicks;
                count++;
                i++;
            }

            // Emit aggregated sample for this window
            compacted.Add(new MetricsSample
            {
                MetricId = metricId,
                TimestampUtcTicks = windowStart,
                Value = sum / count,
                AggregationType = 2 // Average
            });
        }

        _samples.Clear();
        _samples.AddRange(compacted);
    }

    /// <summary>
    /// Returns samples for a specific metric within a time range.
    /// </summary>
    /// <param name="metricId">The metric identifier to filter by.</param>
    /// <param name="fromTicks">Start of the time range (inclusive).</param>
    /// <param name="toTicks">End of the time range (inclusive).</param>
    /// <returns>Matching samples ordered by insertion.</returns>
    public IReadOnlyList<MetricsSample> GetSamples(ushort metricId, long fromTicks, long toTicks)
    {
        var results = new List<MetricsSample>();
        for (int i = 0; i < _samples.Count; i++)
        {
            var s = _samples[i];
            if (s.MetricId == metricId && s.TimestampUtcTicks >= fromTicks && s.TimestampUtcTicks <= toTicks)
                results.Add(s);
        }
        return results;
    }

    /// <summary>
    /// Returns the most recent N samples for a specific metric.
    /// </summary>
    /// <param name="metricId">The metric identifier to filter by.</param>
    /// <param name="count">Maximum number of samples to return.</param>
    /// <returns>Up to <paramref name="count"/> most recent samples.</returns>
    public IReadOnlyList<MetricsSample> GetLatestSamples(ushort metricId, int count)
    {
        var matching = new List<MetricsSample>();
        for (int i = _samples.Count - 1; i >= 0 && matching.Count < count; i--)
        {
            if (_samples[i].MetricId == metricId)
                matching.Add(_samples[i]);
        }
        matching.Reverse();
        return matching;
    }

    /// <summary>
    /// Returns all samples currently stored.
    /// </summary>
    public IReadOnlyList<MetricsSample> GetAllSamples() => _samples.AsReadOnly();

    /// <summary>
    /// Computes the number of blocks required to serialize this region.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        if (_samples.Count == 0)
            return 1;

        int totalSampleBytes = _samples.Count * MetricsSample.SerializedSize;
        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalSampleBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalSampleBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the metrics log region into blocks with UniversalBlockTrailer on each block.
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least RequiredBlocks * blockSize bytes).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int requiredBlocks = RequiredBlocks(blockSize);
        int totalSize = blockSize * requiredBlocks;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        buffer.Slice(0, totalSize).Clear();

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // Block 0 header
        BinaryPrimitives.WriteInt64LittleEndian(buffer, _samples.Count);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(8), MaxCapacitySamples);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(16), CompactionThreshold);
        // bytes 24..31 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int sampleIndex = 0;

        while (sampleIndex < _samples.Count)
        {
            int blockPayloadEnd = payloadSize;

            if (offset + MetricsSample.SerializedSize > blockPayloadEnd)
            {
                UniversalBlockTrailer.Write(
                    buffer.Slice(currentBlock * blockSize, blockSize),
                    blockSize, BlockTypeTags.MTRK, Generation);
                currentBlock++;
                offset = 0;
            }

            _samples[sampleIndex].WriteTo(buffer.Slice(currentBlock * blockSize), offset);
            offset += MetricsSample.SerializedSize;
            sampleIndex++;
        }

        // Write trailer for the last block containing data
        UniversalBlockTrailer.Write(
            buffer.Slice(currentBlock * blockSize, blockSize),
            blockSize, BlockTypeTags.MTRK, Generation);

        // Write trailers for any remaining empty blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            UniversalBlockTrailer.Write(
                buffer.Slice(blk * blockSize, blockSize),
                blockSize, BlockTypeTags.MTRK, Generation);
        }
    }

    /// <summary>
    /// Deserializes a metrics log region from blocks, verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="MetricsLogRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static MetricsLogRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        int totalSize = blockSize * blockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "At least one block is required.");

        // Verify all block trailers
        for (int blk = 0; blk < blockCount; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            if (!UniversalBlockTrailer.Verify(block, blockSize))
                throw new InvalidDataException($"Metrics Log block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        long sampleCount = BinaryPrimitives.ReadInt64LittleEndian(block0);
        long maxCapacity = BinaryPrimitives.ReadInt64LittleEndian(block0.Slice(8));
        double compactionThreshold = BinaryPrimitives.ReadDoubleLittleEndian(block0.Slice(16));

        if (sampleCount < 0 || sampleCount > 10_000_000)
            throw new InvalidDataException($"MetricsLog sampleCount {sampleCount} is out of valid range [0, 10M].");
        if (maxCapacity <= 0 || maxCapacity > 100_000_000)
            throw new InvalidDataException($"MetricsLog maxCapacity {maxCapacity} is out of valid range.");
        if (compactionThreshold <= 0 || compactionThreshold > 1.0)
            throw new InvalidDataException($"MetricsLog compactionThreshold {compactionThreshold} is out of valid range (0, 1.0].");

        var region = new MetricsLogRegion(maxCapacity, compactionThreshold)
        {
            Generation = trailer.GenerationNumber
        };

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        long samplesRead = 0;

        while (samplesRead < sampleCount)
        {
            int blockPayloadEnd = payloadSize;

            if (offset + MetricsSample.SerializedSize > blockPayloadEnd)
            {
                currentBlock++;
                offset = 0;
            }

            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            var sample = MetricsSample.ReadFrom(block, offset);
            offset += MetricsSample.SerializedSize;

            region._samples.Add(sample);
            samplesRead++;
        }

        return region;
    }
}
