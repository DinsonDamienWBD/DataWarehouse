using System.Buffers;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Per-region fragmentation information including the gap (free blocks) after this region.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Per-region fragmentation info (OMA-05)")]
public readonly record struct RegionFragInfo(
    uint RegionTypeId,
    long StartBlock,
    long BlockCount,
    long GapAfter);

/// <summary>
/// Complete fragmentation analysis report for a VDE volume.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Fragmentation report (OMA-05)")]
public readonly record struct FragmentationReport(
    double OverallFragmentationPercent,
    long TotalRegions,
    long TotalGaps,
    long LargestContiguousFreeBlocks,
    long SmallestContiguousFreeBlocks,
    double AverageFreeRunLength,
    long WastedBlocks,
    IReadOnlyList<RegionFragInfo> RegionDetails);

/// <summary>
/// Analyzes VDE block layout fragmentation by reading the region directory and computing
/// gap metrics between active regions. Provides visibility into fragmentation severity
/// and estimated improvement from defragmentation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Fragmentation analysis for online defragmentation (OMA-05)")]
public sealed class FragmentationMetrics
{
    private readonly Stream _vdeStream;
    private readonly int _blockSize;

    /// <summary>
    /// Minimum number of contiguous free blocks considered useful. Gaps smaller than this
    /// are counted as wasted (too small for any region allocation).
    /// </summary>
    private const int MinUsefulRunSize = 16;

    /// <summary>
    /// Creates a new FragmentationMetrics analyzer.
    /// </summary>
    /// <param name="vdeStream">The VDE stream to read region directory from.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public FragmentationMetrics(Stream vdeStream, int blockSize)
    {
        _vdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Performs a complete fragmentation analysis of the VDE volume.
    /// Reads the region directory, sorts regions by start block, and computes
    /// gap metrics between consecutive regions.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A complete fragmentation report.</returns>
    public async Task<FragmentationReport> AnalyzeAsync(CancellationToken ct)
    {
        var directory = await ReadRegionDirectoryAsync(ct);
        var activeRegions = directory.GetActiveRegions();

        if (activeRegions.Count == 0)
        {
            return new FragmentationReport(
                OverallFragmentationPercent: 0,
                TotalRegions: 0,
                TotalGaps: 0,
                LargestContiguousFreeBlocks: 0,
                SmallestContiguousFreeBlocks: 0,
                AverageFreeRunLength: 0,
                WastedBlocks: 0,
                RegionDetails: Array.Empty<RegionFragInfo>());
        }

        // Sort regions by StartBlock
        var sorted = activeRegions
            .OrderBy(r => r.Pointer.StartBlock)
            .ToList();

        // Calculate gaps between consecutive regions and build per-region detail
        var regionDetails = new List<RegionFragInfo>();
        var freeRuns = new List<long>();
        long totalFreeBlocks = 0;

        for (int i = 0; i < sorted.Count; i++)
        {
            var current = sorted[i].Pointer;
            long gapAfter = 0;

            if (i < sorted.Count - 1)
            {
                var next = sorted[i + 1].Pointer;
                long currentEnd = current.StartBlock + current.BlockCount;
                gapAfter = next.StartBlock - currentEnd;
                if (gapAfter > 0)
                {
                    freeRuns.Add(gapAfter);
                    totalFreeBlocks += gapAfter;
                }
            }

            regionDetails.Add(new RegionFragInfo(
                current.RegionTypeId,
                current.StartBlock,
                current.BlockCount,
                gapAfter));
        }

        // Also check gap before first region (after fixed blocks: superblock 0-3, mirror 4-7, region dir 8-9)
        long fixedEndBlock = FormatConstants.RegionDirectoryStartBlock + FormatConstants.RegionDirectoryBlocks; // Block 10
        if (sorted.Count > 0 && sorted[0].Pointer.StartBlock > fixedEndBlock)
        {
            long leadingGap = sorted[0].Pointer.StartBlock - fixedEndBlock;
            freeRuns.Insert(0, leadingGap);
            totalFreeBlocks += leadingGap;
        }

        // Calculate fragmentation metrics
        long largestFreeRun = freeRuns.Count > 0 ? freeRuns.Max() : 0;
        long smallestFreeRun = freeRuns.Count > 0 ? freeRuns.Min() : 0;
        double averageFreeRun = freeRuns.Count > 0 ? freeRuns.Average() : 0;

        // Fragmentation: 1 - (largestFreeRun / totalFreeBlocks)
        // 0% = all free space in one contiguous run (perfect)
        // 100% = maximally fragmented
        double fragmentationPercent = 0;
        if (totalFreeBlocks > 0)
        {
            fragmentationPercent = (1.0 - ((double)largestFreeRun / totalFreeBlocks)) * 100.0;
        }

        // Wasted blocks: sum of free runs smaller than MinUsefulRunSize
        long wastedBlocks = freeRuns.Where(r => r < MinUsefulRunSize).Sum();

        return new FragmentationReport(
            OverallFragmentationPercent: fragmentationPercent,
            TotalRegions: sorted.Count,
            TotalGaps: freeRuns.Count,
            LargestContiguousFreeBlocks: largestFreeRun,
            SmallestContiguousFreeBlocks: smallestFreeRun,
            AverageFreeRunLength: averageFreeRun,
            WastedBlocks: wastedBlocks,
            RegionDetails: regionDetails);
    }

    /// <summary>
    /// Estimates the percentage improvement in largest contiguous free space
    /// that perfect compaction would achieve.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Percentage improvement (0-100). Higher means more benefit from defragmentation.</returns>
    public async Task<double> EstimateImprovementAsync(CancellationToken ct)
    {
        var report = await AnalyzeAsync(ct);

        if (report.TotalRegions == 0 || report.TotalGaps == 0)
            return 0;

        // After perfect compaction: all regions contiguous, one large free run
        long totalRegionBlocks = 0;
        foreach (var r in report.RegionDetails)
            totalRegionBlocks += r.BlockCount;

        // Total free blocks = sum of all gaps
        long totalFreeBlocks = 0;
        foreach (var r in report.RegionDetails)
            totalFreeBlocks += r.GapAfter;

        // Add leading gap if first region doesn't start at fixed end
        long fixedEnd = FormatConstants.RegionDirectoryStartBlock + FormatConstants.RegionDirectoryBlocks;
        if (report.RegionDetails.Count > 0 && report.RegionDetails[0].StartBlock > fixedEnd)
            totalFreeBlocks += report.RegionDetails[0].StartBlock - fixedEnd;

        if (totalFreeBlocks == 0)
            return 0;

        // After compaction, largest free run = totalFreeBlocks (one run)
        double currentLargest = report.LargestContiguousFreeBlocks;
        double improvementPercent = ((totalFreeBlocks - currentLargest) / (double)totalFreeBlocks) * 100.0;

        return Math.Max(0, improvementPercent);
    }

    /// <summary>
    /// Formats a fragmentation report as a human-readable string with ASCII bar chart.
    /// </summary>
    /// <param name="report">The fragmentation report to format.</param>
    /// <returns>A multi-line string representation of the report.</returns>
    public static string FormatReport(FragmentationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== VDE Fragmentation Report ===");
        sb.AppendLine();
        sb.AppendLine($"  Overall Fragmentation: {report.OverallFragmentationPercent:F1}%");
        sb.AppendLine($"  Active Regions:        {report.TotalRegions}");
        sb.AppendLine($"  Free Gaps:             {report.TotalGaps}");
        sb.AppendLine($"  Largest Free Run:      {report.LargestContiguousFreeBlocks} blocks");
        sb.AppendLine($"  Smallest Free Run:     {report.SmallestContiguousFreeBlocks} blocks");
        sb.AppendLine($"  Average Free Run:      {report.AverageFreeRunLength:F1} blocks");
        sb.AppendLine($"  Wasted Blocks:         {report.WastedBlocks} (gaps < {MinUsefulRunSize} blocks)");
        sb.AppendLine();

        // ASCII bar chart: fragmentation level
        int barLength = 40;
        int filledBars = (int)(report.OverallFragmentationPercent / 100.0 * barLength);
        filledBars = Math.Clamp(filledBars, 0, barLength);
        sb.Append("  Fragmentation: [");
        sb.Append(new string('#', filledBars));
        sb.Append(new string('.', barLength - filledBars));
        sb.AppendLine("]");
        sb.AppendLine();

        // Per-region details
        if (report.RegionDetails.Count > 0)
        {
            sb.AppendLine("  Region Details:");
            sb.AppendLine($"  {"Type",-6} {"Start",10} {"Count",10} {"GapAfter",10}");
            sb.AppendLine($"  {new string('-', 6)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)}");

            foreach (var region in report.RegionDetails)
            {
                string tag = BlockTypeTags.TagToString(region.RegionTypeId);
                sb.AppendLine($"  {tag,-6} {region.StartBlock,10} {region.BlockCount,10} {region.GapAfter,10}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads and deserializes the region directory from the VDE stream.
    /// </summary>
    private async Task<RegionDirectory> ReadRegionDirectoryAsync(CancellationToken ct)
    {
        int totalSize = FormatConstants.RegionDirectoryBlocks * _blockSize;
        var buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            long offset = FormatConstants.RegionDirectoryStartBlock * _blockSize;
            _vdeStream.Seek(offset, SeekOrigin.Begin);

            int totalRead = 0;
            while (totalRead < totalSize)
            {
                int bytesRead = await _vdeStream.ReadAsync(buffer.AsMemory(totalRead, totalSize - totalRead), ct);
                if (bytesRead == 0)
                    throw new InvalidDataException("Unexpected end of stream reading region directory.");
                totalRead += bytesRead;
            }

            return RegionDirectory.Deserialize(buffer.AsSpan(0, totalSize), _blockSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
