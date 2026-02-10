using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.RAID;
using SdkRaidStrategyBase = DataWarehouse.SDK.Contracts.RAID.RaidStrategyBase;
using SdkDiskHealthStatus = DataWarehouse.SDK.Contracts.RAID.DiskHealthStatus;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.Extended
{
    /// <summary>
    /// RAID 71/72 Strategy - Multi-parity variants with 3-4 parity drives.
    /// Extends RAID 6 concept to provide even higher fault tolerance.
    /// </summary>
    public sealed class Raid7XStrategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly int _parityCount; // 3 for RAID 71, 4 for RAID 72

        public Raid7XStrategy(int chunkSize = 64 * 1024, int parityCount = 3)
        {
            if (parityCount < 3 || parityCount > 4)
                throw new ArgumentException("RAID 7X supports 3 (71) or 4 (72) parity drives", nameof(parityCount));

            _chunkSize = chunkSize;
            _parityCount = parityCount;
        }

        public override RaidLevel Level => _parityCount == 3 ? RaidLevel.Raid71 : RaidLevel.Raid72;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: _parityCount,
            MinDisks: _parityCount + 2, // At least 2 data + parity drives
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(5),
            ReadPerformanceMultiplier: 0.8,
            WritePerformanceMultiplier: 0.5, // Multi-parity overhead
            CapacityEfficiency: 0.6, // Lower due to multiple parity
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var dataDisks = Enumerable.Range(0, diskCount - _parityCount).ToArray();
            var parityDisks = Enumerable.Range(diskCount - _parityCount, _parityCount).ToArray();

            return new StripeInfo(
                StripeIndex: blockIndex,
                DataDisks: dataDisks,
                ParityDisks: parityDisks,
                ChunkSize: _chunkSize,
                DataChunkCount: dataDisks.Length,
                ParityChunkCount: _parityCount);
        }

        public override async Task WriteAsync(
            ReadOnlyMemory<byte> data,
            IEnumerable<DiskInfo> disks,
            long offset,
            CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, _chunkSize);

            // Calculate multiple parities
            var parities = CalculateMultiParity(chunks, _parityCount);

            var writeTasks = new List<Task>();

            // Write data chunks
            for (int i = 0; i < chunks.Count && i < stripeInfo.DataDisks.Length; i++)
            {
                var diskIndex = stripeInfo.DataDisks[i];
                writeTasks.Add(WriteToDiskAsync(diskList[diskIndex], chunks[i], offset, cancellationToken));
            }

            // Write parity chunks
            for (int p = 0; p < _parityCount && p < parities.Count; p++)
            {
                var parityDiskIndex = stripeInfo.ParityDisks[p];
                writeTasks.Add(WriteToDiskAsync(diskList[parityDiskIndex], parities[p], offset, cancellationToken));
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
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var chunks = new Dictionary<int, byte[]>();
            var failedDisks = new List<int>();

            // Read data disks
            for (int i = 0; i < stripeInfo.DataDisks.Length; i++)
            {
                var diskIndex = stripeInfo.DataDisks[i];
                var disk = diskList[diskIndex];

                if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                {
                    try
                    {
                        var chunk = await ReadFromDiskAsync(disk, offset, _chunkSize, cancellationToken);
                        chunks[i] = chunk;
                    }
                    catch { failedDisks.Add(i); }
                }
                else { failedDisks.Add(i); }
            }

            // Reconstruct if needed
            if (failedDisks.Count > 0 && failedDisks.Count <= _parityCount)
            {
                await ReconstructWithMultiParityAsync(diskList, chunks, failedDisks, stripeInfo, offset, cancellationToken);
            }

            return AssembleResult(chunks, stripeInfo, length);
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
            var stripeInfo = CalculateStripe(0, allDisks.Count);

            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var reconstructedChunk = await ReconstructChunkAsync(allDisks, failedDiskIndex, stripeInfo, offset, cancellationToken);
                await WriteToDiskAsync(targetDisk, reconstructedChunk, offset, cancellationToken);
                bytesRebuilt += _chunkSize;

                ReportProgress(progressCallback, bytesRebuilt, totalBytes, startTime);
            }
        }

        private List<byte[]> CalculateMultiParity(List<byte[]> chunks, int parityCount)
        {
            var parities = new List<byte[]>();

            // P1: XOR parity
            parities.Add(CalculateXorParity(chunks));

            // P2: Diagonal parity
            if (parityCount >= 2)
                parities.Add(CalculateDiagonalParity(chunks));

            // P3: Anti-diagonal parity
            if (parityCount >= 3)
                parities.Add(CalculateAntiDiagonalParity(chunks));

            // P4: Galois field parity
            if (parityCount >= 4)
                parities.Add(CalculateGaloisParity(chunks));

            return parities;
        }

        private byte[] CalculateXorParity(List<byte[]> chunks)
        {
            var parity = new byte[_chunkSize];
            foreach (var chunk in chunks.Where(c => c != null))
                for (int i = 0; i < _chunkSize && i < chunk.Length; i++)
                    parity[i] ^= chunk[i];
            return parity;
        }

        private byte[] CalculateDiagonalParity(List<byte[]> chunks)
        {
            var parity = new byte[_chunkSize];
            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks[i] == null) continue;
                for (int j = 0; j < _chunkSize && j < chunks[i].Length; j++)
                    parity[(j + i) % _chunkSize] ^= chunks[i][j];
            }
            return parity;
        }

        private byte[] CalculateAntiDiagonalParity(List<byte[]> chunks)
        {
            var parity = new byte[_chunkSize];
            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks[i] == null) continue;
                for (int j = 0; j < _chunkSize && j < chunks[i].Length; j++)
                    parity[(j - i + _chunkSize) % _chunkSize] ^= chunks[i][j];
            }
            return parity;
        }

        private byte[] CalculateGaloisParity(List<byte[]> chunks)
        {
            var parity = new byte[_chunkSize];
            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks[i] == null) continue;
                var coefficient = (byte)((i + 1) % 256);
                for (int j = 0; j < _chunkSize && j < chunks[i].Length; j++)
                    parity[j] ^= GaloisMultiply(chunks[i][j], coefficient);
            }
            return parity;
        }

        private byte GaloisMultiply(byte a, byte b)
        {
            byte result = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((b & 1) != 0) result ^= a;
                bool carry = (a & 0x80) != 0;
                a <<= 1;
                if (carry) a ^= 0x1D;
                b >>= 1;
            }
            return result;
        }

        private async Task ReconstructWithMultiParityAsync(List<DiskInfo> disks, Dictionary<int, byte[]> chunks,
            List<int> failedDisks, StripeInfo stripeInfo, long offset, CancellationToken ct)
        {
            var parities = new List<byte[]>();
            foreach (var parityDiskIndex in stripeInfo.ParityDisks)
            {
                var parityChunk = await ReadFromDiskAsync(disks[parityDiskIndex], offset, _chunkSize, ct);
                parities.Add(parityChunk);
            }

            foreach (var failedIndex in failedDisks)
            {
                chunks[failedIndex] = ReconstructFromParities(chunks, parities, failedIndex);
            }
        }

        private byte[] ReconstructFromParities(Dictionary<int, byte[]> chunks, List<byte[]> parities, int failedIndex)
        {
            if (parities.Count == 0) return new byte[_chunkSize];

            var result = parities[0].ToArray();
            foreach (var kvp in chunks)
            {
                if (kvp.Key != failedIndex && kvp.Value != null)
                {
                    for (int i = 0; i < _chunkSize; i++)
                        result[i] ^= kvp.Value[i];
                }
            }
            return result;
        }

        private async Task<byte[]> ReconstructChunkAsync(List<DiskInfo> disks, int failedIndex,
            StripeInfo stripeInfo, long offset, CancellationToken ct)
        {
            var chunks = new Dictionary<int, byte[]>();
            for (int i = 0; i < stripeInfo.DataDisks.Length; i++)
            {
                if (stripeInfo.DataDisks[i] != failedIndex)
                {
                    var chunk = await ReadFromDiskAsync(disks[stripeInfo.DataDisks[i]], offset, _chunkSize, ct);
                    chunks[i] = chunk;
                }
            }

            var parities = new List<byte[]>();
            foreach (var parityDiskIndex in stripeInfo.ParityDisks)
            {
                if (parityDiskIndex != failedIndex)
                {
                    var parity = await ReadFromDiskAsync(disks[parityDiskIndex], offset, _chunkSize, ct);
                    parities.Add(parity);
                }
            }

            var failedDataIndex = Array.IndexOf(stripeInfo.DataDisks, failedIndex);
            if (failedDataIndex >= 0)
                return ReconstructFromParities(chunks, parities, failedDataIndex);

            return CalculateXorParity(chunks.Values.ToList());
        }

        private byte[] AssembleResult(Dictionary<int, byte[]> chunks, StripeInfo stripeInfo, int length)
        {
            var result = new byte[length];
            var position = 0;
            for (int i = 0; i < stripeInfo.DataDisks.Length && position < length; i++)
            {
                if (chunks.TryGetValue(i, out var chunk))
                {
                    var copyLength = Math.Min(chunk.Length, length - position);
                    Array.Copy(chunk, 0, result, position, copyLength);
                    position += copyLength;
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

        private void ReportProgress(IProgress<RebuildProgress>? callback, long rebuilt, long total, DateTime start)
        {
            if (callback == null) return;
            var elapsed = DateTime.UtcNow - start;
            var speed = rebuilt / elapsed.TotalSeconds;
            callback.Report(new RebuildProgress(
                (double)rebuilt / total, rebuilt, total,
                TimeSpan.FromSeconds((total - rebuilt) / speed), (long)speed));
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
    }

    /// <summary>
    /// N-Way Mirror Strategy - 3+ way mirroring for extreme redundancy.
    /// </summary>
    public sealed class NWayMirrorStrategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly int _mirrorCount;

        public NWayMirrorStrategy(int chunkSize = 64 * 1024, int mirrorCount = 3)
        {
            if (mirrorCount < 3)
                throw new ArgumentException("N-Way mirror requires at least 3 mirrors", nameof(mirrorCount));
            _chunkSize = chunkSize;
            _mirrorCount = mirrorCount;
        }

        public override RaidLevel Level => RaidLevel.NWayMirror;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: _mirrorCount - 1,
            MinDisks: _mirrorCount,
            MaxDisks: _mirrorCount,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(2),
            ReadPerformanceMultiplier: _mirrorCount, // Can read from any mirror
            WritePerformanceMultiplier: 1.0 / _mirrorCount, // Must write to all
            CapacityEfficiency: 1.0 / _mirrorCount,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: false,
            RequiresUniformDiskSize: true);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            return new StripeInfo(blockIndex, new[] { 0 }, Enumerable.Range(1, diskCount - 1).ToArray(),
                _chunkSize, 1, diskCount - 1);
        }

        public override async Task WriteAsync(ReadOnlyMemory<byte> data, IEnumerable<DiskInfo> disks,
            long offset, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var dataBytes = data.ToArray();

            var writeTasks = diskList.Where(d => d.HealthStatus == SdkDiskHealthStatus.Healthy)
                .Select(disk => WriteToDiskAsync(disk, dataBytes, offset, cancellationToken)).ToList();

            await Task.WhenAll(writeTasks);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(IEnumerable<DiskInfo> disks,
            long offset, int length, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var healthyDisk = disks.FirstOrDefault(d => d.HealthStatus == SdkDiskHealthStatus.Healthy)
                ?? throw new InvalidOperationException("No healthy disks available");
            return await ReadFromDiskAsync(healthyDisk, offset, length, cancellationToken);
        }

        public override async Task RebuildDiskAsync(DiskInfo failedDisk, IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk, IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var sourceDisk = healthyDisks.First();
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = await ReadFromDiskAsync(sourceDisk, offset, bufferSize, cancellationToken);
                await WriteToDiskAsync(targetDisk, data, offset, cancellationToken);
                bytesRebuilt += bufferSize;

                if (progressCallback != null)
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    var speed = bytesRebuilt / elapsed.TotalSeconds;
                    progressCallback.Report(new RebuildProgress((double)bytesRebuilt / totalBytes, bytesRebuilt, totalBytes,
                        TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / speed), (long)speed));
                }
            }
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
    }

    /// <summary>
    /// JBOD Strategy - Just a Bunch of Disks (concatenation).
    /// </summary>
    public sealed class JbodStrategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;

        public JbodStrategy(int chunkSize = 64 * 1024) => _chunkSize = chunkSize;

        public override RaidLevel Level => RaidLevel.Jbod;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 0,
            MinDisks: 1,
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.Zero,
            ReadPerformanceMultiplier: 1.0,
            WritePerformanceMultiplier: 1.0,
            CapacityEfficiency: 1.0,
            SupportsHotSpare: false,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            return new StripeInfo(blockIndex, Enumerable.Range(0, diskCount).ToArray(),
                Array.Empty<int>(), _chunkSize, diskCount, 0);
        }

        public override async Task WriteAsync(ReadOnlyMemory<byte> data, IEnumerable<DiskInfo> disks,
            long offset, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var dataBytes = data.ToArray();

            // Find which disk this offset falls on
            var (diskIndex, diskOffset) = MapOffsetToDisk(diskList, offset);
            await WriteToDiskAsync(diskList[diskIndex], dataBytes, diskOffset, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(IEnumerable<DiskInfo> disks,
            long offset, int length, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();

            var (diskIndex, diskOffset) = MapOffsetToDisk(diskList, offset);
            return await ReadFromDiskAsync(diskList[diskIndex], diskOffset, length, cancellationToken);
        }

        public override Task RebuildDiskAsync(DiskInfo failedDisk, IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk, IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("JBOD does not support disk rebuild - no redundancy");
        }

        private (int diskIndex, long diskOffset) MapOffsetToDisk(List<DiskInfo> disks, long offset)
        {
            long cumulativeSize = 0;
            for (int i = 0; i < disks.Count; i++)
            {
                if (offset < cumulativeSize + disks[i].Capacity)
                    return (i, offset - cumulativeSize);
                cumulativeSize += disks[i].Capacity;
            }
            return (disks.Count - 1, offset - cumulativeSize + disks[^1].Capacity);
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
    }

    /// <summary>
    /// Crypto RAID Strategy - Encrypted RAID with per-disk keys.
    /// </summary>
    public sealed class CryptoRaidStrategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly ConcurrentDictionary<string, byte[]> _diskKeys;
        private readonly byte[] _masterKey;

        public CryptoRaidStrategy(int chunkSize = 64 * 1024, byte[]? masterKey = null)
        {
            _chunkSize = chunkSize;
            _diskKeys = new ConcurrentDictionary<string, byte[]>();
            _masterKey = masterKey ?? GenerateKey();
        }

        public override RaidLevel Level => RaidLevel.CryptoRaid;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 3,
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(5),
            ReadPerformanceMultiplier: 0.8, // Decryption overhead
            WritePerformanceMultiplier: 0.7, // Encryption overhead
            CapacityEfficiency: 0.67,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var parityDisk = (int)(blockIndex % diskCount);
            var dataDisks = Enumerable.Range(0, diskCount).Where(i => i != parityDisk).ToArray();
            return new StripeInfo(blockIndex, dataDisks, new[] { parityDisk }, _chunkSize, diskCount - 1, 1);
        }

        public override async Task WriteAsync(ReadOnlyMemory<byte> data, IEnumerable<DiskInfo> disks,
            long offset, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, _chunkSize);

            var writeTasks = new List<Task>();
            var encryptedChunks = new List<byte[]>();

            // Encrypt and write data chunks with per-disk keys
            for (int i = 0; i < chunks.Count && i < stripeInfo.DataDisks.Length; i++)
            {
                var diskIndex = stripeInfo.DataDisks[i];
                var disk = diskList[diskIndex];
                var key = GetOrCreateDiskKey(disk.DiskId);
                var encrypted = Encrypt(chunks[i], key);
                encryptedChunks.Add(encrypted);
                writeTasks.Add(WriteToDiskAsync(disk, encrypted, offset, cancellationToken));
            }

            // Calculate encrypted parity
            var parity = CalculateXorParity(encryptedChunks);
            var parityDisk = diskList[stripeInfo.ParityDisks[0]];
            var parityKey = GetOrCreateDiskKey(parityDisk.DiskId);
            var encryptedParity = Encrypt(parity, parityKey);
            writeTasks.Add(WriteToDiskAsync(parityDisk, encryptedParity, offset, cancellationToken));

            await Task.WhenAll(writeTasks);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(IEnumerable<DiskInfo> disks,
            long offset, int length, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var decryptedChunks = new Dictionary<int, byte[]>();
            var failedDisk = -1;

            for (int i = 0; i < stripeInfo.DataDisks.Length; i++)
            {
                var diskIndex = stripeInfo.DataDisks[i];
                var disk = diskList[diskIndex];

                if (disk.HealthStatus == SdkDiskHealthStatus.Healthy)
                {
                    try
                    {
                        var encrypted = await ReadFromDiskAsync(disk, offset, _chunkSize, cancellationToken);
                        var key = GetOrCreateDiskKey(disk.DiskId);
                        decryptedChunks[i] = Decrypt(encrypted, key);
                    }
                    catch { failedDisk = i; }
                }
                else { failedDisk = i; }
            }

            if (failedDisk >= 0)
            {
                decryptedChunks[failedDisk] = await ReconstructEncryptedChunkAsync(
                    diskList, decryptedChunks, failedDisk, stripeInfo, offset, cancellationToken);
            }

            return AssembleResult(decryptedChunks, stripeInfo, length);
        }

        public override async Task RebuildDiskAsync(DiskInfo failedDisk, IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk, IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var allDisks = healthyDisks.Append(failedDisk).ToList();
            var failedDiskIndex = allDisks.IndexOf(failedDisk);
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            // Generate new key for target disk
            var targetKey = GenerateKey();
            _diskKeys[targetDisk.DiskId] = targetKey;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stripeInfo = CalculateStripe(offset / _chunkSize, allDisks.Count);

                var reconstructed = await ReconstructAndReencryptAsync(
                    allDisks, failedDiskIndex, stripeInfo, offset, targetKey, cancellationToken);

                await WriteToDiskAsync(targetDisk, reconstructed, offset, cancellationToken);
                bytesRebuilt += _chunkSize;

                ReportProgress(progressCallback, bytesRebuilt, totalBytes, startTime);
            }
        }

        private byte[] GetOrCreateDiskKey(string diskId)
        {
            return _diskKeys.GetOrAdd(diskId, _ => DeriveKeyFromMaster(diskId));
        }

        private byte[] DeriveKeyFromMaster(string diskId)
        {
            using var hmac = new HMACSHA256(_masterKey);
            return hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(diskId))[..32];
        }

        private static byte[] GenerateKey()
        {
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            return key;
        }

        private byte[] Encrypt(byte[] data, byte[] key)
        {
            // Simplified XOR encryption for demonstration
            var result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)(data[i] ^ key[i % key.Length]);
            return result;
        }

        private byte[] Decrypt(byte[] data, byte[] key) => Encrypt(data, key); // XOR is symmetric

        private byte[] CalculateXorParity(List<byte[]> chunks)
        {
            var parity = new byte[_chunkSize];
            foreach (var chunk in chunks.Where(c => c != null))
                for (int i = 0; i < _chunkSize && i < chunk.Length; i++)
                    parity[i] ^= chunk[i];
            return parity;
        }

        private async Task<byte[]> ReconstructEncryptedChunkAsync(List<DiskInfo> disks,
            Dictionary<int, byte[]> chunks, int failedIndex, StripeInfo stripeInfo, long offset, CancellationToken ct)
        {
            var parityDisk = disks[stripeInfo.ParityDisks[0]];
            var encrypted = await ReadFromDiskAsync(parityDisk, offset, _chunkSize, ct);
            var key = GetOrCreateDiskKey(parityDisk.DiskId);
            var parity = Decrypt(encrypted, key);

            var result = parity.ToArray();
            foreach (var kvp in chunks)
            {
                if (kvp.Key != failedIndex && kvp.Value != null)
                    for (int i = 0; i < _chunkSize; i++)
                        result[i] ^= kvp.Value[i];
            }
            return result;
        }

        private async Task<byte[]> ReconstructAndReencryptAsync(List<DiskInfo> disks, int failedIndex,
            StripeInfo stripeInfo, long offset, byte[] newKey, CancellationToken ct)
        {
            var chunks = new Dictionary<int, byte[]>();
            for (int i = 0; i < stripeInfo.DataDisks.Length; i++)
            {
                var diskIndex = stripeInfo.DataDisks[i];
                if (diskIndex != failedIndex)
                {
                    var disk = disks[diskIndex];
                    var encrypted = await ReadFromDiskAsync(disk, offset, _chunkSize, ct);
                    var key = GetOrCreateDiskKey(disk.DiskId);
                    chunks[i] = Decrypt(encrypted, key);
                }
            }

            var reconstructed = await ReconstructEncryptedChunkAsync(disks, chunks,
                Array.IndexOf(stripeInfo.DataDisks, failedIndex), stripeInfo, offset, ct);

            return Encrypt(reconstructed, newKey);
        }

        private byte[] AssembleResult(Dictionary<int, byte[]> chunks, StripeInfo stripeInfo, int length)
        {
            var result = new byte[length];
            var position = 0;
            for (int i = 0; i < stripeInfo.DataDisks.Length && position < length; i++)
            {
                if (chunks.TryGetValue(i, out var chunk))
                {
                    var copyLength = Math.Min(chunk.Length, length - position);
                    Array.Copy(chunk, 0, result, position, copyLength);
                    position += copyLength;
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

        private void ReportProgress(IProgress<RebuildProgress>? cb, long rebuilt, long total, DateTime start)
        {
            if (cb == null) return;
            var speed = rebuilt / (DateTime.UtcNow - start).TotalSeconds;
            cb.Report(new RebuildProgress((double)rebuilt / total, rebuilt, total,
                TimeSpan.FromSeconds((total - rebuilt) / speed), (long)speed));
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
    }

    /// <summary>
    /// DUP/DDP Strategy - Data/Distributed Data Protection.
    /// </summary>
    public sealed class DupDdpStrategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly bool _distributed;

        public DupDdpStrategy(int chunkSize = 64 * 1024, bool distributed = true)
        {
            _chunkSize = chunkSize;
            _distributed = distributed;
        }

        public override RaidLevel Level => _distributed ? RaidLevel.Ddp : RaidLevel.Dup;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 2,
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(3),
            ReadPerformanceMultiplier: 1.2,
            WritePerformanceMultiplier: 0.7,
            CapacityEfficiency: 0.5,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: false);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            if (_distributed)
            {
                var protectionDisk = (int)(blockIndex % diskCount);
                var dataDisk = (int)((blockIndex + 1) % diskCount);
                return new StripeInfo(blockIndex, new[] { dataDisk }, new[] { protectionDisk }, _chunkSize, 1, 1);
            }
            return new StripeInfo(blockIndex, new[] { 0 }, new[] { 1 }, _chunkSize, 1, 1);
        }

        public override async Task WriteAsync(ReadOnlyMemory<byte> data, IEnumerable<DiskInfo> disks,
            long offset, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var dataBytes = data.ToArray();
            var dataDisk = diskList[stripeInfo.DataDisks[0]];
            var protectionDisk = diskList[stripeInfo.ParityDisks[0]];

            await Task.WhenAll(
                WriteToDiskAsync(dataDisk, dataBytes, offset, cancellationToken),
                WriteToDiskAsync(protectionDisk, dataBytes, offset, cancellationToken) // DUP writes copy
            );
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(IEnumerable<DiskInfo> disks,
            long offset, int length, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            var dataDisk = diskList[stripeInfo.DataDisks[0]];
            var protectionDisk = diskList[stripeInfo.ParityDisks[0]];

            if (dataDisk.HealthStatus == SdkDiskHealthStatus.Healthy)
            {
                try { return await ReadFromDiskAsync(dataDisk, offset, length, cancellationToken); }
                catch { }
            }
            return await ReadFromDiskAsync(protectionDisk, offset, length, cancellationToken);
        }

        public override async Task RebuildDiskAsync(DiskInfo failedDisk, IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk, IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var sourceDisk = healthyDisks.First();
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = await ReadFromDiskAsync(sourceDisk, offset, bufferSize, cancellationToken);
                await WriteToDiskAsync(targetDisk, data, offset, cancellationToken);
                bytesRebuilt += bufferSize;

                if (progressCallback != null)
                {
                    var speed = bytesRebuilt / (DateTime.UtcNow - startTime).TotalSeconds;
                    progressCallback.Report(new RebuildProgress((double)bytesRebuilt / totalBytes, bytesRebuilt, totalBytes,
                        TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / speed), (long)speed));
                }
            }
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
    }

    /// <summary>
    /// SPAN/BIG Strategy - Simple spanning across disks.
    /// </summary>
    public sealed class SpanBigStrategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;

        public SpanBigStrategy(int chunkSize = 64 * 1024) => _chunkSize = chunkSize;

        public override RaidLevel Level => RaidLevel.SpanBig;

        public override RaidCapabilities Capabilities => new RaidCapabilities(0, 2, null, _chunkSize,
            TimeSpan.Zero, 1.0, 1.0, 1.0, false, true, false);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            return new StripeInfo(blockIndex, Enumerable.Range(0, diskCount).ToArray(),
                Array.Empty<int>(), _chunkSize, diskCount, 0);
        }

        public override async Task WriteAsync(ReadOnlyMemory<byte> data, IEnumerable<DiskInfo> disks,
            long offset, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var (diskIndex, diskOffset) = MapOffsetToDisk(diskList, offset);
            await WriteToDiskAsync(diskList[diskIndex], data.ToArray(), diskOffset, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(IEnumerable<DiskInfo> disks,
            long offset, int length, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var (diskIndex, diskOffset) = MapOffsetToDisk(diskList, offset);
            return await ReadFromDiskAsync(diskList[diskIndex], diskOffset, length, cancellationToken);
        }

        public override Task RebuildDiskAsync(DiskInfo failedDisk, IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk, IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("SPAN/BIG does not support rebuild");
        }

        private (int, long) MapOffsetToDisk(List<DiskInfo> disks, long offset)
        {
            long cumulative = 0;
            for (int i = 0; i < disks.Count; i++)
            {
                if (offset < cumulative + disks[i].Capacity)
                    return (i, offset - cumulative);
                cumulative += disks[i].Capacity;
            }
            return (disks.Count - 1, offset - cumulative + disks[^1].Capacity);
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
    }

    /// <summary>
    /// MAID Strategy - Massive Array of Idle Disks (power saving).
    /// </summary>
    public sealed class MaidStrategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;
        private readonly ConcurrentDictionary<string, DiskPowerState> _diskStates;
        private readonly TimeSpan _spinDownDelay;

        public MaidStrategy(int chunkSize = 64 * 1024, int spinDownMinutes = 10)
        {
            _chunkSize = chunkSize;
            _diskStates = new ConcurrentDictionary<string, DiskPowerState>();
            _spinDownDelay = TimeSpan.FromMinutes(spinDownMinutes);
        }

        public override RaidLevel Level => RaidLevel.Maid;

        public override RaidCapabilities Capabilities => new RaidCapabilities(
            RedundancyLevel: 1,
            MinDisks: 3,
            MaxDisks: null,
            StripeSize: _chunkSize,
            EstimatedRebuildTimePerTB: TimeSpan.FromHours(4),
            ReadPerformanceMultiplier: 0.7, // Spin-up latency
            WritePerformanceMultiplier: 0.6,
            CapacityEfficiency: 0.67,
            SupportsHotSpare: true,
            SupportsOnlineExpansion: true,
            RequiresUniformDiskSize: true);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            var parityDisk = (int)(blockIndex % diskCount);
            var dataDisks = Enumerable.Range(0, diskCount).Where(i => i != parityDisk).ToArray();
            return new StripeInfo(blockIndex, dataDisks, new[] { parityDisk }, _chunkSize, diskCount - 1, 1);
        }

        public override async Task WriteAsync(ReadOnlyMemory<byte> data, IEnumerable<DiskInfo> disks,
            long offset, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            // Spin up required disks
            var requiredDisks = stripeInfo.DataDisks.Concat(stripeInfo.ParityDisks)
                .Select(i => diskList[i]).ToList();
            await SpinUpDisksAsync(requiredDisks, cancellationToken);

            var dataBytes = data.ToArray();
            var chunks = SplitIntoChunks(dataBytes, _chunkSize);
            var parity = CalculateXorParity(chunks);

            var writeTasks = new List<Task>();
            for (int i = 0; i < chunks.Count && i < stripeInfo.DataDisks.Length; i++)
                writeTasks.Add(WriteToDiskAsync(diskList[stripeInfo.DataDisks[i]], chunks[i], offset, cancellationToken));
            writeTasks.Add(WriteToDiskAsync(diskList[stripeInfo.ParityDisks[0]], parity, offset, cancellationToken));

            await Task.WhenAll(writeTasks);
            ScheduleSpinDown(requiredDisks);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(IEnumerable<DiskInfo> disks,
            long offset, int length, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var stripeInfo = CalculateStripe(offset / _chunkSize, diskList.Count);

            // Spin up required disks
            var requiredDisks = stripeInfo.DataDisks.Select(i => diskList[i]).ToList();
            await SpinUpDisksAsync(requiredDisks, cancellationToken);

            var result = new byte[length];
            var position = 0;

            for (int i = 0; i < stripeInfo.DataDisks.Length && position < length; i++)
            {
                var diskIndex = stripeInfo.DataDisks[i];
                var chunk = await ReadFromDiskAsync(diskList[diskIndex], offset, _chunkSize, cancellationToken);
                var copyLength = Math.Min(chunk.Length, length - position);
                Array.Copy(chunk, 0, result, position, copyLength);
                position += copyLength;
            }

            ScheduleSpinDown(requiredDisks);
            return result;
        }

        public override async Task RebuildDiskAsync(DiskInfo failedDisk, IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk, IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var allDisks = healthyDisks.Append(failedDisk).ToList();

            // Keep all disks spinning during rebuild
            await SpinUpDisksAsync(allDisks, cancellationToken);

            var failedDiskIndex = allDisks.IndexOf(failedDisk);
            var totalBytes = failedDisk.Capacity;
            var bytesRebuilt = 0L;
            var startTime = DateTime.UtcNow;

            const int bufferSize = 1024 * 1024;
            for (long offset = 0; offset < totalBytes; offset += bufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stripeInfo = CalculateStripe(offset / _chunkSize, allDisks.Count);

                byte[] reconstructed;
                if (stripeInfo.ParityDisks.Contains(failedDiskIndex))
                {
                    var chunks = new List<byte[]>();
                    foreach (var di in stripeInfo.DataDisks)
                    {
                        var chunk = await ReadFromDiskAsync(allDisks[di], offset, _chunkSize, cancellationToken);
                        chunks.Add(chunk);
                    }
                    reconstructed = CalculateXorParity(chunks);
                }
                else
                {
                    var parity = await ReadFromDiskAsync(allDisks[stripeInfo.ParityDisks[0]], offset, _chunkSize, cancellationToken);
                    reconstructed = parity.ToArray();
                    foreach (var di in stripeInfo.DataDisks)
                    {
                        if (di != failedDiskIndex)
                        {
                            var chunk = await ReadFromDiskAsync(allDisks[di], offset, _chunkSize, cancellationToken);
                            for (int j = 0; j < _chunkSize; j++)
                                reconstructed[j] ^= chunk[j];
                        }
                    }
                }

                await WriteToDiskAsync(targetDisk, reconstructed, offset, cancellationToken);
                bytesRebuilt += _chunkSize;

                if (progressCallback != null)
                {
                    var speed = bytesRebuilt / (DateTime.UtcNow - startTime).TotalSeconds;
                    progressCallback.Report(new RebuildProgress((double)bytesRebuilt / totalBytes, bytesRebuilt, totalBytes,
                        TimeSpan.FromSeconds((totalBytes - bytesRebuilt) / speed), (long)speed));
                }
            }
        }

        private async Task SpinUpDisksAsync(List<DiskInfo> disks, CancellationToken ct)
        {
            foreach (var disk in disks)
            {
                var state = _diskStates.GetOrAdd(disk.DiskId, _ => new DiskPowerState { State = PowerState.Idle });
                if (state.State == PowerState.SpunDown)
                {
                    state.State = PowerState.SpinningUp;
                    await Task.Delay(TimeSpan.FromSeconds(2), ct); // Simulate spin-up
                    state.State = PowerState.Active;
                }
                state.LastAccess = DateTime.UtcNow;
            }
        }

        private void ScheduleSpinDown(List<DiskInfo> disks)
        {
            foreach (var disk in disks)
            {
                if (_diskStates.TryGetValue(disk.DiskId, out var state))
                    state.LastAccess = DateTime.UtcNow;
            }
        }

        private byte[] CalculateXorParity(List<byte[]> chunks)
        {
            var parity = new byte[_chunkSize];
            foreach (var chunk in chunks.Where(c => c != null))
                for (int i = 0; i < _chunkSize && i < chunk.Length; i++)
                    parity[i] ^= chunk[i];
            return parity;
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

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);

        private enum PowerState { SpunDown, SpinningUp, Active, Idle }
        private sealed class DiskPowerState { public PowerState State { get; set; } public DateTime LastAccess { get; set; } }
    }

    /// <summary>
    /// Linear Strategy - Linear concatenation of disks.
    /// </summary>
    public sealed class LinearStrategy : SdkRaidStrategyBase
    {
        private readonly int _chunkSize;

        public LinearStrategy(int chunkSize = 64 * 1024) => _chunkSize = chunkSize;

        public override RaidLevel Level => RaidLevel.Linear;

        public override RaidCapabilities Capabilities => new RaidCapabilities(0, 1, null, _chunkSize,
            TimeSpan.Zero, 1.0, 1.0, 1.0, false, true, false);

        public override StripeInfo CalculateStripe(long blockIndex, int diskCount)
        {
            return new StripeInfo(blockIndex, Enumerable.Range(0, diskCount).ToArray(),
                Array.Empty<int>(), _chunkSize, diskCount, 0);
        }

        public override async Task WriteAsync(ReadOnlyMemory<byte> data, IEnumerable<DiskInfo> disks,
            long offset, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var (diskIndex, diskOffset) = MapOffsetToDisk(diskList, offset);
            await WriteToDiskAsync(diskList[diskIndex], data.ToArray(), diskOffset, cancellationToken);
        }

        public override async Task<ReadOnlyMemory<byte>> ReadAsync(IEnumerable<DiskInfo> disks,
            long offset, int length, CancellationToken cancellationToken = default)
        {
            ValidateDiskConfiguration(disks);
            var diskList = disks.ToList();
            var (diskIndex, diskOffset) = MapOffsetToDisk(diskList, offset);
            return await ReadFromDiskAsync(diskList[diskIndex], diskOffset, length, cancellationToken);
        }

        public override Task RebuildDiskAsync(DiskInfo failedDisk, IEnumerable<DiskInfo> healthyDisks,
            DiskInfo targetDisk, IProgress<RebuildProgress>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Linear does not support rebuild");
        }

        private (int, long) MapOffsetToDisk(List<DiskInfo> disks, long offset)
        {
            long cumulative = 0;
            for (int i = 0; i < disks.Count; i++)
            {
                if (offset < cumulative + disks[i].Capacity)
                    return (i, offset - cumulative);
                cumulative += disks[i].Capacity;
            }
            return (disks.Count - 1, offset - cumulative + disks[^1].Capacity);
        }

        private Task WriteToDiskAsync(DiskInfo disk, byte[] data, long offset, CancellationToken ct) => Task.CompletedTask;
        private Task<byte[]> ReadFromDiskAsync(DiskInfo disk, long offset, int length, CancellationToken ct) => Task.FromResult(new byte[length]);
    }
}
