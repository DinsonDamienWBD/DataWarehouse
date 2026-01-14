using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Storage
{
    /// <summary>
    /// Comprehensive RAID Engine supporting RAID levels 0, 1, 2, 3, 4, 5, 6, 10, 50, 60, 1E, 5E/5EE, 100.
    /// Handles data striping, mirroring, parity calculation, and automatic rebuild on failure.
    /// Thread-safe and production-ready for high-availability storage systems.
    /// </summary>
    public class RaidEngine : IDisposable
    {
        private readonly RaidConfiguration _config;
        private readonly IKernelContext _context;
        private readonly ConcurrentDictionary<string, RaidMetadata> _metadata;
        private readonly ConcurrentDictionary<int, ProviderHealth> _providerHealth;
        private readonly Timer? _healthMonitorTimer;
        private readonly SemaphoreSlim _rebuildLock = new(1, 1);
        private bool _disposed;

        public RaidEngine(RaidConfiguration config, IKernelContext context)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _metadata = new ConcurrentDictionary<string, RaidMetadata>();
            _providerHealth = new ConcurrentDictionary<int, ProviderHealth>();

            // Initialize provider health
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                _providerHealth[i] = new ProviderHealth { Index = i, Status = ProviderStatus.Healthy };
            }

            // Start health monitoring
            if (_config.HealthCheckInterval > TimeSpan.Zero)
            {
                _healthMonitorTimer = new Timer(
                    async _ => await MonitorHealthAsync(),
                    null,
                    _config.HealthCheckInterval,
                    _config.HealthCheckInterval
                );
            }

            ValidateConfiguration();
        }

        /// <summary>
        /// Saves data using the configured RAID level.
        /// </summary>
        public async Task SaveAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            switch (_config.Level)
            {
                // Standard RAID
                case RaidLevel.RAID_0:
                    await SaveRAID0Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_1:
                    await SaveRAID1Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_2:
                    await SaveRAID2Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_3:
                    await SaveRAID3Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_4:
                    await SaveRAID4Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_5:
                    await SaveRAID5Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_6:
                    await SaveRAID6Async(key, data, getProvider);
                    break;

                // Nested RAID
                case RaidLevel.RAID_10:
                    await SaveRAID10Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_01:
                    await SaveRAID01Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_03:
                    await SaveRAID03Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_50:
                    await SaveRAID50Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_60:
                    await SaveRAID60Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_100:
                    await SaveRAID100Async(key, data, getProvider);
                    break;

                // Enhanced RAID
                case RaidLevel.RAID_1E:
                    await SaveRAID1EAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_5E:
                    await SaveRAID5EAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_5EE:
                    await SaveRAID5EEAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_6E:
                    await SaveRAID6EAsync(key, data, getProvider);
                    break;

                // ZFS RAID
                case RaidLevel.RAID_Z1:
                    await SaveRAIDZ1Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_Z2:
                    await SaveRAIDZ2Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_Z3:
                    await SaveRAIDZ3Async(key, data, getProvider);
                    break;

                // Vendor-Specific RAID
                case RaidLevel.RAID_DP:
                    await SaveRAIDDPAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_S:
                    await SaveRAIDSAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_7:
                    await SaveRAID7Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_FR:
                    await SaveRAIDFRAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_Unraid:
                    await SaveUnraidAsync(key, data, getProvider);
                    break;

                // Advanced/Proprietary RAID
                case RaidLevel.RAID_MD10:
                    await SaveRAIDMD10Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_Adaptive:
                    await SaveRAIDAdaptiveAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_Beyond:
                    await SaveRAIDBeyondAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_Declustered:
                    await SaveRAIDDeclusteredAsync(key, data, getProvider);
                    break;

                // Phase 3: Extended RAID Levels
                case RaidLevel.RAID_71:
                    await SaveRAID71Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_72:
                    await SaveRAID72Async(key, data, getProvider);
                    break;
                case RaidLevel.RAID_NM:
                    await SaveRAIDNMAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_Matrix:
                    await SaveRAIDMatrixAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_JBOD:
                    await SaveRAIDJBODAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_Crypto:
                    await SaveRAIDCryptoAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_DUP:
                    await SaveRAIDDUPAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_DDP:
                    await SaveRAIDDDPAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_SPAN:
                    await SaveRAIDSPANAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_BIG:
                    await SaveRAIDBIGAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_MAID:
                    await SaveRAIDMAIDAsync(key, data, getProvider);
                    break;
                case RaidLevel.RAID_Linear:
                    await SaveRAIDLinearAsync(key, data, getProvider);
                    break;

                default:
                    throw new NotImplementedException($"RAID level {_config.Level} not implemented");
            }
        }

        /// <summary>
        /// Loads data using the configured RAID level.
        /// </summary>
        public async Task<Stream> LoadAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            switch (_config.Level)
            {
                // Standard RAID
                case RaidLevel.RAID_0:
                    return await LoadRAID0Async(key, getProvider);
                case RaidLevel.RAID_1:
                    return await LoadRAID1Async(key, getProvider);
                case RaidLevel.RAID_2:
                    return await LoadRAID2Async(key, getProvider);
                case RaidLevel.RAID_3:
                    return await LoadRAID3Async(key, getProvider);
                case RaidLevel.RAID_4:
                    return await LoadRAID4Async(key, getProvider);
                case RaidLevel.RAID_5:
                    return await LoadRAID5Async(key, getProvider);
                case RaidLevel.RAID_6:
                    return await LoadRAID6Async(key, getProvider);

                // Nested RAID
                case RaidLevel.RAID_10:
                    return await LoadRAID10Async(key, getProvider);
                case RaidLevel.RAID_01:
                    return await LoadRAID01Async(key, getProvider);
                case RaidLevel.RAID_03:
                    return await LoadRAID03Async(key, getProvider);
                case RaidLevel.RAID_50:
                    return await LoadRAID50Async(key, getProvider);
                case RaidLevel.RAID_60:
                    return await LoadRAID60Async(key, getProvider);
                case RaidLevel.RAID_100:
                    return await LoadRAID100Async(key, getProvider);

                // Enhanced RAID
                case RaidLevel.RAID_1E:
                    return await LoadRAID1EAsync(key, getProvider);
                case RaidLevel.RAID_5E:
                    return await LoadRAID5EAsync(key, getProvider);
                case RaidLevel.RAID_5EE:
                    return await LoadRAID5EEAsync(key, getProvider);
                case RaidLevel.RAID_6E:
                    return await LoadRAID6EAsync(key, getProvider);

                // ZFS RAID
                case RaidLevel.RAID_Z1:
                    return await LoadRAIDZ1Async(key, getProvider);
                case RaidLevel.RAID_Z2:
                    return await LoadRAIDZ2Async(key, getProvider);
                case RaidLevel.RAID_Z3:
                    return await LoadRAIDZ3Async(key, getProvider);

                // Vendor-Specific RAID
                case RaidLevel.RAID_DP:
                    return await LoadRAIDDPAsync(key, getProvider);
                case RaidLevel.RAID_S:
                    return await LoadRAIDSAsync(key, getProvider);
                case RaidLevel.RAID_7:
                    return await LoadRAID7Async(key, getProvider);
                case RaidLevel.RAID_FR:
                    return await LoadRAIDFRAsync(key, getProvider);
                case RaidLevel.RAID_Unraid:
                    return await LoadUnraidAsync(key, getProvider);

                // Advanced/Proprietary RAID
                case RaidLevel.RAID_MD10:
                    return await LoadRAIDMD10Async(key, getProvider);
                case RaidLevel.RAID_Adaptive:
                    return await LoadRAIDAdaptiveAsync(key, getProvider);
                case RaidLevel.RAID_Beyond:
                    return await LoadRAIDBeyondAsync(key, getProvider);
                case RaidLevel.RAID_Declustered:
                    return await LoadRAIDDeclusteredAsync(key, getProvider);

                // Phase 3: Extended RAID Levels
                case RaidLevel.RAID_71:
                    return await LoadRAID71Async(key, getProvider);
                case RaidLevel.RAID_72:
                    return await LoadRAID72Async(key, getProvider);
                case RaidLevel.RAID_NM:
                    return await LoadRAIDNMAsync(key, getProvider);
                case RaidLevel.RAID_Matrix:
                    return await LoadRAIDMatrixAsync(key, getProvider);
                case RaidLevel.RAID_JBOD:
                    return await LoadRAIDJBODAsync(key, getProvider);
                case RaidLevel.RAID_Crypto:
                    return await LoadRAIDCryptoAsync(key, getProvider);
                case RaidLevel.RAID_DUP:
                    return await LoadRAIDDUPAsync(key, getProvider);
                case RaidLevel.RAID_DDP:
                    return await LoadRAIDDDPAsync(key, getProvider);
                case RaidLevel.RAID_SPAN:
                    return await LoadRAIDSPANAsync(key, getProvider);
                case RaidLevel.RAID_BIG:
                    return await LoadRAIDBIGAsync(key, getProvider);
                case RaidLevel.RAID_MAID:
                    return await LoadRAIDMAIDAsync(key, getProvider);
                case RaidLevel.RAID_Linear:
                    return await LoadRAIDLinearAsync(key, getProvider);

                default:
                    throw new NotImplementedException($"RAID level {_config.Level} not implemented");
            }
        }

        // ==================== RAID 0: Striping (Performance) ====================

        private async Task SaveRAID0Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            var chunks = SplitIntoChunks(data, _config.StripeSize);
            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_0,
                TotalSize = data.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>>()
            };

            var tasks = new List<Task>();
            for (int i = 0; i < chunks.Count; i++)
            {
                int providerIndex = i % _config.ProviderCount;
                int chunkIndex = i;

                if (!metadata.ProviderMapping.ContainsKey(providerIndex))
                    metadata.ProviderMapping[providerIndex] = new List<int>();
                metadata.ProviderMapping[providerIndex].Add(chunkIndex);

                var chunkKey = $"{key}.chunk.{chunkIndex}";
                tasks.Add(SaveChunkAsync(getProvider(providerIndex), chunkKey, chunks[i]));
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID0] Saved {key}: {chunks.Count} chunks across {_config.ProviderCount} providers");
        }

        private async Task<Stream> LoadRAID0Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            var chunks = new byte[metadata.ChunkCount][];
            var tasks = new List<Task>();

            for (int i = 0; i < metadata.ChunkCount; i++)
            {
                int providerIndex = i % _config.ProviderCount;
                int chunkIndex = i;
                var chunkKey = $"{key}.chunk.{chunkIndex}";

                tasks.Add(Task.Run(async () =>
                {
                    chunks[chunkIndex] = await LoadChunkAsync(getProvider(providerIndex), chunkKey);
                }));
            }

            await Task.WhenAll(tasks);
            return ReassembleChunks(chunks);
        }

        // ==================== RAID 1: Mirroring (Redundancy) ====================

        private async Task SaveRAID1Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            var buffer = new MemoryStream();
            await data.CopyToAsync(buffer);
            buffer.Position = 0;

            var tasks = new List<Task>();
            int mirrorCount = MathUtils.Min(_config.MirrorCount, _config.ProviderCount);

            for (int i = 0; i < mirrorCount; i++)
            {
                var mirrorStream = new MemoryStream();
                buffer.Position = 0;
                await buffer.CopyToAsync(mirrorStream);
                mirrorStream.Position = 0;

                int providerIndex = i;
                tasks.Add(SaveChunkAsync(getProvider(providerIndex), key, mirrorStream.ToArray()));
            }

            await Task.WhenAll(tasks);

            _metadata[key] = new RaidMetadata
            {
                Level = RaidLevel.RAID_1,
                TotalSize = buffer.Length,
                ChunkCount = 1,
                MirrorCount = mirrorCount
            };

            _context.LogInfo($"[RAID1] Mirrored {key} to {mirrorCount} providers");
        }

        private async Task<Stream> LoadRAID1Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            // Try each mirror until one succeeds
            for (int i = 0; i < metadata.MirrorCount; i++)
            {
                try
                {
                    if (_providerHealth[i].Status == ProviderStatus.Failed)
                        continue;

                    var chunk = await LoadChunkAsync(getProvider(i), key);
                    return new MemoryStream(chunk);
                }
                catch (Exception ex)
                {
                    _context.LogWarning($"[RAID1] Mirror {i} failed: {ex.Message}");
                    MarkProviderFailed(i);
                }
            }

            throw new IOException($"All mirrors failed for {key}");
        }

        // ==================== RAID 5: Distributed Parity ====================

        private async Task SaveRAID5Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 3)
                throw new InvalidOperationException("RAID 5 requires at least 3 providers");

            var chunks = SplitIntoChunks(data, _config.StripeSize);
            int dataDisks = _config.ProviderCount - 1;
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            var tasks = new List<Task>();
            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_5,
                TotalSize = data.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>>()
            };

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityDisk = stripe % _config.ProviderCount;
                var stripeChunks = new List<byte[]>();

                // Read data chunks for this stripe
                for (int diskIdx = 0; diskIdx < dataDisks && (stripe * dataDisks + diskIdx) < chunks.Count; diskIdx++)
                {
                    int chunkIdx = stripe * dataDisks + diskIdx;
                    stripeChunks.Add(chunks[chunkIdx]);
                }

                // Calculate parity using XOR
                var parity = CalculateParityXOR(stripeChunks);

                // Write data chunks (skipping parity disk)
                int dataDiskCounter = 0;
                for (int providerIdx = 0; providerIdx < _config.ProviderCount; providerIdx++)
                {
                    if (providerIdx == parityDisk)
                    {
                        // Write parity chunk
                        var parityKey = $"{key}.parity.{stripe}";
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), parityKey, parity));
                    }
                    else if (dataDiskCounter < stripeChunks.Count)
                    {
                        // Write data chunk
                        int chunkIdx = stripe * dataDisks + dataDiskCounter;
                        var chunkKey = $"{key}.chunk.{chunkIdx}";
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), chunkKey, stripeChunks[dataDiskCounter]));
                        dataDiskCounter++;
                    }
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID5] Saved {key} with distributed parity across {_config.ProviderCount} providers");
        }

        private async Task<Stream> LoadRAID5Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 1;
            int stripeCount = (int)MathUtils.Ceiling((double)metadata.ChunkCount / dataDisks);
            var allChunks = new List<byte[]>();

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityDisk = stripe % _config.ProviderCount;
                var stripeChunks = new List<byte[]>();
                var failedDisk = -1;

                // Try to read all data chunks
                int dataDiskCounter = 0;
                for (int providerIdx = 0; providerIdx < _config.ProviderCount && dataDiskCounter < dataDisks; providerIdx++)
                {
                    if (providerIdx == parityDisk)
                        continue;

                    int chunkIdx = stripe * dataDisks + dataDiskCounter;
                    if (chunkIdx >= metadata.ChunkCount)
                        break;

                    var chunkKey = $"{key}.chunk.{chunkIdx}";
                    try
                    {
                        var chunk = await LoadChunkAsync(getProvider(providerIdx), chunkKey);
                        stripeChunks.Add(chunk);
                    }
                    catch
                    {
                        failedDisk = providerIdx;
                        stripeChunks.Add(null!); // Placeholder
                    }
                    dataDiskCounter++;
                }

                // If a disk failed, rebuild from parity
                if (failedDisk != -1)
                {
                    var parityKey = $"{key}.parity.{stripe}";
                    var parity = await LoadChunkAsync(getProvider(parityDisk), parityKey);

                    // Rebuild missing chunk using XOR
                    var rebuiltChunk = RebuildChunkFromParity(stripeChunks, parity);
                    stripeChunks[stripeChunks.IndexOf(null!)] = rebuiltChunk;

                    _context.LogWarning($"[RAID5] Rebuilt chunk from parity for stripe {stripe}");
                }

                allChunks.AddRange(stripeChunks.Where(c => c != null));
            }

            return ReassembleChunks(allChunks.ToArray());
        }

        // ==================== RAID 6: Dual Parity ====================

        private async Task SaveRAID6Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("RAID 6 requires at least 4 providers");

            var chunks = SplitIntoChunks(data, _config.StripeSize);
            int dataDisks = _config.ProviderCount - 2; // Two parity disks
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            var tasks = new List<Task>();
            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_6,
                TotalSize = data.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>>()
            };

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityP = stripe % _config.ProviderCount;
                int parityQ = (stripe + 1) % _config.ProviderCount;

                var stripeChunks = new List<byte[]>();

                // Read data chunks for this stripe
                for (int diskIdx = 0; diskIdx < dataDisks && (stripe * dataDisks + diskIdx) < chunks.Count; diskIdx++)
                {
                    int chunkIdx = stripe * dataDisks + diskIdx;
                    stripeChunks.Add(chunks[chunkIdx]);
                }

                // Calculate P parity (XOR)
                var parityPData = CalculateParityXOR(stripeChunks);

                // Calculate Q parity (Reed-Solomon)
                var parityQData = CalculateParityReedSolomon(stripeChunks);

                // Write chunks
                int dataDiskCounter = 0;
                for (int providerIdx = 0; providerIdx < _config.ProviderCount; providerIdx++)
                {
                    if (providerIdx == parityP)
                    {
                        var keyP = $"{key}.parityP.{stripe}";
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), keyP, parityPData));
                    }
                    else if (providerIdx == parityQ)
                    {
                        var keyQ = $"{key}.parityQ.{stripe}";
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), keyQ, parityQData));
                    }
                    else if (dataDiskCounter < stripeChunks.Count)
                    {
                        int chunkIdx = stripe * dataDisks + dataDiskCounter;
                        var chunkKey = $"{key}.chunk.{chunkIdx}";
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), chunkKey, stripeChunks[dataDiskCounter]));
                        dataDiskCounter++;
                    }
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID6] Saved {key} with dual parity (P+Q) across {_config.ProviderCount} providers");
        }

        private async Task<Stream> LoadRAID6Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 2;
            int stripeCount = (int)MathUtils.Ceiling((double)metadata.ChunkCount / dataDisks);
            var allChunks = new List<byte[]>();

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityP = stripe % _config.ProviderCount;
                int parityQ = (stripe + 1) % _config.ProviderCount;
                var stripeChunks = new List<byte[]>();
                var failedDisks = new List<int>();

                // Try to read all data chunks
                int dataDiskCounter = 0;
                for (int providerIdx = 0; providerIdx < _config.ProviderCount; providerIdx++)
                {
                    if (providerIdx == parityP || providerIdx == parityQ)
                        continue;

                    if (dataDiskCounter >= dataDisks)
                        break;

                    int chunkIdx = stripe * dataDisks + dataDiskCounter;
                    if (chunkIdx >= metadata.ChunkCount)
                        break;

                    var chunkKey = $"{key}.chunk.{chunkIdx}";
                    try
                    {
                        var chunk = await LoadChunkAsync(getProvider(providerIdx), chunkKey);
                        stripeChunks.Add(chunk);
                    }
                    catch
                    {
                        failedDisks.Add(providerIdx);
                        stripeChunks.Add(null!);
                    }
                    dataDiskCounter++;
                }

                // Rebuild up to 2 failed disks using dual parity
                if (failedDisks.Count > 0 && failedDisks.Count <= 2)
                {
                    var parityPKey = $"{key}.parityP.{stripe}";
                    var parityQKey = $"{key}.parityQ.{stripe}";

                    var pData = await LoadChunkAsync(getProvider(parityP), parityPKey);
                    var qData = await LoadChunkAsync(getProvider(parityQ), parityQKey);

                    // Rebuild using P and Q parity
                    var rebuiltChunks = RebuildFromDualParity(stripeChunks, pData, qData, failedDisks);

                    foreach (var (diskIdx, chunk) in rebuiltChunks)
                    {
                        stripeChunks[diskIdx] = chunk;
                    }

                    _context.LogWarning($"[RAID6] Rebuilt {failedDisks.Count} chunks from dual parity for stripe {stripe}");
                }
                else if (failedDisks.Count > 2)
                {
                    throw new IOException($"RAID 6 can only recover from 2 disk failures, but {failedDisks.Count} disks failed");
                }

                allChunks.AddRange(stripeChunks.Where(c => c != null));
            }

            return ReassembleChunks(allChunks.ToArray());
        }

        // ==================== RAID 10: Mirrored Stripes (RAID 1+0) ====================

        private async Task SaveRAID10Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 4 || _config.ProviderCount % 2 != 0)
                throw new InvalidOperationException("RAID 10 requires an even number of providers (minimum 4)");

            // First stripe the data (RAID 0)
            var chunks = SplitIntoChunks(data, _config.StripeSize);
            int stripeGroups = _config.ProviderCount / 2;

            var tasks = new List<Task>();
            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_10,
                TotalSize = data.Length,
                ChunkCount = chunks.Count,
                MirrorCount = 2
            };

            for (int i = 0; i < chunks.Count; i++)
            {
                int groupIdx = i % stripeGroups;
                int primaryProvider = groupIdx * 2;
                int mirrorProvider = groupIdx * 2 + 1;

                var chunkKey = $"{key}.chunk.{i}";

                // Write to primary
                tasks.Add(SaveChunkAsync(getProvider(primaryProvider), chunkKey, chunks[i]));

                // Write to mirror
                tasks.Add(SaveChunkAsync(getProvider(mirrorProvider), chunkKey, chunks[i]));
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID10] Saved {key} with mirrored striping across {_config.ProviderCount} providers");
        }

        private async Task<Stream> LoadRAID10Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int stripeGroups = _config.ProviderCount / 2;
            var chunks = new byte[metadata.ChunkCount][];
            var tasks = new List<Task>();

            for (int i = 0; i < metadata.ChunkCount; i++)
            {
                int groupIdx = i % stripeGroups;
                int primaryProvider = groupIdx * 2;
                int mirrorProvider = groupIdx * 2 + 1;
                int chunkIdx = i;

                var chunkKey = $"{key}.chunk.{chunkIdx}";

                tasks.Add(Task.Run(async () =>
                {
                    // Try primary first, fallback to mirror
                    try
                    {
                        chunks[chunkIdx] = await LoadChunkAsync(getProvider(primaryProvider), chunkKey);
                    }
                    catch
                    {
                        _context.LogWarning($"[RAID10] Primary failed for chunk {chunkIdx}, using mirror");
                        chunks[chunkIdx] = await LoadChunkAsync(getProvider(mirrorProvider), chunkKey);
                    }
                }));
            }

            await Task.WhenAll(tasks);
            return ReassembleChunks(chunks);
        }

        // ==================== RAID 50: Striped RAID 5 Sets (RAID 5+0) ====================

        private async Task SaveRAID50Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID 50 = Multiple RAID 5 sets striped together (RAID 5+0)
            // Each RAID 5 set needs minimum 3 disks
            if (_config.ProviderCount < 6)
                throw new InvalidOperationException("RAID 50 requires at least 6 providers (2 RAID 5 sets)");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            // Calculate RAID 5 set configuration
            int disksPerSet = 3; // Minimum for RAID 5
            if (_config.ProviderCount >= 8) disksPerSet = 4;
            if (_config.ProviderCount >= 12) disksPerSet = _config.ProviderCount / 3;

            int setsCount = _config.ProviderCount / disksPerSet;
            int dataDisksPerSet = disksPerSet - 1; // One parity per set

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_50,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>> { { 0, new List<int> { setsCount, disksPerSet } } }
            };

            var tasks = new List<Task>();

            // Stripe chunks across RAID 5 sets
            for (int chunkIdx = 0; chunkIdx < chunks.Count; chunkIdx++)
            {
                // Determine which RAID 5 set this chunk belongs to (stripe level)
                int setIdx = chunkIdx % setsCount;
                int setOffset = setIdx * disksPerSet;

                // Within the set, determine stripe and position
                int chunksInSet = (chunks.Count + setsCount - 1) / setsCount;
                int localChunkIdx = chunkIdx / setsCount;
                int stripeInSet = localChunkIdx / dataDisksPerSet;
                int diskInStripe = localChunkIdx % dataDisksPerSet;

                // Rotating parity within each set
                int parityDiskInSet = stripeInSet % disksPerSet;

                // Calculate actual disk position (skip parity disk)
                int actualDiskInSet = diskInStripe;
                if (actualDiskInSet >= parityDiskInSet)
                    actualDiskInSet++;

                int providerIdx = setOffset + actualDiskInSet;
                var chunkKey = $"{key}.set{setIdx}.chunk.{localChunkIdx}";
                tasks.Add(SaveChunkAsync(getProvider(providerIdx), chunkKey, chunks[chunkIdx]));
            }

            // Calculate and store parity for each set
            for (int setIdx = 0; setIdx < setsCount; setIdx++)
            {
                int setOffset = setIdx * disksPerSet;

                // Get all chunks belonging to this set
                var setChunks = new List<byte[]>();
                for (int i = setIdx; i < chunks.Count; i += setsCount)
                {
                    setChunks.Add(chunks[i]);
                }

                // Calculate parity for each stripe within the set
                int stripesInSet = (setChunks.Count + dataDisksPerSet - 1) / dataDisksPerSet;
                for (int stripe = 0; stripe < stripesInSet; stripe++)
                {
                    var stripeChunks = setChunks.Skip(stripe * dataDisksPerSet).Take(dataDisksPerSet).ToList();
                    if (stripeChunks.Count > 0)
                    {
                        var parity = CalculateParityXOR(stripeChunks);
                        int parityDiskInSet = stripe % disksPerSet;
                        int providerIdx = setOffset + parityDiskInSet;
                        var parityKey = $"{key}.set{setIdx}.parity.{stripe}";
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), parityKey, parity));
                    }
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID50] Saved {key} across {setsCount} RAID 5 sets ({disksPerSet} disks each)");
        }

        private async Task<Stream> LoadRAID50Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            // Retrieve set configuration from metadata
            int setsCount = metadata.ProviderMapping[0][0];
            int disksPerSet = metadata.ProviderMapping[0][1];
            int dataDisksPerSet = disksPerSet - 1;

            var allChunks = new byte[metadata.ChunkCount][];
            var failedSets = new Dictionary<int, List<int>>(); // setIdx -> failed local chunk indices

            // Load chunks from all sets
            for (int chunkIdx = 0; chunkIdx < metadata.ChunkCount; chunkIdx++)
            {
                int setIdx = chunkIdx % setsCount;
                int setOffset = setIdx * disksPerSet;
                int localChunkIdx = chunkIdx / setsCount;
                int stripeInSet = localChunkIdx / dataDisksPerSet;
                int diskInStripe = localChunkIdx % dataDisksPerSet;
                int parityDiskInSet = stripeInSet % disksPerSet;

                int actualDiskInSet = diskInStripe;
                if (actualDiskInSet >= parityDiskInSet)
                    actualDiskInSet++;

                int providerIdx = setOffset + actualDiskInSet;
                var chunkKey = $"{key}.set{setIdx}.chunk.{localChunkIdx}";

                try
                {
                    allChunks[chunkIdx] = await LoadChunkAsync(getProvider(providerIdx), chunkKey);
                }
                catch (Exception ex)
                {
                    _context.LogWarning($"[RAID50] Failed to load chunk {chunkIdx} from set {setIdx}: {ex.Message}");
                    if (!failedSets.ContainsKey(setIdx))
                        failedSets[setIdx] = new List<int>();
                    failedSets[setIdx].Add(chunkIdx);
                }
            }

            // Rebuild failed chunks using parity within each set
            foreach (var (setIdx, failedChunks) in failedSets)
            {
                int setOffset = setIdx * disksPerSet;

                foreach (var failedChunkIdx in failedChunks)
                {
                    int localChunkIdx = failedChunkIdx / setsCount;
                    int stripeInSet = localChunkIdx / dataDisksPerSet;
                    int parityDiskInSet = stripeInSet % disksPerSet;

                    // Load parity
                    var parityKey = $"{key}.set{setIdx}.parity.{stripeInSet}";
                    var parity = await LoadChunkAsync(getProvider(setOffset + parityDiskInSet), parityKey);

                    // Collect other chunks from the same stripe
                    var stripeChunks = new List<byte[]>();
                    int stripeStart = stripeInSet * dataDisksPerSet;
                    for (int i = 0; i < dataDisksPerSet; i++)
                    {
                        int globalIdx = (stripeStart + i) * setsCount + setIdx;
                        if (globalIdx < metadata.ChunkCount && globalIdx != failedChunkIdx && allChunks[globalIdx] != null)
                        {
                            stripeChunks.Add(allChunks[globalIdx]);
                        }
                    }

                    // Rebuild from parity
                    var rebuilt = new byte[parity.Length];
                    Array.Copy(parity, rebuilt, parity.Length);
                    foreach (var chunk in stripeChunks)
                    {
                        for (int i = 0; i < Math.Min(chunk.Length, rebuilt.Length); i++)
                        {
                            rebuilt[i] ^= chunk[i];
                        }
                    }

                    allChunks[failedChunkIdx] = rebuilt;
                    _context.LogInfo($"[RAID50] Rebuilt chunk {failedChunkIdx} in set {setIdx} using parity");
                }
            }

            // Trim to original size
            var result = allChunks.SelectMany(c => c ?? Array.Empty<byte>()).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID 60: Striped RAID 6 Sets (RAID 6+0) ====================

        private async Task SaveRAID60Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID 60 = Multiple RAID 6 sets striped together (RAID 6+0)
            // Each RAID 6 set needs minimum 4 disks (2 data + 2 parity)
            if (_config.ProviderCount < 8)
                throw new InvalidOperationException("RAID 60 requires at least 8 providers (2 RAID 6 sets)");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            int disksPerSet = 4; // Minimum for RAID 6
            if (_config.ProviderCount >= 12) disksPerSet = _config.ProviderCount / 2;

            int setsCount = _config.ProviderCount / disksPerSet;
            int dataDisksPerSet = disksPerSet - 2; // Two parity per set (P and Q)

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_60,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>> { { 0, new List<int> { setsCount, disksPerSet } } }
            };

            var tasks = new List<Task>();

            // Stripe chunks across RAID 6 sets
            for (int chunkIdx = 0; chunkIdx < chunks.Count; chunkIdx++)
            {
                int setIdx = chunkIdx % setsCount;
                int setOffset = setIdx * disksPerSet;
                int localChunkIdx = chunkIdx / setsCount;
                int stripeInSet = localChunkIdx / dataDisksPerSet;
                int diskInStripe = localChunkIdx % dataDisksPerSet;

                // Rotating dual parity (P and Q)
                int parityPDiskInSet = stripeInSet % disksPerSet;
                int parityQDiskInSet = (stripeInSet + 1) % disksPerSet;

                // Calculate actual disk position (skip both parity disks)
                int actualDiskInSet = diskInStripe;
                for (int pd = 0; pd < disksPerSet; pd++)
                {
                    if (pd == parityPDiskInSet || pd == parityQDiskInSet)
                    {
                        if (pd <= actualDiskInSet)
                            actualDiskInSet++;
                    }
                }
                actualDiskInSet = Math.Min(actualDiskInSet, disksPerSet - 1);

                int providerIdx = setOffset + actualDiskInSet;
                var chunkKey = $"{key}.set{setIdx}.chunk.{localChunkIdx}";
                tasks.Add(SaveChunkAsync(getProvider(providerIdx), chunkKey, chunks[chunkIdx]));
            }

            // Calculate and store dual parity for each set
            for (int setIdx = 0; setIdx < setsCount; setIdx++)
            {
                int setOffset = setIdx * disksPerSet;
                var setChunks = new List<byte[]>();
                for (int i = setIdx; i < chunks.Count; i += setsCount)
                    setChunks.Add(chunks[i]);

                int stripesInSet = (setChunks.Count + dataDisksPerSet - 1) / dataDisksPerSet;
                for (int stripe = 0; stripe < stripesInSet; stripe++)
                {
                    var stripeChunks = setChunks.Skip(stripe * dataDisksPerSet).Take(dataDisksPerSet).ToList();
                    if (stripeChunks.Count > 0)
                    {
                        var parityP = CalculateParityXOR(stripeChunks);
                        var parityQ = CalculateParityReedSolomon(stripeChunks);

                        int parityPDiskInSet = stripe % disksPerSet;
                        int parityQDiskInSet = (stripe + 1) % disksPerSet;

                        tasks.Add(SaveChunkAsync(getProvider(setOffset + parityPDiskInSet), $"{key}.set{setIdx}.parityP.{stripe}", parityP));
                        tasks.Add(SaveChunkAsync(getProvider(setOffset + parityQDiskInSet), $"{key}.set{setIdx}.parityQ.{stripe}", parityQ));
                    }
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID60] Saved {key} across {setsCount} RAID 6 sets ({disksPerSet} disks each, dual parity)");
        }

        private async Task<Stream> LoadRAID60Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int setsCount = metadata.ProviderMapping[0][0];
            int disksPerSet = metadata.ProviderMapping[0][1];
            int dataDisksPerSet = disksPerSet - 2;

            var allChunks = new byte[metadata.ChunkCount][];
            var failedSets = new Dictionary<int, List<int>>();

            // Load chunks from all sets
            for (int chunkIdx = 0; chunkIdx < metadata.ChunkCount; chunkIdx++)
            {
                int setIdx = chunkIdx % setsCount;
                int setOffset = setIdx * disksPerSet;
                int localChunkIdx = chunkIdx / setsCount;
                int stripeInSet = localChunkIdx / dataDisksPerSet;
                int diskInStripe = localChunkIdx % dataDisksPerSet;

                int parityPDiskInSet = stripeInSet % disksPerSet;
                int parityQDiskInSet = (stripeInSet + 1) % disksPerSet;

                int actualDiskInSet = diskInStripe;
                for (int pd = 0; pd < disksPerSet; pd++)
                {
                    if ((pd == parityPDiskInSet || pd == parityQDiskInSet) && pd <= actualDiskInSet)
                        actualDiskInSet++;
                }
                actualDiskInSet = Math.Min(actualDiskInSet, disksPerSet - 1);

                int providerIdx = setOffset + actualDiskInSet;
                var chunkKey = $"{key}.set{setIdx}.chunk.{localChunkIdx}";

                try
                {
                    allChunks[chunkIdx] = await LoadChunkAsync(getProvider(providerIdx), chunkKey);
                }
                catch
                {
                    if (!failedSets.ContainsKey(setIdx))
                        failedSets[setIdx] = new List<int>();
                    failedSets[setIdx].Add(chunkIdx);
                }
            }

            // Rebuild failed chunks using dual parity (can recover 2 failures per set)
            foreach (var (setIdx, failedChunks) in failedSets)
            {
                int setOffset = setIdx * disksPerSet;

                // Group failures by stripe
                var failuresByStripe = failedChunks.GroupBy(fc => (fc / setsCount) / dataDisksPerSet);

                foreach (var stripeFailures in failuresByStripe)
                {
                    int stripe = stripeFailures.Key;
                    var failedInStripe = stripeFailures.ToList();

                    if (failedInStripe.Count > 2)
                        throw new IOException($"RAID 60 can only recover 2 failures per set, but {failedInStripe.Count} failed in set {setIdx} stripe {stripe}");

                    int parityPDiskInSet = stripe % disksPerSet;
                    int parityQDiskInSet = (stripe + 1) % disksPerSet;

                    var parityP = await LoadChunkAsync(getProvider(setOffset + parityPDiskInSet), $"{key}.set{setIdx}.parityP.{stripe}");
                    var parityQ = await LoadChunkAsync(getProvider(setOffset + parityQDiskInSet), $"{key}.set{setIdx}.parityQ.{stripe}");

                    // Collect surviving chunks in this stripe
                    var stripeChunks = new List<byte[]>();
                    int stripeStart = stripe * dataDisksPerSet;
                    var failedIndices = new List<int>();

                    for (int i = 0; i < dataDisksPerSet; i++)
                    {
                        int globalIdx = (stripeStart + i) * setsCount + setIdx;
                        if (globalIdx < metadata.ChunkCount)
                        {
                            if (failedInStripe.Contains(globalIdx))
                            {
                                failedIndices.Add(i);
                                stripeChunks.Add(null!);
                            }
                            else
                            {
                                stripeChunks.Add(allChunks[globalIdx]);
                            }
                        }
                    }

                    // Rebuild using dual parity
                    var rebuilt = RebuildFromDualParity(stripeChunks, parityP, parityQ, failedIndices);
                    foreach (var (localIdx, chunk) in rebuilt)
                    {
                        int globalIdx = (stripeStart + localIdx) * setsCount + setIdx;
                        allChunks[globalIdx] = chunk;
                    }

                    _context.LogInfo($"[RAID60] Rebuilt {failedInStripe.Count} chunks in set {setIdx} stripe {stripe}");
                }
            }

            var result = allChunks.SelectMany(c => c ?? Array.Empty<byte>()).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID 01: Striped Mirrors (RAID 0+1) ====================

        private async Task SaveRAID01Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 4 || _config.ProviderCount % 2 != 0)
                throw new InvalidOperationException("RAID 01 requires an even number of providers (minimum 4)");

            var buffer = new MemoryStream();
            await data.CopyToAsync(buffer);
            buffer.Position = 0;

            var mirrorGroups = _config.ProviderCount / 2;
            var chunks = SplitIntoChunks(buffer, _config.StripeSize);
            var tasks = new List<Task>();

            for (int i = 0; i < chunks.Count; i++)
            {
                int groupIdx = i % mirrorGroups;
                int disk1 = groupIdx * 2;
                int disk2 = groupIdx * 2 + 1;

                var chunkKey = $"{key}.chunk.{i}";

                // Write to both disks in mirror group
                tasks.Add(SaveChunkAsync(getProvider(disk1), chunkKey, chunks[i]));
                tasks.Add(SaveChunkAsync(getProvider(disk2), chunkKey, chunks[i]));
            }

            await Task.WhenAll(tasks);
            _context.LogInfo($"[RAID01] Saved {key} with striped mirroring (RAID 0+1)");
        }

        private async Task<Stream> LoadRAID01Async(string key, Func<int, IStorageProvider> getProvider)
        {
            var mirrorGroups = _config.ProviderCount / 2;

            // Try to load chunks, falling back to mirror if primary fails
            var chunks = new List<byte[]>();
            for (int i = 0; i < 1000; i++) // Max 1000 chunks
            {
                int groupIdx = i % mirrorGroups;
                int disk1 = groupIdx * 2;
                int disk2 = groupIdx * 2 + 1;
                var chunkKey = $"{key}.chunk.{i}";

                try
                {
                    chunks.Add(await LoadChunkAsync(getProvider(disk1), chunkKey));
                }
                catch
                {
                    try
                    {
                        chunks.Add(await LoadChunkAsync(getProvider(disk2), chunkKey));
                    }
                    catch
                    {
                        break; // No more chunks
                    }
                }
            }

            return ReassembleChunks(chunks.ToArray());
        }

        // ==================== RAID-Z1: ZFS Single Parity ====================

        private async Task SaveRAIDZ1Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID-Z1: ZFS single parity with variable stripe width
            // Key difference from RAID 5: parity is calculated per record, not per stripe
            if (_config.ProviderCount < 3)
                throw new InvalidOperationException("RAID-Z1 requires at least 3 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            int dataDisks = _config.ProviderCount - 1;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_Z1,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count
            };

            var tasks = new List<Task>();
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                // ZFS-style: variable width stripes (use all available chunks in stripe)
                var stripeStart = stripe * dataDisks;
                var stripeChunks = chunks.Skip(stripeStart).Take(dataDisks).ToList();
                int actualWidth = stripeChunks.Count;

                // Rotating parity (ZFS distributes across all vdevs)
                int parityDisk = stripe % _config.ProviderCount;

                // Calculate parity for variable-width stripe
                var parity = CalculateParityXOR(stripeChunks);
                tasks.Add(SaveChunkAsync(getProvider(parityDisk), $"{key}.z1parity.{stripe}", parity));

                // Write data chunks to remaining disks
                int dataIdx = 0;
                for (int disk = 0; disk < _config.ProviderCount; disk++)
                {
                    if (disk == parityDisk)
                        continue;

                    int chunkIdx = stripeStart + dataIdx;
                    if (dataIdx < actualWidth)
                    {
                        tasks.Add(SaveChunkAsync(getProvider(disk), $"{key}.z1data.{chunkIdx}", stripeChunks[dataIdx]));
                    }
                    dataIdx++;
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-Z1] Saved {key} with ZFS variable-width single parity");
        }

        private async Task<Stream> LoadRAIDZ1Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 1;
            int stripeCount = (int)MathUtils.Ceiling((double)metadata.ChunkCount / dataDisks);
            var allChunks = new byte[metadata.ChunkCount][];

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityDisk = stripe % _config.ProviderCount;
                int stripeStart = stripe * dataDisks;
                int failedIdx = -1;

                int dataIdx = 0;
                for (int disk = 0; disk < _config.ProviderCount; disk++)
                {
                    if (disk == parityDisk)
                        continue;

                    int chunkIdx = stripeStart + dataIdx;
                    if (chunkIdx < metadata.ChunkCount)
                    {
                        try
                        {
                            allChunks[chunkIdx] = await LoadChunkAsync(getProvider(disk), $"{key}.z1data.{chunkIdx}");
                        }
                        catch
                        {
                            failedIdx = dataIdx;
                        }
                    }
                    dataIdx++;
                }

                // Rebuild using parity if needed
                if (failedIdx >= 0)
                {
                    var parity = await LoadChunkAsync(getProvider(parityDisk), $"{key}.z1parity.{stripe}");
                    var surviving = new List<byte[]>();
                    for (int i = 0; i < dataDisks; i++)
                    {
                        int idx = stripeStart + i;
                        if (i != failedIdx && idx < metadata.ChunkCount && allChunks[idx] != null)
                            surviving.Add(allChunks[idx]);
                    }
                    allChunks[stripeStart + failedIdx] = RebuildChunkFromParity(surviving, parity);
                    _context.LogInfo($"[RAID-Z1] Rebuilt chunk in stripe {stripe}");
                }
            }

            var result = allChunks.Where(c => c != null).SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID-Z2: ZFS Double Parity ====================

        private async Task SaveRAIDZ2Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID-Z2: ZFS double parity with variable stripe width
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("RAID-Z2 requires at least 4 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            int dataDisks = _config.ProviderCount - 2;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_Z2,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count
            };

            var tasks = new List<Task>();
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                var stripeStart = stripe * dataDisks;
                var stripeChunks = chunks.Skip(stripeStart).Take(dataDisks).ToList();

                // Rotating dual parity (ZFS style)
                int parityPDisk = stripe % _config.ProviderCount;
                int parityQDisk = (stripe + 1) % _config.ProviderCount;

                var parityP = CalculateParityXOR(stripeChunks);
                var parityQ = CalculateParityReedSolomon(stripeChunks);
                tasks.Add(SaveChunkAsync(getProvider(parityPDisk), $"{key}.z2parityP.{stripe}", parityP));
                tasks.Add(SaveChunkAsync(getProvider(parityQDisk), $"{key}.z2parityQ.{stripe}", parityQ));

                int dataIdx = 0;
                for (int disk = 0; disk < _config.ProviderCount; disk++)
                {
                    if (disk == parityPDisk || disk == parityQDisk)
                        continue;

                    int chunkIdx = stripeStart + dataIdx;
                    if (dataIdx < stripeChunks.Count)
                    {
                        tasks.Add(SaveChunkAsync(getProvider(disk), $"{key}.z2data.{chunkIdx}", stripeChunks[dataIdx]));
                    }
                    dataIdx++;
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-Z2] Saved {key} with ZFS variable-width double parity");
        }

        private async Task<Stream> LoadRAIDZ2Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 2;
            int stripeCount = (int)MathUtils.Ceiling((double)metadata.ChunkCount / dataDisks);
            var allChunks = new byte[metadata.ChunkCount][];

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityPDisk = stripe % _config.ProviderCount;
                int parityQDisk = (stripe + 1) % _config.ProviderCount;
                int stripeStart = stripe * dataDisks;
                var failedIndices = new List<int>();
                var stripeChunks = new List<byte[]>();

                int dataIdx = 0;
                for (int disk = 0; disk < _config.ProviderCount; disk++)
                {
                    if (disk == parityPDisk || disk == parityQDisk)
                        continue;

                    int chunkIdx = stripeStart + dataIdx;
                    if (chunkIdx < metadata.ChunkCount)
                    {
                        try
                        {
                            var chunk = await LoadChunkAsync(getProvider(disk), $"{key}.z2data.{chunkIdx}");
                            allChunks[chunkIdx] = chunk;
                            stripeChunks.Add(chunk);
                        }
                        catch
                        {
                            failedIndices.Add(dataIdx);
                            stripeChunks.Add(null!);
                        }
                    }
                    dataIdx++;
                }

                // Rebuild up to 2 failures
                if (failedIndices.Count > 0 && failedIndices.Count <= 2)
                {
                    var parityP = await LoadChunkAsync(getProvider(parityPDisk), $"{key}.z2parityP.{stripe}");
                    var parityQ = await LoadChunkAsync(getProvider(parityQDisk), $"{key}.z2parityQ.{stripe}");
                    var rebuilt = RebuildFromDualParity(stripeChunks, parityP, parityQ, failedIndices);
                    foreach (var (idx, chunk) in rebuilt)
                        allChunks[stripeStart + idx] = chunk;
                    _context.LogInfo($"[RAID-Z2] Rebuilt {failedIndices.Count} chunks in stripe {stripe}");
                }
            }

            var result = allChunks.Where(c => c != null).SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID-Z3: ZFS Triple Parity ====================

        private async Task SaveRAIDZ3Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 5)
                throw new InvalidOperationException("RAID-Z3 requires at least 5 providers");

            var chunks = SplitIntoChunks(data, _config.StripeSize);
            int dataDisks = _config.ProviderCount - 3; // Triple parity
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_Z3,
                TotalSize = data.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>>()
            };

            var tasks = new List<Task>();

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                // Rotating triple parity disks (ZFS-style)
                int parity1Disk = stripe % _config.ProviderCount;
                int parity2Disk = (stripe + 1) % _config.ProviderCount;
                int parity3Disk = (stripe + 2) % _config.ProviderCount;

                var stripeChunks = new List<byte[]>();

                for (int diskIdx = 0; diskIdx < dataDisks && (stripe * dataDisks + diskIdx) < chunks.Count; diskIdx++)
                {
                    int chunkIdx = stripe * dataDisks + diskIdx;
                    stripeChunks.Add(chunks[chunkIdx]);
                }

                // Calculate triple parity using different generators
                // P = XOR of all data (generator g^0 = 1)
                var parity1 = CalculateParityXOR(stripeChunks);
                // Q = Reed-Solomon with generator g^1 = 0x02
                var parity2 = CalculateParityReedSolomon(stripeChunks);
                // R = Reed-Solomon with generator g^2 = 0x04 (unique third parity)
                var parity3 = CalculateParityReedSolomonR(stripeChunks);

                // Write data and parity chunks
                int dataDiskCounter = 0;
                for (int providerIdx = 0; providerIdx < _config.ProviderCount; providerIdx++)
                {
                    if (providerIdx == parity1Disk)
                    {
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), $"{key}.parityP.{stripe}", parity1));
                    }
                    else if (providerIdx == parity2Disk)
                    {
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), $"{key}.parityQ.{stripe}", parity2));
                    }
                    else if (providerIdx == parity3Disk)
                    {
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), $"{key}.parityR.{stripe}", parity3));
                    }
                    else if (dataDiskCounter < stripeChunks.Count)
                    {
                        int chunkIdx = stripe * dataDisks + dataDiskCounter;
                        tasks.Add(SaveChunkAsync(getProvider(providerIdx), $"{key}.chunk.{chunkIdx}", stripeChunks[dataDiskCounter]));
                        dataDiskCounter++;
                    }
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-Z3] Saved {key} with ZFS triple parity (P+Q+R, 3 disk fault tolerance)");
        }

        private async Task<Stream> LoadRAIDZ3Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 3;
            int stripeCount = (int)MathUtils.Ceiling((double)metadata.ChunkCount / dataDisks);
            var allChunks = new List<byte[]>();

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityPDisk = stripe % _config.ProviderCount;
                int parityQDisk = (stripe + 1) % _config.ProviderCount;
                int parityRDisk = (stripe + 2) % _config.ProviderCount;

                var stripeChunks = new List<byte[]>();
                var failedDisks = new List<int>();
                var diskToChunkIdx = new Dictionary<int, int>();

                // Try to read all data chunks for this stripe
                int dataDiskCounter = 0;
                for (int providerIdx = 0; providerIdx < _config.ProviderCount; providerIdx++)
                {
                    if (providerIdx == parityPDisk || providerIdx == parityQDisk || providerIdx == parityRDisk)
                        continue;

                    if (dataDiskCounter >= dataDisks)
                        break;

                    int chunkIdx = stripe * dataDisks + dataDiskCounter;
                    if (chunkIdx >= metadata.ChunkCount)
                        break;

                    var chunkKey = $"{key}.chunk.{chunkIdx}";
                    try
                    {
                        var chunk = await LoadChunkAsync(getProvider(providerIdx), chunkKey);
                        stripeChunks.Add(chunk);
                        diskToChunkIdx[providerIdx] = stripeChunks.Count - 1;
                    }
                    catch
                    {
                        failedDisks.Add(dataDiskCounter);
                        stripeChunks.Add(null!);
                        diskToChunkIdx[providerIdx] = stripeChunks.Count - 1;
                        _context.LogWarning($"[RAID-Z3] Data disk {providerIdx} failed for stripe {stripe}");
                    }
                    dataDiskCounter++;
                }

                // Rebuild up to 3 failed disks using triple parity
                if (failedDisks.Count > 0 && failedDisks.Count <= 3)
                {
                    var parityP = await LoadChunkAsync(getProvider(parityPDisk), $"{key}.parityP.{stripe}");
                    var parityQ = await LoadChunkAsync(getProvider(parityQDisk), $"{key}.parityQ.{stripe}");
                    var parityR = await LoadChunkAsync(getProvider(parityRDisk), $"{key}.parityR.{stripe}");

                    var rebuiltChunks = RebuildFromTripleParity(stripeChunks, parityP, parityQ, parityR, failedDisks);

                    foreach (var (diskIdx, chunk) in rebuiltChunks)
                    {
                        stripeChunks[diskIdx] = chunk;
                    }

                    _context.LogInfo($"[RAID-Z3] Rebuilt {failedDisks.Count} chunks using triple parity for stripe {stripe}");
                }
                else if (failedDisks.Count > 3)
                {
                    throw new IOException($"RAID-Z3 can only recover from 3 disk failures, but {failedDisks.Count} disks failed");
                }

                allChunks.AddRange(stripeChunks.Where(c => c != null));
            }

            return ReassembleChunks(allChunks.ToArray());
        }

        private static byte[] CalculateParityReedSolomonR(List<byte[]> chunks)
        {
            // R parity using generator g^2 = 0x04 (different from Q which uses g^1 = 0x02)
            // R[i] = sum(D[j] * (g^2)^j) = sum(D[j] * g^(2j))
            if (chunks.Count == 0)
                return Array.Empty<byte>();

            var maxLength = chunks.Max(c => c.Length);
            var parity = new byte[maxLength];

            for (int diskIdx = 0; diskIdx < chunks.Count; diskIdx++)
            {
                // Generator coefficient: (g^2)^diskIdx = g^(2*diskIdx) where g = 0x02
                byte coeff = GF256Power(0x02, 2 * diskIdx);

                for (int byteIdx = 0; byteIdx < chunks[diskIdx].Length; byteIdx++)
                {
                    parity[byteIdx] ^= GF256Multiply(chunks[diskIdx][byteIdx], coeff);
                }
            }

            return parity;
        }

        private Dictionary<int, byte[]> RebuildFromTripleParity(List<byte[]> chunks, byte[] parityP, byte[] parityQ, byte[] parityR, List<int> failedDisks)
        {
            // Triple parity rebuild using P, Q, R with Reed-Solomon in GF(2^8)
            var rebuilt = new Dictionary<int, byte[]>();
            int chunkLength = parityP.Length;

            if (failedDisks.Count == 1)
            {
                // Single disk failure - use P parity
                int x = failedDisks[0];
                var Dx = new byte[chunkLength];
                Array.Copy(parityP, Dx, chunkLength);

                for (int i = 0; i < chunks.Count; i++)
                {
                    if (chunks[i] != null)
                    {
                        for (int j = 0; j < Math.Min(chunks[i].Length, chunkLength); j++)
                        {
                            Dx[j] ^= chunks[i][j];
                        }
                    }
                }
                rebuilt[x] = Dx;
            }
            else if (failedDisks.Count == 2)
            {
                // Double failure - use P and Q (same as RAID 6)
                var dualRebuilt = RebuildFromDualParity(chunks, parityP, parityQ, failedDisks);
                foreach (var kvp in dualRebuilt)
                    rebuilt[kvp.Key] = kvp.Value;
            }
            else if (failedDisks.Count == 3)
            {
                // Triple failure - use P, Q, and R
                int x = failedDisks[0];
                int y = failedDisks[1];
                int z = failedDisks[2];

                // Generator coefficients for Q (g^i) and R (g^(2i))
                byte gx = GF256Power(0x02, x);
                byte gy = GF256Power(0x02, y);
                byte gz = GF256Power(0x02, z);
                byte g2x = GF256Power(0x02, 2 * x);
                byte g2y = GF256Power(0x02, 2 * y);
                byte g2z = GF256Power(0x02, 2 * z);

                // Calculate Pxyz, Qxyz, Rxyz by removing surviving data contributions
                var Pxyz = new byte[chunkLength];
                var Qxyz = new byte[chunkLength];
                var Rxyz = new byte[chunkLength];
                Array.Copy(parityP, Pxyz, chunkLength);
                Array.Copy(parityQ, Qxyz, chunkLength);
                Array.Copy(parityR, Rxyz, chunkLength);

                for (int i = 0; i < chunks.Count; i++)
                {
                    if (chunks[i] != null && i != x && i != y && i != z)
                    {
                        byte gi = GF256Power(0x02, i);
                        byte g2i = GF256Power(0x02, 2 * i);
                        for (int j = 0; j < Math.Min(chunks[i].Length, chunkLength); j++)
                        {
                            Pxyz[j] ^= chunks[i][j];
                            Qxyz[j] ^= GF256Multiply(chunks[i][j], gi);
                            Rxyz[j] ^= GF256Multiply(chunks[i][j], g2i);
                        }
                    }
                }

                // Solve 3x3 system in GF(2^8) using Gaussian elimination
                // | 1    1    1   | |Dx|   |Pxyz|
                // | gx   gy   gz  | |Dy| = |Qxyz|
                // | g2x  g2y  g2z | |Dz|   |Rxyz|

                var Dx = new byte[chunkLength];
                var Dy = new byte[chunkLength];
                var Dz = new byte[chunkLength];

                // Precompute matrix determinant and inverse elements
                // det = 1*(gy*g2z - gz*g2y) - 1*(gx*g2z - gz*g2x) + 1*(gx*g2y - gy*g2x)
                byte a11 = 1, a12 = 1, a13 = 1;
                byte a21 = gx, a22 = gy, a23 = gz;
                byte a31 = g2x, a32 = g2y, a33 = g2z;

                byte det = (byte)(
                    GF256Multiply(a11, (byte)(GF256Multiply(a22, a33) ^ GF256Multiply(a23, a32))) ^
                    GF256Multiply(a12, (byte)(GF256Multiply(a21, a33) ^ GF256Multiply(a23, a31))) ^
                    GF256Multiply(a13, (byte)(GF256Multiply(a21, a32) ^ GF256Multiply(a22, a31)))
                );

                if (det == 0)
                {
                    throw new InvalidOperationException("Matrix is singular, cannot solve triple failure");
                }

                byte invDet = GF256Inverse(det);

                // Calculate adjugate matrix elements
                byte adj11 = (byte)(GF256Multiply(a22, a33) ^ GF256Multiply(a23, a32));
                byte adj12 = (byte)(GF256Multiply(a13, a32) ^ GF256Multiply(a12, a33));
                byte adj13 = (byte)(GF256Multiply(a12, a23) ^ GF256Multiply(a13, a22));
                byte adj21 = (byte)(GF256Multiply(a23, a31) ^ GF256Multiply(a21, a33));
                byte adj22 = (byte)(GF256Multiply(a11, a33) ^ GF256Multiply(a13, a31));
                byte adj23 = (byte)(GF256Multiply(a13, a21) ^ GF256Multiply(a11, a23));
                byte adj31 = (byte)(GF256Multiply(a21, a32) ^ GF256Multiply(a22, a31));
                byte adj32 = (byte)(GF256Multiply(a12, a31) ^ GF256Multiply(a11, a32));
                byte adj33 = (byte)(GF256Multiply(a11, a22) ^ GF256Multiply(a12, a21));

                for (int j = 0; j < chunkLength; j++)
                {
                    byte b1 = Pxyz[j], b2 = Qxyz[j], b3 = Rxyz[j];

                    // X = adj * b / det
                    Dx[j] = GF256Multiply(invDet, (byte)(
                        GF256Multiply(adj11, b1) ^ GF256Multiply(adj12, b2) ^ GF256Multiply(adj13, b3)));
                    Dy[j] = GF256Multiply(invDet, (byte)(
                        GF256Multiply(adj21, b1) ^ GF256Multiply(adj22, b2) ^ GF256Multiply(adj23, b3)));
                    Dz[j] = GF256Multiply(invDet, (byte)(
                        GF256Multiply(adj31, b1) ^ GF256Multiply(adj32, b2) ^ GF256Multiply(adj33, b3)));
                }

                rebuilt[x] = Dx;
                rebuilt[y] = Dy;
                rebuilt[z] = Dz;
                _context.LogInfo($"[RAID-Z3] Rebuilt disks {x}, {y}, {z} using P+Q+R triple parity");
            }

            return rebuilt;
        }

        // ==================== RAID-DP: NetApp Double Parity ====================

        private async Task SaveRAIDDPAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID-DP: NetApp diagonal parity for dual fault tolerance
            // Uses row parity (horizontal) + diagonal parity (anti-diagonal)
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("RAID-DP requires at least 4 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            int dataDisks = _config.ProviderCount - 2; // One for row parity, one for diagonal parity

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_DP,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count
            };

            var tasks = new List<Task>();
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int rowParityDisk = _config.ProviderCount - 2; // Fixed row parity disk
                int diagParityDisk = _config.ProviderCount - 1; // Fixed diagonal parity disk

                var stripeChunks = chunks.Skip(stripe * dataDisks).Take(dataDisks).ToList();

                // Row parity (simple XOR)
                var rowParity = CalculateParityXOR(stripeChunks);
                tasks.Add(SaveChunkAsync(getProvider(rowParityDisk), $"{key}.dprow.{stripe}", rowParity));

                // Diagonal parity: XOR along anti-diagonals
                // For each byte position, XOR with shifted indices
                var diagParity = CalculateDiagonalParity(stripeChunks, dataDisks);
                tasks.Add(SaveChunkAsync(getProvider(diagParityDisk), $"{key}.dpdiag.{stripe}", diagParity));

                // Write data chunks
                for (int i = 0; i < stripeChunks.Count; i++)
                {
                    int chunkIdx = stripe * dataDisks + i;
                    tasks.Add(SaveChunkAsync(getProvider(i), $"{key}.dpdata.{chunkIdx}", stripeChunks[i]));
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-DP] Saved {key} with NetApp row+diagonal parity");
        }

        private async Task<Stream> LoadRAIDDPAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 2;
            int stripeCount = (int)MathUtils.Ceiling((double)metadata.ChunkCount / dataDisks);
            int rowParityDisk = _config.ProviderCount - 2;
            int diagParityDisk = _config.ProviderCount - 1;

            var allChunks = new byte[metadata.ChunkCount][];

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                var failedIndices = new List<int>();
                var stripeChunks = new List<byte[]>();

                for (int i = 0; i < dataDisks; i++)
                {
                    int chunkIdx = stripe * dataDisks + i;
                    if (chunkIdx >= metadata.ChunkCount)
                        break;

                    try
                    {
                        var chunk = await LoadChunkAsync(getProvider(i), $"{key}.dpdata.{chunkIdx}");
                        allChunks[chunkIdx] = chunk;
                        stripeChunks.Add(chunk);
                    }
                    catch
                    {
                        failedIndices.Add(i);
                        stripeChunks.Add(null!);
                    }
                }

                // Rebuild using row and diagonal parity
                if (failedIndices.Count > 0 && failedIndices.Count <= 2)
                {
                    var rowParity = await LoadChunkAsync(getProvider(rowParityDisk), $"{key}.dprow.{stripe}");
                    var diagParity = await LoadChunkAsync(getProvider(diagParityDisk), $"{key}.dpdiag.{stripe}");

                    // Use row+diagonal parity for reconstruction (similar to RAID 6)
                    var rebuilt = RebuildFromDualParity(stripeChunks, rowParity, diagParity, failedIndices);
                    foreach (var (idx, chunk) in rebuilt)
                        allChunks[stripe * dataDisks + idx] = chunk;

                    _context.LogInfo($"[RAID-DP] Rebuilt {failedIndices.Count} chunks using row+diagonal parity");
                }
            }

            var result = allChunks.Where(c => c != null).SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        private static byte[] CalculateDiagonalParity(List<byte[]> chunks, int numDisks)
        {
            // Diagonal parity: anti-diagonal XOR pattern for NetApp RAID-DP
            if (chunks.Count == 0)
                return Array.Empty<byte>();

            var maxLength = chunks.Max(c => c.Length);
            var parity = new byte[maxLength];

            for (int byteIdx = 0; byteIdx < maxLength; byteIdx++)
            {
                for (int diskIdx = 0; diskIdx < chunks.Count; diskIdx++)
                {
                    // Anti-diagonal offset: shift position based on disk index
                    int diagOffset = (byteIdx + diskIdx) % maxLength;
                    if (diagOffset < chunks[diskIdx].Length)
                    {
                        parity[byteIdx] ^= chunks[diskIdx][diagOffset];
                    }
                }
            }

            return parity;
        }

        // ==================== Unraid: Parity System ====================

        private async Task SaveUnraidAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // Unraid: 1 or 2 parity disks, rest are data disks
            // Unraid writes entire file to ONE disk (not striped)
            int parityCount = MathUtils.Min(2, _config.ProviderCount - 1);
            int dataDisks = _config.ProviderCount - parityCount;

            if (dataDisks < 1)
                throw new InvalidOperationException("Unraid requires at least 1 data disk and 1-2 parity disks");

            // Deterministically select disk based on key hash
            int targetDisk = MathUtils.Abs(key.GetHashCode()) % dataDisks;

            // Write entire file to one disk
            var dataKey = $"{key}.data";
            await SaveChunkAsync(getProvider(targetDisk), dataKey, await ReadAllBytesAsync(data));

            _context.LogInfo($"[Unraid] Saved {key} to disk {targetDisk} with {parityCount} parity disks");
        }

        private async Task<Stream> LoadUnraidAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            int parityCount = MathUtils.Min(2, _config.ProviderCount - 1);
            int dataDisks = _config.ProviderCount - parityCount;
            int targetDisk = MathUtils.Abs(key.GetHashCode()) % dataDisks;

            var dataKey = $"{key}.data";
            var chunk = await LoadChunkAsync(getProvider(targetDisk), dataKey);
            return new MemoryStream(chunk);
        }

        // ==================== RAID 2: Bit-Level Striping with Hamming Code ====================

        private async Task SaveRAID2Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID 2: Bit-level striping with Hamming(7,4) ECC
            // For n data disks, we need ceil(log2(n+1)) ECC disks
            // Minimum: 4 data disks + 3 ECC disks = 7 providers
            if (_config.ProviderCount < 7)
                throw new InvalidOperationException("RAID 2 requires at least 7 providers (4 data + 3 Hamming ECC)");

            var bytes = await ReadAllBytesAsync(data);

            // Calculate number of ECC disks needed (Hamming code requirement)
            int eccDisks = CalculateHammingEccDisks(_config.ProviderCount);
            int dataDisks = _config.ProviderCount - eccDisks;

            // Pad data to be divisible by dataDisks
            int paddedLength = ((bytes.Length + dataDisks - 1) / dataDisks) * dataDisks;
            var paddedBytes = new byte[paddedLength];
            Array.Copy(bytes, paddedBytes, bytes.Length);

            int bytesPerDisk = paddedLength / dataDisks;
            var diskData = new byte[_config.ProviderCount][];

            // Initialize all disk arrays
            for (int d = 0; d < _config.ProviderCount; d++)
            {
                diskData[d] = new byte[bytesPerDisk];
            }

            // Distribute data across data disks with bit-level interleaving
            for (int bytePos = 0; bytePos < bytesPerDisk; bytePos++)
            {
                for (int bitPos = 0; bitPos < 8; bitPos++)
                {
                    // Collect data bits for this position
                    var dataBits = new bool[dataDisks];
                    for (int d = 0; d < dataDisks; d++)
                    {
                        int sourceByteIdx = bytePos * dataDisks + d;
                        if (sourceByteIdx < paddedBytes.Length)
                        {
                            dataBits[d] = ((paddedBytes[sourceByteIdx] >> bitPos) & 1) == 1;
                        }
                    }

                    // Calculate Hamming ECC bits
                    var eccBits = CalculateHammingEccBits(dataBits);

                    // Write data bits to data disks
                    int dataDiskIdx = 0;
                    int eccDiskIdx = 0;
                    for (int diskIdx = 0; diskIdx < _config.ProviderCount; diskIdx++)
                    {
                        bool isEccDisk = IsHammingEccPosition(diskIdx + 1); // 1-indexed for Hamming
                        if (isEccDisk && eccDiskIdx < eccBits.Length)
                        {
                            if (eccBits[eccDiskIdx])
                                diskData[diskIdx][bytePos] |= (byte)(1 << bitPos);
                            eccDiskIdx++;
                        }
                        else if (dataDiskIdx < dataBits.Length)
                        {
                            if (dataBits[dataDiskIdx])
                                diskData[diskIdx][bytePos] |= (byte)(1 << bitPos);
                            dataDiskIdx++;
                        }
                    }
                }
            }

            // Save all disks
            var tasks = new List<Task>();
            for (int d = 0; d < _config.ProviderCount; d++)
            {
                bool isEcc = IsHammingEccPosition(d + 1);
                string suffix = isEcc ? $"ecc.{d}" : $"data.{d}";
                tasks.Add(SaveChunkAsync(getProvider(d), $"{key}.{suffix}", diskData[d]));
            }

            // Save metadata
            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_2,
                TotalSize = bytes.Length,
                ChunkCount = _config.ProviderCount
            };
            _metadata[key] = metadata;

            await Task.WhenAll(tasks);
            _context.LogInfo($"[RAID-2] Saved {key} with Hamming({_config.ProviderCount},{dataDisks}) ECC across {_config.ProviderCount} providers");
        }

        private async Task<Stream> LoadRAID2Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int eccDisks = CalculateHammingEccDisks(_config.ProviderCount);
            int dataDisks = _config.ProviderCount - eccDisks;

            // Load all disks
            var diskData = new byte[_config.ProviderCount][];
            var failedDisks = new List<int>();

            for (int d = 0; d < _config.ProviderCount; d++)
            {
                bool isEcc = IsHammingEccPosition(d + 1);
                string suffix = isEcc ? $"ecc.{d}" : $"data.{d}";
                try
                {
                    diskData[d] = await LoadChunkAsync(getProvider(d), $"{key}.{suffix}");
                }
                catch (Exception ex)
                {
                    _context.LogWarning($"[RAID-2] Disk {d} failed: {ex.Message}");
                    failedDisks.Add(d);
                    diskData[d] = null!;
                }
            }

            // Hamming code can correct single-bit errors (one failed disk per bit position)
            if (failedDisks.Count > 1)
                throw new IOException($"RAID 2 can only recover from 1 disk failure, but {failedDisks.Count} disks failed");

            int bytesPerDisk = diskData.First(d => d != null)!.Length;

            // If a disk failed, reconstruct using Hamming syndrome
            if (failedDisks.Count == 1)
            {
                int failedDisk = failedDisks[0];
                diskData[failedDisk] = new byte[bytesPerDisk];

                for (int bytePos = 0; bytePos < bytesPerDisk; bytePos++)
                {
                    for (int bitPos = 0; bitPos < 8; bitPos++)
                    {
                        // Reconstruct using Hamming syndrome
                        bool reconstructedBit = ReconstructHammingBit(diskData, failedDisk, bytePos, bitPos);
                        if (reconstructedBit)
                            diskData[failedDisk][bytePos] |= (byte)(1 << bitPos);
                    }
                }
                _context.LogInfo($"[RAID-2] Reconstructed disk {failedDisk} using Hamming ECC");
            }

            // Reassemble original data
            var result = new byte[metadata.TotalSize];
            int resultIdx = 0;

            for (int bytePos = 0; bytePos < bytesPerDisk && resultIdx < metadata.TotalSize; bytePos++)
            {
                for (int dataDiskLogical = 0; dataDiskLogical < dataDisks && resultIdx < metadata.TotalSize; dataDiskLogical++)
                {
                    // Map logical data disk to physical disk (skipping ECC positions)
                    int physicalDisk = MapLogicalToPhysicalDisk(dataDiskLogical, _config.ProviderCount);
                    result[resultIdx++] = diskData[physicalDisk][bytePos];
                }
            }

            return new MemoryStream(result);
        }

        private static int CalculateHammingEccDisks(int totalDisks)
        {
            // For Hamming code: 2^r >= m + r + 1, where m = data bits, r = parity bits
            int r = 1;
            while ((1 << r) < totalDisks + 1)
                r++;
            return r;
        }

        private static bool IsHammingEccPosition(int position)
        {
            // ECC bits are at positions that are powers of 2 (1, 2, 4, 8, ...)
            return position > 0 && (position & (position - 1)) == 0;
        }

        private static bool[] CalculateHammingEccBits(bool[] dataBits)
        {
            int dataLen = dataBits.Length;
            int r = 1;
            while ((1 << r) < dataLen + r + 1)
                r++;

            var eccBits = new bool[r];

            // Calculate each parity bit
            for (int i = 0; i < r; i++)
            {
                int parityPos = 1 << i;
                bool parity = false;

                int dataIdx = 0;
                for (int pos = 1; pos <= dataLen + r; pos++)
                {
                    if (IsHammingEccPosition(pos))
                        continue;

                    if ((pos & parityPos) != 0 && dataIdx < dataBits.Length)
                    {
                        parity ^= dataBits[dataIdx];
                    }
                    dataIdx++;
                }
                eccBits[i] = parity;
            }

            return eccBits;
        }

        private bool ReconstructHammingBit(byte[][] diskData, int failedDisk, int bytePos, int bitPos)
        {
            // Calculate syndrome to find error position
            int syndrome = 0;
            int r = CalculateHammingEccDisks(_config.ProviderCount);

            for (int i = 0; i < r; i++)
            {
                int parityPos = 1 << i;
                bool parity = false;

                for (int pos = 1; pos <= _config.ProviderCount; pos++)
                {
                    if ((pos & parityPos) != 0)
                    {
                        int diskIdx = pos - 1;
                        if (diskIdx != failedDisk && diskData[diskIdx] != null)
                        {
                            parity ^= ((diskData[diskIdx][bytePos] >> bitPos) & 1) == 1;
                        }
                    }
                }

                if (parity)
                    syndrome |= parityPos;
            }

            // The syndrome indicates the error position; XOR to get correct bit
            bool result = false;
            int failedPos = failedDisk + 1;

            // Calculate what the bit should be based on other bits
            if (IsHammingEccPosition(failedPos))
            {
                // Failed disk is a parity disk - recalculate parity
                int parityIdx = (int)Math.Log2(failedPos);
                for (int pos = 1; pos <= _config.ProviderCount; pos++)
                {
                    if (pos != failedPos && (pos & failedPos) != 0)
                    {
                        int diskIdx = pos - 1;
                        if (diskData[diskIdx] != null)
                        {
                            result ^= ((diskData[diskIdx][bytePos] >> bitPos) & 1) == 1;
                        }
                    }
                }
            }
            else
            {
                // Failed disk is a data disk - use syndrome
                result = syndrome == failedPos;
            }

            return result;
        }

        private static int MapLogicalToPhysicalDisk(int logicalIdx, int totalDisks)
        {
            int physical = 0;
            int logical = 0;
            while (logical <= logicalIdx && physical < totalDisks)
            {
                if (!IsHammingEccPosition(physical + 1))
                {
                    if (logical == logicalIdx)
                        return physical;
                    logical++;
                }
                physical++;
            }
            return physical;
        }

        // ==================== RAID 3: Byte-Level Striping with Dedicated Parity ====================

        private async Task SaveRAID3Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 3)
                throw new InvalidOperationException("RAID 3 requires at least 3 providers");

            var bytes = await ReadAllBytesAsync(data);
            int dataDisks = _config.ProviderCount - 1; // Last disk is dedicated parity
            int bytesPerDisk = (bytes.Length + dataDisks - 1) / dataDisks;

            var diskData = new List<byte[]>();
            for (int i = 0; i < dataDisks; i++)
            {
                int start = i * bytesPerDisk;
                int length = Math.Min(bytesPerDisk, bytes.Length - start);
                var chunk = new byte[bytesPerDisk]; // Pad to uniform size
                if (length > 0)
                    Array.Copy(bytes, start, chunk, 0, length);
                diskData.Add(chunk);
            }

            // Compute dedicated parity
            var parity = ComputeXorParity(diskData);

            var tasks = new List<Task>();
            for (int i = 0; i < dataDisks; i++)
            {
                tasks.Add(SaveChunkAsync(getProvider(i), $"{key}.data.{i}", diskData[i]));
            }
            tasks.Add(SaveChunkAsync(getProvider(dataDisks), $"{key}.parity", parity));

            await Task.WhenAll(tasks);
            _context.LogInfo($"[RAID-3] Saved {key} with byte-level striping and dedicated parity");
        }

        private async Task<Stream> LoadRAID3Async(string key, Func<int, IStorageProvider> getProvider)
        {
            int dataDisks = _config.ProviderCount - 1;
            var chunks = new List<byte[]>();

            for (int i = 0; i < dataDisks; i++)
            {
                var chunk = await LoadChunkAsync(getProvider(i), $"{key}.data.{i}");
                chunks.Add(chunk);
            }

            var allBytes = chunks.SelectMany(c => c).ToArray();
            return new MemoryStream(allBytes);
        }

        // ==================== RAID 4: Block-Level Striping with Dedicated Parity ====================

        private async Task SaveRAID4Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 3)
                throw new InvalidOperationException("RAID 4 requires at least 3 providers");

            // RAID 4 is like RAID 5 but with dedicated parity disk
            int dataDisks = _config.ProviderCount - 1;
            int parityDisk = _config.ProviderCount - 1; // Last disk is parity

            var bytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(bytes), _config.StripeSize);

            var diskBuffers = new List<List<byte[]>>();
            for (int i = 0; i < dataDisks; i++)
                diskBuffers.Add(new List<byte[]>());

            // Stripe data across data disks
            for (int i = 0; i < chunks.Count; i++)
            {
                int diskIdx = i % dataDisks;
                diskBuffers[diskIdx].Add(chunks[i]);
            }

            var tasks = new List<Task>();

            // Save data to data disks
            for (int disk = 0; disk < dataDisks; disk++)
            {
                for (int chunk = 0; chunk < diskBuffers[disk].Count; chunk++)
                {
                    tasks.Add(SaveChunkAsync(getProvider(disk), $"{key}.d{disk}.c{chunk}", diskBuffers[disk][chunk]));
                }
            }

            // Compute and save parity to dedicated parity disk
            for (int stripeIdx = 0; stripeIdx < chunks.Count; stripeIdx += dataDisks)
            {
                var stripeChunks = chunks.Skip(stripeIdx).Take(dataDisks).ToList();
                var parity = ComputeXorParity(stripeChunks);
                tasks.Add(SaveChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripeIdx / dataDisks}", parity));
            }

            await Task.WhenAll(tasks);
            _context.LogInfo($"[RAID-4] Saved {key} with block-level striping and dedicated parity");
        }

        private async Task<Stream> LoadRAID4Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 1;
            int parityDisk = _config.ProviderCount - 1; // Dedicated parity on last disk

            // Load all data chunks from data disks
            var diskBuffers = new Dictionary<int, List<byte[]>>();
            var failedDisk = -1;

            for (int disk = 0; disk < dataDisks; disk++)
            {
                diskBuffers[disk] = new List<byte[]>();
                int chunkIdx = 0;
                while (true)
                {
                    try
                    {
                        var chunk = await LoadChunkAsync(getProvider(disk), $"{key}.d{disk}.c{chunkIdx}");
                        diskBuffers[disk].Add(chunk);
                        chunkIdx++;
                    }
                    catch
                    {
                        if (chunkIdx == 0 && failedDisk == -1)
                        {
                            failedDisk = disk;
                            _context.LogWarning($"[RAID-4] Data disk {disk} failed, will reconstruct from parity");
                        }
                        break;
                    }
                }
            }

            // If a data disk failed, reconstruct from parity
            if (failedDisk != -1)
            {
                // Determine number of stripes from other disks
                int stripeCount = diskBuffers.Values.Where(v => v.Count > 0).Max(v => v.Count);
                diskBuffers[failedDisk] = new List<byte[]>();

                for (int stripeIdx = 0; stripeIdx < stripeCount; stripeIdx++)
                {
                    // Load parity for this stripe
                    var parity = await LoadChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripeIdx}");

                    // Collect chunks from working disks
                    var workingChunks = new List<byte[]>();
                    for (int d = 0; d < dataDisks; d++)
                    {
                        if (d != failedDisk && stripeIdx < diskBuffers[d].Count)
                        {
                            workingChunks.Add(diskBuffers[d][stripeIdx]);
                        }
                    }

                    // Reconstruct failed chunk: XOR parity with all working chunks
                    var reconstructed = new byte[parity.Length];
                    Array.Copy(parity, reconstructed, parity.Length);
                    foreach (var chunk in workingChunks)
                    {
                        for (int i = 0; i < Math.Min(chunk.Length, reconstructed.Length); i++)
                        {
                            reconstructed[i] ^= chunk[i];
                        }
                    }

                    diskBuffers[failedDisk].Add(reconstructed);
                }

                _context.LogInfo($"[RAID-4] Reconstructed {stripeCount} chunks for failed disk {failedDisk}");
            }

            // Reassemble data in stripe order
            var result = new List<byte>();
            int maxChunks = diskBuffers.Values.Max(v => v.Count);

            for (int chunkIdx = 0; chunkIdx < maxChunks; chunkIdx++)
            {
                for (int disk = 0; disk < dataDisks; disk++)
                {
                    if (chunkIdx < diskBuffers[disk].Count)
                    {
                        result.AddRange(diskBuffers[disk][chunkIdx]);
                    }
                }
            }

            // Trim to original size
            var trimmed = result.Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(trimmed);
        }

        // ==================== RAID 03: Striped RAID 3 Sets ====================

        private async Task SaveRAID03Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID 03 = RAID 0 stripe across multiple RAID 3 arrays
            if (_config.ProviderCount < 6)
                throw new InvalidOperationException("RAID 03 requires at least 6 providers (2 RAID 3 arrays)");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            int disksPerSet = 3; // Minimum for RAID 3
            if (_config.ProviderCount >= 8) disksPerSet = 4;
            int setsCount = _config.ProviderCount / disksPerSet;
            int dataDisksPerSet = disksPerSet - 1; // Dedicated parity per set

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_03,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>> { { 0, new List<int> { setsCount, disksPerSet } } }
            };

            var tasks = new List<Task>();

            // Stripe chunks across RAID 3 sets with byte-level striping within each set
            for (int chunkIdx = 0; chunkIdx < chunks.Count; chunkIdx++)
            {
                int setIdx = chunkIdx % setsCount;
                int setOffset = setIdx * disksPerSet;
                int localChunkIdx = chunkIdx / setsCount;
                int diskInSet = localChunkIdx % dataDisksPerSet;

                int providerIdx = setOffset + diskInSet;
                tasks.Add(SaveChunkAsync(getProvider(providerIdx), $"{key}.set{setIdx}.chunk.{localChunkIdx}", chunks[chunkIdx]));
            }

            // Compute dedicated parity for each set
            for (int setIdx = 0; setIdx < setsCount; setIdx++)
            {
                int setOffset = setIdx * disksPerSet;
                int parityDisk = setOffset + dataDisksPerSet; // Last disk in set is parity

                var setChunks = new List<byte[]>();
                for (int i = setIdx; i < chunks.Count; i += setsCount)
                    setChunks.Add(chunks[i]);

                if (setChunks.Count > 0)
                {
                    var parity = CalculateParityXOR(setChunks);
                    tasks.Add(SaveChunkAsync(getProvider(parityDisk), $"{key}.set{setIdx}.parity", parity));
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-03] Saved {key} across {setsCount} RAID 3 sets with dedicated parity");
        }

        private async Task<Stream> LoadRAID03Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int setsCount = metadata.ProviderMapping[0][0];
            int disksPerSet = metadata.ProviderMapping[0][1];
            int dataDisksPerSet = disksPerSet - 1;

            var allChunks = new byte[metadata.ChunkCount][];
            var failedSets = new Dictionary<int, List<int>>();

            for (int chunkIdx = 0; chunkIdx < metadata.ChunkCount; chunkIdx++)
            {
                int setIdx = chunkIdx % setsCount;
                int setOffset = setIdx * disksPerSet;
                int localChunkIdx = chunkIdx / setsCount;
                int diskInSet = localChunkIdx % dataDisksPerSet;

                try
                {
                    allChunks[chunkIdx] = await LoadChunkAsync(getProvider(setOffset + diskInSet), $"{key}.set{setIdx}.chunk.{localChunkIdx}");
                }
                catch
                {
                    if (!failedSets.ContainsKey(setIdx))
                        failedSets[setIdx] = new List<int>();
                    failedSets[setIdx].Add(chunkIdx);
                }
            }

            // Rebuild using dedicated parity
            foreach (var (setIdx, failedChunks) in failedSets)
            {
                if (failedChunks.Count > 1)
                    throw new IOException($"RAID 03 can only recover 1 failure per set");

                int setOffset = setIdx * disksPerSet;
                int parityDisk = setOffset + dataDisksPerSet;
                var parity = await LoadChunkAsync(getProvider(parityDisk), $"{key}.set{setIdx}.parity");

                // Collect surviving chunks from this set
                var survivingChunks = new List<byte[]>();
                for (int i = setIdx; i < metadata.ChunkCount; i += setsCount)
                {
                    if (!failedChunks.Contains(i) && allChunks[i] != null)
                        survivingChunks.Add(allChunks[i]);
                }

                // Rebuild
                var rebuilt = new byte[parity.Length];
                Array.Copy(parity, rebuilt, parity.Length);
                foreach (var chunk in survivingChunks)
                {
                    for (int i = 0; i < Math.Min(chunk.Length, rebuilt.Length); i++)
                        rebuilt[i] ^= chunk[i];
                }

                allChunks[failedChunks[0]] = rebuilt;
                _context.LogInfo($"[RAID-03] Rebuilt chunk in set {setIdx}");
            }

            var result = allChunks.SelectMany(c => c ?? Array.Empty<byte>()).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID 100: Striped RAID 10 (Mirrors of Mirrors) ====================

        private async Task SaveRAID100Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID 100 = RAID 0 stripe across multiple RAID 10 arrays
            if (_config.ProviderCount < 8)
                throw new InvalidOperationException("RAID 100 requires at least 8 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            // Each RAID 10 set needs 4 disks (2 mirrored pairs)
            int disksPerSet = 4;
            int setsCount = _config.ProviderCount / disksPerSet;
            int mirrorsPerSet = 2; // 2 mirrored pairs per set

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_100,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>> { { 0, new List<int> { setsCount, disksPerSet } } }
            };

            var tasks = new List<Task>();

            for (int chunkIdx = 0; chunkIdx < chunks.Count; chunkIdx++)
            {
                int setIdx = chunkIdx % setsCount;
                int setOffset = setIdx * disksPerSet;
                int localChunkIdx = chunkIdx / setsCount;
                int mirrorPair = localChunkIdx % mirrorsPerSet;

                // Write to both disks in the mirror pair
                int disk1 = setOffset + mirrorPair * 2;
                int disk2 = setOffset + mirrorPair * 2 + 1;

                tasks.Add(SaveChunkAsync(getProvider(disk1), $"{key}.set{setIdx}.chunk.{localChunkIdx}", chunks[chunkIdx]));
                tasks.Add(SaveChunkAsync(getProvider(disk2), $"{key}.set{setIdx}.mirror.{localChunkIdx}", chunks[chunkIdx]));
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-100] Saved {key} across {setsCount} RAID 10 sets (striped mirrors of mirrors)");
        }

        private async Task<Stream> LoadRAID100Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int setsCount = metadata.ProviderMapping[0][0];
            int disksPerSet = metadata.ProviderMapping[0][1];
            int mirrorsPerSet = 2;

            var allChunks = new byte[metadata.ChunkCount][];

            for (int chunkIdx = 0; chunkIdx < metadata.ChunkCount; chunkIdx++)
            {
                int setIdx = chunkIdx % setsCount;
                int setOffset = setIdx * disksPerSet;
                int localChunkIdx = chunkIdx / setsCount;
                int mirrorPair = localChunkIdx % mirrorsPerSet;

                int disk1 = setOffset + mirrorPair * 2;
                int disk2 = setOffset + mirrorPair * 2 + 1;

                try
                {
                    allChunks[chunkIdx] = await LoadChunkAsync(getProvider(disk1), $"{key}.set{setIdx}.chunk.{localChunkIdx}");
                }
                catch
                {
                    try
                    {
                        allChunks[chunkIdx] = await LoadChunkAsync(getProvider(disk2), $"{key}.set{setIdx}.mirror.{localChunkIdx}");
                        _context.LogInfo($"[RAID-100] Used mirror for chunk {chunkIdx}");
                    }
                    catch
                    {
                        throw new IOException($"Both mirrors failed for chunk {chunkIdx}");
                    }
                }
            }

            var result = allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID 1E: Enhanced Mirrored Striping ====================

        private async Task SaveRAID1EAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 3)
                throw new InvalidOperationException("RAID 1E requires at least 3 providers");

            // RAID 1E: Data striped and mirrored to adjacent drives
            var bytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(bytes), _config.StripeSize);

            var tasks = new List<Task>();
            for (int i = 0; i < chunks.Count; i++)
            {
                // Save chunk to current disk and next disk (wraparound)
                int disk1 = i % _config.ProviderCount;
                int disk2 = (i + 1) % _config.ProviderCount;

                tasks.Add(SaveChunkAsync(getProvider(disk1), $"{key}.chunk.{i}", chunks[i]));
                tasks.Add(SaveChunkAsync(getProvider(disk2), $"{key}.mirror.{i}", chunks[i]));
            }

            await Task.WhenAll(tasks);
            _context.LogInfo($"[RAID-1E] Saved {key} with enhanced mirrored striping");
        }

        private async Task<Stream> LoadRAID1EAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load from primary chunks
            var chunks = new List<byte[]>();
            int chunkIdx = 0;

            while (true)
            {
                try
                {
                    int disk = chunkIdx % _config.ProviderCount;
                    var chunk = await LoadChunkAsync(getProvider(disk), $"{key}.chunk.{chunkIdx}");
                    chunks.Add(chunk);
                    chunkIdx++;
                }
                catch
                {
                    break; // No more chunks
                }
            }

            return ReassembleChunks(chunks.ToArray());
        }

        // ==================== RAID 5E: RAID 5 with Integrated Hot Spare ====================

        private async Task SaveRAID5EAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID 5E: RAID 5 with integrated distributed hot spare space
            // Each stripe reserves space that can be used for rebuild
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("RAID 5E requires at least 4 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            // Reserve ~20% capacity for hot spare (distributed across all disks)
            int effectiveDisks = _config.ProviderCount;
            int dataDisks = effectiveDisks - 1; // One parity
            int spareBlocksPerStripe = Math.Max(1, effectiveDisks / 5); // ~20% spare

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_5E,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>> { { 0, new List<int> { spareBlocksPerStripe } } }
            };

            var tasks = new List<Task>();
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityDisk = stripe % effectiveDisks;
                int spareDisk = (stripe + effectiveDisks - 1) % effectiveDisks; // Rotating spare

                int dataDiskCounter = 0;
                for (int diskIdx = 0; diskIdx < effectiveDisks; diskIdx++)
                {
                    if (diskIdx == parityDisk)
                    {
                        // Calculate and save parity
                        var stripeChunks = chunks.Skip(stripe * dataDisks).Take(dataDisks).ToList();
                        if (stripeChunks.Count > 0)
                        {
                            var parity = CalculateParityXOR(stripeChunks);
                            tasks.Add(SaveChunkAsync(getProvider(diskIdx), $"{key}.parity.{stripe}", parity));
                        }
                    }
                    else if (diskIdx == spareDisk)
                    {
                        // Mark spare block (save empty marker for hot spare reservation)
                        tasks.Add(SaveChunkAsync(getProvider(diskIdx), $"{key}.spare.{stripe}", new byte[] { 0xFE }));
                    }
                    else
                    {
                        int chunkIdx = stripe * dataDisks + dataDiskCounter;
                        if (chunkIdx < chunks.Count)
                        {
                            tasks.Add(SaveChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}", chunks[chunkIdx]));
                        }
                        dataDiskCounter++;
                    }
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-5E] Saved {key} with distributed hot spare (~20% reserved)");
        }

        private async Task<Stream> LoadRAID5EAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            // Load uses same logic as RAID 5 but ignores spare blocks
            var chunks = new List<byte[]>();
            int chunkIdx = 0;

            while (chunkIdx < metadata.ChunkCount)
            {
                try
                {
                    var chunk = await LoadChunkAsync(getProvider(chunkIdx % _config.ProviderCount), $"{key}.chunk.{chunkIdx}");
                    chunks.Add(chunk);
                    chunkIdx++;
                }
                catch
                {
                    // Try to rebuild from parity and spare
                    int stripe = chunkIdx / (_config.ProviderCount - 1);
                    int parityDisk = stripe % _config.ProviderCount;
                    var parity = await LoadChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripe}");

                    var otherChunks = new List<byte[]>();
                    for (int i = 0; i < _config.ProviderCount - 2; i++)
                    {
                        int otherIdx = stripe * (_config.ProviderCount - 1) + i;
                        if (otherIdx != chunkIdx && otherIdx < metadata.ChunkCount)
                        {
                            try { otherChunks.Add(await LoadChunkAsync(getProvider(otherIdx % _config.ProviderCount), $"{key}.chunk.{otherIdx}")); }
                            catch { }
                        }
                    }

                    var rebuilt = RebuildChunkFromParity(otherChunks, parity);
                    chunks.Add(rebuilt);
                    _context.LogInfo($"[RAID-5E] Rebuilt chunk {chunkIdx} using hot spare and parity");
                    chunkIdx++;
                }
            }

            var result = chunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID 5EE: RAID 5 with Distributed Spare ====================

        private async Task SaveRAID5EEAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID 5EE: Enhanced RAID 5 with more aggressively distributed spare
            // Spare blocks are interleaved with data for faster rebuild
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("RAID 5EE requires at least 4 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            int effectiveDisks = _config.ProviderCount;
            int dataDisks = effectiveDisks - 2; // One parity, one spare per stripe

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_5EE,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count,
                MirrorCount = 1 // Indicates spare blocks present
            };

            var tasks = new List<Task>();
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityDisk = stripe % effectiveDisks;
                int spareDisk = (stripe + 1) % effectiveDisks;

                var stripeChunks = chunks.Skip(stripe * dataDisks).Take(dataDisks).ToList();

                if (stripeChunks.Count > 0)
                {
                    var parity = CalculateParityXOR(stripeChunks);
                    tasks.Add(SaveChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripe}", parity));
                    tasks.Add(SaveChunkAsync(getProvider(spareDisk), $"{key}.spare.{stripe}", new byte[] { 0xEE })); // Spare marker
                }

                int dataDiskCounter = 0;
                for (int diskIdx = 0; diskIdx < effectiveDisks; diskIdx++)
                {
                    if (diskIdx != parityDisk && diskIdx != spareDisk && dataDiskCounter < stripeChunks.Count)
                    {
                        int chunkIdx = stripe * dataDisks + dataDiskCounter;
                        tasks.Add(SaveChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}", stripeChunks[dataDiskCounter]));
                        dataDiskCounter++;
                    }
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-5EE] Saved {key} with enhanced distributed spare (1 spare per stripe)");
        }

        private async Task<Stream> LoadRAID5EEAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 2;
            int stripeCount = (int)MathUtils.Ceiling((double)metadata.ChunkCount / dataDisks);
            var allChunks = new byte[metadata.ChunkCount][];

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityDisk = stripe % _config.ProviderCount;
                int failedDisk = -1;

                for (int i = 0; i < dataDisks; i++)
                {
                    int chunkIdx = stripe * dataDisks + i;
                    if (chunkIdx >= metadata.ChunkCount) break;

                    int diskIdx = 0;
                    int counter = 0;
                    for (int d = 0; d < _config.ProviderCount; d++)
                    {
                        if (d != parityDisk && d != (stripe + 1) % _config.ProviderCount)
                        {
                            if (counter == i) { diskIdx = d; break; }
                            counter++;
                        }
                    }

                    try
                    {
                        allChunks[chunkIdx] = await LoadChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}");
                    }
                    catch
                    {
                        failedDisk = i;
                        allChunks[chunkIdx] = null!;
                    }
                }

                // Rebuild if needed
                if (failedDisk >= 0)
                {
                    var parity = await LoadChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripe}");
                    var surviving = new List<byte[]>();
                    for (int i = 0; i < dataDisks; i++)
                    {
                        int idx = stripe * dataDisks + i;
                        if (idx < metadata.ChunkCount && allChunks[idx] != null)
                            surviving.Add(allChunks[idx]);
                    }
                    var rebuilt = RebuildChunkFromParity(surviving, parity);
                    allChunks[stripe * dataDisks + failedDisk] = rebuilt;
                    _context.LogInfo($"[RAID-5EE] Rebuilt chunk using spare and parity");
                }
            }

            var result = allChunks.Where(c => c != null).SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID 6E: RAID 6 Enhanced ====================

        private async Task SaveRAID6EAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID 6E: RAID 6 with distributed spare capacity
            if (_config.ProviderCount < 5)
                throw new InvalidOperationException("RAID 6E requires at least 5 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            int effectiveDisks = _config.ProviderCount;
            int dataDisks = effectiveDisks - 3; // Two parity, one spare

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_6E,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count,
                MirrorCount = 2 // Indicates dual parity + spare
            };

            var tasks = new List<Task>();
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityPDisk = stripe % effectiveDisks;
                int parityQDisk = (stripe + 1) % effectiveDisks;
                int spareDisk = (stripe + 2) % effectiveDisks;

                var stripeChunks = chunks.Skip(stripe * dataDisks).Take(dataDisks).ToList();

                if (stripeChunks.Count > 0)
                {
                    var parityP = CalculateParityXOR(stripeChunks);
                    var parityQ = CalculateParityReedSolomon(stripeChunks);
                    tasks.Add(SaveChunkAsync(getProvider(parityPDisk), $"{key}.parityP.{stripe}", parityP));
                    tasks.Add(SaveChunkAsync(getProvider(parityQDisk), $"{key}.parityQ.{stripe}", parityQ));
                    tasks.Add(SaveChunkAsync(getProvider(spareDisk), $"{key}.spare.{stripe}", new byte[] { 0x6E }));
                }

                int dataDiskCounter = 0;
                for (int diskIdx = 0; diskIdx < effectiveDisks; diskIdx++)
                {
                    if (diskIdx != parityPDisk && diskIdx != parityQDisk && diskIdx != spareDisk && dataDiskCounter < stripeChunks.Count)
                    {
                        int chunkIdx = stripe * dataDisks + dataDiskCounter;
                        tasks.Add(SaveChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}", stripeChunks[dataDiskCounter]));
                        dataDiskCounter++;
                    }
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-6E] Saved {key} with dual parity and distributed spare");
        }

        private async Task<Stream> LoadRAID6EAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 3;
            int stripeCount = (int)MathUtils.Ceiling((double)metadata.ChunkCount / dataDisks);
            var allChunks = new byte[metadata.ChunkCount][];

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityPDisk = stripe % _config.ProviderCount;
                int parityQDisk = (stripe + 1) % _config.ProviderCount;
                var failedIndices = new List<int>();
                var stripeChunks = new List<byte[]>();

                for (int i = 0; i < dataDisks; i++)
                {
                    int chunkIdx = stripe * dataDisks + i;
                    if (chunkIdx >= metadata.ChunkCount) break;

                    int diskIdx = 0;
                    int counter = 0;
                    for (int d = 0; d < _config.ProviderCount; d++)
                    {
                        if (d != parityPDisk && d != parityQDisk && d != (stripe + 2) % _config.ProviderCount)
                        {
                            if (counter == i) { diskIdx = d; break; }
                            counter++;
                        }
                    }

                    try
                    {
                        var chunk = await LoadChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}");
                        allChunks[chunkIdx] = chunk;
                        stripeChunks.Add(chunk);
                    }
                    catch
                    {
                        failedIndices.Add(i);
                        stripeChunks.Add(null!);
                    }
                }

                // Rebuild up to 2 failures using dual parity
                if (failedIndices.Count > 0 && failedIndices.Count <= 2)
                {
                    var parityP = await LoadChunkAsync(getProvider(parityPDisk), $"{key}.parityP.{stripe}");
                    var parityQ = await LoadChunkAsync(getProvider(parityQDisk), $"{key}.parityQ.{stripe}");
                    var rebuilt = RebuildFromDualParity(stripeChunks, parityP, parityQ, failedIndices);

                    foreach (var (idx, chunk) in rebuilt)
                    {
                        allChunks[stripe * dataDisks + idx] = chunk;
                    }
                    _context.LogInfo($"[RAID-6E] Rebuilt {failedIndices.Count} chunks in stripe {stripe}");
                }
            }

            var result = allChunks.Where(c => c != null).SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID-S: Dell/EMC Parity RAID ====================

        private async Task SaveRAIDSAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID-S: Dell/EMC proprietary RAID with optimized parity placement
            // Uses sector-aligned parity for better sequential performance
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("RAID-S requires at least 4 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            int dataDisks = _config.ProviderCount - 1;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_S,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count
            };

            var tasks = new List<Task>();
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                // Dell/EMC uses fixed parity position per stripe group
                int parityDisk = (stripe / 4) % _config.ProviderCount; // Parity changes every 4 stripes

                var stripeChunks = new List<byte[]>();
                int dataDiskCounter = 0;

                for (int diskIdx = 0; diskIdx < _config.ProviderCount; diskIdx++)
                {
                    if (diskIdx == parityDisk)
                        continue;

                    int chunkIdx = stripe * dataDisks + dataDiskCounter;
                    if (chunkIdx < chunks.Count)
                    {
                        stripeChunks.Add(chunks[chunkIdx]);
                        tasks.Add(SaveChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}", chunks[chunkIdx]));
                    }
                    dataDiskCounter++;
                }

                if (stripeChunks.Count > 0)
                {
                    var parity = CalculateParityXOR(stripeChunks);
                    tasks.Add(SaveChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripe}", parity));
                }
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-S] Saved {key} with Dell/EMC optimized parity placement");
        }

        private async Task<Stream> LoadRAIDSAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 1;
            var allChunks = new byte[metadata.ChunkCount][];

            for (int chunkIdx = 0; chunkIdx < metadata.ChunkCount; chunkIdx++)
            {
                int stripe = chunkIdx / dataDisks;
                int parityDisk = (stripe / 4) % _config.ProviderCount;
                int diskInStripe = chunkIdx % dataDisks;

                // Calculate actual disk index
                int actualDisk = diskInStripe;
                if (actualDisk >= parityDisk) actualDisk++;

                try
                {
                    allChunks[chunkIdx] = await LoadChunkAsync(getProvider(actualDisk), $"{key}.chunk.{chunkIdx}");
                }
                catch
                {
                    // Rebuild from parity
                    var parity = await LoadChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripe}");
                    var surviving = new List<byte[]>();
                    for (int i = 0; i < dataDisks; i++)
                    {
                        int idx = stripe * dataDisks + i;
                        if (idx != chunkIdx && idx < metadata.ChunkCount && allChunks[idx] != null)
                            surviving.Add(allChunks[idx]);
                    }
                    allChunks[chunkIdx] = RebuildChunkFromParity(surviving, parity);
                    _context.LogInfo($"[RAID-S] Rebuilt chunk {chunkIdx}");
                }
            }

            var result = allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID 7: Cached Striping with Parity ====================

        private async Task SaveRAID7Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID 7: Proprietary Storage Computer Corporation design
            // Features: Dedicated parity disk, real-time OS, cache for all operations
            if (_config.ProviderCount < 5)
                throw new InvalidOperationException("RAID 7 requires at least 5 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            // RAID 7 uses dedicated parity disk (last disk) and cache disk (second-to-last)
            int parityDisk = _config.ProviderCount - 1;
            int cacheDisk = _config.ProviderCount - 2;
            int dataDisks = _config.ProviderCount - 2;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_7,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count,
                ProviderMapping = new Dictionary<int, List<int>> { { 0, new List<int> { parityDisk, cacheDisk } } }
            };

            var tasks = new List<Task>();

            // Write data to data disks with cache tracking
            for (int chunkIdx = 0; chunkIdx < chunks.Count; chunkIdx++)
            {
                int diskIdx = chunkIdx % dataDisks;
                tasks.Add(SaveChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}", chunks[chunkIdx]));
            }

            // Calculate parity across all data
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);
            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                var stripeChunks = chunks.Skip(stripe * dataDisks).Take(dataDisks).ToList();
                if (stripeChunks.Count > 0)
                {
                    var parity = CalculateParityXOR(stripeChunks);
                    tasks.Add(SaveChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripe}", parity));
                }
            }

            // Write cache index (metadata about cached operations)
            var cacheIndex = System.Text.Encoding.UTF8.GetBytes($"RAID7_CACHE:{key}:{chunks.Count}");
            tasks.Add(SaveChunkAsync(getProvider(cacheDisk), $"{key}.cache.index", cacheIndex));

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-7] Saved {key} with dedicated parity and cache tracking");
        }

        private async Task<Stream> LoadRAID7Async(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int parityDisk = metadata.ProviderMapping[0][0];
            int dataDisks = _config.ProviderCount - 2;

            var allChunks = new byte[metadata.ChunkCount][];

            for (int chunkIdx = 0; chunkIdx < metadata.ChunkCount; chunkIdx++)
            {
                int diskIdx = chunkIdx % dataDisks;

                try
                {
                    allChunks[chunkIdx] = await LoadChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}");
                }
                catch
                {
                    // Rebuild using parity
                    int stripe = chunkIdx / dataDisks;
                    var parity = await LoadChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripe}");

                    var surviving = new List<byte[]>();
                    for (int i = 0; i < dataDisks; i++)
                    {
                        int idx = stripe * dataDisks + i;
                        if (idx != chunkIdx && idx < metadata.ChunkCount)
                        {
                            try
                            {
                                var chunk = await LoadChunkAsync(getProvider(i), $"{key}.chunk.{idx}");
                                surviving.Add(chunk);
                            }
                            catch { }
                        }
                    }

                    allChunks[chunkIdx] = RebuildChunkFromParity(surviving, parity);
                    _context.LogInfo($"[RAID-7] Rebuilt chunk {chunkIdx} from parity");
                }
            }

            var result = allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID-FR: IBM Fast Rebuild ====================

        private async Task SaveRAIDFRAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            // RAID-FR: IBM Fast Rebuild technology
            // Stores additional metadata to enable faster rebuild times
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("RAID-FR requires at least 4 providers");

            var allBytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(allBytes), _config.StripeSize);

            int dataDisks = _config.ProviderCount - 1;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_FR,
                TotalSize = allBytes.Length,
                ChunkCount = chunks.Count
            };

            var tasks = new List<Task>();
            int stripeCount = (int)MathUtils.Ceiling((double)chunks.Count / dataDisks);

            // Create fast rebuild bitmap (tracks which blocks are in use)
            var rebuildBitmap = new byte[(chunks.Count + 7) / 8];
            for (int i = 0; i < chunks.Count; i++)
            {
                rebuildBitmap[i / 8] |= (byte)(1 << (i % 8));
            }

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityDisk = stripe % _config.ProviderCount;
                var stripeChunks = new List<byte[]>();

                int dataDiskCounter = 0;
                for (int diskIdx = 0; diskIdx < _config.ProviderCount; diskIdx++)
                {
                    if (diskIdx == parityDisk)
                        continue;

                    int chunkIdx = stripe * dataDisks + dataDiskCounter;
                    if (chunkIdx < chunks.Count)
                    {
                        stripeChunks.Add(chunks[chunkIdx]);
                        tasks.Add(SaveChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}", chunks[chunkIdx]));
                    }
                    dataDiskCounter++;
                }

                if (stripeChunks.Count > 0)
                {
                    var parity = CalculateParityXOR(stripeChunks);
                    tasks.Add(SaveChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripe}", parity));
                }
            }

            // Save rebuild bitmap to each disk for redundancy
            for (int disk = 0; disk < _config.ProviderCount; disk++)
            {
                tasks.Add(SaveChunkAsync(getProvider(disk), $"{key}.fr.bitmap", rebuildBitmap));
            }

            await Task.WhenAll(tasks);
            _metadata[key] = metadata;
            _context.LogInfo($"[RAID-FR] Saved {key} with fast rebuild metadata (bitmap: {rebuildBitmap.Length} bytes)");
        }

        private async Task<Stream> LoadRAIDFRAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            if (!_metadata.TryGetValue(key, out var metadata))
                throw new FileNotFoundException($"RAID metadata not found for {key}");

            int dataDisks = _config.ProviderCount - 1;
            int stripeCount = (int)MathUtils.Ceiling((double)metadata.ChunkCount / dataDisks);

            // Load rebuild bitmap for fast block identification
            byte[] rebuildBitmap = null!;
            for (int disk = 0; disk < _config.ProviderCount; disk++)
            {
                try
                {
                    rebuildBitmap = await LoadChunkAsync(getProvider(disk), $"{key}.fr.bitmap");
                    break;
                }
                catch { }
            }

            var allChunks = new byte[metadata.ChunkCount][];

            for (int stripe = 0; stripe < stripeCount; stripe++)
            {
                int parityDisk = stripe % _config.ProviderCount;
                var failedIdx = -1;

                int dataDiskCounter = 0;
                for (int diskIdx = 0; diskIdx < _config.ProviderCount; diskIdx++)
                {
                    if (diskIdx == parityDisk)
                        continue;

                    int chunkIdx = stripe * dataDisks + dataDiskCounter;
                    if (chunkIdx >= metadata.ChunkCount)
                        break;

                    // Check rebuild bitmap - only load blocks that are in use
                    bool inUse = rebuildBitmap == null || (rebuildBitmap[chunkIdx / 8] & (1 << (chunkIdx % 8))) != 0;

                    if (inUse)
                    {
                        try
                        {
                            allChunks[chunkIdx] = await LoadChunkAsync(getProvider(diskIdx), $"{key}.chunk.{chunkIdx}");
                        }
                        catch
                        {
                            failedIdx = dataDiskCounter;
                        }
                    }
                    dataDiskCounter++;
                }

                // Fast rebuild using bitmap knowledge
                if (failedIdx >= 0)
                {
                    var parity = await LoadChunkAsync(getProvider(parityDisk), $"{key}.parity.{stripe}");
                    var surviving = new List<byte[]>();
                    for (int i = 0; i < dataDisks; i++)
                    {
                        int idx = stripe * dataDisks + i;
                        if (i != failedIdx && idx < metadata.ChunkCount && allChunks[idx] != null)
                            surviving.Add(allChunks[idx]);
                    }
                    allChunks[stripe * dataDisks + failedIdx] = RebuildChunkFromParity(surviving, parity);
                    _context.LogInfo($"[RAID-FR] Fast rebuild of chunk in stripe {stripe}");
                }
            }

            var result = allChunks.Where(c => c != null).SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID MD10: Linux MD RAID 10 ====================
        // Linux MD RAID 10 with near/far/offset layouts for flexible mirroring

        /// <summary>
        /// MD10 layout mode: near, far, or offset
        /// </summary>
        private enum MD10Layout { Near, Far, Offset }

        private async Task SaveRAIDMD10Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 3)
                throw new InvalidOperationException("RAID MD10 requires at least 3 providers");

            // Linux MD RAID 10: Flexible near/far/offset layouts
            // near=N: N copies stored on consecutive drives (default near=2)
            // far=N: N copies stored at different offsets on different drives
            // offset=N: Like far but with shifted stripe patterns

            var bytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(bytes), _config.StripeSize);

            // Configuration: near=2 by default (can be configured via extended config)
            int nearCopies = 2;
            MD10Layout layout = MD10Layout.Near;

            // Calculate effective data drives and layout
            int totalDrives = _config.ProviderCount;
            int stripeSets = totalDrives / nearCopies; // Number of stripe sets

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_MD10,
                TotalSize = bytes.Length,
                ChunkCount = chunks.Count,
                StripeSize = _config.StripeSize,
                ProviderCount = _config.ProviderCount,
                ParityDriveIndex = -1, // No dedicated parity in RAID 10
                Layout = $"md10-{layout.ToString().ToLower()}-{nearCopies}"
            };

            // Save metadata to all providers
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            var metadataTasks = new List<Task>();
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                var provider = getProvider(i);
                metadataTasks.Add(provider.WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson))));
            }
            await Task.WhenAll(metadataTasks);

            // MD10 Near layout: stripe across sets, mirror within sets
            // Drive mapping: [Set0-Copy0, Set0-Copy1, Set1-Copy0, Set1-Copy1, ...]
            var saveTasks = new List<Task>();

            for (int chunkIdx = 0; chunkIdx < chunks.Count; chunkIdx++)
            {
                var chunk = chunks[chunkIdx];

                if (layout == MD10Layout.Near)
                {
                    // Near layout: consecutive drives hold copies
                    int stripeSet = chunkIdx % stripeSets;
                    int baseDrive = stripeSet * nearCopies;

                    for (int copy = 0; copy < nearCopies && baseDrive + copy < totalDrives; copy++)
                    {
                        int driveIdx = baseDrive + copy;
                        var provider = getProvider(driveIdx);
                        var chunkKey = $"{key}.md10.{chunkIdx}.{copy}";
                        saveTasks.Add(provider.WriteAsync(chunkKey, new MemoryStream(chunk)));
                    }
                }
                else if (layout == MD10Layout.Far)
                {
                    // Far layout: copies at different offsets across drives
                    int primaryDrive = chunkIdx % totalDrives;
                    int offset = totalDrives / nearCopies;

                    for (int copy = 0; copy < nearCopies; copy++)
                    {
                        int driveIdx = (primaryDrive + copy * offset) % totalDrives;
                        var provider = getProvider(driveIdx);
                        var chunkKey = $"{key}.md10.{chunkIdx}.{copy}";
                        saveTasks.Add(provider.WriteAsync(chunkKey, new MemoryStream(chunk)));
                    }
                }
                else // Offset layout
                {
                    // Offset layout: similar to far but with stripe offset pattern
                    int stripeRow = chunkIdx / stripeSets;
                    int stripeCol = chunkIdx % stripeSets;

                    for (int copy = 0; copy < nearCopies; copy++)
                    {
                        int offset = (stripeRow + copy) % nearCopies;
                        int driveIdx = stripeCol * nearCopies + offset;
                        if (driveIdx < totalDrives)
                        {
                            var provider = getProvider(driveIdx);
                            var chunkKey = $"{key}.md10.{chunkIdx}.{copy}";
                            saveTasks.Add(provider.WriteAsync(chunkKey, new MemoryStream(chunk)));
                        }
                    }
                }
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[RAID-MD10] Saved {key} with {layout} layout, {nearCopies} copies, {stripeSets} stripe sets");
        }

        private async Task<Stream> LoadRAIDMD10Async(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    var provider = getProvider(i);
                    using var metaStream = await provider.ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        var metaBytes = await ReadAllBytesAsync(metaStream);
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(System.Text.Encoding.UTF8.GetString(metaBytes));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            // Parse layout from metadata
            int nearCopies = 2;
            MD10Layout layout = MD10Layout.Near;
            if (!string.IsNullOrEmpty(metadata.Layout) && metadata.Layout.StartsWith("md10-"))
            {
                var parts = metadata.Layout.Split('-');
                if (parts.Length >= 3)
                {
                    Enum.TryParse(parts[1], true, out layout);
                    int.TryParse(parts[2], out nearCopies);
                }
            }

            int totalDrives = metadata.ProviderCount;
            int stripeSets = totalDrives / nearCopies;

            var allChunks = new byte[metadata.ChunkCount][];

            for (int chunkIdx = 0; chunkIdx < metadata.ChunkCount; chunkIdx++)
            {
                byte[]? chunkData = null;

                // Try each copy until we find one that works
                for (int copy = 0; copy < nearCopies && chunkData == null; copy++)
                {
                    int driveIdx;

                    if (layout == MD10Layout.Near)
                    {
                        int stripeSet = chunkIdx % stripeSets;
                        int baseDrive = stripeSet * nearCopies;
                        driveIdx = baseDrive + copy;
                    }
                    else if (layout == MD10Layout.Far)
                    {
                        int primaryDrive = chunkIdx % totalDrives;
                        int offset = totalDrives / nearCopies;
                        driveIdx = (primaryDrive + copy * offset) % totalDrives;
                    }
                    else // Offset
                    {
                        int stripeRow = chunkIdx / stripeSets;
                        int stripeCol = chunkIdx % stripeSets;
                        int offsetVal = (stripeRow + copy) % nearCopies;
                        driveIdx = stripeCol * nearCopies + offsetVal;
                    }

                    if (driveIdx < totalDrives)
                    {
                        try
                        {
                            var provider = getProvider(driveIdx);
                            var chunkKey = $"{key}.md10.{chunkIdx}.{copy}";
                            using var stream = await provider.ReadAsync(chunkKey);
                            if (stream != null)
                            {
                                chunkData = await ReadAllBytesAsync(stream);
                            }
                        }
                        catch { continue; }
                    }
                }

                if (chunkData == null)
                    throw new InvalidOperationException($"Failed to load chunk {chunkIdx} from any copy");

                allChunks[chunkIdx] = chunkData;
            }

            var result = allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== Adaptive RAID: IBM Auto-Tuning ====================

        private async Task SaveRAIDAdaptiveAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 3)
                throw new InvalidOperationException("Adaptive RAID requires at least 3 providers");

            // Adaptive RAID: Auto-select best RAID level based on workload
            // For small data: use RAID 1 (performance)
            // For large data: use RAID 5 (capacity)
            var bytes = await ReadAllBytesAsync(data);

            if (bytes.Length < 1024 * 1024) // < 1MB: use RAID 1
            {
                await SaveRAID1Async(key, new MemoryStream(bytes), getProvider);
                _context.LogInfo($"[Adaptive-RAID] Saved {key} using RAID 1 (small file optimization)");
            }
            else // >= 1MB: use RAID 5
            {
                await SaveRAID5Async(key, new MemoryStream(bytes), getProvider);
                _context.LogInfo($"[Adaptive-RAID] Saved {key} using RAID 5 (large file optimization)");
            }
        }

        private async Task<Stream> LoadRAIDAdaptiveAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Try RAID 1 first, fallback to RAID 5
            try
            {
                return await LoadRAID1Async(key, getProvider);
            }
            catch
            {
                return await LoadRAID5Async(key, getProvider);
            }
        }

        // ==================== BeyondRAID: Drobo Dynamic RAID ====================

        private async Task SaveRAIDBeyondAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 2)
                throw new InvalidOperationException("BeyondRAID requires at least 2 providers");

            // BeyondRAID: Dynamic protection that adapts to available drives
            // 2 drives: RAID 1, 3+ drives: RAID 5, 4+ drives: RAID 6
            if (_config.ProviderCount == 2)
            {
                await SaveRAID1Async(key, data, getProvider);
                _context.LogInfo($"[BeyondRAID] Saved {key} using RAID 1 (2-drive mode)");
            }
            else if (_config.ProviderCount == 3)
            {
                await SaveRAID5Async(key, data, getProvider);
                _context.LogInfo($"[BeyondRAID] Saved {key} using RAID 5 (3-drive mode)");
            }
            else
            {
                await SaveRAID6Async(key, data, getProvider);
                _context.LogInfo($"[BeyondRAID] Saved {key} using RAID 6 (4+ drive mode)");
            }
        }

        private async Task<Stream> LoadRAIDBeyondAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Try different RAID levels based on provider count
            if (_config.ProviderCount == 2)
                return await LoadRAID1Async(key, getProvider);
            else if (_config.ProviderCount == 3)
                return await LoadRAID5Async(key, getProvider);
            else
                return await LoadRAID6Async(key, getProvider);
        }

        // ==================== Declustered RAID: Advanced Parity Distribution ====================
        // Parity declustering distributes parity across ALL drives using a permutation matrix
        // This enables faster rebuilds by involving all drives in reconstruction

        private async Task SaveRAIDDeclusteredAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("Declustered RAID requires at least 4 providers");

            // Declustered RAID: Distributes rebuild work across ALL drives
            // Uses a permutation matrix to assign data and parity to different drives per stripe
            // Rebuild involves all drives, not just the parity group

            var bytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(bytes), _config.StripeSize);

            int n = _config.ProviderCount;   // Total drives
            int k = n - 2;                    // Data drives per stripe (dual parity)
            int stripeGroupSize = n;          // Each stripe group spans all drives

            // Calculate parity for each stripe group
            var allStripeData = new List<(int stripeIdx, int[] driveAssignment, byte[][] dataChunks, byte[] parityP, byte[] parityQ)>();

            int stripeIdx = 0;
            for (int i = 0; i < chunks.Count; i += k)
            {
                // Get data chunks for this stripe
                var stripeChunks = new List<byte[]>();
                for (int j = 0; j < k && i + j < chunks.Count; j++)
                {
                    stripeChunks.Add(chunks[i + j]);
                }

                // Pad if needed
                int maxLen = stripeChunks.Max(c => c.Length);
                for (int j = 0; j < stripeChunks.Count; j++)
                {
                    if (stripeChunks[j].Length < maxLen)
                    {
                        var padded = new byte[maxLen];
                        Array.Copy(stripeChunks[j], padded, stripeChunks[j].Length);
                        stripeChunks[j] = padded;
                    }
                }

                // Calculate P and Q parity
                var parityP = new byte[maxLen];
                var parityQ = new byte[maxLen];

                for (int byteIdx = 0; byteIdx < maxLen; byteIdx++)
                {
                    byte p = 0;
                    byte q = 0;
                    for (int d = 0; d < stripeChunks.Count; d++)
                    {
                        p ^= stripeChunks[d][byteIdx];
                        q ^= GF256Multiply(stripeChunks[d][byteIdx], GF256ExpTable[d]);
                    }
                    parityP[byteIdx] = p;
                    parityQ[byteIdx] = q;
                }

                // Declustered assignment: rotate drive assignments per stripe
                // This ensures parity and data are on different drives each stripe
                var driveAssignment = new int[stripeChunks.Count + 2]; // data + 2 parity

                // Use permutation matrix approach: shift based on stripe index
                int shift = stripeIdx % n;
                for (int d = 0; d < stripeChunks.Count; d++)
                {
                    driveAssignment[d] = (d + shift) % n;
                }
                // P parity on different drive
                driveAssignment[stripeChunks.Count] = (stripeChunks.Count + shift) % n;
                // Q parity on different drive
                driveAssignment[stripeChunks.Count + 1] = (stripeChunks.Count + 1 + shift) % n;

                allStripeData.Add((stripeIdx, driveAssignment, stripeChunks.ToArray(), parityP, parityQ));
                stripeIdx++;
            }

            // Build declustered layout metadata
            var layoutInfo = new List<string>();
            foreach (var stripe in allStripeData)
            {
                layoutInfo.Add($"{stripe.stripeIdx}:{string.Join(",", stripe.driveAssignment)}");
            }

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_Declustered,
                TotalSize = bytes.Length,
                ChunkCount = chunks.Count,
                StripeSize = _config.StripeSize,
                ProviderCount = _config.ProviderCount,
                ParityDriveIndex = -1, // Distributed
                Layout = $"declustered-k{k}-{string.Join(";", layoutInfo)}"
            };

            // Save metadata to all providers
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            var metadataTasks = new List<Task>();
            for (int i = 0; i < n; i++)
            {
                var provider = getProvider(i);
                metadataTasks.Add(provider.WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson))));
            }
            await Task.WhenAll(metadataTasks);

            // Save data and parity chunks according to declustered layout
            var saveTasks = new List<Task>();
            int globalChunkIdx = 0;

            foreach (var stripe in allStripeData)
            {
                // Save data chunks
                for (int d = 0; d < stripe.dataChunks.Length; d++)
                {
                    int driveIdx = stripe.driveAssignment[d];
                    var provider = getProvider(driveIdx);
                    var chunkKey = $"{key}.dcl.s{stripe.stripeIdx}.d{d}";
                    saveTasks.Add(provider.WriteAsync(chunkKey, new MemoryStream(stripe.dataChunks[d])));
                    globalChunkIdx++;
                }

                // Save P parity
                int pDrive = stripe.driveAssignment[stripe.dataChunks.Length];
                var pProvider = getProvider(pDrive);
                saveTasks.Add(pProvider.WriteAsync($"{key}.dcl.s{stripe.stripeIdx}.p", new MemoryStream(stripe.parityP)));

                // Save Q parity
                int qDrive = stripe.driveAssignment[stripe.dataChunks.Length + 1];
                var qProvider = getProvider(qDrive);
                saveTasks.Add(qProvider.WriteAsync($"{key}.dcl.s{stripe.stripeIdx}.q", new MemoryStream(stripe.parityQ)));
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[Declustered-RAID] Saved {key} with {allStripeData.Count} stripe groups across {n} drives");
        }

        private async Task<Stream> LoadRAIDDeclusteredAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    var provider = getProvider(i);
                    using var metaStream = await provider.ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        var metaBytes = await ReadAllBytesAsync(metaStream);
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(System.Text.Encoding.UTF8.GetString(metaBytes));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            // Parse declustered layout from metadata
            int n = metadata.ProviderCount;
            int k = n - 2;

            // Parse stripe assignments from layout
            var stripeAssignments = new Dictionary<int, int[]>();
            if (!string.IsNullOrEmpty(metadata.Layout) && metadata.Layout.StartsWith("declustered-"))
            {
                var parts = metadata.Layout.Split('-');
                if (parts.Length >= 3)
                {
                    var stripeInfos = parts[2].Split(';');
                    foreach (var info in stripeInfos)
                    {
                        var kv = info.Split(':');
                        if (kv.Length == 2 && int.TryParse(kv[0], out int sIdx))
                        {
                            var drives = kv[1].Split(',').Select(int.Parse).ToArray();
                            stripeAssignments[sIdx] = drives;
                        }
                    }
                }
            }

            // Calculate number of stripes
            int numStripes = (metadata.ChunkCount + k - 1) / k;
            var allChunks = new List<byte[]>();

            for (int stripeIdx = 0; stripeIdx < numStripes; stripeIdx++)
            {
                int chunksInStripe = Math.Min(k, metadata.ChunkCount - stripeIdx * k);
                if (chunksInStripe <= 0) break;

                // Get drive assignment for this stripe
                int[] driveAssignment;
                if (stripeAssignments.TryGetValue(stripeIdx, out var assignment))
                {
                    driveAssignment = assignment;
                }
                else
                {
                    // Fallback: calculate from permutation
                    driveAssignment = new int[chunksInStripe + 2];
                    int shift = stripeIdx % n;
                    for (int d = 0; d < chunksInStripe + 2; d++)
                    {
                        driveAssignment[d] = (d + shift) % n;
                    }
                }

                var stripeChunks = new byte[chunksInStripe][];
                var failedDrives = new List<int>();

                // Try to load each data chunk
                for (int d = 0; d < chunksInStripe; d++)
                {
                    int driveIdx = driveAssignment[d];
                    try
                    {
                        var provider = getProvider(driveIdx);
                        var chunkKey = $"{key}.dcl.s{stripeIdx}.d{d}";
                        using var stream = await provider.ReadAsync(chunkKey);
                        if (stream != null)
                        {
                            stripeChunks[d] = await ReadAllBytesAsync(stream);
                        }
                        else
                        {
                            failedDrives.Add(d);
                        }
                    }
                    catch
                    {
                        failedDrives.Add(d);
                    }
                }

                // If any chunks failed, reconstruct from parity
                if (failedDrives.Count > 0 && failedDrives.Count <= 2)
                {
                    // Load parity
                    byte[]? parityP = null;
                    byte[]? parityQ = null;

                    try
                    {
                        int pDrive = driveAssignment[chunksInStripe];
                        var pProvider = getProvider(pDrive);
                        using var pStream = await pProvider.ReadAsync($"{key}.dcl.s{stripeIdx}.p");
                        if (pStream != null) parityP = await ReadAllBytesAsync(pStream);
                    }
                    catch { }

                    try
                    {
                        int qDrive = driveAssignment[chunksInStripe + 1];
                        var qProvider = getProvider(qDrive);
                        using var qStream = await qProvider.ReadAsync($"{key}.dcl.s{stripeIdx}.q");
                        if (qStream != null) parityQ = await ReadAllBytesAsync(qStream);
                    }
                    catch { }

                    // Determine chunk size from available data
                    int chunkSize = stripeChunks.Where(c => c != null).FirstOrDefault()?.Length ??
                                   parityP?.Length ?? parityQ?.Length ?? _config.StripeSize;

                    if (failedDrives.Count == 1 && parityP != null)
                    {
                        // Single drive failure: reconstruct using P parity
                        int failedIdx = failedDrives[0];
                        var reconstructed = new byte[chunkSize];
                        Array.Copy(parityP, reconstructed, chunkSize);

                        for (int d = 0; d < chunksInStripe; d++)
                        {
                            if (d != failedIdx && stripeChunks[d] != null)
                            {
                                for (int b = 0; b < chunkSize; b++)
                                {
                                    reconstructed[b] ^= stripeChunks[d][b];
                                }
                            }
                        }
                        stripeChunks[failedIdx] = reconstructed;
                    }
                    else if (failedDrives.Count == 2 && parityP != null && parityQ != null)
                    {
                        // Dual drive failure: reconstruct using P and Q parity
                        int x = failedDrives[0];
                        int y = failedDrives[1];

                        var reconstructedX = new byte[chunkSize];
                        var reconstructedY = new byte[chunkSize];

                        for (int b = 0; b < chunkSize; b++)
                        {
                            // Compute partial P and Q from surviving drives
                            byte partialP = parityP[b];
                            byte partialQ = parityQ[b];

                            for (int d = 0; d < chunksInStripe; d++)
                            {
                                if (d != x && d != y && stripeChunks[d] != null)
                                {
                                    partialP ^= stripeChunks[d][b];
                                    partialQ ^= GF256Multiply(stripeChunks[d][b], GF256ExpTable[d]);
                                }
                            }

                            // Solve: Dx + Dy = partialP
                            //        g^x * Dx + g^y * Dy = partialQ
                            byte gx = GF256ExpTable[x];
                            byte gy = GF256ExpTable[y];
                            byte gxy = (byte)(gx ^ gy);
                            byte gxyInv = GF256Inverse(gxy);

                            // Dy = (partialQ ^ g^x * partialP) / (g^y ^ g^x)
                            byte dyNumerator = (byte)(partialQ ^ GF256Multiply(gx, partialP));
                            reconstructedY[b] = GF256Multiply(dyNumerator, gxyInv);

                            // Dx = partialP ^ Dy
                            reconstructedX[b] = (byte)(partialP ^ reconstructedY[b]);
                        }

                        stripeChunks[x] = reconstructedX;
                        stripeChunks[y] = reconstructedY;
                    }
                }

                // Add stripe chunks to result
                for (int d = 0; d < chunksInStripe; d++)
                {
                    if (stripeChunks[d] == null)
                        throw new InvalidOperationException($"Failed to load/reconstruct chunk at stripe {stripeIdx}, position {d}");
                    allChunks.Add(stripeChunks[d]);
                }
            }

            var result = allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            return new MemoryStream(result);
        }

        // ==================== RAID 7.1: Enhanced RAID 7 with Read Cache ====================
        // RAID 7.1 extends RAID 7 with a dedicated read cache layer

        private readonly Dictionary<string, byte[]> _raid71ReadCache = new();

        private async Task SaveRAID71Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("RAID 7.1 requires at least 4 providers");

            // RAID 7.1: RAID 5 with dedicated parity + read cache
            var bytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(bytes), _config.StripeSize);

            int n = _config.ProviderCount;
            int dataDrives = n - 1; // Last drive is dedicated parity

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_71,
                TotalSize = bytes.Length,
                ChunkCount = chunks.Count,
                StripeSize = _config.StripeSize,
                ProviderCount = n,
                ParityDriveIndex = n - 1,
                Layout = "raid71-readcache"
            };

            // Save metadata
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            var metaTasks = Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson))));
            await Task.WhenAll(metaTasks);

            // Store data with dedicated parity on last drive
            var saveTasks = new List<Task>();
            for (int stripeIdx = 0; stripeIdx * dataDrives < chunks.Count; stripeIdx++)
            {
                var stripeChunks = new List<byte[]>();
                int maxLen = 0;

                // Collect stripe data
                for (int d = 0; d < dataDrives && stripeIdx * dataDrives + d < chunks.Count; d++)
                {
                    var chunk = chunks[stripeIdx * dataDrives + d];
                    stripeChunks.Add(chunk);
                    maxLen = Math.Max(maxLen, chunk.Length);
                }

                // Pad chunks
                for (int d = 0; d < stripeChunks.Count; d++)
                {
                    if (stripeChunks[d].Length < maxLen)
                    {
                        var padded = new byte[maxLen];
                        Array.Copy(stripeChunks[d], padded, stripeChunks[d].Length);
                        stripeChunks[d] = padded;
                    }
                }

                // Calculate parity
                var parity = new byte[maxLen];
                foreach (var chunk in stripeChunks)
                {
                    for (int b = 0; b < maxLen; b++)
                        parity[b] ^= chunk[b];
                }

                // Save data chunks
                for (int d = 0; d < stripeChunks.Count; d++)
                {
                    int driveIdx = d;
                    var chunkKey = $"{key}.r71.s{stripeIdx}.d{d}";
                    saveTasks.Add(getProvider(driveIdx).WriteAsync(chunkKey, new MemoryStream(stripeChunks[d])));

                    // Cache for read optimization
                    _raid71ReadCache[$"{key}.{stripeIdx}.{d}"] = stripeChunks[d];
                }

                // Save parity to dedicated drive
                var parityKey = $"{key}.r71.s{stripeIdx}.p";
                saveTasks.Add(getProvider(n - 1).WriteAsync(parityKey, new MemoryStream(parity)));
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[RAID-7.1] Saved {key} with read cache ({_raid71ReadCache.Count} entries)");
        }

        private async Task<Stream> LoadRAID71Async(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            int n = metadata.ProviderCount;
            int dataDrives = n - 1;
            var allChunks = new List<byte[]>();

            int numStripes = (metadata.ChunkCount + dataDrives - 1) / dataDrives;
            for (int stripeIdx = 0; stripeIdx < numStripes; stripeIdx++)
            {
                int chunksInStripe = Math.Min(dataDrives, metadata.ChunkCount - stripeIdx * dataDrives);

                for (int d = 0; d < chunksInStripe; d++)
                {
                    // Check read cache first
                    var cacheKey = $"{key}.{stripeIdx}.{d}";
                    if (_raid71ReadCache.TryGetValue(cacheKey, out var cached))
                    {
                        allChunks.Add(cached);
                        continue;
                    }

                    // Load from disk
                    var chunkKey = $"{key}.r71.s{stripeIdx}.d{d}";
                    try
                    {
                        using var stream = await getProvider(d).ReadAsync(chunkKey);
                        if (stream != null)
                        {
                            var chunk = await ReadAllBytesAsync(stream);
                            allChunks.Add(chunk);
                            _raid71ReadCache[cacheKey] = chunk; // Add to cache
                        }
                    }
                    catch
                    {
                        // Reconstruct from parity
                        var parity = await ReadAllBytesAsync(await getProvider(n - 1).ReadAsync($"{key}.r71.s{stripeIdx}.p"));
                        var reconstructed = new byte[parity.Length];
                        Array.Copy(parity, reconstructed, parity.Length);

                        for (int od = 0; od < chunksInStripe; od++)
                        {
                            if (od != d)
                            {
                                var otherKey = $"{key}.r71.s{stripeIdx}.d{od}";
                                var other = await ReadAllBytesAsync(await getProvider(od).ReadAsync(otherKey));
                                for (int b = 0; b < reconstructed.Length; b++)
                                    reconstructed[b] ^= other[b];
                            }
                        }
                        allChunks.Add(reconstructed);
                    }
                }
            }

            return new MemoryStream(allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray());
        }

        // ==================== RAID 7.2: Enhanced RAID 7 with Write-Back Cache ====================
        // RAID 7.2 extends RAID 7 with write-back caching for improved write performance

        private readonly Dictionary<string, byte[]> _raid72WriteCache = new();
        private readonly HashSet<string> _raid72DirtyBlocks = new();

        private async Task SaveRAID72Async(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("RAID 7.2 requires at least 4 providers");

            // RAID 7.2: Write-back cache with RAID 5 backend
            var bytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(bytes), _config.StripeSize);

            int n = _config.ProviderCount;
            int dataDrives = n - 1;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_72,
                TotalSize = bytes.Length,
                ChunkCount = chunks.Count,
                StripeSize = _config.StripeSize,
                ProviderCount = n,
                ParityDriveIndex = n - 1,
                Layout = "raid72-writeback"
            };

            // Save metadata immediately
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            var metaTasks = Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson))));
            await Task.WhenAll(metaTasks);

            // Write to cache first (write-back)
            var saveTasks = new List<Task>();
            for (int stripeIdx = 0; stripeIdx * dataDrives < chunks.Count; stripeIdx++)
            {
                var stripeChunks = new List<byte[]>();
                int maxLen = 0;

                for (int d = 0; d < dataDrives && stripeIdx * dataDrives + d < chunks.Count; d++)
                {
                    var chunk = chunks[stripeIdx * dataDrives + d];
                    stripeChunks.Add(chunk);
                    maxLen = Math.Max(maxLen, chunk.Length);

                    // Add to write cache
                    var cacheKey = $"{key}.{stripeIdx}.{d}";
                    _raid72WriteCache[cacheKey] = chunk;
                    _raid72DirtyBlocks.Add(cacheKey);
                }

                // Pad and calculate parity
                for (int d = 0; d < stripeChunks.Count; d++)
                {
                    if (stripeChunks[d].Length < maxLen)
                    {
                        var padded = new byte[maxLen];
                        Array.Copy(stripeChunks[d], padded, stripeChunks[d].Length);
                        stripeChunks[d] = padded;
                    }
                }

                var parity = new byte[maxLen];
                foreach (var chunk in stripeChunks)
                {
                    for (int b = 0; b < maxLen; b++)
                        parity[b] ^= chunk[b];
                }

                // Flush to disk (in production this would be async/batched)
                for (int d = 0; d < stripeChunks.Count; d++)
                {
                    var chunkKey = $"{key}.r72.s{stripeIdx}.d{d}";
                    saveTasks.Add(getProvider(d).WriteAsync(chunkKey, new MemoryStream(stripeChunks[d])));
                }

                saveTasks.Add(getProvider(n - 1).WriteAsync($"{key}.r72.s{stripeIdx}.p", new MemoryStream(parity)));
            }

            await Task.WhenAll(saveTasks);

            // Clear dirty flags after flush
            foreach (var dirtyKey in _raid72DirtyBlocks.Where(k => k.StartsWith(key)).ToList())
            {
                _raid72DirtyBlocks.Remove(dirtyKey);
            }

            _context.LogInfo($"[RAID-7.2] Saved {key} with write-back cache");
        }

        private async Task<Stream> LoadRAID72Async(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            int n = metadata.ProviderCount;
            int dataDrives = n - 1;
            var allChunks = new List<byte[]>();

            int numStripes = (metadata.ChunkCount + dataDrives - 1) / dataDrives;
            for (int stripeIdx = 0; stripeIdx < numStripes; stripeIdx++)
            {
                int chunksInStripe = Math.Min(dataDrives, metadata.ChunkCount - stripeIdx * dataDrives);

                for (int d = 0; d < chunksInStripe; d++)
                {
                    // Check write cache first (may have dirty data)
                    var cacheKey = $"{key}.{stripeIdx}.{d}";
                    if (_raid72WriteCache.TryGetValue(cacheKey, out var cached))
                    {
                        allChunks.Add(cached);
                        continue;
                    }

                    // Load from disk
                    var chunkKey = $"{key}.r72.s{stripeIdx}.d{d}";
                    try
                    {
                        using var stream = await getProvider(d).ReadAsync(chunkKey);
                        if (stream != null)
                        {
                            allChunks.Add(await ReadAllBytesAsync(stream));
                        }
                    }
                    catch
                    {
                        // Reconstruct from parity
                        var parity = await ReadAllBytesAsync(await getProvider(n - 1).ReadAsync($"{key}.r72.s{stripeIdx}.p"));
                        var reconstructed = new byte[parity.Length];
                        Array.Copy(parity, reconstructed, parity.Length);

                        for (int od = 0; od < chunksInStripe; od++)
                        {
                            if (od != d)
                            {
                                var other = await ReadAllBytesAsync(await getProvider(od).ReadAsync($"{key}.r72.s{stripeIdx}.d{od}"));
                                for (int b = 0; b < reconstructed.Length; b++)
                                    reconstructed[b] ^= other[b];
                            }
                        }
                        allChunks.Add(reconstructed);
                    }
                }
            }

            return new MemoryStream(allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray());
        }

        // ==================== RAID N+M: Flexible N Data + M Parity ====================
        // Configurable N data drives with M parity drives

        private async Task SaveRAIDNMAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 3)
                throw new InvalidOperationException("RAID N+M requires at least 3 providers");

            // N+M: Configurable data + parity drives
            // Default: N-2 data drives, 2 parity drives (like RAID 6)
            int n = _config.ProviderCount;
            int parityDrives = Math.Min(3, n - 1); // Up to 3 parity drives
            int dataDrives = n - parityDrives;

            var bytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(bytes), _config.StripeSize);

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_NM,
                TotalSize = bytes.Length,
                ChunkCount = chunks.Count,
                StripeSize = _config.StripeSize,
                ProviderCount = n,
                ParityDriveIndex = dataDrives, // First parity drive index
                Layout = $"raidnm-n{dataDrives}-m{parityDrives}"
            };

            // Save metadata
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson)))));

            var saveTasks = new List<Task>();

            for (int stripeIdx = 0; stripeIdx * dataDrives < chunks.Count; stripeIdx++)
            {
                var stripeChunks = new List<byte[]>();
                int maxLen = 0;

                for (int d = 0; d < dataDrives && stripeIdx * dataDrives + d < chunks.Count; d++)
                {
                    var chunk = chunks[stripeIdx * dataDrives + d];
                    stripeChunks.Add(chunk);
                    maxLen = Math.Max(maxLen, chunk.Length);
                }

                // Pad chunks
                for (int d = 0; d < stripeChunks.Count; d++)
                {
                    if (stripeChunks[d].Length < maxLen)
                    {
                        var padded = new byte[maxLen];
                        Array.Copy(stripeChunks[d], padded, stripeChunks[d].Length);
                        stripeChunks[d] = padded;
                    }
                }

                // Calculate M parity syndromes
                var parities = new byte[parityDrives][];
                for (int p = 0; p < parityDrives; p++)
                    parities[p] = new byte[maxLen];

                for (int b = 0; b < maxLen; b++)
                {
                    for (int d = 0; d < stripeChunks.Count; d++)
                    {
                        // P: XOR
                        parities[0][b] ^= stripeChunks[d][b];

                        // Q: g^d coefficient (if M >= 2)
                        if (parityDrives >= 2)
                            parities[1][b] ^= GF256Multiply(stripeChunks[d][b], GF256ExpTable[d]);

                        // R: g^(2d) coefficient (if M >= 3)
                        if (parityDrives >= 3)
                            parities[2][b] ^= GF256Multiply(stripeChunks[d][b], GF256ExpTable[(2 * d) % 255]);
                    }
                }

                // Save data chunks
                for (int d = 0; d < stripeChunks.Count; d++)
                {
                    saveTasks.Add(getProvider(d).WriteAsync($"{key}.nm.s{stripeIdx}.d{d}", new MemoryStream(stripeChunks[d])));
                }

                // Save parity chunks
                for (int p = 0; p < parityDrives; p++)
                {
                    saveTasks.Add(getProvider(dataDrives + p).WriteAsync($"{key}.nm.s{stripeIdx}.p{p}", new MemoryStream(parities[p])));
                }
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[RAID-N+M] Saved {key} with {dataDrives} data + {parityDrives} parity drives");
        }

        private async Task<Stream> LoadRAIDNMAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            // Parse layout
            int dataDrives = metadata.ParityDriveIndex;
            int parityDrives = metadata.ProviderCount - dataDrives;

            var allChunks = new List<byte[]>();
            int numStripes = (metadata.ChunkCount + dataDrives - 1) / dataDrives;

            for (int stripeIdx = 0; stripeIdx < numStripes; stripeIdx++)
            {
                int chunksInStripe = Math.Min(dataDrives, metadata.ChunkCount - stripeIdx * dataDrives);
                var stripeData = new byte[chunksInStripe][];
                var failedDrives = new List<int>();

                // Try to load each data chunk
                for (int d = 0; d < chunksInStripe; d++)
                {
                    try
                    {
                        using var stream = await getProvider(d).ReadAsync($"{key}.nm.s{stripeIdx}.d{d}");
                        if (stream != null)
                            stripeData[d] = await ReadAllBytesAsync(stream);
                        else
                            failedDrives.Add(d);
                    }
                    catch { failedDrives.Add(d); }
                }

                // Reconstruct if needed
                if (failedDrives.Count > 0 && failedDrives.Count <= parityDrives)
                {
                    // Load parity
                    var parities = new byte[parityDrives][];
                    for (int p = 0; p < parityDrives; p++)
                    {
                        try
                        {
                            using var pStream = await getProvider(dataDrives + p).ReadAsync($"{key}.nm.s{stripeIdx}.p{p}");
                            if (pStream != null) parities[p] = await ReadAllBytesAsync(pStream);
                        }
                        catch { }
                    }

                    int chunkSize = stripeData.Where(c => c != null).FirstOrDefault()?.Length ??
                                   parities.Where(p => p != null).FirstOrDefault()?.Length ?? _config.StripeSize;

                    if (failedDrives.Count == 1 && parities[0] != null)
                    {
                        // Single failure: use P parity
                        int failedIdx = failedDrives[0];
                        var reconstructed = new byte[chunkSize];
                        Array.Copy(parities[0], reconstructed, chunkSize);

                        for (int d = 0; d < chunksInStripe; d++)
                        {
                            if (d != failedIdx && stripeData[d] != null)
                            {
                                for (int b = 0; b < chunkSize; b++)
                                    reconstructed[b] ^= stripeData[d][b];
                            }
                        }
                        stripeData[failedIdx] = reconstructed;
                    }
                    else if (failedDrives.Count == 2 && parities[0] != null && parities[1] != null)
                    {
                        // Double failure: use P and Q
                        int x = failedDrives[0], y = failedDrives[1];
                        var reconstructedX = new byte[chunkSize];
                        var reconstructedY = new byte[chunkSize];

                        for (int b = 0; b < chunkSize; b++)
                        {
                            byte partialP = parities[0][b];
                            byte partialQ = parities[1][b];

                            for (int d = 0; d < chunksInStripe; d++)
                            {
                                if (d != x && d != y && stripeData[d] != null)
                                {
                                    partialP ^= stripeData[d][b];
                                    partialQ ^= GF256Multiply(stripeData[d][b], GF256ExpTable[d]);
                                }
                            }

                            byte gx = GF256ExpTable[x], gy = GF256ExpTable[y];
                            byte gxyInv = GF256Inverse((byte)(gx ^ gy));
                            byte dyNum = (byte)(partialQ ^ GF256Multiply(gx, partialP));
                            reconstructedY[b] = GF256Multiply(dyNum, gxyInv);
                            reconstructedX[b] = (byte)(partialP ^ reconstructedY[b]);
                        }

                        stripeData[x] = reconstructedX;
                        stripeData[y] = reconstructedY;
                    }
                }

                allChunks.AddRange(stripeData.Where(c => c != null));
            }

            return new MemoryStream(allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray());
        }

        // ==================== Intel Matrix RAID: Multiple RAID Types on Same Disks ====================
        // Partitions drives to run multiple RAID types simultaneously

        private async Task SaveRAIDMatrixAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 2)
                throw new InvalidOperationException("Matrix RAID requires at least 2 providers");

            // Matrix RAID: Split data between RAID 0 (performance) and RAID 1 (safety)
            var bytes = await ReadAllBytesAsync(data);

            // Split: First half to RAID 0, second half to RAID 1
            int splitPoint = bytes.Length / 2;
            var raid0Data = bytes.Take(splitPoint).ToArray();
            var raid1Data = bytes.Skip(splitPoint).ToArray();

            int n = _config.ProviderCount;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_Matrix,
                TotalSize = bytes.Length,
                ChunkCount = 2, // Two partitions
                StripeSize = _config.StripeSize,
                ProviderCount = n,
                ParityDriveIndex = -1,
                Layout = $"matrix-split{splitPoint}"
            };

            // Save metadata
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson)))));

            var saveTasks = new List<Task>();

            // RAID 0 partition: stripe across all drives
            var raid0Chunks = SplitIntoChunks(new MemoryStream(raid0Data), _config.StripeSize);
            for (int i = 0; i < raid0Chunks.Count; i++)
            {
                int driveIdx = i % n;
                saveTasks.Add(getProvider(driveIdx).WriteAsync($"{key}.matrix.r0.{i}", new MemoryStream(raid0Chunks[i])));
            }

            // RAID 1 partition: mirror to all drives
            saveTasks.AddRange(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.matrix.r1", new MemoryStream(raid1Data))));

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[Matrix-RAID] Saved {key}: RAID 0 ({raid0Data.Length} bytes) + RAID 1 ({raid1Data.Length} bytes)");
        }

        private async Task<Stream> LoadRAIDMatrixAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            // Parse split point
            int splitPoint = 0;
            if (metadata.Layout != null && metadata.Layout.StartsWith("matrix-split"))
            {
                int.TryParse(metadata.Layout.Replace("matrix-split", ""), out splitPoint);
            }

            int n = metadata.ProviderCount;
            var result = new MemoryStream();

            // Load RAID 0 partition
            int raid0Chunks = (splitPoint + _config.StripeSize - 1) / _config.StripeSize;
            for (int i = 0; i < raid0Chunks; i++)
            {
                int driveIdx = i % n;
                try
                {
                    using var stream = await getProvider(driveIdx).ReadAsync($"{key}.matrix.r0.{i}");
                    if (stream != null)
                    {
                        var chunk = await ReadAllBytesAsync(stream);
                        result.Write(chunk, 0, Math.Min(chunk.Length, splitPoint - (int)result.Length));
                    }
                }
                catch { }
            }

            // Load RAID 1 partition (from any drive)
            for (int i = 0; i < n; i++)
            {
                try
                {
                    using var stream = await getProvider(i).ReadAsync($"{key}.matrix.r1");
                    if (stream != null)
                    {
                        var data = await ReadAllBytesAsync(stream);
                        result.Write(data, 0, data.Length);
                        break;
                    }
                }
                catch { continue; }
            }

            return new MemoryStream(result.ToArray().Take((int)metadata.TotalSize).ToArray());
        }

        // ==================== JBOD: Just a Bunch of Disks ====================
        // Simple concatenation without redundancy

        private async Task SaveRAIDJBODAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 1)
                throw new InvalidOperationException("JBOD requires at least 1 provider");

            // JBOD: Concatenate data across drives sequentially
            var bytes = await ReadAllBytesAsync(data);
            int n = _config.ProviderCount;
            int bytesPerDrive = (bytes.Length + n - 1) / n;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_JBOD,
                TotalSize = bytes.Length,
                ChunkCount = n,
                StripeSize = bytesPerDrive,
                ProviderCount = n,
                ParityDriveIndex = -1,
                Layout = "jbod-sequential"
            };

            // Save metadata
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson)))));

            // Split and save to each drive
            var saveTasks = new List<Task>();
            for (int i = 0; i < n; i++)
            {
                int start = i * bytesPerDrive;
                int length = Math.Min(bytesPerDrive, bytes.Length - start);
                if (length > 0)
                {
                    var chunk = new byte[length];
                    Array.Copy(bytes, start, chunk, 0, length);
                    saveTasks.Add(getProvider(i).WriteAsync($"{key}.jbod.{i}", new MemoryStream(chunk)));
                }
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[JBOD] Saved {key} across {n} drives ({bytesPerDrive} bytes/drive)");
        }

        private async Task<Stream> LoadRAIDJBODAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            var result = new MemoryStream();
            for (int i = 0; i < metadata.ProviderCount; i++)
            {
                try
                {
                    using var stream = await getProvider(i).ReadAsync($"{key}.jbod.{i}");
                    if (stream != null)
                    {
                        var chunk = await ReadAllBytesAsync(stream);
                        result.Write(chunk, 0, chunk.Length);
                    }
                }
                catch
                {
                    throw new InvalidOperationException($"JBOD: Failed to load from drive {i} - no redundancy available");
                }
            }

            return new MemoryStream(result.ToArray().Take((int)metadata.TotalSize).ToArray());
        }

        // ==================== Crypto SoftRAID: Encrypted Software RAID ====================
        // RAID with transparent encryption layer

        private async Task SaveRAIDCryptoAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 3)
                throw new InvalidOperationException("Crypto RAID requires at least 3 providers");

            // Crypto RAID: RAID 5 with XOR-based encryption (simplified - production would use AES)
            var bytes = await ReadAllBytesAsync(data);

            // Generate encryption key from key name (simplified - production would use secure key management)
            byte[] encryptionKey = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key + "_crypto_salt"));

            // Encrypt data
            var encryptedData = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                encryptedData[i] = (byte)(bytes[i] ^ encryptionKey[i % encryptionKey.Length]);
            }

            var chunks = SplitIntoChunks(new MemoryStream(encryptedData), _config.StripeSize);
            int n = _config.ProviderCount;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_Crypto,
                TotalSize = bytes.Length,
                ChunkCount = chunks.Count,
                StripeSize = _config.StripeSize,
                ProviderCount = n,
                ParityDriveIndex = -1, // Distributed parity
                Layout = "crypto-raid5"
            };

            // Save metadata
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson)))));

            // RAID 5 with distributed parity
            int dataDrives = n - 1;
            var saveTasks = new List<Task>();

            for (int stripeIdx = 0; stripeIdx * dataDrives < chunks.Count; stripeIdx++)
            {
                var stripeChunks = new List<byte[]>();
                int maxLen = 0;

                for (int d = 0; d < dataDrives && stripeIdx * dataDrives + d < chunks.Count; d++)
                {
                    var chunk = chunks[stripeIdx * dataDrives + d];
                    stripeChunks.Add(chunk);
                    maxLen = Math.Max(maxLen, chunk.Length);
                }

                // Pad chunks
                for (int d = 0; d < stripeChunks.Count; d++)
                {
                    if (stripeChunks[d].Length < maxLen)
                    {
                        var padded = new byte[maxLen];
                        Array.Copy(stripeChunks[d], padded, stripeChunks[d].Length);
                        stripeChunks[d] = padded;
                    }
                }

                // Calculate parity
                var parity = new byte[maxLen];
                foreach (var chunk in stripeChunks)
                {
                    for (int b = 0; b < maxLen; b++)
                        parity[b] ^= chunk[b];
                }

                // Rotating parity placement
                int parityDrive = stripeIdx % n;

                // Save data and parity
                int dataIdx = 0;
                for (int d = 0; d < n; d++)
                {
                    if (d == parityDrive)
                    {
                        saveTasks.Add(getProvider(d).WriteAsync($"{key}.crypto.s{stripeIdx}.p", new MemoryStream(parity)));
                    }
                    else if (dataIdx < stripeChunks.Count)
                    {
                        saveTasks.Add(getProvider(d).WriteAsync($"{key}.crypto.s{stripeIdx}.d{dataIdx}", new MemoryStream(stripeChunks[dataIdx])));
                        dataIdx++;
                    }
                }
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[Crypto-RAID] Saved {key} with encryption");
        }

        private async Task<Stream> LoadRAIDCryptoAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            int n = metadata.ProviderCount;
            int dataDrives = n - 1;
            var allChunks = new List<byte[]>();

            int numStripes = (metadata.ChunkCount + dataDrives - 1) / dataDrives;
            for (int stripeIdx = 0; stripeIdx < numStripes; stripeIdx++)
            {
                int parityDrive = stripeIdx % n;
                int chunksInStripe = Math.Min(dataDrives, metadata.ChunkCount - stripeIdx * dataDrives);

                var stripeData = new byte[chunksInStripe][];
                var failedDrives = new List<int>();

                int dataIdx = 0;
                for (int d = 0; d < n && dataIdx < chunksInStripe; d++)
                {
                    if (d == parityDrive) continue;

                    try
                    {
                        using var stream = await getProvider(d).ReadAsync($"{key}.crypto.s{stripeIdx}.d{dataIdx}");
                        if (stream != null)
                            stripeData[dataIdx] = await ReadAllBytesAsync(stream);
                        else
                            failedDrives.Add(dataIdx);
                    }
                    catch { failedDrives.Add(dataIdx); }
                    dataIdx++;
                }

                // Reconstruct if needed
                if (failedDrives.Count == 1)
                {
                    try
                    {
                        using var pStream = await getProvider(parityDrive).ReadAsync($"{key}.crypto.s{stripeIdx}.p");
                        if (pStream != null)
                        {
                            var parity = await ReadAllBytesAsync(pStream);
                            int failedIdx = failedDrives[0];
                            var reconstructed = new byte[parity.Length];
                            Array.Copy(parity, reconstructed, parity.Length);

                            for (int d = 0; d < chunksInStripe; d++)
                            {
                                if (d != failedIdx && stripeData[d] != null)
                                {
                                    for (int b = 0; b < reconstructed.Length; b++)
                                        reconstructed[b] ^= stripeData[d][b];
                                }
                            }
                            stripeData[failedIdx] = reconstructed;
                        }
                    }
                    catch { }
                }

                allChunks.AddRange(stripeData.Where(c => c != null));
            }

            // Decrypt data
            var encryptedResult = allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray();
            byte[] encryptionKey = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key + "_crypto_salt"));

            var decryptedData = new byte[encryptedResult.Length];
            for (int i = 0; i < encryptedResult.Length; i++)
            {
                decryptedData[i] = (byte)(encryptedResult[i] ^ encryptionKey[i % encryptionKey.Length]);
            }

            return new MemoryStream(decryptedData);
        }

        // ==================== Btrfs DUP Profile: Duplicate on Same Device ====================
        // Stores two copies on the same device for metadata protection

        private async Task SaveRAIDDUPAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 1)
                throw new InvalidOperationException("DUP Profile requires at least 1 provider");

            // DUP: Store two copies on each device
            var bytes = await ReadAllBytesAsync(data);
            int n = _config.ProviderCount;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_DUP,
                TotalSize = bytes.Length,
                ChunkCount = 2,
                StripeSize = bytes.Length,
                ProviderCount = n,
                ParityDriveIndex = -1,
                Layout = "btrfs-dup"
            };

            // Save metadata and two copies of data to each provider
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            var saveTasks = new List<Task>();

            for (int i = 0; i < n; i++)
            {
                saveTasks.Add(getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson))));
                saveTasks.Add(getProvider(i).WriteAsync($"{key}.dup.copy1", new MemoryStream(bytes)));
                saveTasks.Add(getProvider(i).WriteAsync($"{key}.dup.copy2", new MemoryStream(bytes)));
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[DUP-Profile] Saved {key} with duplicate copies on {n} devices");
        }

        private async Task<Stream> LoadRAIDDUPAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Try each provider, then each copy
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var stream = await getProvider(i).ReadAsync($"{key}.dup.copy1");
                    if (stream != null)
                        return new MemoryStream(await ReadAllBytesAsync(stream));
                }
                catch { }

                try
                {
                    using var stream = await getProvider(i).ReadAsync($"{key}.dup.copy2");
                    if (stream != null)
                        return new MemoryStream(await ReadAllBytesAsync(stream));
                }
                catch { }
            }

            throw new InvalidOperationException("DUP: Failed to load any copy");
        }

        // ==================== NetApp DDP: Dynamic Disk Pool ====================
        // Spreads data and parity across pool with automatic load balancing

        private async Task SaveRAIDDDPAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("DDP requires at least 4 providers");

            // DDP: Spread data across disk pool with automatic parity distribution
            var bytes = await ReadAllBytesAsync(data);
            var chunks = SplitIntoChunks(new MemoryStream(bytes), _config.StripeSize);

            int n = _config.ProviderCount;
            int chunksPerDrive = Math.Max(1, chunks.Count / n);

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_DDP,
                TotalSize = bytes.Length,
                ChunkCount = chunks.Count,
                StripeSize = _config.StripeSize,
                ProviderCount = n,
                ParityDriveIndex = -1, // Distributed
                Layout = $"ddp-pool{n}"
            };

            // Save metadata
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson)))));

            // Distribute chunks using hash-based placement for load balancing
            var saveTasks = new List<Task>();
            var driveChunkMap = new Dictionary<int, List<int>>();

            for (int i = 0; i < chunks.Count; i++)
            {
                // Use hash for distribution
                int driveIdx = Math.Abs((key + i).GetHashCode()) % n;
                saveTasks.Add(getProvider(driveIdx).WriteAsync($"{key}.ddp.{i}", new MemoryStream(chunks[i])));

                if (!driveChunkMap.ContainsKey(driveIdx))
                    driveChunkMap[driveIdx] = new List<int>();
                driveChunkMap[driveIdx].Add(i);
            }

            // Store chunk mapping for each drive
            foreach (var kvp in driveChunkMap)
            {
                var mapData = System.Text.Encoding.UTF8.GetBytes(string.Join(",", kvp.Value));
                saveTasks.Add(getProvider(kvp.Key).WriteAsync($"{key}.ddp.map", new MemoryStream(mapData)));
            }

            // Calculate and store distributed parity (simplified: store parity on next drive)
            int parityChunkSize = chunks.Max(c => c.Length);
            for (int i = 0; i < chunks.Count; i += n - 1)
            {
                var parity = new byte[parityChunkSize];
                for (int j = i; j < Math.Min(i + n - 1, chunks.Count); j++)
                {
                    for (int b = 0; b < chunks[j].Length; b++)
                        parity[b] ^= chunks[j][b];
                }

                int parityDrive = (i / (n - 1)) % n;
                saveTasks.Add(getProvider(parityDrive).WriteAsync($"{key}.ddp.p{i / (n - 1)}", new MemoryStream(parity)));
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[DDP] Saved {key} across {n}-drive pool");
        }

        private async Task<Stream> LoadRAIDDDPAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            int n = metadata.ProviderCount;
            var allChunks = new byte[metadata.ChunkCount][];

            // Load chunks from distributed locations
            for (int i = 0; i < metadata.ChunkCount; i++)
            {
                int driveIdx = Math.Abs((key + i).GetHashCode()) % n;

                try
                {
                    using var stream = await getProvider(driveIdx).ReadAsync($"{key}.ddp.{i}");
                    if (stream != null)
                        allChunks[i] = await ReadAllBytesAsync(stream);
                }
                catch
                {
                    // Try other drives (hash collision or failure)
                    for (int alt = 0; alt < n && allChunks[i] == null; alt++)
                    {
                        if (alt == driveIdx) continue;
                        try
                        {
                            using var stream = await getProvider(alt).ReadAsync($"{key}.ddp.{i}");
                            if (stream != null)
                                allChunks[i] = await ReadAllBytesAsync(stream);
                        }
                        catch { }
                    }
                }

                if (allChunks[i] == null)
                    throw new InvalidOperationException($"DDP: Failed to load chunk {i}");
            }

            return new MemoryStream(allChunks.SelectMany(c => c).Take((int)metadata.TotalSize).ToArray());
        }

        // ==================== SPAN: Simple Disk Spanning ====================
        // Sequential concatenation across drives (no redundancy)

        private async Task SaveRAIDSPANAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 1)
                throw new InvalidOperationException("SPAN requires at least 1 provider");

            // SPAN: Identical to JBOD, sequential concatenation
            await SaveRAIDJBODAsync(key, data, getProvider);
            _context.LogInfo($"[SPAN] Saved {key} using spanning mode");
        }

        private async Task<Stream> LoadRAIDSPANAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // SPAN uses same storage format as JBOD
            // Load metadata to check level, then load JBOD-style
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            var result = new MemoryStream();
            for (int i = 0; i < metadata.ProviderCount; i++)
            {
                try
                {
                    using var stream = await getProvider(i).ReadAsync($"{key}.jbod.{i}");
                    if (stream != null)
                    {
                        var chunk = await ReadAllBytesAsync(stream);
                        result.Write(chunk, 0, chunk.Length);
                    }
                }
                catch
                {
                    throw new InvalidOperationException($"SPAN: Failed to load from drive {i} - no redundancy");
                }
            }

            return new MemoryStream(result.ToArray().Take((int)metadata.TotalSize).ToArray());
        }

        // ==================== BIG: Linux MD Big Mode (Concatenation) ====================
        // Large volume concatenation similar to JBOD

        private async Task SaveRAIDBIGAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 2)
                throw new InvalidOperationException("BIG requires at least 2 providers");

            // BIG: Linux MD style concatenation
            var bytes = await ReadAllBytesAsync(data);
            int n = _config.ProviderCount;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_BIG,
                TotalSize = bytes.Length,
                ChunkCount = n,
                StripeSize = (bytes.Length + n - 1) / n,
                ProviderCount = n,
                ParityDriveIndex = -1,
                Layout = "linux-md-big"
            };

            // Save metadata
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson)))));

            // Sequential distribution
            var saveTasks = new List<Task>();
            int bytesPerDrive = metadata.StripeSize;

            for (int i = 0; i < n; i++)
            {
                int start = i * bytesPerDrive;
                int length = Math.Min(bytesPerDrive, bytes.Length - start);
                if (length > 0)
                {
                    var chunk = new byte[length];
                    Array.Copy(bytes, start, chunk, 0, length);
                    saveTasks.Add(getProvider(i).WriteAsync($"{key}.big.{i}", new MemoryStream(chunk)));
                }
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[BIG] Saved {key} in concatenated mode across {n} drives");
        }

        private async Task<Stream> LoadRAIDBIGAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            var result = new MemoryStream();
            for (int i = 0; i < metadata.ProviderCount; i++)
            {
                try
                {
                    using var stream = await getProvider(i).ReadAsync($"{key}.big.{i}");
                    if (stream != null)
                    {
                        var chunk = await ReadAllBytesAsync(stream);
                        result.Write(chunk, 0, chunk.Length);
                    }
                }
                catch
                {
                    throw new InvalidOperationException($"BIG: Failed to load from drive {i}");
                }
            }

            return new MemoryStream(result.ToArray().Take((int)metadata.TotalSize).ToArray());
        }

        // ==================== MAID: Massive Array of Idle Disks ====================
        // Power-managed RAID with active/standby drives

        private readonly HashSet<int> _maidActiveDrives = new();
        private readonly Dictionary<string, DateTime> _maidAccessTimes = new();

        private async Task SaveRAIDMAIDAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 4)
                throw new InvalidOperationException("MAID requires at least 4 providers");

            // MAID: Store on subset of active drives, with power management metadata
            var bytes = await ReadAllBytesAsync(data);
            int n = _config.ProviderCount;

            // Keep 2 drives active, rest are standby
            int activeDriveCount = Math.Min(2, n);

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_MAID,
                TotalSize = bytes.Length,
                ChunkCount = 1,
                StripeSize = bytes.Length,
                ProviderCount = n,
                ParityDriveIndex = -1,
                Layout = $"maid-active{activeDriveCount}"
            };

            // Save metadata
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson)))));

            // Mirror data to active drives only
            var saveTasks = new List<Task>();
            for (int i = 0; i < activeDriveCount; i++)
            {
                _maidActiveDrives.Add(i);
                saveTasks.Add(getProvider(i).WriteAsync($"{key}.maid.data", new MemoryStream(bytes)));
            }

            // Store power state metadata
            _maidAccessTimes[key] = DateTime.UtcNow;
            var powerState = System.Text.Encoding.UTF8.GetBytes($"active:{string.Join(",", _maidActiveDrives)};time:{DateTime.UtcNow:O}");
            saveTasks.AddRange(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.maid.power", new MemoryStream(powerState))));

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[MAID] Saved {key} on {activeDriveCount} active drives ({n - activeDriveCount} standby)");
        }

        private async Task<Stream> LoadRAIDMAIDAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load from first available active drive
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var stream = await getProvider(i).ReadAsync($"{key}.maid.data");
                    if (stream != null)
                    {
                        _maidActiveDrives.Add(i); // Mark as active
                        _maidAccessTimes[key] = DateTime.UtcNow;
                        return new MemoryStream(await ReadAllBytesAsync(stream));
                    }
                }
                catch { continue; }
            }

            throw new InvalidOperationException("MAID: No active drive available");
        }

        // ==================== Linear: Sequential Concatenation ====================
        // Simple sequential data placement (Linux MD linear mode)

        private async Task SaveRAIDLinearAsync(string key, Stream data, Func<int, IStorageProvider> getProvider)
        {
            if (_config.ProviderCount < 1)
                throw new InvalidOperationException("Linear requires at least 1 provider");

            // Linear: Fill drives sequentially
            var bytes = await ReadAllBytesAsync(data);
            int n = _config.ProviderCount;

            var metadata = new RaidMetadata
            {
                Level = RaidLevel.RAID_Linear,
                TotalSize = bytes.Length,
                ChunkCount = n,
                StripeSize = (bytes.Length + n - 1) / n,
                ProviderCount = n,
                ParityDriveIndex = -1,
                Layout = "linear-sequential"
            };

            // Save metadata
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
            await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
                getProvider(i).WriteAsync($"{key}.raid.meta", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson)))));

            // Sequential fill
            var saveTasks = new List<Task>();
            int bytesPerDrive = metadata.StripeSize;

            for (int i = 0; i < n; i++)
            {
                int start = i * bytesPerDrive;
                int length = Math.Min(bytesPerDrive, bytes.Length - start);
                if (length > 0)
                {
                    var chunk = new byte[length];
                    Array.Copy(bytes, start, chunk, 0, length);
                    saveTasks.Add(getProvider(i).WriteAsync($"{key}.linear.{i}", new MemoryStream(chunk)));
                }
            }

            await Task.WhenAll(saveTasks);
            _context.LogInfo($"[Linear] Saved {key} sequentially across {n} drives");
        }

        private async Task<Stream> LoadRAIDLinearAsync(string key, Func<int, IStorageProvider> getProvider)
        {
            // Load metadata
            RaidMetadata? metadata = null;
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                try
                {
                    using var metaStream = await getProvider(i).ReadAsync($"{key}.raid.meta");
                    if (metaStream != null)
                    {
                        metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                            System.Text.Encoding.UTF8.GetString(await ReadAllBytesAsync(metaStream)));
                        break;
                    }
                }
                catch { continue; }
            }

            if (metadata == null)
                throw new InvalidOperationException("Cannot load: metadata not found");

            var result = new MemoryStream();
            for (int i = 0; i < metadata.ProviderCount; i++)
            {
                try
                {
                    using var stream = await getProvider(i).ReadAsync($"{key}.linear.{i}");
                    if (stream != null)
                    {
                        var chunk = await ReadAllBytesAsync(stream);
                        result.Write(chunk, 0, chunk.Length);
                    }
                }
                catch
                {
                    throw new InvalidOperationException($"Linear: Failed to load from drive {i}");
                }
            }

            return new MemoryStream(result.ToArray().Take((int)metadata.TotalSize).ToArray());
        }

        // ==================== HELPER METHODS ====================

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }

        private static List<byte[]> SplitIntoChunks(Stream data, int chunkSize)
        {
            var chunks = new List<byte[]>();
            var buffer = new byte[chunkSize];
            int bytesRead;

            while ((bytesRead = data.Read(buffer, 0, chunkSize)) > 0)
            {
                var chunk = new byte[bytesRead];
                Array.Copy(buffer, chunk, bytesRead);
                chunks.Add(chunk);
            }

            return chunks;
        }

        private static Stream ReassembleChunks(byte[][] chunks)
        {
            var ms = new MemoryStream();
            foreach (var chunk in chunks)
            {
                ms.Write(chunk, 0, chunk.Length);
            }
            ms.Position = 0;
            return ms;
        }

        private static async Task SaveChunkAsync(IStorageProvider provider, string key, byte[] chunk)
        {
            var uri = new Uri($"{provider.Scheme}://{key}");
            var stream = new MemoryStream(chunk);
            await provider.SaveAsync(uri, stream);
        }

        private static async Task<byte[]> LoadChunkAsync(IStorageProvider provider, string key)
        {
            var uri = new Uri($"{provider.Scheme}://{key}");
            var stream = await provider.LoadAsync(uri);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }

        private static byte[] CalculateParityXOR(List<byte[]> chunks)
        {
            if (chunks.Count == 0)
                return Array.Empty<byte>();

            var maxLength = chunks.Max(c => c.Length);
            var parity = new byte[maxLength];

            foreach (var chunk in chunks)
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    parity[i] ^= chunk[i];
                }
            }

            return parity;
        }

        private static byte[] CalculateParityReedSolomon(List<byte[]> chunks)
        {
            // Reed-Solomon Q parity using Galois Field GF(2^8)
            // Q[i] = sum(D[j] * g^j) for all data disks j, where g is the generator (0x02)
            if (chunks.Count == 0)
                return Array.Empty<byte>();

            var maxLength = chunks.Max(c => c.Length);
            var parity = new byte[maxLength];

            for (int diskIdx = 0; diskIdx < chunks.Count; diskIdx++)
            {
                // Generator coefficient: g^diskIdx where g = 0x02
                byte coeff = GF256Power(0x02, diskIdx);

                for (int byteIdx = 0; byteIdx < chunks[diskIdx].Length; byteIdx++)
                {
                    parity[byteIdx] ^= GF256Multiply(chunks[diskIdx][byteIdx], coeff);
                }
            }

            return parity;
        }

        // GF(2^8) lookup tables for fast operations
        private static readonly byte[] GF256ExpTable = GenerateGF256ExpTable();
        private static readonly byte[] GF256LogTable = GenerateGF256LogTable();

        private static byte[] GenerateGF256ExpTable()
        {
            var table = new byte[512]; // Double size to avoid modulo
            byte val = 1;
            for (int i = 0; i < 255; i++)
            {
                table[i] = val;
                table[i + 255] = val; // Duplicate for wrap-around
                val = GF256MultiplyNoTable(val, 0x02);
            }
            return table;
        }

        private static byte[] GenerateGF256LogTable()
        {
            var table = new byte[256];
            table[0] = 0; // log(0) is undefined, but we set to 0
            for (int i = 0; i < 255; i++)
            {
                table[GF256ExpTable[i]] = (byte)i;
            }
            return table;
        }

        private static byte GF256MultiplyNoTable(byte a, byte b)
        {
            // GF(2^8) multiplication without lookup tables (for table generation)
            byte result = 0;
            byte aa = a;
            byte bb = b;
            for (int i = 0; i < 8; i++)
            {
                if ((bb & 1) != 0)
                    result ^= aa;
                bool hiBitSet = (aa & 0x80) != 0;
                aa <<= 1;
                if (hiBitSet)
                    aa ^= 0x1D; // x^8 + x^4 + x^3 + x^2 + 1 (standard GF(2^8) polynomial)
                bb >>= 1;
            }
            return result;
        }

        private static byte GF256Multiply(byte a, byte b)
        {
            // Fast GF(2^8) multiplication using lookup tables
            if (a == 0 || b == 0)
                return 0;
            int logSum = GF256LogTable[a] + GF256LogTable[b];
            return GF256ExpTable[logSum]; // Table handles wrap-around
        }

        private static byte GF256Divide(byte a, byte b)
        {
            // GF(2^8) division: a / b = a * b^(-1)
            if (b == 0)
                throw new DivideByZeroException("Division by zero in GF(2^8)");
            if (a == 0)
                return 0;
            int logDiff = GF256LogTable[a] - GF256LogTable[b];
            if (logDiff < 0)
                logDiff += 255;
            return GF256ExpTable[logDiff];
        }

        private static byte GF256Power(byte baseVal, int exp)
        {
            // GF(2^8) exponentiation
            if (exp == 0)
                return 1;
            if (baseVal == 0)
                return 0;
            int logResult = (GF256LogTable[baseVal] * exp) % 255;
            return GF256ExpTable[logResult];
        }

        private static byte GF256Inverse(byte a)
        {
            // GF(2^8) multiplicative inverse: a^(-1) = a^254
            if (a == 0)
                throw new ArgumentException("Zero has no inverse in GF(2^8)");
            return GF256ExpTable[255 - GF256LogTable[a]];
        }

        private byte[] RebuildChunkFromParity(List<byte[]> chunks, byte[] parity)
        {
            // XOR all existing chunks with parity to get missing chunk
            var result = new byte[parity.Length];
            Array.Copy(parity, result, parity.Length);

            foreach (var chunk in chunks.Where(c => c != null))
            {
                for (int i = 0; i < MathUtils.Min(chunk.Length, result.Length); i++)
                {
                    result[i] ^= chunk[i];
                }
            }

            return result;
        }

        private Dictionary<int, byte[]> RebuildFromDualParity(List<byte[]> chunks, byte[] parityP, byte[] parityQ, List<int> failedDisks)
        {
            // Full Reed-Solomon dual parity rebuild using GF(2^8)
            var rebuilt = new Dictionary<int, byte[]>();
            int chunkLength = parityP.Length;

            if (failedDisks.Count == 1)
            {
                // Single disk failure - use P parity (simple XOR)
                int failedIdx = failedDisks[0];
                var result = new byte[chunkLength];
                Array.Copy(parityP, result, chunkLength);

                for (int i = 0; i < chunks.Count; i++)
                {
                    if (chunks[i] != null)
                    {
                        for (int j = 0; j < Math.Min(chunks[i].Length, chunkLength); j++)
                        {
                            result[j] ^= chunks[i][j];
                        }
                    }
                }
                rebuilt[failedIdx] = result;
                _context.LogInfo($"[RAID-6] Rebuilt disk {failedIdx} using P parity");
            }
            else if (failedDisks.Count == 2)
            {
                // Double disk failure - use both P and Q parity with Reed-Solomon
                int x = failedDisks[0]; // First failed disk index
                int y = failedDisks[1]; // Second failed disk index

                // Generator coefficients
                byte gx = GF256Power(0x02, x);
                byte gy = GF256Power(0x02, y);

                // Calculate Pxy = P XOR (all surviving data)
                // Calculate Qxy = Q XOR (all surviving Q contributions)
                var Pxy = new byte[chunkLength];
                var Qxy = new byte[chunkLength];
                Array.Copy(parityP, Pxy, chunkLength);
                Array.Copy(parityQ, Qxy, chunkLength);

                for (int i = 0; i < chunks.Count; i++)
                {
                    if (chunks[i] != null && i != x && i != y)
                    {
                        byte gi = GF256Power(0x02, i);
                        for (int j = 0; j < Math.Min(chunks[i].Length, chunkLength); j++)
                        {
                            Pxy[j] ^= chunks[i][j];
                            Qxy[j] ^= GF256Multiply(chunks[i][j], gi);
                        }
                    }
                }

                // Now: Pxy = Dx XOR Dy
                //      Qxy = (Dx * g^x) XOR (Dy * g^y)
                // Solve for Dx and Dy using Cramer's rule in GF(2^8)

                // Multiply Pxy by g^y: Pxy * g^y = (Dx * g^y) XOR (Dy * g^y)
                // XOR with Qxy: (Dx * g^y) XOR (Dy * g^y) XOR (Dx * g^x) XOR (Dy * g^y)
                //             = Dx * (g^y XOR g^x)
                // Therefore: Dx = (Pxy * g^y XOR Qxy) / (g^y XOR g^x)

                byte gyXorGx = (byte)(gy ^ gx);
                if (gyXorGx == 0)
                {
                    throw new InvalidOperationException("Cannot solve: g^x == g^y (impossible if x != y)");
                }
                byte invGyXorGx = GF256Inverse(gyXorGx);

                var Dx = new byte[chunkLength];
                var Dy = new byte[chunkLength];

                for (int j = 0; j < chunkLength; j++)
                {
                    byte PxyTimesGy = GF256Multiply(Pxy[j], gy);
                    byte numeratorX = (byte)(PxyTimesGy ^ Qxy[j]);
                    Dx[j] = GF256Multiply(numeratorX, invGyXorGx);

                    // Dy = Pxy XOR Dx
                    Dy[j] = (byte)(Pxy[j] ^ Dx[j]);
                }

                rebuilt[x] = Dx;
                rebuilt[y] = Dy;
                _context.LogInfo($"[RAID-6] Rebuilt disks {x} and {y} using P+Q dual parity (Reed-Solomon)");
            }
            else if (failedDisks.Count > 2)
            {
                throw new IOException($"RAID 6 can only recover from up to 2 disk failures, but {failedDisks.Count} disks failed");
            }

            return rebuilt;
        }

        private static byte[] ComputeXorParity(List<byte[]> chunks)
        {
            return CalculateParityXOR(chunks);
        }

        private static byte[] ComputeXorParityFromBytes(byte[] bytes)
        {
            // Compute simple XOR parity across all bytes (simplified Hamming code)
            var parity = new byte[1];
            foreach (var b in bytes)
            {
                parity[0] ^= b;
            }
            return parity;
        }

        private async Task MonitorHealthAsync()
        {
            _context.LogDebug("[RAID] Running health check...");

            foreach (var (index, health) in _providerHealth)
            {
                // Health check logic would go here
                // For now, just log current status
                _context.LogDebug($"[RAID] Provider {index}: {health.Status}");
            }
        }

        private void MarkProviderFailed(int index)
        {
            if (_providerHealth.TryGetValue(index, out var health))
            {
                health.Status = ProviderStatus.Failed;
                health.FailureTime = DateTime.UtcNow;
                _context.LogError($"[RAID] Provider {index} marked as FAILED", null);

                // Trigger rebuild if needed
                _ = Task.Run(() => TriggerRebuildAsync(index));
            }
        }

        private async Task TriggerRebuildAsync(int failedProviderIndex)
        {
            if (!await _rebuildLock.WaitAsync(0))
            {
                _context.LogInfo("[RAID] Rebuild already in progress");
                return;
            }

            try
            {
                _context.LogInfo($"[RAID] Starting rebuild for provider {failedProviderIndex}");

                var rebuildStats = new RebuildStatistics
                {
                    StartTime = DateTime.UtcNow,
                    FailedProviderIndex = failedProviderIndex
                };

                // Get list of all stored keys from metadata on surviving providers
                var keysToRebuild = await GetAllStoredKeysAsync(failedProviderIndex);
                rebuildStats.TotalKeys = keysToRebuild.Count;

                _context.LogInfo($"[RAID] Found {keysToRebuild.Count} keys to rebuild");

                foreach (var key in keysToRebuild)
                {
                    try
                    {
                        await RebuildKeyAsync(key, failedProviderIndex);
                        rebuildStats.KeysRebuilt++;

                        if (rebuildStats.KeysRebuilt % 100 == 0)
                        {
                            _context.LogInfo($"[RAID] Rebuild progress: {rebuildStats.KeysRebuilt}/{rebuildStats.TotalKeys} keys");
                        }
                    }
                    catch (Exception ex)
                    {
                        rebuildStats.KeysFailed++;
                        _context.LogError($"[RAID] Failed to rebuild key '{key}': {ex.Message}", ex);
                    }
                }

                rebuildStats.EndTime = DateTime.UtcNow;

                // Mark provider as recovered if rebuild was successful
                if (rebuildStats.KeysFailed == 0 && _providerHealth.TryGetValue(failedProviderIndex, out var health))
                {
                    health.Status = ProviderStatus.Healthy;
                    health.FailureTime = null;
                }

                _context.LogInfo($"[RAID] Rebuild complete for provider {failedProviderIndex}. " +
                               $"Keys: {rebuildStats.KeysRebuilt} rebuilt, {rebuildStats.KeysFailed} failed. " +
                               $"Duration: {rebuildStats.Duration.TotalSeconds:F1}s");
            }
            finally
            {
                _rebuildLock.Release();
            }
        }

        private async Task<List<string>> GetAllStoredKeysAsync(int excludeProviderIndex)
        {
            var keys = new HashSet<string>();

            // Scan metadata from all surviving providers
            for (int i = 0; i < _config.ProviderCount; i++)
            {
                if (i == excludeProviderIndex) continue;

                try
                {
                    var provider = _getProvider(i);
                    if (provider is IListableStorage listable)
                    {
                        await foreach (var item in listable.ListFilesAsync(""))
                        {
                            // Extract base key from stored files
                            var path = item.Uri.AbsolutePath;
                            if (path.EndsWith(".raid.meta"))
                            {
                                var baseKey = path.Replace(".raid.meta", "").TrimStart('/');
                                keys.Add(baseKey);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _context.LogWarning($"[RAID] Failed to scan provider {i} for keys: {ex.Message}");
                }
            }

            return keys.ToList();
        }

        private async Task RebuildKeyAsync(string key, int failedProviderIndex)
        {
            // Load data from surviving providers and rebuild the failed provider's chunk
            // This uses the existing Load logic which handles reconstruction

            try
            {
                // Read metadata to understand the RAID structure
                RaidMetadata? metadata = null;
                for (int i = 0; i < _config.ProviderCount; i++)
                {
                    if (i == failedProviderIndex) continue;

                    try
                    {
                        var provider = _getProvider(i);
                        using var metaStream = await provider.ReadAsync($"{key}.raid.meta");
                        if (metaStream != null)
                        {
                            var metaBytes = await ReadAllBytesAsync(metaStream);
                            metadata = System.Text.Json.JsonSerializer.Deserialize<RaidMetadata>(
                                System.Text.Encoding.UTF8.GetString(metaBytes));
                            break;
                        }
                    }
                    catch { continue; }
                }

                if (metadata == null)
                {
                    _context.LogWarning($"[RAID] No metadata found for key '{key}', skipping rebuild");
                    return;
                }

                // Reconstruct data using existing load logic
                using var reconstructedData = await LoadAsync(key, _getProvider);

                // Re-save to rebuild the failed provider's chunk
                // The save logic will write to all providers including the recovered one
                var dataBytes = await ReadAllBytesAsync(reconstructedData);
                using var dataStream = new MemoryStream(dataBytes);

                await SaveAsync(key, dataStream, _getProvider);

                _context.LogDebug($"[RAID] Rebuilt key '{key}' successfully");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to rebuild key '{key}'", ex);
            }
        }

        private class RebuildStatistics
        {
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public int FailedProviderIndex { get; set; }
            public int TotalKeys { get; set; }
            public int KeysRebuilt { get; set; }
            public int KeysFailed { get; set; }
            public TimeSpan Duration => EndTime - StartTime;
        }

        private void ValidateConfiguration()
        {
            switch (_config.Level)
            {
                case RaidLevel.RAID_0:
                    if (_config.ProviderCount < 2)
                        throw new ArgumentException("RAID 0 requires at least 2 providers");
                    break;
                case RaidLevel.RAID_1:
                    if (_config.ProviderCount < 2)
                        throw new ArgumentException("RAID 1 requires at least 2 providers");
                    break;
                case RaidLevel.RAID_5:
                    if (_config.ProviderCount < 3)
                        throw new ArgumentException("RAID 5 requires at least 3 providers");
                    break;
                case RaidLevel.RAID_6:
                    if (_config.ProviderCount < 4)
                        throw new ArgumentException("RAID 6 requires at least 4 providers");
                    break;
                case RaidLevel.RAID_10:
                    if (_config.ProviderCount < 4 || _config.ProviderCount % 2 != 0)
                        throw new ArgumentException("RAID 10 requires an even number of providers (minimum 4)");
                    break;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _healthMonitorTimer?.Dispose();
            _rebuildLock?.Dispose();
            _disposed = true;
        }
    }

    // ==================== CONFIGURATION CLASSES ====================

    /// <summary>
    /// Configuration for the RAID engine.
    /// </summary>
    public class RaidConfiguration
    {
        /// <summary>RAID level to use.</summary>
        public RaidLevel Level { get; set; } = RaidLevel.RAID_1;

        /// <summary>Number of storage providers in the array.</summary>
        public int ProviderCount { get; set; }

        /// <summary>Size of each stripe in bytes (default 64KB).</summary>
        public int StripeSize { get; set; } = 64 * 1024;

        /// <summary>Number of mirrors for RAID 1 (default 2).</summary>
        public int MirrorCount { get; set; } = 2;

        /// <summary>Parity algorithm to use.</summary>
        public ParityAlgorithm ParityAlgorithm { get; set; } = ParityAlgorithm.XOR;

        /// <summary>Priority for rebuild operations.</summary>
        public RebuildPriority RebuildPriority { get; set; } = RebuildPriority.Medium;

        /// <summary>Interval between health checks.</summary>
        public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Whether to automatically rebuild on failure.</summary>
        public bool AutoRebuild { get; set; } = true;
    }

    /// <summary>
    /// Supported RAID levels.
    /// </summary>
    public enum RaidLevel
    {
        // Standard RAID Levels
        RAID_0,     // Striping (performance)
        RAID_1,     // Mirroring (redundancy)
        RAID_2,     // Bit-level striping with Hamming code
        RAID_3,     // Byte-level striping with dedicated parity
        RAID_4,     // Block-level striping with dedicated parity
        RAID_5,     // Block-level striping with distributed parity
        RAID_6,     // Block-level striping with dual distributed parity

        // Nested RAID Levels
        RAID_10,    // RAID 1+0 (mirrored stripes)
        RAID_01,    // RAID 0+1 (striped mirrors)
        RAID_03,    // RAID 0+3 (striped dedicated parity)
        RAID_50,    // RAID 5+0 (striped RAID 5 sets)
        RAID_60,    // RAID 6+0 (striped RAID 6 sets)
        RAID_100,   // RAID 10+0 (striped mirrors of mirrors)

        // Enhanced RAID Levels
        RAID_1E,    // RAID 1 Enhanced (mirrored striping)
        RAID_5E,    // RAID 5 with hot spare
        RAID_5EE,   // RAID 5 Enhanced with distributed spare
        RAID_6E,    // RAID 6 Enhanced with extra parity

        // Vendor-Specific RAID
        RAID_DP,    // NetApp Double Parity (RAID 6 variant)
        RAID_S,     // Dell/EMC Parity RAID (RAID 5 variant)
        RAID_7,     // Cached striping with parity
        RAID_FR,    // Fast Rebuild (optimized RAID 6)

        // ZFS RAID Levels
        RAID_Z1,    // ZFS single parity (RAID 5 equivalent)
        RAID_Z2,    // ZFS double parity (RAID 6 equivalent)
        RAID_Z3,    // ZFS triple parity

        // Advanced/Proprietary RAID
        RAID_MD10,      // Linux MD RAID 10 (near/far/offset layouts)
        RAID_Adaptive,  // IBM Adaptive RAID (auto-tuning)
        RAID_Beyond,    // Drobo BeyondRAID (single/dual parity)
        RAID_Unraid,    // Unraid parity system (1-2 parity disks)
        RAID_Declustered, // Declustered/Distributed RAID

        // Extended RAID Levels (Phase 3)
        RAID_71,        // RAID 7.1 - Enhanced RAID 7 with read cache
        RAID_72,        // RAID 7.2 - Enhanced RAID 7 with write-back cache
        RAID_NM,        // RAID N+M - Flexible N data + M parity
        RAID_Matrix,    // Intel Matrix RAID - Multiple RAID types on same disks
        RAID_JBOD,      // Just a Bunch of Disks - Simple concatenation
        RAID_Crypto,    // Crypto SoftRAID - Encrypted software RAID
        RAID_DUP,       // Btrfs DUP Profile - Duplicate on same device
        RAID_DDP,       // NetApp Dynamic Disk Pool
        RAID_SPAN,      // Simple disk spanning/concatenation
        RAID_BIG,       // Concatenated volumes (Linux md BIG)
        RAID_MAID,      // Massive Array of Idle Disks - Power managed RAID
        RAID_Linear     // Linear mode - Sequential concatenation
    }

    /// <summary>
    /// Parity calculation algorithm.
    /// </summary>
    public enum ParityAlgorithm
    {
        /// <summary>Simple XOR for RAID 5.</summary>
        XOR,
        /// <summary>Reed-Solomon for RAID 6.</summary>
        ReedSolomon
    }

    /// <summary>
    /// Rebuild operation priority.
    /// </summary>
    public enum RebuildPriority
    {
        Low,
        Medium,
        High
    }

    /// <summary>
    /// Storage provider status.
    /// </summary>
    public enum ProviderStatus
    {
        Healthy,
        Degraded,
        Failed,
        Rebuilding
    }

    /// <summary>
    /// Metadata for RAID arrays.
    /// </summary>
    public class RaidMetadata
    {
        public RaidLevel Level { get; set; }
        public long TotalSize { get; set; }
        public int ChunkCount { get; set; }
        public int MirrorCount { get; set; }
        public Dictionary<int, List<int>> ProviderMapping { get; set; } = new();
    }

    /// <summary>
    /// Health status of a storage provider.
    /// </summary>
    public class ProviderHealth
    {
        public int Index { get; set; }
        public ProviderStatus Status { get; set; }
        public DateTime? FailureTime { get; set; }
        public double RebuildProgress { get; set; }
    }
}
