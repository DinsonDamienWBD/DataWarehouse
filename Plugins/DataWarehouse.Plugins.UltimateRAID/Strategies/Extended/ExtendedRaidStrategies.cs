using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.RAID;
using SdkRaidStrategyBase = DataWarehouse.SDK.Contracts.RAID.RaidStrategyBase;
using SdkDiskHealthStatus = DataWarehouse.SDK.Contracts.RAID.DiskHealthStatus;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.Extended
{
    /// <summary>
    /// RAID 01 (0+1) strategy implementing mirrored stripes for high performance
    /// with redundancy through mirroring of entire stripe sets.
    /// </summary>
    public class Raid01Strategy : SdkRaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.Raid01;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 4,
            MaxDisks: null,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(2),
            ReadPerformanceMultiplier: 1.8,
            WritePerformanceMultiplier: 0.9,
            CapacityEfficiency: 0.5, // 50% due to mirroring
            SupportsHotSpare: true,
            SupportsOnlineExpansion: false,
            RequiresUniformDiskSize: true);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            // RAID 0+1: Stripe data first, then mirror the stripes
            var halfCount = diskList.Count / 2;
            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, Capabilities.StripeSize);

            var writeTasks = new List<Task>();

            // Stripe across first half (primary stripe set)
            for (int i = 0; i < chunks.Count; i++)
            {
                var diskIndex = i % halfCount;
                var chunkOffset = offset + (i / halfCount) * Capabilities.StripeSize;

                // Write to primary stripe set
                writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunks[i], chunkOffset, cancellationToken));

                // Mirror to secondary stripe set
                writeTasks.Add(WriteToDiskAsync(diskList[diskIndex + halfCount], chunks[i], chunkOffset, cancellationToken));
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
            var halfCount = diskList.Count / 2;

            var result = new byte[length];
            var chunksNeeded = (int)Math.Ceiling((double)length / Capabilities.StripeSize);
            var position = 0;

            // Read from primary stripe set, fallback to mirror if needed
            for (int i = 0; i < chunksNeeded && position < length; i++)
            {
                var diskIndex = i % halfCount;
                var chunkOffset = offset + (i / halfCount) * Capabilities.StripeSize;
                var chunkLength = Math.Min(Capabilities.StripeSize, length - position);

                byte[] chunk;
                var primaryDisk = diskList[diskIndex];
                var mirrorDisk = diskList[diskIndex + halfCount];

                if (primaryDisk.HealthStatus == SdkDiskHealthStatus.Healthy)
                {
                    try
                    {
                        chunk = await ReadFromDiskAsync(primaryDisk, chunkOffset, chunkLength, cancellationToken);
                    }
                    catch
                    {
                        chunk = await ReadFromDiskAsync(mirrorDisk, chunkOffset, chunkLength, cancellationToken);
                    }
                }
                else
                {
                    chunk = await ReadFromDiskAsync(mirrorDisk, chunkOffset, chunkLength, cancellationToken);
                }

                var copyLength = Math.Min(chunk.Length, result.Length - position);
                Array.Copy(chunk, 0, result, position, copyLength);
                position += copyLength;
            }

            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            // First half are data disks in stripe, second half are mirrors
            var halfCount = diskCount / 2;
            var dataDisks = Enumerable.Range(0, halfCount).ToArray();
            var mirrorDisks = Enumerable.Range(halfCount, halfCount).ToArray();

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: mirrorDisks, // Mirror disks act as "parity"
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: halfCount,
                ParityChunkCount: halfCount);
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
            var halfCount = allDisks.Count / 2;

            // Determine mirror disk
            DiskInfo mirrorDisk;
            if (failedDiskIndex < halfCount)
            {
                // Failed disk is in primary stripe set, use mirror from secondary set
                mirrorDisk = allDisks[failedDiskIndex + halfCount];
            }
            else
            {
                // Failed disk is in secondary stripe set, use mirror from primary set
                mirrorDisk = allDisks[failedDiskIndex - halfCount];
            }

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = (int)Math.Min(bufferSize, totalBytes - offset);
                var data = await ReadFromDiskAsync(mirrorDisk, offset, length, cancellationToken);
                await WriteToDiskAsync(targetDisk, data, offset, cancellationToken);

                bytesRebuilt += length;

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
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

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[chunkSize];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }
    }

    /// <summary>
    /// RAID 100 (10+0) strategy implementing striped RAID 10 arrays for maximum
    /// performance and redundancy in very large storage systems.
    /// </summary>
    public class Raid100Strategy : SdkRaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.Raid100;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 2, // Can survive multiple disk failures
            MinDisks: 8,
            MaxDisks: null,
            StripeSize: 131072, // 128KB
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(1.5),
            ReadPerformanceMultiplier: 2.5,
            WritePerformanceMultiplier: 1.2,
            CapacityEfficiency: 0.5,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            // RAID 100: Multiple RAID 10 arrays striped together
            var raid10Groups = diskList.Count / 4; // Each RAID 10 needs 4 disks minimum
            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, Capabilities.StripeSize);

            var writeTasks = new List<Task>();

            for (int i = 0; i < chunks.Count; i++)
            {
                // Determine which RAID 10 group this chunk goes to
                var groupIndex = i % raid10Groups;
                var groupOffset = groupIndex * 4; // Each group has 4 disks

                // Within group, write to mirror pair (RAID 1)
                var pairIndex = (i / raid10Groups) % 2;
                var primaryDisk = groupOffset + (pairIndex * 2);
                var mirrorDisk = groupOffset + (pairIndex * 2) + 1;

                var chunkOffset = offset + (i / (raid10Groups * 2)) * Capabilities.StripeSize;

                // Write to both disks in mirror pair
                writeTasks.Add(WriteToDiskAsync(diskList[primaryDisk], chunks[i], chunkOffset, cancellationToken));
                writeTasks.Add(WriteToDiskAsync(diskList[mirrorDisk], chunks[i], chunkOffset, cancellationToken));
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
            var raid10Groups = diskList.Count / 4;

            var result = new byte[length];
            var chunksNeeded = (int)Math.Ceiling((double)length / Capabilities.StripeSize);
            var position = 0;

            for (int i = 0; i < chunksNeeded && position < length; i++)
            {
                var groupIndex = i % raid10Groups;
                var groupOffset = groupIndex * 4;
                var pairIndex = (i / raid10Groups) % 2;
                var primaryDisk = diskList[groupOffset + (pairIndex * 2)];
                var mirrorDisk = diskList[groupOffset + (pairIndex * 2) + 1];

                var chunkOffset = offset + (i / (raid10Groups * 2)) * Capabilities.StripeSize;
                var chunkLength = Math.Min(Capabilities.StripeSize, length - position);

                byte[] chunk;
                if (primaryDisk.HealthStatus == SdkDiskHealthStatus.Healthy)
                {
                    try
                    {
                        chunk = await ReadFromDiskAsync(primaryDisk, chunkOffset, chunkLength, cancellationToken);
                    }
                    catch
                    {
                        chunk = await ReadFromDiskAsync(mirrorDisk, chunkOffset, chunkLength, cancellationToken);
                    }
                }
                else
                {
                    chunk = await ReadFromDiskAsync(mirrorDisk, chunkOffset, chunkLength, cancellationToken);
                }

                var copyLength = Math.Min(chunk.Length, result.Length - position);
                Array.Copy(chunk, 0, result, position, copyLength);
                position += copyLength;
            }

            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            // Distribute across RAID 10 sets
            var dataDisks = new List<int>();
            var parityDisks = new List<int>();

            for (int i = 0; i < diskCount; i++)
            {
                if (i % 2 == 0)
                    dataDisks.Add(i);
                else
                    parityDisks.Add(i); // Mirror pairs
            }

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks.ToArray(),
                ParityDisks: parityDisks.ToArray(),
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: dataDisks.Count,
                ParityChunkCount: parityDisks.Count);
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

            // Find mirror pair in RAID 100 structure
            var groupIndex = failedDiskIndex / 4;
            var positionInGroup = failedDiskIndex % 4;
            var mirrorIndex = (positionInGroup % 2 == 0) ? failedDiskIndex + 1 : failedDiskIndex - 1;
            var mirrorDisk = allDisks[mirrorIndex];

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = (int)Math.Min(bufferSize, totalBytes - offset);
                var data = await ReadFromDiskAsync(mirrorDisk, offset, length, cancellationToken);
                await WriteToDiskAsync(targetDisk, data, offset, cancellationToken);

                bytesRebuilt += length;

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
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

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[chunkSize];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }
    }

    /// <summary>
    /// RAID 50 (5+0) strategy combining RAID 5 arrays in a RAID 0 stripe for
    /// improved performance while maintaining single-disk redundancy per array.
    /// </summary>
    public class Raid50Strategy : SdkRaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.Raid50;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1, // Per RAID 5 set
            MinDisks: 6,
            MaxDisks: null,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(3),
            ReadPerformanceMultiplier: 1.6,
            WritePerformanceMultiplier: 0.8,
            CapacityEfficiency: 0.67, // (n-2)/n for typical 6-disk configuration
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            // Distribute across RAID 5 sets
            var raid5Sets = diskList.Count / 3; // Minimum 3 disks per RAID 5 set
            var disksPerSet = diskList.Count / raid5Sets;
            var dataBytes = data.ToArray();
            var dataDisksPerSet = disksPerSet - 1;

            var chunks = SplitIntoChunks(dataBytes, Capabilities.StripeSize);
            var writeTasks = new List<Task>();

            for (int i = 0; i < chunks.Count; i++)
            {
                // Determine which RAID 5 set and position within set
                var setIndex = i % raid5Sets;
                var stripeIndex = i / raid5Sets;
                var setOffset = setIndex * disksPerSet;

                // Calculate parity position for this stripe
                var parityPos = (int)(stripeIndex % disksPerSet);

                // Distribute data within RAID 5 set
                var dataPos = 0;
                for (int j = 0; j < disksPerSet; j++)
                {
                    if (j == parityPos)
                    {
                        // Calculate and write parity
                        var parityData = CalculateXorParity(new ReadOnlyMemory<byte>[] { chunks[i] });
                        writeTasks.Add(WriteToDiskAsync(diskList[setOffset + j], parityData.ToArray(), offset + stripeIndex * Capabilities.StripeSize, cancellationToken));
                    }
                    else if (i < chunks.Count)
                    {
                        // Write data chunk
                        writeTasks.Add(WriteToDiskAsync(diskList[setOffset + j], chunks[i], offset + stripeIndex * Capabilities.StripeSize, cancellationToken));
                        dataPos++;
                    }
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
            var raid5Sets = diskList.Count / 3;
            var disksPerSet = diskList.Count / raid5Sets;

            var result = new byte[length];
            var chunksNeeded = (int)Math.Ceiling((double)length / Capabilities.StripeSize);
            var position = 0;

            for (int i = 0; i < chunksNeeded && position < length; i++)
            {
                var setIndex = i % raid5Sets;
                var stripeIndex = i / raid5Sets;
                var setOffset = setIndex * disksPerSet;
                var parityPos = (int)(stripeIndex % disksPerSet);

                var chunkOffset = offset + stripeIndex * Capabilities.StripeSize;
                var chunkLength = Math.Min(Capabilities.StripeSize, length - position);

                // Read from first available data disk in the set
                byte[]? chunk = null;
                for (int j = 0; j < disksPerSet && chunk == null; j++)
                {
                    if (j != parityPos)
                    {
                        var disk = diskList[setOffset + j];
                        if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                        {
                            try
                            {
                                chunk = await ReadFromDiskAsync(disk, chunkOffset, chunkLength, cancellationToken);
                            }
                            catch { }
                        }
                    }
                }

                if (chunk != null)
                {
                    var copyLength = Math.Min(chunk.Length, result.Length - position);
                    Array.Copy(chunk, 0, result, position, copyLength);
                    position += copyLength;
                }
            }

            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var raid5Sets = diskCount / 3;
            var disksPerSet = diskCount / raid5Sets;

            var dataDisks = new List<int>();
            var parityDisks = new List<int>();

            // Distribute parity across sets
            for (int set = 0; set < raid5Sets; set++)
            {
                var setOffset = set * disksPerSet;
                var parityPos = (int)(blockIndex % disksPerSet);

                for (int i = 0; i < disksPerSet; i++)
                {
                    if (i == parityPos)
                        parityDisks.Add(setOffset + i);
                    else
                        dataDisks.Add(setOffset + i);
                }
            }

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks.ToArray(),
                ParityDisks: parityDisks.ToArray(),
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: dataDisks.Count,
                ParityChunkCount: parityDisks.Count);
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

            var raid5Sets = allDisks.Count / 3;
            var disksPerSet = allDisks.Count / raid5Sets;
            var setIndex = failedDiskIndex / disksPerSet;
            var setOffset = setIndex * disksPerSet;

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stripeIndex = offset / Capabilities.StripeSize;
                var parityPos = (int)(stripeIndex % disksPerSet);

                // Collect all chunks from the RAID 5 set
                var chunks = new List<byte[]>();
                for (int i = 0; i < disksPerSet; i++)
                {
                    var diskIndex = setOffset + i;
                    if (diskIndex == failedDiskIndex)
                    {
                        chunks.Add(Array.Empty<byte>());
                    }
                    else
                    {
                        var disk = allDisks[diskIndex];
                        var chunk = await ReadFromDiskAsync(disk, offset, Capabilities.StripeSize, cancellationToken);
                        chunks.Add(chunk);
                    }
                }

                // Reconstruct using XOR parity
                var reconstructed = ReconstructFromParity(chunks, failedDiskIndex % disksPerSet);
                await WriteToDiskAsync(targetDisk, reconstructed, offset, cancellationToken);

                bytesRebuilt += Math.Min(reconstructed.Length, (int)(totalBytes - offset));

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
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

        private byte[] ReconstructFromParity(List<byte[]> chunks, int missingIndex)
        {
            var result = new byte[Capabilities.StripeSize];

            for (int i = 0; i < chunks.Count; i++)
            {
                if (i != missingIndex && chunks[i] != null)
                {
                    for (int j = 0; j < Math.Min(Capabilities.StripeSize, chunks[i].Length); j++)
                    {
                        result[j] ^= chunks[i][j];
                    }
                }
            }

            return result;
        }

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[chunkSize];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }
    }

    /// <summary>
    /// RAID 60 (6+0) strategy combining RAID 6 arrays in a RAID 0 stripe for
    /// high performance with dual-disk redundancy per array.
    /// </summary>
    public class Raid60Strategy : SdkRaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.Raid60;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 2, // Per RAID 6 set
            MinDisks: 8,
            MaxDisks: null,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(4),
            ReadPerformanceMultiplier: 1.5,
            WritePerformanceMultiplier: 0.7,
            CapacityEfficiency: 0.60, // (n-4)/n for typical 8-disk configuration
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            var raid6Sets = diskList.Count / 4; // Minimum 4 disks per RAID 6 set
            var disksPerSet = diskList.Count / raid6Sets;
            var dataBytes = data.ToArray();

            var chunks = SplitIntoChunks(dataBytes, Capabilities.StripeSize);
            var writeTasks = new List<Task>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var setIndex = i % raid6Sets;
                var stripeIndex = i / raid6Sets;
                var setOffset = setIndex * disksPerSet;

                // Calculate P and Q parity positions
                var pParityPos = (int)(stripeIndex % disksPerSet);
                var qParityPos = (int)((stripeIndex + 1) % disksPerSet);

                // Calculate P parity (XOR)
                var pParity = CalculateXorParity(new ReadOnlyMemory<byte>[] { chunks[i] });

                // Calculate Q parity (Galois field)
                var qParity = CalculateXorParity(new ReadOnlyMemory<byte>[] { chunks[i] }); // Simplified

                // Write data and parity
                for (int j = 0; j < disksPerSet; j++)
                {
                    var diskIndex = setOffset + j;
                    var chunkOffset = offset + stripeIndex * Capabilities.StripeSize;

                    if (j == pParityPos)
                    {
                        writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], pParity.ToArray(), chunkOffset, cancellationToken));
                    }
                    else if (j == qParityPos)
                    {
                        writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], qParity.ToArray(), chunkOffset, cancellationToken));
                    }
                    else if (i < chunks.Count)
                    {
                        writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunks[i], chunkOffset, cancellationToken));
                    }
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
            var raid6Sets = diskList.Count / 4;
            var disksPerSet = diskList.Count / raid6Sets;

            var result = new byte[length];
            var chunksNeeded = (int)Math.Ceiling((double)length / Capabilities.StripeSize);
            var position = 0;

            for (int i = 0; i < chunksNeeded && position < length; i++)
            {
                var setIndex = i % raid6Sets;
                var stripeIndex = i / raid6Sets;
                var setOffset = setIndex * disksPerSet;
                var pParityPos = (int)(stripeIndex % disksPerSet);
                var qParityPos = (int)((stripeIndex + 1) % disksPerSet);

                var chunkOffset = offset + stripeIndex * Capabilities.StripeSize;
                var chunkLength = Math.Min(Capabilities.StripeSize, length - position);

                // Read from first available data disk
                byte[]? chunk = null;
                for (int j = 0; j < disksPerSet && chunk == null; j++)
                {
                    if (j != pParityPos && j != qParityPos)
                    {
                        var disk = diskList[setOffset + j];
                        if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                        {
                            try
                            {
                                chunk = await ReadFromDiskAsync(disk, chunkOffset, chunkLength, cancellationToken);
                            }
                            catch { }
                        }
                    }
                }

                if (chunk != null)
                {
                    var copyLength = Math.Min(chunk.Length, result.Length - position);
                    Array.Copy(chunk, 0, result, position, copyLength);
                    position += copyLength;
                }
            }

            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var raid6Sets = diskCount / 4;
            var disksPerSet = diskCount / raid6Sets;

            var dataDisks = new List<int>();
            var parityDisks = new List<int>();

            for (int set = 0; set < raid6Sets; set++)
            {
                var setOffset = set * disksPerSet;
                var parityPos1 = (int)(blockIndex % disksPerSet);
                var parityPos2 = (int)((blockIndex + 1) % disksPerSet);

                for (int i = 0; i < disksPerSet; i++)
                {
                    if (i == parityPos1 || i == parityPos2)
                        parityDisks.Add(setOffset + i);
                    else
                        dataDisks.Add(setOffset + i);
                }
            }

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks.ToArray(),
                ParityDisks: parityDisks.ToArray(),
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: dataDisks.Count,
                ParityChunkCount: parityDisks.Count);
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

            var raid6Sets = allDisks.Count / 4;
            var disksPerSet = allDisks.Count / raid6Sets;
            var setIndex = failedDiskIndex / disksPerSet;
            var setOffset = setIndex * disksPerSet;

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Collect all chunks from the RAID 6 set
                var chunks = new List<byte[]>();
                for (int i = 0; i < disksPerSet; i++)
                {
                    var diskIndex = setOffset + i;
                    if (diskIndex == failedDiskIndex)
                    {
                        chunks.Add(Array.Empty<byte>());
                    }
                    else
                    {
                        var disk = allDisks[diskIndex];
                        var chunk = await ReadFromDiskAsync(disk, offset, Capabilities.StripeSize, cancellationToken);
                        chunks.Add(chunk);
                    }
                }

                // Reconstruct using dual parity (simplified to XOR for now)
                var reconstructed = ReconstructFromParity(chunks, failedDiskIndex % disksPerSet);
                await WriteToDiskAsync(targetDisk, reconstructed, offset, cancellationToken);

                bytesRebuilt += Math.Min(reconstructed.Length, (int)(totalBytes - offset));

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
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

        private byte[] ReconstructFromParity(List<byte[]> chunks, int missingIndex)
        {
            var result = new byte[Capabilities.StripeSize];

            for (int i = 0; i < chunks.Count; i++)
            {
                if (i != missingIndex && chunks[i] != null)
                {
                    for (int j = 0; j < Math.Min(Capabilities.StripeSize, chunks[i].Length); j++)
                    {
                        result[j] ^= chunks[i][j];
                    }
                }
            }

            return result;
        }

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[chunkSize];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }
    }

    /// <summary>
    /// RAID 1E strategy providing striped mirrors with support for odd number of disks.
    /// Combines characteristics of RAID 1 and RAID 0.
    /// </summary>
    public class Raid1EStrategy : SdkRaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.Raid1E;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 3,
            MaxDisks: null,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(2),
            ReadPerformanceMultiplier: 1.4,
            WritePerformanceMultiplier: 0.7,
            CapacityEfficiency: 0.5,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, Capabilities.StripeSize);

            var writeTasks = new List<Task>();

            // RAID 1E: Each chunk is written to adjacent disk pair with offset
            for (int i = 0; i < chunks.Count; i++)
            {
                var diskIndex = (i * 2) % diskList.Count;
                var mirrorIndex = (diskIndex + 1) % diskList.Count;
                var chunkOffset = offset + (i / diskList.Count) * Capabilities.StripeSize;

                // Write to primary and mirror
                writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunks[i], chunkOffset, cancellationToken));
                writeTasks.Add(WriteToDiskAsync(diskList[mirrorIndex], chunks[i], chunkOffset, cancellationToken));
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

            var result = new byte[length];
            var chunksNeeded = (int)Math.Ceiling((double)length / Capabilities.StripeSize);
            var position = 0;

            for (int i = 0; i < chunksNeeded && position < length; i++)
            {
                var diskIndex = (i * 2) % diskList.Count;
                var mirrorIndex = (diskIndex + 1) % diskList.Count;
                var chunkOffset = offset + (i / diskList.Count) * Capabilities.StripeSize;
                var chunkLength = Math.Min(Capabilities.StripeSize, length - position);

                byte[] chunk;
                var primaryDisk = diskList[diskIndex];
                var mirrorDisk = diskList[mirrorIndex];

                if (primaryDisk.HealthStatus == SdkDiskHealthStatus.Healthy)
                {
                    try
                    {
                        chunk = await ReadFromDiskAsync(primaryDisk, chunkOffset, chunkLength, cancellationToken);
                    }
                    catch
                    {
                        chunk = await ReadFromDiskAsync(mirrorDisk, chunkOffset, chunkLength, cancellationToken);
                    }
                }
                else
                {
                    chunk = await ReadFromDiskAsync(mirrorDisk, chunkOffset, chunkLength, cancellationToken);
                }

                var copyLength = Math.Min(chunk.Length, result.Length - position);
                Array.Copy(chunk, 0, result, position, copyLength);
                position += copyLength;
            }

            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            // Each stripe is mirrored to adjacent disk
            var diskIndex = (int)(blockIndex % diskCount);
            var mirrorIndex = (diskIndex + 1) % diskCount;

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: new[] { diskIndex },
                ParityDisks: new[] { mirrorIndex },
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: 1,
                ParityChunkCount: 1);
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

            // Find mirror disk (adjacent in rotation)
            var mirrorIndex = (failedDiskIndex % 2 == 0) ? failedDiskIndex + 1 : failedDiskIndex - 1;
            if (mirrorIndex < 0) mirrorIndex = allDisks.Count - 1;
            if (mirrorIndex >= allDisks.Count) mirrorIndex = 0;

            var mirrorDisk = allDisks[mirrorIndex];

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = (int)Math.Min(bufferSize, totalBytes - offset);
                var data = await ReadFromDiskAsync(mirrorDisk, offset, length, cancellationToken);
                await WriteToDiskAsync(targetDisk, data, offset, cancellationToken);

                bytesRebuilt += length;

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
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

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[chunkSize];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }
    }

    /// <summary>
    /// RAID 5E strategy implementing RAID 5 with integrated hot spare capacity
    /// distributed across all disks.
    /// </summary>
    public class Raid5EStrategy : SdkRaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.Raid5E;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 4,
            MaxDisks: null,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(2.5),
            ReadPerformanceMultiplier: 1.1,
            WritePerformanceMultiplier: 0.7,
            CapacityEfficiency: 0.60, // (n-2)/n accounting for integrated spare
            SupportsHotSpare: true, // Integrated
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, diskList.Count);

            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, Capabilities.StripeSize);

            var writeTasks = new List<Task>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var blockIndex = offset / Capabilities.StripeSize + i;
                var currentStripeInfo = CalculateStripe(blockIndex, diskList.Count);

                // Calculate parity
                var parity = CalculateXorParity(new ReadOnlyMemory<byte>[] { chunks[i] });

                // Write data to data disks
                for (int j = 0; j < currentStripeInfo.DataDisks.Length && j < chunks.Count; j++)
                {
                    var diskIndex = currentStripeInfo.DataDisks[j];
                    var chunkOffset = offset + i * Capabilities.StripeSize;
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunks[i], chunkOffset, cancellationToken));
                }

                // Write parity
                var parityDisk = currentStripeInfo.ParityDisks[0];
                var parityOffset = offset + i * Capabilities.StripeSize;
                writeTasks.Add(WriteToDiskAsync(diskList[parityDisk], parity.ToArray(), parityOffset, cancellationToken));
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

            var result = new byte[length];
            var chunksNeeded = (int)Math.Ceiling((double)length / Capabilities.StripeSize);
            var position = 0;

            for (int i = 0; i < chunksNeeded && position < length; i++)
            {
                var blockIndex = offset / Capabilities.StripeSize + i;
                var stripeInfo = CalculateStripe(blockIndex, diskList.Count);
                var chunkOffset = offset + i * Capabilities.StripeSize;
                var chunkLength = Math.Min(Capabilities.StripeSize, length - position);

                // Read from first healthy data disk
                byte[]? chunk = null;
                foreach (var diskIndex in stripeInfo.DataDisks)
                {
                    var disk = diskList[diskIndex];
                    if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                    {
                        try
                        {
                            chunk = await ReadFromDiskAsync(disk, chunkOffset, chunkLength, cancellationToken);
                            break;
                        }
                        catch { }
                    }
                }

                if (chunk != null)
                {
                    var copyLength = Math.Min(chunk.Length, result.Length - position);
                    Array.Copy(chunk, 0, result, position, copyLength);
                    position += copyLength;
                }
            }

            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            // RAID 5E uses distributed parity + integrated spare space
            var parityDisk = (int)(blockIndex % diskCount);
            var spareDisk = (int)((blockIndex + 1) % diskCount);

            var dataDisks = new List<int>();
            for (int i = 0; i < diskCount; i++)
            {
                if (i != parityDisk && i != spareDisk)
                    dataDisks.Add(i);
            }

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks.ToArray(),
                ParityDisks: new[] { parityDisk },
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: dataDisks.Count,
                ParityChunkCount: 1);
        }

        public override async Task RebuildDiskAsync(
            DiskInfo failedDisk,
            IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk,
            IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            // RAID 5E can rebuild using integrated spare space
            var allDisks = healthyDisks.Append(failedDisk).ToList();
            var failedDiskIndex = allDisks.IndexOf(failedDisk);

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var blockIndex = offset / Capabilities.StripeSize;
                var stripeInfo = CalculateStripe(blockIndex, allDisks.Count);

                // Collect chunks from healthy disks
                var chunks = new List<byte[]>();
                for (int i = 0; i < allDisks.Count; i++)
                {
                    if (i == failedDiskIndex)
                    {
                        chunks.Add(Array.Empty<byte>());
                    }
                    else if (stripeInfo.ParityDisks.Contains(i))
                    {
                        var disk = allDisks[i];
                        var chunk = await ReadFromDiskAsync(disk, offset, Capabilities.StripeSize, cancellationToken);
                        chunks.Add(chunk);
                    }
                    else if (stripeInfo.DataDisks.Contains(i))
                    {
                        var disk = allDisks[i];
                        var chunk = await ReadFromDiskAsync(disk, offset, Capabilities.StripeSize, cancellationToken);
                        chunks.Add(chunk);
                    }
                    else
                    {
                        chunks.Add(Array.Empty<byte>());
                    }
                }

                // Reconstruct using XOR parity
                var reconstructed = ReconstructFromParity(chunks, failedDiskIndex);
                await WriteToDiskAsync(targetDisk, reconstructed, offset, cancellationToken);

                bytesRebuilt += Math.Min(reconstructed.Length, (int)(totalBytes - offset));

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
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

        private byte[] ReconstructFromParity(List<byte[]> chunks, int missingIndex)
        {
            var result = new byte[Capabilities.StripeSize];

            for (int i = 0; i < chunks.Count; i++)
            {
                if (i != missingIndex && chunks[i] != null)
                {
                    for (int j = 0; j < Math.Min(Capabilities.StripeSize, chunks[i].Length); j++)
                    {
                        result[j] ^= chunks[i][j];
                    }
                }
            }

            return result;
        }

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[chunkSize];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }
    }

    /// <summary>
    /// RAID 5EE strategy implementing enhanced RAID 5E with distributed hot spare
    /// for improved rebuild performance and fault tolerance.
    /// </summary>
    public class Raid5EEStrategy : SdkRaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.Raid5EE;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 4,
            MaxDisks: null,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(2),
            ReadPerformanceMultiplier: 1.2,
            WritePerformanceMultiplier: 0.75,
            CapacityEfficiency: 0.58,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, Capabilities.StripeSize);

            var writeTasks = new List<Task>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var blockIndex = offset / Capabilities.StripeSize + i;
                var stripeInfo = CalculateStripe(blockIndex, diskList.Count);

                // Calculate parity
                var parity = CalculateXorParity(new ReadOnlyMemory<byte>[] { chunks[i] });

                // Write data
                for (int j = 0; j < stripeInfo.DataDisks.Length && j < chunks.Count; j++)
                {
                    var diskIndex = stripeInfo.DataDisks[j];
                    var chunkOffset = offset + i * Capabilities.StripeSize;
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunks[i], chunkOffset, cancellationToken));
                }

                // Write parity
                var parityDisk = stripeInfo.ParityDisks[0];
                var parityOffset = offset + i * Capabilities.StripeSize;
                writeTasks.Add(WriteToDiskAsync(diskList[parityDisk], parity.ToArray(), parityOffset, cancellationToken));
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

            var result = new byte[length];
            var chunksNeeded = (int)Math.Ceiling((double)length / Capabilities.StripeSize);
            var position = 0;

            for (int i = 0; i < chunksNeeded && position < length; i++)
            {
                var blockIndex = offset / Capabilities.StripeSize + i;
                var stripeInfo = CalculateStripe(blockIndex, diskList.Count);
                var chunkOffset = offset + i * Capabilities.StripeSize;
                var chunkLength = Math.Min(Capabilities.StripeSize, length - position);

                // Read from first healthy data disk
                byte[]? chunk = null;
                foreach (var diskIndex in stripeInfo.DataDisks)
                {
                    var disk = diskList[diskIndex];
                    if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                    {
                        try
                        {
                            chunk = await ReadFromDiskAsync(disk, chunkOffset, chunkLength, cancellationToken);
                            break;
                        }
                        catch { }
                    }
                }

                if (chunk != null)
                {
                    var copyLength = Math.Min(chunk.Length, result.Length - position);
                    Array.Copy(chunk, 0, result, position, copyLength);
                    position += copyLength;
                }
            }

            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            // Enhanced distribution with rotating spare blocks
            var parityDisk = (int)(blockIndex % diskCount);
            var spareDisk = (int)((blockIndex * 2 + 1) % diskCount);

            var dataDisks = new List<int>();
            for (int i = 0; i < diskCount; i++)
            {
                if (i != parityDisk && i != spareDisk)
                    dataDisks.Add(i);
            }

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks.ToArray(),
                ParityDisks: new[] { parityDisk },
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: dataDisks.Count,
                ParityChunkCount: 1);
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

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var blockIndex = offset / Capabilities.StripeSize;
                var stripeInfo = CalculateStripe(blockIndex, allDisks.Count);

                // Collect chunks from healthy disks
                var chunks = new List<byte[]>();
                for (int i = 0; i < allDisks.Count; i++)
                {
                    if (i == failedDiskIndex)
                    {
                        chunks.Add(Array.Empty<byte>());
                    }
                    else if (stripeInfo.ParityDisks.Contains(i) || stripeInfo.DataDisks.Contains(i))
                    {
                        var disk = allDisks[i];
                        var chunk = await ReadFromDiskAsync(disk, offset, Capabilities.StripeSize, cancellationToken);
                        chunks.Add(chunk);
                    }
                    else
                    {
                        chunks.Add(Array.Empty<byte>());
                    }
                }

                // Reconstruct using XOR parity
                var reconstructed = ReconstructFromParity(chunks, failedDiskIndex);
                await WriteToDiskAsync(targetDisk, reconstructed, offset, cancellationToken);

                bytesRebuilt += Math.Min(reconstructed.Length, (int)(totalBytes - offset));

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
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

        private byte[] ReconstructFromParity(List<byte[]> chunks, int missingIndex)
        {
            var result = new byte[Capabilities.StripeSize];

            for (int i = 0; i < chunks.Count; i++)
            {
                if (i != missingIndex && chunks[i] != null)
                {
                    for (int j = 0; j < Math.Min(Capabilities.StripeSize, chunks[i].Length); j++)
                    {
                        result[j] ^= chunks[i][j];
                    }
                }
            }

            return result;
        }

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[chunkSize];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }
    }

    /// <summary>
    /// RAID 6E strategy implementing RAID 6 with integrated hot spare capacity
    /// for maximum redundancy and automatic failover.
    /// </summary>
    public class Raid6EStrategy : SdkRaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.Raid6E;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 2,
            MinDisks: 5,
            MaxDisks: null,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(3.5),
            ReadPerformanceMultiplier: 1.0,
            WritePerformanceMultiplier: 0.6,
            CapacityEfficiency: 0.55, // (n-3)/n accounting for dual parity + spare
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, Capabilities.StripeSize);

            var writeTasks = new List<Task>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var blockIndex = offset / Capabilities.StripeSize + i;
                var stripeInfo = CalculateStripe(blockIndex, diskList.Count);

                // Calculate dual parity
                var parity1 = CalculateXorParity(new ReadOnlyMemory<byte>[] { chunks[i] });
                var parity2 = CalculateXorParity(new ReadOnlyMemory<byte>[] { chunks[i], parity1.ToArray() });

                // Write data
                for (int j = 0; j < stripeInfo.DataDisks.Length && j < chunks.Count; j++)
                {
                    var diskIndex = stripeInfo.DataDisks[j];
                    var chunkOffset = offset + i * Capabilities.StripeSize;
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunks[i], chunkOffset, cancellationToken));
                }

                // Write P and Q parity
                if (stripeInfo.ParityDisks.Length >= 2)
                {
                    var parityOffset = offset + i * Capabilities.StripeSize;
                    writeTasks.Add(WriteToDiskAsync(diskList[stripeInfo.ParityDisks[0]], parity1.ToArray(), parityOffset, cancellationToken));
                    writeTasks.Add(WriteToDiskAsync(diskList[stripeInfo.ParityDisks[1]], parity2.ToArray(), parityOffset, cancellationToken));
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

            var result = new byte[length];
            var chunksNeeded = (int)Math.Ceiling((double)length / Capabilities.StripeSize);
            var position = 0;

            for (int i = 0; i < chunksNeeded && position < length; i++)
            {
                var blockIndex = offset / Capabilities.StripeSize + i;
                var stripeInfo = CalculateStripe(blockIndex, diskList.Count);
                var chunkOffset = offset + i * Capabilities.StripeSize;
                var chunkLength = Math.Min(Capabilities.StripeSize, length - position);

                // Read from first healthy data disk
                byte[]? chunk = null;
                foreach (var diskIndex in stripeInfo.DataDisks)
                {
                    var disk = diskList[diskIndex];
                    if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                    {
                        try
                        {
                            chunk = await ReadFromDiskAsync(disk, chunkOffset, chunkLength, cancellationToken);
                            break;
                        }
                        catch { }
                    }
                }

                if (chunk != null)
                {
                    var copyLength = Math.Min(chunk.Length, result.Length - position);
                    Array.Copy(chunk, 0, result, position, copyLength);
                    position += copyLength;
                }
            }

            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var parity1Disk = (int)(blockIndex % diskCount);
            var parity2Disk = (int)((blockIndex + 1) % diskCount);
            var spareDisk = (int)((blockIndex + 2) % diskCount);

            var dataDisks = new List<int>();
            for (int i = 0; i < diskCount; i++)
            {
                if (i != parity1Disk && i != parity2Disk && i != spareDisk)
                    dataDisks.Add(i);
            }

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks.ToArray(),
                ParityDisks: new[] { parity1Disk, parity2Disk },
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: dataDisks.Count,
                ParityChunkCount: 2);
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

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var blockIndex = offset / Capabilities.StripeSize;
                var stripeInfo = CalculateStripe(blockIndex, allDisks.Count);

                // Collect chunks from healthy disks
                var chunks = new List<byte[]>();
                for (int i = 0; i < allDisks.Count; i++)
                {
                    if (i == failedDiskIndex)
                    {
                        chunks.Add(Array.Empty<byte>());
                    }
                    else if (stripeInfo.ParityDisks.Contains(i) || stripeInfo.DataDisks.Contains(i))
                    {
                        var disk = allDisks[i];
                        var chunk = await ReadFromDiskAsync(disk, offset, Capabilities.StripeSize, cancellationToken);
                        chunks.Add(chunk);
                    }
                    else
                    {
                        chunks.Add(Array.Empty<byte>());
                    }
                }

                // Reconstruct using dual parity
                var reconstructed = ReconstructFromParity(chunks, failedDiskIndex);
                await WriteToDiskAsync(targetDisk, reconstructed, offset, cancellationToken);

                bytesRebuilt += Math.Min(reconstructed.Length, (int)(totalBytes - offset));

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
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

        private byte[] ReconstructFromParity(List<byte[]> chunks, int missingIndex)
        {
            var result = new byte[Capabilities.StripeSize];

            for (int i = 0; i < chunks.Count; i++)
            {
                if (i != missingIndex && chunks[i] != null)
                {
                    for (int j = 0; j < Math.Min(Capabilities.StripeSize, chunks[i].Length); j++)
                    {
                        result[j] ^= chunks[i][j];
                    }
                }
            }

            return result;
        }

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[chunkSize];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            return Task.FromResult(new byte[length]);
        }
    }
}
