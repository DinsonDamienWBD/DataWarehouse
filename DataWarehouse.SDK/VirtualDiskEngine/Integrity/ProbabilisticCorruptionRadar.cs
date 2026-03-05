using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Result of a probabilistic corruption radar scan.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Probabilistic corruption radar result (VOPT-48)")]
public readonly struct RadarResult
{
    /// <summary>Number of TRLR blocks sampled during the scan.</summary>
    public int TrlrBlocksSampled { get; init; }

    /// <summary>Total number of trailer records examined across all sampled TRLR blocks.</summary>
    public int TrailerRecordsVerified { get; init; }

    /// <summary>Number of records exhibiting suspect indicators (zero generation or zero checksum).</summary>
    public int SuspectRecords { get; init; }

    /// <summary>Number of records confirmed as corrupt (checksum mismatch after retry).</summary>
    public int ConfirmedCorrupt { get; init; }

    /// <summary>Point estimate of the corruption rate in the volume (0.0 to 1.0).</summary>
    public double EstimatedCorruptionRate { get; init; }

    /// <summary>Lower bound of the Wilson score confidence interval.</summary>
    public double ConfidenceLowerBound { get; init; }

    /// <summary>Upper bound of the Wilson score confidence interval.</summary>
    public double ConfidenceUpperBound { get; init; }

    /// <summary>True if the estimated corruption rate exceeds the configured alert threshold.</summary>
    public bool AlertTriggered { get; init; }

    /// <summary>Confirmed corrupt data block numbers (absolute block numbers within the volume).</summary>
    public IReadOnlyList<long> CorruptBlockNumbers { get; init; }

    /// <summary>Wall-clock duration of the scan.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Probabilistic corruption radar that uses statistical TRLR block sampling to detect
/// corruption without performing a full volume scrub (VOPT-48).
///
/// Architecture:
/// In the separated trailer architecture, every 256th block starting at offset 255
/// within the data region is a TRLR block. Each 4 KiB TRLR block contains exactly
/// 255 trailer records (16 bytes each): DataBlockTypeTag(4), GenerationNumber(4),
/// XxHash64(8). The radar samples a subset of TRLR blocks using reservoir sampling
/// (Algorithm R), verifies each trailer record, and estimates the corruption rate
/// using the Wilson score confidence interval for binomial proportions.
///
/// Usage:
/// <code>
/// var config = new CorruptionRadarConfig { SampleSize = 512 };
/// var radar = new ProbabilisticCorruptionRadar(device, blockSize, config);
/// radar.OnAlertTriggered += result => logger.LogWarning("Corruption detected: {Rate:P3}", result.EstimatedCorruptionRate);
/// RadarResult result = await radar.ScanAsync(dataRegionStartBlock, dataRegionBlockCount, ct);
/// </code>
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: Probabilistic corruption radar (VOPT-48)")]
public sealed class ProbabilisticCorruptionRadar
{
    // Number of trailer records that fit in a TRLR block.
    // Each TRLR block is 4096 bytes. Each record is 16 bytes.
    // 255 data blocks precede each TRLR block, so 255 records fit (255 * 16 = 4080 bytes).
    private const int TrailerRecordsPerTrlrBlock = 255;

    // TRLR blocks appear at every 256th block starting at offset 255 (0-indexed).
    // Pattern: 255, 511, 767, … i.e. (n * 256) + 255 for n = 0, 1, 2, …
    private const int TrlrBlockStride = 256;
    private const int TrlrBlockOffset = 255;

    private readonly IBlockDevice _device;
    private readonly int _blockSize;
    private readonly CorruptionRadarConfig _config;
    private readonly Random _random;

    /// <summary>
    /// Raised when a scan completes and the estimated corruption rate exceeds the alert threshold.
    /// </summary>
    public event Action<RadarResult>? OnAlertTriggered;

    /// <summary>
    /// Initializes a new probabilistic corruption radar.
    /// </summary>
    /// <param name="device">Block device to read TRLR and data blocks from.</param>
    /// <param name="blockSize">Block size in bytes (must match the device and format).</param>
    /// <param name="config">Radar configuration.</param>
    public ProbabilisticCorruptionRadar(IBlockDevice device, int blockSize, CorruptionRadarConfig config)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, FormatConstants.UniversalBlockTrailerSize);

        _device = device;
        _blockSize = blockSize;
        _config = config;
        _random = config.RandomSeed.HasValue
            ? new Random(config.RandomSeed.Value)
            : new Random();
    }

    // ── TRLR Block Position Generation ───────────────────────────────────────

    /// <summary>
    /// Generates all TRLR block absolute positions within the data region.
    /// TRLR blocks are at positions: dataRegionStartBlock + 255, + 511, + 767, …
    /// (every 256th block starting at offset 255 within the region).
    /// </summary>
    /// <param name="dataRegionStartBlock">Absolute block number where the data region begins.</param>
    /// <param name="dataRegionBlockCount">Total number of blocks in the data region.</param>
    /// <returns>All TRLR block absolute positions within the data region.</returns>
    public IReadOnlyList<long> GenerateTrlrBlockPositions(long dataRegionStartBlock, long dataRegionBlockCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dataRegionStartBlock, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThan(dataRegionBlockCount, 0L);

        var positions = new List<long>();

        for (long offset = TrlrBlockOffset; offset < dataRegionBlockCount; offset += TrlrBlockStride)
        {
            positions.Add(dataRegionStartBlock + offset);
        }

        return positions;
    }

    // ── Reservoir Sampling (Algorithm R) ─────────────────────────────────────

    /// <summary>
    /// Selects a uniform random sample of TRLR block positions using Vitter's Algorithm R.
    /// If sampleSize >= trlrPositions.Count, all positions are returned.
    /// </summary>
    /// <param name="trlrPositions">All candidate TRLR block positions.</param>
    /// <param name="sampleSize">Desired sample size.</param>
    /// <returns>Selected positions (order not guaranteed).</returns>
    public IReadOnlyList<long> SelectSample(IReadOnlyList<long> trlrPositions, int sampleSize)
    {
        ArgumentNullException.ThrowIfNull(trlrPositions);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleSize, 0);

        if (sampleSize >= trlrPositions.Count)
            return trlrPositions;

        if (sampleSize == 0)
            return Array.Empty<long>();

        // Algorithm R (Vitter, 1985): O(n) uniform reservoir sampling
        var reservoir = new long[sampleSize];

        // Fill the reservoir with the first sampleSize elements
        for (int i = 0; i < sampleSize; i++)
            reservoir[i] = trlrPositions[i];

        // Randomly replace reservoir elements with decreasing probability
        for (int i = sampleSize; i < trlrPositions.Count; i++)
        {
            int j = _random.Next(0, i + 1);
            if (j < sampleSize)
                reservoir[j] = trlrPositions[i];
        }

        return reservoir;
    }

    // ── Statistical Estimation (Wilson Score Interval) ────────────────────────

    /// <summary>
    /// Estimates the corruption rate and its confidence interval using the Wilson score
    /// interval for a binomial proportion. This method is accurate for small proportions
    /// where corruption is expected to be rare.
    /// </summary>
    /// <param name="corrupt">Number of corrupt records observed.</param>
    /// <param name="sampled">Total records sampled (trials).</param>
    /// <param name="confidenceLevel">Confidence level (e.g., 0.95 for 95%).</param>
    /// <returns>Point estimate, lower bound, and upper bound of the corruption rate.</returns>
    public (double EstimatedRate, double LowerBound, double UpperBound) EstimateCorruptionRate(
        int corrupt,
        int sampled,
        double confidenceLevel)
    {
        if (sampled <= 0)
            return (0.0, 0.0, 0.0);

        double p = (double)corrupt / sampled;

        if (corrupt == 0)
            return (0.0, 0.0, 0.0);

        // z-score for confidence level using a normal approximation table
        // Common values: 0.90 -> 1.645, 0.95 -> 1.960, 0.99 -> 2.576
        double z = GetZScore(confidenceLevel);
        double z2 = z * z;
        double n = sampled;

        // Wilson score interval:
        // center = (p + z²/2n) / (1 + z²/n)
        // half-width = z * sqrt(p(1-p)/n + z²/(4n²)) / (1 + z²/n)
        double denominator = 1.0 + z2 / n;
        double center = (p + z2 / (2.0 * n)) / denominator;
        double halfWidth = z * Math.Sqrt(p * (1.0 - p) / n + z2 / (4.0 * n * n)) / denominator;

        double lower = Math.Max(0.0, center - halfWidth);
        double upper = Math.Min(1.0, center + halfWidth);

        return (p, lower, upper);
    }

    // ── Main Scan ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs a probabilistic corruption scan of the data region.
    ///
    /// Steps:
    /// 1. Generate all TRLR block positions in the data region.
    /// 2. Select a random sample using reservoir sampling.
    /// 3. For each sampled TRLR block, read and parse its 255 trailer records.
    /// 4. Identify suspect records (zero generation or zero checksum).
    /// 5. Optionally confirm corruption by re-reading the corresponding data block.
    /// 6. Estimate the corruption rate with a Wilson score confidence interval.
    /// </summary>
    /// <param name="dataRegionStartBlock">Absolute starting block of the data region.</param>
    /// <param name="dataRegionBlockCount">Number of blocks in the data region.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Radar scan result with statistical estimates.</returns>
    public async Task<RadarResult> ScanAsync(
        long dataRegionStartBlock,
        long dataRegionBlockCount,
        CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;

        // Step 1: generate TRLR positions
        var allTrlrPositions = GenerateTrlrBlockPositions(dataRegionStartBlock, dataRegionBlockCount);

        // Step 2: select sample
        var sampledPositions = SelectSample(allTrlrPositions, _config.SampleSize);

        int trlrBlocksSampled = 0;
        int trailerRecordsVerified = 0;
        int suspectRecords = 0;
        int confirmedCorrupt = 0;
        var corruptBlockNumbers = new List<long>();

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        byte[] dataBlockBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);

        try
        {
            foreach (long trlrAbsoluteBlock in sampledPositions)
            {
                ct.ThrowIfCancellationRequested();

                // Step 3a: read the TRLR block
                bool trlrReadOk = await TryReadBlockWithRetryAsync(trlrAbsoluteBlock, blockBuffer, ct);
                if (!trlrReadOk)
                    continue;

                trlrBlocksSampled++;

                // Step 3b: parse up to TrailerRecordsPerTrlrBlock (255) records
                // Each record: DataBlockTypeTag(4 LE) + GenerationNumber(4 LE) + XxHash64(8 LE) = 16 bytes
                int recordsInBlock = Math.Min(
                    TrailerRecordsPerTrlrBlock,
                    (_blockSize - FormatConstants.UniversalBlockTrailerSize) / UniversalBlockTrailer.Size);

                // The TRLR block itself covers data blocks at:
                // trlrAbsoluteBlock - TrailerRecordsPerTrlrBlock .. trlrAbsoluteBlock - 1
                // i.e., data block[i] = trlrAbsoluteBlock - recordsInBlock + i  (0-indexed)
                long firstDataBlock = trlrAbsoluteBlock - recordsInBlock;

                for (int recordIndex = 0; recordIndex < recordsInBlock; recordIndex++)
                {
                    ct.ThrowIfCancellationRequested();

                    int byteOffset = recordIndex * UniversalBlockTrailer.Size;

                    // Guard: ensure we don't read past the end of the buffer
                    if (byteOffset + UniversalBlockTrailer.Size > _blockSize)
                        break;

                    uint dataBlockTypeTag = BinaryPrimitives.ReadUInt32LittleEndian(
                        blockBuffer.AsSpan(byteOffset, 4));
                    uint generationNumber = BinaryPrimitives.ReadUInt32LittleEndian(
                        blockBuffer.AsSpan(byteOffset + 4, 4));
                    ulong xxHash64 = BinaryPrimitives.ReadUInt64LittleEndian(
                        blockBuffer.AsSpan(byteOffset + 8, 8));

                    trailerRecordsVerified++;

                    // Step 3c: verify block type tag
                    if (dataBlockTypeTag != BlockTypeTags.Data)
                    {
                        // Not a DATA block tag — treat as suspect
                        suspectRecords++;
                        continue;
                    }

                    // Step 3d: check suspect indicators
                    bool isSuspect = generationNumber == 0 || xxHash64 == 0;
                    if (isSuspect)
                    {
                        suspectRecords++;

                        // Step 3e: confirm by reading data block and recomputing checksum
                        long dataBlockAbsolute = firstDataBlock + recordIndex;
                        if (dataBlockAbsolute >= dataRegionStartBlock
                            && dataBlockAbsolute < dataRegionStartBlock + dataRegionBlockCount)
                        {
                            bool confirmed = await ConfirmCorruptionAsync(
                                dataBlockAbsolute, dataBlockBuffer, xxHash64, ct);
                            if (confirmed)
                            {
                                confirmedCorrupt++;
                                corruptBlockNumbers.Add(dataBlockAbsolute);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
            ArrayPool<byte>.Shared.Return(dataBlockBuffer);
        }

        // Step 5: estimate corruption rate with Wilson score CI
        var (estimatedRate, lower, upper) = EstimateCorruptionRate(
            confirmedCorrupt,
            trailerRecordsVerified,
            _config.ConfidenceLevel);

        bool alertTriggered = estimatedRate > _config.AlertThreshold;

        var result = new RadarResult
        {
            TrlrBlocksSampled = trlrBlocksSampled,
            TrailerRecordsVerified = trailerRecordsVerified,
            SuspectRecords = suspectRecords,
            ConfirmedCorrupt = confirmedCorrupt,
            EstimatedCorruptionRate = estimatedRate,
            ConfidenceLowerBound = lower,
            ConfidenceUpperBound = upper,
            AlertTriggered = alertTriggered,
            CorruptBlockNumbers = corruptBlockNumbers,
            Duration = DateTime.UtcNow - startedAt
        };

        if (alertTriggered)
            OnAlertTriggered?.Invoke(result);

        return result;
    }

    // ── Continuous Monitoring ─────────────────────────────────────────────────

    /// <summary>
    /// Runs periodic corruption scans until cancellation is requested.
    /// Raises <see cref="OnAlertTriggered"/> when corruption exceeds the configured threshold.
    /// </summary>
    /// <param name="dataRegionStartBlock">Absolute starting block of the data region.</param>
    /// <param name="dataRegionBlockCount">Number of blocks in the data region.</param>
    /// <param name="ct">Cancellation token to stop monitoring.</param>
    public async Task RunContinuousAsync(
        long dataRegionStartBlock,
        long dataRegionBlockCount,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(dataRegionStartBlock, dataRegionBlockCount, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal shutdown — exit cleanly
                break;
            }
#pragma warning disable CA1031 // Intentional: scan errors must not crash the monitor loop
            catch (Exception)
            {
                // Scan failures (I/O errors, transient faults) should not crash the monitor loop.
                // The next scheduled scan will retry.
            }
#pragma warning restore CA1031

            try
            {
                await Task.Delay(_config.ScanInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    // ── Private Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to read a block, retrying up to <see cref="CorruptionRadarConfig.MaxRetries"/> times.
    /// </summary>
    private async Task<bool> TryReadBlockWithRetryAsync(long blockNumber, byte[] buffer, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _device.ReadBlockAsync(blockNumber, buffer.AsMemory(0, _blockSize), ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Intentional: transient I/O errors trigger retry logic
            catch (Exception)
            {
                if (attempt == _config.MaxRetries)
                    return false;
            }
#pragma warning restore CA1031
        }

        return false;
    }

    /// <summary>
    /// Confirms corruption by re-reading the data block and comparing the recomputed
    /// XxHash64 checksum against the stored value from the TRLR record.
    /// Returns true if the checksum does not match (confirmed corrupt).
    /// </summary>
    private async Task<bool> ConfirmCorruptionAsync(
        long dataBlockAbsolute,
        byte[] buffer,
        ulong storedHash,
        CancellationToken ct)
    {
        bool readOk = await TryReadBlockWithRetryAsync(dataBlockAbsolute, buffer, ct);
        if (!readOk)
            return true; // Unreadable block is corrupt

        // Compute checksum over payload (excluding the 16-byte trailer)
        int payloadLength = _blockSize - FormatConstants.UniversalBlockTrailerSize;
        ulong computed = XxHash64.HashToUInt64(buffer.AsSpan(0, payloadLength));

        // Mismatch means the stored hash in the TRLR block doesn't match the actual data
        return computed != storedHash;
    }

    /// <summary>
    /// Returns the z-score for the given two-tailed confidence level using a piecewise
    /// approximation of the standard normal quantile function.
    /// </summary>
    private static double GetZScore(double confidenceLevel)
    {
        // Common confidence levels — exact values
        return confidenceLevel switch
        {
            >= 0.999 => 3.291,
            >= 0.995 => 2.807,
            >= 0.99  => 2.576,
            >= 0.98  => 2.326,
            >= 0.95  => 1.960,
            >= 0.90  => 1.645,
            >= 0.85  => 1.440,
            >= 0.80  => 1.282,
            _        => 1.000  // fallback for low confidence levels
        };
    }
}
