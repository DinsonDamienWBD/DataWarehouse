using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.RAID;
using SdkRaidStrategyBase = DataWarehouse.SDK.Contracts.RAID.RaidStrategyBase;
using SdkDiskHealthStatus = DataWarehouse.SDK.Contracts.RAID.DiskHealthStatus;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.Nested
{
    /// <summary>
    /// RAID 03 (0+3) Strategy - Striped RAID 3 arrays.
    /// Combines the performance of RAID 0 striping with the redundancy of RAID 3
    /// byte-level striping with dedicated parity.
    /// </summary>
    /// <remarks>
    /// RAID 03 characteristics:
    /// - Multiple RAID 3 arrays striped together (RAID 0)
    /// - Each RAID 3 sub-array has byte-level striping + dedicated parity disk
    /// - Can survive one disk failure per RAID 3 group
    /// - Better write performance than pure RAID 3 (parallel parity updates)
    /// - Good for sequential workloads requiring redundancy
    /// - Minimum 6 disks (2 groups of 3 disks each)
    /// </remarks>
    public sealed class Raid03Strategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly int _disksPerRaid3Group;

        /// <summary>
        /// Initializes a new RAID 03 strategy.
        /// </summary>
        /// <param name="chunkSize">Size of each stripe chunk in bytes.</param>
        /// <param name="disksPerRaid3Group">Number of disks per RAID 3 sub-array (minimum 3).</param>
        public Raid03Strategy(int chunkSize = 64 * 1024, int disksPerRaid3Group = 3)
        {
            if (disksPerRaid3Group < 3)
                throw new ArgumentException("RAID 3 groups require at least 3 disks", nameof(disksPerRaid3Group));

            _chunkSize = chunkSize;
            _disksPerRaid3Group = disksPerRaid3Group;
        }

        public override RaidLevel Level => RaidLevel.Raid03;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1, // Per RAID 3 group
            MinDisks: _disksPerRaid3Group * 2, // At least 2 RAID 3 groups
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(4),
            ReadPerformanceMultiplier: 1.4, // Striped reads across groups
            WritePerformanceMultiplier: 0.7, // Parity overhead per group
            CapacityEfficiency: (double)(_disksPerRaid3Group - 1) / _disksPerRaid3Group, // ~67% with 3 disks/group
            SupportsHotSpare: true,
            SupportsOnlineExpansion: false, // Adding groups is complex
            RequiresUniformDiskSize: true);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            if (diskCount % _disksPerRaid3Group != 0)
                throw new ArgumentException($"Disk count must be a multiple of {_disksPerRaid3Group} for RAID 03");

            var groupCount = diskCount / _disksPerRaid3Group;
            var dataDisks = new List<int>();
            var parityDisks = new List<int>();

            // Each group: (disksPerGroup - 1) data disks + 1 parity disk
            for (int group = 0; group < groupCount; group++)
            {
                var groupOffset = group * _disksPerRaid3Group;

                // Data disks for this group
                for (int i = 0; i < _disksPerRaid3Group - 1; i++)
                {
                    dataDisks.Add(groupOffset + i);
                }

                // Dedicated parity disk (last disk in each group)
                parityDisks.Add(groupOffset + _disksPerRaid3Group - 1);
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
            var groupCount = diskList.Count / _disksPerRaid3Group;
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var dataBytes = data.ToArray();
            var writeTasks = new List<Task>();

            // Split data across RAID 3 groups (RAID 0 striping)
            var bytesPerGroup = dataBytes.Length / groupCount;
            if (bytesPerGroup == 0) bytesPerGroup = dataBytes.Length;

            for (int group = 0; group < groupCount; group++)
            {
                var groupOffset = group * _disksPerRaid3Group;
                var groupDataStart = group * bytesPerGroup;
                var groupDataLength = Math.Min(bytesPerGroup, dataBytes.Length - groupDataStart);

                if (groupDataLength <= 0) continue;

                var groupData = new byte[groupDataLength];
                Array.Copy(dataBytes, groupDataStart, groupData, 0, groupDataLength);

                // Distribute within RAID 3 group (byte-level striping)
                var dataDisksInGroup = _disksPerRaid3Group - 1;
                var groupChunks = DistributeByteLevel(groupData, dataDisksInGroup);

                // Calculate parity for this group
                var parityChunk = CalculateXorParity(groupChunks);

                // Write data chunks
                for (int i = 0; i < groupChunks.Count; i++)
                {
                    var diskIndex = groupOffset + i;
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], groupChunks[i], offset, cancellationToken));
                }

                // Write parity chunk to dedicated parity disk
                var parityDiskIndex = groupOffset + _disksPerRaid3Group - 1;
                writeTasks.Add(WriteToDiskAsync(diskList[parityDiskIndex], parityChunk, offset, cancellationToken));
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
            var groupCount = diskList.Count / _disksPerRaid3Group;

            var result = new byte[length];
            var bytesPerGroup = length / groupCount;
            if (bytesPerGroup == 0) bytesPerGroup = length;

            var readTasks = new List<Task<(int group, byte[] data)>>();

            for (int group = 0; group < groupCount; group++)
            {
                var groupIndex = group;
                readTasks.Add(ReadFromGroupAsync(diskList, groupIndex, offset, bytesPerGroup, cancellationToken));
            }

            var groupResults = await Task.WhenAll(readTasks);

            // Combine results from all groups
            var position = 0;
            foreach (var groupResult in groupResults.OrderBy(r => r.group))
            {
                var copyLength = Math.Min(groupResult.data.Length, length - position);
                Array.Copy(groupResult.data, 0, result, position, copyLength);
                position += copyLength;
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
            var failedDiskIndex = allDisks.IndexOf(failedDisk);

            // Determine which RAID 3 group contains the failed disk
            var groupIndex = failedDiskIndex / _disksPerRaid3Group;
            var positionInGroup = failedDiskIndex % _disksPerRaid3Group;
            var groupOffset = groupIndex * _disksPerRaid3Group;
            var isParityDisk = positionInGroup == _disksPerRaid3Group - 1;

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] reconstructedChunk;

                if (isParityDisk)
                {
                    // Rebuild parity: XOR all data disks in the group
                    reconstructedChunk = new byte[_chunkSize];
                    for (int i = 0; i < _disksPerRaid3Group - 1; i++)
                    {
                        var dataDiskIndex = groupOffset + i;
                        var chunk = await ReadFromDiskAsync(allDisks[dataDiskIndex], offset, _chunkSize, cancellationToken);
                        for (int j = 0; j < _chunkSize; j++)
                        {
                            reconstructedChunk[j] ^= chunk[j];
                        }
                    }
                }
                else
                {
                    // Rebuild data disk: XOR parity with all other data disks in group
                    var parityDiskIndex = groupOffset + _disksPerRaid3Group - 1;
                    reconstructedChunk = await ReadFromDiskAsync(allDisks[parityDiskIndex], offset, _chunkSize, cancellationToken);

                    for (int i = 0; i < _disksPerRaid3Group - 1; i++)
                    {
                        var dataDiskIndex = groupOffset + i;
                        if (dataDiskIndex != failedDiskIndex)
                        {
                            var chunk = await ReadFromDiskAsync(allDisks[dataDiskIndex], offset, _chunkSize, cancellationToken);
                            for (int j = 0; j < _chunkSize; j++)
                            {
                                reconstructedChunk[j] ^= chunk[j];
                            }
                        }
                    }
                }

                await WriteToDiskAsync(targetDisk, reconstructedChunk, offset, cancellationToken);
                bytesRebuilt += _chunkSize;

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = elapsed.TotalSeconds > 0 ? bytesRebuilt / elapsed.TotalSeconds : bytesRebuilt;
                    var remaining = speed > 0 ? (long)((totalBytes - bytesRebuilt) / speed) : 0;

                    progressCallback.Report(new RebuildProgress(
                        PercentComplete: (double)bytesRebuilt / totalBytes,
                        BytesRebuilt: bytesRebuilt,
                        TotalBytes: totalBytes,
                        EstimatedTimeRemaining: TimeSpan.FromSeconds(remaining),
                        CurrentSpeed: (long)speed));
                }
            }
        }

        private async Task<(int group, byte[] data)> ReadFromGroupAsync(
            List<DiskInfo> diskList,
            int groupIndex,
            long offset,
            int length,
            CancellationToken cancellationToken)
        {
            var groupOffset = groupIndex * _disksPerRaid3Group;
            var dataDisksInGroup = _disksPerRaid3Group - 1;

            var chunks = new List<byte[]>();
            int failedDiskInGroup = -1;

            // Read from data disks in group
            for (int i = 0; i < dataDisksInGroup; i++)
            {
                var diskIndex = groupOffset + i;
                var disk = diskList[diskIndex];

                if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                {
                    try
                    {
                        var chunk = await ReadFromDiskAsync(disk, offset, _chunkSize, cancellationToken);
                        chunks.Add(chunk);
                    }
                    catch
                    {
                        failedDiskInGroup = i;
                        chunks.Add(Array.Empty<byte>());
                    }
                }
                else
                {
                    failedDiskInGroup = i;
                    chunks.Add(Array.Empty<byte>());
                }
            }

            // Reconstruct if needed
            if (failedDiskInGroup >= 0)
            {
                var parityDiskIndex = groupOffset + _disksPerRaid3Group - 1;
                var parityChunk = await ReadFromDiskAsync(diskList[parityDiskIndex], offset, _chunkSize, cancellationToken);

                var reconstructed = parityChunk.ToArray();
                for (int i = 0; i < chunks.Count; i++)
                {
                    if (i != failedDiskInGroup && chunks[i].Length > 0)
                    {
                        for (int j = 0; j < _chunkSize; j++)
                        {
                            reconstructed[j] ^= chunks[i][j];
                        }
                    }
                }
                chunks[failedDiskInGroup] = reconstructed;
            }

            // Reconstruct data from byte-level striping
            var result = ReconstructFromByteLevel(chunks, length);
            return (groupIndex, result);
        }

        private List<byte[]> DistributeByteLevel(byte[] data, int diskCount)
        {
            var chunks = new List<byte[]>();
            for (int d = 0; d < diskCount; d++)
            {
                chunks.Add(new byte[_chunkSize]);
            }

            for (int i = 0; i < data.Length; i++)
            {
                var diskIndex = i % diskCount;
                var posInChunk = i / diskCount;
                if (posInChunk < _chunkSize)
                {
                    chunks[diskIndex][posInChunk] = data[i];
                }
            }

            return chunks;
        }

        private byte[] CalculateXorParity(List<byte[]> chunks)
        {
            var parity = new byte[_chunkSize];
            foreach (var chunk in chunks.Where(c => c != null))
            {
                for (int i = 0; i < _chunkSize && i < chunk.Length; i++)
                {
                    parity[i] ^= chunk[i];
                }
            }
            return parity;
        }

        private byte[] ReconstructFromByteLevel(List<byte[]> chunks, int length)
        {
            var result = new byte[length];
            var diskCount = chunks.Count;

            for (int i = 0; i < length; i++)
            {
                var diskIndex = i % diskCount;
                var posInChunk = i / diskCount;

                if (chunks[diskIndex] != null && posInChunk < chunks[diskIndex].Length)
                {
                    result[i] = chunks[diskIndex][posInChunk];
                }
            }

            return result;
        }

        private async Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");
            using var fs = new FileStream(disk.Location, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 65536, useAsync: true);
            fs.Seek(offset, SeekOrigin.Begin);
            await fs.WriteAsync(data, ct);
            await fs.FlushAsync(ct);
        }

        private async Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");
            if (!File.Exists(disk.Location))
                throw new FileNotFoundException($"Disk device not found: {disk.Location}", disk.Location);
            using var fs = new FileStream(disk.Location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
            if (offset + length > fs.Length) length = (int)Math.Max(0, fs.Length - offset);
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            int totalRead = 0;
            while (totalRead < length) { var read = await fs.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct); if (read == 0) break; totalRead += read; }
            if (totalRead < length) { var trimmed = new byte[totalRead]; Array.Copy(buffer, trimmed, totalRead); return trimmed; }
            return buffer;
        }
    }
}
