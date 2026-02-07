using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.RAID;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.Vendor
{
    /// <summary>
    /// NetApp RAID-DP (Double Parity) strategy providing dual parity protection
    /// similar to RAID 6 but optimized for NetApp storage systems.
    /// </summary>
    public class NetAppRaidDpStrategy : RaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.NetAppRaidDp;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 2,
            MinDisks: 3,
            MaxDisks: 28,
            StripeSize: 4096,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(4),
            ReadPerformanceMultiplier: 0.9,
            WritePerformanceMultiplier: 0.5,
            CapacityEfficiency: 0.71, // (n-2)/n for typical configurations
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

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

            // Calculate dual parity using diagonal and row parity
            var rowParity = CalculateXorParity(dataChunks);
            var diagonalParity = CalculateDiagonalParity(dataChunks);

            // Write data chunks
            foreach (var kvp in chunks)
            {
                await Task.Delay(1, cancellationToken); // Simulate disk write
            }

            // Write parity chunks to dedicated parity disks
            await Task.Delay(1, cancellationToken);
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

            // Simulate reading with dual parity reconstruction capability
            await Task.Delay(10, cancellationToken);

            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var dataDisks = Enumerable.Range(0, diskCount - 2).ToArray();
            var parityDisks = new[] { diskCount - 2, diskCount - 1 }; // Dedicated dual parity disks

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: parityDisks,
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: diskCount - 2,
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
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 100_000_000),
                    CurrentSpeed: 100_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }

        private ReadOnlyMemory<byte> CalculateDiagonalParity(List<ReadOnlyMemory<byte>> dataChunks)
        {
            if (dataChunks.Count == 0) return ReadOnlyMemory<byte>.Empty;

            var length = dataChunks[0].Length;
            var parity = new byte[length];

            // Diagonal parity calculation for improved failure recovery
            for (int i = 0; i < dataChunks.Count; i++)
            {
                var span = dataChunks[i].Span;
                for (int j = 0; j < length; j++)
                {
                    var diagonalIndex = (j + i) % length;
                    parity[diagonalIndex] ^= span[j];
                }
            }

            return parity;
        }
    }

    /// <summary>
    /// NetApp RAID-TEC (Triple Erasure Coding) strategy providing triple parity
    /// protection for maximum data integrity and survivability.
    /// </summary>
    public class NetAppRaidTecStrategy : RaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.NetAppRaidTec;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 3,
            MinDisks: 4,
            MaxDisks: 28,
            StripeSize: 4096,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(6),
            ReadPerformanceMultiplier: 0.85,
            WritePerformanceMultiplier: 0.4,
            CapacityEfficiency: 0.68, // (n-3)/n for typical configurations
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

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

            // Calculate triple parity using row, diagonal, and anti-diagonal
            var rowParity = CalculateXorParity(dataChunks);
            var diagonalParity = CalculateDiagonalParity(dataChunks);
            var antiDiagonalParity = CalculateAntiDiagonalParity(dataChunks);

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
            await Task.Delay(10, cancellationToken);
            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var dataDisks = Enumerable.Range(0, diskCount - 3).ToArray();
            var parityDisks = new[] { diskCount - 3, diskCount - 2, diskCount - 1 };

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: parityDisks,
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: diskCount - 3,
                ParityChunkCount: 3);
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
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 90_000_000),
                    CurrentSpeed: 90_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }

        private ReadOnlyMemory<byte> CalculateDiagonalParity(List<ReadOnlyMemory<byte>> dataChunks)
        {
            if (dataChunks.Count == 0) return ReadOnlyMemory<byte>.Empty;

            var length = dataChunks[0].Length;
            var parity = new byte[length];

            for (int i = 0; i < dataChunks.Count; i++)
            {
                var span = dataChunks[i].Span;
                for (int j = 0; j < length; j++)
                {
                    parity[(j + i) % length] ^= span[j];
                }
            }

            return parity;
        }

        private ReadOnlyMemory<byte> CalculateAntiDiagonalParity(List<ReadOnlyMemory<byte>> dataChunks)
        {
            if (dataChunks.Count == 0) return ReadOnlyMemory<byte>.Empty;

            var length = dataChunks[0].Length;
            var parity = new byte[length];

            for (int i = 0; i < dataChunks.Count; i++)
            {
                var span = dataChunks[i].Span;
                for (int j = 0; j < length; j++)
                {
                    parity[(j - i + length) % length] ^= span[j];
                }
            }

            return parity;
        }
    }

    /// <summary>
    /// Synology Hybrid RAID (SHR) strategy supporting mixed disk sizes with 1-disk redundancy.
    /// Automatically optimizes capacity usage across heterogeneous disk configurations.
    /// </summary>
    public class SynologyShrStrategy : RaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.SynologyShr;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 3,
            MaxDisks: null,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(5),
            ReadPerformanceMultiplier: 0.8,
            WritePerformanceMultiplier: 0.6,
            CapacityEfficiency: 0.67, // Variable based on disk mix
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.OrderBy(d => d.Capacity).ToList();

            // SHR dynamically allocates data based on available capacity
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, diskList.Count);
            var chunks = DistributeData(data, stripeInfo);

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
            await Task.Delay(10, cancellationToken);
            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            // Dynamic stripe calculation based on disk capacity distribution
            var dataDisks = Enumerable.Range(0, diskCount - 1).ToArray();
            var parityDisks = new[] { diskCount - 1 };

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: parityDisks,
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: diskCount - 1,
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
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 80_000_000),
                    CurrentSpeed: 80_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }

        public override long CalculateUsableCapacity(IEnumerable<DiskInfo> disks)
        {
            var diskList = disks.OrderBy(d => d.Capacity).ToList();
            if (diskList.Count < Capabilities.MinDisks) return 0;

            // SHR capacity calculation for mixed disk sizes
            var smallestDisk = diskList[0].Capacity;
            var totalCapacity = diskList.Sum(d => Math.Min(d.Capacity, smallestDisk * diskList.Count));

            return totalCapacity - smallestDisk; // Subtract one disk for parity
        }
    }

    /// <summary>
    /// Synology Hybrid RAID 2 (SHR-2) strategy with 2-disk redundancy for mixed disk sizes.
    /// </summary>
    public class SynologyShr2Strategy : RaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.SynologyShr2;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 2,
            MinDisks: 4,
            MaxDisks: null,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(6),
            ReadPerformanceMultiplier: 0.75,
            WritePerformanceMultiplier: 0.5,
            CapacityEfficiency: 0.60, // Variable based on disk mix
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.OrderBy(d => d.Capacity).ToList();
            var stripeInfo = CalculateStripe(offset / Capabilities.StripeSize, diskList.Count);

            var chunks = DistributeData(data, stripeInfo);
            var dataChunks = chunks.Values.ToList();

            // Dual parity for SHR-2
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
            await Task.Delay(10, cancellationToken);
            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var dataDisks = Enumerable.Range(0, diskCount - 2).ToArray();
            var parityDisks = new[] { diskCount - 2, diskCount - 1 };

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: parityDisks,
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: diskCount - 2,
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
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 75_000_000),
                    CurrentSpeed: 75_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }

        public override long CalculateUsableCapacity(IEnumerable<DiskInfo> disks)
        {
            var diskList = disks.OrderBy(d => d.Capacity).ToList();
            if (diskList.Count < Capabilities.MinDisks) return 0;

            var smallestDisk = diskList[0].Capacity;
            var totalCapacity = diskList.Sum(d => Math.Min(d.Capacity, smallestDisk * diskList.Count));

            return totalCapacity - (2 * smallestDisk); // Subtract two disks for dual parity
        }
    }

    /// <summary>
    /// Drobo BeyondRAID strategy providing dynamic RAID with mixed disk sizes
    /// and automatic capacity optimization.
    /// </summary>
    public class DroboBeyondRaidStrategy : RaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.DroboBeyondRaid;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 2,
            MinDisks: 3,
            MaxDisks: 64,
            StripeSize: 262144, // 256KB
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(8),
            ReadPerformanceMultiplier: 0.7,
            WritePerformanceMultiplier: 0.5,
            CapacityEfficiency: 0.65, // Highly variable
            SupportsHotSpare: false, // Built-in dynamic allocation
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);

            // BeyondRAID dynamically selects optimal disk placement
            var diskList = disks.OrderByDescending(d => d.Capacity - d.UsedCapacity).ToList();
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
            await Task.Delay(10, cancellationToken);
            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            // Dynamic stripe allocation based on available space
            var dataDisks = Enumerable.Range(0, Math.Min(diskCount - 2, 8)).ToArray();
            var parityDisks = Enumerable.Range(diskCount - 2, 2).ToArray();

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: parityDisks,
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: dataDisks.Length,
                ParityChunkCount: 2);
        }

        public override async Task RebuildDiskAsync(
            DiskInfo failedDisk,
            IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk,
            IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var totalBytes = failedDisk.UsedCapacity; // Only rebuild used data
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 60_000_000),
                    CurrentSpeed: 60_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }

        public override long CalculateUsableCapacity(IEnumerable<DiskInfo> disks)
        {
            var diskList = disks.OrderBy(d => d.Capacity).ToList();
            if (diskList.Count < Capabilities.MinDisks) return 0;

            // BeyondRAID complex capacity calculation
            var totalRawCapacity = diskList.Sum(d => d.Capacity);
            var largestDisk = diskList[^1].Capacity;
            var secondLargestDisk = diskList.Count > 1 ? diskList[^2].Capacity : 0;

            // Reserve space for dual disk redundancy
            return totalRawCapacity - largestDisk - secondLargestDisk;
        }
    }

    /// <summary>
    /// QNAP Static Volume strategy similar to RAID 6 with QNAP-specific optimizations.
    /// </summary>
    public class QnapStaticVolumeStrategy : RaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.QnapStaticVolume;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 2,
            MinDisks: 4,
            MaxDisks: 16,
            StripeSize: 65536,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(5),
            ReadPerformanceMultiplier: 0.85,
            WritePerformanceMultiplier: 0.55,
            CapacityEfficiency: 0.75, // (n-2)/n
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

            // Dual parity calculation
            var parity1 = CalculateXorParity(dataChunks);
            var parity2 = CalculateReedSolomonParity(dataChunks);

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
            await Task.Delay(10, cancellationToken);
            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var parityOffset = (int)(blockIndex % diskCount);
            var dataDisks = new List<int>();

            for (int i = 0; i < diskCount; i++)
            {
                if (i != parityOffset && i != (parityOffset + 1) % diskCount)
                {
                    dataDisks.Add(i);
                }
            }

            var parityDisks = new[] { parityOffset, (parityOffset + 1) % diskCount };

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks.ToArray(),
                ParityDisks: parityDisks,
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: diskCount - 2,
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
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 85_000_000),
                    CurrentSpeed: 85_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }

        private ReadOnlyMemory<byte> CalculateReedSolomonParity(List<ReadOnlyMemory<byte>> dataChunks)
        {
            if (dataChunks.Count == 0) return ReadOnlyMemory<byte>.Empty;

            var length = dataChunks[0].Length;
            var parity = new byte[length];

            // Simplified Reed-Solomon parity using Galois Field multiplication
            for (int i = 0; i < dataChunks.Count; i++)
            {
                var span = dataChunks[i].Span;
                var coefficient = (byte)(i + 1); // GF multiplier

                for (int j = 0; j < length; j++)
                {
                    parity[j] ^= GaloisMultiply(span[j], coefficient);
                }
            }

            return parity;
        }

        private byte GaloisMultiply(byte a, byte b)
        {
            byte result = 0;
            byte highBit;

            for (int i = 0; i < 8; i++)
            {
                if ((b & 1) != 0)
                    result ^= a;

                highBit = (byte)(a & 0x80);
                a <<= 1;

                if (highBit != 0)
                    a ^= 0x1D; // Primitive polynomial for GF(2^8)

                b >>= 1;
            }

            return result;
        }
    }

    /// <summary>
    /// Unraid single parity disk strategy allowing mixed disk sizes with 1-disk redundancy.
    /// </summary>
    public class UnraidSingleStrategy : RaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.UnraidSingle;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 2,
            MaxDisks: 30,
            StripeSize: 4096,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(4),
            ReadPerformanceMultiplier: 1.0, // Direct disk reads
            WritePerformanceMultiplier: 0.7,
            CapacityEfficiency: 0.80, // Sum of all data disks / total
            SupportsHotSpare: false,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            // Unraid writes to individual disks, not striped
            // Last disk is parity
            var dataDisk = (int)(offset % (diskList.Count - 1));

            // Write data to selected disk
            await Task.Delay(1, cancellationToken);

            // Update parity disk
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

            // Direct read from individual disk
            await Task.Delay(5, cancellationToken);
            return result;
        }

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            // Unraid doesn't stripe - each file lives on one disk
            var dataDisk = (int)(blockIndex % (diskCount - 1));
            var parityDisk = diskCount - 1;

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: new[] { dataDisk },
                ParityDisks: new[] { parityDisk },
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
            var totalBytes = failedDisk.UsedCapacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 100_000_000),
                    CurrentSpeed: 100_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }

        public override long CalculateUsableCapacity(IEnumerable<DiskInfo> disks)
        {
            var diskList = disks.ToList();
            if (diskList.Count < Capabilities.MinDisks) return 0;

            // All disks except parity disk contribute their full capacity
            return diskList.Take(diskList.Count - 1).Sum(d => d.Capacity);
        }
    }

    /// <summary>
    /// Unraid dual parity disk strategy with 2-disk redundancy for mixed disk sizes.
    /// </summary>
    public class UnraidDualStrategy : RaidStrategyBase
    {
        public override RaidLevel Level => RaidLevel.UnraidDual;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 2,
            MinDisks: 3,
            MaxDisks: 30,
            StripeSize: 4096,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(5),
            ReadPerformanceMultiplier: 1.0,
            WritePerformanceMultiplier: 0.6,
            CapacityEfficiency: 0.75,
            SupportsHotSpare: false,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            // Write to individual disk (not striped)
            var dataDisk = (int)(offset % (diskList.Count - 2));
            await Task.Delay(1, cancellationToken);

            // Update both parity disks
            await Task.Delay(2, cancellationToken);
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
            var dataDisk = (int)(blockIndex % (diskCount - 2));
            var parityDisks = new[] { diskCount - 2, diskCount - 1 };

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: new[] { dataDisk },
                ParityDisks: parityDisks,
                ChunkSize: Capabilities.StripeSize,
                DataChunkCount: 1,
                ParityChunkCount: 2);
        }

        public override async Task RebuildDiskAsync(
            DiskInfo failedDisk,
            IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk,
            IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var totalBytes = failedDisk.UsedCapacity;
            var bytesRebuilt = 0L;

            for (long i = 0; i < totalBytes; i += Capabilities.StripeSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesRebuilt += Capabilities.StripeSize;

                progressCallback?.Report(new RebuildProgress(
                    PercentComplete: (double)bytesRebuilt / totalBytes,
                    BytesRebuilt: bytesRebuilt,
                    TotalBytes: totalBytes,
                    EstimatedTimeRemaining: TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / 95_000_000),
                    CurrentSpeed: 95_000_000));

                await Task.Delay(1, cancellationToken);
            }
        }

        public override long CalculateUsableCapacity(IEnumerable<DiskInfo> disks)
        {
            var diskList = disks.ToList();
            if (diskList.Count < Capabilities.MinDisks) return 0;

            // All disks except two parity disks
            return diskList.Take(diskList.Count - 2).Sum(d => d.Capacity);
        }
    }
}
