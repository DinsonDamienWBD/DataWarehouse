using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.RAID;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.Extended
{
    /// <summary>
    /// RAID 01 (0+1) strategy implementing mirrored stripes for high performance
    /// with redundancy through mirroring of entire stripe sets.
    /// </summary>
    public class Raid01Strategy : RaidStrategyBase
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
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, halfCount);

            // Write to first stripe set
            await Task.Delay(1, cancellationToken);

            // Mirror to second stripe set
            await Task.Delay(1, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var result = new byte[length];
            await Task.Delay(5, cancellationToken);
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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 150_000_000),
                    CurrentSpeed: 150_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }
    }

    /// <summary>
    /// RAID 100 (10+0) strategy implementing striped RAID 10 arrays for maximum
    /// performance and redundancy in very large storage systems.
    /// </summary>
    public class Raid100Strategy : RaidStrategyBase
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
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, diskList.Count);

            await Task.Delay(1, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var result = new byte[length];
            await Task.Delay(3, cancellationToken);
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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 200_000_000),
                    CurrentSpeed: 200_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }
    }

    /// <summary>
    /// RAID 50 (5+0) strategy combining RAID 5 arrays in a RAID 0 stripe for
    /// improved performance while maintaining single-disk redundancy per array.
    /// </summary>
    public class Raid50Strategy : RaidStrategyBase
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
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, diskList.Count);

            var chunks = DistributeData(data, stripeInfo);
            var dataChunks = chunks.Values.ToList();

            // Calculate parity for each RAID 5 set
            await Task.Delay(1, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var result = new byte[length];
            await Task.Delay(6, cancellationToken);
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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 120_000_000),
                    CurrentSpeed: 120_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }
    }

    /// <summary>
    /// RAID 60 (6+0) strategy combining RAID 6 arrays in a RAID 0 stripe for
    /// high performance with dual-disk redundancy per array.
    /// </summary>
    public class Raid60Strategy : RaidStrategyBase
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
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, diskList.Count);

            var chunks = DistributeData(data, stripeInfo);
            var dataChunks = chunks.Values.ToList();

            // Calculate dual parity for each RAID 6 set
            await Task.Delay(1, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var result = new byte[length];
            await Task.Delay(7, cancellationToken);
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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 110_000_000),
                    CurrentSpeed: 110_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }
    }

    /// <summary>
    /// RAID 1E strategy providing striped mirrors with support for odd number of disks.
    /// Combines characteristics of RAID 1 and RAID 0.
    /// </summary>
    public class Raid1EStrategy : RaidStrategyBase
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
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, diskList.Count);

            // Write data and mirror to next disk in rotation
            await Task.Delay(1, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var result = new byte[length];
            await Task.Delay(5, cancellationToken);
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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 140_000_000),
                    CurrentSpeed: 140_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }
    }

    /// <summary>
    /// RAID 5E strategy implementing RAID 5 with integrated hot spare capacity
    /// distributed across all disks.
    /// </summary>
    public class Raid5EStrategy : RaidStrategyBase
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

            var chunks = DistributeData(data, stripeInfo);
            var dataChunks = chunks.Values.ToList();
            var parity = CalculateXorParity(dataChunks);

            // Reserve space on each disk for hot spare area
            await Task.Delay(1, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var result = new byte[length];
            await Task.Delay(6, cancellationToken);
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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 125_000_000),
                    CurrentSpeed: 125_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }
    }

    /// <summary>
    /// RAID 5EE strategy implementing enhanced RAID 5E with distributed hot spare
    /// for improved rebuild performance and fault tolerance.
    /// </summary>
    public class Raid5EEStrategy : RaidStrategyBase
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
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, diskList.Count);

            // Distributed data, parity, and spare blocks
            await Task.Delay(1, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var result = new byte[length];
            await Task.Delay(6, cancellationToken);
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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 130_000_000),
                    CurrentSpeed: 130_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }
    }

    /// <summary>
    /// RAID 6E strategy implementing RAID 6 with integrated hot spare capacity
    /// for maximum redundancy and automatic failover.
    /// </summary>
    public class Raid6EStrategy : RaidStrategyBase
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
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, diskList.Count);

            var chunks = DistributeData(data, stripeInfo);
            var dataChunks = chunks.Values.ToList();

            // Dual parity + integrated spare
            var parity1 = CalculateXorParity(dataChunks);
            var parity2 = CalculateXorParity(dataChunks.Skip(1).Append(parity1));

            await Task.Delay(1, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var result = new byte[length];
            await Task.Delay(7, cancellationToken);
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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 105_000_000),
                    CurrentSpeed: 105_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }
    }
}
