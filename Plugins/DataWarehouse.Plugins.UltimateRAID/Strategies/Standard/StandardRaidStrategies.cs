using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.RAID;
using DataWarehouse.SDK.Mathematics;
using SdkRaidStrategyBase = DataWarehouse.SDK.Contracts.RAID.RaidStrategyBase;
using SdkDiskHealthStatus = DataWarehouse.SDK.Contracts.RAID.DiskHealthStatus;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.Standard
{
    /// <summary>
    /// Production-ready RAID 0 strategy - Striping without redundancy.
    /// Provides maximum performance by distributing data across all disks without parity.
    /// Data loss occurs if any single disk fails.
    /// </summary>
    public sealed class Raid0Strategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private CancellationTokenSource? _rebuildCancellation;

        public Raid0Strategy(int chunkSize = 64 * 1024)
        {
            _chunkSize = chunkSize;
        }

        public override RaidLevel Level => RaidLevel.Raid0;

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            // Validate configuration
            if (_chunkSize < 4096 || _chunkSize > 1024 * 1024)
                throw new ArgumentException($"Chunk size must be between 4KB and 1MB, got {_chunkSize}");

            if ((_chunkSize & (_chunkSize - 1)) != 0)
                throw new ArgumentException($"Chunk size must be a power of 2, got {_chunkSize}");

            return Task.CompletedTask;
        }

        public override async Task<RaidHealth> CheckHealthAsync(
            IEnumerable<DiskInfo> disks,
            CancellationToken cancellationToken = default)
        {
            IncrementCounter("raid0.health_check");
            return await base.CheckHealthAsync(disks, cancellationToken);
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            // Cancel any ongoing rebuild operations (though RAID 0 doesn't support rebuild)
            _rebuildCancellation?.Cancel();
            _rebuildCancellation?.Dispose();
            _rebuildCancellation = null;

            return Task.CompletedTask;
        }

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 0,
            MinDisks: 2,
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.Zero, // No rebuild possible
            ReadPerformanceMultiplier: 1.0, // Linear scaling per disk
            WritePerformanceMultiplier: 1.0, // Linear scaling per disk
            CapacityEfficiency: 1.0,
            SupportsHotSpare: false,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            ValidateDiskCount(diskCount);

            var stripeIndex = blockIndex / diskCount;
            var diskIndex = (int)(blockIndex % diskCount);

            var dataDisks = new int[diskCount];
            for (int i = 0; i < diskCount; i++)
            {
                dataDisks[i] = i;
            }

            return new StripeInfo(
                StripeIndex: stripeIndex,
                DataDisks: dataDisks,
                ParityDisks: Array.Empty<int>(),
                ChunkSize: _chunkSize,
                DataChunkCount: diskCount,
                ParityChunkCount: 0);
        }

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            if (data.Length == 0) return;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");

            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, _chunkSize);

            var writeTasks = new List<Task>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var diskIndex = i % diskList.Count;
                var chunk = chunks[i];
                var chunkOffset = offset + (i / diskList.Count) * _chunkSize;

                writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunk, chunkOffset, cancellationToken));
            }

            await Task.WhenAll(writeTasks);
            IncrementCounter("raid0.write");
            IncrementCounter("raid0.bytes_written");
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            if (length == 0) return ReadOnlyMemory<byte>.Empty;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative");

            var result = new byte[length];
            var chunksNeeded = (int)Math.Ceiling((double)length / _chunkSize);
            var readTasks = new Task<byte[]>[chunksNeeded];

            for (int i = 0; i < chunksNeeded; i++)
            {
                var diskIndex = i % diskList.Count;
                var chunkOffset = offset + (i / diskList.Count) * _chunkSize;
                var chunkLength = Math.Min(_chunkSize, length - i * _chunkSize);

                readTasks[i] = ReadFromDiskAsync(diskList[diskIndex], chunkOffset, chunkLength, cancellationToken);
            }

            var chunks = await Task.WhenAll(readTasks);
            var position = 0;
            foreach (var chunk in chunks)
            {
                var copyLength = Math.Min(chunk.Length, result.Length - position);
                Array.Copy(chunk, 0, result, position, copyLength);
                position += copyLength;
            }

            IncrementCounter("raid0.read");
            IncrementCounter("raid0.bytes_read");
            return result;
        }

        public override Task RebuildDiskAsync(
            DiskInfo failedDisk,
            IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk,
            IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("RAID 0 does not support disk rebuild. All data is lost when a disk fails.");
        }

        private void ValidateDiskCount(int diskCount)
        {
            if (diskCount < Capabilities.MinDisks)
                throw new ArgumentException($"RAID 0 requires at least {Capabilities.MinDisks} disks");
        }

        private List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < data.Length; i += chunkSize)
            {
                var length = Math.Min(chunkSize, data.Length - i);
                var chunk = new byte[length];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private async Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 65536,
                useAsync: true);

            fileStream.Seek(offset, SeekOrigin.Begin);
            await fileStream.WriteAsync(data, ct);
            await fileStream.FlushAsync(ct);
        }

        private async Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            if (!File.Exists(disk.Location))
                throw new FileNotFoundException($"Disk device not found: {disk.Location}", disk.Location);

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 65536,
                useAsync: true);

            if (offset + length > fileStream.Length)
                length = (int)Math.Max(0, fileStream.Length - offset);

            fileStream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            var totalRead = 0;

            while (totalRead < length)
            {
                var read = await fileStream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead < length)
            {
                var trimmed = new byte[totalRead];
                Array.Copy(buffer, trimmed, totalRead);
                return trimmed;
            }

            return buffer;
        }
    }

    /// <summary>
    /// Production-ready RAID 1 strategy - Mirroring.
    /// Writes identical data to all disks. Reads can be distributed across mirrors for performance.
    /// Survives failure of all but one disk.
    /// </summary>
    public sealed class Raid1Strategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private CancellationTokenSource? _rebuildCancellation;

        public Raid1Strategy(int chunkSize = 64 * 1024)
        {
            _chunkSize = chunkSize;
        }

        public override RaidLevel Level => RaidLevel.Raid1;

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            if (_chunkSize < 4096 || _chunkSize > 1024 * 1024)
                throw new ArgumentException($"Chunk size must be between 4KB and 1MB, got {_chunkSize}");

            if ((_chunkSize & (_chunkSize - 1)) != 0)
                throw new ArgumentException($"Chunk size must be a power of 2, got {_chunkSize}");

            return Task.CompletedTask;
        }

        public override async Task<RaidHealth> CheckHealthAsync(
            IEnumerable<DiskInfo> disks,
            CancellationToken cancellationToken = default)
        {
            IncrementCounter("raid1.health_check");
            return await base.CheckHealthAsync(disks, cancellationToken);
        }

        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            // Cancel any ongoing rebuild operations
            if (_rebuildCancellation != null)
            {
                _rebuildCancellation.Cancel();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException ex)
                {

                    // Graceful shutdown interrupted
                    System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
                }
                _rebuildCancellation.Dispose();
                _rebuildCancellation = null;
            }
        }

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1, // Can lose n-1 disks
            MinDisks: 2,
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(2),
            ReadPerformanceMultiplier: 1.0, // Can read from any mirror
            WritePerformanceMultiplier: 1.0 / 2.0, // Must write to all mirrors
            CapacityEfficiency: 0.5, // 50% efficiency with 2 disks
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            ValidateDiskConfiguration(new DiskInfo[diskCount].Select((_, i) =>
                new DiskInfo($"disk{i}", 0, 0, SdkDiskHealthStatus.Healthy, DiskType.HDD, $"bay{i}")));

            // All disks contain the same data
            var dataDisks = new int[] { 0 };
            var allDisks = Enumerable.Range(0, diskCount).ToArray();

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: Array.Empty<int>(),
                ChunkSize: _chunkSize,
                DataChunkCount: 1,
                ParityChunkCount: 0);
        }

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            if (data.Length == 0) return;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");

            var dataBytes = data.ToArray();

            // Write to all mirrors simultaneously
            var writeTasks = diskList
                .Where(d => d.HealthStatus == SdkDiskHealthStatus.Healthy)
                .Select(disk => WriteToDiskAsync(disk, dataBytes, offset, cancellationToken))
                .ToList();

            await Task.WhenAll(writeTasks);
            IncrementCounter("raid1.write");
            IncrementCounter("raid1.bytes_written");
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            if (length == 0) return ReadOnlyMemory<byte>.Empty;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative");

            // Read from the first healthy disk (could implement load balancing)
            var healthyDisk = diskList.FirstOrDefault(d => d.HealthStatus == SdkDiskHealthStatus.Healthy);
            if (healthyDisk == null)
                throw new InvalidOperationException("No healthy disks available for read operation");

            var data = await ReadFromDiskAsync(healthyDisk, offset, length, cancellationToken);
            IncrementCounter("raid1.read");
            IncrementCounter("raid1.bytes_read");
            return data;
        }

        public override async Task RebuildDiskAsync(
            DiskInfo failedDisk,
            IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk,
            IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var healthyDiskList = healthyDisks.ToList();
            if (healthyDiskList.Count == 0)
                throw new InvalidOperationException("No healthy disks available for rebuild");

            var sourceDisk = healthyDiskList.First();
            var totalBytes = sourceDisk.Capacity;
            var bytesRebuilt = 0L;

            var startTime = DateTime.UtcNow;

            _rebuildCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                // Copy all data from a healthy mirror to the target disk
                const int bufferSize = 1024 * 1024; // 1MB chunks
                for (long offset = 0; offset < totalBytes; offset += bufferSize)
                {
                    _rebuildCancellation.Token.ThrowIfCancellationRequested();

                    var length = (int)Math.Min(bufferSize, totalBytes - offset);
                    var data = await ReadFromDiskAsync(sourceDisk, offset, length, _rebuildCancellation.Token);
                    await WriteToDiskAsync(targetDisk, data, offset, _rebuildCancellation.Token);

                    bytesRebuilt += length;
                    IncrementCounter("raid1.rebuild_bytes");

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

                IncrementCounter("raid1.rebuild_complete");
            }
            catch (OperationCanceledException)
            {
                IncrementCounter("raid1.rebuild_cancelled");
                throw;
            }
            finally
            {
                _rebuildCancellation?.Dispose();
                _rebuildCancellation = null;
            }
        }

        private async Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 65536,
                useAsync: true);

            fileStream.Seek(offset, SeekOrigin.Begin);
            await fileStream.WriteAsync(data, ct);
            await fileStream.FlushAsync(ct);
        }

        private async Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            if (!File.Exists(disk.Location))
                throw new FileNotFoundException($"Disk device not found: {disk.Location}", disk.Location);

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 65536,
                useAsync: true);

            if (offset + length > fileStream.Length)
                length = (int)Math.Max(0, fileStream.Length - offset);

            fileStream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            var totalRead = 0;

            while (totalRead < length)
            {
                var read = await fileStream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead < length)
            {
                var trimmed = new byte[totalRead];
                Array.Copy(buffer, trimmed, totalRead);
                return trimmed;
            }

            return buffer;
        }
    }

    /// <summary>
    /// Production-ready RAID 5 strategy - Distributed parity.
    /// Uses XOR parity distributed across all disks. Can survive single disk failure.
    /// Parity disk rotates with each stripe for balanced write performance.
    /// </summary>
    public sealed class Raid5Strategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly ReedSolomon _reedSolomon;
        private CancellationTokenSource? _rebuildCancellation;

        public Raid5Strategy(int chunkSize = 64 * 1024)
        {
            _chunkSize = chunkSize;
            _reedSolomon = new ReedSolomon(2, 1); // Will be reconfigured per stripe
        }

        public override RaidLevel Level => RaidLevel.Raid5;

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            if (_chunkSize < 4096 || _chunkSize > 1024 * 1024)
                throw new ArgumentException($"Chunk size must be between 4KB and 1MB, got {_chunkSize}");

            if ((_chunkSize & (_chunkSize - 1)) != 0)
                throw new ArgumentException($"Chunk size must be a power of 2, got {_chunkSize}");

            return Task.CompletedTask;
        }

        public override async Task<RaidHealth> CheckHealthAsync(
            IEnumerable<DiskInfo> disks,
            CancellationToken cancellationToken = default)
        {
            IncrementCounter("raid5.health_check");
            return await base.CheckHealthAsync(disks, cancellationToken);
        }

        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            if (_rebuildCancellation != null)
            {
                _rebuildCancellation.Cancel();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException ex)
                {

                    // Graceful shutdown interrupted
                    System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
                }
                _rebuildCancellation.Dispose();
                _rebuildCancellation = null;
            }
        }

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 3,
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(4),
            ReadPerformanceMultiplier: 0.9,
            WritePerformanceMultiplier: 0.7, // Write penalty due to parity calculation
            CapacityEfficiency: 0.67, // (n-1)/n efficiency
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            ValidateDiskConfiguration(new DiskInfo[diskCount].Select((_, i) =>
                new DiskInfo($"disk{i}", 0, 0, SdkDiskHealthStatus.Healthy, DiskType.HDD, $"bay{i}")));

            var stripeIndex = blockIndex / (diskCount - 1);
            var parityDisk = (int)(stripeIndex % diskCount);

            var dataDisks = new List<int>();
            for (int i = 0; i < diskCount; i++)
            {
                if (i != parityDisk)
                    dataDisks.Add(i);
            }

            return new StripeInfo(
                StripeIndex: stripeIndex,
                DataDisks: dataDisks.ToArray(),
                ParityDisks: new[] { parityDisk },
                ChunkSize: _chunkSize,
                DataChunkCount: diskCount - 1,
                ParityChunkCount: 1);
        }

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            if (data.Length == 0) return;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");

            var dataBytes = data.ToArray();
            var diskCount = diskList.Count;
            var dataDisksCount = diskCount - 1;
            var chunks = SplitIntoChunks(dataBytes, _chunkSize, dataDisksCount);

            var writeTasks = new List<Task>();

            for (int stripeIdx = 0; stripeIdx < chunks.Count; stripeIdx++)
            {
                var stripeInfo = CalculateStripe(stripeIdx, diskCount);
                var stripeChunks = chunks[stripeIdx];

                // Calculate XOR parity
                var parity = CalculateXorParity(stripeChunks.Select(c => (ReadOnlyMemory<byte>)c));
                IncrementCounter("raid5.parity_compute");

                // Write data chunks
                for (int i = 0; i < stripeChunks.Count; i++)
                {
                    var diskIndex = stripeInfo.DataDisks[i];
                    var chunkOffset = offset + stripeIdx * _chunkSize;
                    writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], stripeChunks[i], chunkOffset, cancellationToken));
                }

                // Write parity chunk
                var parityDiskIndex = stripeInfo.ParityDisks[0];
                var parityOffset = offset + stripeIdx * _chunkSize;
                writeTasks.Add(WriteToDiskAsync(diskList[parityDiskIndex], parity.ToArray(), parityOffset, cancellationToken));
            }

            await Task.WhenAll(writeTasks);
            IncrementCounter("raid5.write");
            IncrementCounter("raid5.bytes_written");
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            if (length == 0) return ReadOnlyMemory<byte>.Empty;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative");

            var result = new byte[length];
            var diskCount = diskList.Count;
            var dataDisksCount = diskCount - 1;
            var stripesNeeded = (int)Math.Ceiling((double)length / (_chunkSize * dataDisksCount));

            var position = 0;
            var hadReconstruction = false;

            for (int stripeIdx = 0; stripeIdx < stripesNeeded && position < length; stripeIdx++)
            {
                var stripeInfo = CalculateStripe(stripeIdx, diskCount);
                var stripeOffset = offset + stripeIdx * _chunkSize;

                // Try to read from data disks
                var chunks = new List<byte[]>();
                var failedDiskIndex = -1;

                for (int i = 0; i < stripeInfo.DataDisks.Length; i++)
                {
                    var diskIndex = stripeInfo.DataDisks[i];
                    var disk = diskList[diskIndex];

                    if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                    {
                        try
                        {
                            var chunk = await ReadFromDiskAsync(disk, stripeOffset, _chunkSize, cancellationToken);
                            chunks.Add(chunk);
                        }
                        catch
                        {
                            failedDiskIndex = i;
                            chunks.Add(Array.Empty<byte>());
                        }
                    }
                    else
                    {
                        failedDiskIndex = i;
                        chunks.Add(Array.Empty<byte>());
                    }
                }

                // If a disk failed, reconstruct using parity
                if (failedDiskIndex >= 0)
                {
                    var parityDisk = diskList[stripeInfo.ParityDisks[0]];
                    var parity = await ReadFromDiskAsync(parityDisk, stripeOffset, _chunkSize, cancellationToken);
                    chunks[failedDiskIndex] = ReconstructFromParity(chunks, parity.ToArray(), failedDiskIndex);
                    hadReconstruction = true;
                    IncrementCounter("raid5.parity_reconstruct");
                }

                // Copy chunks to result
                foreach (var chunk in chunks.Where(c => c != null))
                {
                    var copyLength = Math.Min(chunk.Length, result.Length - position);
                    Array.Copy(chunk, 0, result, position, copyLength);
                    position += copyLength;
                }
            }

            IncrementCounter("raid5.read");
            IncrementCounter("raid5.bytes_read");
            if (hadReconstruction)
                IncrementCounter("raid5.degraded_read");

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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stripeInfo = CalculateStripe(offset / _chunkSize, allDisks.Count);

                // Read data from all healthy disks in this stripe
                var chunks = new List<byte[]>();
                for (int i = 0; i < stripeInfo.DataDisks.Length; i++)
                {
                    if (stripeInfo.DataDisks[i] == failedDiskIndex)
                    {
                        chunks.Add(Array.Empty<byte>());
                    }
                    else
                    {
                        var disk = allDisks[stripeInfo.DataDisks[i]];
                        var chunk = await ReadFromDiskAsync(disk, offset, _chunkSize, cancellationToken);
                        chunks.Add(chunk);
                    }
                }

                // Read parity
                var parityDisk = allDisks[stripeInfo.ParityDisks[0]];
                var parity = await ReadFromDiskAsync(parityDisk, offset, _chunkSize, cancellationToken);

                // Reconstruct failed chunk
                var reconstructedChunk = ReconstructFromParity(chunks, parity, failedDiskIndex);

                // Write to target disk
                await WriteToDiskAsync(targetDisk, reconstructedChunk, offset, cancellationToken);

                bytesRebuilt += reconstructedChunk.Length;

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
                    var remaining = (long)((totalBytes - bytesRebuilt) / speed);

                    progressCallback.Report(new RebuildProgress(
                        PercentComplete: (double)bytesRebuilt / totalBytes,
                        BytesRebuilt: bytesRebuilt,
                        TotalBytes: totalBytes,
                        EstimatedTimeRemaining: TimeSpan.FromSeconds(remaining),
                        CurrentSpeed: (long)speed));
                }
            }
        }

        private List<List<byte[]>> SplitIntoChunks(byte[] data, int chunkSize, int chunksPerStripe)
        {
            var stripes = new List<List<byte[]>>();
            var position = 0;

            while (position < data.Length)
            {
                var stripe = new List<byte[]>();
                for (int i = 0; i < chunksPerStripe && position < data.Length; i++)
                {
                    var length = Math.Min(chunkSize, data.Length - position);
                    var chunk = new byte[chunkSize]; // Pad to chunk size
                    Array.Copy(data, position, chunk, 0, length);
                    stripe.Add(chunk);
                    position += length;
                }
                stripes.Add(stripe);
            }

            return stripes;
        }

        private byte[] ReconstructFromParity(List<byte[]> chunks, byte[] parity, int missingIndex)
        {
            var result = new byte[_chunkSize];
            Array.Copy(parity, result, _chunkSize);

            for (int i = 0; i < chunks.Count; i++)
            {
                if (i != missingIndex && chunks[i] != null)
                {
                    for (int j = 0; j < _chunkSize; j++)
                    {
                        result[j] ^= chunks[i][j];
                    }
                }
            }

            return result;
        }

        private async Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 65536,
                useAsync: true);

            fileStream.Seek(offset, SeekOrigin.Begin);
            await fileStream.WriteAsync(data, ct);
            await fileStream.FlushAsync(ct);
        }

        private async Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            if (!File.Exists(disk.Location))
                throw new FileNotFoundException($"Disk device not found: {disk.Location}", disk.Location);

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 65536,
                useAsync: true);

            if (offset + length > fileStream.Length)
                length = (int)Math.Max(0, fileStream.Length - offset);

            fileStream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            var totalRead = 0;

            while (totalRead < length)
            {
                var read = await fileStream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead < length)
            {
                var trimmed = new byte[totalRead];
                Array.Copy(buffer, trimmed, totalRead);
                return trimmed;
            }

            return buffer;
        }
    }

    /// <summary>
    /// Production-ready RAID 6 strategy - Double distributed parity.
    /// Uses P+Q parity with Reed-Solomon encoding in GF(2^8). Can survive two disk failures.
    /// P parity is XOR, Q parity uses Galois field multiplication.
    /// </summary>
    public sealed class Raid6Strategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly ReedSolomon _reedSolomon;
        private CancellationTokenSource? _rebuildCancellation;

        public Raid6Strategy(int chunkSize = 64 * 1024)
        {
            _chunkSize = chunkSize;
            _reedSolomon = new ReedSolomon(2, 2); // Will be reconfigured per stripe
        }

        public override RaidLevel Level => RaidLevel.Raid6;

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            if (_chunkSize < 4096 || _chunkSize > 1024 * 1024)
                throw new ArgumentException($"Chunk size must be between 4KB and 1MB, got {_chunkSize}");

            if ((_chunkSize & (_chunkSize - 1)) != 0)
                throw new ArgumentException($"Chunk size must be a power of 2, got {_chunkSize}");

            return Task.CompletedTask;
        }

        public override async Task<RaidHealth> CheckHealthAsync(
            IEnumerable<DiskInfo> disks,
            CancellationToken cancellationToken = default)
        {
            IncrementCounter("raid6.health_check");
            return await base.CheckHealthAsync(disks, cancellationToken);
        }

        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            if (_rebuildCancellation != null)
            {
                _rebuildCancellation.Cancel();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException ex)
                {

                    // Graceful shutdown interrupted
                    System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
                }
                _rebuildCancellation.Dispose();
                _rebuildCancellation = null;
            }
        }

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 2,
            MinDisks: 4,
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(6),
            ReadPerformanceMultiplier: 0.85,
            WritePerformanceMultiplier: 0.6, // Higher write penalty due to dual parity
            CapacityEfficiency: 0.5, // (n-2)/n efficiency
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            ValidateDiskConfiguration(new DiskInfo[diskCount].Select((_, i) =>
                new DiskInfo($"disk{i}", 0, 0, SdkDiskHealthStatus.Healthy, DiskType.HDD, $"bay{i}")));

            var stripeIndex = blockIndex / (diskCount - 2);
            var pParityDisk = (int)(stripeIndex % diskCount);
            var qParityDisk = (int)((stripeIndex + 1) % diskCount);

            var dataDisks = new List<int>();
            for (int i = 0; i < diskCount; i++)
            {
                if (i != pParityDisk && i != qParityDisk)
                    dataDisks.Add(i);
            }

            return new StripeInfo(
                StripeIndex: stripeIndex,
                DataDisks: dataDisks.ToArray(),
                ParityDisks: new[] { pParityDisk, qParityDisk },
                ChunkSize: _chunkSize,
                DataChunkCount: diskCount - 2,
                ParityChunkCount: 2);
        }

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            if (data.Length == 0) return;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");

            var dataBytes = data.ToArray();
            var diskCount = diskList.Count;
            var dataDisksCount = diskCount - 2;

            // Create shards for Reed-Solomon encoding
            var totalShards = dataDisksCount + 2;
            var rs = new ReedSolomon(dataDisksCount, 2);

            var shards = new byte[totalShards][];
            var position = 0;

            // Fill data shards
            for (int i = 0; i < dataDisksCount; i++)
            {
                shards[i] = new byte[_chunkSize];
                var length = Math.Min(_chunkSize, dataBytes.Length - position);
                if (length > 0)
                {
                    Array.Copy(dataBytes, position, shards[i], 0, length);
                    position += length;
                }
            }

            // Initialize parity shards (Encode will fill them)
            for (int i = dataDisksCount; i < totalShards; i++)
            {
                shards[i] = new byte[_chunkSize];
            }

            // Encode to generate parity shards
            rs.Encode(shards.AsSpan());
            IncrementCounter("raid6.parity_compute");

            // Write all shards to disks
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskCount);
            var writeTasks = new List<Task>();

            // Write data shards
            for (int i = 0; i < dataDisksCount; i++)
            {
                var diskIndex = stripeInfo.DataDisks[i];
                writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], shards[i], offset, cancellationToken));
            }

            // Write P parity
            var pParityDisk = stripeInfo.ParityDisks[0];
            writeTasks.Add(WriteToDiskAsync(diskList[pParityDisk], shards[dataDisksCount], offset, cancellationToken));

            // Write Q parity
            var qParityDisk = stripeInfo.ParityDisks[1];
            writeTasks.Add(WriteToDiskAsync(diskList[qParityDisk], shards[dataDisksCount + 1], offset, cancellationToken));

            await Task.WhenAll(writeTasks);
            IncrementCounter("raid6.write");
            IncrementCounter("raid6.bytes_written");
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            if (length == 0) return ReadOnlyMemory<byte>.Empty;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative");

            var diskCount = diskList.Count;
            var dataDisksCount = diskCount - 2;
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskCount);

            var shards = new byte[diskCount][];
            var shardPresent = new bool[diskCount];
            var failedCount = 0;

            // Initialize all shards with empty arrays
            for (int i = 0; i < diskCount; i++)
            {
                shards[i] = Array.Empty<byte>();
            }

            // Read data disks
            for (int i = 0; i < dataDisksCount; i++)
            {
                var diskIndex = stripeInfo.DataDisks[i];
                var disk = diskList[diskIndex];

                if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                {
                    try
                    {
                        shards[i] = await ReadFromDiskAsync(disk, offset, _chunkSize, cancellationToken);
                        shardPresent[i] = true;
                    }
                    catch
                    {
                        shardPresent[i] = false;
                        failedCount++;
                    }
                }
                else
                {
                    shardPresent[i] = false;
                    failedCount++;
                }
            }

            // Read parity disks if needed
            if (failedCount > 0)
            {
                var pParityDisk = diskList[stripeInfo.ParityDisks[0]];
                shards[dataDisksCount] = await ReadFromDiskAsync(pParityDisk, offset, _chunkSize, cancellationToken);
                shardPresent[dataDisksCount] = true;

                if (failedCount > 1)
                {
                    var qParityDisk = diskList[stripeInfo.ParityDisks[1]];
                    shards[dataDisksCount + 1] = await ReadFromDiskAsync(qParityDisk, offset, _chunkSize, cancellationToken);
                    shardPresent[dataDisksCount + 1] = true;
                }
            }

            // Reconstruct if needed
            if (failedCount > 0)
            {
                var rs = new ReedSolomon(dataDisksCount, 2);
                rs.Decode(shards.AsSpan(), shardPresent.AsSpan());
                IncrementCounter("raid6.parity_reconstruct");
                if (failedCount > 1)
                    IncrementCounter("raid6.dual_failure_recover");
            }

            // Assemble result
            var result = new byte[length];
            var position = 0;
            for (int i = 0; i < dataDisksCount && position < length; i++)
            {
                var copyLength = Math.Min(shards[i].Length, result.Length - position);
                Array.Copy(shards[i], 0, result, position, copyLength);
                position += copyLength;
            }

            IncrementCounter("raid6.read");
            IncrementCounter("raid6.bytes_read");
            if (failedCount > 0)
                IncrementCounter("raid6.degraded_read");

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
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            var diskCount = allDisks.Count;
            var dataDisksCount = diskCount - 2;
            var rs = new ReedSolomon(dataDisksCount, 2);

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stripeInfo = CalculateStripe(offset / _chunkSize, diskCount);
                var shards = new byte[diskCount][];
                var shardPresent = new bool[diskCount];

                // Read all available shards
                for (int i = 0; i < diskCount; i++)
                {
                    if (i == failedDiskIndex)
                    {
                        shards[i] = Array.Empty<byte>();
                        shardPresent[i] = false;
                    }
                    else
                    {
                        var disk = allDisks[i];
                        shards[i] = await ReadFromDiskAsync(disk, offset, _chunkSize, cancellationToken);
                        shardPresent[i] = true;
                    }
                }

                // Reconstruct failed shard
                rs.Decode(shards.AsSpan(), shardPresent.AsSpan());

                // Write to target disk
                await WriteToDiskAsync(targetDisk, shards[failedDiskIndex], offset, cancellationToken);

                bytesRebuilt += shards[failedDiskIndex].Length;

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
                    var remaining = (long)((totalBytes - bytesRebuilt) / speed);

                    progressCallback.Report(new RebuildProgress(
                        PercentComplete: (double)bytesRebuilt / totalBytes,
                        BytesRebuilt: bytesRebuilt,
                        TotalBytes: totalBytes,
                        EstimatedTimeRemaining: TimeSpan.FromSeconds(remaining),
                        CurrentSpeed: (long)speed));
                }
            }
        }

        private async Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 65536,
                useAsync: true);

            fileStream.Seek(offset, SeekOrigin.Begin);
            await fileStream.WriteAsync(data, ct);
            await fileStream.FlushAsync(ct);
        }

        private async Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            if (!File.Exists(disk.Location))
                throw new FileNotFoundException($"Disk device not found: {disk.Location}", disk.Location);

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 65536,
                useAsync: true);

            if (offset + length > fileStream.Length)
                length = (int)Math.Max(0, fileStream.Length - offset);

            fileStream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            var totalRead = 0;

            while (totalRead < length)
            {
                var read = await fileStream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead < length)
            {
                var trimmed = new byte[totalRead];
                Array.Copy(buffer, trimmed, totalRead);
                return trimmed;
            }

            return buffer;
        }
    }

    /// <summary>
    /// Production-ready RAID 10 strategy - Striped mirrors (RAID 1+0).
    /// Combines mirroring and striping. Data is mirrored in pairs, then striped across pairs.
    /// Provides both performance and redundancy. Can survive multiple disk failures
    /// as long as no mirror pair loses both disks.
    /// </summary>
    public sealed class Raid10Strategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private CancellationTokenSource? _rebuildCancellation;

        public Raid10Strategy(int chunkSize = 64 * 1024)
        {
            _chunkSize = chunkSize;
        }

        public override RaidLevel Level => RaidLevel.Raid10;

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            if (_chunkSize < 4096 || _chunkSize > 1024 * 1024)
                throw new ArgumentException($"Chunk size must be between 4KB and 1MB, got {_chunkSize}");

            if ((_chunkSize & (_chunkSize - 1)) != 0)
                throw new ArgumentException($"Chunk size must be a power of 2, got {_chunkSize}");

            return Task.CompletedTask;
        }

        public override async Task<RaidHealth> CheckHealthAsync(
            IEnumerable<DiskInfo> disks,
            CancellationToken cancellationToken = default)
        {
            IncrementCounter("raid10.health_check");
            return await base.CheckHealthAsync(disks, cancellationToken);
        }

        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            if (_rebuildCancellation != null)
            {
                _rebuildCancellation.Cancel();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException ex)
                {

                    // Graceful shutdown interrupted
                    System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
                }
                _rebuildCancellation.Dispose();
                _rebuildCancellation = null;
            }
        }

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1, // Per mirror pair
            MinDisks: 4,
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(2),
            ReadPerformanceMultiplier: 1.5, // Read from multiple mirrors
            WritePerformanceMultiplier: 0.9, // Good write performance
            CapacityEfficiency: 0.5,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            if (diskCount % 2 != 0)
                throw new ArgumentException("RAID 10 requires an even number of disks");

            ValidateDiskConfiguration(new DiskInfo[diskCount].Select((_, i) =>
                new DiskInfo($"disk{i}", 0, 0, SdkDiskHealthStatus.Healthy, DiskType.HDD, $"bay{i}")));

            var mirrorPairs = diskCount / 2;
            var stripeIndex = blockIndex / mirrorPairs;

            // Data disks are the primary disk in each mirror pair
            var dataDisks = new int[mirrorPairs];
            for (int i = 0; i < mirrorPairs; i++)
            {
                dataDisks[i] = i * 2; // Primary disk in pair
            }

            return new StripeInfo(
                StripeIndex: stripeIndex,
                DataDisks: dataDisks,
                ParityDisks: Array.Empty<int>(),
                ChunkSize: _chunkSize,
                DataChunkCount: mirrorPairs,
                ParityChunkCount: 0);
        }

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            if (data.Length == 0) return;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (diskList.Count % 2 != 0)
                throw new ArgumentException("RAID 10 requires an even number of disks");

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");

            var dataBytes = data.ToArray();
            var mirrorPairs = diskList.Count / 2;
            var chunks = SplitIntoChunks(dataBytes, _chunkSize);

            var writeTasks = new List<Task>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var pairIndex = i % mirrorPairs;
                var primaryDisk = pairIndex * 2;
                var secondaryDisk = pairIndex * 2 + 1;
                var chunkOffset = offset + (i / mirrorPairs) * _chunkSize;

                // Write to both disks in the mirror pair
                writeTasks.Add(WriteToDiskAsync(diskList[primaryDisk], chunks[i], chunkOffset, cancellationToken));
                writeTasks.Add(WriteToDiskAsync(diskList[secondaryDisk], chunks[i], chunkOffset, cancellationToken));
            }

            await Task.WhenAll(writeTasks);
            IncrementCounter("raid10.write");
            IncrementCounter("raid10.bytes_written");
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(
            IEnumerable<DiskInfo> disks,
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            if (length == 0) return ReadOnlyMemory<byte>.Empty;

            var diskList = disks.ToList();
            ValidateDiskConfiguration(diskList);

            if (diskList.Count % 2 != 0)
                throw new ArgumentException("RAID 10 requires an even number of disks");

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative");

            var result = new byte[length];
            var mirrorPairs = diskList.Count / 2;
            var chunksNeeded = (int)Math.Ceiling((double)length / _chunkSize);
            var position = 0;
            var hadFailover = false;

            for (int i = 0; i < chunksNeeded && position < length; i++)
            {
                var pairIndex = i % mirrorPairs;
                var primaryDisk = diskList[pairIndex * 2];
                var secondaryDisk = diskList[pairIndex * 2 + 1];
                var chunkOffset = offset + (i / mirrorPairs) * _chunkSize;
                var chunkLength = Math.Min(_chunkSize, length - position);

                byte[] chunk;

                // Try primary disk first
                if (primaryDisk.HealthStatus == SdkDiskHealthStatus.Healthy)
                {
                    try
                    {
                        chunk = await ReadFromDiskAsync(primaryDisk, chunkOffset, chunkLength, cancellationToken);
                    }
                    catch
                    {
                        // Fall back to secondary
                        chunk = await ReadFromDiskAsync(secondaryDisk, chunkOffset, chunkLength, cancellationToken);
                        hadFailover = true;
                        IncrementCounter("raid10.failover_read");
                    }
                }
                else
                {
                    // Primary failed, use secondary
                    chunk = await ReadFromDiskAsync(secondaryDisk, chunkOffset, chunkLength, cancellationToken);
                    hadFailover = true;
                }

                var copyLength = Math.Min(chunk.Length, result.Length - position);
                Array.Copy(chunk, 0, result, position, copyLength);
                position += copyLength;
            }

            IncrementCounter("raid10.read");
            IncrementCounter("raid10.bytes_read");
            if (hadFailover)
                IncrementCounter("raid10.degraded_read");

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

            // Find the mirror pair
            var pairIndex = failedDiskIndex / 2;
            var mirrorDiskIndex = (failedDiskIndex % 2 == 0) ? pairIndex * 2 + 1 : pairIndex * 2;
            var mirrorDisk = allDisks[mirrorDiskIndex];

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
                    var remaining = (long)((totalBytes - bytesRebuilt) / speed);

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
                var chunk = new byte[length];
                Array.Copy(data, i, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }

        private async Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 65536,
                useAsync: true);

            fileStream.Seek(offset, SeekOrigin.Begin);
            await fileStream.WriteAsync(data, ct);
            await fileStream.FlushAsync(ct);
        }

        private async Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(disk.Location))
                throw new InvalidOperationException($"Disk {disk.DiskId} has no device path configured");

            if (!File.Exists(disk.Location))
                throw new FileNotFoundException($"Disk device not found: {disk.Location}", disk.Location);

            using var fileStream = new FileStream(
                disk.Location,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 65536,
                useAsync: true);

            if (offset + length > fileStream.Length)
                length = (int)Math.Max(0, fileStream.Length - offset);

            fileStream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            var totalRead = 0;

            while (totalRead < length)
            {
                var read = await fileStream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead < length)
            {
                var trimmed = new byte[totalRead];
                Array.Copy(buffer, trimmed, totalRead);
                return trimmed;
            }

            return buffer;
        }
    }
}
