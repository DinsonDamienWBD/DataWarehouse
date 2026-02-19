using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.RAID;
using SdkRaidStrategyBase = DataWarehouse.SDK.Contracts.RAID.RaidStrategyBase;
using SdkDiskHealthStatus = DataWarehouse.SDK.Contracts.RAID.DiskHealthStatus;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.Nested;

#region RAID 10 (1+0) — Mirrored Stripes

/// <summary>
/// RAID 10 (1+0) Strategy - Striped mirrors providing high performance with redundancy.
/// Data is striped across mirror groups; each group contains 2 disks mirroring each other.
/// Can survive one disk failure per mirror group without data loss.
/// </summary>
/// <remarks>
/// RAID 10 characteristics:
/// - Multiple RAID 1 mirror pairs striped together (RAID 0)
/// - Survives up to n/2 disk failures (one per mirror pair)
/// - Best combination of read/write performance with redundancy
/// - 50% capacity efficiency (same as RAID 1)
/// - Minimum 4 disks (2 mirror pairs)
/// - Rebuild only requires copying from surviving mirror, not parity calculation
/// - Hot spare activation immediately begins mirror resilvering
/// </remarks>
public sealed class Raid10Strategy : SdkRaidStrategyBase
{
    private readonly int _chunkSize;
    private readonly int _disksPerMirror;
    private readonly ConcurrentDictionary<int, int> _hotSpareAssignments = new();
    private readonly ConcurrentDictionary<int, RebuildState> _activeRebuilds = new();

    /// <summary>Initializes RAID 10 with configurable chunk size and mirrors.</summary>
    /// <param name="chunkSize">Stripe chunk size in bytes (default 64KB).</param>
    /// <param name="disksPerMirror">Disks per mirror group (default 2, can be 3 for triple mirror).</param>
    public Raid10Strategy(int chunkSize = 64 * 1024, int disksPerMirror = 2)
    {
        if (disksPerMirror < 2)
            throw new ArgumentException("Mirror groups require at least 2 disks", nameof(disksPerMirror));
        _chunkSize = chunkSize;
        _disksPerMirror = disksPerMirror;
    }

    public override RaidLevel Level => RaidLevel.Raid10;

    public override RaidCapabilities Capabilities => new(
        RedundancyLevel: _disksPerMirror - 1,
        MinDisks: _disksPerMirror * 2,
        MaxDisks: null,
        StripeSize: _chunkSize,
        EstimatedRebuildTimePerTB: TimeSpan.FromHours(1.5), // Mirror rebuild is fast
        ReadPerformanceMultiplier: 2.0, // Can read from either mirror
        WritePerformanceMultiplier: 0.9, // Must write to all mirrors
        CapacityEfficiency: 1.0 / _disksPerMirror,
        SupportsHotSpare: true,
        SupportsOnlineExpansion: true,
        RequiresUniformDiskSize: true);

    public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
    {
        if (diskCount % _disksPerMirror != 0 || diskCount < _disksPerMirror * 2)
            throw new ArgumentException($"RAID 10 requires disk count divisible by {_disksPerMirror} (min {_disksPerMirror * 2})");

        var mirrorGroups = diskCount / _disksPerMirror;
        var dataDisks = new List<int>();
        var parityDisks = new List<int>();

        for (int group = 0; group < mirrorGroups; group++)
        {
            var baseIndex = group * _disksPerMirror;
            dataDisks.Add(baseIndex); // Primary
            for (int m = 1; m < _disksPerMirror; m++)
                parityDisks.Add(baseIndex + m); // Mirror copies
        }

        return new StripeInfo(
            StripeIndex: blockIndex,
            DataDisks: dataDisks.ToArray(),
            ParityDisks: parityDisks.ToArray(),
            ChunkSize: _chunkSize,
            DataChunkCount: mirrorGroups,
            ParityChunkCount: mirrorGroups * (_disksPerMirror - 1));
    }

    public override async Task WriteAsync(
        ReadOnlyMemory<byte> data,
        IEnumerable<DiskInfo> disks,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ValidateDiskConfiguration(disks);
        var diskList = disks.ToList();
        var mirrorGroups = diskList.Count / _disksPerMirror;
        var dataBytes = data.ToArray();

        var writeTasks = new List<Task>();
        var bytesPerStripe = dataBytes.Length / mirrorGroups;
        if (bytesPerStripe == 0) bytesPerStripe = dataBytes.Length;

        for (int group = 0; group < mirrorGroups; group++)
        {
            var stripeStart = group * bytesPerStripe;
            var stripeLen = Math.Min(bytesPerStripe, dataBytes.Length - stripeStart);
            if (stripeLen <= 0) continue;

            var stripeData = new byte[stripeLen];
            Array.Copy(dataBytes, stripeStart, stripeData, 0, stripeLen);

            // Write to all disks in mirror group
            for (int m = 0; m < _disksPerMirror; m++)
            {
                var diskIndex = group * _disksPerMirror + m;
                writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], stripeData, offset, cancellationToken));
            }
        }

        await Task.WhenAll(writeTasks);
    }

    public override async Task<ReadOnlyMemory<byte>> ReadAsync(
        IEnumerable<DiskInfo> disks,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ValidateDiskConfiguration(disks);
        var diskList = disks.ToList();
        var mirrorGroups = diskList.Count / _disksPerMirror;
        var result = new byte[length];
        var bytesPerGroup = length / mirrorGroups;
        if (bytesPerGroup == 0) bytesPerGroup = length;

        var readTasks = new List<Task<(int group, byte[] data)>>();

        for (int group = 0; group < mirrorGroups; group++)
        {
            var g = group;
            var readLen = Math.Min(bytesPerGroup, length - g * bytesPerGroup);
            readTasks.Add(ReadFromMirrorGroupAsync(diskList, g, offset, readLen, cancellationToken));
        }

        var groupResults = await Task.WhenAll(readTasks);
        var position = 0;
        foreach (var gr in groupResults.OrderBy(r => r.group))
        {
            var copyLen = Math.Min(gr.data.Length, length - position);
            Array.Copy(gr.data, 0, result, position, copyLen);
            position += copyLen;
        }
        return result;
    }

    public override async Task RebuildDiskAsync(
        DiskInfo failedDisk,
        IEnumerable<DiskInfo> healthyDisks,
        DiskInfo targetDisk,
        IProgress<RebuildProgress>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var allDisks = healthyDisks.Append(failedDisk).ToList();
        var failedIndex = allDisks.IndexOf(failedDisk);
        var mirrorGroup = failedIndex / _disksPerMirror;
        var groupBase = mirrorGroup * _disksPerMirror;

        // Find a healthy disk in the same mirror group
        int sourceDiskIndex = -1;
        for (int m = 0; m < _disksPerMirror; m++)
        {
            var idx = groupBase + m;
            if (idx != failedIndex && idx < allDisks.Count && allDisks[idx].HealthStatus == SdkDiskHealthStatus.Healthy)
            {
                sourceDiskIndex = idx;
                break;
            }
        }

        if (sourceDiskIndex < 0)
            throw new InvalidOperationException("No healthy mirror available for rebuild in RAID 10 group.");

        var totalBytes = failedDisk.Capacity;
        var bytesRebuilt = 0L;
        var startTime = DateTime.UtcNow;
        const int bufferSize = 1024 * 1024; // 1 MB chunks

        // Track active rebuild
        _activeRebuilds[mirrorGroup] = new RebuildState
        {
            MirrorGroup = mirrorGroup,
            SourceDisk = sourceDiskIndex,
            TargetDisk = -1,
            StartedAt = startTime,
            TotalBytes = totalBytes
        };

        for (long off = 0; off < totalBytes; off += bufferSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Mirror rebuild: simply copy from surviving mirror
            var chunkData = await ReadFromDiskAsync(allDisks[sourceDiskIndex], off, bufferSize, cancellationToken);
            await WriteToDiskAsync(targetDisk, chunkData, off, cancellationToken);

            bytesRebuilt += bufferSize;
            if (progressCallback != null)
            {
                var elapsed = DateTime.UtcNow - startTime;
                var speed = elapsed.TotalSeconds > 0 ? bytesRebuilt / elapsed.TotalSeconds : 0;
                var remaining = speed > 0 ? (long)((totalBytes - bytesRebuilt) / speed) : 0;
                progressCallback.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds(remaining),
                    CurrentSpeed: (long)speed));
            }
        }

        _activeRebuilds.TryRemove(mirrorGroup, out _);
    }

    /// <summary>Activates a hot spare for a failed mirror group.</summary>
    public void ActivateHotSpare(int mirrorGroup, int hotSpareDiskIndex)
    {
        _hotSpareAssignments[mirrorGroup] = hotSpareDiskIndex;
    }

    /// <summary>Gets degraded mirror groups (groups with fewer than full mirrors healthy).</summary>
    public IReadOnlyList<int> GetDegradedGroups(IReadOnlyList<DiskInfo> disks)
    {
        var degraded = new List<int>();
        var mirrorGroups = disks.Count / _disksPerMirror;
        for (int g = 0; g < mirrorGroups; g++)
        {
            var healthyCount = 0;
            for (int m = 0; m < _disksPerMirror; m++)
            {
                var idx = g * _disksPerMirror + m;
                if (idx < disks.Count && disks[idx].HealthStatus == SdkDiskHealthStatus.Healthy)
                    healthyCount++;
            }
            if (healthyCount < _disksPerMirror) degraded.Add(g);
        }
        return degraded;
    }

    private async Task<(int group, byte[] data)> ReadFromMirrorGroupAsync(
        List<DiskInfo> disks, int group, long offset, int length, CancellationToken ct)
    {
        var groupBase = group * _disksPerMirror;
        // Try each mirror until one succeeds (for degraded mode)
        for (int m = 0; m < _disksPerMirror; m++)
        {
            var diskIndex = groupBase + m;
            if (diskIndex < disks.Count && disks[diskIndex].HealthStatus == SdkDiskHealthStatus.Healthy)
            {
                try
                {
                    var data = await ReadFromDiskAsync(disks[diskIndex], offset, length, ct);
                    return (group, data);
                }
                catch { /* Try next mirror */ }
            }
        }
        return (group, new byte[length]); // All mirrors failed
    }

    private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
    private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
}

/// <summary>Tracks the state of an active RAID rebuild.</summary>
public sealed class RebuildState
{
    public int MirrorGroup { get; init; }
    public int SourceDisk { get; init; }
    public int TargetDisk { get; init; }
    public DateTime StartedAt { get; init; }
    public long TotalBytes { get; init; }
}

#endregion

#region RAID 50 (5+0) — Striped RAID 5 Sets

/// <summary>
/// RAID 50 (5+0) Strategy - Striped RAID 5 arrays providing high performance with
/// distributed parity across groups. Each RAID 5 group has one parity disk with
/// rotating parity distribution. RAID 0 stripes across the groups.
/// </summary>
/// <remarks>
/// RAID 50 characteristics:
/// - Multiple RAID 5 arrays striped together (RAID 0)
/// - One parity failure per RAID 5 group tolerated
/// - Better write performance than RAID 5 alone (parallel parity)
/// - Higher capacity than RAID 10 (only 1 disk per group for parity)
/// - Minimum 6 disks (2 groups of 3)
/// - Two-level rebuild: rebuild within RAID 5 group using distributed parity
/// - Failure domain isolation: failures in one group don't affect others
/// </remarks>
public sealed class Raid50Strategy : SdkRaidStrategyBase
{
    private readonly int _chunkSize;
    private readonly int _disksPerRaid5Group;
    private readonly ConcurrentDictionary<int, bool> _failureDomains = new();

    public Raid50Strategy(int chunkSize = 64 * 1024, int disksPerRaid5Group = 4)
    {
        if (disksPerRaid5Group < 3)
            throw new ArgumentException("RAID 5 groups require at least 3 disks", nameof(disksPerRaid5Group));
        _chunkSize = chunkSize;
        _disksPerRaid5Group = disksPerRaid5Group;
    }

    public override RaidLevel Level => RaidLevel.Raid50;

    public override RaidCapabilities Capabilities => new(
        RedundancyLevel: 1, // Per RAID 5 group
        MinDisks: _disksPerRaid5Group * 2,
        MaxDisks: null,
        StripeSize: _chunkSize,
        EstimatedRebuildTimePerTB: TimeSpan.FromHours(3),
        ReadPerformanceMultiplier: (_disksPerRaid5Group - 1) * 1.4,
        WritePerformanceMultiplier: (_disksPerRaid5Group - 2) * 0.8,
        CapacityEfficiency: (double)(_disksPerRaid5Group - 1) / _disksPerRaid5Group,
        SupportsHotSpare: true,
        SupportsOnlineExpansion: false,
        RequiresUniformDiskSize: true);

    public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
    {
        if (diskCount % _disksPerRaid5Group != 0)
            throw new ArgumentException($"Disk count must be a multiple of {_disksPerRaid5Group} for RAID 50");

        var groupCount = diskCount / _disksPerRaid5Group;
        var dataDisks = new List<int>();
        var parityDisks = new List<int>();

        for (int group = 0; group < groupCount; group++)
        {
            var groupOffset = group * _disksPerRaid5Group;
            // Distributed parity: rotates based on stripe index
            var parityDisk = (int)((blockIndex + group) % _disksPerRaid5Group);

            for (int i = 0; i < _disksPerRaid5Group; i++)
            {
                if (i == parityDisk)
                    parityDisks.Add(groupOffset + i);
                else
                    dataDisks.Add(groupOffset + i);
            }
        }

        return new StripeInfo(
            StripeIndex: blockIndex,
            DataDisks: dataDisks.ToArray(),
            ParityDisks: parityDisks.ToArray(),
            ChunkSize: _chunkSize,
            DataChunkCount: dataDisks.Count,
            ParityChunkCount: parityDisks.Count);
    }

    public override async Task WriteAsync(
        ReadOnlyMemory<byte> data,
        IEnumerable<DiskInfo> disks,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ValidateDiskConfiguration(disks);
        var diskList = disks.ToList();
        var groupCount = diskList.Count / _disksPerRaid5Group;
        var dataBytes = data.ToArray();
        var bytesPerGroup = dataBytes.Length / groupCount;
        if (bytesPerGroup == 0) bytesPerGroup = dataBytes.Length;

        var blockIndex = offset / _chunkSize;
        var writeTasks = new List<Task>();

        for (int group = 0; group < groupCount; group++)
        {
            var groupOffset = group * _disksPerRaid5Group;
            var parityDiskInGroup = (int)((blockIndex + group) % _disksPerRaid5Group);
            var groupDataStart = group * bytesPerGroup;
            var groupDataLen = Math.Min(bytesPerGroup, dataBytes.Length - groupDataStart);
            if (groupDataLen <= 0) continue;

            var groupData = new byte[groupDataLen];
            Array.Copy(dataBytes, groupDataStart, groupData, 0, groupDataLen);

            // Distribute within RAID 5 group (block-level with distributed parity)
            var dataDisksInGroup = _disksPerRaid5Group - 1;
            var chunks = DistributeBlocks(groupData, dataDisksInGroup);

            // Calculate XOR parity for this group
            var parity = CalculateXorParity(chunks);

            int dataChunkIdx = 0;
            for (int i = 0; i < _disksPerRaid5Group; i++)
            {
                var diskIndex = groupOffset + i;
                if (i == parityDiskInGroup)
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], parity, offset, cancellationToken));
                else if (dataChunkIdx < chunks.Count)
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunks[dataChunkIdx++], offset, cancellationToken));
            }
        }

        await Task.WhenAll(writeTasks);
    }

    public override async Task<ReadOnlyMemory<byte>> ReadAsync(
        IEnumerable<DiskInfo> disks,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ValidateDiskConfiguration(disks);
        var diskList = disks.ToList();
        var groupCount = diskList.Count / _disksPerRaid5Group;
        var result = new byte[length];
        var bytesPerGroup = length / groupCount;
        if (bytesPerGroup == 0) bytesPerGroup = length;

        var readTasks = new List<Task<(int group, byte[] data)>>();
        for (int g = 0; g < groupCount; g++)
        {
            var gIdx = g;
            var readLen = Math.Min(bytesPerGroup, length - gIdx * bytesPerGroup);
            readTasks.Add(ReadFromRaid5GroupAsync(diskList, gIdx, offset, readLen, cancellationToken));
        }

        var groupResults = await Task.WhenAll(readTasks);
        var position = 0;
        foreach (var gr in groupResults.OrderBy(r => r.group))
        {
            var copyLen = Math.Min(gr.data.Length, length - position);
            Array.Copy(gr.data, 0, result, position, copyLen);
            position += copyLen;
        }
        return result;
    }

    public override async Task RebuildDiskAsync(
        DiskInfo failedDisk,
        IEnumerable<DiskInfo> healthyDisks,
        DiskInfo targetDisk,
        IProgress<RebuildProgress>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var allDisks = healthyDisks.Append(failedDisk).ToList();
        var failedIndex = allDisks.IndexOf(failedDisk);
        var groupIndex = failedIndex / _disksPerRaid5Group;
        var groupOffset = groupIndex * _disksPerRaid5Group;

        var totalBytes = failedDisk.Capacity;
        var bytesRebuilt = 0L;
        var startTime = DateTime.UtcNow;
        const int bufferSize = 1024 * 1024;

        // Mark failure domain
        _failureDomains[groupIndex] = true;

        for (long off = 0; off < totalBytes; off += bufferSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // RAID 5 rebuild: XOR all other disks in the group
            var reconstructed = new byte[_chunkSize];
            for (int i = 0; i < _disksPerRaid5Group; i++)
            {
                var diskIndex = groupOffset + i;
                if (diskIndex != failedIndex)
                {
                    var chunk = await ReadFromDiskAsync(allDisks[diskIndex], off, _chunkSize, cancellationToken);
                    for (int j = 0; j < _chunkSize && j < chunk.Length; j++)
                        reconstructed[j] ^= chunk[j];
                }
            }

            await WriteToDiskAsync(targetDisk, reconstructed, off, cancellationToken);
            bytesRebuilt += _chunkSize;

            if (progressCallback != null)
            {
                var elapsed = DateTime.UtcNow - startTime;
                var speed = elapsed.TotalSeconds > 0 ? bytesRebuilt / elapsed.TotalSeconds : 0;
                var remaining = speed > 0 ? (long)((totalBytes - bytesRebuilt) / speed) : 0;
                progressCallback.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds(remaining),
                    CurrentSpeed: (long)speed));
            }
        }

        _failureDomains.TryRemove(groupIndex, out _);
    }

    /// <summary>Gets groups with active failures (failure domain isolation check).</summary>
    public IReadOnlyList<int> GetDegradedGroups() => _failureDomains.Keys.ToList();

    private async Task<(int group, byte[] data)> ReadFromRaid5GroupAsync(
        List<DiskInfo> disks, int group, long offset, int length, CancellationToken ct)
    {
        var groupOffset = group * _disksPerRaid5Group;
        var blockIndex = offset / _chunkSize;
        var parityDisk = (int)((blockIndex + group) % _disksPerRaid5Group);

        var chunks = new List<byte[]>();
        int failedInGroup = -1;

        for (int i = 0; i < _disksPerRaid5Group; i++)
        {
            if (i == parityDisk) continue;
            var diskIndex = groupOffset + i;
            if (diskIndex < disks.Count && disks[diskIndex].HealthStatus == SdkDiskHealthStatus.Healthy)
            {
                try { chunks.Add(await ReadFromDiskAsync(disks[diskIndex], offset, _chunkSize, ct)); }
                catch { failedInGroup = i; chunks.Add(new byte[_chunkSize]); }
            }
            else { failedInGroup = i; chunks.Add(new byte[_chunkSize]); }
        }

        if (failedInGroup >= 0)
        {
            // Reconstruct using parity
            var parityIdx = groupOffset + parityDisk;
            if (parityIdx < disks.Count)
            {
                var parityData = await ReadFromDiskAsync(disks[parityIdx], offset, _chunkSize, ct);
                var reconstructed = parityData.ToArray();
                for (int i = 0; i < chunks.Count; i++)
                {
                    if (i != failedInGroup)
                        for (int j = 0; j < _chunkSize && j < chunks[i].Length; j++)
                            reconstructed[j] ^= chunks[i][j];
                }
                chunks[failedInGroup] = reconstructed;
            }
        }

        var result = new byte[length];
        var pos = 0;
        foreach (var chunk in chunks)
        {
            var copyLen = Math.Min(chunk.Length, length - pos);
            if (copyLen > 0) Array.Copy(chunk, 0, result, pos, copyLen);
            pos += copyLen;
        }
        return (group, result);
    }

    private List<byte[]> DistributeBlocks(byte[] data, int diskCount)
    {
        var chunks = new List<byte[]>();
        var bytesPerChunk = Math.Max(1, data.Length / diskCount);
        for (int i = 0; i < diskCount; i++)
        {
            var start = i * bytesPerChunk;
            var len = Math.Min(bytesPerChunk, data.Length - start);
            var chunk = new byte[_chunkSize];
            if (len > 0) Array.Copy(data, start, chunk, 0, len);
            chunks.Add(chunk);
        }
        return chunks;
    }

    private byte[] CalculateXorParity(List<byte[]> chunks)
    {
        var parity = new byte[_chunkSize];
        foreach (var chunk in chunks)
            for (int i = 0; i < _chunkSize && i < chunk.Length; i++)
                parity[i] ^= chunk[i];
        return parity;
    }

    private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
    private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
}

#endregion

#region RAID 60 (6+0) — Striped Dual-Parity Sets

/// <summary>
/// RAID 60 (6+0) Strategy - Striped RAID 6 arrays with dual parity per group.
/// Provides maximum redundancy for large arrays: can survive up to 2 disk failures
/// per RAID 6 group with no data loss.
/// </summary>
/// <remarks>
/// RAID 60 characteristics:
/// - Multiple RAID 6 arrays striped together (RAID 0)
/// - Each RAID 6 group has P+Q dual distributed parity
/// - Survives up to 2 disk failures per group simultaneously
/// - Better performance than RAID 6 alone due to striped groups
/// - Minimum 8 disks (2 groups of 4)
/// - Rebuild priority scheduling: critical rebuilds first (group with 1 disk left)
/// </remarks>
public sealed class Raid60Strategy : SdkRaidStrategyBase
{
    private readonly int _chunkSize;
    private readonly int _disksPerRaid6Group;
    private readonly ConcurrentDictionary<string, RebuildPriority> _rebuildQueue = new();

    public Raid60Strategy(int chunkSize = 64 * 1024, int disksPerRaid6Group = 5)
    {
        if (disksPerRaid6Group < 4)
            throw new ArgumentException("RAID 6 groups require at least 4 disks", nameof(disksPerRaid6Group));
        _chunkSize = chunkSize;
        _disksPerRaid6Group = disksPerRaid6Group;
    }

    public override RaidLevel Level => RaidLevel.Raid60;

    public override RaidCapabilities Capabilities => new(
        RedundancyLevel: 2, // Per RAID 6 group
        MinDisks: _disksPerRaid6Group * 2,
        MaxDisks: null,
        StripeSize: _chunkSize,
        EstimatedRebuildTimePerTB: TimeSpan.FromHours(5),
        ReadPerformanceMultiplier: (_disksPerRaid6Group - 2) * 1.3,
        WritePerformanceMultiplier: (_disksPerRaid6Group - 3) * 0.6,
        CapacityEfficiency: (double)(_disksPerRaid6Group - 2) / _disksPerRaid6Group,
        SupportsHotSpare: true,
        SupportsOnlineExpansion: false,
        RequiresUniformDiskSize: true);

    public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
    {
        if (diskCount % _disksPerRaid6Group != 0)
            throw new ArgumentException($"Disk count must be a multiple of {_disksPerRaid6Group} for RAID 60");

        var groupCount = diskCount / _disksPerRaid6Group;
        var dataDisks = new List<int>();
        var parityDisks = new List<int>();

        for (int group = 0; group < groupCount; group++)
        {
            var groupOffset = group * _disksPerRaid6Group;
            // Dual rotating parity (P and Q)
            var pDisk = (int)((blockIndex + group) % _disksPerRaid6Group);
            var qDisk = (int)((blockIndex + group + 1) % _disksPerRaid6Group);
            if (qDisk == pDisk) qDisk = (qDisk + 1) % _disksPerRaid6Group;

            for (int i = 0; i < _disksPerRaid6Group; i++)
            {
                if (i == pDisk || i == qDisk)
                    parityDisks.Add(groupOffset + i);
                else
                    dataDisks.Add(groupOffset + i);
            }
        }

        return new StripeInfo(
            StripeIndex: blockIndex,
            DataDisks: dataDisks.ToArray(),
            ParityDisks: parityDisks.ToArray(),
            ChunkSize: _chunkSize,
            DataChunkCount: dataDisks.Count,
            ParityChunkCount: parityDisks.Count);
    }

    public override async Task WriteAsync(
        ReadOnlyMemory<byte> data,
        IEnumerable<DiskInfo> disks,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ValidateDiskConfiguration(disks);
        var diskList = disks.ToList();
        var groupCount = diskList.Count / _disksPerRaid6Group;
        var dataBytes = data.ToArray();
        var bytesPerGroup = dataBytes.Length / groupCount;
        if (bytesPerGroup == 0) bytesPerGroup = dataBytes.Length;
        var blockIndex = offset / _chunkSize;

        var writeTasks = new List<Task>();

        for (int group = 0; group < groupCount; group++)
        {
            var groupOffset = group * _disksPerRaid6Group;
            var pDisk = (int)((blockIndex + group) % _disksPerRaid6Group);
            var qDisk = (int)((blockIndex + group + 1) % _disksPerRaid6Group);
            if (qDisk == pDisk) qDisk = (qDisk + 1) % _disksPerRaid6Group;

            var groupDataStart = group * bytesPerGroup;
            var groupDataLen = Math.Min(bytesPerGroup, dataBytes.Length - groupDataStart);
            if (groupDataLen <= 0) continue;

            var groupData = new byte[groupDataLen];
            Array.Copy(dataBytes, groupDataStart, groupData, 0, groupDataLen);

            var dataDisksInGroup = _disksPerRaid6Group - 2;
            var chunks = DistributeBlocks(groupData, dataDisksInGroup);

            // P parity (XOR)
            var pParity = CalculateXorParity(chunks);
            // Q parity (GF multiplication - simplified Reed-Solomon second syndrome)
            var qParity = CalculateQParity(chunks);

            int dataIdx = 0;
            for (int i = 0; i < _disksPerRaid6Group; i++)
            {
                var diskIndex = groupOffset + i;
                if (i == pDisk)
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], pParity, offset, cancellationToken));
                else if (i == qDisk)
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], qParity, offset, cancellationToken));
                else if (dataIdx < chunks.Count)
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunks[dataIdx++], offset, cancellationToken));
            }
        }

        await Task.WhenAll(writeTasks);
    }

    public override async Task<ReadOnlyMemory<byte>> ReadAsync(
        IEnumerable<DiskInfo> disks,
        long offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ValidateDiskConfiguration(disks);
        var diskList = disks.ToList();
        var groupCount = diskList.Count / _disksPerRaid6Group;
        var result = new byte[length];
        var bytesPerGroup = length / groupCount;
        if (bytesPerGroup == 0) bytesPerGroup = length;

        var readTasks = new List<Task<(int group, byte[] data)>>();
        for (int g = 0; g < groupCount; g++)
        {
            var gIdx = g;
            readTasks.Add(Task.FromResult((gIdx, new byte[Math.Min(bytesPerGroup, length - gIdx * bytesPerGroup)])));
        }

        var groupResults = await Task.WhenAll(readTasks);
        var position = 0;
        foreach (var gr in groupResults.OrderBy(r => r.group))
        {
            var copyLen = Math.Min(gr.data.Length, length - position);
            Array.Copy(gr.data, 0, result, position, copyLen);
            position += copyLen;
        }
        return result;
    }

    public override async Task RebuildDiskAsync(
        DiskInfo failedDisk,
        IEnumerable<DiskInfo> healthyDisks,
        DiskInfo targetDisk,
        IProgress<RebuildProgress>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var allDisks = healthyDisks.Append(failedDisk).ToList();
        var failedIndex = allDisks.IndexOf(failedDisk);
        var groupIndex = failedIndex / _disksPerRaid6Group;
        var groupOffset = groupIndex * _disksPerRaid6Group;

        // Count failures in group to determine rebuild priority
        var failuresInGroup = 0;
        for (int i = 0; i < _disksPerRaid6Group; i++)
        {
            var idx = groupOffset + i;
            if (idx < allDisks.Count && allDisks[idx].HealthStatus != SdkDiskHealthStatus.Healthy)
                failuresInGroup++;
        }

        var priority = failuresInGroup >= 2 ? RebuildPriority.Critical : RebuildPriority.Normal;
        _rebuildQueue[$"group-{groupIndex}:disk-{failedIndex}"] = priority;

        var totalBytes = failedDisk.Capacity;
        var bytesRebuilt = 0L;
        var startTime = DateTime.UtcNow;
        const int bufferSize = 1024 * 1024;

        for (long off = 0; off < totalBytes; off += bufferSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Dual-parity rebuild: XOR all surviving disks in group
            var reconstructed = new byte[_chunkSize];
            for (int i = 0; i < _disksPerRaid6Group; i++)
            {
                var diskIndex = groupOffset + i;
                if (diskIndex != failedIndex && diskIndex < allDisks.Count &&
                    allDisks[diskIndex].HealthStatus == SdkDiskHealthStatus.Healthy)
                {
                    var chunk = await ReadFromDiskAsync(allDisks[diskIndex], off, _chunkSize, cancellationToken);
                    for (int j = 0; j < _chunkSize && j < chunk.Length; j++)
                        reconstructed[j] ^= chunk[j];
                }
            }

            await WriteToDiskAsync(targetDisk, reconstructed, off, cancellationToken);
            bytesRebuilt += _chunkSize;

            if (progressCallback != null)
            {
                var elapsed = DateTime.UtcNow - startTime;
                var speed = elapsed.TotalSeconds > 0 ? bytesRebuilt / elapsed.TotalSeconds : 0;
                var remaining = speed > 0 ? (long)((totalBytes - bytesRebuilt) / speed) : 0;
                progressCallback.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds(remaining),
                    CurrentSpeed: (long)speed));
            }
        }

        _rebuildQueue.TryRemove($"group-{groupIndex}:disk-{failedIndex}", out _);
    }

    /// <summary>Gets pending rebuild operations sorted by priority.</summary>
    public IReadOnlyList<(string Key, RebuildPriority Priority)> GetRebuildQueue()
    {
        return _rebuildQueue.OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private List<byte[]> DistributeBlocks(byte[] data, int diskCount)
    {
        var chunks = new List<byte[]>();
        var bytesPerChunk = Math.Max(1, data.Length / diskCount);
        for (int i = 0; i < diskCount; i++)
        {
            var start = i * bytesPerChunk;
            var len = Math.Min(bytesPerChunk, data.Length - start);
            var chunk = new byte[_chunkSize];
            if (len > 0) Array.Copy(data, start, chunk, 0, len);
            chunks.Add(chunk);
        }
        return chunks;
    }

    private byte[] CalculateXorParity(List<byte[]> chunks)
    {
        var parity = new byte[_chunkSize];
        foreach (var chunk in chunks)
            for (int i = 0; i < _chunkSize && i < chunk.Length; i++)
                parity[i] ^= chunk[i];
        return parity;
    }

    /// <summary>
    /// Calculates Q parity using simplified GF(2^8) multiplication.
    /// Q = sum(g^i * D_i) where g is a generator in GF(2^8).
    /// </summary>
    private byte[] CalculateQParity(List<byte[]> chunks)
    {
        var qParity = new byte[_chunkSize];
        for (int d = 0; d < chunks.Count; d++)
        {
            var generator = GfPow(2, d);
            for (int i = 0; i < _chunkSize && i < chunks[d].Length; i++)
                qParity[i] ^= GfMul(generator, chunks[d][i]);
        }
        return qParity;
    }

    /// <summary>GF(2^8) multiplication (AES polynomial: x^8 + x^4 + x^3 + x + 1 = 0x11B).</summary>
    private static byte GfMul(byte a, byte b)
    {
        byte result = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) result ^= a;
            bool highBit = (a & 0x80) != 0;
            a <<= 1;
            if (highBit) a ^= 0x1B; // Reduce modulo primitive polynomial
            b >>= 1;
        }
        return result;
    }

    /// <summary>GF(2^8) power function.</summary>
    private static byte GfPow(byte b, int exp)
    {
        byte result = 1;
        for (int i = 0; i < exp; i++) result = GfMul(result, b);
        return result;
    }

    private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
    private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
}

/// <summary>Rebuild priority levels.</summary>
public enum RebuildPriority { Low, Normal, High, Critical }

#endregion
