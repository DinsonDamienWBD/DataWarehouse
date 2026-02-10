// 91.D2.5: Bad Block Remapping - Remap bad sectors automatically
using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts.RAID;

namespace DataWarehouse.Plugins.UltimateRAID.Features;

/// <summary>
/// 91.D2.5: Bad Block Remapping - Automatic remapping of bad sectors.
/// Detects bad blocks during I/O operations and transparently remaps them
/// to spare sectors while recovering data from RAID redundancy.
/// </summary>
public sealed class BadBlockRemapping
{
    private readonly ConcurrentDictionary<string, DiskBadBlockMap> _diskMaps = new();
    private readonly ConcurrentDictionary<string, RemappingStatistics> _statistics = new();
    private readonly object _remapLock = new();

    /// <summary>
    /// Maximum number of remapped blocks per disk before marking as failing.
    /// </summary>
    public int MaxRemappedBlocksWarning { get; set; } = 100;

    /// <summary>
    /// Maximum number of remapped blocks per disk before marking as failed.
    /// </summary>
    public int MaxRemappedBlocksCritical { get; set; } = 500;

    /// <summary>
    /// Registers a bad block detected during I/O and initiates remapping.
    /// </summary>
    public async Task<RemapResult> RegisterBadBlockAsync(
        string diskId,
        long logicalBlockAddress,
        BadBlockType blockType,
        byte[]? originalData,
        IEnumerable<DiskInfo>? raidDisks = null,
        CancellationToken cancellationToken = default)
    {
        var map = _diskMaps.GetOrAdd(diskId, _ => new DiskBadBlockMap(diskId));
        var stats = _statistics.GetOrAdd(diskId, _ => new RemappingStatistics { DiskId = diskId });

        // Check if block is already remapped
        if (map.IsBlockRemapped(logicalBlockAddress))
        {
            return new RemapResult(
                Success: true,
                Message: "Block already remapped",
                RemappedLBA: map.GetRemappedAddress(logicalBlockAddress));
        }

        // Find a spare block
        var spareBlock = map.AllocateSpareBlock();
        if (spareBlock < 0)
        {
            stats.FailedRemaps++;
            return new RemapResult(
                Success: false,
                Message: "No spare blocks available",
                RemappedLBA: -1);
        }

        try
        {
            // Attempt to recover data from RAID redundancy
            byte[]? recoveredData = null;
            if (originalData == null && raidDisks != null)
            {
                recoveredData = await RecoverDataFromParityAsync(
                    diskId, logicalBlockAddress, raidDisks, cancellationToken);
            }

            var dataToWrite = recoveredData ?? originalData ?? new byte[512];

            // Write recovered/original data to spare block
            await WriteToSpareBlockAsync(diskId, spareBlock, dataToWrite, cancellationToken);

            // Update remapping table
            map.AddRemapping(logicalBlockAddress, spareBlock, blockType);

            // Update statistics
            lock (_remapLock)
            {
                stats.TotalRemappedBlocks++;
                stats.LastRemapTime = DateTime.UtcNow;
                stats.RemapsByType[blockType] = stats.RemapsByType.GetValueOrDefault(blockType) + 1;
            }

            // Check disk health thresholds
            var healthStatus = EvaluateDiskHealth(stats);

            return new RemapResult(
                Success: true,
                Message: $"Block remapped successfully. Disk health: {healthStatus}",
                RemappedLBA: spareBlock,
                DataRecovered: recoveredData != null,
                DiskHealthStatus: healthStatus);
        }
        catch (Exception ex)
        {
            stats.FailedRemaps++;
            return new RemapResult(
                Success: false,
                Message: $"Remapping failed: {ex.Message}",
                RemappedLBA: -1);
        }
    }

    /// <summary>
    /// Gets the remapped address for a logical block if it exists.
    /// </summary>
    public long? GetRemappedAddress(string diskId, long logicalBlockAddress)
    {
        if (_diskMaps.TryGetValue(diskId, out var map) && map.IsBlockRemapped(logicalBlockAddress))
        {
            return map.GetRemappedAddress(logicalBlockAddress);
        }
        return null;
    }

    /// <summary>
    /// Gets bad block statistics for a disk.
    /// </summary>
    public RemappingStatistics GetStatistics(string diskId)
    {
        return _statistics.GetOrAdd(diskId, _ => new RemappingStatistics { DiskId = diskId });
    }

    /// <summary>
    /// Gets all disks with remapped blocks.
    /// </summary>
    public IReadOnlyList<RemappingStatistics> GetAllStatistics()
    {
        return _statistics.Values.ToList();
    }

    /// <summary>
    /// Performs proactive bad block scanning.
    /// </summary>
    public async Task<ScanResult> ScanForBadBlocksAsync(
        string diskId,
        long startLBA,
        long endLBA,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ScanResult { DiskId = diskId, StartLBA = startLBA, EndLBA = endLBA };
        var totalBlocks = endLBA - startLBA;
        var scannedBlocks = 0L;

        for (var lba = startLBA; lba < endLBA; lba++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var blockStatus = await TestBlockAsync(diskId, lba, cancellationToken);

            if (blockStatus != BlockTestResult.Good)
            {
                result.BadBlocks.Add(new BadBlockInfo
                {
                    LBA = lba,
                    Type = blockStatus switch
                    {
                        BlockTestResult.ReadError => BadBlockType.ReadError,
                        BlockTestResult.WriteError => BadBlockType.WriteError,
                        BlockTestResult.Timeout => BadBlockType.Timeout,
                        BlockTestResult.DataCorruption => BadBlockType.DataCorruption,
                        _ => BadBlockType.Unknown
                    }
                });
            }

            scannedBlocks++;
            if (scannedBlocks % 10000 == 0)
            {
                progress?.Report((double)scannedBlocks / totalBlocks);
            }
        }

        result.ScanCompleted = DateTime.UtcNow;
        result.TotalBlocksScanned = totalBlocks;
        return result;
    }

    /// <summary>
    /// Clears the remapping table for a disk (use after disk replacement).
    /// </summary>
    public void ClearRemappingTable(string diskId)
    {
        _diskMaps.TryRemove(diskId, out _);
        _statistics.TryRemove(diskId, out _);
    }

    /// <summary>
    /// Exports the bad block map for persistence.
    /// </summary>
    public BadBlockMapExport ExportBadBlockMap(string diskId)
    {
        if (!_diskMaps.TryGetValue(diskId, out var map))
        {
            return new BadBlockMapExport { DiskId = diskId, Entries = new List<BadBlockMapEntry>() };
        }

        return new BadBlockMapExport
        {
            DiskId = diskId,
            Entries = map.GetAllEntries(),
            ExportTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Imports a bad block map (e.g., after restart).
    /// </summary>
    public void ImportBadBlockMap(BadBlockMapExport export)
    {
        var map = _diskMaps.GetOrAdd(export.DiskId, _ => new DiskBadBlockMap(export.DiskId));
        foreach (var entry in export.Entries)
        {
            map.AddRemapping(entry.OriginalLBA, entry.RemappedLBA, entry.BlockType);
        }
    }

    private async Task<byte[]?> RecoverDataFromParityAsync(
        string failedDiskId,
        long lba,
        IEnumerable<DiskInfo> raidDisks,
        CancellationToken cancellationToken)
    {
        var diskList = raidDisks.ToList();
        var healthyDisks = diskList.Where(d => d.DiskId != failedDiskId && d.HealthStatus == DataWarehouse.SDK.Contracts.RAID.DiskHealthStatus.Healthy).ToList();

        if (healthyDisks.Count < diskList.Count - 1)
        {
            return null; // Cannot recover without enough healthy disks
        }

        // Read blocks from healthy disks and XOR to recover
        const int blockSize = 512;
        var recoveredData = new byte[blockSize];

        foreach (var disk in healthyDisks)
        {
            var data = await ReadBlockFromDiskAsync(disk.DiskId, lba, cancellationToken);
            if (data != null)
            {
                for (int i = 0; i < blockSize; i++)
                {
                    recoveredData[i] ^= data[i];
                }
            }
        }

        return recoveredData;
    }

    private Task<byte[]?> ReadBlockFromDiskAsync(string diskId, long lba, CancellationToken ct)
    {
        // Simulated disk read
        return Task.FromResult<byte[]?>(new byte[512]);
    }

    private Task WriteToSpareBlockAsync(string diskId, long spareLba, byte[] data, CancellationToken ct)
    {
        // Simulated write to spare area
        return Task.CompletedTask;
    }

    private Task<BlockTestResult> TestBlockAsync(string diskId, long lba, CancellationToken ct)
    {
        // Simulated block test - in production would do actual read/write test
        return Task.FromResult(BlockTestResult.Good);
    }

    private DiskHealthStatus EvaluateDiskHealth(RemappingStatistics stats)
    {
        if (stats.TotalRemappedBlocks >= MaxRemappedBlocksCritical)
            return DiskHealthStatus.Failed;
        if (stats.TotalRemappedBlocks >= MaxRemappedBlocksWarning)
            return DiskHealthStatus.Warning;
        return DiskHealthStatus.Healthy;
    }
}

/// <summary>
/// Manages the bad block remapping table for a single disk.
/// </summary>
public sealed class DiskBadBlockMap
{
    private readonly string _diskId;
    private readonly ConcurrentDictionary<long, long> _remappings = new();
    private readonly ConcurrentDictionary<long, BadBlockType> _blockTypes = new();
    private long _nextSpareBlock;
    private readonly object _spareLock = new();

    // Spare area configuration
    private const long SpareAreaStart = 0x7FFFFFFFFFF00000; // Near end of address space
    private const long SpareAreaSize = 0x100000; // 1M spare blocks

    public DiskBadBlockMap(string diskId)
    {
        _diskId = diskId;
        _nextSpareBlock = SpareAreaStart;
    }

    public bool IsBlockRemapped(long lba) => _remappings.ContainsKey(lba);

    public long GetRemappedAddress(long lba) => _remappings.GetValueOrDefault(lba, lba);

    public long AllocateSpareBlock()
    {
        lock (_spareLock)
        {
            if (unchecked(_nextSpareBlock >= SpareAreaStart + SpareAreaSize))
                return -1; // No more spare blocks

            return _nextSpareBlock++;
        }
    }

    public void AddRemapping(long originalLba, long spareLba, BadBlockType type)
    {
        _remappings[originalLba] = spareLba;
        _blockTypes[originalLba] = type;
    }

    public List<BadBlockMapEntry> GetAllEntries()
    {
        return _remappings.Select(kvp => new BadBlockMapEntry
        {
            OriginalLBA = kvp.Key,
            RemappedLBA = kvp.Value,
            BlockType = _blockTypes.GetValueOrDefault(kvp.Key, BadBlockType.Unknown)
        }).ToList();
    }
}

/// <summary>
/// Type of bad block error.
/// </summary>
public enum BadBlockType
{
    Unknown,
    ReadError,
    WriteError,
    Timeout,
    DataCorruption,
    MediaError,
    Reallocated
}

/// <summary>
/// Result of block test during scanning.
/// </summary>
public enum BlockTestResult
{
    Good,
    ReadError,
    WriteError,
    Timeout,
    DataCorruption
}

/// <summary>
/// Statistics about remapped blocks for a disk.
/// </summary>
public sealed class RemappingStatistics
{
    public string DiskId { get; set; } = string.Empty;
    public long TotalRemappedBlocks { get; set; }
    public long FailedRemaps { get; set; }
    public DateTime? LastRemapTime { get; set; }
    public Dictionary<BadBlockType, long> RemapsByType { get; set; } = new();
}

/// <summary>
/// Result of a remapping operation.
/// </summary>
public record RemapResult(
    bool Success,
    string Message,
    long RemappedLBA,
    bool DataRecovered = false,
    DiskHealthStatus DiskHealthStatus = DiskHealthStatus.Healthy);

/// <summary>
/// Information about a detected bad block.
/// </summary>
public sealed class BadBlockInfo
{
    public long LBA { get; set; }
    public BadBlockType Type { get; set; }
    public DateTime DetectedTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result of a bad block scan.
/// </summary>
public sealed class ScanResult
{
    public string DiskId { get; set; } = string.Empty;
    public long StartLBA { get; set; }
    public long EndLBA { get; set; }
    public long TotalBlocksScanned { get; set; }
    public List<BadBlockInfo> BadBlocks { get; set; } = new();
    public DateTime ScanCompleted { get; set; }
}

/// <summary>
/// Entry in the bad block map for export/import.
/// </summary>
public sealed class BadBlockMapEntry
{
    public long OriginalLBA { get; set; }
    public long RemappedLBA { get; set; }
    public BadBlockType BlockType { get; set; }
}

/// <summary>
/// Exported bad block map for persistence.
/// </summary>
public sealed class BadBlockMapExport
{
    public string DiskId { get; set; } = string.Empty;
    public List<BadBlockMapEntry> Entries { get; set; } = new();
    public DateTime ExportTime { get; set; }
}
